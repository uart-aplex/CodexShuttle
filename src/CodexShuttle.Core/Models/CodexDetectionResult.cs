namespace CodexShuttle.Core.Models;

public sealed class CodexDetectionResult
{
    public string UserProfile { get; set; } = string.Empty;
    public string CodexHome { get; set; } = string.Empty;
    public bool CodexHomeExists { get; set; }
    public string AgentsHome { get; set; } = string.Empty;
    public bool AgentsHomeExists { get; set; }
    public List<WorkspaceEntry> WorkspacePaths { get; set; } = new();
    public List<AppDataEntry> AppDataPaths { get; set; } = new();
}
