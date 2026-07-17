using System.Text;
using RebornLauncher.Core.Services;

namespace RebornLauncher.Core.Tests;

public sealed class ChannelManifestServiceTests
{
    [Fact]
    public async Task LoadChannelAcceptsPinnedRepositories()
    {
        const string json =
            """
            {
              "schemaVersion": 1,
              "id": "x64-dx9-vanilla",
              "displayName": "Vanilla",
              "branch": "x64-dx9-vanilla",
              "server": {
                "repositories": [{
                  "name": "swg-main",
                  "repository": "https://github.com/Galaxies-Reborn/swg-main",
                  "revision": "b3b186d1c22b2877123baffbd291ea7a2892123a",
                  "installPath": "swg-main"
                }]
              },
              "clients": {
                "payloadDirectory": "clients",
                "regularExecutable": "clients/regular.exe",
                "godExecutable": "clients/god.exe",
                "regularLoginConfiguration": "clients/login.cfg",
                "godLoginConfiguration": "clients/client.cfg"
              }
            }
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var channel = await ChannelManifestService.LoadChannelAsync(stream);

        Assert.Equal("x64-dx9-vanilla", channel.Id);
        Assert.Single(channel.Server.Repositories);
    }

    [Fact]
    public async Task LoadChannelRejectsPathTraversal()
    {
        const string json =
            """
            {
              "schemaVersion": 1,
              "id": "test",
              "displayName": "Test",
              "branch": "test",
              "server": {
                "repositories": [{
                  "name": "source",
                  "repository": "https://github.com/Galaxies-Reborn/swg-main",
                  "revision": "b3b186d1c22b2877123baffbd291ea7a2892123a",
                  "installPath": "../outside"
                }]
              },
              "clients": {
                "payloadDirectory": "clients",
                "regularExecutable": "clients/regular.exe",
                "godExecutable": "clients/god.exe",
                "regularLoginConfiguration": "clients/login.cfg",
                "godLoginConfiguration": "clients/client.cfg"
              }
            }
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await Assert.ThrowsAsync<InvalidDataException>(() => ChannelManifestService.LoadChannelAsync(stream));
    }
}
