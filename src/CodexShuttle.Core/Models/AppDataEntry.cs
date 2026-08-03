namespace CodexShuttle.Core.Models;

public sealed class AppDataEntry
{
    public string SourcePath { get; set; } = string.Empty;
    public string PackagePath { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public bool Included { get; set; }
    public long FileCount { get; set; }
    public long TotalBytes { get; set; }
}
