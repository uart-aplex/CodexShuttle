namespace CodexShuttle.App.Services;

public sealed class AppSettings
{
    public string BackupDestination { get; set; } = string.Empty;
    public string RestorePackage { get; set; } = string.Empty;
    public string MigrationMode { get; set; } = "HistoryAndTools";
    public List<string> WorkspacePaths { get; set; } = new();
    public string CodexHomeOverride { get; set; } = string.Empty;
}
