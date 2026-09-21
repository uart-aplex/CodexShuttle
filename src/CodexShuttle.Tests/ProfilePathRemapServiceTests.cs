using System.Text;
using CodexShuttle.Core.Models;
using CodexShuttle.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class ProfilePathRemapServiceTests
{
    [TestMethod]
    public async Task RemapAsync_DifferentWindowsUser_UpdatesTextAndSqliteButPreservesWorkspaceAndCredentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexShuttleTests", Guid.NewGuid().ToString("N"));
        var packageCodex = Path.Combine(root, "package", ".codex");
        var destinationProfile = Path.Combine(root, "HomeUser");
        var destinationCodex = Path.Combine(destinationProfile, ".codex");
        Directory.CreateDirectory(packageCodex);
        Directory.CreateDirectory(destinationCodex);

        const string sourceProfile = @"C:\Users\OfficeUser";
        const string fixedWorkspace = @"E:\CodexWorkspace\Project";
        var sourceCodex = sourceProfile + @"\.codex";
        var indexContent =
            "raw=\\\\?\\" + sourceCodex + @"\sessions\rollout.jsonl" + Environment.NewLine +
            "json=C:\\\\Users\\\\OfficeUser\\\\.codex\\\\sessions\\\\rollout.jsonl" + Environment.NewLine +
            "slash=C:/Users/OfficeUser/.codex/sessions/rollout.jsonl" + Environment.NewLine +
            "workspace=" + fixedWorkspace;
        var sourceIndex = Path.Combine(packageCodex, "session_index.jsonl");
        var destinationIndex = Path.Combine(destinationCodex, "session_index.jsonl");
        await File.WriteAllTextAsync(sourceIndex, indexContent, new UTF8Encoding(true));
        File.Copy(sourceIndex, destinationIndex);

        var sourceDatabase = Path.Combine(packageCodex, "state_5.sqlite");
        var destinationDatabase = Path.Combine(destinationCodex, "state_5.sqlite");
        CreateSqlite(sourceDatabase, sourceCodex + @"\sessions\rollout.jsonl", fixedWorkspace);
        File.Copy(sourceDatabase, destinationDatabase);

        var sourceAuth = Path.Combine(packageCodex, "auth.json");
        var destinationAuth = Path.Combine(destinationCodex, "auth.json");
        await File.WriteAllTextAsync(sourceAuth, sourceProfile);
        await File.WriteAllTextAsync(destinationAuth, sourceProfile);
        File.SetAttributes(destinationIndex, File.GetAttributes(destinationIndex) | FileAttributes.ReadOnly);
        File.SetAttributes(destinationDatabase, File.GetAttributes(destinationDatabase) | FileAttributes.ReadOnly);

        try
        {
            var manifest = new BackupManifest
            {
                SourceUser = "OfficeUser",
                SourceUserProfile = sourceProfile,
                CodexHome = sourceCodex,
                CodexHomeExists = true
            };
            var resolver = new RestorePathResolver(destinationCodex, destinationProfile);
            var plan = new[]
            {
                new MirrorPlanItem
                {
                    Name = "Codex profile",
                    SourcePath = packageCodex,
                    DestinationPath = destinationCodex,
                    RemapUserProfilePaths = true,
                    MirrorOptions = CodexMigrationPolicy.CredentialOnlyOptions
                }
            };

            var result = await new ProfilePathRemapService(resolver).RemapAsync(manifest, plan);

            Assert.IsTrue(result.Success, string.Join(Environment.NewLine, result.Errors));
            var remappedIndex = await File.ReadAllTextAsync(destinationIndex);
            Assert.IsFalse(remappedIndex.Contains(sourceProfile, StringComparison.OrdinalIgnoreCase));
            StringAssert.Contains(remappedIndex, destinationCodex);
            StringAssert.Contains(remappedIndex, destinationCodex.Replace("\\", "\\\\", StringComparison.Ordinal));
            StringAssert.Contains(remappedIndex, destinationCodex.Replace('\\', '/'));
            StringAssert.Contains(remappedIndex, fixedWorkspace);
            Assert.AreEqual(sourceProfile, await File.ReadAllTextAsync(destinationAuth));
            Assert.IsTrue(File.GetAttributes(destinationIndex).HasFlag(FileAttributes.ReadOnly));
            Assert.IsTrue(File.GetAttributes(destinationDatabase).HasFlag(FileAttributes.ReadOnly));

            await using var connection = new SqliteConnection($"Data Source={destinationDatabase};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT rollout_path, cwd FROM threads WHERE id = 1";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(destinationCodex + @"\sessions\rollout.jsonl", reader.GetString(0));
            Assert.AreEqual(fixedWorkspace, reader.GetString(1));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                }

                Directory.Delete(root, true);
            }
        }
    }

    private static void CreateSqlite(string path, string rolloutPath, string cwd)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE threads (id INTEGER PRIMARY KEY, rollout_path TEXT, cwd TEXT); " +
                              "INSERT INTO threads (rollout_path, cwd) VALUES ($rollout, $cwd);";
        command.Parameters.AddWithValue("$rollout", rolloutPath);
        command.Parameters.AddWithValue("$cwd", cwd);
        command.ExecuteNonQuery();
    }

    [TestMethod]
    public async Task RemapAsync_UartToUartc_DoesNotReplaceOutputTwiceOrAnotherUsersPrefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexShuttleTests", Guid.NewGuid().ToString("N"));
        var sourceProfile = Path.Combine(root, "uart");
        var targetProfile = Path.Combine(root, "uartc");
        var targetHome = Path.Combine(targetProfile, ".codex");
        var sourceHome = Path.Combine(sourceProfile, ".codex");
        Directory.CreateDirectory(targetHome);
        var database = Path.Combine(targetHome, "state_5.sqlite");
        var anotherUser = sourceProfile + @"c\Documents";
        CreateSqlite(database, sourceHome.ToUpperInvariant() + @"\sessions\test.jsonl", anotherUser);
        try
        {
            var service = new ProfilePathRemapService(new RestorePathResolver(targetHome, targetProfile));
            var result = await service.RemapAsync(new BackupManifest { SourceUserProfile = sourceProfile, CodexHome = sourceHome },
                [new MirrorPlanItem { IsDirectory = false, RemapUserProfilePaths = true, DestinationPath = database }]);
            Assert.IsTrue(result.Success, result.Message);
            using var connection = RestoreRolloutRegressionTests.OpenDatabase(database);
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT rollout_path, cwd FROM threads";
            using var reader = query.ExecuteReader();
            Assert.IsTrue(reader.Read());
            Assert.AreEqual(targetHome + @"\sessions\test.jsonl", reader.GetString(0));
            Assert.AreEqual(anotherUser, reader.GetString(1));
        }
        finally { Directory.Delete(root, true); }
    }
}
