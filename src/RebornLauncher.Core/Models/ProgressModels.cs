namespace RebornLauncher.Core.Models;

public sealed record TransferProgress(
    string Operation,
    string CurrentItem,
    long BytesCompleted,
    long TotalBytes,
    double BytesPerSecond = 0)
{
    public double Percentage => TotalBytes <= 0 ? 0 : BytesCompleted * 100d / TotalBytes;
}

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    public string CombinedOutput => string.Join(
        Environment.NewLine,
        new[] { StandardOutput, StandardError }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed record ValidationIssue(string Field, string Message);

public sealed class ValidationResult
{
    private readonly List<ValidationIssue> _issues = [];

    public IReadOnlyList<ValidationIssue> Issues => _issues;

    public bool IsValid => _issues.Count == 0;

    public void Add(string field, string message) => _issues.Add(new ValidationIssue(field, message));
}
