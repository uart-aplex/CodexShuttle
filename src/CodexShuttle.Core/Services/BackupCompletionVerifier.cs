using System.Security.Cryptography;

namespace CodexShuttle.Core.Services;

public sealed class BackupCompletionVerifier
{
    public async Task VerifyAsync(string root, string expectedRunId, CancellationToken token = default,
        IProgress<string>? progress = null)
    {
        var manifest = await new ManifestService().LoadAsync(Path.Combine(root, "manifest.json"), token);
        var values = File.ReadLines(Path.Combine(root, BackupService.CompleteMarkerFileName))
            .Select(line => line.Split('=', 2)).Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(expectedRunId) || manifest?.BackupRunId != expectedRunId
            || !values.TryGetValue("BackupRunId", out var markerRunId) || markerRunId != expectedRunId)
        {
            throw new InvalidDataException("The saved backup does not belong to this operation. Success cannot be confirmed.");
        }

        var checksumPath = new PathSafetyService().ResolvePackagePath(root, manifest.ChecksumFile);
        await using (var stream = File.OpenRead(checksumPath))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
            if (!values.TryGetValue("ChecksumFileSha256", out var expectedHash)
                || !hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The completion marker does not match the saved checksum list.");
            }
        }

        var result = await new ChecksumService().VerifySha256FileAsync(root, checksumPath, token, progress);
        if (!result.Success)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, new[] { result.Message }.Concat(result.Errors)));
        }
    }
}
