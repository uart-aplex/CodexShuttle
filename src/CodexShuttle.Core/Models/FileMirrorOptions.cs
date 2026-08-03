namespace CodexShuttle.Core.Models;

public sealed class FileMirrorOptions
{
    public IReadOnlyList<string> ExcludedFiles { get; init; } = [];
    public IReadOnlyList<string> ExcludedDirectories { get; init; } = [];
}
