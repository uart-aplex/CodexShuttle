using CodexShuttle.Core.Models;
using CodexShuttle.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class AutomaticPathRepairTests
{
    [DataTestMethod]
    [DataRow(MigrationMode.HistoryAndTools)]
    [DataRow(MigrationMode.FullProfile)]
    public async Task Restore_RepairsBothIndexes_BeforeDesktopCanReconcileThem(MigrationMode mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexShuttleTests", Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "package");
        var source = Path.Combine(package, ".codex");
        var destination = Path.Combine(root, "uart", ".codex");
        Directory.CreateDirectory(Path.Combine(source, "sessions"));
        Directory.CreateDirectory(Path.Combine(source, "sqlite"));
        await File.WriteAllTextAsync(Path.Combine(source, "sessions", "rollout-test.jsonl"), "{}\n");
        CreateIndex(Path.Combine(source, "state_5.sqlite"), 1);
        CreateIndex(Path.Combine(source, "sqlite", "state_5.sqlite"), 2);
        try
        {
            var manifest = new BackupManifest
            {
                CodexHomeExists = true, CredentialsExcluded = true,
                CodexHome = @"C:\Users\OfficeUser\.codex", SourceUserProfile = @"C:\Users\OfficeUser"
            };
            await RestoreRolloutRegressionTests.SealPackageAsync(package, manifest);
            var result = await new RestoreService(new FileMirrorService(), new ManifestService(),
                    new RestorePathResolver(destination, Path.GetDirectoryName(destination)))
                .RestoreAsync(package, new RestoreOptions { MigrationMode = mode });
            Assert.IsTrue(result.Success, result.Message + string.Join("\n", result.Errors));

            var expected = Path.Combine(destination, "sessions", "rollout-test.jsonl");
            using var db = RestoreRolloutRegressionTests.OpenDatabase(Path.Combine(destination, "state_5.sqlite"));
            using var query = db.CreateCommand();
            query.CommandText = "SELECT rollout_path FROM threads";
            Assert.AreEqual(expected, query.ExecuteScalar());
            var legacy = Path.Combine(destination, "sqlite", "state_5.sqlite");
            Assert.IsTrue(File.Exists(legacy), "The legacy index must be included in the restore plan.");
            using var oldDb = RestoreRolloutRegressionTests.OpenDatabase(legacy);
            using var oldQuery = oldDb.CreateCommand();
            oldQuery.CommandText = "SELECT rollout_path FROM threads";
            Assert.AreEqual(expected, oldQuery.ExecuteScalar());

            // Model Desktop startup importing a newer row from the nested index.
            using var reconcile = db.CreateCommand();
            reconcile.CommandText = "UPDATE threads SET rollout_path=$path, updated_at=2 WHERE id='test' AND updated_at<2";
            reconcile.Parameters.AddWithValue("$path", oldQuery.ExecuteScalar()!);
            reconcile.ExecuteNonQuery();
            Assert.AreEqual(expected, query.ExecuteScalar());
            Assert.IsTrue(File.Exists((string)query.ExecuteScalar()!));
            StringAssert.Contains(result.Message, "conversation");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Restore_StillRepairsIndex_WhenAnUnrelatedPluginDatabaseCannotBeRemapped()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexShuttleTests", Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "package");
        var source = Path.Combine(package, ".codex");
        var destination = Path.Combine(root, "uart", ".codex");
        Directory.CreateDirectory(Path.Combine(source, "sessions"));
        Directory.CreateDirectory(Path.Combine(source, "plugins"));
        await File.WriteAllTextAsync(Path.Combine(source, "sessions", "rollout-test.jsonl"), "{}\n");
        await File.WriteAllTextAsync(Path.Combine(source, "plugins", "broken.sqlite"), "SQLite format 3\0invalid database");
        CreateIndex(Path.Combine(source, "state_5.sqlite"), 1);
        try
        {
            await RestoreRolloutRegressionTests.SealPackageAsync(package, new BackupManifest
            {
                CodexHomeExists = true, CredentialsExcluded = true,
                CodexHome = @"C:\Users\uartc\.codex", SourceUserProfile = @"C:\Users\uartc"
            });
            var result = await new RestoreService(new FileMirrorService(), new ManifestService(),
                    new RestorePathResolver(destination, Path.GetDirectoryName(destination)))
                .RestoreAsync(package, new RestoreOptions());
            Assert.IsFalse(result.Success, "A plugin failure must not be reported as a successful restore.");
            using var db = RestoreRolloutRegressionTests.OpenDatabase(Path.Combine(destination, "state_5.sqlite"));
            using var query = db.CreateCommand();
            query.CommandText = "SELECT rollout_path FROM threads";
            Assert.AreEqual(Path.Combine(destination, "sessions", "rollout-test.jsonl"), query.ExecuteScalar());
        }
        finally { Directory.Delete(root, true); }
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Check_OnlySkipsUnavailableLegacyEntries_WhenAlreadyReconciled(bool reconciled, bool primaryMissing)
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexShuttleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        Directory.CreateDirectory(Path.Combine(root, "sqlite"));
        if (!primaryMissing) await File.WriteAllTextAsync(Path.Combine(root, "sessions", "rollout-test.jsonl"), "{}\n");
        CreateIndex(Path.Combine(root, "state_5.sqlite"), 1);
        var legacyPath = Path.Combine(root, "sqlite", "state_5.sqlite");
        CreateIndex(legacyPath, 2);
        using (var legacy = RestoreRolloutRegressionTests.OpenDatabase(legacyPath))
        {
            using var change = legacy.CreateCommand();
            change.CommandText = "UPDATE threads SET rollout_path='C:/Users/uartc/.codex/sessions/missing.jsonl'";
            change.ExecuteNonQuery();
        }
        if (reconciled) await File.WriteAllTextAsync(Path.Combine(root, ".app-server-state-reconciled-v1"), "");
        try
        {
            var result = await new RolloutPathService().CheckAsync(root, true);
            Assert.AreEqual(reconciled && !primaryMissing, result.Success, result.Message);
            if (result.Success) Assert.AreEqual(1, result.Warnings.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task FinalCheck_RejectsUnchangedOldPath_EvenWhenSessionWasCopied()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexShuttleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        await File.WriteAllTextAsync(Path.Combine(root, "sessions", "rollout-test.jsonl"), "{}\n");
        CreateIndex(Path.Combine(root, "state_5.sqlite"), 1);
        try
        {
            var service = new RolloutPathService();
            var unresolved = await service.CheckAsync(root, false, requireResolvedPaths: true);
            Assert.IsFalse(unresolved.Success);
            Assert.IsTrue((await service.CheckAsync(root, true)).Success);
            Assert.IsTrue((await service.CheckAsync(root, false, requireResolvedPaths: true)).Success);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void CreateIndex(string path, int updatedAt)
    {
        using var db = RestoreRolloutRegressionTests.OpenDatabase(path);
        using var create = db.CreateCommand();
        create.CommandText = @"CREATE TABLE threads(id TEXT PRIMARY KEY, rollout_path TEXT, updated_at INTEGER);
            INSERT INTO threads VALUES ('test', '\\?\C:\Users\uartc\.codex\sessions\rollout-test.jsonl', $updated);";
        create.Parameters.AddWithValue("$updated", updatedAt);
        create.ExecuteNonQuery();
    }
}
