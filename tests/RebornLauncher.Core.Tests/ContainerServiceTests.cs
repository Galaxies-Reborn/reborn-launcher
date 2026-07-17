using RebornLauncher.Core.Services;

namespace RebornLauncher.Core.Tests;

public sealed class ContainerServiceTests
{
    [Fact]
    public void ProjectNameIsComposeSafeAndBounded()
    {
        var name = ContainerService.BuildProjectName(
            "x64 DX9 vanilla with spaces",
            new string('A', 100));

        Assert.Matches("^[a-z0-9_-]+$", name);
        Assert.True(name.Length <= 63);
    }
}
