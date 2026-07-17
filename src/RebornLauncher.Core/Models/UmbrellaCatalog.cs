namespace RebornLauncher.Core.Models;

public sealed class UmbrellaCatalog
{
    public int SchemaVersion { get; init; } = 4;

    public List<ProjectReference> Projects { get; init; } = [];

    /// <summary>Every variant on offer, flattened across projects and flavors.</summary>
    public IEnumerable<VariantReference> AllVariants =>
        Projects.SelectMany(project => project.AllVariants);
}

/// <summary>
/// A top-level source lineage. A project either groups its variants under flavors (eras), as
/// Galaxies Reborn does, or offers them directly, as SWGEmu and SWG Source do. Never both.
/// </summary>
public sealed class ProjectReference
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string Description { get; init; } = string.Empty;

    public required string Path { get; init; }

    public bool Available { get; init; }

    public List<FlavorReference> Flavors { get; init; } = [];

    public List<VariantReference> Variants { get; init; } = [];

    /// <summary>True when this project groups variants by flavor rather than offering them directly.</summary>
    public bool HasFlavors => Flavors.Count > 0;

    public IEnumerable<VariantReference> AllVariants =>
        Variants.Concat(Flavors.SelectMany(flavor => flavor.Variants));

    public override string ToString() => DisplayName;
}

public sealed class FlavorReference
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public string Description { get; init; } = string.Empty;

    public required string Path { get; init; }

    public bool Available { get; init; }

    public List<VariantReference> Variants { get; init; } = [];

    /// <summary>Renderers offered here, in declared order. Empty when this era is not renderer specific.</summary>
    public IReadOnlyList<RendererKind> Renderers => Variants
        .Select(variant => variant.Renderer)
        .Where(renderer => renderer != RendererKind.Unspecified)
        .Distinct()
        .ToList();

    public override string ToString() => DisplayName;
}

/// <summary>
/// Graphics backend a variant targets. Orthogonal to era: a renderer multiplies against every era
/// rather than nesting under one. Projects that are not renderer specific leave it unspecified.
/// </summary>
public enum RendererKind
{
    Unspecified,

    Dx9,

    Dx11,
}

public sealed class VariantReference
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Empty when the variant is announced but not yet published.</summary>
    public string Manifest { get; init; } = string.Empty;

    public RendererKind Renderer { get; init; } = RendererKind.Unspecified;

    /// <summary>False for a variant that is listed so it can be seen coming, but has no sources yet.</summary>
    public bool Available { get; init; } = true;

    public string Description { get; init; } = string.Empty;

    public override string ToString() => DisplayName;
}

public sealed class SubmoduleSpec
{
    public required string Name { get; init; }

    /// <summary>Path of the submodule inside the umbrella working tree.</summary>
    public required string Path { get; init; }

    /// <summary>Path of the submodule relative to the variant root.</summary>
    public required string InstallPath { get; init; }

    public string Repository { get; init; } = string.Empty;

    public string Branch { get; init; } = string.Empty;

    /// <summary>Initialize nested submodules. Required for repositories carrying their own .gitmodules.</summary>
    public bool Recursive { get; init; }

    /// <summary>Fetched whenever the variant is prepared. Optional submodules are fetched only on request.</summary>
    public bool Required { get; init; }

    /// <summary>Rough transfer size, used to describe the cost of a variant before anything is fetched.</summary>
    public long ApproximateBytes { get; init; }

    public string Description { get; init; } = string.Empty;

    public override string ToString() => Name;
}
