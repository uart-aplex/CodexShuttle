using System.Security.Cryptography;
using System.Text.Json;
using CodexShuttle.Core.Models;
using CodexShuttle.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class RestoreRolloutRegressionTests
{
    [DataTestMethod]
    [DataRow(@"c:\users\officeuser\.codex", false)]
    [DataRow(@"\\?\C:\Users\OfficeUser\.codex", false)]
    [DataRow(@"\\?\C:\Users\uartc\.codex", false)]
    [DataRow(@"\\?\C:\Users\uartc\.codex", true)]
    public async Task Restore_ResolvesRolloutOnDestination(string indexedHome, bool sameProfile)
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexShuttleTests", Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "package");
        var packagedHome = Path.Combine(package, ".codex");
        var targetProfile = Path.Combine(root, "HomeUser");
        var targetHome = Path.Combine(targetProfile, ".codex");
        const string relativeRollout = @"sessions\2026\06\30\rollout-2026-06-30T14-08-12-019f1724-6040-7122-a711-340c84a27af0.jsonl";
        var sourceRollout = Path.Combine(packagedHome, relativeRollout);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceRollout)!);
        await File.WriteAllTextAsync(sourceRollout, JsonSerializer.Serialize(new { cwd = @"E:\CodexWorkspace", id = "test" }) + "\n");
        var state = Path.Combine(packagedHome, "state_5.sqlite");
        using (var connection = OpenDatabase(state))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE threads(id TEXT PRIMARY KEY, rollout_path TEXT, cwd TEXT); " +
                                  @"INSERT INTO threads VALUES ('test', $path, 'E:\CodexWorkspace');";
            command.Parameters.AddWithValue("$path", indexedHome + "\\" + relativeRollout);
            command.ExecuteNonQuery();
        }

        var manifest = new BackupManifest
        {
            SourceUserProfile = sameProfile ? targetProfile : @"C:\Users\OfficeUser",
            CodexHome = sameProfile ? targetHome : @"C:\Users\OfficeUser\.codex",
            CodexHomeExists = true,
            CredentialsExcluded = true
        };
        try
        {
            await SealPackageAsync(package, manifest);
            var packageHash = SHA256.HashData(await File.ReadAllBytesAsync(state));
            var restore = new RestoreService(new FileMirrorService(), new ManifestService(),
                new RestorePathResolver(targetHome, targetProfile));
            var result = await restore.RestoreAsync(package, new RestoreOptions());
            Assert.IsTrue(result.Success, result.Message + string.Join("\n", result.Errors));
            using var restored = OpenDatabase(Path.Combine(targetHome, "state_5.sqlite"));
            using var read = restored.CreateCommand();
            read.CommandText = "SELECT rollout_path FROM threads WHERE id='test'";
            var path = (string)read.ExecuteScalar()!;
            Assert.IsTrue(File.Exists(path), "Restored rollout path does not exist: " + path);
            Assert.AreEqual(Path.Combine(targetHome, relativeRollout), path.Replace(@"\\?\", ""), true);
            read.CommandText = "SELECT cwd FROM threads WHERE id='test'";
            Assert.AreEqual(@"E:\CodexWorkspace", read.ExecuteScalar());
            CollectionAssert.AreEqual(packageHash, SHA256.HashData(await File.ReadAllBytesAsync(state)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    internal static SqliteConnection OpenDatabase(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    [TestMethod]
    public async Task Repair_ExistingUartProfile_FixesUartcIndexWithoutCopyingSessions()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexShuttleTests", Guid.NewGuid().ToString("N"), "uart", ".codex");
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        var session = Path.Combine(root, "sessions", "rollout-test.jsonl");
        await File.WriteAllTextAsync(session, "{\"cwd\":\"E:\\\\CodexWorkspace\"}\n");
        var bytes = await File.ReadAllBytesAsync(session);
        var stamp = File.GetLastWriteTimeUtc(session);
        try
        {
            using (var db = OpenDatabase(Path.Combine(root, "state_5.sqlite")))
            {
                using var create = db.CreateCommand();
                create.CommandText = @"CREATE TABLE threads(id TEXT PRIMARY KEY, rollout_path TEXT);
                    INSERT INTO threads VALUES ('test', '\\?\C:\Users\uartc\.codex\sessions\rollout-test.jsonl');";
                create.ExecuteNonQuery();
            }
            var result = await new RolloutPathService().CheckAsync(root, true);
            Assert.IsTrue(result.Success, result.Message);
            using var connection = OpenDatabase(Path.Combine(root, "state_5.sqlite"));
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT rollout_path FROM threads";
            Assert.AreEqual(session, query.ExecuteScalar());
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(session));
            Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(session));
            var repeat = await new RolloutPathService().CheckAsync(root, true);
            Assert.IsTrue(repeat.Success, repeat.Message);
            StringAssert.Contains(repeat.Message, "repaired 0");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Restore_MissingRollout_StopsBeforeOverwritingDestination()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexShuttleTests", Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "package");
        var profile = Path.Combine(package, ".codex");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(target);
        var destinationState = Path.Combine(target, "state_5.sqlite");
        await File.WriteAllTextAsync(destinationState, "existing-data-must-survive");
        using (var connection = OpenDatabase(Path.Combine(profile, "state_5.sqlite")))
        {
            using var create = connection.CreateCommand();
            create.CommandText = @"CREATE TABLE threads(rollout_path TEXT);
                INSERT INTO threads VALUES ('C:\Users\uartc\.codex\sessions\missing.jsonl');";
            create.ExecuteNonQuery();
        }
        try
        {
            await SealPackageAsync(package, new BackupManifest { CodexHomeExists = true, CredentialsExcluded = true });
            var result = await new RestoreService(new FileMirrorService(), new ManifestService(), new RestorePathResolver(target))
                .RestoreAsync(package, new RestoreOptions());
            Assert.IsFalse(result.Success);
            StringAssert.Contains(string.Join("\n", result.Errors), "missing.jsonl");
            Assert.AreEqual("existing-data-must-survive", await File.ReadAllTextAsync(destinationState));
        }
        finally { Directory.Delete(root, true); }
    }

    internal static async Task SealPackageAsync(string package, BackupManifest manifest)
    {
        await new ManifestService().SaveAsync(manifest, Path.Combine(package, "manifest.json"));
        var checksums = Path.Combine(package, "checksums.sha256");
        await new ChecksumService().SaveSha256FileAsync(package, [".codex", "manifest.json"], checksums);
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(checksums)));
        await File.WriteAllTextAsync(Path.Combine(package, BackupService.CompleteMarkerFileName), "ChecksumFileSha256=" + hash);
    }
}
