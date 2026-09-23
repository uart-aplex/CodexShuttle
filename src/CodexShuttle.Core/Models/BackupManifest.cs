namespace CodexShuttle.Core.Models;

public sealed class BackupManifest
{
    public int SchemaVersion { get; set; } = 2;
    public string AppName { get; set; } = "Codex Shuttle";
    public string AppVersion { get; set; } = "0.2.7";
    public DateTimeOffset CreatedAt { get; set; }
    public string SourceComputer { get; set; } = string.Empty;
    public string SourceUser { get; set; } = string.Empty;
    public string SourceUserProfile { get; set; } = string.Empty;
    public string CodexHome { get; set; } = string.Empty;
    public string CodexPackagePath { get; set; } = ".codex";
    public bool CodexHomeExists { get; set; }
    public string AgentsHome { get; set; } = string.Empty;
    public string AgentsPackagePath { get; set; } = ".agents";
    public bool AgentsHomeExists { get; set; }
    public bool CredentialsExcluded { get; set; }
    public string BackupRoot { get; set; } = string.Empty;
    public List<WorkspaceEntry> WorkspacePaths { get; set; } = new();
    public List<AppDataEntry> AppDataPaths { get; set; } = new();
    public CodexInspectionResult CodexInspection { get; set; } = new();
    public string CopyMode { get; set; } = "mirror";
    public string ChecksumFile { get; set; } = "checksums.sha256";
}
