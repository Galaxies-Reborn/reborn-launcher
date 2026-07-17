using RebornLauncher.Core.Services;

namespace RebornLauncher.Core.Tests;

public sealed class ClientConfigurationServiceTests
{
    [Fact]
    public async Task UpdatesRegularLoginValuesWithoutDuplicatingKeys()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "login.cfg");
        await File.WriteAllTextAsync(path, "[ClientGame]\n loginServerPort0=1\n loginServerAddress0=old\n");

        await ClientConfigurationService.UpdateLoginAsync(path, "192.168.1.25", 44453);

        var content = await File.ReadAllTextAsync(path);
        Assert.Contains("loginServerAddress0=192.168.1.25", content);
        Assert.Contains("loginServerPort0=44453", content);
        Assert.Equal(1, Count(content, "loginServerAddress0="));
    }

    [Fact]
    public async Task UpdatesGodClientAddressAndPort()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "client.cfg");
        await File.WriteAllTextAsync(path, "[ClientGame]\n\tloginServerAddress=old\n\n[Direct3d9]\n");

        await ClientConfigurationService.UpdateGodLoginAsync(path, "server.local", 44453);

        var content = await File.ReadAllTextAsync(path);
        Assert.Contains("loginServerAddress=server.local", content);
        Assert.Contains("loginServerPort=44453", content);
        Assert.True(
            content.IndexOf("loginServerPort=44453", StringComparison.Ordinal) <
            content.IndexOf("[Direct3d9]", StringComparison.Ordinal));
    }

    private static int Count(string value, string needle) =>
        value.Split(needle, StringSplitOptions.None).Length - 1;
}
