using RebornLauncher.Core.Models;
using RebornLauncher.Core.Services;

namespace RebornLauncher.Core.Tests;

public sealed class InstanceConfigurationServiceTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("server.local")]
    [InlineData("::1")]
    public void AcceptsSafePublicAddresses(string address)
    {
        var settings = new InstanceSettings { PublicAddress = address };

        var result = InstanceConfigurationService.Validate(settings);

        Assert.DoesNotContain(result.Issues, issue => issue.Field == nameof(settings.PublicAddress));
    }

    [Theory]
    [InlineData("server.local' where 1=1 --")]
    [InlineData("server/path")]
    [InlineData("server name")]
    public void RejectsUnsafePublicAddresses(string address)
    {
        var settings = new InstanceSettings { PublicAddress = address };

        var result = InstanceConfigurationService.Validate(settings);

        Assert.Contains(result.Issues, issue => issue.Field == nameof(settings.PublicAddress));
    }

    [Fact]
    public async Task LoadsSecretsFromAnUmbrellaBackedInstance()
    {
        using var temp = new TemporaryDirectory();
        var settings = new InstanceSettings
        {
            InstallRoot = temp.Path,
            ChannelId = "x64-dx9-vanilla",
            DatabasePassword = "application-secret",
            DatabaseAdminPassword = "administrator-secret",
        };
        var channel = new ReleaseChannel
        {
            Id = settings.ChannelId,
            DisplayName = "NGE DX9",
            Branch = "main",
            SchemaVersion = 4,
            VariantPath = "galaxies-reborn/nge/x64-dx9-vanilla",
            Server = new ServerRelease { MainRepositoryPath = "swg-main" },
        };

        await InstanceConfigurationService.SaveAsync(settings, channel);
        var paths = new LauncherPaths(settings, channel);
        var restored = await InstanceConfigurationService.LoadAsync(paths.SettingsPath);

        Assert.NotNull(restored);
        Assert.Equal("application-secret", restored.DatabasePassword);
        Assert.Equal("administrator-secret", restored.DatabaseAdminPassword);
    }
}
