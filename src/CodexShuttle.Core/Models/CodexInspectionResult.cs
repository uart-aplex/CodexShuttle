namespace CodexShuttle.Core.Models;

public sealed class CodexInspectionResult
{
    public bool CodexHomeExists { get; set; }
    public bool SessionsFolderExists { get; set; }
    public int SessionsFileCount { get; set; }
    public bool ArchivedSessionsFolderExists { get; set; }
    public int ArchivedSessionsFileCount { get; set; }
    public DateTimeOffset? LatestSessionWriteTime { get; set; }
    public bool StateSqliteExists { get; set; }
    public bool StateSqliteReadable { get; set; }
    public bool SessionIndexExists { get; set; }
    public bool SessionIndexReadable { get; set; }
    public bool GlobalStateExists { get; set; }
    public bool GlobalStateJsonValid { get; set; }
    public bool ConfigTomlExists { get; set; }
    public List<string> Warnings { get; set; } = new();
}
