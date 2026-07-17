using System.Text.RegularExpressions;
using RebornLauncher.Core.Models;

namespace RebornLauncher.Core.Services;

/// <summary>
/// Wraps the Git command line. Credential prompting is disabled on every invocation so that a
/// missing or unauthorized repository fails immediately instead of blocking on a prompt that has
/// no console attached.
/// </summary>
public sealed partial class GitService(ProcessRunner processRunner, string executable)
{
    private static readonly Dictionary<string, string> NonInteractiveEnvironment = new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GCM_INTERACTIVE"] = "never",
        ["GIT_ASKPASS"] = "echo",
        ["SSH_ASKPASS"] = "echo",
    };

    /// <summary>
    /// Applied to every invocation. Nested submodule object paths such as
    /// <c>.git/modules/&lt;flavor&gt;/&lt;variant&gt;/swg-main/modules/dsrc/objects/pack/pack-&lt;sha&gt;.keep</c>
    /// exceed the 260-character Windows limit, and without this git fails to clone them.
    /// </summary>
    private static readonly string[] GlobalConfiguration = ["-c", "core.longpaths=true"];

    public string Executable { get; } = executable;

    public async Task<CommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        return await processRunner.RunAsync(
            Executable,
            [.. GlobalConfiguration, .. arguments],
            workingDirectory,
            NonInteractiveEnvironment,
            output,
            cancellationToken);
    }

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(Environment.CurrentDirectory, ["--version"], null, cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Unable to query the Git version: {result.CombinedOutput}");
        }

        return result.StandardOutput.Trim();
    }

    /// <summary>
    /// Clones the umbrella without fetching any submodule content. Submodules are materialized
    /// later, once a flavor and variant are chosen.
    /// </summary>
    public async Task CloneAsync(
        string remote,
        string destination,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(destination))
            ?? throw new ArgumentException("The destination has no parent directory.", nameof(destination));
        Directory.CreateDirectory(parent);

        var result = await RunAsync(
            parent,
            ["clone", "--progress", remote, destination],
            ReportProgress("Cloning umbrella", remote, progress),
            cancellationToken);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Unable to clone '{remote}': {result.CombinedOutput}");
        }
    }

    public async Task FetchAsync(
        string repositoryRoot,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            repositoryRoot,
            ["fetch", "--progress", "--prune", "origin"],
            ReportProgress("Updating umbrella", "origin", progress),
            cancellationToken);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Unable to fetch the umbrella: {result.CombinedOutput}");
        }
    }

    /// <summary>Initializes one submodule by path, optionally including its nested submodules.</summary>
    public async Task UpdateSubmoduleAsync(
        string repositoryRoot,
        string submodulePath,
        bool recursive,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["submodule", "update", "--init", "--progress"];
        if (recursive)
        {
            arguments.Add("--recursive");
        }

        arguments.Add("--");
        arguments.Add(submodulePath);

        var result = await RunAsync(
            repositoryRoot,
            arguments,
            ReportProgress("Fetching sources", submodulePath, progress),
            cancellationToken);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Unable to initialize submodule '{submodulePath}': {result.CombinedOutput}");
        }
    }

    public async Task<string> RevParseAsync(
        string repositoryRoot,
        string revision,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(repositoryRoot, ["rev-parse", revision], null, cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Unable to resolve '{revision}': {result.CombinedOutput}");
        }

        return result.StandardOutput.Trim();
    }

    /// <summary>Returns true when the submodule at the given path has been initialized.</summary>
    public async Task<bool> IsSubmoduleInitializedAsync(
        string repositoryRoot,
        string submodulePath,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            repositoryRoot,
            ["submodule", "status", "--", submodulePath],
            null,
            cancellationToken);

        if (!result.Succeeded)
        {
            return false;
        }

        var line = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return line is not null && !IsUninitialized(line);
    }

    /// <summary>
    /// Paths of submodules that are still uninitialized beneath the given repository.
    ///
    /// This exists because <c>git submodule update</c> exits zero even when every nested clone
    /// failed: it prints "Failed to clone ... Retry scheduled" and then reports success. The exit
    /// code therefore cannot be trusted, and the resulting state must be inspected directly.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetUninitializedSubmodulesAsync(
        string repositoryRoot,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["submodule", "status"];
        if (recursive)
        {
            arguments.Add("--recursive");
        }

        var result = await RunAsync(repositoryRoot, arguments, null, cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Unable to read submodule status in '{repositoryRoot}': {result.CombinedOutput}");
        }

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(IsUninitialized)
            .Select(ParseSubmodulePath)
            .Where(path => path.Length > 0)
            .ToList();
    }

    // git prefixes uninitialized submodules with '-' and out-of-date ones with '+'.
    private static bool IsUninitialized(string statusLine) => statusLine.TrimStart('\r').StartsWith('-');

    private static string ParseSubmodulePath(string statusLine)
    {
        // Format: "-<sha> <path>" or " <sha> <path> (<describe>)"
        var parts = statusLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : string.Empty;
    }

    private static Action<string> ReportProgress(
        string operation,
        string item,
        IProgress<TransferProgress>? progress) => line =>
    {
        if (progress is null)
        {
            return;
        }

        var match = ProgressPattern().Match(line);
        if (!match.Success)
        {
            return;
        }

        var completed = long.Parse(match.Groups["completed"].Value);
        var total = long.Parse(match.Groups["total"].Value);
        progress.Report(new TransferProgress(operation, $"{item} ({match.Groups["phase"].Value})", completed, total));
    };

    [GeneratedRegex(
        @"(?<phase>Counting objects|Compressing objects|Receiving objects|Resolving deltas):\s+\d+%\s+\((?<completed>\d+)/(?<total>\d+)\)")]
    private static partial Regex ProgressPattern();
}
