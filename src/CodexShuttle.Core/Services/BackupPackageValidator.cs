using CodexShuttle.Core.Models;
using System.Security.Cryptography;

namespace CodexShuttle.Core.Services;

public sealed class BackupPackageValidator
{
    private readonly PathSafetyService _pathSafety = new();

    public OperationResult Validate(string packageRoot, BackupManifest manifest, bool requireChecksums)
    {
        try
        {
            if (manifest.SchemaVersion is < 1 or > 2)
            {
                return OperationResult.Fail($"Unsupported manifest schema: {manifest.SchemaVersion}");
            }

            var inProgress = Path.Combine(packageRoot, BackupService.InProgressMarkerFileName);
            var complete = Path.Combine(packageRoot, BackupService.CompleteMarkerFileName);
            if (File.Exists(inProgress))
            {
                return OperationResult.Fail("The backup package contains an unfinished transaction.");
            }

            if (!File.Exists(complete))
            {
                return OperationResult.Fail("The backup package has no completion marker.");
            }

            if (manifest.CodexHomeExists)
            {
                var codexPath = _pathSafety.ResolvePackagePath(packageRoot, manifest.CodexPackagePath);
                if (!Directory.Exists(codexPath))
                {
                    return OperationResult.Fail("The Codex profile is missing from the package.", codexPath);
                }
            }

            foreach (var workspace in manifest.WorkspacePaths.Where(item => item.Enabled && item.Exists))
            {
                var packagePath = _pathSafety.ResolvePackagePath(packageRoot, workspace.PackagePath);
                if (!Directory.Exists(packagePath))
                {
                    return OperationResult.Fail("A workspace is missing from the package.", packagePath);
                }
            }

            if (requireChecksums && (manifest.SchemaVersion < 2
                || !File.Exists(Path.Combine(packageRoot, manifest.ChecksumFile))))
            {
                return OperationResult.Fail("This package has no current checksum file. Create a new backup before restoring.");
            }

            if (requireChecksums)
            {
                var markerValues = File.ReadLines(complete)
                    .Select(line => line.Split('=', 2))
                    .Where(parts => parts.Length == 2)
                    .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
                if (!markerValues.TryGetValue("ChecksumFileSha256", out var expectedChecksumHash))
                {
                    return OperationResult.Fail("The completion marker does not identify the checksum file.");
                }

                using var checksumStream = File.OpenRead(Path.Combine(packageRoot, manifest.ChecksumFile));
                var actualChecksumHash = Convert.ToHexString(SHA256.HashData(checksumStream)).ToLowerInvariant();
                if (!actualChecksumHash.Equals(expectedChecksumHash, StringComparison.OrdinalIgnoreCase))
                {
                    return OperationResult.Fail("The checksum file does not match the completion marker.");
                }
            }

            return OperationResult.Ok("Backup package structure is valid.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("Backup package validation failed.", ex.Message);
        }
    }
}
