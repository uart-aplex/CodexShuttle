using CodexShuttle.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class CodexInspectorTests
{
    [TestMethod]
    public void Inspect_WhenCodexHomeMissing_ReturnsWarning()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var inspector = new CodexInspector();

        var result = inspector.Inspect(missingPath);

        Assert.IsFalse(result.CodexHomeExists);
        Assert.AreEqual(1, result.Warnings.Count);
    }

    [TestMethod]
    public void Inspect_WhenCoreFilesExist_ReturnsReadableStatus()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexShuttleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var sessions = Path.Combine(root, "sessions");
            Directory.CreateDirectory(sessions);
            File.WriteAllText(Path.Combine(sessions, "session.jsonl"), "{}");
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "state_5.sqlite")};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE state (id INTEGER PRIMARY KEY, value TEXT); INSERT INTO state(value) VALUES ('ok');";
                command.ExecuteNonQuery();
            }
            File.WriteAllText(Path.Combine(root, "session_index.jsonl"), "{\"id\":\"test\"}");
            File.WriteAllText(Path.Combine(root, ".codex-global-state.json"), "{\"ok\":true}");
            File.WriteAllText(Path.Combine(root, "config.toml"), "model = \"test\"");

            var inspector = new CodexInspector();
            var result = inspector.Inspect(root);

            Assert.IsTrue(result.CodexHomeExists);
            Assert.IsTrue(result.SessionsFolderExists);
            Assert.AreEqual(1, result.SessionsFileCount);
            Assert.IsTrue(result.StateSqliteExists);
            Assert.IsTrue(result.StateSqliteReadable);
            Assert.IsTrue(result.SessionIndexExists);
            Assert.IsTrue(result.SessionIndexReadable);
            Assert.IsTrue(result.GlobalStateExists);
            Assert.IsTrue(result.GlobalStateJsonValid);
            Assert.IsTrue(result.ConfigTomlExists);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
