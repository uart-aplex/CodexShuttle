using CodexShuttle.Core.Services;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class OperationJournalTests
{
    [TestMethod]
    public void Finish_DropsQueuedProgressAndKeepsFullDiskLog()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexShuttleTests", Guid.NewGuid().ToString("N"));
        try
        {
            var queued = new Queue<Action>();
            var displayed = new List<string>();
            using var journal = new OperationJournal(root, "backup", queued.Enqueue, displayed.Add);
            for (var i = 0; i < 1000; i++) journal.Report($"Step {i}");
            journal.Finish("Backup failed; previous backup retained.");
            while (queued.TryDequeue(out var callback)) callback();
            journal.Report("Late success");
            journal.Finish("Other result");
            Assert.AreEqual(0, displayed.Count);
            var lines = File.ReadAllLines(journal.FilePath);
            Assert.AreEqual(1001, lines.Length);
            StringAssert.Contains(lines[^1], "Backup failed");
            Assert.IsNull(journal.Error);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void UnwritableLog_ReportsErrorWithoutReportingSuccess()
    {
        var file = Path.GetTempFileName();
        try
        {
            using var journal = new OperationJournal(file, "backup", action => action(), _ => { });
            journal.Report("Starting");
            Assert.IsNotNull(journal.Error);
        }
        finally { File.Delete(file); }
    }
}
