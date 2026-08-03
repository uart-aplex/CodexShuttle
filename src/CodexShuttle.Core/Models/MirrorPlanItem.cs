namespace CodexShuttle.Core.Models;

public sealed class MirrorPlanItem
{
    public string Name { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public string DestinationPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; } = true;
    public FileMirrorOptions MirrorOptions { get; init; } = new();
}
