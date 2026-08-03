namespace CodexShuttle.Core.Services;

internal sealed class DirectoryStats
{
    public long FileCount { get; private init; }
    public long TotalBytes { get; private init; }
    public DateTimeOffset? LatestWriteTime { get; private init; }

    public static DirectoryStats Get(string path)
    {
        if (!Directory.Exists(path))
        {
            return new DirectoryStats();
        }

        long fileCount = 0;
        long totalBytes = 0;
        DateTime? latestWriteTime = null;

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(file);
                    fileCount++;
                    totalBytes += info.Length;
                    if (latestWriteTime is null || info.LastWriteTime > latestWriteTime)
                    {
                        latestWriteTime = info.LastWriteTime;
                    }
                }
                catch
                {
                    // Keep scanning even when a file is inaccessible.
                }
            }
        }
        catch
        {
            return new DirectoryStats { FileCount = fileCount, TotalBytes = totalBytes };
        }

        return new DirectoryStats
        {
            FileCount = fileCount,
            TotalBytes = totalBytes,
            LatestWriteTime = latestWriteTime is null ? null : new DateTimeOffset(latestWriteTime.Value)
        };
    }
}
