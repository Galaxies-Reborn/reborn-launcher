using System.Text.RegularExpressions;
using RebornLauncher.Core.Models;

namespace RebornLauncher.Core.Services;

public sealed partial class ContainerService(ProcessRunner processRunner)
{
    public static IReadOnlyList<ContainerBackendDefinition> Backends { get; } =
    [
        new(
            ContainerBackendKind.Auto,
            "Automatic detection",
            string.Empty,
            string.Empty,
            "Use the first healthy Docker, Podman, or Rancher Desktop command."),
        new(
            ContainerBackendKind.DockerDesktop,
            "Docker Desktop",
            "docker",
            "https://www.docker.com/products/docker-desktop/",
            "Docker Engine with the built-in Compose plugin."),
        new(
            ContainerBackendKind.PodmanDesktop,
            "Podman Desktop",
            "podman",
            "https://podman-desktop.io/downloads",
            "Podman with an installed Compose provider."),
        new(
            ContainerBackendKind.RancherDesktopMoby,
            "Rancher Desktop (Moby)",
            "docker",
            "https://rancherdesktop.io/",
            "Rancher Desktop using its Docker-compatible Moby engine."),
        new(
            ContainerBackendKind.RancherDesktopContainerd,
            "Rancher Desktop (containerd)",
            "nerdctl",
            "https://rancherdesktop.io/",
            "Rancher Desktop using containerd and nerdctl Compose."),
    ];

    /// <param name="requireCompose">
    /// False for pipelines that drive the engine directly. SWGEmu's Core3 builds and runs a single
    /// container of its own, so demanding Compose would reject an otherwise usable engine.
    /// </param>
    public async Task<ContainerBackendDefinition> ResolveAsync(
        ContainerBackendKind selectedBackend,
        CancellationToken cancellationToken = default,
        bool requireCompose = true)
    {
        if (selectedBackend != ContainerBackendKind.Auto)
        {
            var selected = Backends.Single(backend => backend.Kind == selectedBackend);
            await ValidateAsync(selected, cancellationToken, requireCompose);
            return selected;
        }

        foreach (var backend in Backends.Where(backend => backend.Kind is
                     ContainerBackendKind.DockerDesktop or
                     ContainerBackendKind.PodmanDesktop or
                     ContainerBackendKind.RancherDesktopContainerd))
        {
            try
            {
                await ValidateAsync(backend, cancellationToken, requireCompose);
                return backend;
            }
            catch (Exception exception) when (exception is FileNotFoundException or InvalidOperationException)
            {
                // Keep probing the supported commands.
            }
        }

        throw new InvalidOperationException(
            "No supported container environment is ready. Start Docker Desktop, Podman Desktop, or Rancher Desktop and try again.");
    }

    public async Task ValidateAsync(
        ContainerBackendDefinition backend,
        CancellationToken cancellationToken = default,
        bool requireCompose = true)
    {
        var version = await processRunner.RunAsync(
            backend.Executable,
            ["version"],
            Environment.CurrentDirectory,
            cancellationToken: cancellationToken);
        if (!version.Succeeded)
        {
            throw new InvalidOperationException(
                $"{backend.DisplayName} is installed but its engine is not ready.{Environment.NewLine}{version.CombinedOutput}");
        }

        if (!requireCompose)
        {
            return;
        }

        var compose = await processRunner.RunAsync(
            backend.Executable,
            ["compose", "version"],
            Environment.CurrentDirectory,
            cancellationToken: cancellationToken);
        if (!compose.Succeeded)
        {
            throw new InvalidOperationException(
                $"{backend.DisplayName} is available, but Compose is not ready.{Environment.NewLine}{compose.CombinedOutput}");
        }
    }

    public async Task InitializeAndStartAsync(
        InstanceSettings settings,
        ReleaseChannel channel,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        var backend = await ResolveAsync(settings.ContainerBackend, cancellationToken);
        output?.Invoke($"Using {backend.DisplayName}.");

        await RunRequiredAsync(backend, settings, channel, ["build", "swg-server"], output, cancellationToken);
        await RunRequiredAsync(backend, settings, channel, ["up", "-d", "oracle"], output, cancellationToken);
        await RunRequiredAsync(backend, settings, channel, ["run", "--rm", "swg-server", "init"], output, cancellationToken);
        await RunRequiredAsync(backend, settings, channel, ["up", "-d", "swg-server"], output, cancellationToken);
    }

    public async Task<CommandResult> StartAsync(
        InstanceSettings settings,
        ReleaseChannel channel,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        var backend = await ResolveAsync(settings.ContainerBackend, cancellationToken);
        return await RunComposeAsync(backend, settings, channel, ["up", "-d"], output, cancellationToken);
    }

    public async Task<CommandResult> StopAsync(
        InstanceSettings settings,
        ReleaseChannel channel,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        var backend = await ResolveAsync(settings.ContainerBackend, cancellationToken);
        return await RunComposeAsync(backend, settings, channel, ["down"], output, cancellationToken);
    }

    public async Task<CommandResult> StatusAsync(
        InstanceSettings settings,
        ReleaseChannel channel,
        CancellationToken cancellationToken = default)
    {
        var backend = await ResolveAsync(settings.ContainerBackend, cancellationToken);
        return await RunComposeAsync(backend, settings, channel, ["ps"], cancellationToken: cancellationToken);
    }

    public async Task<CommandResult> LogsAsync(
        InstanceSettings settings,
        ReleaseChannel channel,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        var backend = await ResolveAsync(settings.ContainerBackend, cancellationToken);
        return await RunComposeAsync(
            backend,
            settings,
            channel,
            ["logs", "--no-color", "--tail", "400"],
            output,
            cancellationToken);
    }

    private async Task RunRequiredAsync(
        ContainerBackendDefinition backend,
        InstanceSettings settings,
        ReleaseChannel channel,
        IReadOnlyList<string> composeArguments,
        Action<string>? output,
        CancellationToken cancellationToken)
    {
        var result = await RunComposeAsync(
            backend,
            settings,
            channel,
            composeArguments,
            output,
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Container command failed with exit code {result.ExitCode}.{Environment.NewLine}{result.CombinedOutput}");
        }
    }

    private Task<CommandResult> RunComposeAsync(
        ContainerBackendDefinition backend,
        InstanceSettings settings,
        ReleaseChannel channel,
        IReadOnlyList<string> composeArguments,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        var paths = new LauncherPaths(settings, channel);
        if (!File.Exists(paths.ComposePath) || !File.Exists(paths.ComposeOverridePath))
        {
            throw new FileNotFoundException("The server instance has not been prepared yet.", paths.ComposePath);
        }

        var arguments = new List<string>
        {
            "compose",
            "-f",
            paths.ComposePath,
            "-f",
            paths.ComposeOverridePath,
            "--project-name",
            BuildProjectName(channel.Id, settings.ClusterName),
        };
        arguments.AddRange(composeArguments);

        return processRunner.RunAsync(
            backend.Executable,
            arguments,
            paths.MainRepositoryRoot,
            InstanceConfigurationService.BuildProcessEnvironment(settings),
            output,
            cancellationToken);
    }

    public static string BuildProjectName(string channelId, string clusterName)
    {
        var value = $"reborn-{channelId}-{clusterName}".ToLowerInvariant();
        value = InvalidProjectCharacterPattern().Replace(value, "-").Trim('-');
        return value.Length <= 63 ? value : value[..63].TrimEnd('-');
    }

    [GeneratedRegex("[^a-z0-9_-]+")]
    private static partial Regex InvalidProjectCharacterPattern();
}
