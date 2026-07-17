using RebornLauncher.Core.Models;

namespace RebornLauncher.Core.Services;

/// <summary>
/// Fetches a vendor installer and hands it to the operator to run.
///
/// It deliberately stops at launching the installer rather than driving it silently: installing
/// Docker changes the machine, and that approval belongs to the person at the keyboard. Downloads
/// come only from the vendor endpoints in <see cref="PrerequisiteService"/>, never from a manifest.
/// </summary>
public sealed class PrerequisiteInstaller(ProcessRunner processRunner)
{
    private readonly HttpClient _httpClient = new();

    public async Task<string> DownloadAsync(
        Prerequisite prerequisite,
        string cacheDirectory,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prerequisite);
        if (prerequisite.InstallerUri is not { } uri)
        {
            throw new InvalidOperationException($"{prerequisite.DisplayName} has no installer to download.");
        }

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"Refusing to download {prerequisite.DisplayName} over '{uri}'.");
        }

        Directory.CreateDirectory(cacheDirectory);
        var fileName = Uri.UnescapeDataString(parsed.Segments[^1]);
        var destination = Path.Combine(cacheDirectory, fileName);
        var partial = destination + ".partial";

        using var response = await _httpClient.GetAsync(parsed, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;

        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = File.Create(partial))
        {
            var buffer = new byte[1024 * 256];
            long completed = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                completed += read;
                progress?.Report(new TransferProgress(
                    $"Downloading {prerequisite.DisplayName}",
                    fileName,
                    completed,
                    total));
            }
        }

        File.Move(partial, destination, true);
        return destination;
    }

    /// <summary>
    /// Starts the downloaded installer. The operator completes it, including any elevation prompt.
    /// </summary>
    public void Launch(string installerPath)
    {
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException("The installer is no longer present.", installerPath);
        }

        processRunner.Start(installerPath, [], Path.GetDirectoryName(installerPath)!);
    }

    /// <summary>Opens a page for prerequisites that have no direct installer, such as Docker on Linux.</summary>
    public void OpenDocumentation(Prerequisite prerequisite)
    {
        ArgumentNullException.ThrowIfNull(prerequisite);
        if (prerequisite.InstallerUri is { } uri)
        {
            processRunner.Start(uri, [], Environment.CurrentDirectory);
        }
    }
}
