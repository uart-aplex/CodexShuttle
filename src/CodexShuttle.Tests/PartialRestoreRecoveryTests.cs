using System.Security.Cryptography;
using CodexShuttle.Core.Models;
using CodexShuttle.Core.Services;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class PartialRestoreRecoveryTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LockedPlugin_CopyFailsButCompleteHistoryPathsAreRecovered(bool compressed)
    {
        using var f = new Fixture(compressed);
        await f.Seal();
        var plugin = Path.Combine(f.Target, "plugins", "busy.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(plugin)!);
        await File.WriteAllTextAsync(plugin, "previous plugin");
        using var locked = new FileStream(plugin, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = await f.Restore().RestoreAsync(f.Package, new RestoreOptions { MigrationMode = MigrationMode.FullProfile });
        Assert.IsFalse(result.Success, "Partial restore must never be reported as completed.");
        StringAssert.Contains(result.Message, "Robocopy failed");
        Assert.IsTrue(result.Errors.Any(error => error.Contains("busy.exe")), "Retain the failed file, not just the final copy summary.");
        StringAssert.Contains(result.Message, "Conversation index paths recovered");
        Assert.AreEqual(Path.Combine(f.Target, "sessions", "test.jsonl"), f.IndexedPath());
        Assert.IsTrue((await new ChecksumService().VerifySha256FileAsync(f.Package, Path.Combine(f.Package, "checksums.sha256"))).Success);
    }

    [TestMethod]
    public async Task FailedConversationCopy_DoesNotRepairMixedHistory()
    {
        using var f = new Fixture(false);
        await f.Seal();
        var session = Path.Combine(f.Target, "sessions", "test.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(session)!);
        await File.WriteAllTextAsync(session, "incomplete destination conversation");
        using var locked = new FileStream(session, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = await f.Restore().RestoreAsync(f.Package, new RestoreOptions { MigrationMode = MigrationMode.FullProfile });
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, "Automatic path recovery skipped");
        Assert.AreEqual(Fixture.OldPath, f.IndexedPath());
    }

    [DataTestMethod]
    [DataRow("missing-session")]
    [DataRow("changed-session")]
    [DataRow("extra-wal")]
    public async Task Recovery_RejectsIncompleteHistoryBeforeChangingIndex(string problem)
    {
        using var f = new Fixture(false);
        foreach (var source in Directory.EnumerateFiles(f.Source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(f.Target, Path.GetRelativePath(f.Source, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
        var session = Path.Combine(f.Target, "sessions", "test.jsonl");
        if (problem == "missing-session") File.Delete(session);
        if (problem == "changed-session") await File.WriteAllTextAsync(session, "different");
        if (problem == "extra-wal") await File.WriteAllTextAsync(Path.Combine(f.Target, "state_5.sqlite-wal"), "unexpected WAL");
        var state = Path.Combine(f.Target, "state_5.sqlite");
        var before = SHA256.HashData(await File.ReadAllBytesAsync(state));
        var result = await PartialRestorePathRecovery.RepairAsync(f.Source, f.Target, default, null);
        Assert.IsFalse(result.Success);
        CollectionAssert.AreEqual(before, SHA256.HashData(await File.ReadAllBytesAsync(state)));
    }

    [TestMethod]
    public async Task RunningPlugin_BlocksRestoreBeforeAnyDestinationCopy()
    {
        using var f = new Fixture(false);
        await f.Seal();
        Directory.CreateDirectory(f.Target);
        var sentinel = Path.Combine(f.Target, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "local-only data");
        var guard = new RestoreProcessGuard(() => [new(42, Path.Combine(f.Target, "plugins", "latest", "extension-host.exe"))]);
        var result = await f.Restore(guard).RestoreAsync(f.Package, new RestoreOptions { MigrationMode = MigrationMode.FullProfile });
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, "Restore blocked");
        StringAssert.Contains(result.Errors.Single(), "PID 42");
        Assert.AreEqual("local-only data", await File.ReadAllTextAsync(sentinel));
        Assert.IsFalse(File.Exists(Path.Combine(f.Target, "state_5.sqlite")));
    }

    [TestMethod]
    public void ProcessGuard_DoesNotConfuseSiblingProfiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "home", ".codex");
        var guard = new RestoreProcessGuard(() => [new(42, root + "-other\\extension-host.exe")]);
        Assert.IsTrue(guard.Check([new MirrorPlanItem { DestinationPath = root }]).Success);
    }

    [DataTestMethod]
    [DataRow(MigrationMode.FullProfile, true)]
    [DataRow(MigrationMode.FullProfile, false)]
    [DataRow(MigrationMode.HistoryAndTools, true)]
    [DataRow(MigrationMode.HistoryAndTools, false)]
    public async Task ActiveChromeCache_IsKeptWhileHistoryAndOtherPluginsRestore(MigrationMode mode, bool sourceHasChrome)
    {
        using var f = new Fixture(false);
        var relative = Path.Combine("plugins", "cache", "openai-bundled", "chrome", "latest");
        var executable = Path.Combine(relative, "extension-host", "windows", "x64", "extension-host.exe");
        var targetExe = Path.Combine(f.Target, executable);
        Directory.CreateDirectory(Path.GetDirectoryName(targetExe)!);
        File.WriteAllText(targetExe, "running local binary");
        var localConfig = Path.Combine(f.Target, relative, "config.json");
        File.WriteAllText(localConfig, "C:\\Users\\ShuttleSource\\local-only chrome config");
        if (sourceHasChrome)
        {
            var sourceExe = Path.Combine(f.Source, executable);
            Directory.CreateDirectory(Path.GetDirectoryName(sourceExe)!);
            File.WriteAllText(sourceExe, "different incoming binary");
            File.WriteAllText(Path.Combine(f.Source, relative, "config.json"), "incoming chrome config");
            File.WriteAllText(Path.Combine(f.Source, relative, "incoming-only.txt"), "not installed while active");
        }
        await f.Seal();
        var guard = new RestoreProcessGuard(() => [new(42, targetExe)]);
        var options = new RestoreOptions { MigrationMode = mode };
        var resolver = new RestorePathResolver(f.Target, Path.GetDirectoryName(f.Target));
        var manifest = (await new ManifestService().LoadAsync(Path.Combine(f.Package, "manifest.json")))!;
        var prepared = guard.PreserveActiveBrowserCache(new RestorePlanner(resolver).CreatePlan(f.Package, manifest, options), f.Target);
        Assert.IsTrue(guard.Check(prepared.Plan).Success);
        var dryRun = new DryRunService().ComparePlan(prepared.Plan);
        Assert.IsFalse(dryRun.Operations.Any(operation => RestoreProcessGuard.IsWithin(Path.Combine(f.Target, relative), operation.DestinationPath)));
        using var locked = new FileStream(targetExe, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = await f.Restore(guard).RestoreAsync(f.Package, options);
        Assert.IsTrue(result.Success, result.Message + string.Join(";", result.Errors));
        StringAssert.Contains(result.Message, "Chrome plugin cache was kept locally");
        Assert.IsTrue(result.Warnings.Any(warning => warning.Contains("not restored or path-remapped")));
        Assert.AreEqual("running local binary", File.ReadAllText(targetExe));
        Assert.AreEqual("C:\\Users\\ShuttleSource\\local-only chrome config", File.ReadAllText(localConfig));
        Assert.IsFalse(File.Exists(Path.Combine(f.Target, relative, "incoming-only.txt")));
        Assert.AreEqual("incoming plugin, deliberately different size", File.ReadAllText(Path.Combine(f.Target, "plugins", "busy.exe")));
        Assert.AreEqual(Path.Combine(f.Target, "sessions", "test.jsonl"), f.IndexedPath());
        Assert.IsTrue((await new ChecksumService().VerifySha256FileAsync(f.Package, Path.Combine(f.Package, "checksums.sha256"))).Success);
    }

    [TestMethod]
    public async Task InactiveChromeCache_IsRestoredNormally()
    {
        using var f = new Fixture(false);
        var relative = Path.Combine("plugins", "cache", "openai-bundled", "chrome", "latest", "extension-host", "windows", "x64", "extension-host.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(f.Source, relative))!);
        File.WriteAllText(Path.Combine(f.Source, relative), "incoming binary");
        await f.Seal();
        var result = await f.Restore(new RestoreProcessGuard(() => [])).RestoreAsync(f.Package, new RestoreOptions { MigrationMode = MigrationMode.FullProfile });
        Assert.IsTrue(result.Success, result.Message + string.Join(";", result.Errors));
        Assert.AreEqual("incoming binary", File.ReadAllText(Path.Combine(f.Target, relative)));
        Assert.IsFalse(result.Message.Contains("cache was kept"));
    }

    [DataTestMethod]
    [DataRow("plugins/cache/other/chrome/latest/extension-host/windows/x64/extension-host.exe")]
    [DataRow("plugins/cache/openai-bundled/chrome-other/latest/extension-host/windows/x64/extension-host.exe")]
    [DataRow("plugins/cache/openai-bundled/chrome/latest/extension-host/windows/x64/unknown.exe")]
    [DataRow("plugins/extension-host.exe")]
    [DataRow("codex.exe")]
    [DataRow("CodexShuttle.App.exe")]
    public void UnknownExecutable_IsNotExemptedByNameOrSimilarDirectory(string relative)
    {
        using var f = new Fixture(false);
        var guard = new RestoreProcessGuard(() => [new(42, Path.Combine(f.Target, relative.Replace('/', Path.DirectorySeparatorChar)))]);
        var plan = guard.PreserveActiveBrowserCache([new MirrorPlanItem { SourcePath = f.Source, DestinationPath = f.Target }], f.Target);
        Assert.AreEqual(0, plan.Warnings.Count);
        Assert.IsFalse(guard.Check(plan.Plan).Success);
    }

    [TestMethod]
    public void NestedExclusion_PreservesOnlyTheExactSubtree()
    {
        using var f = new Fixture(false);
        var options = new FileMirrorOptions { ExcludedDirectories = [Path.Combine("plugins", "cache", "chrome")] };
        Assert.IsTrue(options.ExcludesDirectory(f.Target, Path.Combine(f.Target, "plugins", "cache", "chrome", "host.exe")));
        Assert.IsFalse(options.ExcludesDirectory(f.Target, Path.Combine(f.Target, "plugins", "cache", "chrome-other", "host.exe")));
        Assert.IsFalse(options.ExcludesDirectory(f.Target, Path.Combine(f.Target, "sessions", "chat.jsonl")));
    }

    private sealed class Fixture : IDisposable
    {
        internal const string OldPath = @"C:\Users\ShuttleSource\.codex\sessions\test.jsonl";
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "CodexShuttleTests", Guid.NewGuid().ToString("N"));
        internal string Package => Path.Combine(Root, "package");
        internal string Source => Path.Combine(Package, ".codex");
        internal string Target => Path.Combine(Root, "uartc", ".codex");
        internal Fixture(bool compressed)
        {
            Directory.CreateDirectory(Path.Combine(Source, "sessions"));
            Directory.CreateDirectory(Path.Combine(Source, "plugins"));
            File.WriteAllText(Path.Combine(Source, "sessions", "test.jsonl" + (compressed ? ".zst" : "")), "synthetic rollout bytes");
            File.WriteAllText(Path.Combine(Source, "plugins", "busy.exe"), "incoming plugin, deliberately different size");
            using var db = RestoreRolloutRegressionTests.OpenDatabase(Path.Combine(Source, "state_5.sqlite"));
            using var create = db.CreateCommand();
            create.CommandText = "CREATE TABLE threads(id TEXT PRIMARY KEY, rollout_path TEXT); INSERT INTO threads VALUES ('test', $path)";
            create.Parameters.AddWithValue("$path", OldPath);
            create.ExecuteNonQuery();
        }
        internal Task Seal() => RestoreRolloutRegressionTests.SealPackageAsync(Package, new BackupManifest
        { CodexHomeExists = true, CredentialsExcluded = true, CodexHome = @"C:\Users\ShuttleSource\.codex", SourceUserProfile = @"C:\Users\ShuttleSource" });
        internal RestoreService Restore(RestoreProcessGuard? guard = null) => new(new FileMirrorService(), new ManifestService(),
            new RestorePathResolver(Target, Path.GetDirectoryName(Target)), guard);
        internal string IndexedPath()
        {
            using var db = RestoreRolloutRegressionTests.OpenDatabase(Path.Combine(Target, "state_5.sqlite"));
            using var query = db.CreateCommand();
            query.CommandText = "SELECT rollout_path FROM threads";
            return (string)query.ExecuteScalar()!;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
