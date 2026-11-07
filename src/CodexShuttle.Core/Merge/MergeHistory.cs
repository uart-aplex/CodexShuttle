using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexShuttle.Core.Merge;

// Keep rollout bytes and projection offsets together.
internal static class MergeHistory
{
    internal const string FileName = "thread_history_1.sqlite";
    private static readonly Dictionary<string, string[]> Columns = new()
    {
        ["thread_turns"] = ["thread_id", "turn_id", "rollout_ordinal", "status", "error_json", "started_at", "completed_at", "duration_ms", "first_user_item_id", "final_agent_item_id", "rollout_byte_offset", "rollout_end_ordinal", "rollout_end_byte_offset", "root_turn_id"],
        ["thread_items"] = ["thread_id", "turn_id", "item_id", "rollout_ordinal", "created_at_ms", "item_json", "item_type", "updated_at_ordinal", "started_at_ms", "completed_at_ms"],
        ["thread_history_projection_state"] = ["thread_id", "next_rollout_byte_offset", "next_rollout_ordinal"],
        ["thread_realtime_items"] = ["thread_id", "item_id", "rollout_ordinal", "created_at_ms", "item_type", "item_json"]
    };
    private static readonly Dictionary<string, string> Keys = new()
    {
        ["thread_turns"] = "thread_id,turn_id", ["thread_items"] = "thread_id,turn_id,item_id",
        ["thread_history_projection_state"] = "thread_id", ["thread_realtime_items"] = "thread_id,item_id"
    };
    private static SqliteCommand Command(SqliteConnection db, string sql, SqliteTransaction? tx = null)
    { var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.Transaction = tx; return cmd; }

    internal static void Validate(SqliteConnection db, string schema = "main")
    {
        if (schema is not ("main" or "history")) throw new ArgumentException(nameof(schema));
        var names = new HashSet<string>();
        using (var command = Command(db, $"SELECT name FROM {schema}.sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var name = reader.GetString(0);
                if (name != "_sqlx_migrations" && !Columns.ContainsKey(name)) throw new InvalidDataException($"Unsupported history table: {name}");
                names.Add(name);
            }
        foreach (var (table, columns) in Columns)
        {
            if (!names.Contains(table)) throw new InvalidDataException($"Missing history table: {table}");
            var actual = new List<string>();
            var keys = new SortedDictionary<int, string>();
            using (var command = Command(db, $"PRAGMA {schema}.table_info({table})"))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                { actual.Add(reader.GetString(1)); if (reader.GetInt32(5) > 0) keys.Add(reader.GetInt32(5), reader.GetString(1)); }
            if (!actual.SequenceEqual(columns) || string.Join(',', keys.Values) != Keys[table])
                throw new InvalidDataException($"Unsupported history columns or keys: {table}. No import performed.");
            using var foreignKeys = Command(db, $"PRAGMA {schema}.foreign_key_list({table})");
            using var foreignReader = foreignKeys.ExecuteReader();
            if (foreignReader.Read()) throw new InvalidDataException("History foreign keys require a reviewed adapter.");
        }
        static string Normalize(string sql) => System.Text.RegularExpressions.Regex.Replace(sql, @"\s+", " ").Trim();
        var allowed = Normalize("CREATE TRIGGER thread_realtime_items_projection_cleanup AFTER DELETE ON thread_history_projection_state BEGIN DELETE FROM thread_realtime_items WHERE thread_id = OLD.thread_id; END");
        using (var command = Command(db, $"SELECT sql FROM {schema}.sqlite_master WHERE type='trigger'"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                if (Normalize(reader.GetString(0)) != allowed) throw new InvalidDataException("Unrecognized history trigger.");
        using var integrity = Command(db, $"PRAGMA {schema}.quick_check");
        if (Convert.ToString(integrity.ExecuteScalar()) != "ok") throw new InvalidDataException("History database integrity check failed.");
    }

    internal static string Fingerprint(SqliteConnection db, string schema = "main", string? id = null, SqliteTransaction? tx = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (id is null)
        {
            using var definition = Command(db, $"SELECT type,name,tbl_name,sql FROM {schema}.sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name", tx);
            using var definitions = definition.ExecuteReader();
            while (definitions.Read()) hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(Values(definitions)));
            definitions.Close();
            using var exists = Command(db, $"SELECT COUNT(*) FROM {schema}.sqlite_master WHERE type='table' AND name='_sqlx_migrations'", tx);
            if (Convert.ToInt64(exists.ExecuteScalar()) > 0)
            {
                using var migrations = Command(db, $"SELECT * FROM {schema}._sqlx_migrations ORDER BY version", tx);
                using var reader = migrations.ExecuteReader();
                while (reader.Read()) hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(Values(reader)));
            }
        }
        foreach (var table in Columns.Keys)
        {
            using var command = Command(db, $"SELECT * FROM {schema}.{table}" + (id is null ? "" : " WHERE thread_id=$id") + $" ORDER BY {Keys[table]}", tx);
            if (id is not null) command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(table));
            while (reader.Read())
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(Values(reader));
                hash.AppendData(BitConverter.GetBytes(bytes.Length));
                hash.AppendData(bytes);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static object?[] Values(SqliteDataReader reader) => Enumerable.Range(0, reader.FieldCount)
        .Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray();

    internal static string ContentHash(SqliteConnection db, string id, MergeComparison comparison, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (table, columns) in Columns)
        {
            if (table == "thread_history_projection_state") continue;
            var compared = columns.Where(c => c is not ("rollout_ordinal" or "rollout_byte_offset" or "rollout_end_ordinal" or "rollout_end_byte_offset" or "updated_at_ordinal")).ToArray();
            using var command = Command(db, $"SELECT {string.Join(',', compared)} FROM {table} WHERE thread_id=$id ORDER BY rollout_ordinal,{Keys[table]}");
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            MergeComparison.Append(hash, JsonSerializer.SerializeToUtf8Bytes(new { table, compared }));
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var values = Values(reader);
                for (var i = 0; i < compared.Length; i++)
                {
                    if (!compared[i].EndsWith("_json", StringComparison.Ordinal) || values[i] is not string json) continue;
                    try { using var document = JsonDocument.Parse(json); values[i] = document.RootElement.Clone(); }
                    catch (JsonException) { /* Preserve non-JSON text conservatively. */ }
                }
                using var row = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(values));
                MergeComparison.Append(hash, comparison.Canonical(row.RootElement));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static HistoryRelation CompareContents(SqliteConnection source, SqliteConnection local, string id, MergeComparison comparison, CancellationToken token)
    {
        var result = HistoryRelation.Equal;
        foreach (var table in Columns.Keys.Where(t => t != "thread_history_projection_state"))
        {
            var relation = MergeComparison.CompareSequences(ContentRows(source, table, id, comparison, token), ContentRows(local, table, id, comparison, token));
            if (relation == HistoryRelation.Equal) continue;
            if (relation == HistoryRelation.Diverged || (result != HistoryRelation.Equal && result != relation)) return HistoryRelation.Diverged;
            result = relation;
        }
        return result;
    }

    private static IEnumerable<byte[]> ContentRows(SqliteConnection db, string table, string id, MergeComparison comparison, CancellationToken token)
    {
        var columns = Columns[table].Where(c => c is not ("rollout_ordinal" or "rollout_byte_offset" or "rollout_end_ordinal" or "rollout_end_byte_offset" or "updated_at_ordinal")).ToArray();
        using var command = Command(db, $"SELECT {string.Join(',', columns)} FROM {table} WHERE thread_id=$id ORDER BY rollout_ordinal,{Keys[table]}");
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            var values = Values(reader);
            for (var i = 0; i < columns.Length; i++)
            {
                if (!columns[i].EndsWith("_json", StringComparison.Ordinal) || values[i] is not string json) continue;
                try { using var document = JsonDocument.Parse(json); values[i] = document.RootElement.Clone(); }
                catch (JsonException) { }
            }
            using var row = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(values));
            yield return comparison.Canonical(row.RootElement);
        }
    }

    internal static HashSet<string> Ids(SqliteConnection db)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in Columns.Keys)
        {
            using var command = Command(db, $"SELECT DISTINCT thread_id FROM {table}");
            using var reader = command.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetString(0));
        }
        return ids;
    }

    internal static void Attach(SqliteConnection db, string path)
    {
        MergeFiles.NoLinks(path);
        if (!File.Exists(path)) throw new InvalidDataException("Destination paginated history is missing. Initialize Codex first.");
        using var command = Command(db, "ATTACH DATABASE $path AS history");
        command.Parameters.AddWithValue("$path", path);
        command.ExecuteNonQuery();
        Validate(db, "history");
    }

    internal static void Insert(SqliteConnection source, SqliteConnection target, SqliteTransaction tx, string id)
    {
        foreach (var (table, columns) in Columns)
        {
            using var read = Command(source, $"SELECT * FROM {table} WHERE thread_id=$id ORDER BY {Keys[table]}");
            read.Parameters.AddWithValue("$id", id);
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                using var write = Command(target, $"INSERT INTO history.{table} ({string.Join(',', columns)}) VALUES ({string.Join(',', columns.Select((_, i) => "$p" + i))})", tx);
                var values = Values(reader);
                for (var i = 0; i < values.Length; i++) write.Parameters.AddWithValue("$p" + i, values[i] ?? DBNull.Value);
                write.ExecuteNonQuery();
            }
        }
    }

    internal static void DeleteInserted(SqliteConnection db, SqliteTransaction tx, IEnumerable<string> ids)
    {
        foreach (var id in ids)
        foreach (var table in Columns.Keys)
        {
            using var command = Command(db, $"DELETE FROM history.{table} WHERE thread_id=$id", tx);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    internal static byte[] ExportThread(SqliteConnection db, string id)
    {
        var tables = new Dictionary<string, List<object?[]>>();
        foreach (var table in Columns.Keys)
        {
            var rows = new List<object?[]>();
            using var command = Command(db, $"SELECT * FROM {table} WHERE thread_id=$id ORDER BY {Keys[table]}");
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(Values(reader));
            tables.Add(table, rows);
        }
        return JsonSerializer.SerializeToUtf8Bytes(new { Columns, Tables = tables });
    }

    internal static Dictionary<string, SessionFile> Inventory(string home,
        Dictionary<string, Dictionary<string, object?>> rows, CancellationToken token, IProgress<string>? progress, ISet<string>? unindexedIds = null)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[] { "sessions", "archived_sessions" })
        foreach (var file in MergeFiles.Files(Path.Combine(home, dir)).Where(p => p.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase)))
        {
            var logical = file.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
            var relative = Path.GetRelativePath(home, logical).Replace('\\', '/');
            if (!files.ContainsKey(relative) || logical == file) files[relative] = file;
        }
        var result = new Dictionary<string, SessionFile>();
        foreach (var (id, row) in rows)
        {
            token.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(id, "D", out _)) throw new InvalidDataException("Invalid conversation ID.");
            var stored = Convert.ToString(row.GetValueOrDefault("rollout_path"))?.Replace('\\', '/') ?? "";
            if (stored.EndsWith(".zst", StringComparison.OrdinalIgnoreCase)) stored = stored[..^4];
            if (stored.Split('/').Any(x => x is ".." or ".")) throw new InvalidDataException("Unsafe rollout path.");
            var relative = new[] { "sessions/", "archived_sessions/" }.Select(prefix =>
            { var at = stored.LastIndexOf('/' + prefix, StringComparison.OrdinalIgnoreCase); return at >= 0 ? stored[(at + 1)..] : stored; }).FirstOrDefault(files.ContainsKey);
            if (relative is null) throw new InvalidDataException($"Indexed conversation {id} has no unambiguous rollout file; repair this profile first.");
            var path = files[relative];
            progress?.Report($"Inspecting conversation {id}: {Path.GetFileName(path)}");
            if (ReadId(path) != id)
                throw new InvalidDataException("Rollout and database thread IDs disagree.");
            result.Add(id, new(id, path, MergeFiles.FileHash(path), Convert.ToString(row.GetValueOrDefault("cwd")) ?? "", ""));
        }
        var unmatched = files.Values.Except(result.Values.Select(x => x.Path), StringComparer.OrdinalIgnoreCase).ToArray();
        if (unindexedIds is not null)
            foreach (var path in unmatched)
            {
                token.ThrowIfCancellationRequested();
                unindexedIds.Add(ReadId(path));
            }
        if (unmatched.Length > 0) progress?.Report($"Warning: {unmatched.Length} unindexed rollout files are not imported by the paginated adapter; no files are removed.");
        return result;
    }

    private static string ReadId(string path)
    {
        using var input = File.OpenRead(path);
        using Stream content = path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) ? new ZstdSharp.DecompressionStream(input) : input;
        using var reader = new StreamReader(content, new System.Text.UTF8Encoding(false, true));
        // Bound the header independently of decompressed transcript size.
        var header = new System.Text.StringBuilder();
        int c;
        while ((c = reader.Read()) >= 0 && c != '\n')
        { if (header.Length >= 1024 * 1024) throw new InvalidDataException("Oversized session header."); header.Append((char)c); }
        using var meta = JsonDocument.Parse(header.ToString());
        if (meta.RootElement.GetProperty("type").GetString() != "session_meta") throw new InvalidDataException("Missing session metadata.");
        var id = meta.RootElement.GetProperty("payload").GetProperty("id").GetString();
        if (!Guid.TryParseExact(id, "D", out _)) throw new InvalidDataException("Invalid rollout conversation ID.");
        return id!;
    }
}
