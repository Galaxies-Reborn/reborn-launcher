using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RebornLauncher.Core.Models;

namespace RebornLauncher.Core.Services;

public static partial class ChannelManifestService
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task<ReleaseChannel> LoadChannelAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var channel = await JsonSerializer.DeserializeAsync<ReleaseChannel>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The release channel manifest is empty.");
        var validation = Validate(channel);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(string.Join(
                Environment.NewLine,
                validation.Issues.Select(issue => $"{issue.Field}: {issue.Message}")));
        }

        return channel;
    }

    public static async Task<ReleaseChannel> LoadChannelAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return await LoadChannelAsync(stream, cancellationToken);
    }

    public static async Task<ChannelCatalog> LoadCatalogAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var catalog = await JsonSerializer.DeserializeAsync<ChannelCatalog>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The release channel catalog is empty.");
        if (catalog.SchemaVersion != 1 || catalog.Channels.Count == 0)
        {
            throw new InvalidDataException("The release channel catalog is unsupported or contains no channels.");
        }

        return catalog;
    }

    public static async Task<UmbrellaCatalog> LoadUmbrellaCatalogAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var catalog = await JsonSerializer.DeserializeAsync<UmbrellaCatalog>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The umbrella catalog is empty.");
        if (catalog.SchemaVersion != 4)
        {
            throw new InvalidDataException(
                $"Umbrella catalog schema version {catalog.SchemaVersion} is unsupported. Only version 4 is supported.");
        }

        if (catalog.Projects.Count == 0)
        {
            throw new InvalidDataException("The umbrella catalog contains no projects.");
        }

        foreach (var project in catalog.Projects)
        {
            // A project either groups variants under flavors or offers them directly. Allowing both
            // would leave the launcher without a single place to look for a project's variants.
            if (project.Flavors.Count > 0 && project.Variants.Count > 0)
            {
                throw new InvalidDataException(
                    $"Project '{project.Id}' declares both flavors and direct variants. Use one or the other.");
            }

            // An announced-but-unpublished variant does not make its owner available, so
            // availability counts only variants that can actually be installed.
            if (project.Available && !project.AllVariants.Any(variant => variant.Available))
            {
                throw new InvalidDataException(
                    $"Project '{project.Id}' is marked available but declares no available variants.");
            }

            foreach (var flavor in project.Flavors.Where(
                         flavor => flavor.Available && !flavor.Variants.Any(variant => variant.Available)))
            {
                throw new InvalidDataException(
                    $"Flavor '{flavor.Id}' is marked available but declares no available variants.");
            }

            foreach (var variant in project.AllVariants.Where(
                         variant => variant.Available && string.IsNullOrWhiteSpace(variant.Manifest)))
            {
                throw new InvalidDataException(
                    $"Variant '{variant.Id}' is marked available but declares no manifest.");
            }
        }

        return catalog;
    }

    public static async Task<UmbrellaCatalog> LoadUmbrellaCatalogAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return await LoadUmbrellaCatalogAsync(stream, cancellationToken);
    }

    public static ValidationResult Validate(ReleaseChannel channel)
    {
        var result = new ValidationResult();
        if (channel.SchemaVersion is not (1 or 4))
        {
            result.Add(nameof(channel.SchemaVersion), "Only schema versions 1 and 4 are supported.");
            return result;
        }

        ValidateIdentifier(channel.Id, nameof(channel.Id), result);
        ValidateIdentifier(channel.Branch, nameof(channel.Branch), result);

        if (string.IsNullOrWhiteSpace(channel.DisplayName))
        {
            result.Add(nameof(channel.DisplayName), "A display name is required.");
        }

        if (channel.SchemaVersion == 4)
        {
            ValidateSubmodules(channel, result);
        }
        else
        {
            ValidateRepositories(channel, result);
        }

        // Clients are not distributed. A variant either describes where to find one inside the
        // operator's own folder, or declares it has none.
        if (!channel.Clients.Supported)
        {
            return result;
        }

        ValidateRelativePath(channel.Clients.RegularExecutable, "clients.regularExecutable", result);
        ValidateRelativePath(channel.Clients.GodExecutable, "clients.godExecutable", result);
        ValidateRelativePath(channel.Clients.RegularLoginConfiguration, "clients.regularLoginConfiguration", result);
        ValidateRelativePath(channel.Clients.GodLoginConfiguration, "clients.godLoginConfiguration", result);

        return result;
    }

    private static void ValidateRepositories(ReleaseChannel channel, ValidationResult result)
    {
        if (channel.Server.Repositories.Count == 0)
        {
            result.Add("server.repositories", "At least one source repository is required.");
        }

        foreach (var repository in channel.Server.Repositories)
        {
            if (!ShaPattern().IsMatch(repository.Revision))
            {
                result.Add($"server.repositories.{repository.Name}.revision", "An exact 40-character Git revision is required.");
            }

            ValidateGitHubUrl(repository.Repository, $"server.repositories.{repository.Name}.repository", result);
            ValidateRelativePath(repository.InstallPath, $"server.repositories.{repository.Name}.installPath", result);
        }
    }

    private static void ValidateSubmodules(ReleaseChannel channel, ValidationResult result)
    {
        ValidateIdentifier(channel.Project, nameof(channel.Project), result);

        // Empty for projects that offer variants directly instead of grouping them by era.
        if (!string.IsNullOrEmpty(channel.Flavor))
        {
            ValidateIdentifier(channel.Flavor, nameof(channel.Flavor), result);
        }

        ValidateRelativePath(channel.VariantPath, nameof(channel.VariantPath), result);
        if (!string.Equals(channel.VariantPath, channel.ExpectedVariantPath, StringComparison.Ordinal))
        {
            result.Add(
                nameof(channel.VariantPath),
                $"Expected '{channel.ExpectedVariantPath}' from the project, flavor, and id.");
        }

        if (channel.Server.Submodules.Count == 0)
        {
            result.Add("server.submodules", "At least one submodule is required.");
            return;
        }

        if (!channel.Server.Submodules.Any(submodule => submodule.Required))
        {
            result.Add("server.submodules", "At least one submodule must be required.");
        }

        foreach (var submodule in channel.Server.Submodules)
        {
            var field = $"server.submodules.{submodule.Name}";
            ValidateRelativePath(submodule.Path, $"{field}.path", result);
            ValidateRelativePath(submodule.InstallPath, $"{field}.installPath", result);

            if (!string.IsNullOrEmpty(submodule.Repository))
            {
                ValidateGitHubUrl(submodule.Repository, $"{field}.repository", result);
            }

            if (!string.IsNullOrEmpty(submodule.Branch))
            {
                ValidateIdentifier(submodule.Branch, $"{field}.branch", result);
            }

            // The umbrella path must be the variant path joined with the install path, so that a
            // manifest cannot point at a submodule belonging to another flavor or variant.
            var expected = $"{channel.VariantPath}/{submodule.InstallPath}";
            if (!string.Equals(submodule.Path, expected, StringComparison.Ordinal))
            {
                result.Add($"{field}.path", $"Expected '{expected}' to match the variant path and install path.");
            }

            if (submodule.ApproximateBytes < 0)
            {
                result.Add($"{field}.approximateBytes", "A negative transfer size is not valid.");
            }
        }

        var duplicateNames = channel.Server.Submodules
            .GroupBy(submodule => submodule.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        foreach (var name in duplicateNames)
        {
            result.Add("server.submodules", $"Submodule '{name}' is declared more than once.");
        }

        var main = channel.Server.MainRepositoryPath;
        if (!channel.Server.Submodules.Any(submodule =>
                string.Equals(submodule.InstallPath, main, StringComparison.OrdinalIgnoreCase) && submodule.Required))
        {
            result.Add("server.submodules", $"The main repository '{main}' must be declared as a required submodule.");
        }
    }

    private static void ValidateGitHubUrl(string value, string field, ValidationResult result)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            result.Add(field, "A GitHub HTTPS repository URL is required.");
        }
    }

    private static void ValidateIdentifier(string value, string field, ValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(value) || !IdentifierPattern().IsMatch(value))
        {
            result.Add(field, "Use letters, numbers, periods, underscores, or hyphens.");
        }
    }

    private static void ValidateRelativePath(string value, string field, ValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
        {
            result.Add(field, "A non-empty relative path is required.");
            return;
        }

        var segments = value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            result.Add(field, "Relative traversal segments are not allowed.");
        }
    }

    [GeneratedRegex("^[a-zA-Z0-9._-]+$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^[a-fA-F0-9]{40}$")]
    private static partial Regex ShaPattern();

}
