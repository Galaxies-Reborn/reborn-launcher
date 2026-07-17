namespace RebornLauncher.Core.Models;

public sealed class LauncherPaths
{
    public LauncherPaths(InstanceSettings settings, ReleaseChannel channel)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(channel);

        InstanceRoot = Path.GetFullPath(Path.Combine(settings.InstallRoot, channel.Id));
        UmbrellaRoot = Path.Combine(InstanceRoot, "umbrella");

        // Umbrella-backed variants live inside the clone, so Git can update them in place. Schema
        // version 1 channels keep their extracted layout under the instance.
        ServerRoot = channel.SchemaVersion >= 4
            ? Path.Combine(UmbrellaRoot, channel.VariantPath.Replace('/', Path.DirectorySeparatorChar))
            : Path.Combine(InstanceRoot, channel.Server.RootDirectory);

        MainRepositoryRoot = Path.Combine(ServerRoot, channel.Server.MainRepositoryPath);
        AssetsRepositoryRoot = string.IsNullOrEmpty(channel.Server.AssetsRepositoryPath)
            ? ServerRoot
            : Path.Combine(ServerRoot, channel.Server.AssetsRepositoryPath);
        // The operator's own client folder. Clients are not distributed, so nothing is ever written
        // here and it lives wherever they already keep it, outside the instance.
        ClientRoot = string.IsNullOrWhiteSpace(settings.ClientDirectory)
            ? string.Empty
            : Path.GetFullPath(settings.ClientDirectory);

        StateRoot = Path.Combine(InstanceRoot, ".reborn");
        CacheRoot = Path.Combine(StateRoot, "cache");
        SettingsPath = Path.Combine(StateRoot, "instance.json");
        SourceStatePath = Path.Combine(StateRoot, "source-state.json");
        EnvironmentPath = Path.Combine(MainRepositoryRoot, ".env.reborn");
        ComposeOverridePath = Path.Combine(MainRepositoryRoot, "docker-compose.reborn.yml");
        ComposePath = Path.Combine(MainRepositoryRoot, "docker-compose.yml");

        // Core3 publishes no Compose file, so the launcher generates one. It lives beside the
        // instance state rather than inside the submodule, which would leave the working tree
        // dirty and block later submodule updates.
        Core3ComposePath = Path.Combine(StateRoot, "docker-compose.core3.yml");
    }

    public string InstanceRoot { get; }

    /// <summary>The umbrella clone backing this instance. Empty for schema version 1 channels.</summary>
    public string UmbrellaRoot { get; }

    public string ServerRoot { get; }

    public string MainRepositoryRoot { get; }

    public string AssetsRepositoryRoot { get; }

    /// <summary>The operator's own client folder. Empty when none has been chosen.</summary>
    public string ClientRoot { get; }

    public string StateRoot { get; }

    public string CacheRoot { get; }

    public string SettingsPath { get; }

    public string SourceStatePath { get; }

    public string EnvironmentPath { get; }

    public string ComposeOverridePath { get; }

    public string ComposePath { get; }

    /// <summary>Generated Compose file for the Core3 pipeline.</summary>
    public string Core3ComposePath { get; }

    public string RegularClientExecutable(ReleaseChannel channel) =>
        ResolveClientPath(channel?.Clients.RegularExecutable);

    public string GodClientExecutable(ReleaseChannel channel) =>
        ResolveClientPath(channel?.Clients.GodExecutable);

    public string RegularLoginConfiguration(ReleaseChannel channel) =>
        ResolveClientPath(channel?.Clients.RegularLoginConfiguration);

    public string GodLoginConfiguration(ReleaseChannel channel) =>
        ResolveClientPath(channel?.Clients.GodLoginConfiguration);

    /// <summary>Empty when no client folder is set, so callers see "not configured" and not a path
    /// rooted at the launcher's working directory.</summary>
    private string ResolveClientPath(string? relativePath) =>
        string.IsNullOrEmpty(ClientRoot) || string.IsNullOrWhiteSpace(relativePath)
            ? string.Empty
            : Path.Combine(ClientRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
