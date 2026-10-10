using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexShuttle.Core.Merge;

internal static class MergeDatabase
{
    public static SqliteConnection Open(string path, bool write = false)
    {
        MergeFiles.NoLinks(path);
        if (!File.Exists(path)) throw new InvalidDataException($"Initialize Codex before merge; missing supported database: {path}");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = write ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5, ForeignKeys = true }.ToString());
        connection.Open();
        return connection;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    private static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    public static Dictionary<string, Dictionary<string, object?>> Rows(SqliteConnection connection)
    {
        var rows = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        using var command = Command(connection, "SELECT * FROM threads ORDER BY id");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? null : reader.GetValue(i));
            rows.Add(Convert.ToString(row["id"])!, row);
        }
        return rows;
    }

    public static string Fingerprint(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        var items = new List<string>();
        var tables = new List<string>();
        using (var command = Command(connection, "SELECT type,name,sql FROM sqlite_master ORDER BY type,name", transaction))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var name = reader.GetString(1);
                items.Add(JsonSerializer.Serialize(new object?[] { reader.GetString(0), name, reader.IsDBNull(2) ? null : reader.GetString(2) }));
                if (reader.GetString(0) == "table") tables.Add(name);
            }
        foreach (var table in tables)
        {
            var rows = new List<string>();
            using var command = Command(connection, $"SELECT * FROM {Quote(table)}", transaction);
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray()));
            items.Add(table);
            items.AddRange(rows.Order(StringComparer.Ordinal));
        }
        return MergeFiles.Hash(Encoding.UTF8.GetBytes(string.Join('\n', items)));
    }

    public static Dictionary<string, object?> PrepareRow(SqliteConnection target, Dictionary<string, object?> source, MergeEntry entry, SqliteTransaction? transaction = null)
    {
        ValidateTriggers(target, transaction);
        if (source.TryGetValue("history_mode", out var mode) && Convert.ToString(mode) is not (null or "legacy"))
            throw new InvalidDataException("Only self-contained legacy JSONL history is supported by this merge adapter.");
        var row = new Dictionary<string, object?>();
        using var command = Command(target, "PRAGMA table_info(threads)", transaction);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            if (source.TryGetValue(name, out var value)) row[name] = value;
            if (name == "rollout_path") row[name] = entry.TargetPath;
            if (name == "cwd") row[name] = entry.WorkingDirectory;
            if (name == "preview" && (!row.TryGetValue(name, out var preview) || string.IsNullOrWhiteSpace(Convert.ToString(preview))))
                row[name] = source.TryGetValue("first_user_message", out var first) && !string.IsNullOrWhiteSpace(Convert.ToString(first)) ? first : entry.Title;
            // Cross-device project/parent relations cannot be inferred from local registries.
            if (name is "project_id" or "parent_thread_id" or "thread_section_id" or "section_position" or "section_entered_at_ms") row[name] = null;
            if (name == "is_pinned") row[name] = 0L;
            if (reader.GetInt32(3) != 0 && reader.IsDBNull(4) && (!row.TryGetValue(name, out var required) || required is null))
                throw new InvalidDataException($"Unsupported required threads column: {name}");
        }
        foreach (var key in new[] { "id", "rollout_path", "cwd" })
            if (!row.ContainsKey(key)) throw new InvalidDataException($"Unsupported threads schema: missing {key}");
        if (Convert.ToString(row["id"]) != entry.Id) throw new InvalidDataException("Session and SQLite thread IDs disagree.");
        reader.Close();
        using var foreignKeys = Command(target, "PRAGMA foreign_key_list(threads)", transaction);
        using var keys = foreignKeys.ExecuteReader();
        while (keys.Read())
            if (row.TryGetValue(keys.GetString(3), out var reference) && reference is not null)
                throw new InvalidDataException($"Unsupported cross-profile foreign key: {keys.GetString(3)}");
        return row;
    }

    private static void ValidateTriggers(SqliteConnection target, SqliteTransaction? transaction)
    {
        // Exact known SQL, ignoring whitespace only; names alone cannot establish safety.
        static string Normalize(string sql) => System.Text.RegularExpressions.Regex.Replace(sql, @"\s+", " ").Trim();
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in new[] { "created_at", "updated_at" })
        {
            allowed.Add(Normalize($"CREATE TRIGGER threads_{column}_ms_after_insert AFTER INSERT ON threads WHEN NEW.{column}_ms IS NULL BEGIN UPDATE threads SET {column}_ms = NEW.{column} * 1000 WHERE id = NEW.id; END"));
            allowed.Add(Normalize($"CREATE TRIGGER threads_{column}_ms_after_update AFTER UPDATE OF {column} ON threads WHEN NEW.{column} != OLD.{column} AND NEW.{column}_ms IS OLD.{column}_ms BEGIN UPDATE threads SET {column}_ms = NEW.{column} * 1000 WHERE id = NEW.id; END"));
        }
        allowed.Add(Normalize("CREATE TRIGGER threads_recency_at_after_insert AFTER INSERT ON threads WHEN NEW.recency_at_ms = 0 BEGIN UPDATE threads SET recency_at = NEW.updated_at, recency_at_ms = COALESCE(NEW.updated_at_ms, NEW.updated_at * 1000) WHERE id = NEW.id; END"));
        using var command = Command(target, "SELECT sql FROM sqlite_master WHERE type='trigger' AND tbl_name='threads'", transaction);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (!allowed.Contains(Normalize(reader.GetString(0)))) throw new InvalidDataException("Unrecognized thread trigger; this Codex schema needs a reviewed adapter.");
    }

    public static void Insert(SqliteConnection connection, SqliteTransaction transaction, Dictionary<string, object?> row)
    {
        using var command = Command(connection,
            $"INSERT INTO threads ({string.Join(',', row.Keys.Select(Quote))}) VALUES ({string.Join(',', row.Keys.Select((_, i) => "$p" + i))})", transaction);
        var i = 0;
        foreach (var value in row.Values) command.Parameters.AddWithValue("$p" + i++, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public static void DeleteInserted(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            using var command = Command(connection, "DELETE FROM threads WHERE id=$id", transaction);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public static void Snapshot(SqliteConnection source, string destination)
    {
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        target.Open();
        source.BackupDatabase(target);
        using var command = Command(target, "PRAGMA integrity_check");
        if (Convert.ToString(command.ExecuteScalar()) != "ok") throw new InvalidDataException("SQLite restore point failed integrity verification.");
    }
}
