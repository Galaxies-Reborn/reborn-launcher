using RebornLauncher.Cli;
using RebornLauncher.Core.Models;
using RebornLauncher.Core.Services;

// Headless front end for a VPS. It drives the same Core services the desktop launcher does, so the
// umbrella clone, submodule materialization, secret generation, and Compose handling are the same
// code rather than a second implementation that could drift.

var arguments = Arguments.Parse(args);
var runner = new ProcessRunner();
var containers = new ContainerService(runner);
var core3 = new Core3PipelineService(containers, runner);

try
{
    return arguments.Command switch
    {
        "list" => await ListAsync(),
        "prepare" => await PrepareAsync(),
        "start" => await ControlAsync("start"),
        "stop" => await ControlAsync("stop"),
        "status" => await ControlAsync("status"),
        "logs" => await ControlAsync("logs"),
        _ => Help(),
    };
}
catch (Exception exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}

static string DataRoot() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "galaxies-reborn");

string Remote() => arguments.Value("remote")
    ?? Environment.GetEnvironmentVariable("REBORN_UMBRELLA_REMOTE")
    ?? UmbrellaService.DefaultRemote;

string InstallRoot() => arguments.Value("install-root") ?? Path.Combine(DataRoot(), "instances");

async Task<UmbrellaService> UmbrellaAsync()
{
    var bootstrapper = new GitBootstrapper(runner);
    var installation = await bootstrapper.ResolveAsync();
    Console.WriteLine($"Using {installation}.");
    return new UmbrellaService(bootstrapper.CreateService(installation));
}

// The catalog is metadata only, so listing costs a few hundred kilobytes and fetches no source.
async Task<(UmbrellaService Umbrella, string Root, UmbrellaCatalog Catalog)> CatalogAsync()
{
    var umbrella = await UmbrellaAsync();
    var root = Path.Combine(DataRoot(), "umbrella");
    await umbrella.EnsureClonedAsync(Remote(), root, Progress());
    try
    {
        await umbrella.UpdateAsync(root);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"warning: catalog refresh skipped: {exception.Message}");
    }

    return (umbrella, root, await umbrella.LoadCatalogAsync(root));
}

async Task<int> ListAsync()
{
    var (_, _, catalog) = await CatalogAsync();
    Console.WriteLine();
    foreach (var project in catalog.Projects)
    {
        Console.WriteLine($"{project.DisplayName}{(project.Available ? "" : "  (unavailable)")}");
        var groups = project.HasFlavors
            ? project.Flavors.Select(flavor => (Label: flavor.DisplayName, flavor.Variants))
            : [(project.DisplayName, project.Variants)];

        foreach (var (label, variants) in groups)
        {
            if (project.HasFlavors)
            {
                Console.WriteLine($"  {label}");
            }

            foreach (var variant in variants)
            {
                var state = variant.Available ? "" : "  (not published)";
                var renderer = variant.Renderer == RendererKind.Unspecified ? "" : $"  [{variant.Renderer}]";
                Console.WriteLine($"    {variant.Id,-22}{renderer}{state}");
            }
        }
    }

    Console.WriteLine();
    Console.WriteLine("Prepare one with:  reborn prepare --variant <id> --address <public-ip>");
    return 0;
}

async Task<(ReleaseChannel Channel, InstanceSettings Settings)> ResolveAsync(UmbrellaCatalog catalog, UmbrellaService umbrella, string root)
{
    var id = arguments.Require("variant");
    var reference = catalog.AllVariants.FirstOrDefault(variant => variant.Id == id)
        ?? throw new ArgumentException($"Unknown variant '{id}'. Run 'reborn list' to see the options.");
    if (!reference.Available)
    {
        throw new InvalidOperationException($"Variant '{id}' is announced but not published yet.");
    }

    var channel = await umbrella.LoadVariantAsync(root, reference);
    var settings = new InstanceSettings
    {
        ChannelId = channel.Id,
        InstallRoot = InstallRoot(),
        InstanceName = arguments.Value("name") ?? "Galaxies Reborn",
        ClusterName = arguments.Value("cluster") ?? "swg",
        PublicAddress = arguments.Value("address") ?? "127.0.0.1",
        LoginPort = arguments.Int("port", 44453),
        ContainerBackend = ContainerBackendKind.Auto,
        InitializeServer = !arguments.Has("no-start"),
        RetailClientDirectory = arguments.Value("tre") ?? string.Empty,
    };

    var validation = InstanceConfigurationService.Validate(settings);
    if (!validation.IsValid)
    {
        throw new ArgumentException(string.Join(Environment.NewLine, validation.Issues.Select(i => i.Message)));
    }

    return (channel, settings);
}

async Task<int> PrepareAsync()
{
    var (umbrella, root, catalog) = await CatalogAsync();
    var (channel, settings) = await ResolveAsync(catalog, umbrella, root);
    var paths = new LauncherPaths(settings, channel);

    if (channel.Pipeline == PipelineKind.Core3)
    {
        var validation = Core3PipelineService.ValidateClientDirectory(settings.RetailClientDirectory);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"{validation.Issues[0].Message} Pass --tre <folder> with the retail client files.");
        }
    }

    Console.WriteLine($"Preparing {channel.DisplayName} in {paths.InstanceRoot}");
    await umbrella.EnsureClonedAsync(Remote(), paths.UmbrellaRoot, Progress());

    var includeOptional = arguments.Has("with-tools");
    Console.WriteLine($"Fetching sources: about {Bytes(UmbrellaService.GetApproximateBytes(channel, includeOptional))}.");
    await umbrella.MaterializeVariantAsync(paths.UmbrellaRoot, channel, includeOptional, Progress());

    await InstanceConfigurationService.SaveAsync(settings, channel);

    if (settings.InitializeServer)
    {
        Console.WriteLine("Building and starting the server. The first build takes a long time.");
        if (channel.Pipeline == PipelineKind.Core3)
        {
            await core3.PrepareAndStartAsync(settings, paths, settings.RetailClientDirectory, Log);
        }
        else
        {
            await containers.InitializeAndStartAsync(settings, channel, Log);
        }
    }

    Console.WriteLine();
    Console.WriteLine($"Ready. Players connect to {settings.PublicAddress}:{settings.LoginPort}");
    return 0;
}

async Task<int> ControlAsync(string action)
{
    var (umbrella, root, catalog) = await CatalogAsync();
    var (channel, settings) = await ResolveAsync(catalog, umbrella, root);
    var paths = new LauncherPaths(settings, channel);
    var core3Pipeline = channel.Pipeline == PipelineKind.Core3;

    var result = action switch
    {
        "start" => core3Pipeline
            ? await core3.StartAsync(settings, paths, Log)
            : await containers.StartAsync(settings, channel, Log),
        "stop" => core3Pipeline
            ? await core3.StopAsync(settings, paths, Log)
            : await containers.StopAsync(settings, channel, Log),
        "status" => core3Pipeline
            ? await core3.StatusAsync(settings, paths)
            : await containers.StatusAsync(settings, channel),
        _ => core3Pipeline
            ? await core3.LogsAsync(settings, paths, Log)
            : await containers.LogsAsync(settings, channel, Log),
    };

    Console.WriteLine(result.CombinedOutput);
    return result.Succeeded ? 0 : 1;
}

int Help()
{
    Console.WriteLine("""
        reborn — headless Galaxies Reborn server installer

        Usage:
          reborn list                                  Show projects and variants. Downloads no source.
          reborn prepare --variant <id> [options]      Fetch sources, build, and start.
          reborn start   --variant <id>
          reborn stop    --variant <id>
          reborn status  --variant <id>
          reborn logs    --variant <id>

        Options:
          --address <host>       Public address players connect to. Default 127.0.0.1
          --port <n>             Login port. Default 44453
          --name <text>          Instance name.
          --cluster <text>       Cluster name. Default swg
          --install-root <dir>   Where instances live.
          --with-tools           Also fetch the optional client build tools (large).
          --no-start             Fetch and configure only; do not build or start.
          --tre <dir>            Retail client .tre folder. Required by SWGEmu Core3.
          --remote <url>         Umbrella to read. Default the public Galaxies Reborn umbrella.

        Example:
          reborn prepare --variant x64-dx11-vanilla --address 203.0.113.10
        """);
    return 0;
}

void Log(string line) => Console.WriteLine(line);

IProgress<TransferProgress> Progress()
{
    // A VPS session is usually a log, not a terminal, so report each completed step once rather
    // than redrawing a bar. Git repeats a phase's final tick, so identical lines are collapsed.
    var lastReported = string.Empty;
    return new Progress<TransferProgress>(progress =>
    {
        if (progress.TotalBytes <= 0 || progress.BytesCompleted != progress.TotalBytes)
        {
            return;
        }

        var line = $"  {progress.Operation}: {progress.CurrentItem}";
        if (line != lastReported)
        {
            lastReported = line;
            Console.WriteLine(line);
        }
    });
}

static string Bytes(long bytes)
{
    string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
    var value = (double)Math.Max(0, bytes);
    var unit = 0;
    while (value >= 1024 && unit < units.Length - 1)
    {
        value /= 1024;
        unit++;
    }

    return $"{value:0.##} {units[unit]}";
}
