using System.Text.Json;
using CodexShuttle.Core.Merge;
using Microsoft.Data.Sqlite;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class IncrementalMergeTests
{
    private static string Setup(MergeFixture f, bool mapped = false)
    {
        PaginatedMergeTests.Initialize(f);
        var localProject = mapped ? Directory.CreateDirectory(Path.Combine(f.Root, "home", "project")).FullName : f.Workspace;
        var id = f.AddSession(f.Source, mapped ? @"Read C:\Users\OfficeUser\notes.txt" : "shared history", cwd: mapped ? @"C:\Users\OfficeUser\project" : f.Workspace);
        f.AddSession(f.Target, mapped ? "Read " + Path.Combine(f.Root, "home", "notes.txt") : "shared history", id, localProject);
        PaginatedMergeTests.AddHistory(f.Source, id, "shared item");
        PaginatedMergeTests.AddHistory(f.Target, id, "shared item");
        return id;
    }

    internal static void AppendTurn(string home, string path, string id, string message, string turn = "t2")
    {
        var ordinal = File.ReadLines(path).Select(line =>
        {
            using var json = JsonDocument.Parse(line);
            return json.RootElement.TryGetProperty("ordinal", out var value) && value.TryGetInt64(out var number) ? number : 0L;
        }).DefaultIfEmpty().Max() + 1;
        File.AppendAllText(path, JsonSerializer.Serialize(new { timestamp = "2026-11-07T00:00:01Z", ordinal, type = "event_msg",
            payload = new { type = "user_message", message, images = Array.Empty<string>(), local_images = Array.Empty<string>(), text_elements = Array.Empty<string>() } }) + "\n");
        using var db = MergeDatabase.Open(Path.Combine(home, MergeHistory.FileName), true);
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO thread_turns(thread_id,turn_id,rollout_ordinal,status,rollout_byte_offset,rollout_end_ordinal,rollout_end_byte_offset) VALUES($id,$turn,20,'completed',900,30,$length);
            INSERT INTO thread_items(thread_id,turn_id,item_id,rollout_ordinal,created_at_ms,item_json,item_type) VALUES($id,$turn,$turn,21,20,$text,'userMessage');
            UPDATE thread_history_projection_state SET next_rollout_byte_offset=$length,next_rollout_ordinal=$lines WHERE thread_id=$id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$turn", turn);
        command.Parameters.AddWithValue("$text", JsonSerializer.Serialize(new { type = "userMessage", id = turn, content = new[] { new { type = "text", text = message } } }));
        command.Parameters.AddWithValue("$length", new FileInfo(path).Length);
        command.Parameters.AddWithValue("$lines", File.ReadLines(path).Count());
        command.ExecuteNonQuery();
    }

    private static string State(string home)
    { using var db = MergeDatabase.Open(Path.Combine(home, "state_5.sqlite")); return MergeDatabase.Fingerprint(db); }
    private static string History(string home)
    { using var db = MergeDatabase.Open(Path.Combine(home, MergeHistory.FileName)); return MergeHistory.Fingerprint(db); }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SourceContinuation_UpdatesSameId_PreservesLocalMetadataAndOriginal_AndIsIdempotent(bool compressed)
    {
        using var f = new MergeFixture(); var id = Setup(f, mapped: true);
        var personal = f.AddSession(f.Target, "today local only"); PaginatedMergeTests.AddHistory(f.Target, personal, "today local item");
        using (var db = MergeDatabase.Open(Path.Combine(f.Target, "state_5.sqlite"), true))
            PaginatedMergeTests.Execute(db, "ALTER TABLE threads ADD COLUMN is_pinned INTEGER DEFAULT 1; ALTER TABLE threads ADD COLUMN project_id TEXT DEFAULT 'local-project'; UPDATE threads SET title='local title',model_provider='local-provider';");
        AppendTurn(f.Source, f.SessionPath(f.Source, id), id, "incoming continuation");
        if (compressed) PaginatedMergeTests.Compress(f.SessionPath(f.Source, id));
        await f.Package();
        var before = File.ReadAllBytes(f.SessionPath(f.Target, id));
        var personalBefore = File.ReadAllBytes(f.SessionPath(f.Target, personal));
        var package = f.HashTree(f.PackageRoot);
        var state = State(f.Target); var history = History(f.Target);
        var service = f.Service(); var plan = await service.PreviewAsync(f.Request(id));
        var entry = plan.Entries.Single();
        Assert.AreEqual(MergeAction.Update, entry.Action, entry.Reason);
        var result = await service.ApplyAsync(plan);
        Assert.IsTrue(result.Success, result.Message + string.Join(";", result.Errors));
        StringAssert.Contains(result.Message, "Updated 1 conversations");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(f.SessionPath(f.Target, id)));
        CollectionAssert.AreEqual(personalBefore, File.ReadAllBytes(f.SessionPath(f.Target, personal)));
        CollectionAssert.AreEqual(File.ReadAllBytes(entry.SourcePath), File.ReadAllBytes(entry.TargetPath));
        using (var db = MergeDatabase.Open(Path.Combine(f.Target, "state_5.sqlite")))
        {
            var row = MergeDatabase.Rows(db)[id];
            Assert.AreEqual("local title", row["title"]); Assert.AreEqual("local-provider", row["model_provider"]);
            Assert.AreEqual(1L, row["is_pinned"]); Assert.AreEqual("local-project", row["project_id"]);
            Assert.AreEqual(compressed ? entry.TargetPath[..^4] : entry.TargetPath, row["rollout_path"]);
            Assert.AreEqual(Path.Combine(f.Root, "home", "project"), row["cwd"]);
        }
        Assert.AreEqual(MergeAction.Identical, (await service.PreviewAsync(f.Request(id))).Entries.Single().Action);
        CollectionAssert.AreEquivalent(package.ToArray(), f.HashTree(f.PackageRoot).ToArray());
        var recovered = service.Recover(service.RestorePoints().Single().Path);
        Assert.IsTrue(recovered.Success, recovered.Message + string.Join(";", recovered.Errors));
        Assert.AreEqual(state, State(f.Target)); Assert.AreEqual(history, History(f.Target));
        Assert.IsFalse(File.Exists(entry.TargetPath));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(f.SessionPath(f.Target, id)));
    }

    [DataTestMethod]
    [DataRow("SnapshotVerified")]
    [DataRow("SessionWritten")]
    [DataRow("BeforeCommit")]
    [DataRow("AfterCommit")]
    public async Task UpdateFailure_RestoresBothDatabasesAndOriginalTranscript(string checkpoint)
    {
        using var f = new MergeFixture(); var id = Setup(f);
        AppendTurn(f.Source, f.SessionPath(f.Source, id), id, "incoming continuation"); await f.Package();
        var state = State(f.Target); var history = History(f.Target); var before = File.ReadAllBytes(f.SessionPath(f.Target, id));
        var service = f.Service(s => { if (s == checkpoint) throw new IOException("injected failure"); });
        var plan = await service.PreviewAsync(f.Request(id));
        Assert.IsFalse((await service.ApplyAsync(plan)).Success);
        Assert.AreEqual(state, State(f.Target)); Assert.AreEqual(history, History(f.Target));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(f.SessionPath(f.Target, id)));
        Assert.IsFalse(File.Exists(plan.Entries.Single().TargetPath));
        Assert.AreEqual("RolledBack", service.RestorePoints().Single().Status);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PartialCommit_RecoversOnlyChangedSide(bool stateCommitted)
    {
        using var f = new MergeFixture(); var id = Setup(f);
        AppendTurn(f.Source, f.SessionPath(f.Source, id), id, "incoming continuation"); await f.Package();
        var state = State(f.Target); var history = History(f.Target); var service = f.Service();
        Assert.IsTrue((await service.ApplyAsync(await service.PreviewAsync(f.Request(id)))).Success);
        var point = service.RestorePoints().Single();
        using (var db = MergeDatabase.Open(Path.Combine(f.Target, "state_5.sqlite"), true))
        {
            MergeHistory.Attach(db, Path.Combine(f.Target, MergeHistory.FileName));
            using var tx = db.BeginTransaction();
            using var saved = MergeDatabase.Open(Path.Combine(point.Path, stateCommitted ? "history.sqlite" : "state.sqlite"));
            if (stateCommitted) { MergeHistory.DeleteInserted(db, tx, [id]); MergeHistory.Insert(saved, db, tx, id); }
            else MergeDatabase.Update(db, tx, MergeDatabase.Rows(saved)[id]);
            tx.Commit();
        }
        var recovered = service.Recover(point.Path);
        Assert.IsTrue(recovered.Success, string.Join(";", recovered.Errors));
        Assert.AreEqual(state, State(f.Target)); Assert.AreEqual(history, History(f.Target));
    }

    [TestMethod]
    public async Task LocalContinuation_IsNotReplacedByOlderBackup()
    {
        using var f = new MergeFixture(); var id = Setup(f);
        AppendTurn(f.Target, f.SessionPath(f.Target, id), id, "today's local work"); await f.Package();
        var before = f.HashTree(f.Target); var service = f.Service(); var plan = await service.PreviewAsync(f.Request(id));
        Assert.AreEqual(MergeAction.LocalAhead, plan.Entries.Single().Action);
        Assert.IsTrue((await service.ApplyAsync(plan)).Success);
        CollectionAssert.AreEquivalent(before.ToArray(), f.HashTree(f.Target).ToArray());
        Assert.AreEqual(0, service.RestorePoints().Count);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DivergenceOrProjectionDisagreement_RemainsConflictAndDoesNotReplaceLocal(bool projectionOnly)
    {
        using var f = new MergeFixture(); var id = Setup(f);
        AppendTurn(f.Source, f.SessionPath(f.Source, id), id, "office branch");
        if (!projectionOnly) AppendTurn(f.Target, f.SessionPath(f.Target, id), id, "home branch");
        else using (var db = MergeDatabase.Open(Path.Combine(f.Target, MergeHistory.FileName), true))
            PaginatedMergeTests.Execute(db, "UPDATE thread_items SET item_json='locally corrected content'");
        await f.Package(); var before = f.HashTree(f.Target); var service = f.Service(); var plan = await service.PreviewAsync(f.Request(id));
        Assert.AreEqual(MergeAction.Conflict, plan.Entries.Single().Action);
        Assert.IsTrue((await service.ApplyAsync(plan)).Success);
        CollectionAssert.AreEquivalent(before.ToArray(), f.HashTree(f.Target).ToArray());
    }

    [TestMethod]
    public async Task NewerLocalWorkAfterUpdate_BlocksRollbackAndStaleApproval()
    {
        using var f = new MergeFixture(); var id = Setup(f);
        AppendTurn(f.Source, f.SessionPath(f.Source, id), id, "office continuation"); await f.Package();
        var service = f.Service(); var stale = await service.PreviewAsync(f.Request(id));
        Assert.IsTrue((await service.ApplyAsync(stale)).Success);
        var point = service.RestorePoints().Single();
        AppendTurn(f.Target, stale.Entries.Single().TargetPath, id, "newer local work", "t3");
        var before = f.HashTree(f.Target);
        Assert.IsFalse(service.Recover(point.Path).Success);
        Assert.IsFalse((await service.ApplyAsync(stale)).Success);
        CollectionAssert.AreEquivalent(before.ToArray(), f.HashTree(f.Target).ToArray());
    }

    [TestMethod]
    public async Task UpdatedRevision_CanBePackagedAndImportedOnAnotherComputer()
    {
        using var f = new MergeFixture(); var id = Setup(f);
        AppendTurn(f.Source, f.SessionPath(f.Source, id), id, "incoming continuation"); await f.Package();
        var service = f.Service(); var plan = await service.PreviewAsync(f.Request(id));
        Assert.IsTrue((await service.ApplyAsync(plan)).Success);
        using var next = new MergeFixture(); PaginatedMergeTests.Initialize(next);
        // Synthetic profile only: emulate the existing backup layout, including retained revisions.
        foreach (var file in Directory.EnumerateFiles(f.Target, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(next.Source, Path.GetRelativePath(f.Target, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, true);
        }
        await next.Package(Path.Combine(f.Root, "home"));
        var second = next.Service(); var moved = await second.PreviewAsync(next.Request(id));
        Assert.AreEqual(MergeAction.Add, moved.Entries.Single().Action, moved.Entries.Single().Reason);
        Assert.IsTrue((await second.ApplyAsync(moved)).Success);
        using var a = MergeDatabase.Open(Path.Combine(f.Target, MergeHistory.FileName));
        using var b = MergeDatabase.Open(Path.Combine(next.Target, MergeHistory.FileName));
        Assert.AreEqual(MergeHistory.Fingerprint(a,id:id), MergeHistory.Fingerprint(b,id:id));
        Assert.AreEqual(MergeAction.Identical, (await second.PreviewAsync(next.Request(id))).Entries.Single().Action);
    }

    [TestMethod]
    public async Task ChangedOriginalRevision_RefusesRollbackWithoutDeletingFiles()
    {
        using var f = new MergeFixture(); var id = Setup(f);
        AppendTurn(f.Source, f.SessionPath(f.Source, id), id, "incoming continuation"); await f.Package();
        var service = f.Service();
        Assert.IsTrue((await service.ApplyAsync(await service.PreviewAsync(f.Request(id)))).Success);
        File.AppendAllText(f.SessionPath(f.Target, id), "changed original");
        var before = f.HashTree(f.Target);
        Assert.IsFalse(service.Recover(service.RestorePoints().Single().Path).Success);
        CollectionAssert.AreEquivalent(before.ToArray(), f.HashTree(f.Target).ToArray());
    }

    [TestMethod]
    public void MalformedLongerRollout_IsRejectedEvenAfterMatchingPrefix()
    {
        using var f = new MergeFixture(); var id = Setup(f);
        File.AppendAllText(f.SessionPath(f.Source, id), "{\"type\":\"event_msg\"}\ninvalid-json\n");
        var comparison = new MergeComparison(f.Request(), @"C:\Users\OfficeUser");
        try { comparison.CompareRollouts(f.SessionPath(f.Source, id), f.SessionPath(f.Target, id), default); Assert.Fail("Malformed tail accepted."); }
        catch (JsonException) { }
    }

    [TestMethod]
    public async Task NativeIncrementalMerge_CanReadNewTurnsAndResumeSameIdWithoutModelCall()
    {
        var executable = Environment.GetEnvironmentVariable("CODEXSHUTTLE_TEST_CODEX");
        if (string.IsNullOrEmpty(executable)) Assert.Inconclusive("Set CODEXSHUTTLE_TEST_CODEX for isolated native validation.");
        using var f = new MergeFixture();
        string id = "", path = "";
        var personal = "";
        foreach (var home in new[] { f.Source, f.Target })
        {
            File.Delete(Path.Combine(home, "state_5.sqlite"));
            await using var native = new MergeAppServerTests.IsolatedAppServer(executable!, f, home);
            await native.Initialize(experimental: true);
            var start = await native.Call("thread/start", new { cwd = f.Workspace, historyMode = "paginated", approvalPolicy = "never", sandbox = "read-only" });
            var thread = start.GetProperty("thread").GetProperty("id").GetString()!;
            await native.Call("thread/inject_items", new { threadId = thread, items = new[] { new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "Synthetic shared base" } } } } });
            if (home == f.Source) id = thread; else personal = thread;
        }
        foreach (var (home, thread) in new[] { (f.Source, id), (f.Target, personal) })
        {
            PaginatedMergeTests.AddHistory(home, thread, JsonSerializer.Serialize(new { type = "userMessage", id = "i", content = new[] { new { type = "text", text = "Synthetic shared base" } } }));
            using var db = MergeDatabase.Open(Path.Combine(home, "state_5.sqlite"));
            var rollout = Convert.ToString(MergeDatabase.Rows(db)[thread]["rollout_path"])!;
            using var history = MergeDatabase.Open(Path.Combine(home, MergeHistory.FileName), true);
            PaginatedMergeTests.Execute(history, $"UPDATE thread_history_projection_state SET next_rollout_byte_offset={new FileInfo(rollout).Length},next_rollout_ordinal={File.ReadLines(rollout).Count()}; DELETE FROM thread_realtime_items;");
            if (home == f.Source) path = rollout;
        }
        var service = f.Service(); await f.Package();
        var first = await service.ApplyAsync(await service.PreviewAsync(f.Request(id)));
        Assert.IsTrue(first.Success, first.Message + string.Join(";", first.Errors));
        AppendTurn(f.Source, path, id, "Synthetic incoming continuation now visible");
        PaginatedMergeTests.Compress(path);
        await f.Package();
        var plan = await service.PreviewAsync(f.Request(id));
        Assert.AreEqual(MergeAction.Update, plan.Entries.Single().Action, plan.Entries.Single().Reason);
        var applied = await service.ApplyAsync(plan);
        Assert.IsTrue(applied.Success, applied.Message + string.Join(";", applied.Errors));
        await using var target = new MergeAppServerTests.IsolatedAppServer(executable!, f);
        await target.Initialize(experimental: true);
        var read = await target.Call("thread/read", new { threadId = id, includeTurns = true });
        StringAssert.Contains(read.GetRawText(), "Synthetic shared base");
        StringAssert.Contains(read.GetRawText(), "Synthetic incoming continuation now visible");
        var kept = await target.Call("thread/read", new { threadId = personal, includeTurns = true });
        StringAssert.Contains(kept.GetRawText(), "Synthetic shared base");
        var resumed = await target.Call("thread/resume", new { threadId = id, approvalPolicy = "never", sandbox = "read-only" });
        Assert.AreEqual(id, resumed.GetProperty("thread").GetProperty("id").GetString());
        Assert.AreEqual(f.Workspace, resumed.GetProperty("thread").GetProperty("cwd").GetString());
        StringAssert.Contains(resumed.GetRawText(), "Synthetic incoming continuation now visible");
    }
}
