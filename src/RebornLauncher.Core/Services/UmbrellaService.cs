using RebornLauncher.Core.Models;

namespace RebornLauncher.Core.Services;

/// <summary>
/// Manages the umbrella working tree that backs an instance.
///
/// Cloning the umbrella transfers only manifests and submodule pointers. No source is fetched
/// until <see cref="MaterializeVariantAsync"/> is called for a specific variant, so selecting one
/// flavor never downloads another's content.
/// </summary>
/// <summary>
/// What an instance would gain by updating. <paramref name="StaleSubmodules"/> covers sources left
/// behind even when the umbrella itself is current.
/// </summary>
public sealed record UpdateStatus(string LocalRevision, string RemoteRevision, IReadOnlyList<string> StaleSubmodules)
{
    public bool UmbrellaBehind => !string.Equals(LocalRevision, RemoteRevision, StringComparison.Ordinal);

    public bool UpdateAvailable => UmbrellaBehind || StaleSubmodules.Count > 0;

    public string Describe() => !UpdateAvailable
        ? "The server is up to date."
        : UmbrellaBehind
            ? $"A newer release is available ({RemoteRevision[..7]}). The server will be rebuilt."
            : $"These sources are behind: {string.Join(", ", StaleSubmodules)}. The server will be rebuilt.";
}

public sealed class UmbrellaService(GitService git)
{
    public const string DefaultRemote = "https://github.com/Galaxies-Reborn/galaxies-reborn.git";

    public const string CatalogRelativePath = "channels/index.json";

    /// <summary>Clones the umbrella if it is absent. Fetches no submodule content.</summary>
    public async Task EnsureClonedAsync(
        string remote,
        string umbrellaRoot,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(Path.Combine(umbrellaRoot, ".git")))
        {
            return;
        }

        if (Directory.Exists(umbrellaRoot) && Directory.EnumerateFileSystemEntries(umbrellaRoot).Any())
        {
            throw new InvalidOperationException(
                $"'{umbrellaRoot}' already exists and is not an umbrella clone. Choose a fresh instance folder.");
        }

        await git.CloneAsync(remote, umbrellaRoot, progress, cancellationToken);
    }

    /// <summary>Fetches umbrella metadata and fast-forwards it. Submodule content is untouched.</summary>
    public async Task UpdateAsync(
        string umbrellaRoot,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await git.FetchAsync(umbrellaRoot, progress, cancellationToken);
        var result = await git.RunAsync(umbrellaRoot, ["merge", "--ff-only", "FETCH_HEAD"], null, cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"The umbrella could not be fast-forwarded, which usually means it carries local commits: {result.CombinedOutput}");
        }
    }

    /// <summary>
    /// Whether an instance's sources are behind what the umbrella publishes. Fetches metadata only,
    /// so asking is cheap and downloads no source.
    /// </summary>
    public async Task<UpdateStatus> CheckForUpdateAsync(
        string umbrellaRoot,
        ReleaseChannel channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);

        await git.FetchAsync(umbrellaRoot, null, cancellationToken);
        var local = await git.RevParseAsync(umbrellaRoot, "HEAD", cancellationToken);
        var remote = await git.RevParseAsync(umbrellaRoot, "FETCH_HEAD", cancellationToken);

        // The umbrella can be current while the instance's submodules are not: a prepare that was
        // cancelled, or sources moved by hand, leave them behind without the umbrella noticing.
        //
        // Only fetched-but-stale counts. A submodule that was never fetched is absent by choice,
        // such as an optional one nobody asked for, and reporting it would offer an update that
        // installs something the operator declined.
        var stale = new List<string>();
        foreach (var submodule in GetPendingSubmodules(channel, includeOptional: true))
        {
            if (await git.GetSubmoduleStateAsync(umbrellaRoot, submodule.Path, cancellationToken)
                == SubmoduleState.Stale)
            {
                stale.Add(submodule.Name);
            }
        }

        return new UpdateStatus(local, remote, stale);
    }

    public async Task<UmbrellaCatalog> LoadCatalogAsync(
        string umbrellaRoot,
        CancellationToken cancellationToken = default)
    {
        var path = SafeCombine(umbrellaRoot, CatalogRelativePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The umbrella catalog is missing at '{path}'.", path);
        }

        return await ChannelManifestService.LoadUmbrellaCatalogAsync(path, cancellationToken);
    }

    public async Task<ReleaseChannel> LoadVariantAsync(
        string umbrellaRoot,
        VariantReference variant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(variant);
        var path = SafeCombine(umbrellaRoot, variant.Manifest);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The manifest for '{variant.Id}' is missing at '{path}'.", path);
        }

        return await ChannelManifestService.LoadChannelAsync(path, cancellationToken);
    }

    /// <summary>Root of a variant's sources inside the umbrella working tree.</summary>
    public static string ResolveVariantRoot(string umbrellaRoot, ReleaseChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return SafeCombine(umbrellaRoot, channel.VariantPath);
    }

    /// <summary>
    /// Submodules that a prepare would fetch. Lets the launcher state the transfer cost before
    /// anything is downloaded.
    /// </summary>
    public static IReadOnlyList<SubmoduleSpec> GetPendingSubmodules(ReleaseChannel channel, bool includeOptional)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return channel.Server.Submodules
            .Where(submodule => submodule.Required || includeOptional)
            .ToList();
    }

    public static long GetApproximateBytes(ReleaseChannel channel, bool includeOptional) =>
        GetPendingSubmodules(channel, includeOptional).Sum(submodule => submodule.ApproximateBytes);

    /// <summary>
    /// Fetches the selected variant's submodules. This is the first point at which any source is
    /// transferred.
    /// </summary>
    public async Task MaterializeVariantAsync(
        string umbrellaRoot,
        ReleaseChannel channel,
        bool includeOptional = false,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (channel.SchemaVersion != 4)
        {
            throw new InvalidOperationException(
                $"Variant '{channel.Id}' uses schema version {channel.SchemaVersion} and is not backed by the umbrella.");
        }

        foreach (var submodule in GetPendingSubmodules(channel, includeOptional))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Guard against a manifest pointing outside the umbrella before handing the path to git.
            SafeCombine(umbrellaRoot, submodule.Path);

            // Skipped only when it already sits at the recorded revision. After the umbrella
            // advances, a submodule is still initialized but stale, and must be moved.
            if (await git.IsSubmoduleCurrentAsync(umbrellaRoot, submodule.Path, cancellationToken))
            {
                progress?.Report(new TransferProgress("Fetching sources", $"{submodule.Name} is up to date.", 1, 1));
                continue;
            }

            await git.UpdateSubmoduleAsync(
                umbrellaRoot,
                submodule.Path,
                submodule.Recursive,
                progress,
                cancellationToken);

            await VerifyAsync(umbrellaRoot, submodule, cancellationToken);
        }
    }

    /// <summary>
    /// Confirms a submodule really arrived. <c>git submodule update</c> exits zero even when its
    /// clones failed, so the fetched state is inspected rather than the exit code.
    /// </summary>
    private async Task VerifyAsync(
        string umbrellaRoot,
        SubmoduleSpec submodule,
        CancellationToken cancellationToken)
    {
        if (!await git.IsSubmoduleCurrentAsync(umbrellaRoot, submodule.Path, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Git reported success but '{submodule.Name}' is not at its pinned revision. " +
                "Check the network connection and rerun.");
        }

        if (!submodule.Recursive)
        {
            return;
        }

        var root = SafeCombine(umbrellaRoot, submodule.Path);
        var missing = await git.GetUninitializedSubmodulesAsync(root, recursive: true, cancellationToken);
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"'{submodule.Name}' fetched, but these nested repositories did not: {string.Join(", ", missing)}. " +
                "Check the network connection and rerun.");
        }
    }

    private static string SafeCombine(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var combined = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Path '{relativePath}' escapes the umbrella root.");
        }

        return combined;
    }
}
