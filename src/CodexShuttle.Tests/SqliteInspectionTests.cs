using System.Security.Cryptography;
using CodexShuttle.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class SqliteInspectionTests
{
    [DataTestMethod]
    [DataRow("state_5.sqlite", true)]
    [DataRow("state_5.sqlite", false)]
    [DataRow("sqlite/state_5.sqlite", true)]
    [DataRow("sqlite/state_5.sqlite", false)]
    [DataRow("thread_history_1.sqlite", true)]
    [DataRow("thread_history_1.sqlite", false)]
    public async Task RepeatedInspection_PreservesEverySourceByteAndCommittedWal(string relative, bool includeShm)
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexShuttleTests", Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "profile");
        var database = Path.Combine(home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        Directory.CreateDirectory(Path.Combine(home, "sessions"));
        var rollout = Path.Combine(home, "sessions", "rollout-test.jsonl");
        File.WriteAllText(rollout, "{}\n");
        try
        {
            using (var seed = RestoreRolloutRegressionTests.OpenDatabase(Path.Combine(root, "seed.sqlite")))
            {
                using var command = seed.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE threads(rollout_path TEXT); INSERT INTO threads VALUES($path)";
                command.Parameters.AddWithValue("$path", rollout);
                command.ExecuteNonQuery();
                foreach (var suffix in new[] { "", "-wal" })
                {
                    using var input = new FileStream(Path.Combine(root, "seed.sqlite") + suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var output = File.Create(database + suffix);
                    input.CopyTo(output);
                }
            }
            // A derived cache requiring regeneration must never be touched in the package.
            if (includeShm) File.WriteAllBytes(database + "-shm", new byte[32768]);
            var before = Fingerprints(home);
            var stamps = before.Keys.ToDictionary(path => path, File.GetLastWriteTimeUtc);
            var checker = new RolloutPathService();
            for (var run = 0; run < 2; run++)
            {
                var result = await checker.CheckAsync(home, false);
                Assert.IsTrue(result.Success, result.Message + string.Join(";", result.Errors));
                if (relative != "thread_history_1.sqlite") StringAssert.Contains(result.Message, "Verified 1 conversation");
            }
            string temporary;
            using (var copy = await SqliteInspectionCopy.CreateAsync(database, CancellationToken.None))
            {
                temporary = copy.DatabasePath;
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = temporary, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                connection.Open();
                using var query = connection.CreateCommand();
                query.CommandText = "SELECT rollout_path FROM threads";
                Assert.AreEqual(rollout, query.ExecuteScalar(), "The committed WAL must be included, not ignored.");
            }
            Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(temporary)));
            var after = Fingerprints(home);
            CollectionAssert.AreEquivalent(before.Keys.ToArray(), after.Keys.ToArray());
            foreach (var path in before.Keys)
            {
                Assert.AreEqual(before[path], after[path], path);
                Assert.AreEqual(stamps[path], File.GetLastWriteTimeUtc(path), path);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Inspection_RejectsAnActiveWriter()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexShuttleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "test.sqlite");
            using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            await Assert.ThrowsExceptionAsync<IOException>(() => SqliteInspectionCopy.CreateAsync(path, CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    private static Dictionary<string, string> Fingerprints(string home) => Directory.GetFiles(home, "*", SearchOption.AllDirectories)
        .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
}
