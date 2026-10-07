using CodexShuttle.Core.Models;
using CodexShuttle.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class BackupRestoreIntegrationTests
{
    [TestMethod]
    public async Task FirstBackup_VerificationFails_DoesNotLeaveCompletionMarker()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexShuttleTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source", ".codex");
        var package = Path.Combine(root, "package");
        Directory.CreateDirectory(Path.Combine(source, "sessions"));
        File.WriteAllText(Path.Combine(source, "sessions", "test.jsonl"), "{}");
        try
        {
            var backup = new BackupService(new CodexDetector([], source), new CodexInspector(),
                new FileMirrorService(), new ManifestService(), new ReportService());
            var result = await backup.CreateBackupAsync(package, false, progress: new InlineProgress(message =>
            {
                if (message == "Verifying this backup from disk before committing...")
                    File.WriteAllText(Path.Combine(package, ".codex", "sessions", "test.jsonl"), "corrupted");
            }));
            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.Message, "no complete backup is available");
            Assert.IsFalse(File.Exists(Path.Combine(package, BackupService.CompleteMarkerFileName)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task BackupThenHistoryAndToolsRestore_MigratesHistoryAndToolsButPreservesCredentialsAndConfig()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexShuttleTests", Guid.NewGuid().ToString("N"));
        var sourceCodex = Path.Combine(root, "source", ".codex");
        var sourceWorkspace = Path.Combine(root, "source", "workspace");
        var package = Path.Combine(root, "transfer", "CodexShuttle-Current");
        var targetCodex = Path.Combine(root, "target", ".codex");
        var targetWorkspace = Path.Combine(root, "target", "workspace");
        Directory.CreateDirectory(Path.Combine(sourceCodex, "sessions"));
        Directory.CreateDirectory(Path.Combine(sourceCodex, "skills", "sample-skill"));
        Directory.CreateDirectory(Path.Combine(sourceCodex, "plugins", "cache", "sample-plugin"));
        Directory.CreateDirectory(sourceWorkspace);
        Directory.CreateDirectory(targetCodex);
        File.WriteAllText(Path.Combine(sourceCodex, "sessions", "session.jsonl"), "{}");
        File.WriteAllText(Path.Combine(sourceCodex, "skills", "sample-skill", "SKILL.md"), "# Sample");
        File.WriteAllText(Path.Combine(sourceCodex, "plugins", "cache", "sample-plugin", "plugin.json"), "{}");
        File.WriteAllText(Path.Combine(sourceCodex, "session_index.jsonl"), "{\"id\":\"test\"}");
        File.WriteAllText(Path.Combine(sourceCodex, ".codex-global-state.json"), "{\"ok\":true}");
        File.WriteAllText(Path.Combine(sourceCodex, "config.toml"), "model = 'source'");
        File.WriteAllText(Path.Combine(sourceCodex, "auth.json"), "source-secret");
        File.WriteAllText(Path.Combine(sourceWorkspace, "project.txt"), "project-data");
        File.WriteAllText(Path.Combine(targetCodex, "config.toml"), "model = 'target'");
        File.WriteAllText(Path.Combine(targetCodex, "auth.json"), "target-secret");
        CreateSqlite(Path.Combine(sourceCodex, "state_5.sqlite"));

        try
        {
            var detector = new CodexDetector([sourceWorkspace], sourceCodex);
            var mirror = new FileMirrorService();
            var manifest = new ManifestService();
            var backup = new BackupService(detector, new CodexInspector(), mirror, manifest, new ReportService());

            var backupResult = await backup.CreateBackupAsync(package, includeAppData: false);

            Assert.IsTrue(backupResult.Success, string.Join(Environment.NewLine, backupResult.Errors));
            Assert.IsFalse(File.Exists(Path.Combine(package, ".codex", "auth.json")));
            Assert.IsTrue(File.Exists(Path.Combine(package, "checksums.sha256")));
            Assert.IsTrue(File.Exists(Path.Combine(package, BackupService.CompleteMarkerFileName)));

            File.WriteAllText(Path.Combine(sourceCodex, "sessions", "session.jsonl"), "{\"updated\":true}");
            File.WriteAllText(Path.Combine(sourceWorkspace, "project.txt"), "project-data-v2");
            var differentialResult = await backup.CreateBackupAsync(package, includeAppData: false);
            Assert.IsTrue(differentialResult.Success, string.Join(Environment.NewLine, differentialResult.Errors));
            Assert.AreEqual("{\"updated\":true}", File.ReadAllText(Path.Combine(package, ".codex", "sessions", "session.jsonl")));
            Assert.IsFalse(Directory.Exists(package + ".rollback"));

            var previousMarker = File.ReadAllText(Path.Combine(package, BackupService.CompleteMarkerFileName));
            var previousManifest = await manifest.LoadAsync(Path.Combine(package, "manifest.json"));
            Assert.IsFalse(string.IsNullOrWhiteSpace(previousManifest!.BackupRunId));
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
                new BackupCompletionVerifier().VerifyAsync(package, Guid.NewGuid().ToString("N")));

            File.WriteAllText(Path.Combine(sourceCodex, "sessions", "new-session.jsonl"), "{}");
            var failedUpdate = await backup.CreateBackupAsync(package, false, progress: new InlineProgress(message =>
            {
                if (message == "Verifying this backup from disk before committing...")
                    File.WriteAllText(Path.Combine(package, ".codex", "sessions", "new-session.jsonl"), "corrupted");
            }));
            Assert.IsFalse(failedUpdate.Success);
            StringAssert.Contains(failedUpdate.Message, "no new backup was saved");
            StringAssert.Contains(failedUpdate.Message, "Previous backup retained:");
            Assert.AreEqual(previousMarker, File.ReadAllText(Path.Combine(package, BackupService.CompleteMarkerFileName)));
            Assert.IsFalse(File.Exists(Path.Combine(package, ".codex", "sessions", "new-session.jsonl")));
            await new BackupCompletionVerifier().VerifyAsync(package, previousManifest.BackupRunId);
            Assert.IsFalse(File.Exists(Path.Combine(package, BackupService.InProgressMarkerFileName)));

            using var cancel = new CancellationTokenSource();
            var canceledUpdate = await backup.CreateBackupAsync(package, false, cancel.Token, new InlineProgress(message =>
            {
                if (message == "Verifying this backup from disk before committing...") cancel.Cancel();
            }));
            Assert.IsFalse(canceledUpdate.Success);
            StringAssert.Contains(canceledUpdate.Message, "Backup canceled");
            await new BackupCompletionVerifier().VerifyAsync(package, previousManifest.BackupRunId);

            var failedCommit = await backup.CreateBackupAsync(package, false, progress: new InlineProgress(message =>
            {
                if (message == "Committing verified backup...")
                    Directory.CreateDirectory(Path.Combine(package + ".rollback", "committed.marker"));
            }));
            Assert.IsFalse(failedCommit.Success, "A written completion marker must not hide a commit failure.");
            StringAssert.Contains(failedCommit.Message, "Previous backup retained:");
            await new BackupCompletionVerifier().VerifyAsync(package, previousManifest.BackupRunId);

            var loadedManifest = await manifest.LoadAsync(Path.Combine(package, "manifest.json"));
            Assert.IsNotNull(loadedManifest);
            var restore = new RestoreService(mirror, manifest, new RestorePathResolver(targetCodex));
            var restoreResult = await restore.RestoreAsync(package, new RestoreOptions
            {
                MigrationMode = MigrationMode.HistoryAndTools,
                // The integration sandbox cannot recreate the source at the same absolute path.
                RequireOriginalWorkspacePaths = false,
                WorkspaceTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [sourceWorkspace] = targetWorkspace
                }
            });

            Assert.IsTrue(restoreResult.Success, string.Join(Environment.NewLine, restoreResult.Errors));
            Assert.IsTrue(File.Exists(Path.Combine(targetCodex, "sessions", "session.jsonl")));
            Assert.IsTrue(File.Exists(Path.Combine(targetCodex, "skills", "sample-skill", "SKILL.md")));
            Assert.IsTrue(File.Exists(Path.Combine(targetCodex, "plugins", "cache", "sample-plugin", "plugin.json")));
            Assert.AreEqual("target-secret", File.ReadAllText(Path.Combine(targetCodex, "auth.json")));
            Assert.AreEqual("model = 'target'", File.ReadAllText(Path.Combine(targetCodex, "config.toml")));
            Assert.AreEqual("project-data-v2", File.ReadAllText(Path.Combine(targetWorkspace, "project.txt")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void CreateSqlite(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE state (id INTEGER PRIMARY KEY, value TEXT); INSERT INTO state(value) VALUES ('ok');";
        command.ExecuteNonQuery();
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
