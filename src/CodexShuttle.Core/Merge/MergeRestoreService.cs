using System.Text;
using System.Text.Json;
using CodexShuttle.Core.Models;
using CodexShuttle.Core.Services;

namespace CodexShuttle.Core.Merge;

public sealed class MergeRestoreService
{
    private readonly string _store;
    private readonly Func<bool> _codexRunning;
    private readonly Action<string>? _checkpoint;
    public MergeRestoreService() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexShuttle", "merge"), () => new CodexProcessService().IsCodexRunning()) { }
    internal MergeRestoreService(string store, Func<bool> codexRunning, Action<string>? checkpoint = null)
    { _store = MergeFiles.Canonical(store); _codexRunning = codexRunning; _checkpoint = checkpoint; }

    private string ProfileKey(string home) => MergeFiles.Hash(Encoding.UTF8.GetBytes(MergeFiles.Canonical(home).ToUpperInvariant()));
    private string ConflictPath(string home, string id, string hash) => Path.Combine(_store, "conflicts", ProfileKey(home), id, hash + ".jsonl");

    private void ValidateRoots(MergeRequest request)
    {
        foreach (var path in new[] { request.PackageRoot, request.ProfileRoot, _store }) MergeFiles.NoLinks(path);
        new PathSafetyService().ValidateMirrorPair(request.PackageRoot, request.ProfileRoot);
        foreach (var root in new[] { request.ProfileRoot, request.PackageRoot })
            if (MergeFiles.Canonical(root) == _store || MergeFiles.Within(root, _store) || MergeFiles.Within(_store, root))
                throw new InvalidDataException("Restore points must be outside both the backup and Codex profile.");
        if (!Path.IsPathFullyQualified(request.TargetUserProfile)) throw new InvalidDataException("An absolute destination user profile is required.");
        foreach (var pair in request.WorkspaceMappings)
            if (!Path.IsPathFullyQualified(pair.Key) || !Path.IsPathFullyQualified(pair.Value)) throw new InvalidDataException("Workspace mappings must use absolute paths.");
    }

    private async Task<BackupManifest> ValidatePackage(string package, CancellationToken token, IProgress<string>? progress)
    {
        var manifest = await new ManifestService().LoadAsync(Path.Combine(package, "manifest.json"), token)
            ?? throw new InvalidDataException("Backup manifest is missing.");
        var checksum = new PathSafetyService().ResolvePackagePath(package, manifest.ChecksumFile);
        MergeFiles.NoLinks(checksum);
        var validation = new BackupPackageValidator().Validate(package, manifest, true);
        if (!validation.Success) throw new InvalidDataException(validation.Message);
        foreach (var line in File.ReadLines(checksum))
        {
            if (line.Length < 67) throw new InvalidDataException("Invalid checksum list.");
            MergeFiles.NoLinks(new PathSafetyService().ResolvePackagePath(package, line[66..].Replace('/', Path.DirectorySeparatorChar)));
        }
        var verified = await new ChecksumService().VerifySha256FileAsync(package, checksum, token, progress);
        if (!verified.Success) throw new InvalidDataException(verified.Message + " " + string.Join("; ", verified.Errors));
        return manifest;
    }

    private static Dictionary<string, string> Index(string profile)
    {
        var path = Path.Combine(profile, "session_index.jsonl");
        MergeFiles.NoLinks(path);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var id = doc.RootElement.GetProperty("id").GetString() ?? throw new InvalidDataException("Index ID is missing.");
            result[id] = line;
        }
        return result;
    }

    public async Task<MergePreview> PreviewAsync(MergeRequest request, CancellationToken token = default, IProgress<string>? progress = null)
    {
        // Clone mutable inputs so a UI edit cannot change a previously approved plan.
        request = request with { PackageRoot = MergeFiles.Canonical(request.PackageRoot), ProfileRoot = MergeFiles.Canonical(request.ProfileRoot),
            SelectedIds = request.SelectedIds.Distinct().Order().ToArray(), WorkspaceMappings = new(request.WorkspaceMappings, StringComparer.OrdinalIgnoreCase) };
        ValidateRoots(request);
        progress?.Report("Verifying package and inventorying source and destination conversations...");
        var manifest = await ValidatePackage(request.PackageRoot, token, progress);
        var sourceHome = new PathSafetyService().ResolvePackagePath(request.PackageRoot, manifest.CodexPackagePath);
        foreach (var home in new[] { sourceHome, request.ProfileRoot })
        {
            if (Directory.EnumerateFiles(home, "state_*.sqlite").Any(p => !Path.GetFileName(p).Equals("state_5.sqlite", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Multiple or unknown primary database generations require a reviewed merge adapter.");
            if (MergeFiles.Files(Path.Combine(home, "sqlite")).Any(p => Path.GetFileName(p).StartsWith("state_", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("This profile has a nested SQLite index. Merge is blocked until that Desktop layout has a verified adapter; do not remove the index to bypass this check.");
        }
        var source = MergeFiles.Inventory(sourceHome, token);
        var local = MergeFiles.Inventory(request.ProfileRoot, token);
        var checksummed = File.ReadLines(Path.Combine(request.PackageRoot, manifest.ChecksumFile))
            .Select(line => Path.GetFullPath(Path.Combine(request.PackageRoot, line[66..].Replace('/', Path.DirectorySeparatorChar))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in source.Values)
            if (!checksummed.Contains(file.Path)) throw new InvalidDataException($"Unverified session not listed in package checksums: {file.Path}");
        foreach (var name in new[] { "state_5.sqlite", "state_5.sqlite-wal", "session_index.jsonl" })
        {
            var path = Path.Combine(sourceHome, name);
            if (File.Exists(path) && !checksummed.Contains(path)) throw new InvalidDataException($"Unverified metadata: {path}");
        }
        using var sourceDb = MergeDatabase.Open(Path.Combine(sourceHome, "state_5.sqlite"));
        using var targetDb = MergeDatabase.Open(Path.Combine(request.ProfileRoot, "state_5.sqlite"));
        var sourceRows = MergeDatabase.Rows(sourceDb);
        var localRows = MergeDatabase.Rows(targetDb);
        var localIndex = Index(request.ProfileRoot);
        var items = new List<MergeEntry>();
        foreach (var session in source.Values.OrderBy(x => x.Id))
        {
            token.ThrowIfCancellationRequested();
            var mapped = MergeFiles.MapSession(MergeFiles.Read(session.Path), request, manifest.SourceUserProfile);
            var hash = MergeFiles.Hash(mapped);
            var cwd = MergeFiles.MapCwd(session.Cwd, request, manifest.SourceUserProfile);
            var path = Path.Combine(request.ProfileRoot, Path.GetRelativePath(sourceHome, session.Path));
            var action = MergeAction.Add;
            var reason = "New conversation; insert only.";
            if (local.TryGetValue(session.Id, out var existing))
            {
                action = existing.Hash == hash ? MergeAction.Identical : MergeAction.Conflict;
                reason = action == MergeAction.Identical ? "Identical conversation; no write." : "Same ID, different contents; local version remains unchanged.";
                path = existing.Path;
                if (action == MergeAction.Identical && (!localRows.TryGetValue(session.Id, out var localRow)
                    || !localRow.TryGetValue("rollout_path", out var rollout) || !File.Exists(Convert.ToString(rollout))))
                { action = MergeAction.Blocked; reason = "Existing transcript has missing or invalid local SQLite metadata; manual repair required."; }
                if (action == MergeAction.Conflict)
                {
                    path = ConflictPath(request.ProfileRoot, session.Id, session.Hash);
                    if (File.Exists(path))
                    {
                        MergeFiles.NoLinks(path);
                        if (MergeFiles.FileHash(path) != session.Hash) throw new InvalidDataException("A retained conflict object is corrupted.");
                        action = MergeAction.ConflictAlreadyPreserved;
                        reason = "This conflict revision is already preserved; no write.";
                    }
                }
            }
            else if (localRows.ContainsKey(session.Id) || localIndex.ContainsKey(session.Id) || File.Exists(path))
            { action = MergeAction.Blocked; reason = "Destination has an existing ID or occupied path without a matching inventoried session."; }
            var title = sourceRows.TryGetValue(session.Id, out var row) && row.TryGetValue("title", out var titleValue) ? Convert.ToString(titleValue) ?? "" : "";
            var entry = new MergeEntry(session.Id, title, action, reason, session.Path, path, cwd, session.Hash, hash);
            if (action == MergeAction.Add)
            {
                try
                {
                    if (row is null) throw new InvalidDataException("No supported SQLite metadata for this session.");
                    if (!Path.IsPathFullyQualified(cwd) || !Directory.Exists(cwd)) throw new InvalidDataException("Working directory is unavailable; add an explicit workspace path mapping.");
                    MergeFiles.NoLinks(path);
                    _ = MergeDatabase.PrepareRow(targetDb, row, entry);
                }
                catch (Exception ex) { entry = entry with { Action = MergeAction.Blocked, Reason = ex.Message }; }
            }
            items.Add(entry);
        }
        if (request.SelectedIds.Except(source.Keys).Any()) throw new InvalidDataException("Selection contains an unknown session ID.");
        var indexPath = Path.Combine(request.ProfileRoot, "session_index.jsonl");
        var fingerprint = MergeFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Request = request, Manifest = MergeFiles.FileHash(Path.Combine(request.PackageRoot, "manifest.json")),
            Checksums = MergeFiles.FileHash(Path.Combine(request.PackageRoot, manifest.ChecksumFile)),
            SourceDatabase = MergeDatabase.Fingerprint(sourceDb), TargetDatabase = MergeDatabase.Fingerprint(targetDb),
            Local = local.Values.OrderBy(x => x.Id), Index = File.Exists(indexPath) ? MergeFiles.FileHash(indexPath) : "absent", Items = items
        }));
        return new(request, fingerprint, items, manifest.SourceComputer, manifest.CreatedAt);
    }

    public IReadOnlyList<RestorePointInfo> RestorePoints() =>
        MergeFiles.Files(Path.Combine(_store, "restore-points")).Where(path => Path.GetFileName(path) == "operation.json")
            .Select(path => (Path: path, Journal: JsonSerializer.Deserialize<MergeJournal>(File.ReadAllText(path))!))
            .Select(x => new RestorePointInfo(x.Journal.Id, x.Journal.ProfileRoot, x.Journal.Status, x.Journal.CreatedAt, Path.GetDirectoryName(x.Path)!)).ToArray();

    public async Task<OperationResult> ApplyAsync(MergePreview approved, CancellationToken token = default, IProgress<string>? progress = null)
    {
        MergeJournal? journal = null;
        string? point = null;
        try
        {
            if (_codexRunning()) throw new InvalidOperationException("Close Codex Desktop, CLI and IDE sessions before merge.");
            ValidateRoots(approved.Request);
            using var profileLock = PackageOperationLock.Acquire(approved.Request.ProfileRoot, false);
            using var packageLock = PackageOperationLock.Acquire(approved.Request.PackageRoot, false);
            if (RestorePoints().Any(p => p.ProfileRoot.Equals(approved.Request.ProfileRoot, StringComparison.OrdinalIgnoreCase) && p.Status == "Applying"))
                throw new InvalidOperationException("An interrupted merge needs recovery in Restore Point Manager first.");
            var plan = await PreviewAsync(approved.Request, token, progress);
            if (plan.Fingerprint != approved.Fingerprint) throw new InvalidOperationException("Source, destination or mappings changed. Run a new Dry Run.");
            var selected = plan.Entries.Where(x => plan.Request.SelectedIds.Contains(x.Id)).ToArray();
            if (selected.Any(x => x.Action == MergeAction.Blocked)) throw new InvalidOperationException("A selected session is blocked. Resolve it before importing.");
            var writes = selected.Where(x => x.Action is MergeAction.Add or MergeAction.Conflict).ToArray();
            if (writes.Length == 0) return OperationResult.Ok("No changes: selected conversations/revisions are already present or no sessions were selected.");
            var manifest = (await new ManifestService().LoadAsync(Path.Combine(plan.Request.PackageRoot, "manifest.json"), token))!;
            var sourceHome = new PathSafetyService().ResolvePackagePath(plan.Request.PackageRoot, manifest.CodexPackagePath);
            using var sourceDb = MergeDatabase.Open(Path.Combine(sourceHome, "state_5.sqlite"));
            var sourceRows = MergeDatabase.Rows(sourceDb);
            using var database = MergeDatabase.Open(Path.Combine(plan.Request.ProfileRoot, "state_5.sqlite"), true);
            var indexPath = Path.Combine(plan.Request.ProfileRoot, "session_index.jsonl");
            var beforeIndex = File.Exists(indexPath) ? File.ReadAllBytes(indexPath) : [];
            var sourceIndex = Index(sourceHome);
            var additions = writes.Where(x => x.Action == MergeAction.Add).ToArray();
            var indexText = new StringBuilder(new UTF8Encoding(false, true).GetString(beforeIndex));
            if (additions.Length > 0 && indexText.Length > 0 && indexText[^1] != '\n') indexText.Append('\n');
            foreach (var item in additions)
                indexText.AppendLine(sourceIndex.TryGetValue(item.Id, out var line) ? line : JsonSerializer.Serialize(new { id = item.Id, thread_name = item.Title, updated_at = plan.BackupDate }));
            var afterIndex = additions.Length == 0 ? beforeIndex : Encoding.UTF8.GetBytes(indexText.ToString());
            journal = new MergeJournal { ProfileRoot = plan.Request.ProfileRoot, IndexExisted = File.Exists(indexPath),
                BeforeIndexHash = MergeFiles.Hash(beforeIndex), AfterIndexHash = MergeFiles.Hash(afterIndex),
                BeforeDatabaseFingerprint = MergeDatabase.Fingerprint(database), AddedIds = additions.Select(x => x.Id).ToList() };
            point = Path.Combine(_store, "restore-points", journal.Id);
            Directory.CreateDirectory(point);
            progress?.Report($"Creating local verified restore point: {point}");
            var snapshot = Path.Combine(point, "state.sqlite");
            MergeDatabase.Snapshot(database, snapshot);
            journal.SnapshotHash = MergeFiles.FileHash(snapshot);
            using (var saved = MergeDatabase.Open(snapshot))
                if (MergeDatabase.Fingerprint(saved) != journal.BeforeDatabaseFingerprint) throw new InvalidDataException("Database changed during snapshot.");
            MergeFiles.AtomicWrite(Path.Combine(point, "session_index.before"), beforeIndex, false);
            if (MergeFiles.FileHash(Path.Combine(point, "session_index.before")) != journal.BeforeIndexHash) throw new InvalidDataException("Index snapshot verification failed.");
            // Persist every intended file before any destination write for crash recovery.
            foreach (var item in writes)
            {
                journal.Writes.Add(new(item.TargetPath, item.Action == MergeAction.Add ? item.TargetHash : item.SourceHash));
                if (item.Action == MergeAction.Conflict)
                {
                    var metadata = ConflictMetadata(item, plan);
                    journal.Writes.Add(new(item.TargetPath + ".json", MergeFiles.Hash(metadata)));
                }
            }
            foreach (var write in journal.Writes)
            {
                MergeFiles.NoLinks(write.Path);
                if (File.Exists(write.Path) || Directory.Exists(write.Path)) throw new IOException($"Import path is already occupied: {write.Path}");
            }
            using (var transaction = database.BeginTransaction(deferred: false))
            {
                if (_codexRunning() || MergeDatabase.Fingerprint(database, transaction) != journal.BeforeDatabaseFingerprint)
                    throw new InvalidOperationException("Codex started or destination metadata changed before merge.");
                journal.Status = "Applying";
                MergeFiles.SaveJournal(point, journal);
                _checkpoint?.Invoke("SnapshotVerified");
                foreach (var item in writes)
                {
                    token.ThrowIfCancellationRequested();
                    var original = MergeFiles.Read(item.SourcePath);
                    if (MergeFiles.Hash(original) != item.SourceHash) throw new InvalidDataException("Source session changed after preview.");
                    var bytes = item.Action == MergeAction.Add ? MergeFiles.MapSession(original, plan.Request, manifest.SourceUserProfile) : original;
                    if (MergeFiles.Hash(bytes) != (item.Action == MergeAction.Add ? item.TargetHash : item.SourceHash)) throw new InvalidDataException("Mapped session changed after preview.");
                    progress?.Report($"{item.Action}: {item.Id} -> {item.TargetPath}");
                    WriteOwned(item.TargetPath, bytes, point, journal);
                    if (item.Action == MergeAction.Add) MergeDatabase.Insert(database, transaction, MergeDatabase.PrepareRow(database, sourceRows[item.Id], item, transaction));
                    else WriteOwned(item.TargetPath + ".json", ConflictMetadata(item, plan), point, journal);
                    _checkpoint?.Invoke("SessionWritten");
                }
                if ((File.Exists(indexPath) ? MergeFiles.FileHash(indexPath) : MergeFiles.Hash([])) != journal.BeforeIndexHash)
                    throw new InvalidOperationException("Destination session index changed during merge.");
                if (additions.Length > 0) MergeFiles.AtomicWrite(indexPath, afterIndex, true);
                journal.AfterDatabaseFingerprint = MergeDatabase.Fingerprint(database, transaction);
                MergeFiles.SaveJournal(point, journal);
                _checkpoint?.Invoke("BeforeCommit");
                token.ThrowIfCancellationRequested();
                transaction.Commit();
            }
            _checkpoint?.Invoke("AfterCommit");
            journal.Status = "Completed";
            MergeFiles.SaveJournal(point, journal);
            return OperationResult.Ok($"Merge completed: {additions.Length} imported, {writes.Length - additions.Length} conflicting revisions preserved. Restore point: {point}. Existing history/settings/workspaces unchanged.");
        }
        catch (Exception ex)
        {
            if (point is not null && journal?.Status == "Applying")
            {
                try { Rollback(point); }
                catch (Exception recovery) { return OperationResult.Fail($"Merge stopped; recovery required: {point}. No automatic sync is enabled.", ex.Message, recovery.Message); }
            }
            return OperationResult.Fail("Merge did not complete; no existing conversation was overwritten. " + ex.Message, ex.Message);
        }
    }

    private static byte[] ConflictMetadata(MergeEntry entry, MergePreview plan) => JsonSerializer.SerializeToUtf8Bytes(new
    { SessionId = entry.Id, SourceDevice = plan.SourceComputer, SourceRevision = entry.SourceHash, LocalMappedRevision = entry.TargetHash,
        BackupDate = plan.BackupDate, OriginalPath = entry.SourcePath, Reason = entry.Reason }, MergeFiles.Json);

    private static void WriteOwned(string path, byte[] bytes, string point, MergeJournal journal)
    {
        try { MergeFiles.AtomicWrite(path, bytes, false); }
        catch
        {
            // A failed create must never authorize rollback to delete a colliding existing file.
            journal.Writes.RemoveAll(write => write.Path == path);
            MergeFiles.SaveJournal(point, journal);
            throw;
        }
    }

    public OperationResult Recover(string restorePoint)
    {
        try
        {
            if (!MergeFiles.Within(Path.Combine(_store, "restore-points"), restorePoint)) throw new InvalidDataException("Unknown restore point location.");
            MergeFiles.NoLinks(restorePoint);
            var journal = JsonSerializer.Deserialize<MergeJournal>(File.ReadAllText(Path.Combine(restorePoint, "operation.json")))!;
            using var profileLock = PackageOperationLock.Acquire(journal.ProfileRoot, false);
            Rollback(restorePoint);
            return OperationResult.Ok("Merge changes were rolled back; original metadata and index verified.");
        }
        catch (Exception ex) { return OperationResult.Fail("Rollback refused or incomplete. Existing data was not force-replaced.", ex.Message); }
    }

    private void Rollback(string point)
    {
        if (_codexRunning()) throw new InvalidOperationException("Close Codex before rollback.");
        if (!MergeFiles.Within(Path.Combine(_store, "restore-points"), point)) throw new InvalidDataException("Unknown restore point location.");
        MergeFiles.NoLinks(point);
        var journal = JsonSerializer.Deserialize<MergeJournal>(File.ReadAllText(Path.Combine(point, "operation.json")))!;
        if (journal.Status == "RolledBack") return;
        if (journal.Status is not ("Applying" or "Completed")) throw new InvalidOperationException("This restore point did not modify the profile.");
        var snapshot = Path.Combine(point, "state.sqlite");
        var originalIndex = File.ReadAllBytes(Path.Combine(point, "session_index.before"));
        if (MergeFiles.FileHash(snapshot) != journal.SnapshotHash || MergeFiles.Hash(originalIndex) != journal.BeforeIndexHash)
            throw new InvalidDataException("Restore point verification failed.");
        using var database = MergeDatabase.Open(Path.Combine(journal.ProfileRoot, "state_5.sqlite"), true);
        using var transaction = database.BeginTransaction(deferred: false);
        var fingerprint = MergeDatabase.Fingerprint(database, transaction);
        if (fingerprint != journal.BeforeDatabaseFingerprint && fingerprint != journal.AfterDatabaseFingerprint)
            throw new InvalidOperationException("Metadata changed after import. Refusing to remove newer local work.");
        var indexPath = Path.Combine(journal.ProfileRoot, "session_index.jsonl");
        MergeFiles.NoLinks(indexPath);
        var indexHash = File.Exists(indexPath) ? MergeFiles.FileHash(indexPath) : MergeFiles.Hash([]);
        if (indexHash != journal.BeforeIndexHash && indexHash != journal.AfterIndexHash) throw new InvalidOperationException("Session index changed after import.");
        foreach (var file in journal.Writes)
        {
            if (!MergeFiles.Within(Path.Combine(journal.ProfileRoot, "sessions"), file.Path)
                && !MergeFiles.Within(Path.Combine(journal.ProfileRoot, "archived_sessions"), file.Path)
                && !MergeFiles.Within(Path.Combine(_store, "conflicts", ProfileKey(journal.ProfileRoot)), file.Path))
                throw new InvalidDataException("Unsafe rollback file path.");
            MergeFiles.NoLinks(file.Path);
            if (File.Exists(file.Path) && MergeFiles.FileHash(file.Path) != file.Hash) throw new InvalidOperationException("Imported file changed after import; rollback needs manual review.");
        }
        if (fingerprint == journal.AfterDatabaseFingerprint && fingerprint != journal.BeforeDatabaseFingerprint)
            MergeDatabase.DeleteInserted(database, transaction, journal.AddedIds);
        if (MergeDatabase.Fingerprint(database, transaction) != journal.BeforeDatabaseFingerprint) throw new InvalidDataException("Rollback did not reproduce original metadata.");
        if (indexHash != journal.BeforeIndexHash)
        {
            if (journal.IndexExisted) MergeFiles.AtomicWrite(indexPath, originalIndex, true);
            else File.Delete(indexPath);
        }
        foreach (var file in journal.Writes) if (File.Exists(file.Path)) File.Delete(file.Path);
        transaction.Commit();
        journal.Status = "RolledBack";
        MergeFiles.SaveJournal(point, journal);
    }
}
