using System.Diagnostics;
using System.Text;
using RebornLauncher.Core.Models;

namespace RebornLauncher.Core.Services;

public sealed class ProcessRunner
{
    /// <summary>
    /// Starts a process and returns without waiting. Used for the game clients, which outlive the
    /// call and whose output the launcher does not consume.
    /// </summary>
    public void Start(string executable, IEnumerable<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new FileNotFoundException($"Unable to start '{executable}'.", executable, exception);
        }
    }

    public async Task<CommandResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Unable to start {executable}.");
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new FileNotFoundException($"The command '{executable}' is not installed or is not on PATH.", executable, exception);
        }

        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var outputTask = PumpAsync(process.StandardOutput, standardOutput, output, cancellationToken);
        var errorTask = PumpAsync(process.StandardError, standardError, output, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        return new CommandResult(process.ExitCode, standardOutput.ToString(), standardError.ToString());
    }

    private static async Task PumpAsync(
        StreamReader reader,
        StringBuilder destination,
        Action<string>? output,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            destination.AppendLine(line);
            output?.Invoke(line);
        }
    }
}
