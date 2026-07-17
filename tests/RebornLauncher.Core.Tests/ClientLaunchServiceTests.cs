using RebornLauncher.Core.Services;

namespace RebornLauncher.Core.Tests;

/// <summary>
/// The launch plan is resolved separately from launching, so the non-Windows path can be exercised
/// on any host without Wine installed.
/// </summary>
public sealed class ClientLaunchServiceTests
{
    [Fact]
    public void PlanRunsTheClientDirectlyWhereNoLayerIsNeeded()
    {
        var executable = Path.Combine(Path.GetTempPath(), "clients", "SwgClient_r.exe");

        var plan = ClientLaunchService.CreatePlan(executable, "wine", requiresCompatibilityLayer: false);

        Assert.Equal(ClientRuntime.Native, plan.Runtime);
        Assert.Equal(executable, plan.Executable);
        // A tool is irrelevant on a host that runs the client natively, so it is ignored.
        Assert.Empty(plan.Arguments);
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(executable)), plan.WorkingDirectory);
    }

    [Fact]
    public void PlanPassesTheClientToTheCompatibilityToolWhereOneIsNeeded()
    {
        var executable = Path.Combine(Path.GetTempPath(), "clients", "SwgClient_r.exe");

        var plan = ClientLaunchService.CreatePlan(executable, "wine", requiresCompatibilityLayer: true);

        Assert.Equal(ClientRuntime.Compatibility, plan.Runtime);
        Assert.Equal("wine", plan.Executable);
        Assert.Equal([executable], plan.Arguments);

        // The client resolves its own data relative to the working directory, not the tool's.
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(executable)), plan.WorkingDirectory);
    }

    [Fact]
    public void PlanAcceptsAProtonWrapperPath()
    {
        var plan = ClientLaunchService.CreatePlan(
            "/opt/clients/SwgClient_r.exe",
            "/home/me/proton-run",
            requiresCompatibilityLayer: true);

        Assert.Equal("/home/me/proton-run", plan.Executable);
        Assert.Equal(["/opt/clients/SwgClient_r.exe"], plan.Arguments);
    }

    [Fact]
    public void PlanRefusesToGuessWhenALayerIsNeededButNoneIsAvailable()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ClientLaunchService.CreatePlan(
            "/opt/clients/SwgClient_r.exe",
            compatibilityTool: null,
            requiresCompatibilityLayer: true));

        Assert.Contains("Wine", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanRejectsAnEmptyExecutable()
    {
        Assert.Throws<ArgumentException>(() => ClientLaunchService.CreatePlan(string.Empty, "wine"));
    }

    [Fact]
    public async Task ResolvingRejectsAConfiguredToolThatCannotRun()
    {
        if (!ClientLaunchService.RequiresCompatibilityLayer)
        {
            return;
        }

        var service = new ClientLaunchService(new ProcessRunner());

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => service.ResolveCompatibilityToolAsync("/nonexistent/proton-run"));
    }

    [Fact]
    public async Task ResolvingReturnsNothingOnWindowsBecauseNoLayerIsNeeded()
    {
        if (ClientLaunchService.RequiresCompatibilityLayer)
        {
            return;
        }

        var service = new ClientLaunchService(new ProcessRunner());

        Assert.Null(await service.ResolveCompatibilityToolAsync());
        Assert.Null(await service.ResolveCompatibilityToolAsync("wine"));
    }

    [Fact]
    public async Task LaunchingRejectsAClientThatIsNotInstalled()
    {
        var service = new ClientLaunchService(new ProcessRunner());
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "SwgClient_r.exe");

        await Assert.ThrowsAsync<FileNotFoundException>(() => service.LaunchAsync(missing));
    }
}
