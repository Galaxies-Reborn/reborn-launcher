using RebornLauncher.Core.Models;
using RebornLauncher.Core.Services;

namespace RebornLauncher.Core.Tests;

public sealed class Core3PipelineServiceTests
{
    private static ReleaseChannel Core3Channel() => new()
    {
        Id = "core3",
        DisplayName = "SWGEmu Core3",
        Branch = "unstable",
        Project = "swgemu",
        VariantPath = "swgemu/core3",
        SchemaVersion = 4,
        Pipeline = PipelineKind.Core3,
        Server = new ServerRelease { MainRepositoryPath = "Core3", AssetsRepositoryPath = string.Empty },
    };

    [Fact]
    public async Task ComposeFileTakesItsPortsFromTheBuiltImage()
    {
        using var temp = new TemporaryDirectory();
        var channel = Core3Channel();
        var paths = new LauncherPaths(new InstanceSettings { InstallRoot = temp.Path }, channel);

        await Core3PipelineService.WriteComposeFileAsync(paths, new Dictionary<string, string>
        {
            ["LOGINPORT"] = "44453",
            ["STATUSPORT"] = "44455",
            ["SSHPORT"] = "44422",
            ["PATH"] = "/usr/bin",
        });

        var compose = await File.ReadAllTextAsync(paths.Core3ComposePath);

        Assert.Contains("\"44453:44453/udp\"", compose, StringComparison.Ordinal);
        Assert.Contains("\"44455:44455/tcp\"", compose, StringComparison.Ordinal);
        Assert.Contains("\"44422:44422/tcp\"", compose, StringComparison.Ordinal);
        // Volume names are pinned so Compose reuses the already-loaded tre volume.
        Assert.Contains($"name: {Core3PipelineService.TreVolume}", compose, StringComparison.Ordinal);
        Assert.Contains($"name: {Core3PipelineService.HomeVolume}", compose, StringComparison.Ordinal);
        Assert.Contains("/tre:ro", compose, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComposeFileIgnoresEnvironmentValuesThatAreNotPorts()
    {
        using var temp = new TemporaryDirectory();
        var channel = Core3Channel();
        var paths = new LauncherPaths(new InstanceSettings { InstallRoot = temp.Path }, channel);

        await Core3PipelineService.WriteComposeFileAsync(paths, new Dictionary<string, string>
        {
            ["LOGINPORT"] = "${PORT_GROUP}453",
            ["STATUSPORT"] = "44455",
        });

        var compose = await File.ReadAllTextAsync(paths.Core3ComposePath);

        // An unexpanded variable is not a port; publishing it would produce an invalid Compose file.
        Assert.DoesNotContain("PORT_GROUP", compose, StringComparison.Ordinal);
        Assert.Contains("\"44455:44455/tcp\"", compose, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComposeFileLivesOutsideTheSubmoduleWorkingTree()
    {
        using var temp = new TemporaryDirectory();
        var channel = Core3Channel();
        var paths = new LauncherPaths(new InstanceSettings { InstallRoot = temp.Path }, channel);

        await Core3PipelineService.WriteComposeFileAsync(paths, new Dictionary<string, string>());

        // Writing into Core3 would dirty the submodule and block later updates.
        Assert.StartsWith(paths.StateRoot, paths.Core3ComposePath, StringComparison.OrdinalIgnoreCase);
        Assert.False(paths.Core3ComposePath.StartsWith(paths.MainRepositoryRoot, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(paths.Core3ComposePath));
    }

    [Fact]
    public void ValidateClientDirectoryRejectsAMissingFolder()
    {
        var result = Core3PipelineService.ValidateClientDirectory(
            Path.Combine(Path.GetTempPath(), "RebornLauncher.Tests", Guid.NewGuid().ToString("N")));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidateClientDirectoryRejectsAnEmptySelection()
    {
        Assert.False(Core3PipelineService.ValidateClientDirectory(string.Empty).IsValid);
    }

    [Fact]
    public void ValidateClientDirectoryRejectsAFolderWithoutClientFiles()
    {
        using var temp = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "readme.txt"), "not a client");

        var result = Core3PipelineService.ValidateClientDirectory(temp.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Message.Contains(".tre", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateClientDirectoryAcceptsARetailClient()
    {
        using var temp = new TemporaryDirectory();
        foreach (var name in new[] { "bottom.tre", "patch_14.tre", "data_sku1_00.tre" })
        {
            File.WriteAllText(Path.Combine(temp.Path, name), "payload");
        }

        Assert.True(Core3PipelineService.ValidateClientDirectory(temp.Path).IsValid);
        Assert.Equal(3, Core3PipelineService.CountTreFiles(temp.Path));
    }
}
