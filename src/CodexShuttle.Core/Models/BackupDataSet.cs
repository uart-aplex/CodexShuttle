namespace CodexShuttle.Core.Models;

public sealed class BackupDataSet
{
    public string Name { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public string DestinationPath { get; init; } = string.Empty;
    public FileMirrorOptions MirrorOptions { get; init; } = new();
}
