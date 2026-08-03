using System.Diagnostics;
using CodexShuttle.Core.Models;

namespace CodexShuttle.Core.Services;

public sealed class FileMirrorService
{
    public async Task<OperationResult> MirrorAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null,
        FileMirrorOptions? options = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(sourcePath))
        {
            return OperationResult.Fail("Source folder does not exist.", sourcePath);
        }

        Directory.CreateDirectory(destinationPath);
        progress?.Report($"Copying: {sourcePath}");
        progress?.Report($"To: {destinationPath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = "robocopy.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add(sourcePath);
        startInfo.ArgumentList.Add(destinationPath);
        startInfo.ArgumentList.Add("/MIR");
        startInfo.ArgumentList.Add("/R:1");
        startInfo.ArgumentList.Add("/W:1");
        startInfo.ArgumentList.Add("/XJ");
        startInfo.ArgumentList.Add("/FFT");
        startInfo.ArgumentList.Add("/NP");
        startInfo.ArgumentList.Add("/NDL");

        options ??= new FileMirrorOptions();
        if (options.ExcludedFiles.Count > 0)
        {
            startInfo.ArgumentList.Add("/XF");
            foreach (var pattern in options.ExcludedFiles)
            {
                startInfo.ArgumentList.Add(pattern);
            }
        }

        if (options.ExcludedDirectories.Count > 0)
        {
            startInfo.ArgumentList.Add("/XD");
            foreach (var directory in options.ExcludedDirectories)
            {
                startInfo.ArgumentList.Add(Path.Combine(sourcePath, directory));
                startInfo.ArgumentList.Add(Path.Combine(destinationPath, directory));
            }
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return OperationResult.Fail("Could not start robocopy.");
        }

        await using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Process may have exited between the cancellation check and kill request.
            }
        });

        var outputLines = new List<string>();
        var errorLines = new List<string>();
        try
        {
            var outputTask = ReadLinesAsync(process.StandardOutput, outputLines, progress, cancellationToken);
            var errorTask = ReadLinesAsync(process.StandardError, errorLines, progress, cancellationToken);
            while (!process.HasExited)
            {
                progress?.Report($"Still copying: {Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }

            await Task.WhenAll(outputTask, errorTask);
        }
        catch (OperationCanceledException)
        {
            progress?.Report("Copy canceled. Robocopy was stopped.");
            return OperationResult.Fail("Copy canceled.");
        }

        var output = string.Join(Environment.NewLine, outputLines);
        var error = string.Join(Environment.NewLine, errorLines);

        if (process.ExitCode >= 8)
        {
            return OperationResult.Fail("Robocopy failed.", output, error);
        }

        var result = OperationResult.Ok($"Robocopy completed with exit code {process.ExitCode}.");
        progress?.Report(result.Message);
        if (!string.IsNullOrWhiteSpace(error))
        {
            result.Warnings.Add(error);
        }

        return result;
    }

    public async Task<OperationResult> CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(sourcePath))
        {
            return OperationResult.Fail("Source file does not exist.", sourcePath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
        progress?.Report($"Current file: {sourcePath}");

        try
        {
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
            await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
            await source.CopyToAsync(destination, cancellationToken);
            File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourcePath));
            return OperationResult.Ok("File copied.");
        }
        catch (OperationCanceledException)
        {
            TryDelete(destinationPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(destinationPath);
            return OperationResult.Fail("File copy failed.", ex.Message);
        }
    }

    private static async Task ReadLinesAsync(
        StreamReader reader,
        List<string> lines,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var lastFileReport = DateTimeOffset.MinValue;
        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            AddBounded(lines, line);
            var trimmed = line.Trim();
            if (ShouldAlwaysShowLine(trimmed))
            {
                progress?.Report(trimmed);
                continue;
            }

            if (LooksLikeFileLine(trimmed) && DateTimeOffset.Now - lastFileReport > TimeSpan.FromSeconds(1))
            {
                progress?.Report($"Current file: {SimplifyRobocopyFileLine(trimmed)}");
                lastFileReport = DateTimeOffset.Now;
            }
        }
    }

    private static void AddBounded(List<string> lines, string line)
    {
        const int maxLines = 2_000;
        if (lines.Count == maxLines)
        {
            lines.RemoveRange(0, 500);
        }

        lines.Add(line);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Preserve the original copy error.
        }
    }

    private static bool ShouldAlwaysShowLine(string line)
    {
        return line.StartsWith("New Dir", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Extra Dir", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Ended", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Files :", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Bytes :", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Times :", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeFileLine(string line)
    {
        return line.Contains("New File", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Newer", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Older", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Changed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Extra File", StringComparison.OrdinalIgnoreCase)
            || line.Contains('\\')
            || line.Contains('/');
    }

    private static string SimplifyRobocopyFileLine(string line)
    {
        const int maxLength = 180;
        if (line.Length <= maxLength)
        {
            return line;
        }

        return "..." + line[^maxLength..];
    }
}
