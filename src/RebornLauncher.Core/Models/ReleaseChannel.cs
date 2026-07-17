namespace RebornLauncher.Core.Models;

public sealed class ChannelCatalog
{
    public int SchemaVersion { get; init; } = 1;

    public List<ChannelReference> Channels { get; init; } = [];
}

public sealed class ChannelReference
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Manifest { get; init; }

    public string Description { get; init; } = string.Empty;
}

public sealed class ReleaseChannel
{
    public int SchemaVersion { get; init; } = 1;

    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Branch { get; init; }

    /// <summary>Umbrella project identifier. Empty for schema version 1 manifests.</summary>
    public string Project { get; init; } = string.Empty;

    /// <summary>
    /// Umbrella flavor identifier. Empty both for schema version 1 manifests and for projects that
    /// offer variants directly rather than grouping them by era.
    /// </summary>
    public string Flavor { get; init; } = string.Empty;

    /// <summary>
    /// Path of this variant inside the umbrella working tree. Declared rather than derived, because
    /// projects with flavors nest one level deeper than projects without them.
    /// </summary>
    public string VariantPath { get; init; } = string.Empty;

    /// <summary>Preparation pipeline that can build and run this variant.</summary>
    public PipelineKind Pipeline { get; init; } = PipelineKind.SwgSource;

    /// <summary>Graphics backend this variant targets.</summary>
    public RendererKind Renderer { get; init; } = RendererKind.Unspecified;

    public string Description { get; init; } = string.Empty;

    public DateTimeOffset PublishedAt { get; init; }

    public ServerRelease Server { get; init; } = new();

    /// <summary>
    /// Where a client's login configuration lives, relative to the folder the operator points at.
    /// Galaxies Reborn does not distribute clients; players bring their own.
    /// </summary>
    public ClientLayout Clients { get; init; } = new();

    /// <summary>The variant path implied by the project, flavor, and id.</summary>
    public string ExpectedVariantPath => string.IsNullOrEmpty(Flavor)
        ? $"{Project}/{Id}"
        : $"{Project}/{Flavor}/{Id}";

    public override string ToString() => DisplayName;
}

/// <summary>
/// Source lineages do not share a build. SWGSource-derived trees are driven through swg-main's
/// Compose stack; SWGEmu's Core3 carries its own container build and runtime.
/// </summary>
public enum PipelineKind
{
    /// <summary>swg-main with Oracle and the Compose stack.</summary>
    SwgSource,

    /// <summary>SWGEmu Core3 with its own Dockerfile, MySQL, and CMake build.</summary>
    Core3,
}

public sealed class ServerRelease
{
    public string RootDirectory { get; init; } = "server";

    public string MainRepositoryPath { get; init; } = "swg-main";

    public string AssetsRepositoryPath { get; init; } = "client-assets";

    /// <summary>Pinned source archives. Schema version 1 only.</summary>
    public List<RepositoryArchive> Repositories { get; init; } = [];

    /// <summary>Umbrella submodules backing this variant. Schema version 4 only.</summary>
    public List<SubmoduleSpec> Submodules { get; init; } = [];
}

public sealed class RepositoryArchive
{
    public required string Name { get; init; }

    public required string Repository { get; init; }

    public required string Revision { get; init; }

    public required string InstallPath { get; init; }

    public string Branch { get; init; } = string.Empty;

    public Uri GetArchiveUri()
    {
        var repository = Repository.TrimEnd('/');
        return new Uri($"{repository}/archive/{Revision}.zip", UriKind.Absolute);
    }
}

/// <summary>
/// Describes how to find a client's parts inside a folder the operator already has. Clients are not
/// distributed, so these are paths to look for, never anything to download.
/// </summary>
public sealed class ClientLayout
{
    /// <summary>
    /// False for a variant that has no usable client at all, such as an upstream source set. The
    /// launcher offers no client controls for it.
    /// </summary>
    public bool Supported { get; init; }

    /// <summary>Regular client, relative to the operator's client folder.</summary>
    public string RegularExecutable { get; init; } = string.Empty;

    public string GodExecutable { get; init; } = string.Empty;

    public string RegularLoginConfiguration { get; init; } = string.Empty;

    public string GodLoginConfiguration { get; init; } = string.Empty;
}
