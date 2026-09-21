using CodexShuttle.Core.Models;
using Microsoft.Data.Sqlite;

namespace CodexShuttle.Core.Services;

// Resolve against packaged/restored sessions, not the current machine's old profile.
public sealed class RolloutPathService
{
    public async Task<OperationResult> CheckAsync(
        string codexHome,
        bool repair,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        if (!Directory.Exists(codexHome))
        {
            return OperationResult.Fail("The Codex profile folder is missing.", codexHome);
        }

        var checkedPaths = 0;
        var changedPaths = 0;
        try
        {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in new[] { "sessions", "archived_sessions" })
            {
                var root = Path.Combine(codexHome, directory);
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", new EnumerationOptions
                         { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    files.Add(Path.GetRelativePath(codexHome, file).Replace('\\', '/'), file);
                }
            }
            var byName = files.Values.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key!, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

            foreach (var database in EnumerateStateDatabases(codexHome))
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Checking conversation paths: {database}");
                var attributes = File.GetAttributes(database);
                if (repair && attributes.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(database, attributes & ~FileAttributes.ReadOnly);
                try
                {
                    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = database,
                        Mode = repair ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
                        Pooling = false
                    }.ToString());
                    await connection.OpenAsync(cancellationToken);
                    using var schema = connection.CreateCommand();
                    schema.CommandText = "SELECT count(*) FROM pragma_table_info('threads') WHERE name='rollout_path'";
                    if (Convert.ToInt64(await schema.ExecuteScalarAsync(cancellationToken)) == 0) continue;

                    using var transaction = connection.BeginTransaction(deferred: !repair);
                    var fixes = new Dictionary<string, string>(StringComparer.Ordinal);
                    using (var query = connection.CreateCommand())
                    {
                        query.Transaction = transaction;
                        query.CommandText = "SELECT DISTINCT rollout_path FROM threads WHERE rollout_path IS NOT NULL AND rollout_path <> ''";
                        using var reader = await query.ExecuteReaderAsync(cancellationToken);
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            var original = reader.GetString(0);
                            var target = Resolve(original, files, byName);
                            if (target is null)
                                return OperationResult.Fail("A conversation points to a missing or ambiguous rollout file.",
                                    $"Database: {database}", $"Unresolved rollout: {original}");
                            // Opening verifies access as well as existence; no conversation text is logged.
                            using (File.Open(target, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                            checkedPaths++;
                            if (repair && !Normalize(original).Equals(Normalize(target), StringComparison.OrdinalIgnoreCase))
                                fixes[original] = target;
                        }
                    }

                    foreach (var (original, target) in fixes)
                    {
                        using var update = connection.CreateCommand();
                        update.Transaction = transaction;
                        update.CommandText = "UPDATE threads SET rollout_path=$target WHERE rollout_path=$original";
                        update.Parameters.AddWithValue("$target", target);
                        update.Parameters.AddWithValue("$original", original);
                        changedPaths += await update.ExecuteNonQueryAsync(cancellationToken);
                    }
                    using var check = connection.CreateCommand();
                    check.Transaction = transaction;
                    check.CommandText = "PRAGMA quick_check(1)";
                    if (!string.Equals(Convert.ToString(await check.ExecuteScalarAsync(cancellationToken)), "ok", StringComparison.OrdinalIgnoreCase))
                        return OperationResult.Fail("The conversation database did not pass SQLite validation.", database);
                    transaction.Commit();
                }
                finally
                {
                    if (repair) File.SetAttributes(database, attributes);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return OperationResult.Fail("Conversation path validation failed.", ex.Message);
        }

        return OperationResult.Ok($"Verified {checkedPaths:N0} conversation file paths; repaired {changedPaths:N0} index entries.");
    }

    private static IEnumerable<string> EnumerateStateDatabases(string codexHome)
    {
        foreach (var directory in new[] { codexHome, Path.Combine(codexHome, "sqlite") })
        {
            if (!Directory.Exists(directory)) continue;
            var databases = Directory.GetFiles(directory, "state_*.sqlite");
            foreach (var path in databases) yield return path;
            // The nested location is a legacy fallback, not a second active index.
            if (databases.Length > 0) yield break;
        }
    }

    private static string? Resolve(string original, Dictionary<string, string> files, Dictionary<string, string[]> byName)
    {
        var normalized = Normalize(original);
        // Never resolve a package reference outside its two session directories.
        if (normalized.Split('/').Any(segment => segment is "." or "..")) return null;
        foreach (var prefix in new[] { "sessions/", "archived_sessions/" })
        {
            var index = normalized.LastIndexOf("/" + prefix, StringComparison.OrdinalIgnoreCase);
            var relative = index >= 0 ? normalized[(index + 1)..] : normalized;
            if (relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && files.TryGetValue(relative, out var target)) return target;
        }
        // A session can have moved between sessions and archived_sessions since indexing.
        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        return byName.TryGetValue(name, out var candidates) && candidates.Length == 1 ? candidates[0] : null;
    }

    private static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("//?/", StringComparison.Ordinal) ? normalized[4..] : normalized;
    }
}
