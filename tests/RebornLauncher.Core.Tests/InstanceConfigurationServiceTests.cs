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
}
