using RebornLauncher.Core.Services;

namespace RebornLauncher.Core.Tests;

public sealed class UpstreamIsolationTests
{
    [Theory]
    [InlineData("https://github.com/SWG-Source/src.git")]
    [InlineData("https://github.com/swg-source/src.git")]
    [InlineData("git@github.com:SWG-Source/client-tools.git")]
    [InlineData("ssh://git@github.com/swg-source/swg-main.git")]
    public async Task ExternalSourceUrlsAreBlockedBeforeNetworkAccess(string remote)
    {
        var git = new GitService(new ProcessRunner(), "git");
        var result = await git.RunAsync(Environment.CurrentDirectory, ["ls-remote", remote]);
        Assert.False(result.Succeeded);
        Assert.Contains("upstream' not allowed", result.CombinedOutput);
    }
}
