using System.Text;
using System.Text.RegularExpressions;

namespace RebornLauncher.Core.Services;

public static partial class ClientConfigurationService
{
    public static async Task UpdateLoginAsync(
        string configurationPath,
        string address,
        int port,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(configurationPath))
        {
            throw new FileNotFoundException("The client login configuration was not found.", configurationPath);
        }

        if (string.IsNullOrWhiteSpace(address) || address.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("A valid host name or IP address is required.", nameof(address));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        var lines = (await File.ReadAllLinesAsync(configurationPath, cancellationToken)).ToList();
        SetValue(lines, "ClientGame", "loginServerAddress0", address);
        SetValue(lines, "ClientGame", "loginServerPort0", port.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var temporaryPath = configurationPath + ".partial";
        await File.WriteAllLinesAsync(temporaryPath, lines, new UTF8Encoding(false), cancellationToken);
        File.Move(temporaryPath, configurationPath, true);
    }

    public static async Task UpdateGodLoginAsync(
        string configurationPath,
        string address,
        int port,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(configurationPath))
        {
            throw new FileNotFoundException("The God client configuration was not found.", configurationPath);
        }

        if (string.IsNullOrWhiteSpace(address) || address.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("A valid host name or IP address is required.", nameof(address));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        var lines = (await File.ReadAllLinesAsync(configurationPath, cancellationToken)).ToList();
        SetValue(lines, "ClientGame", "loginServerAddress", address);
        SetValue(lines, "ClientGame", "loginServerPort", port.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var temporaryPath = configurationPath + ".partial";
        await File.WriteAllLinesAsync(temporaryPath, lines, new UTF8Encoding(false), cancellationToken);
        File.Move(temporaryPath, configurationPath, true);
    }

    private static void SetValue(List<string> lines, string section, string key, string value)
    {
        var matcher = ConfigurationKeyPattern();
        for (var index = 0; index < lines.Count; index++)
        {
            var match = matcher.Match(lines[index]);
            if (match.Success && string.Equals(match.Groups["key"].Value, key, StringComparison.OrdinalIgnoreCase))
            {
                lines[index] = $"{key}={value}";
                return;
            }
        }

        var sectionHeader = $"[{section}]";
        var sectionIndex = lines.FindIndex(line =>
            string.Equals(line.Trim(), sectionHeader, StringComparison.OrdinalIgnoreCase));
        if (sectionIndex < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add(sectionHeader);
            lines.Add($"\t{key}={value}");
            return;
        }

        var insertIndex = sectionIndex + 1;
        while (insertIndex < lines.Count && !SectionHeaderPattern().IsMatch(lines[insertIndex]))
        {
            insertIndex++;
        }

        lines.Insert(insertIndex, $"\t{key}={value}");
    }

    [GeneratedRegex("^\\s*(?<key>[A-Za-z0-9_]+)\\s*=")]
    private static partial Regex ConfigurationKeyPattern();

    [GeneratedRegex("^\\s*\\[[^]]+\\]\\s*$")]
    private static partial Regex SectionHeaderPattern();
}
