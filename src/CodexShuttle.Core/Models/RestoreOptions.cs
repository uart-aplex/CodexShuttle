namespace CodexShuttle.Core.Models;

public sealed class RestoreOptions
{
    public MigrationMode MigrationMode { get; init; } = MigrationMode.HistoryAndTools;
    public bool IncludeAppData { get; init; }
    public bool RequireOriginalWorkspacePaths { get; init; } = true;
    public Dictionary<string, string> WorkspaceTargets { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
