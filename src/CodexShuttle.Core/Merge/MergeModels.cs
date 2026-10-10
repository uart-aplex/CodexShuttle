namespace CodexShuttle.Core.Merge;

public enum MergeAction { Add, Identical, Conflict, ConflictAlreadyPreserved, Blocked }

public sealed record MergeRequest(string PackageRoot, string ProfileRoot, string TargetUserProfile,
    string[] SelectedIds, Dictionary<string, string> WorkspaceMappings);

public sealed record MergeEntry(string Id, string Title, MergeAction Action, string Reason,
    string SourcePath, string TargetPath, string WorkingDirectory, string SourceHash, string TargetHash);

public sealed record MergePreview(MergeRequest Request, string Fingerprint, IReadOnlyList<MergeEntry> Entries,
    string SourceComputer, DateTimeOffset BackupDate)
{
    public string Summary => $"{Entries.Count(x => x.Action == MergeAction.Add)} additions, " +
        $"{Entries.Count(x => x.Action == MergeAction.Identical)} identical, " +
        $"{Entries.Count(x => x.Action is MergeAction.Conflict or MergeAction.ConflictAlreadyPreserved)} conflicts, " +
        $"{Entries.Count(x => x.Action == MergeAction.Blocked)} blocked. No workspace files or local conversations are removed.";
}

internal sealed record SessionFile(string Id, string Path, string Hash, string Cwd, string Timestamp);

internal sealed class MergeJournal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProfileRoot { get; set; } = "";
    public string Status { get; set; } = "Prepared";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public bool IndexExisted { get; set; }
    public string BeforeIndexHash { get; set; } = "";
    public string AfterIndexHash { get; set; } = "";
    public string BeforeDatabaseFingerprint { get; set; } = "";
    public string AfterDatabaseFingerprint { get; set; } = "";
    public string SnapshotHash { get; set; } = "";
    public List<MergeWrite> Writes { get; set; } = new();
    public List<string> AddedIds { get; set; } = new();
}

internal sealed record MergeWrite(string Path, string Hash);
public sealed record RestorePointInfo(string Id, string ProfileRoot, string Status, DateTimeOffset CreatedAt, string Path);
