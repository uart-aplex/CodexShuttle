using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexShuttle.Core.Merge;

internal static class MergeFiles
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static string FileHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    public static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool Within(string root, string path) => Canonical(path).StartsWith(Canonical(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Reparse points are not supported for merge: {current}");
        }
    }

    public static IEnumerable<string> Files(string root)
    {
        NoLinks(root);
        if (!Directory.Exists(root)) yield break;
        foreach (var path in Directory.EnumerateFileSystemEntries(root).Order(StringComparer.OrdinalIgnoreCase))
        {
            NoLinks(path);
            if (Directory.Exists(path)) { foreach (var file in Files(path)) yield return file; }
            else yield return path;
        }
    }

    public static byte[] Read(string path)
    {
        NoLinks(path);
        if (new FileInfo(path).Length > 256L * 1024 * 1024)
            throw new InvalidDataException($"Session exceeds the 256 MiB preview limit: {path}");
        return File.ReadAllBytes(path);
    }

    public static Dictionary<string, SessionFile> Inventory(string home, CancellationToken token)
    {
        var result = new Dictionary<string, SessionFile>(StringComparer.Ordinal);
        foreach (var path in new[] { "sessions", "archived_sessions" }.SelectMany(name => Files(Path.Combine(home, name)))
                     .Where(p => p.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)))
        {
            token.ThrowIfCancellationRequested();
            var bytes = Read(path);
            using var reader = new StringReader(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));
            string? line;
            string? id = null;
            var cwd = "";
            var timestamp = "";
            var count = 0;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                var item = document.RootElement;
                var type = item.GetProperty("type").GetString();
                if (count++ == 0 && type != "session_meta") throw new InvalidDataException($"Missing first session_meta: {path}");
                if (type != "session_meta") continue;
                if (id is not null) throw new InvalidDataException($"Repeated session_meta: {path}");
                var meta = item.GetProperty("payload");
                if (!Guid.TryParse(meta.GetProperty("id").GetString(), out var guid)) throw new InvalidDataException($"Invalid stable thread ID: {path}");
                id = guid.ToString();
                cwd = meta.GetProperty("cwd").GetString() ?? "";
                timestamp = meta.GetProperty("timestamp").GetString() ?? "";
            }
            if (id is null || !result.TryAdd(id, new(id, path, Hash(bytes), cwd, timestamp)))
                throw new InvalidDataException($"Empty session or duplicate thread ID: {path}");
        }
        return result;
    }

    public static string MapCwd(string cwd, MergeRequest request, string sourceUserProfile)
    {
        static string Comparable(string path)
        {
            path = path.Replace('/', '\\');
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        }
        var candidate = Comparable(cwd);
        var mappings = request.WorkspaceMappings.ToList();
        if (!string.IsNullOrWhiteSpace(sourceUserProfile)) mappings.Add(new(sourceUserProfile, request.TargetUserProfile));
        foreach (var pair in mappings.OrderByDescending(x => x.Key.Length))
        {
            var from = Comparable(pair.Key).TrimEnd('\\');
            if (candidate.Equals(from, StringComparison.OrdinalIgnoreCase)) return pair.Value;
            if (candidate.StartsWith(from + "\\", StringComparison.OrdinalIgnoreCase))
                return pair.Value.TrimEnd('\\', '/') + candidate[from.Length..];
        }
        return cwd;
    }

    public static byte[] MapSession(byte[] original, MergeRequest request, string sourceUserProfile)
    {
        var text = new UTF8Encoding(false, true).GetString(original);
        var changed = false;
        var lines = text.TrimStart('\uFEFF').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var node = JsonNode.Parse(lines[i])!;
            if (node["type"]?.GetValue<string>() is not ("session_meta" or "turn_context")) continue;
            if (node["payload"] is not JsonObject payload || payload["cwd"] is not JsonValue value) continue;
            var cwd = value.GetValue<string>();
            var mapped = MapCwd(cwd, request, sourceUserProfile);
            if (cwd == mapped) continue;
            payload["cwd"] = mapped;
            lines[i] = node.ToJsonString();
            changed = true;
        }
        return changed ? Encoding.UTF8.GetBytes(string.Join('\n', lines)) : original;
    }

    public static void AtomicWrite(string path, byte[] bytes, bool overwrite)
    {
        NoLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            File.Move(temp, path, overwrite);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static void SaveJournal(string directory, MergeJournal journal) =>
        AtomicWrite(Path.Combine(directory, "operation.json"), JsonSerializer.SerializeToUtf8Bytes(journal, Json), true);
}
