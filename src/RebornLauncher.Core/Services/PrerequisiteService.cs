using RebornLauncher.Core.Models;

namespace RebornLauncher.Core.Services;

public enum PrerequisiteState
{
    Missing,

    /// <summary>Present and usable.</summary>
    Ready,

    /// <summary>Present but not usable, such as an engine that is installed but not running.</summary>
    NotReady,
}

/// <summary>
/// Something the host needs before a build can run. Anything the build itself needs lives inside
/// the container images, so this list stays short by design.
/// </summary>
public sealed record Prerequisite(
    string Id,
    string DisplayName,
    string Purpose,
    PrerequisiteState State,
    string Detail,
    string? InstallerUri,
    bool Required)
{
    /// <summary>True when the launcher can fetch and run an installer for this itself.</summary>
    public bool CanInstall => State != PrerequisiteState.Ready && InstallerUri is not null;

    public override string ToString() => $"{DisplayName}: {State}";
}

/// <summary>
/// Reports what the host is missing and, where the vendor publishes a direct installer, fetches it.
///
/// The launcher never silently installs: it hands the operator an installer to approve, because
/// these are machine-wide changes that are not ours to make quietly.
/// </summary>
public sealed class PrerequisiteService(ProcessRunner processRunner, ContainerService containerService)
{
    // Vendor download endpoints. Only used when the operator asks for the install.
    private const string DockerDesktopWindows = "https://desktop.docker.com/win/main/amd64/Docker%20Desktop%20Installer.exe";
    private const string DockerDesktopMacArm = "https://desktop.docker.com/mac/main/arm64/Docker.dmg";
    private const string DockerDesktopMacIntel = "https://desktop.docker.com/mac/main/amd64/Docker.dmg";
    private const string DockerLinuxDocs = "https://docs.docker.com/engine/install/debian/";

    /// <summary>Builds the client from source. Not needed to run a server.</summary>
    private const string BuildToolsWindows =
        "https://aka.ms/vs/17/release/vs_BuildTools.exe";

    public async Task<IReadOnlyList<Prerequisite>> InspectAsync(
        InstanceSettings settings,
        bool includeClientBuild = false,
        CancellationToken cancellationToken = default)
    {
        var prerequisites = new List<Prerequisite> { await InspectGitAsync(cancellationToken) };
        prerequisites.Add(await InspectContainerRuntimeAsync(settings, cancellationToken));

        if (includeClientBuild && OperatingSystem.IsWindows())
        {
            prerequisites.Add(await InspectMsBuildAsync(cancellationToken));
        }

        return prerequisites;
    }

    private async Task<Prerequisite> InspectGitAsync(CancellationToken cancellationToken)
    {
        // Windows installs ship MinGit, so this is normally satisfied before the launcher runs.
        try
        {
            var installation = await new GitBootstrapper(processRunner).ResolveAsync(null, cancellationToken);
            return new Prerequisite(
                "git",
                "Git",
                "Downloads the server sources.",
                PrerequisiteState.Ready,
                installation.ToString(),
                null,
                Required: true);
        }
        catch (FileNotFoundException)
        {
            return new Prerequisite(
                "git",
                "Git",
                "Downloads the server sources.",
                PrerequisiteState.Missing,
                OperatingSystem.IsWindows()
                    ? "Reinstall Reborn Launcher to restore its bundled copy."
                    : "Install Git with your package manager, for example: sudo apt install git",
                null,
                Required: true);
        }
    }

    private async Task<Prerequisite> InspectContainerRuntimeAsync(
        InstanceSettings settings,
        CancellationToken cancellationToken)
    {
        const string purpose = "Builds and runs the server.";
        try
        {
            var backend = await containerService.ResolveAsync(settings.ContainerBackend, cancellationToken);
            return new Prerequisite(
                "container",
                "Container runtime",
                purpose,
                PrerequisiteState.Ready,
                backend.DisplayName,
                null,
                Required: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or FileNotFoundException)
        {
            // Installed-but-stopped and not-installed both land here; the message distinguishes them.
            var stopped = exception is InvalidOperationException;
            return new Prerequisite(
                "container",
                "Docker Desktop",
                purpose,
                stopped ? PrerequisiteState.NotReady : PrerequisiteState.Missing,
                exception.Message.Split(Environment.NewLine)[0],
                DockerInstaller(),
                Required: true);
        }
    }

    private async Task<Prerequisite> InspectMsBuildAsync(CancellationToken cancellationToken)
    {
        const string purpose = "Builds the game client from source. Not needed to run a server.";
        var located = await LocateMsBuildAsync(cancellationToken);
        return located is null
            ? new Prerequisite(
                "msbuild",
                "Visual Studio Build Tools",
                purpose,
                PrerequisiteState.Missing,
                "Not found. Needed only if you build the client yourself.",
                BuildToolsWindows,
                Required: false)
            : new Prerequisite("msbuild", "Visual Studio Build Tools", purpose, PrerequisiteState.Ready, located, null, Required: false);
    }

    /// <summary>
    /// Finds MSBuild through vswhere, which ships with every Visual Studio installer and is the
    /// supported way to locate an installation; probing Program Files misses side-by-side versions.
    /// </summary>
    private async Task<string?> LocateMsBuildAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio",
            "Installer",
            "vswhere.exe");
        if (!File.Exists(vswhere))
        {
            return null;
        }

        var result = await processRunner.RunAsync(
            vswhere,
            [
                "-latest", "-products", "*",
                "-requires", "Microsoft.Component.MSBuild",
                "-find", @"MSBuild\**\Bin\MSBuild.exe",
            ],
            Environment.CurrentDirectory,
            cancellationToken: cancellationToken);

        var path = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return result.Succeeded && !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
    }

    private static string DockerInstaller() =>
        OperatingSystem.IsWindows() ? DockerDesktopWindows
        : OperatingSystem.IsMacOS()
            ? System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
              System.Runtime.InteropServices.Architecture.Arm64
                ? DockerDesktopMacArm
                : DockerDesktopMacIntel
            // Linux installs through the distribution's package manager, so the docs are the
            // honest destination rather than an installer we would have to guess at.
            : DockerLinuxDocs;
}
