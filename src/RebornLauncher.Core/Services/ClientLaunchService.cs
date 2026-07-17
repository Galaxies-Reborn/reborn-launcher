using RebornLauncher.Core.Models;

namespace RebornLauncher.Core.Services;

public enum ClientRuntime
{
    /// <summary>The client is a native executable for this host.</summary>
    Native,

    /// <summary>The client runs through a Windows compatibility layer.</summary>
    Compatibility,
}

/// <summary>How a client would be started. Resolved separately from starting it, so the launcher
/// can explain what it will do, or why it cannot, before anything runs.</summary>
public sealed record ClientLaunchPlan(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    ClientRuntime Runtime)
{
    public string Describe() => Runtime == ClientRuntime.Native
        ? Executable
        : $"{Executable} {string.Join(' ', Arguments)}";
}

/// <summary>
/// Starts the game clients. They are Windows executables, so on Linux and macOS they run through a
/// compatibility layer. That layer is the operator's own installation: it cannot be bundled, and
/// how well a given client runs under it is outside this launcher's control.
/// </summary>
public sealed class ClientLaunchService(ProcessRunner processRunner)
{
    /// <summary>Compatibility tools probed on PATH, in preference order.</summary>
    public static IReadOnlyList<string> KnownCompatibilityTools { get; } = ["wine", "wine64"];

    /// <summary>True where the Windows client executables cannot run directly.</summary>
    public static bool RequiresCompatibilityLayer => !OperatingSystem.IsWindows();

    /// <summary>
    /// Builds the plan for starting a client. <paramref name="compatibilityTool"/> overrides the
    /// probed tool, allowing a Proton wrapper or a specific Wine prefix launcher to be used.
    /// </summary>
    /// <param name="requiresCompatibilityLayer">
    /// Defaults to whether this host needs one. Stated explicitly so the Linux and macOS shape can
    /// be exercised from any host.
    /// </param>
    public static ClientLaunchPlan CreatePlan(
        string executable,
        string? compatibilityTool = null,
        bool? requiresCompatibilityLayer = null)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new ArgumentException("A client executable is required.", nameof(executable));
        }

        var workingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))
            ?? throw new ArgumentException("The client executable has no parent directory.", nameof(executable));

        if (!(requiresCompatibilityLayer ?? RequiresCompatibilityLayer))
        {
            return new ClientLaunchPlan(executable, [], workingDirectory, ClientRuntime.Native);
        }

        if (string.IsNullOrWhiteSpace(compatibilityTool))
        {
            throw new InvalidOperationException(
                "The game clients are Windows executables. Install Wine, or set a Proton wrapper in the " +
                "instance settings, to run them on this system.");
        }

        return new ClientLaunchPlan(compatibilityTool, [executable], workingDirectory, ClientRuntime.Compatibility);
    }

    /// <summary>Finds a usable compatibility tool, preferring an explicit one over any on PATH.</summary>
    public async Task<string?> ResolveCompatibilityToolAsync(
        string? configured = null,
        CancellationToken cancellationToken = default)
    {
        if (!RequiresCompatibilityLayer)
        {
            return null;
        }

        // An explicit path may be a Proton wrapper script, which will not be on PATH by name.
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured) || await ProbeAsync(configured, cancellationToken))
            {
                return configured;
            }

            throw new FileNotFoundException(
                $"The configured compatibility tool '{configured}' could not be run.",
                configured);
        }

        foreach (var candidate in KnownCompatibilityTools)
        {
            if (await ProbeAsync(candidate, cancellationToken))
            {
                return candidate;
            }
        }

        return null;
    }

    public async Task<ClientLaunchPlan> LaunchAsync(
        string executable,
        string? configuredTool = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The selected client is not installed for this instance.", executable);
        }

        var tool = await ResolveCompatibilityToolAsync(configuredTool, cancellationToken);
        var plan = CreatePlan(executable, tool);
        processRunner.Start(plan.Executable, plan.Arguments, plan.WorkingDirectory);
        return plan;
    }

    private async Task<bool> ProbeAsync(string executable, CancellationToken cancellationToken)
    {
        try
        {
            var result = await processRunner.RunAsync(
                executable,
                ["--version"],
                Environment.CurrentDirectory,
                cancellationToken: cancellationToken);
            return result.Succeeded;
        }
        catch (Exception exception) when (exception is FileNotFoundException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
