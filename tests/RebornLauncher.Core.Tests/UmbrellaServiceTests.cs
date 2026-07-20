using RebornLauncher.Core.Models;
using RebornLauncher.Core.Services;

namespace RebornLauncher.Core.Tests;

/// <summary>
/// Exercises the umbrella against a real Git repository. Git refuses the file transport for
/// submodules by default, so these tests enable it through GIT_CONFIG_* for child processes only.
/// </summary>
public sealed class UmbrellaServiceTests : IDisposable
{
    private const string VariantManifest =
        """
        {
          "schemaVersion": 4,
          "id": "x64-dx9-vanilla",
          "project": "galaxies-reborn",
          "flavor": "nge",
          "variantPath": "galaxies-reborn/nge/x64-dx9-vanilla",
          "displayName": "Vanilla",
          "branch": "x64-dx9-vanilla",
          "pipeline": "swgSource",
          "server": {
            "mainRepositoryPath": "swg-main",
            "submodules": [
              {
                "name": "swg-main",
                "path": "galaxies-reborn/nge/x64-dx9-vanilla/swg-main",
                "installPath": "swg-main",
                "required": true,
                "recursive": true,
                "approximateBytes": 100
              },
              {
                "name": "client-tools",
                "path": "galaxies-reborn/nge/x64-dx9-vanilla/client-tools",
                "installPath": "client-tools",
                "required": false,
                "recursive": false,
                "approximateBytes": 900
              }
            ]
          },
          "clients": { "published": false }
        }
        """;

    private const string Catalog =
        """
        {
          "schemaVersion": 4,
          "projects": [
            {
              "id": "galaxies-reborn",
              "displayName": "Galaxies Reborn",
              "path": "galaxies-reborn",
              "available": true,
              "flavors": [
                {
                  "id": "nge",
                  "displayName": "NGE",
                  "path": "galaxies-reborn/nge",
                  "available": true,
                  "variants": [{
                    "id": "x64-dx9-vanilla",
                    "displayName": "Vanilla",
                    "manifest": "channels/galaxies-reborn/nge/x64-dx9-vanilla.json"
                  }]
                },
                { "id": "cu", "displayName": "CU", "path": "galaxies-reborn/cu", "available": false, "variants": [] }
              ],
              "variants": []
            }
          ]
        }
        """;

    private readonly string? _previousCount;

    public UmbrellaServiceTests()
    {
        _previousCount = Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT");
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "protocol.file.allow");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "always");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", _previousCount);
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", null);
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", null);
    }

    [Fact]
    public async Task CloningTheUmbrellaFetchesNoSourceUntilAVariantIsMaterialized()
    {
        using var temp = new TemporaryDirectory();
        var git = await CreateGitAsync();
        var remote = await BuildFixtureAsync(git, temp.Path);
        var umbrella = new UmbrellaService(git);
        var root = Path.Combine(temp.Path, "instance", "umbrella");

        await umbrella.EnsureClonedAsync(remote, root);

        // The catalog travels with the clone, so a project can be offered before anything is fetched.
        var catalog = await umbrella.LoadCatalogAsync(root);
        var project = Assert.Single(catalog.Projects, candidate => candidate.Available);
        var flavor = Assert.Single(project.Flavors, candidate => candidate.Available);
        var channel = await umbrella.LoadVariantAsync(root, flavor.Variants[0]);

        var marker = Path.Combine(root, "galaxies-reborn", "nge", "x64-dx9-vanilla", "swg-main", "marker.txt");
        Assert.False(File.Exists(marker), "Cloning the umbrella must not fetch submodule content.");

        await umbrella.MaterializeVariantAsync(root, channel);

        Assert.True(File.Exists(marker), "Materializing the variant must fetch its required submodules.");
        Assert.True(
            File.Exists(Path.Combine(root, "galaxies-reborn", "nge", "x64-dx9-vanilla", "swg-main", "src", "marker.txt")),
            "A recursive submodule must fetch its nested repositories.");
    }

    [Fact]
    public async Task UninitializedSubmodulesAreReportedAfterAPlainClone()
    {
        using var temp = new TemporaryDirectory();
        var git = await CreateGitAsync();
        var remote = await BuildFixtureAsync(git, temp.Path);
        var umbrella = new UmbrellaService(git);
        var root = Path.Combine(temp.Path, "instance", "umbrella");

        await umbrella.EnsureClonedAsync(remote, root);

        // git submodule update exits zero even when clones fail, so materialization relies on this
        // to tell whether the sources actually arrived.
        var missing = await git.GetUninitializedSubmodulesAsync(root, recursive: false);

        Assert.Equal(2, missing.Count);
        Assert.Contains("galaxies-reborn/nge/x64-dx9-vanilla/swg-main", missing);
    }

    [Fact]
    public async Task MaterializingAVariantSkipsOptionalSubmodules()
    {
        using var temp = new TemporaryDirectory();
        var git = await CreateGitAsync();
        var remote = await BuildFixtureAsync(git, temp.Path);
        var umbrella = new UmbrellaService(git);
        var root = Path.Combine(temp.Path, "instance", "umbrella");

        await umbrella.EnsureClonedAsync(remote, root);
        var catalog = await umbrella.LoadCatalogAsync(root);
        var channel = await umbrella.LoadVariantAsync(root, catalog.Projects[0].Flavors[0].Variants[0]);
        var variantRoot = Path.Combine(root, "galaxies-reborn", "nge", "x64-dx9-vanilla");

        await umbrella.MaterializeVariantAsync(root, channel, includeOptional: false);

        Assert.True(File.Exists(Path.Combine(variantRoot, "swg-main", "marker.txt")));
        Assert.False(
            File.Exists(Path.Combine(variantRoot, "client-tools", "marker.txt")),
            "An optional submodule must not be fetched unless it is requested.");

        await umbrella.MaterializeVariantAsync(root, channel, includeOptional: true);

        Assert.True(File.Exists(Path.Combine(variantRoot, "client-tools", "marker.txt")));
    }

    [Fact]
    public async Task AStaleSubmoduleIsMovedBackToTheRecordedRevision()
    {
        using var temp = new TemporaryDirectory();
        var git = await CreateGitAsync();
        var remote = await BuildFixtureAsync(git, temp.Path);
        var umbrella = new UmbrellaService(git);
        var root = Path.Combine(temp.Path, "instance", "umbrella");

        await umbrella.EnsureClonedAsync(remote, root);
        var catalog = await umbrella.LoadCatalogAsync(root);
        var channel = await umbrella.LoadVariantAsync(root, catalog.Projects[0].Flavors[0].Variants[0]);
        await umbrella.MaterializeVariantAsync(root, channel);

        var submodule = Path.Combine(root, "galaxies-reborn", "nge", "x64-dx9-vanilla", "swg-main");
        var pinned = (await git.RunAsync(submodule, ["rev-parse", "HEAD"])).StandardOutput.Trim();

        // Stand in for the umbrella advancing: the submodule is still initialized, but sits at the
        // wrong revision. Git marks this '+', not '-'.
        await git.RunAsync(submodule, ["checkout", "--detach", "HEAD~1"]);
        Assert.False(await git.IsSubmoduleCurrentAsync(root, "galaxies-reborn/nge/x64-dx9-vanilla/swg-main"));

        await umbrella.MaterializeVariantAsync(root, channel);

        // Materializing must move it back rather than skip it for being "already present".
        var actual = (await git.RunAsync(submodule, ["rev-parse", "HEAD"])).StandardOutput.Trim();
        Assert.Equal(pinned, actual);
        Assert.True(await git.IsSubmoduleCurrentAsync(root, "galaxies-reborn/nge/x64-dx9-vanilla/swg-main"));
    }

    [Fact]
    public async Task CheckForUpdateReportsStaleSourcesEvenWhenTheUmbrellaIsCurrent()
    {
        using var temp = new TemporaryDirectory();
        var git = await CreateGitAsync();
        var remote = await BuildFixtureAsync(git, temp.Path);
        var umbrella = new UmbrellaService(git);
        var root = Path.Combine(temp.Path, "instance", "umbrella");

        await umbrella.EnsureClonedAsync(remote, root);
        var catalog = await umbrella.LoadCatalogAsync(root);
        var channel = await umbrella.LoadVariantAsync(root, catalog.Projects[0].Flavors[0].Variants[0]);
        await umbrella.MaterializeVariantAsync(root, channel);

        Assert.False((await umbrella.CheckForUpdateAsync(root, channel)).UpdateAvailable);

        var submodule = Path.Combine(root, "galaxies-reborn", "nge", "x64-dx9-vanilla", "swg-main");
        await git.RunAsync(submodule, ["checkout", "--detach", "HEAD~1"]);

        var status = await umbrella.CheckForUpdateAsync(root, channel);

        // The umbrella has not moved, so only the submodule check can catch this.
        Assert.False(status.UmbrellaBehind);
        Assert.True(status.UpdateAvailable);
        Assert.Contains("swg-main", status.StaleSubmodules);
    }

    [Fact]
    public async Task MaterializingIsIdempotent()
    {
        using var temp = new TemporaryDirectory();
        var git = await CreateGitAsync();
        var remote = await BuildFixtureAsync(git, temp.Path);
        var umbrella = new UmbrellaService(git);
        var root = Path.Combine(temp.Path, "instance", "umbrella");

        await umbrella.EnsureClonedAsync(remote, root);
        var catalog = await umbrella.LoadCatalogAsync(root);
        var channel = await umbrella.LoadVariantAsync(root, catalog.Projects[0].Flavors[0].Variants[0]);

        await umbrella.MaterializeVariantAsync(root, channel);
        await umbrella.MaterializeVariantAsync(root, channel);

        Assert.True(File.Exists(
            Path.Combine(root, "galaxies-reborn", "nge", "x64-dx9-vanilla", "swg-main", "marker.txt")));
    }

    [Fact]
    public async Task MaterializingRepairsTrackedFilesInAnExistingCheckout()
    {
        using var temp = new TemporaryDirectory();
        var git = await CreateGitAsync();
        var remote = await BuildFixtureAsync(git, temp.Path);
        var umbrella = new UmbrellaService(git);
        var root = Path.Combine(temp.Path, "instance", "umbrella");

        await umbrella.EnsureClonedAsync(remote, root);
        var catalog = await umbrella.LoadCatalogAsync(root);
        var channel = await umbrella.LoadVariantAsync(root, catalog.Projects[0].Flavors[0].Variants[0]);
        await umbrella.MaterializeVariantAsync(root, channel);

        var marker = Path.Combine(
            root, "galaxies-reborn", "nge", "x64-dx9-vanilla", "swg-main", "marker.txt");
        var repositoryContent = await File.ReadAllBytesAsync(marker);
        await File.WriteAllTextAsync(marker, "damaged\r\nworking\r\ntree\r\n");

        await umbrella.MaterializeVariantAsync(root, channel);

        Assert.Equal(repositoryContent, await File.ReadAllBytesAsync(marker));
    }

    [Fact]
    public async Task EnsureClonedRejectsANonEmptyDestination()
    {
        using var temp = new TemporaryDirectory();
        var git = await CreateGitAsync();
        var remote = await BuildFixtureAsync(git, temp.Path);
        var umbrella = new UmbrellaService(git);
        var root = Path.Combine(temp.Path, "occupied");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "existing.txt"), "local work");

        await Assert.ThrowsAsync<InvalidOperationException>(() => umbrella.EnsureClonedAsync(remote, root));
    }

    [Fact]
    public void PendingSubmodulesExcludeOptionalWorkByDefault()
    {
        var channel = new ReleaseChannel
        {
            Id = "x64-dx9-vanilla",
            DisplayName = "Vanilla",
            Branch = "x64-dx9-vanilla",
            Project = "galaxies-reborn",
            Flavor = "nge",
            VariantPath = "galaxies-reborn/nge/x64-dx9-vanilla",
            SchemaVersion = 4,
            Server = new ServerRelease
            {
                Submodules =
                [
                    new SubmoduleSpec
                    {
                        Name = "swg-main",
                        Path = "galaxies-reborn/nge/x64-dx9-vanilla/swg-main",
                        InstallPath = "swg-main",
                        Required = true,
                        ApproximateBytes = 100,
                    },
                    new SubmoduleSpec
                    {
                        Name = "client-tools",
                        Path = "galaxies-reborn/nge/x64-dx9-vanilla/client-tools",
                        InstallPath = "client-tools",
                        Required = false,
                        ApproximateBytes = 900,
                    },
                ],
            },
        };

        Assert.Equal(100, UmbrellaService.GetApproximateBytes(channel, includeOptional: false));
        Assert.Equal(1000, UmbrellaService.GetApproximateBytes(channel, includeOptional: true));
        Assert.Equal("swg-main", Assert.Single(UmbrellaService.GetPendingSubmodules(channel, false)).Name);
    }

    private static async Task<GitService> CreateGitAsync()
    {
        var bootstrapper = new GitBootstrapper(new ProcessRunner());
        var installation = await bootstrapper.ResolveAsync();
        return bootstrapper.CreateService(installation);
    }

    /// <summary>
    /// Builds an origin umbrella with one required and one optional submodule. The required one
    /// carries a nested submodule of its own, mirroring swg-main, so recursive initialization is
    /// exercised.
    /// </summary>
    private static async Task<string> BuildFixtureAsync(GitService git, string root)
    {
        var originRoot = Path.Combine(root, "origin");

        // Nested repository, standing in for swg-main's src/dsrc/serverdata.
        var nested = Path.Combine(originRoot, "src");
        Directory.CreateDirectory(nested);
        await CommitAsync(git, nested, "marker.txt", "nested source");

        var sources = new List<string>();
        foreach (var name in new[] { "swg-main", "client-tools" })
        {
            var source = Path.Combine(originRoot, name);
            Directory.CreateDirectory(source);
            await CommitAsync(git, source, "marker.txt", $"{name} source");
            sources.Add(source);
        }

        await RunAsync(git, sources[0], ["submodule", "add", "-b", "main", AsUrl(nested), "src"]);
        await RunAsync(git, sources[0], ["add", "-A"]);
        await CommitStagedAsync(git, sources[0], "Add nested submodule");

        var umbrella = Path.Combine(originRoot, "umbrella");
        Directory.CreateDirectory(umbrella);
        await RunAsync(git, umbrella, ["init", "-b", "main"]);

        var channels = Path.Combine(umbrella, "channels", "galaxies-reborn", "nge");
        Directory.CreateDirectory(channels);
        await File.WriteAllTextAsync(Path.Combine(umbrella, "channels", "index.json"), Catalog);
        await File.WriteAllTextAsync(Path.Combine(channels, "x64-dx9-vanilla.json"), VariantManifest);

        foreach (var source in sources)
        {
            var name = Path.GetFileName(source);
            await RunAsync(git, umbrella, [
                "submodule", "add", "-b", "main", AsUrl(source), $"galaxies-reborn/nge/x64-dx9-vanilla/{name}",
            ]);
        }

        await RunAsync(git, umbrella, ["add", "-A"]);
        await CommitStagedAsync(git, umbrella, "Add umbrella fixture");
        return AsUrl(umbrella);
    }

    private static async Task CommitAsync(GitService git, string repository, string file, string content)
    {
        await RunAsync(git, repository, ["init", "-b", "main"]);
        await File.WriteAllTextAsync(Path.Combine(repository, file), content);
        await RunAsync(git, repository, ["add", "-A"]);
        await CommitStagedAsync(git, repository, $"Add {file}");
    }

    private static Task CommitStagedAsync(GitService git, string repository, string message) =>
        RunAsync(git, repository, [
            "-c", "user.name=Reborn Tests",
            "-c", "user.email=tests@galaxies-reborn.invalid",
            "commit", "-m", message,
        ]);

    private static async Task RunAsync(GitService git, string workingDirectory, string[] arguments)
    {
        var result = await git.RunAsync(workingDirectory, arguments);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed in '{workingDirectory}': {result.CombinedOutput}");
        }
    }

    private static string AsUrl(string path) => path.Replace('\\', '/');
}
