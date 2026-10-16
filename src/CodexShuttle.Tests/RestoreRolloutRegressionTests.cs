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

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task CompressedRollout_UsesLogicalPath_AndPreservesCompressedBytes(bool indexedCompressed, bool archived)
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexShuttleTests", Guid.NewGuid().ToString("N"));
        var physical = Path.Combine(root, archived ? "archived_sessions" : "sessions", "rollout-compressed.jsonl.zst");
        Directory.CreateDirectory(Path.GetDirectoryName(physical)!);
        // Minimal empty Zstandard frame; path resolution never decompresses or rewrites it.
        byte[] bytes = [0x28, 0xb5, 0x2f, 0xfd, 0x20, 0, 1, 0, 0];
        await File.WriteAllBytesAsync(physical, bytes);
        using (var db = OpenDatabase(Path.Combine(root, "state_5.sqlite")))
        using (var command = db.CreateCommand())
        {
            command.CommandText = "CREATE TABLE threads(rollout_path TEXT); INSERT INTO threads VALUES($path)";
            command.Parameters.AddWithValue("$path", @"\\?\C:\Users\uartc\.codex\sessions\rollout-compressed.jsonl" + (indexedCompressed ? ".zst" : ""));
            command.ExecuteNonQuery();
        }
        try
        {
            var service = new RolloutPathService();
            Assert.IsTrue((await service.CheckAsync(root, false)).Success);
            Assert.IsTrue((await service.CheckAsync(root, true)).Success);
            Assert.IsTrue((await service.CheckAsync(root, false, requireResolvedPaths: true)).Success);
            using var db = OpenDatabase(Path.Combine(root, "state_5.sqlite"));
            using var query = db.CreateCommand();
            query.CommandText = "SELECT rollout_path FROM threads";
            Assert.AreEqual(physical[..^4], query.ExecuteScalar());
            Assert.IsFalse(File.Exists(physical[..^4]));
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(physical));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CompressedFallback_RejectsAmbiguousArchiveMatches()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexShuttleTests", Guid.NewGuid().ToString("N"));
        foreach (var folder in new[] { "one", "two" })
        {
            var directory = Path.Combine(root, "archived_sessions", folder);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "rollout-duplicate.jsonl.zst"), [1]);
        }
        using (var db = OpenDatabase(Path.Combine(root, "state_5.sqlite")))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE threads(rollout_path TEXT); INSERT INTO threads VALUES('C:/old/sessions/rollout-duplicate.jsonl')";
            cmd.ExecuteNonQuery();
        }
        try { Assert.IsFalse((await new RolloutPathService().CheckAsync(root, true)).Success); }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task PlainAndCompressedSiblings_ResolveAsOneLogicalRollout()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexShuttleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        var plain = Path.Combine(root, "sessions", "rollout-pair.jsonl");
        File.WriteAllText(plain, "plain content");
        File.WriteAllBytes(plain + ".zst", [1, 2]);
        using (var db = OpenDatabase(Path.Combine(root, "state_5.sqlite")))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE threads(rollout_path TEXT); INSERT INTO threads VALUES('C:/old/archived_sessions/rollout-pair.jsonl.zst')";
            cmd.ExecuteNonQuery();
        }
        try
        {
            Assert.IsTrue((await new RolloutPathService().CheckAsync(root, true)).Success);
            using var db = OpenDatabase(Path.Combine(root, "state_5.sqlite"));
            using var query = db.CreateCommand();
            query.CommandText = "SELECT rollout_path FROM threads";
            Assert.AreEqual(plain, query.ExecuteScalar());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task HistoryAndTools_RestoresHistoryDatabaseIncludingCommittedWal()
    {
        using var fixture = new MergeFixture();
        fixture.AddSession(fixture.Source, "history database transfer");
        var seed = Path.Combine(fixture.Root, "history-seed.sqlite");
        using (var history = OpenDatabase(seed))
        {
            using (var command = history.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE history_test(value TEXT); INSERT INTO history_test VALUES('committed history');";
                command.ExecuteNonQuery();
            }
            // Produce a quiescent package fixture with committed data still in WAL.
            foreach (var suffix in new[] { "", "-wal" })
            {
                using var input = new FileStream(seed + suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var output = File.Create(Path.Combine(fixture.Source, "thread_history_1.sqlite" + suffix));
                input.CopyTo(output);
            }
        }
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Source, "thread_history_1.sqlite-wal")));
        await fixture.Package();
        var service = new RestoreService(new FileMirrorService(), new ManifestService(), new RestorePathResolver(fixture.Target, Path.Combine(fixture.Root, "home")));
        var result = await service.RestoreAsync(fixture.PackageRoot, new RestoreOptions());
        Assert.IsTrue(result.Success, result.Message + string.Join(";", result.Errors));
        var verified = await new ChecksumService().VerifySha256FileAsync(fixture.PackageRoot, Path.Combine(fixture.PackageRoot, "checksums.sha256"));
        Assert.IsTrue(verified.Success, "Restore must leave the source package checksums valid: " + string.Join(";", verified.Errors));
        using var restored = OpenDatabase(Path.Combine(fixture.Target, "thread_history_1.sqlite"));
        using var read = restored.CreateCommand();
        read.CommandText = "SELECT value FROM history_test";
        Assert.AreEqual("committed history", read.ExecuteScalar());
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingOrCorruptPaginatedDatabase_BlocksBeforeOverwrite(bool corrupt)
    {
        using var fixture = new MergeFixture();
        fixture.AddSession(fixture.Source, "paginated metadata");
        using (var source = OpenDatabase(Path.Combine(fixture.Source, "state_5.sqlite")))
        using (var command = source.CreateCommand())
        {
            command.CommandText = "ALTER TABLE threads ADD COLUMN history_mode TEXT NOT NULL DEFAULT 'paginated'";
            command.ExecuteNonQuery();
        }
        if (corrupt) File.WriteAllText(Path.Combine(fixture.Source, "thread_history_1.sqlite"), "corrupt SQLite");
        await fixture.Package();
        var before = SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture.Target, "state_5.sqlite")));
        var service = new RestoreService(new FileMirrorService(), new ManifestService(), new RestorePathResolver(fixture.Target));
        Assert.IsFalse((await service.RestoreAsync(fixture.PackageRoot, new RestoreOptions())).Success);
        CollectionAssert.AreEqual(before, SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture.Target, "state_5.sqlite"))));
    }
}
