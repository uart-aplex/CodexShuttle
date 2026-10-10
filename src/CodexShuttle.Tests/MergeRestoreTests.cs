using System.Text;
using System.Text.Json;
using CodexShuttle.Core.Merge;
using CodexShuttle.Core.Models;
using CodexShuttle.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class MergeRestoreTests
{
    [TestMethod]
    public async Task DisjointImport_PreservesPersonalHistorySettingsAndWorkspace_AndIsIdempotent()
    {
        using var fixture = new MergeFixture();
        var incoming = fixture.AddSession(fixture.Source, "office");
        fixture.AddSession(fixture.Target, "personal");
        foreach (var name in new[] { "config.toml", "auth.json", "skills/private/SKILL.md", ".codex-global-state.json" })
            fixture.Write(fixture.Target, name, "personal-only");
        fixture.Write(fixture.Workspace, "private.txt", "do not change");
        var originals = fixture.HashTree(fixture.Target);
        await fixture.Package();
        var service = fixture.Service();
        var preview = await service.PreviewAsync(fixture.Request(incoming));
        Assert.AreEqual(MergeAction.Add, preview.Entries.Single().Action);
        var result = await service.ApplyAsync(preview);
        Assert.IsTrue(result.Success, result.Message);
        foreach (var pair in originals.Where(x => !x.Key.Contains("state_5.sqlite") && x.Key != "session_index.jsonl"))
            Assert.AreEqual(pair.Value, MergeFiles.FileHash(Path.Combine(fixture.Target, pair.Key)), pair.Key);
        Assert.AreEqual("do not change", File.ReadAllText(Path.Combine(fixture.Workspace, "private.txt")));
        using (var db = MergeDatabase.Open(Path.Combine(fixture.Target, "state_5.sqlite"))) Assert.AreEqual(2, MergeDatabase.Rows(db).Count);
        var again = await service.PreviewAsync(fixture.Request(incoming));
        Assert.AreEqual(MergeAction.Identical, again.Entries.Single().Action);
        Assert.IsTrue((await service.ApplyAsync(again)).Success);
        Assert.AreEqual(1, service.RestorePoints().Count);
    }

    [DataTestMethod]
    [DataRow("older source")]
    [DataRow("divergent source")]
    [DataRow("newer source extension")]
    public async Task SameIdDifferentHistory_IsPreservedWithoutOverwriting(string incomingText)
    {
        using var f = new MergeFixture();
        var id = f.AddSession(f.Target, "new local personal turn");
        f.AddSession(f.Source, incomingText, id);
        await f.Package();
        var original = f.HashTree(f.Target);
        var service = f.Service();
        var plan = await service.PreviewAsync(f.Request(id));
        Assert.AreEqual(MergeAction.Conflict, plan.Entries.Single().Action);
        Assert.IsTrue((await service.ApplyAsync(plan)).Success);
        CollectionAssert.AreEquivalent(original.ToArray(), f.HashTree(f.Target).ToArray());
        Assert.AreEqual(MergeAction.ConflictAlreadyPreserved, (await service.PreviewAsync(f.Request(id))).Entries.Single().Action);
        Assert.IsTrue((await service.ApplyAsync(await service.PreviewAsync(f.Request(id)))).Success);
        Assert.AreEqual(1, service.RestorePoints().Count);
    }

    [DataTestMethod]
    [DataRow("SnapshotVerified")]
    [DataRow("SessionWritten")]
    [DataRow("BeforeCommit")]
    [DataRow("AfterCommit")]
    public async Task InjectedFailure_RollbackRecoversOriginalLogicalDatabaseAndFiles(string stage)
    {
        using var f = new MergeFixture();
        var id = f.AddSession(f.Source, "office");
        f.AddSession(f.Target, "personal");
        await f.Package();
        string before;
        using (var db = MergeDatabase.Open(Path.Combine(f.Target, "state_5.sqlite"))) before = MergeDatabase.Fingerprint(db);
        var index = File.ReadAllBytes(Path.Combine(f.Target, "session_index.jsonl"));
        var service = f.Service(checkpoint => { if (checkpoint == stage) throw new IOException("Injected failure"); });
        var result = await service.ApplyAsync(await service.PreviewAsync(f.Request(id)));
        Assert.IsFalse(result.Success);
        using (var db = MergeDatabase.Open(Path.Combine(f.Target, "state_5.sqlite"))) Assert.AreEqual(before, MergeDatabase.Fingerprint(db));
        CollectionAssert.AreEqual(index, File.ReadAllBytes(Path.Combine(f.Target, "session_index.jsonl")));
        Assert.IsFalse(File.Exists(f.SessionPath(f.Target, id)));
        Assert.AreEqual("RolledBack", service.RestorePoints().Single().Status);
    }

    [TestMethod]
    public async Task RecoveryAfterCommitAndInterruption_IsIdempotent()
    {
        using var f = new MergeFixture();
        var id = f.AddSession(f.Source, "office");
        await f.Package();
        var service = f.Service();
        Assert.IsTrue((await service.ApplyAsync(await service.PreviewAsync(f.Request(id)))).Success);
        var point = service.RestorePoints().Single();
        var journalPath = Path.Combine(point.Path, "operation.json");
        var journal = JsonSerializer.Deserialize<MergeJournal>(File.ReadAllText(journalPath))!;
        journal.Status = "Applying"; // Simulate a crash after SQL commit, before terminal journal write.
        MergeFiles.SaveJournal(point.Path, journal);
        Assert.IsTrue(service.Recover(point.Path).Success);
        Assert.IsTrue(service.Recover(point.Path).Success);
        Assert.IsFalse(File.Exists(f.SessionPath(f.Target, id)));
    }

    [TestMethod]
    public async Task RollbackRefusesNewLocalEdits()
    {
        using var f = new MergeFixture();
        var id = f.AddSession(f.Source, "office");
        await f.Package();
        var service = f.Service();
        Assert.IsTrue((await service.ApplyAsync(await service.PreviewAsync(f.Request(id)))).Success);
        File.AppendAllText(f.SessionPath(f.Target, id), "\nnewer local data");
        var before = File.ReadAllText(f.SessionPath(f.Target, id));
        Assert.IsFalse(service.Recover(service.RestorePoints().Single().Path).Success);
        Assert.AreEqual(before, File.ReadAllText(f.SessionPath(f.Target, id)));
    }

    [TestMethod]
    public async Task ChangedPreviewAndCorruptPackage_FailBeforeModification()
    {
        using var f = new MergeFixture();
        var id = f.AddSession(f.Source, "office");
        await f.Package();
        var service = f.Service();
        var plan = await service.PreviewAsync(f.Request(id));
        f.AddSession(f.Target, "created after preview");
        Assert.IsFalse((await service.ApplyAsync(plan)).Success);
        Assert.AreEqual(0, service.RestorePoints().Count);
        File.AppendAllText(f.SessionPath(f.Source, id), "corrupt");
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => service.PreviewAsync(f.Request(id)));
    }

    [TestMethod]
    public async Task AccountAndWorkspaceMapping_OnlyChangesIncomingStructuredCwd()
    {
        using var f = new MergeFixture();
        var id = f.AddSession(f.Source, @"literal reference C:\Users\OfficeUser\notes.txt", cwd: @"Z:\OfficeWork");
        await f.Package();
        var request = f.Request(id) with { WorkspaceMappings = new() { [@"Z:\OfficeWork"] = f.Workspace } };
        var service = f.Service();
        Assert.IsTrue((await service.ApplyAsync(await service.PreviewAsync(request))).Success);
        var imported = File.ReadAllText(f.SessionPath(f.Target, id));
        StringAssert.Contains(imported, @"C:\\Users\\OfficeUser\\notes.txt");
        using var meta = JsonDocument.Parse(imported.Split('\n')[0]);
        Assert.AreEqual(f.Workspace, meta.RootElement.GetProperty("payload").GetProperty("cwd").GetString());
        Assert.AreEqual(MergeAction.Identical, (await service.PreviewAsync(request)).Entries.Single().Action);
        var userRequest = f.Request() with { TargetUserProfile = f.Root };
        Assert.AreEqual(Path.Combine(f.Root, "notes"), MergeFiles.MapCwd(@"C:\Users\OfficeUser\notes", userRequest, @"C:\Users\OfficeUser"));
    }

    [TestMethod]
    public async Task ExplicitSelection_PreservesLocalOnlyAndLeavesOtherSourceSessionsOut()
    {
        using var f = new MergeFixture();
        var included = f.AddSession(f.Source, "included");
        var excluded = f.AddSession(f.Source, "private office");
        await f.Package();
        var service = f.Service();
        Assert.IsTrue((await service.ApplyAsync(await service.PreviewAsync(f.Request()))).Success);
        Assert.AreEqual(0, service.RestorePoints().Count);
        Assert.IsTrue((await service.ApplyAsync(await service.PreviewAsync(f.Request(included)))).Success);
        Assert.IsFalse(File.Exists(f.SessionPath(f.Target, excluded)));
    }

    [TestMethod]
    public async Task DuplicateIdsAndUnlistedSessionsAreRejected()
    {
        using var f = new MergeFixture();
        var id = f.AddSession(f.Source, "office");
        await f.Package();
        File.Copy(f.SessionPath(f.Source, id), Path.Combine(Path.GetDirectoryName(f.SessionPath(f.Source, id))!, "duplicate.jsonl"));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => f.Service().PreviewAsync(f.Request(id)));
    }

    [TestMethod]
    public async Task UnlistedUniqueTranscript_IsRejected()
    {
        using var f = new MergeFixture();
        var id = f.AddSession(f.Source, "office");
        await f.Package();
        var newPath = f.SessionPath(f.Source, Guid.NewGuid().ToString());
        var original = File.ReadAllText(f.SessionPath(f.Source, id));
        File.WriteAllText(newPath, original.Replace(id, Guid.NewGuid().ToString()));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => f.Service().PreviewAsync(f.Request(id)));
    }

    [DataTestMethod]
    [DataRow("CREATE TRIGGER unsafe AFTER INSERT ON threads BEGIN DELETE FROM unrelated; END")]
    [DataRow("ALTER TABLE threads ADD COLUMN unsupported TEXT NOT NULL")]
    public async Task UnknownSchemaOrTriggers_BlockImport(string sql)
    {
        using var f = new MergeFixture();
        var id = f.AddSession(f.Source, "office");
        using (var db = MergeDatabase.Open(Path.Combine(f.Target, "state_5.sqlite"), true))
        using (var command = db.CreateCommand()) { command.CommandText = sql; command.ExecuteNonQuery(); }
        await f.Package();
        var service = f.Service();
        var plan = await service.PreviewAsync(f.Request(id));
        Assert.AreEqual(MergeAction.Blocked, plan.Entries.Single().Action);
        Assert.IsFalse((await service.ApplyAsync(plan)).Success);
        Assert.AreEqual(0, service.RestorePoints().Count);
    }

    [TestMethod]
    public async Task RunningCodex_BlocksBeforeSnapshotOrWrites()
    {
        using var f = new MergeFixture();
        var id = f.AddSession(f.Source, "office");
        await f.Package();
        var service = new MergeRestoreService(Path.Combine(f.Root, "private-store"), () => true);
        Assert.IsFalse((await service.ApplyAsync(await service.PreviewAsync(f.Request(id)))).Success);
        Assert.AreEqual(0, service.RestorePoints().Count);
        Assert.IsFalse(File.Exists(f.SessionPath(f.Target, id)));
    }

    [TestMethod]
    public async Task CorruptRestorePoint_RefusesRecoveryWithoutChangingData()
    {
        using var f = new MergeFixture();
        var id = f.AddSession(f.Source, "office");
        await f.Package();
        var service = f.Service();
        Assert.IsTrue((await service.ApplyAsync(await service.PreviewAsync(f.Request(id)))).Success);
        var point = service.RestorePoints().Single();
        File.AppendAllText(Path.Combine(point.Path, "state.sqlite"), "corruption");
        var before = f.HashTree(f.Target);
        Assert.IsFalse(service.Recover(point.Path).Success);
        CollectionAssert.AreEquivalent(before.ToArray(), f.HashTree(f.Target).ToArray());
    }

    [TestMethod]
    public async Task Rollback_PreservesNewerMetadata_AndHandlesOriginallyMissingIndex()
    {
        using var f = new MergeFixture();
        var id = f.AddSession(f.Source, "office");
        await f.Package();
        var service = f.Service();
        Assert.IsTrue((await service.ApplyAsync(await service.PreviewAsync(f.Request(id)))).Success);
        Assert.IsTrue(service.Recover(service.RestorePoints().Single().Path).Success);
        Assert.IsFalse(File.Exists(Path.Combine(f.Target, "session_index.jsonl")));
        Assert.IsTrue((await service.ApplyAsync(await service.PreviewAsync(f.Request(id)))).Success);
        var latest = service.RestorePoints().Single(p => p.Status == "Completed");
        var local = f.AddSession(f.Target, "new personal history");
        Assert.IsFalse(service.Recover(latest.Path).Success);
        Assert.IsTrue(File.Exists(f.SessionPath(f.Target, local)));
    }

    [TestMethod]
    public async Task RelocatedUsbPackage_UsesSelectedRoot_NotManifestSourcePaths()
    {
        using var f = new MergeFixture();
        var id = f.AddSession(f.Source, "office");
        await f.Package();
        var moved = Path.Combine(f.Root, "other-usb-mount");
        Directory.Move(f.PackageRoot, moved);
        var request = f.Request(id) with { PackageRoot = moved };
        var service = f.Service();
        Assert.IsTrue((await service.ApplyAsync(await service.PreviewAsync(request))).Success);
        using var db = MergeDatabase.Open(Path.Combine(f.Target, "state_5.sqlite"));
        Assert.AreEqual(f.SessionPath(f.Target, id), Convert.ToString(MergeDatabase.Rows(db)[id]["rollout_path"]));
    }

    [TestMethod]
    public async Task OccupiedConflictMetadata_IsNeverDeletedByFailedImport()
    {
        using var f = new MergeFixture();
        var id = f.AddSession(f.Source, "office");
        f.AddSession(f.Target, "home", id);
        await f.Package();
        var service = f.Service();
        var plan = await service.PreviewAsync(f.Request(id));
        var occupied = plan.Entries.Single().TargetPath + ".json";
        Directory.CreateDirectory(Path.GetDirectoryName(occupied)!);
        File.WriteAllText(occupied, "existing file");
        Assert.IsFalse((await service.ApplyAsync(plan)).Success);
        Assert.AreEqual("existing file", File.ReadAllText(occupied));
        Assert.IsFalse(File.Exists(plan.Entries.Single().TargetPath));
    }

    [DataTestMethod]
    [DataRow(@"\\?\C:\Users\OfficeUser\notes")]
    [DataRow("C:/Users/OfficeUser/notes")]
    public void UserRootMapping_HandlesExtendedAndForwardSlashPaths(string path)
    {
        using var f = new MergeFixture();
        Assert.AreEqual(Path.Combine(f.Root, "home", "notes"), MergeFiles.MapCwd(path, f.Request(), @"C:\Users\OfficeUser"));
    }

    [TestMethod]
    public async Task NestedDesktopIndex_BlocksRatherThanIgnoringAnotherDatabase()
    {
        using var f = new MergeFixture();
        var id = f.AddSession(f.Source, "office");
        f.Write(f.Target, "sqlite/state_5.sqlite", "independent index, never delete");
        await f.Package();
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => f.Service().PreviewAsync(f.Request(id)));
        Assert.AreEqual("independent index, never delete", File.ReadAllText(Path.Combine(f.Target, "sqlite/state_5.sqlite")));
    }

    [TestMethod]
    public async Task InterruptedPackage_BlocksBeforeImport()
    {
        using var f = new MergeFixture();
        var id = f.AddSession(f.Source, "office");
        await f.Package();
        f.Write(f.PackageRoot, BackupService.InProgressMarkerFileName, "interrupted transfer");
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => f.Service().PreviewAsync(f.Request(id)));
        Assert.IsFalse(File.Exists(f.SessionPath(f.Target, id)));
    }

    [TestMethod]
    public void SqliteSnapshot_IncludesCommittedWalState()
    {
        using var f = new MergeFixture();
        using var database = MergeDatabase.Open(Path.Combine(f.Target, "state_5.sqlite"), true);
        using var command = database.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; INSERT INTO unrelated VALUES('committed WAL content');";
        command.ExecuteNonQuery();
        Assert.IsTrue(File.Exists(Path.Combine(f.Target, "state_5.sqlite-wal")));
        var path = Path.Combine(f.Root, "wal-snapshot.sqlite");
        MergeDatabase.Snapshot(database, path);
        using var snapshot = MergeDatabase.Open(path);
        Assert.AreEqual(MergeDatabase.Fingerprint(database), MergeDatabase.Fingerprint(snapshot));
    }
}

internal sealed class MergeFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "CodexShuttleMergeTests", Guid.NewGuid().ToString("N"));
    public string PackageRoot => Path.Combine(Root, "package");
    public string Source => Path.Combine(PackageRoot, ".codex");
    public string Target => Path.Combine(Root, "home", ".codex");
    public string Workspace => Path.Combine(Root, "workspace");
    public MergeFixture()
    {
        Directory.CreateDirectory(Workspace);
        foreach (var home in new[] { Source, Target })
        {
            Directory.CreateDirectory(home);
            using var db = new SqliteConnection($"Data Source={Path.Combine(home, "state_5.sqlite")};Pooling=False");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TABLE threads(id TEXT PRIMARY KEY, rollout_path TEXT NOT NULL, cwd TEXT NOT NULL, title TEXT NOT NULL, model_provider TEXT NOT NULL DEFAULT 'openai', created_at INTEGER NOT NULL DEFAULT 1, updated_at INTEGER NOT NULL DEFAULT 1); CREATE TABLE unrelated(value TEXT); INSERT INTO unrelated VALUES('personal metadata');";
            cmd.ExecuteNonQuery();
        }
    }
    public string SessionPath(string home, string id) => Path.Combine(home, "sessions", "2026", "10", "10", $"rollout-2026-10-10T08-00-00-{id}.jsonl");
    public string AddSession(string home, string message, string? id = null, string? cwd = null)
    {
        id ??= Guid.NewGuid().ToString();
        cwd ??= Workspace;
        var path = SessionPath(home, id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = new[] {
            JsonSerializer.Serialize(new { timestamp = "2026-10-10T00:00:00Z", type = "session_meta", payload = new { id, timestamp = "2026-10-10T00:00:00Z", cwd, originator = "codex_cli_rs", cli_version = "0.162.0-alpha.2", source = "cli", model_provider = "openai" } }),
            JsonSerializer.Serialize(new { timestamp = "2026-10-10T00:00:01Z", type = "event_msg", payload = new { type = "user_message", message, images = Array.Empty<string>(), local_images = Array.Empty<string>(), text_elements = Array.Empty<string>() } }),
            JsonSerializer.Serialize(new { timestamp = "2026-10-10T00:00:01Z", type = "response_item", payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text = message } } } }) };
        File.WriteAllText(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
        using var db = MergeDatabase.Open(Path.Combine(home, "state_5.sqlite"), true);
        using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO threads(id,rollout_path,cwd,title) VALUES($id,$path,$cwd,$title)";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$cwd", cwd); command.Parameters.AddWithValue("$title", message);
        command.ExecuteNonQuery();
        File.AppendAllText(Path.Combine(home, "session_index.jsonl"), JsonSerializer.Serialize(new { id, thread_name = message, updated_at = "2026-10-10T00:00:01Z" }) + "\n");
        return id;
    }
    public void Write(string root, string name, string value)
    { var path = Path.Combine(root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); }
    public async Task Package()
    {
        var manifest = new BackupManifest { SourceComputer = "OFFICE", SourceUserProfile = @"C:\Users\OfficeUser", CodexHomeExists = true, CreatedAt = DateTimeOffset.Parse("2026-10-10T08:00:00+08:00") };
        await new ManifestService().SaveAsync(manifest, Path.Combine(PackageRoot, "manifest.json"));
        await new ChecksumService().SaveSha256FileAsync(PackageRoot, new[] { ".codex", "manifest.json" }, Path.Combine(PackageRoot, "checksums.sha256"));
        File.WriteAllText(Path.Combine(PackageRoot, BackupService.CompleteMarkerFileName), "ChecksumFileSha256=" + MergeFiles.FileHash(Path.Combine(PackageRoot, "checksums.sha256")));
    }
    public MergeRestoreService Service(Action<string>? checkpoint = null) => new(Path.Combine(Root, "private-store"), () => false, checkpoint);
    public MergeRequest Request(params string[] ids) => new(PackageRoot, Target, Path.Combine(Root, "home"), ids, new());
    public Dictionary<string, string> HashTree(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(root, p), MergeFiles.FileHash);
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}
