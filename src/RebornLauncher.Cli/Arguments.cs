namespace RebornLauncher.Cli;

/// <summary>Minimal --key value parser. Kept dependency free so the CLI stays a single small binary.</summary>
internal sealed class Arguments
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    private Arguments(string command) => Command = command;

    public string Command { get; }

    public static Arguments Parse(IReadOnlyList<string> args)
    {
        var parsed = new Arguments(args.Count > 0 && !args[0].StartsWith('-') ? args[0] : "help");

        for (var index = parsed.Command == "help" ? 0 : 1; index < args.Count; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = argument[2..];
            var next = index + 1 < args.Count ? args[index + 1] : null;
            if (next is null || next.StartsWith("--", StringComparison.Ordinal))
            {
                parsed._flags.Add(key);
                continue;
            }

            parsed._values[key] = next;
            index++;
        }

        return parsed;
    }

    public bool Has(string flag) => _flags.Contains(flag) || _values.ContainsKey(flag);

    public string? Value(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public string Require(string key) => Value(key)
        ?? throw new ArgumentException($"--{key} is required. Run 'reborn help' for usage.");

    public int Int(string key, int fallback) =>
        Value(key) is { } raw && int.TryParse(raw, out var value) ? value : fallback;
}
