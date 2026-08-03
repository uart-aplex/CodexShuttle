using CodexShuttle.Core.Models;
using CodexShuttle.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class BackupRestoreIntegrationTests
{
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
}
