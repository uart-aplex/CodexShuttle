namespace CodexShuttle.Core.Models;

public sealed class WorkspaceEntry
{
    public string SourcePath { get; set; } = string.Empty;
    public string PackagePath { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool Exists { get; set; }
    public long FileCount { get; set; }
    public long TotalBytes { get; set; }
    public DateTimeOffset? LatestWriteTime { get; set; }
}
