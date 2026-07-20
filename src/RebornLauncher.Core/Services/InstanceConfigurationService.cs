using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RebornLauncher.Core.Models;

namespace RebornLauncher.Core.Services;

public static partial class InstanceConfigurationService
{
    public static ValidationResult Validate(InstanceSettings settings)
    {
        var result = new ValidationResult();
        if (string.IsNullOrWhiteSpace(settings.InstallRoot) || !Path.IsPathFullyQualified(settings.InstallRoot))
        {
            result.Add(nameof(settings.InstallRoot), "Choose an absolute installation folder.");
        }

        if (string.IsNullOrWhiteSpace(settings.InstanceName))
        {
            result.Add(nameof(settings.InstanceName), "Enter a name for this instance.");
        }

        if (!ClusterNamePattern().IsMatch(settings.ClusterName))
        {
            result.Add(nameof(settings.ClusterName), "Use 1-24 letters, numbers, or underscores, beginning with a letter.");
        }

        if (string.IsNullOrWhiteSpace(settings.PublicAddress) ||
            settings.PublicAddress.Length > 253 ||
            !PublicAddressPattern().IsMatch(settings.PublicAddress))
        {
            result.Add(nameof(settings.PublicAddress), "Enter an IP address or DNS name using letters, numbers, dots, colons, underscores, or hyphens.");
        }

        if (settings.LoginPort is < 1 or > 65535)
        {
            result.Add(nameof(settings.LoginPort), "The login port must be between 1 and 65535.");
        }

        return result;
    }

    public static string GeneratePassword(int bytes = 18)
    {
        var buffer = RandomNumberGenerator.GetBytes(bytes);
        return Convert.ToHexString(buffer).ToLowerInvariant();
    }

    public static async Task SaveAsync(
        InstanceSettings settings,
        ReleaseChannel channel,
        CancellationToken cancellationToken = default)
    {
        var validation = Validate(settings);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(string.Join(
                Environment.NewLine,
                validation.Issues.Select(issue => $"{issue.Field}: {issue.Message}")));
        }

        var paths = new LauncherPaths(settings, channel);
        Directory.CreateDirectory(paths.StateRoot);
        Directory.CreateDirectory(paths.MainRepositoryRoot);

        settings.DatabasePassword = string.IsNullOrWhiteSpace(settings.DatabasePassword)
            ? GeneratePassword()
            : settings.DatabasePassword;
        settings.DatabaseAdminPassword = string.IsNullOrWhiteSpace(settings.DatabaseAdminPassword)
            ? GeneratePassword()
            : settings.DatabaseAdminPassword;
        settings.UpdatedAt = DateTimeOffset.UtcNow;

        await WriteAtomicAsync(
            paths.SettingsPath,
            JsonSerializer.Serialize(settings, ChannelManifestService.JsonOptions),
            cancellationToken);
        await WriteAtomicAsync(paths.EnvironmentPath, RenderEnvironment(settings), cancellationToken);
        await WriteAtomicAsync(paths.ComposeOverridePath, RenderComposeOverride(), cancellationToken);
    }

    public static async Task<InstanceSettings?> LoadAsync(
        string settingsPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(settingsPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(settingsPath);
        var settings = await JsonSerializer.DeserializeAsync<InstanceSettings>(
            stream,
            ChannelManifestService.JsonOptions,
            cancellationToken);
        if (settings is null)
        {
            return null;
        }

        var environmentPath = FindEnvironmentPath(settingsPath, settings);
        if (File.Exists(environmentPath))
        {
            var values = ParseEnvironment(await File.ReadAllLinesAsync(environmentPath, cancellationToken));
            values.TryGetValue("SWG_DB_PASSWORD", out var databasePassword);
            values.TryGetValue("SWG_DB_ADMIN_PASSWORD", out var adminPassword);
            settings.DatabasePassword = databasePassword ?? string.Empty;
            settings.DatabaseAdminPassword = adminPassword ?? string.Empty;
        }

        return settings;
    }

    private static string FindEnvironmentPath(string settingsPath, InstanceSettings settings)
    {
        // Schema-v1 instances used this fixed location. Keep it as the fast path and for backward
        // compatibility.
        var legacyPath = Path.Combine(
            settings.InstallRoot,
            settings.ChannelId,
            "server",
            "swg-main",
            ".env.reborn");
        if (File.Exists(legacyPath))
        {
            return legacyPath;
        }

        // Umbrella-backed variants keep swg-main beneath their catalog-defined variant path, which
        // is intentionally not duplicated in instance.json. Locate the generated environment file
        // within this instance instead of guessing that evolving path.
        var stateRoot = Path.GetDirectoryName(Path.GetFullPath(settingsPath));
        var instanceRoot = stateRoot is null ? null : Path.GetDirectoryName(stateRoot);
        if (instanceRoot is null || !Directory.Exists(instanceRoot))
        {
            return legacyPath;
        }

        return Directory.EnumerateFiles(instanceRoot, ".env.reborn", SearchOption.AllDirectories)
            .FirstOrDefault() ?? legacyPath;
    }

    public static IReadOnlyDictionary<string, string> BuildProcessEnvironment(InstanceSettings settings) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SWG_DB_USER"] = "swg",
            ["SWG_DB_PASSWORD"] = settings.DatabasePassword,
            ["SWG_DB_ADMIN_USER"] = "system",
            ["SWG_DB_ADMIN_PASSWORD"] = settings.DatabaseAdminPassword,
            ["SWG_CLUSTER_NAME"] = settings.ClusterName,
            ["SWG_PUBLIC_ADDRESS"] = settings.PublicAddress,
        };

    public static string RenderEnvironment(InstanceSettings settings)
    {
        var values = BuildProcessEnvironment(settings);
        var builder = new StringBuilder();
        builder.AppendLine("# Generated by Reborn Launcher. Keep this file private.");
        foreach (var pair in values)
        {
            builder.Append(pair.Key).Append('=').AppendLine(pair.Value);
        }

        return builder.ToString();
    }

    public static string RenderComposeOverride() =>
        """
        # Generated by Reborn Launcher. Instance values are read from .env.reborn.
        services:
          oracle:
            environment:
              ORACLE_PASSWORD: ${SWG_DB_ADMIN_PASSWORD}
              APP_USER: ${SWG_DB_USER}
              APP_USER_PASSWORD: ${SWG_DB_PASSWORD}

          swg-server:
            environment:
              SWG_DB_USER: ${SWG_DB_USER}
              SWG_DB_PASSWORD: ${SWG_DB_PASSWORD}
              SWG_DB_ADMIN_USER: ${SWG_DB_ADMIN_USER}
              SWG_DB_ADMIN_PASSWORD: ${SWG_DB_ADMIN_PASSWORD}
              SWG_CLUSTER_NAME: ${SWG_CLUSTER_NAME}
              SWG_PUBLIC_ADDRESS: ${SWG_PUBLIC_ADDRESS}
        """ + Environment.NewLine;

    private static Dictionary<string, string> ParseEnvironment(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                values[line[..separator]] = line[(separator + 1)..];
            }
        }

        return values;
    }

    private static async Task WriteAtomicAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".partial";
        await File.WriteAllTextAsync(temporaryPath, content, new UTF8Encoding(false), cancellationToken);
        File.Move(temporaryPath, path, true);
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,23}$")]
    private static partial Regex ClusterNamePattern();

    [GeneratedRegex("^[A-Za-z0-9._:-]+$")]
    private static partial Regex PublicAddressPattern();
}
