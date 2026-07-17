using System.Text.Json.Serialization;

namespace RebornLauncher.Core.Models;

public sealed class InstanceSettings
{
    public string ChannelId { get; set; } = "x64-dx9-vanilla";

    public string InstallRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Galaxies Reborn",
        "Instances");

    public string InstanceName { get; set; } = "My SWG Server";

    public string ClusterName { get; set; } = "swg";

    public string PublicAddress { get; set; } = "127.0.0.1";

    public int LoginPort { get; set; } = 44453;

    public ContainerBackendKind ContainerBackend { get; set; } = ContainerBackendKind.Auto;

    /// <summary>
    /// Wine binary or Proton wrapper used to run the Windows clients on Linux and macOS. Empty
    /// probes PATH. Ignored on Windows, where the clients are native.
    /// </summary>
    public string CompatibilityTool { get; set; } = string.Empty;

    /// <summary>
    /// Retail client folder holding the .tre files SWGEmu's Core3 needs. They cannot be
    /// distributed, so the operator supplies their own copy.
    /// </summary>
    public string RetailClientDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Folder holding the operator's own game client. Galaxies Reborn does not distribute clients,
    /// so this is supplied rather than downloaded. Empty disables the client controls.
    /// </summary>
    public string ClientDirectory { get; set; } = string.Empty;

    public bool InitializeServer { get; set; } = true;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public string DatabasePassword { get; set; } = string.Empty;

    [JsonIgnore]
    public string DatabaseAdminPassword { get; set; } = string.Empty;
}

public enum ContainerBackendKind
{
    Auto,
    DockerDesktop,
    PodmanDesktop,
    RancherDesktopMoby,
    RancherDesktopContainerd,
}

public sealed record ContainerBackendDefinition(
    ContainerBackendKind Kind,
    string DisplayName,
    string Executable,
    string InstallUri,
    string Description)
{
    public override string ToString() => DisplayName;
}
