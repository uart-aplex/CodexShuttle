using System.Security.Cryptography;
using System.Text;

namespace CodexShuttle.Core.Services;

public sealed class PackageOperationLock : IDisposable
{
    public const string LockFileName = "codex-shuttle-operation.lock";
    private readonly string? _lockPath;
    private readonly FileStream? _stream;
    private readonly Semaphore _semaphore;

    private PackageOperationLock(string? lockPath, FileStream? stream, Semaphore semaphore)
    {
        _lockPath = lockPath;
        _stream = stream;
        _semaphore = semaphore;
    }

    public static PackageOperationLock Acquire(string packageRoot, bool usePackageFileLock = true)
    {
        var normalized = Path.GetFullPath(packageRoot).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24];
        var semaphore = new Semaphore(1, 1, $"Local\\CodexShuttle.Package.{hash}");
        if (!semaphore.WaitOne(0))
        {
            semaphore.Dispose();
            throw new IOException("This backup package is already being used by another Codex Shuttle operation.");
        }

        if (!usePackageFileLock)
        {
            return new PackageOperationLock(null, null, semaphore);
        }

        Directory.CreateDirectory(packageRoot);
        var lockPath = Path.Combine(packageRoot, LockFileName);
        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            var content = Encoding.UTF8.GetBytes($"Machine={Environment.MachineName}{Environment.NewLine}Process={Environment.ProcessId}{Environment.NewLine}StartedAt={DateTimeOffset.Now:O}");
            stream.Write(content);
            stream.Flush(flushToDisk: true);
            return new PackageOperationLock(lockPath, stream, semaphore);
        }
        catch
        {
            semaphore.Release();
            semaphore.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        if (_lockPath is not null)
        {
            try
            {
                File.Delete(_lockPath);
            }
            catch
            {
                // A stale unlocked marker is harmless and can be reused next time.
            }
        }

        _semaphore.Release();
        _semaphore.Dispose();
    }
}
