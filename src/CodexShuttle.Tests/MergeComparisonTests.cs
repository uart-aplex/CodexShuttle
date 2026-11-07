using System.Text.Json;
using CodexShuttle.Core.Merge;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class MergeComparisonTests
{
    [TestMethod]
    public async Task MigratedProfileCompressionSettingsAndOffsets_AreEquivalentAndDoNotWrite()
    {
        using var f = new MergeFixture(); PaginatedMergeTests.Initialize(f);
        var localUser = Path.Combine(f.Root, "home");
        var sourceMessage = @"Read C:\Users\OfficeUser\notes.txt";
        var localMessage = "Read " + localUser + @"\notes.txt";
        var id = f.AddSession(f.Source, sourceMessage);
        f.AddSession(f.Target, localMessage, id);
        PaginatedMergeTests.AddHistory(f.Source, id, JsonSerializer.Serialize(new { type = "userMessage", text = sourceMessage }));
        PaginatedMergeTests.AddHistory(f.Target, id, JsonSerializer.Serialize(new { type = "userMessage", text = localMessage }));
        File.AppendAllText(f.SessionPath(f.Source, id), "{\"type\":\"event_msg\",\"payload\":{\"type\":\"thread_settings_applied\",\"thread_settings\":{\"model\":\"test\"}}}\n");
        PaginatedMergeTests.Compress(f.SessionPath(f.Source, id));
        using (var db = MergeDatabase.Open(Path.Combine(f.Target, MergeHistory.FileName), true))
            PaginatedMergeTests.Execute(db, "UPDATE thread_turns SET rollout_byte_offset=42,rollout_end_byte_offset=99,rollout_ordinal=7,rollout_end_ordinal=10; UPDATE thread_items SET rollout_ordinal=8,updated_at_ordinal=12; UPDATE thread_history_projection_state SET next_rollout_byte_offset=99,next_rollout_ordinal=13;");
        await f.Package();
        var package = f.HashTree(f.PackageRoot); var local = f.HashTree(f.Target);
        var service = f.Service();
        var plan = await service.PreviewAsync(f.Request(id));
        Assert.AreEqual(MergeAction.Equivalent, plan.Entries.Single().Action, plan.Entries.Single().Reason);
        var result = await service.ApplyAsync(plan);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.Contains(result.Message, "No changes");
        Assert.AreEqual(0, service.RestorePoints().Count);
        CollectionAssert.AreEquivalent(package.ToArray(), f.HashTree(f.PackageRoot).ToArray());
        CollectionAssert.AreEquivalent(local.ToArray(), f.HashTree(f.Target).ToArray());
    }

    [DataTestMethod]
    [DataRow("user_message")]
    [DataRow("agent_message")]
    [DataRow("unknown_future_event")]
    [DataRow("tool_result")]
    public async Task ExtraLocalContent_IsLocalAhead_AndTodaysLocalFileIsUntouched(string type)
    {
        using var f = new MergeFixture(); PaginatedMergeTests.Initialize(f);
        var id = f.AddSession(f.Source, "old conversation");
        f.AddSession(f.Target, "old conversation", id);
        PaginatedMergeTests.AddHistory(f.Source, id, "old item");
        PaginatedMergeTests.AddHistory(f.Target, id, "old item");
        File.AppendAllText(f.SessionPath(f.Target, id), JsonSerializer.Serialize(new { type = "event_msg", payload = new { type, message = "today's local work" } }) + "\n");
        await f.Package();
        var before = f.HashTree(f.Target);
        var service = f.Service(); var plan = await service.PreviewAsync(f.Request(id));
        Assert.AreEqual(MergeAction.LocalAhead, plan.Entries.Single().Action);
        var result = await service.ApplyAsync(plan);
        Assert.IsTrue(result.Success);
        StringAssert.Contains(result.Message, "No changes");
        Assert.AreEqual(0, service.RestorePoints().Count);
        CollectionAssert.AreEquivalent(before.ToArray(), f.HashTree(f.Target).ToArray());
    }

    [TestMethod]
    public void PathMapping_RespectsUserBoundaryEscapingAndSinglePass()
    {
        var request = new MergeRequest(@"L:\backup", @"C:\Users\Home\.codex", @"C:\Users\Home", [], new());
        var comparison = new MergeComparison(request, @"C:\Users\Office");
        Assert.AreEqual(@"C:\Users\Home\file", comparison.Normalize(@"C:\Users\Office\file"));
        Assert.AreEqual(@"C:\\Users\\Home\\file", comparison.Normalize(@"C:\\Users\\Office\\file"));
        Assert.AreEqual("C:/Users/Home/file", comparison.Normalize("C:/Users/Office/file"));
        Assert.AreEqual(@"C:\Users\OfficeOther\file", comparison.Normalize(@"C:\Users\OfficeOther\file"));
        Assert.AreEqual(@"prefixC:\Users\Office\file", comparison.Normalize(@"prefixC:\Users\Office\file"));
        Assert.AreEqual(@"D:\Users\Office\file", comparison.Normalize(@"D:\Users\Office\file"));
        var nested = new MergeComparison(request with { TargetUserProfile = @"C:\Users\Office\home" }, @"C:\Users\Office");
        Assert.AreEqual(@"C:\Users\Office\home\file", nested.Normalize(@"C:\Users\Office\home\file"));
    }

    [TestMethod]
    public void JsonFormatting_IsIgnored_ButArrayOrderAndTextAreNot()
    {
        var comparison = new MergeComparison(new("", "", @"C:\Users\Home", [], new()), @"C:\Users\Office");
        byte[] Canonical(string json) { using var doc = JsonDocument.Parse(json); return comparison.Canonical(doc.RootElement); }
        CollectionAssert.AreEqual(Canonical("{\"a\":1,\"b\":[\"x\",\"y\"]}"), Canonical("{ \"b\": [\"x\",\"y\"], \"a\": 1 }"));
        Assert.IsFalse(Canonical("[\"x\",\"y\"]").SequenceEqual(Canonical("[\"y\",\"x\"]")));
        Assert.IsFalse(Canonical("\"message\"").SequenceEqual(Canonical("\"Message\"")));
        Assert.ThrowsException<InvalidDataException>(() => Canonical("{\"a\":1,\"a\":2}"));
    }

    [TestMethod]
    public async Task DifferentProjectedMessage_WithEqualRollout_RemainsConflict()
    {
        using var f = new MergeFixture(); PaginatedMergeTests.Initialize(f);
        var id = f.AddSession(f.Source, "same rollout"); f.AddSession(f.Target, "same rollout", id);
        PaginatedMergeTests.AddHistory(f.Source, id, "old message"); PaginatedMergeTests.AddHistory(f.Target, id, "today's correction");
        await f.Package();
        var entry = (await f.Service().PreviewAsync(f.Request(id))).Entries.Single();
        Assert.AreEqual(MergeAction.Conflict, entry.Action);
        StringAssert.Contains(entry.Reason, "projected turns/items differ");
    }
}
