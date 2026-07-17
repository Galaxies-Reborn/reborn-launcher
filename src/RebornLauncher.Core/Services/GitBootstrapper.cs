using RebornLauncher.Core.Models;

namespace RebornLauncher.Core.Services;

public enum GitSource
{
    /// <summary>MinGit shipped beside the launcher by the installer.</summary>
    Bundled,

    /// <summary>A Git installation already present on PATH.</summary>
    System,
}

public sealed record GitInstallation(GitSource Source, string Executable, string Version)
{
    public override string ToString() => $"{Version} ({Source})";
}

/// <summary>
/// Locates a usable Git. The installer ships MinGit beside the launcher so that a fresh machine
/// needs no manual prerequisite; a developer machine falls back to Git on PATH.
/// </summary>
public sealed class GitBootstrapper(ProcessRunner processRunner)
{
    /// <summary>Location of MinGit relative to the launcher directory, as laid down by the installer.</summary>
    public static string BundledRelativePath { get; } = Path.Combine("tools", "MinGit", "cmd", "git.exe");

    public async Task<GitInstallation> ResolveAsync(
        string? baseDirectory = null,
        CancellationToken cancellationToken = default)
    {
        baseDirectory ??= AppContext.BaseDirectory;

        var bundled = Path.Combine(baseDirectory, BundledRelativePath);
        if (File.Exists(bundled) &&
            await ProbeAsync(bundled, cancellationToken) is { } bundledVersion)
        {
            return new GitInstallation(GitSource.Bundled, bundled, bundledVersion);
        }

        if (await ProbeAsync("git", cancellationToken) is { } systemVersion)
        {
            return new GitInstallation(GitSource.System, "git", systemVersion);
        }

        throw new FileNotFoundException(
            "No Git installation was found. Reinstall Reborn Launcher so it can restore its bundled " +
            $"copy at '{BundledRelativePath}', or install Git for Windows and ensure it is on PATH.");
    }

    public GitService CreateService(GitInstallation installation) =>
        new(processRunner, installation.Executable);

    private async Task<string?> ProbeAsync(string executable, CancellationToken cancellationToken)
    {
        try
        {
            var result = await processRunner.RunAsync(
                executable,
                ["--version"],
                Environment.CurrentDirectory,
                null,
                null,
                cancellationToken);
            return result.Succeeded ? result.StandardOutput.Trim() : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
