namespace CodexShuttle.Core.Merge;

public enum MergeAction { Add, Identical, Conflict, ConflictAlreadyPreserved, Blocked, Equivalent, Update, LocalAhead }
internal enum HistoryRelation { Equal, SourceAhead, LocalAhead, Diverged }

public sealed record MergeRequest(string PackageRoot, string ProfileRoot, string TargetUserProfile,
    string[] SelectedIds, Dictionary<string, string> WorkspaceMappings)
{
    public bool IncludeMissingWorkspaceFiles { get; init; } = true;
}

public sealed record WorkspaceAddition(string Source, string Target, long Length, long ModifiedUtcTicks, string Hash);
public sealed record WorkspaceMergeSummary(string Source, string Target, int NewFiles, int NewDirectories, int RetainedFiles, int ExcludedFiles, long Bytes)
{
    public string Size => CodexShuttle.Core.Services.ReportService.FormatBytes(Bytes);
}
public sealed record WorkspaceMergePlan(IReadOnlyList<WorkspaceAddition> Files, IReadOnlyList<string> Directories, IReadOnlyList<WorkspaceMergeSummary> Roots)
{
    public static WorkspaceMergePlan Empty { get; } = new([], [], []);
    public bool HasChanges => Files.Count > 0 || Directories.Count > 0;
    public string Summary => $"Workspace files: {Files.Count:N0} missing to add ({CodexShuttle.Core.Services.ReportService.FormatBytes(Files.Sum(f => f.Length))}), {Roots.Sum(r => r.RetainedFiles):N0} existing kept; {Directories.Count:N0} missing directories.";
}

public sealed record MergeEntry(string Id, string Title, MergeAction Action, string Reason,
    string SourcePath, string TargetPath, string WorkingDirectory, string SourceHash, string TargetHash)
{
    public string HistoryHash { get; init; } = "";
    public string LocalPath { get; init; } = "";
    public string LocalHash { get; init; } = "";
}

public sealed record MergePreview(MergeRequest Request, string Fingerprint, IReadOnlyList<MergeEntry> Entries,
    string SourceComputer, DateTimeOffset BackupDate)
{
    public bool Paginated { get; init; }
    public WorkspaceMergePlan Workspaces { get; init; } = WorkspaceMergePlan.Empty;
    public string SourceDatabaseFingerprint { get; init; } = "";
    public string TargetDatabaseFingerprint { get; init; } = "";
    public string SourceHistoryFingerprint { get; init; } = "";
    public string TargetHistoryFingerprint { get; init; } = "";
    public string SelectionSummary
    {
        get
        {
            var selected = Entries.Where(x => Request.SelectedIds.Contains(x.Id)).ToArray();
            return $"Will add {selected.Count(x => x.Action == MergeAction.Add)} new conversations to Codex; " +
                $"update {selected.Count(x => x.Action == MergeAction.Update)} conversations with incoming continuations; " +
                $"save {selected.Count(x => x.Action == MergeAction.Conflict)} conflicts outside Codex; " +
                $"skip {selected.Count(x => x.Action is MergeAction.Identical or MergeAction.Equivalent or MergeAction.ConflictAlreadyPreserved or MergeAction.LocalAhead)} unchanged/local ahead; " +
                $"{selected.Count(x => x.Action == MergeAction.Blocked)} selected blocked. " +
                $"{Entries.Count(x => x.Action is (MergeAction.Add or MergeAction.Update) && !Request.SelectedIds.Contains(x.Id))} available additions/updates not selected. {Workspaces.Summary}";
        }
    }
    public string Summary => $"{Entries.Count(x => x.Action == MergeAction.Add)} additions, " +
        $"{Entries.Count(x => x.Action == MergeAction.Update)} continuations, {Entries.Count(x => x.Action == MergeAction.LocalAhead)} local ahead, " +
        $"{Entries.Count(x => x.Action == MergeAction.Identical)} identical, " +
        $"{Entries.Count(x => x.Action == MergeAction.Equivalent)} equivalent (metadata only), " +
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
    public string BeforeHistoryFingerprint { get; set; } = "";
    public string AfterHistoryFingerprint { get; set; } = "";
    public string HistorySnapshotHash { get; set; } = "";
    public List<MergeWrite> Writes { get; set; } = new();
    public List<string> AddedIds { get; set; } = new();
    public List<string> UpdatedIds { get; set; } = new();
    public List<MergeWrite> PreservedFiles { get; set; } = new();
}

internal sealed record MergeWrite(string Path, string Hash);
public sealed record RestorePointInfo(string Id, string ProfileRoot, string Status, DateTimeOffset CreatedAt, string Path);
