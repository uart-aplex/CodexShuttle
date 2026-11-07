using CodexShuttle.Core.Merge;
using CodexShuttle.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class PaginatedMergeTests
{
    internal static void Initialize(MergeFixture f)
    {
        foreach (var home in new[] { f.Source, f.Target })
        {
            using (var db = MergeDatabase.Open(Path.Combine(home, "state_5.sqlite"), true))
                Execute(db, "ALTER TABLE threads ADD COLUMN history_mode TEXT NOT NULL DEFAULT 'paginated'");
            using var history = new SqliteConnection($"Data Source={Path.Combine(home, MergeHistory.FileName)};Pooling=False");
            history.Open();
            Execute(history, """
                CREATE TABLE thread_turns(thread_id TEXT NOT NULL,turn_id TEXT NOT NULL,rollout_ordinal INTEGER NOT NULL,status TEXT NOT NULL,error_json TEXT,started_at INTEGER,completed_at INTEGER,duration_ms INTEGER,first_user_item_id TEXT,final_agent_item_id TEXT,rollout_byte_offset INTEGER,rollout_end_ordinal INTEGER,rollout_end_byte_offset INTEGER,root_turn_id TEXT,PRIMARY KEY(thread_id,turn_id));
                CREATE TABLE thread_items(thread_id TEXT NOT NULL,turn_id TEXT NOT NULL,item_id TEXT NOT NULL,rollout_ordinal INTEGER NOT NULL,created_at_ms INTEGER NOT NULL,item_json TEXT NOT NULL,item_type TEXT NOT NULL DEFAULT '',updated_at_ordinal INTEGER NOT NULL DEFAULT 0,started_at_ms INTEGER,completed_at_ms INTEGER,PRIMARY KEY(thread_id,turn_id,item_id));
                CREATE TABLE thread_history_projection_state(thread_id TEXT PRIMARY KEY,next_rollout_byte_offset INTEGER NOT NULL,next_rollout_ordinal INTEGER NOT NULL);
                CREATE TABLE thread_realtime_items(thread_id TEXT NOT NULL,item_id TEXT NOT NULL,rollout_ordinal INTEGER NOT NULL,created_at_ms INTEGER NOT NULL,item_type TEXT NOT NULL,item_json TEXT NOT NULL,PRIMARY KEY(thread_id,item_id));
                CREATE TRIGGER thread_realtime_items_projection_cleanup AFTER DELETE ON thread_history_projection_state BEGIN DELETE FROM thread_realtime_items WHERE thread_id = OLD.thread_id; END;
                """);
            // The legacy index must remain byte-for-byte unchanged, not be deleted to bypass checks.
            f.Write(home, "sqlite/state_5.sqlite", "dormant fixture index");
            f.Write(home, ".app-server-state-reconciled-v1", "already reconciled by desktop");
        }
    }
    internal static void Execute(SqliteConnection db, string sql)
    { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    internal static void AddHistory(string home, string id, string text)
    {
        using var db = MergeDatabase.Open(Path.Combine(home, MergeHistory.FileName), true);
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO thread_turns(thread_id,turn_id,rollout_ordinal,status,rollout_byte_offset,rollout_end_ordinal,rollout_end_byte_offset) VALUES($id,'t',1,'completed',100,8,900);
            INSERT INTO thread_items(thread_id,turn_id,item_id,rollout_ordinal,created_at_ms,item_json,item_type) VALUES($id,'t','i',2,10,$text,'userMessage');
            INSERT INTO thread_history_projection_state VALUES($id,900,9) ON CONFLICT(thread_id) DO UPDATE SET next_rollout_byte_offset=900,next_rollout_ordinal=9;
            INSERT INTO thread_realtime_items VALUES($id,'r',3,10,'message',$text);
            """;
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$text", text);
        command.ExecuteNonQuery();
    }
    internal static void Compress(string path)
    {
        using var compressor = new ZstdSharp.Compressor();
        File.WriteAllBytes(path + ".zst", compressor.Wrap(File.ReadAllBytes(path)).ToArray());
        File.Delete(path);
    }
    private static string HistoryHash(string home)
    { using var db = MergeDatabase.Open(Path.Combine(home, MergeHistory.FileName)); return MergeHistory.Fingerprint(db); }

    [TestMethod]
    public async Task CompressedPaginatedImport_PreservesBytesOffsetsLocalHistoryAndPackage()
    {
        using var f = new MergeFixture(); Initialize(f);
        var id = f.AddSession(f.Source, "incoming", cwd: @"C:\Users\OfficeUser\project");
        Directory.CreateDirectory(Path.Combine(f.Root, "home", "project"));
        AddHistory(f.Source, id, "incoming item");
        var personal = f.AddSession(f.Target, "personal"); AddHistory(f.Target, personal, "private item");
        var localFile = File.ReadAllBytes(f.SessionPath(f.Target, personal));
        Compress(f.SessionPath(f.Source, id));
        var bytes = File.ReadAllBytes(f.SessionPath(f.Source, id) + ".zst");
        await f.Package();
        var package = f.HashTree(f.PackageRoot);
        var service = f.Service();
        var scan = await service.PreviewAsync(f.Request());
        Assert.IsTrue(scan.Paginated);
        Assert.AreEqual(MergeAction.Add, scan.Entries.Single().Action, scan.Entries.Single().Reason);
        // Selection is not a data change: applying a scanned subset does not need another UI Dry Run.
        var selected = scan with { Request = scan.Request with { SelectedIds = [id] } };
        var result = await service.ApplyAsync(selected);
        Assert.IsTrue(result.Success, result.Message + string.Join(";", result.Errors));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(f.SessionPath(f.Target, id) + ".zst"));
        CollectionAssert.AreEqual(localFile, File.ReadAllBytes(f.SessionPath(f.Target, personal)));
        Assert.IsFalse(File.Exists(f.SessionPath(f.Target, id)));
        using (var db = MergeDatabase.Open(Path.Combine(f.Target, "state_5.sqlite")))
        {
            var row = MergeDatabase.Rows(db)[id];
            Assert.AreEqual(f.SessionPath(f.Target, id), row["rollout_path"]);
            Assert.AreEqual(Path.Combine(f.Root, "home", "project"), row["cwd"]);
        }
        using (var a = MergeDatabase.Open(Path.Combine(f.Source, MergeHistory.FileName)))
        using (var b = MergeDatabase.Open(Path.Combine(f.Target, MergeHistory.FileName)))
            Assert.AreEqual(MergeHistory.Fingerprint(a, id: id), MergeHistory.Fingerprint(b, id: id));
        Assert.AreEqual("dormant fixture index", File.ReadAllText(Path.Combine(f.Target, "sqlite/state_5.sqlite")));
        Assert.AreEqual(MergeAction.Identical, (await service.PreviewAsync(f.Request(id))).Entries.Single().Action);
        CollectionAssert.AreEquivalent(package.ToArray(), f.HashTree(f.PackageRoot).ToArray());
    }

    [DataTestMethod]
    [DataRow("SnapshotVerified")]
    [DataRow("SessionWritten")]
    [DataRow("BeforeCommit")]
    [DataRow("AfterCommit")]
    public async Task Failure_RollsBackBothDatabases(string stage)
    {
        using var f = new MergeFixture(); Initialize(f);
        var id = f.AddSession(f.Source, "incoming"); AddHistory(f.Source, id, "data");
        var beforeHistory = HistoryHash(f.Target);
        string beforeState;
        using (var db = MergeDatabase.Open(Path.Combine(f.Target, "state_5.sqlite"))) beforeState = MergeDatabase.Fingerprint(db);
        await f.Package();
        var service = f.Service(s => { if (s == stage) throw new IOException("injected"); });
        var result = await service.ApplyAsync(await service.PreviewAsync(f.Request(id)));
        Assert.IsFalse(result.Success);
        Assert.AreEqual(beforeHistory, HistoryHash(f.Target));
        using (var db = MergeDatabase.Open(Path.Combine(f.Target, "state_5.sqlite"))) Assert.AreEqual(beforeState, MergeDatabase.Fingerprint(db));
        Assert.IsFalse(File.Exists(f.SessionPath(f.Target, id)));
        Assert.AreEqual("RolledBack", service.RestorePoints().Single().Status);
    }

    [TestMethod]
    public async Task PartialDatabaseCommit_CanRecoverAndNewerHistoryIsNeverRemoved()
    {
        using var f = new MergeFixture(); Initialize(f);
        var id = f.AddSession(f.Source, "incoming"); AddHistory(f.Source, id, "data");
        await f.Package(); var service = f.Service();
        Assert.IsTrue((await service.ApplyAsync(await service.PreviewAsync(f.Request(id)))).Success);
        var point = service.RestorePoints().Single();
        using (var db = MergeDatabase.Open(Path.Combine(f.Target, "state_5.sqlite"), true))
        {
            MergeHistory.Attach(db, Path.Combine(f.Target, MergeHistory.FileName));
            using var tx = db.BeginTransaction();
            MergeHistory.DeleteInserted(db, tx, [id]); tx.Commit();
        }
        Assert.IsTrue(service.Recover(point.Path).Success);
        Assert.IsTrue((await service.ApplyAsync(await service.PreviewAsync(f.Request(id)))).Success);
        using (var db = MergeDatabase.Open(Path.Combine(f.Target, MergeHistory.FileName), true)) Execute(db, "UPDATE thread_items SET item_json='new local turn'");
        var newer = HistoryHash(f.Target);
        Assert.IsFalse(service.Recover(service.RestorePoints().Single(p => p.Status == "Completed").Path).Success);
        Assert.AreEqual(newer, HistoryHash(f.Target));
    }

    [TestMethod]
    public async Task SameRolloutDifferentHistory_PreservesConflictWithHistoryOutsideProfile()
    {
        using var f = new MergeFixture(); Initialize(f);
        var id = f.AddSession(f.Source, "same transcript");
        f.AddSession(f.Target, "same transcript", id);
        AddHistory(f.Source, id, "remote turn"); AddHistory(f.Target, id, "personal turn");
        var before = HistoryHash(f.Target);
        await f.Package(); var service = f.Service();
        var plan = await service.PreviewAsync(f.Request(id));
        Assert.AreEqual(MergeAction.Conflict, plan.Entries.Single().Action);
        Assert.IsTrue((await service.ApplyAsync(plan)).Success);
        Assert.AreEqual(before, HistoryHash(f.Target));
        StringAssert.Contains(File.ReadAllText(plan.Entries.Single().TargetPath + ".history.json"), "remote turn");
        Assert.AreEqual(MergeAction.ConflictAlreadyPreserved, (await service.PreviewAsync(f.Request(id))).Entries.Single().Action);
    }

    [TestMethod]
    public async Task UnknownHistorySchema_StopsBeforeDestinationWrite()
    {
        using var f = new MergeFixture(); Initialize(f);
        var id = f.AddSession(f.Source, "incoming");
        using (var db = MergeDatabase.Open(Path.Combine(f.Target, MergeHistory.FileName), true)) Execute(db, "ALTER TABLE thread_items ADD COLUMN future_required TEXT");
        await f.Package();
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => f.Service().PreviewAsync(f.Request(id)));
        Assert.IsFalse(File.Exists(f.SessionPath(f.Target, id)));
    }

    [TestMethod]
    public async Task UnindexedLocalRollout_BlocksDuplicateIdAtDifferentPath()
    {
        using var f = new MergeFixture(); Initialize(f);
        var id = f.AddSession(f.Source, "incoming"); AddHistory(f.Source, id, "data");
        var orphan = Path.Combine(f.Target, "sessions", "orphan.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
        File.Copy(f.SessionPath(f.Source, id), orphan);
        await f.Package();
        var plan = await f.Service().PreviewAsync(f.Request(id));
        Assert.AreEqual(MergeAction.Blocked, plan.Entries.Single().Action);
    }

    [TestMethod]
    public async Task RealPackagePreview_UsesOnlyIsolatedDestinationAndDoesNotImport()
    {
        var package = Environment.GetEnvironmentVariable("CODEXSHUTTLE_PROBE_PACKAGE");
        var executable = Environment.GetEnvironmentVariable("CODEXSHUTTLE_TEST_CODEX");
        if (string.IsNullOrEmpty(package) || string.IsNullOrEmpty(executable)) Assert.Inconclusive("Opt-in read-only package preview.");
        using var f = new MergeFixture();
        File.Delete(Path.Combine(f.Target, "state_5.sqlite"));
        await using (var native = new MergeAppServerTests.IsolatedAppServer(executable!, f))
        {
            await native.Initialize(experimental: true);
            var started = await native.Call("thread/start", new { cwd = f.Workspace, historyMode = "paginated", approvalPolicy = "never", sandbox = "read-only" });
            await native.Call("thread/inject_items", new { threadId = started.GetProperty("thread").GetProperty("id").GetString(), items = new[] { new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "Synthetic preview destination" } } } } });
        }
        var request = f.Request() with { PackageRoot = package!, TargetUserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        var plan = await f.Service().PreviewAsync(request);
        Assert.IsTrue(plan.Paginated);
        Assert.IsTrue(plan.Entries.Count > 0);
        Console.WriteLine(plan.Summary);
        foreach (var group in plan.Entries.Where(e => e.Action == MergeAction.Blocked).GroupBy(e => e.Reason))
            Console.WriteLine($"Blocked ({group.Count()}): {group.Key}");
        Assert.IsFalse(Directory.Exists(Path.Combine(f.Root, "private-store")));
    }

    [TestMethod]
    public async Task NativePaginatedCompressedImport_CanReadAndResumeWithoutModelCall()
    {
        var executable = Environment.GetEnvironmentVariable("CODEXSHUTTLE_TEST_CODEX");
        if (string.IsNullOrEmpty(executable)) Assert.Inconclusive("Set CODEXSHUTTLE_TEST_CODEX for isolated native validation.");
        using var f = new MergeFixture();
        var sourceUser = Path.Combine(f.Root, "office-user");
        var sourceWorkspace = Directory.CreateDirectory(Path.Combine(sourceUser, "work")).FullName;
        var targetWorkspace = Path.Combine(f.Root, "home", "work");
        var ids = new List<string>();
        foreach (var (home, message) in new[] { (f.Source, "Synthetic incoming paginated conversation"), (f.Target, "Synthetic personal conversation") })
        {
            File.Delete(Path.Combine(home, "state_5.sqlite"));
            await using var native = new MergeAppServerTests.IsolatedAppServer(executable!, f, home);
            await native.Initialize(experimental: true);
            var started = await native.Call("thread/start", new { cwd = home == f.Source ? sourceWorkspace : f.Workspace, historyMode = "paginated", approvalPolicy = "never", sandbox = "read-only" });
            var id = started.GetProperty("thread").GetProperty("id").GetString()!;
            ids.Add(id);
            await native.Call("thread/inject_items", new { threadId = id, items = new[] { new { type = "message", role = "user", content = new[] { new { type = "input_text", text = message } } } } });
        }
        var incoming = ids[0]; var personal = ids[1];
        // Seed a synthetic projected turn in native-created schemas, without a model request.
        foreach (var (home, id, message) in new[] { (f.Source, incoming, "Synthetic incoming paginated conversation"), (f.Target, personal, "Synthetic personal conversation") })
        {
            AddHistory(home, id, System.Text.Json.JsonSerializer.Serialize(new { type = "userMessage", id = "i", content = new[] { new { type = "text", text = message } } }));
            using var state = MergeDatabase.Open(Path.Combine(home, "state_5.sqlite"));
            var rollout = Convert.ToString(MergeDatabase.Rows(state)[id]["rollout_path"])!;
            using var history = MergeDatabase.Open(Path.Combine(home, MergeHistory.FileName), true);
            Execute(history, $"UPDATE thread_history_projection_state SET next_rollout_byte_offset={new FileInfo(rollout).Length}, next_rollout_ordinal={File.ReadLines(rollout).Count()}; DELETE FROM thread_realtime_items;");
        }
        string incomingPath;
        using (var db = MergeDatabase.Open(Path.Combine(f.Source, "state_5.sqlite")))
        {
            Assert.AreEqual("paginated", MergeDatabase.Rows(db)[incoming]["history_mode"]);
            incomingPath = Convert.ToString(MergeDatabase.Rows(db)[incoming]["rollout_path"])!;
        }
        Compress(incomingPath);
        f.Write(f.PackageRoot, "project-files/README.md", "Synthetic migrated workspace");
        await f.Package(sourceUser, [new CodexShuttle.Core.Models.WorkspaceEntry { SourcePath = sourceWorkspace, PackagePath = "project-files", Enabled = true, Exists = true }]);
        var service = f.Service();
        var plan = await service.PreviewAsync(f.Request(incoming));
        Assert.AreEqual(MergeAction.Add, plan.Entries.Single().Action, plan.Entries.Single().Reason);
        var result = await service.ApplyAsync(plan);
        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual("Synthetic migrated workspace", File.ReadAllText(Path.Combine(targetWorkspace, "README.md")));
        await using var target = new MergeAppServerTests.IsolatedAppServer(executable!, f);
        await target.Initialize(experimental: true);
        var read = await target.Call("thread/read", new { threadId = incoming, includeTurns = true });
        StringAssert.Contains(read.GetRawText(), "Synthetic incoming paginated conversation");
        var kept = await target.Call("thread/read", new { threadId = personal, includeTurns = true });
        StringAssert.Contains(kept.GetRawText(), "Synthetic personal conversation");
        var resumed = await target.Call("thread/resume", new { threadId = incoming, approvalPolicy = "never", sandbox = "read-only" });
        Assert.AreEqual(incoming, resumed.GetProperty("thread").GetProperty("id").GetString());
        Assert.AreEqual(targetWorkspace, resumed.GetProperty("thread").GetProperty("cwd").GetString());
    }
}
