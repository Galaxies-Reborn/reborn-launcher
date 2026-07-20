using System.Globalization;
using System.Text;
using RebornLauncher.Core.Models;

namespace RebornLauncher.Core.Services;

/// <summary>
/// Drives SWGEmu's Core3, which shares nothing with the swg-main stack: it carries its own
/// Dockerfile, builds with CMake, and uses MySQL rather than Oracle.
///
/// Core3 documents a pair of host shell scripts, and on Windows an entire WSL2 setup. Neither is
/// used here. Everything runs inside containers through Compose, the same way the other pipelines
/// are driven, so a standup needs nothing on the host but the container engine.
/// </summary>
public sealed class Core3PipelineService(ContainerService containerService, ProcessRunner processRunner)
{
    public const string ImageTag = "swgemu/core3-dev:latest";

    public const string ContainerName = "swgemu-core3";

    public const string ServiceName = "core3";

    /// <summary>Volume holding the retail client .tre files, mounted read-only at /tre.</summary>
    public const string TreVolume = "shared-tre";

    /// <summary>Volume holding the workspace and MySQL data, so a rebuild keeps prior work.</summary>
    public const string HomeVolume = "swgemu-core3";

    /// <summary>Ports Core3 publishes, named by the environment variables its own image declares.</summary>
    private static readonly (string Variable, string Protocol)[] PublishedPorts =
    [
        ("SSHPORT", "tcp"),
        ("STATUSPORT", "tcp"),
        ("LOGINPORT", "udp"),
        ("PINGPORT", "udp"),
        ("ZONESERVERPORT", "udp"),
    ];

    /// <summary>
    /// Confirms a folder holds retail client .tre files. Core3 cannot run without them and they
    /// cannot be distributed, so the operator supplies their own copy. This is the only part of a
    /// standup that reaches outside the container.
    /// </summary>
    public static ValidationResult ValidateClientDirectory(string clientDirectory)
    {
        var result = new ValidationResult();
        if (string.IsNullOrWhiteSpace(clientDirectory))
        {
            result.Add("clientDirectory", "Select the folder holding your retail Star Wars Galaxies client.");
            return result;
        }

        if (!Directory.Exists(clientDirectory))
        {
            result.Add("clientDirectory", $"'{clientDirectory}' does not exist.");
            return result;
        }

        if (CountTreFiles(clientDirectory) == 0)
        {
            result.Add(
                "clientDirectory",
                $"'{clientDirectory}' contains no .tre files. Select the root of a retail client installation.");
        }

        return result;
    }

    public static int CountTreFiles(string clientDirectory) =>
        Directory.EnumerateFiles(clientDirectory, "*.tre", SearchOption.TopDirectoryOnly).Count();

    /// <summary>Builds the image, loads the client files, then brings the stack up and compiles.</summary>
    public async Task PrepareAndStartAsync(
        InstanceSettings settings,
        LauncherPaths paths,
        string retailClientDirectory,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var validation = ValidateClientDirectory(retailClientDirectory);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                string.Join(Environment.NewLine, validation.Issues.Select(issue => issue.Message)));
        }

        var backend = await containerService.ResolveAsync(settings.ContainerBackend, cancellationToken);
        output?.Invoke($"Using {backend.DisplayName}.");

        // The image must exist before the client files are loaded, because loading uses that image
        // rather than pulling a general-purpose one.
        await BuildImageAsync(backend, paths, output, cancellationToken);
        await LoadClientFilesAsync(backend, retailClientDirectory, output, cancellationToken);

        var imageEnvironment = await ReadImageEnvironmentAsync(backend, cancellationToken);
        await WriteComposeFileAsync(paths, imageEnvironment, cancellationToken);

        output?.Invoke("Starting the Core3 container.");
        await ComposeRequiredAsync(backend, settings, paths, ["up", "-d"], output, cancellationToken);

        output?.Invoke("Compiling Core3 in the container. The first build takes a long time.");
        await ComposeRequiredAsync(
            backend,
            settings,
            paths,
            ["exec", "-T", ServiceName, "bash", "-lc", "build"],
            output,
            cancellationToken);

        output?.Invoke("Starting the server in the container.");
        await ComposeRequiredAsync(
            backend,
            settings,
            paths,
            ["exec", "-d", ServiceName, "bash", "-lc", "run"],
            output,
            cancellationToken);
    }

    public async Task BuildImageAsync(
        ContainerBackendDefinition backend,
        LauncherPaths paths,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        var context = Path.Combine(paths.MainRepositoryRoot, "docker");
        if (!File.Exists(Path.Combine(context, "Dockerfile")))
        {
            throw new DirectoryNotFoundException(
                $"Core3's Dockerfile is missing at '{context}'. Fetch the variant's sources first.");
        }

        output?.Invoke("Building the Core3 image.");
        await RunRequiredAsync(backend, ["build", "-t", ImageTag, context], output, cancellationToken);
    }

    /// <summary>
    /// Copies the retail .tre files into the shared volume using the Core3 image itself, so a
    /// standup pulls no image outside Core3's own environment. The host folder is read-only and is
    /// not touched again once loaded.
    /// </summary>
    public async Task LoadClientFilesAsync(
        ContainerBackendDefinition backend,
        string retailClientDirectory,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        output?.Invoke($"Loading {CountTreFiles(retailClientDirectory)} client .tre files into '{TreVolume}'.");
        await RunRequiredAsync(backend, ["volume", "create", TreVolume], null, cancellationToken);

        // The image declares an entrypoint of its own, so it is overridden to run a plain copy.
        await RunRequiredAsync(
            backend,
            [
                "run", "--rm",
                "--entrypoint", "sh",
                "-v", $"{TreVolume}:/tre",
                "-v", $"{Path.GetFullPath(retailClientDirectory)}:/src:ro",
                ImageTag,
                "-c", "cp -f /src/*.tre /tre/",
            ],
            output,
            cancellationToken);
    }

    /// <summary>
    /// Reads the image's declared environment, which is where Core3 defines its ports. Core3's own
    /// build.sh captures the same values into env-base.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ReadImageEnvironmentAsync(
        ContainerBackendDefinition backend,
        CancellationToken cancellationToken = default)
    {
        var result = await processRunner.RunAsync(
            backend.Executable,
            ["image", "inspect", "--format", "{{range .Config.Env}}{{println .}}{{end}}", ImageTag],
            Environment.CurrentDirectory,
            cancellationToken: cancellationToken);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Unable to inspect '{ImageTag}'.{Environment.NewLine}{result.CombinedOutput}");
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                environment[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        return environment;
    }

    /// <summary>
    /// Writes the Compose file describing the Core3 service. Ports are taken from the built image
    /// rather than hardcoded, so Core3 can change them without breaking the launcher.
    /// </summary>
    public static async Task WriteComposeFileAsync(
        LauncherPaths paths,
        IReadOnlyDictionary<string, string> imageEnvironment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(imageEnvironment);

        var context = Path.Combine(paths.MainRepositoryRoot, "docker").Replace('\\', '/');
        var builder = new StringBuilder();
        builder.AppendLine("# Generated by Reborn Launcher. Edits are overwritten on the next prepare.");
        builder.AppendLine("services:");
        builder.AppendLine($"  {ServiceName}:");
        builder.AppendLine($"    image: {ImageTag}");
        builder.AppendLine("    build:");
        builder.AppendLine($"      context: {context}");
        builder.AppendLine($"    container_name: {ContainerName}");
        builder.AppendLine($"    hostname: {ContainerName}");
        builder.AppendLine("    cap_add:");
        builder.AppendLine("      - SYS_PTRACE");
        builder.AppendLine("    restart: unless-stopped");

        var ports = PublishedPorts
            .Where(port => imageEnvironment.TryGetValue(port.Variable, out var value) && IsPort(value))
            .Select(port => (Port: imageEnvironment[port.Variable], port.Protocol))
            .ToList();
        if (ports.Count > 0)
        {
            builder.AppendLine("    ports:");
            foreach (var (port, protocol) in ports)
            {
                builder.AppendLine($"      - \"{port}:{port}/{protocol}\"");
            }
        }

        builder.AppendLine("    volumes:");
        builder.AppendLine("      - tre:/tre:ro");
        builder.AppendLine("      - home:/home/swgemu");
        builder.AppendLine("volumes:");
        builder.AppendLine("  tre:");
        // Pinned names, so Compose reuses the volume already loaded rather than creating a
        // project-prefixed one beside it.
        builder.AppendLine($"    name: {TreVolume}");
        builder.AppendLine("  home:");
        builder.AppendLine($"    name: {HomeVolume}");

        Directory.CreateDirectory(Path.GetDirectoryName(paths.Core3ComposePath)!);
        await File.WriteAllTextAsync(paths.Core3ComposePath, builder.ToString(), cancellationToken);
    }

    private static bool IsPort(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) &&
        port is > 0 and <= 65535;

    public async Task<CommandResult> StartAsync(
        InstanceSettings settings,
        LauncherPaths paths,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        var backend = await containerService.ResolveAsync(settings.ContainerBackend, cancellationToken);
        return await ComposeAsync(backend, settings, paths, ["up", "-d"], output, cancellationToken);
    }

    public async Task<CommandResult> StopAsync(
        InstanceSettings settings,
        LauncherPaths paths,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        var backend = await containerService.ResolveAsync(settings.ContainerBackend, cancellationToken);
        // Preserve the configured containers so Start can reuse their identities.
        return await ComposeAsync(backend, settings, paths, ["stop"], output, cancellationToken);
    }

    public async Task<CommandResult> StatusAsync(
        InstanceSettings settings,
        LauncherPaths paths,
        CancellationToken cancellationToken = default)
    {
        var backend = await containerService.ResolveAsync(settings.ContainerBackend, cancellationToken);
        return await ComposeAsync(backend, settings, paths, ["ps"], null, cancellationToken);
    }

    public async Task<CommandResult> LogsAsync(
        InstanceSettings settings,
        LauncherPaths paths,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        var backend = await containerService.ResolveAsync(settings.ContainerBackend, cancellationToken);
        return await ComposeAsync(
            backend,
            settings,
            paths,
            ["logs", "--no-color", "--tail", "400"],
            output,
            cancellationToken);
    }

    private Task<CommandResult> ComposeAsync(
        ContainerBackendDefinition backend,
        InstanceSettings settings,
        LauncherPaths paths,
        IReadOnlyList<string> composeArguments,
        Action<string>? output,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.Core3ComposePath))
        {
            throw new FileNotFoundException(
                "The SWGEmu instance has not been prepared yet.",
                paths.Core3ComposePath);
        }

        List<string> arguments =
        [
            "compose",
            "-f", paths.Core3ComposePath,
            "--project-name", ContainerService.BuildProjectName(settings.ChannelId, settings.ClusterName),
        ];
        arguments.AddRange(composeArguments);

        return processRunner.RunAsync(
            backend.Executable,
            arguments,
            paths.StateRoot,
            null,
            output,
            cancellationToken);
    }

    private async Task ComposeRequiredAsync(
        ContainerBackendDefinition backend,
        InstanceSettings settings,
        LauncherPaths paths,
        IReadOnlyList<string> composeArguments,
        Action<string>? output,
        CancellationToken cancellationToken)
    {
        var result = await ComposeAsync(backend, settings, paths, composeArguments, output, cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Compose {composeArguments[0]} failed with exit code {result.ExitCode}." +
                $"{Environment.NewLine}{result.CombinedOutput}");
        }
    }

    private async Task RunRequiredAsync(
        ContainerBackendDefinition backend,
        IReadOnlyList<string> arguments,
        Action<string>? output,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            backend.Executable,
            arguments,
            Environment.CurrentDirectory,
            null,
            output,
            cancellationToken);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"'{backend.Executable} {arguments[0]}' failed with exit code {result.ExitCode}." +
                $"{Environment.NewLine}{result.CombinedOutput}");
        }
    }
}
