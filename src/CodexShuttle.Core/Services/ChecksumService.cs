using System.Security.Cryptography;
using System.Text;
using CodexShuttle.Core.Models;

namespace CodexShuttle.Core.Services;

public sealed class ChecksumService
{
    public async Task SaveSha256FileAsync(
        string packageRoot,
        IEnumerable<string> includedRelativePaths,
        string checksumFilePath,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        var lines = new List<string>();
        var root = Path.GetFullPath(packageRoot);

        foreach (var relativeRoot in includedRelativePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var itemPath = Path.GetFullPath(Path.Combine(root, relativeRoot));
            if (File.Exists(itemPath))
            {
                await AddFileHashAsync(root, itemPath, lines, cancellationToken, progress);
                continue;
            }

            if (!Directory.Exists(itemPath))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(itemPath, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await AddFileHashAsync(root, file, lines, cancellationToken, progress);
            }
        }

        var tempPath = checksumFilePath + ".tmp";
        await File.WriteAllLinesAsync(tempPath, lines.OrderBy(line => line), Encoding.UTF8, cancellationToken);
        File.Move(tempPath, checksumFilePath, overwrite: true);
    }

    public async Task SaveSha256FileAsync(string rootPath, string checksumFilePath, CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();

        if (!Directory.Exists(rootPath))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Path.GetFullPath(file).Equals(Path.GetFullPath(checksumFilePath), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await using var stream = File.OpenRead(file);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            var relative = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
            lines.Add($"{Convert.ToHexString(hash).ToLowerInvariant()}  {relative}");
        }

        await File.WriteAllLinesAsync(checksumFilePath, lines.OrderBy(line => line), Encoding.UTF8, cancellationToken);
    }

    public async Task<OperationResult> VerifySha256FileAsync(
        string packageRoot,
        string checksumFilePath,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        if (!File.Exists(checksumFilePath))
        {
            return OperationResult.Fail("Checksum file is missing.", checksumFilePath);
        }

        var root = Path.GetFullPath(packageRoot);
        var checkedFiles = 0;
        foreach (var line in File.ReadLines(checksumFilePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line) || line.Length < 67 || line[64..66] != "  ")
            {
                return OperationResult.Fail("Checksum file contains an invalid line.");
            }

            var expected = line[..64];
            var relative = line[66..].Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(root, relative));
            if (!fullPath.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(fullPath))
            {
                return OperationResult.Fail("A checksummed file is missing or outside the package.", relative);
            }

            await using var stream = File.OpenRead(fullPath);
            if (stream.Length >= 100L * 1024 * 1024)
            {
                progress?.Report($"Verifying large file: {relative} ({ReportService.FormatBytes(stream.Length)})");
            }
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult.Fail("Checksum verification failed.", relative);
            }

            checkedFiles++;
            if (checkedFiles % 100 == 0)
            {
                progress?.Report($"Verified {checkedFiles:N0} Codex migration files...");
            }
        }

        return OperationResult.Ok($"Verified {checkedFiles:N0} files.");
    }

    private static async Task AddFileHashAsync(
        string packageRoot,
        string filePath,
        List<string> lines,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        await using var stream = File.OpenRead(filePath);
        var relative = Path.GetRelativePath(packageRoot, filePath).Replace('\\', '/');
        if (stream.Length >= 100L * 1024 * 1024)
        {
            progress?.Report($"Checksumming large file: {relative} ({ReportService.FormatBytes(stream.Length)})");
        }
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        lines.Add($"{Convert.ToHexString(hash).ToLowerInvariant()}  {relative}");
        if (lines.Count % 100 == 0)
        {
            progress?.Report($"Checksummed {lines.Count:N0} Codex migration files...");
        }
    }
}
