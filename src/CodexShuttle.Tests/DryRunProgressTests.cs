using CodexShuttle.Core.Models;
using CodexShuttle.Core.Services;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class DryRunProgressTests
{
    private string _root = null!;
    [TestInitialize]
    public void Setup() => _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CodexShuttleTests", Guid.NewGuid().ToString("N"))).FullName;
    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, true);

    [TestMethod]
    public void Plan_LogsBothSidesAndResultsWithoutChangingFiles()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        File.WriteAllText(Path.Combine(source, "new.txt"), "source");
        File.WriteAllText(Path.Combine(target, "extra.txt"), "keep");
        var messages = new List<string>();
        using var journal = new OperationJournal(Path.Combine(_root, "logs"), "dry-run", action => action(), messages.Add);
        var result = new DryRunService().ComparePlan([new MirrorPlanItem { Name = "Workspace", SourcePath = source, DestinationPath = target }], journal);
        journal.Finish("Dry Run completed.");
        Assert.AreEqual(1, result.FilesToCopy);
        Assert.AreEqual(1, result.FilesToDelete);
        Assert.IsTrue(messages.Any(x => x.Contains("Scanning source:")));
        Assert.IsTrue(messages.Any(x => x.Contains("Scanning destination:")));
        Assert.IsTrue(messages.Any(x => x.Contains("current file:")));
        Assert.IsTrue(messages.Any(x => x.Contains("Target scanned:")));
        Assert.IsTrue(File.ReadAllText(journal.FilePath).Contains("Dry Run completed."));
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(target, "extra.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(target, "new.txt")));
        Assert.AreEqual("source", File.ReadAllText(Path.Combine(source, "new.txt")));
    }

    [TestMethod]
    public void Scan_CanCancelFromProgressWithoutChangingFiles()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        File.WriteAllText(Path.Combine(source, "one.txt"), "unchanged");
        File.WriteAllText(Path.Combine(source, "two.txt"), "unchanged");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(message => { if (message.Contains("current file:")) cancellation.Cancel(); });
        Assert.ThrowsException<OperationCanceledException>(() => new DryRunService().ComparePlan(
            [new MirrorPlanItem { Name = "Workspace", SourcePath = source, DestinationPath = Path.Combine(_root, "target") }],
            progress, cancellation.Token));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "target")));
        Assert.AreEqual(2, Directory.GetFiles(source).Length);
    }

    [TestMethod]
    public void MissingSource_LogsWarningAndEmptyFolderLogsCompletion()
    {
        var messages = new List<string>();
        var result = new DryRunService().ComparePlan(
            [new MirrorPlanItem { Name = "Missing", SourcePath = Path.Combine(_root, "missing"), DestinationPath = Path.Combine(_root, "target") }],
            new InlineProgress(messages.Add));
        Assert.AreEqual(1, result.Warnings.Count);
        Assert.IsTrue(messages.Any(x => x.StartsWith("Warning: Missing:")));
        new DryRunService().CompareMirror(_root, Path.Combine(_root, "target"), progress: new InlineProgress(messages.Add));
        Assert.IsTrue(messages.Any(x => x.Contains("Scanning source finished: 0")));
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
