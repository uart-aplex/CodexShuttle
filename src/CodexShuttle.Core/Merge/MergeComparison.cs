using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexShuttle.Core.Merge;

// Comparison only: never rewrite rollout bytes or reuse this hash for package verification/rollback.
internal sealed class MergeComparison
{
    private readonly Dictionary<string, string> _roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Regex? _paths;

    internal MergeComparison(MergeRequest request, string sourceUserProfile)
    {
        var mappings = request.WorkspaceMappings.ToList();
        if (!string.IsNullOrWhiteSpace(sourceUserProfile) && Path.IsPathFullyQualified(sourceUserProfile))
            mappings.Add(new(sourceUserProfile, request.TargetUserProfile));
        foreach (var pair in mappings.OrderByDescending(p => p.Key.Length))
        foreach (var separator in new[] { "\\", "\\\\", "\\\\\\\\", "/" })
        {
            static string Format(string path, string separator) => path.TrimEnd('\\', '/').Replace("\\", "/").Replace("/", separator);
            var target = Format(pair.Value, separator);
            _roots.TryAdd(Format(pair.Key, separator), target);
            _roots.TryAdd(target, target);
        }
        if (_roots.Count > 0)
            _paths = new Regex(@"(?<![\w])(?:" + string.Join("|", _roots.Keys.OrderByDescending(x => x.Length).Select(Regex.Escape))
                + @")(?=$|[\\/\s""'<>\[\](){},;])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    }

    internal string Normalize(string text) => _paths?.Replace(text, match => _roots[match.Value]) ?? text;

    internal byte[] Canonical(JsonElement element)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes)) Write(writer, element);
        return bytes.ToArray();
    }

    private void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
                if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                    throw new InvalidDataException("Duplicate JSON fields cannot be compared safely.");
                foreach (var property in properties) { writer.WritePropertyName(property.Name); Write(writer, property.Value); }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) Write(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String: writer.WriteStringValue(Normalize(element.GetString()!)); break;
            default: element.WriteTo(writer); break;
        }
    }

    internal static void Append(IncrementalHash hash, byte[] bytes)
    { hash.AppendData(BitConverter.GetBytes(bytes.Length)); hash.AppendData(bytes); }

    internal string RolloutHash(string path, CancellationToken token, IProgress<string>? progress = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var record in Records(path, token, progress)) Append(hash, record);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal HistoryRelation CompareRollouts(string source, string local, CancellationToken token, IProgress<string>? progress = null)
        => CompareSequences(Records(source, token, progress), Records(local, token, progress));

    internal static HistoryRelation CompareSequences(IEnumerable<byte[]> source, IEnumerable<byte[]> local)
    {
        using var a = source.GetEnumerator();
        using var b = local.GetEnumerator();
        while (true)
        {
            var moreA = a.MoveNext(); var moreB = b.MoveNext();
            if (!moreA && !moreB) return HistoryRelation.Equal;
            if (!moreA || !moreB)
            {
                // Validate the entire longer stream, including decompression and JSON bounds.
                var remainder = moreA ? a : b;
                while (remainder.MoveNext()) { }
                return moreA ? HistoryRelation.SourceAhead : HistoryRelation.LocalAhead;
            }
            if (!a.Current.AsSpan().SequenceEqual(b.Current)) return HistoryRelation.Diverged;
        }
    }

    private IEnumerable<byte[]> Records(string path, CancellationToken token, IProgress<string>? progress)
    {
        MergeFiles.NoLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using Stream content = path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) ? new ZstdSharp.DecompressionStream(input) : input;
        using var reader = new StreamReader(content, new UTF8Encoding(false, true));
        var count = 0;
        foreach (var line in Lines(reader, token))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            var record = document.RootElement;
            // Codex can append this settings event without adding a conversation turn.
            // Unknown records and all messages/tools remain part of the comparison.
            var settings = record.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "event_msg"
                && record.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("type", out var eventType) && eventType.ValueKind == JsonValueKind.String && eventType.GetString() == "thread_settings_applied";
            if (!settings) yield return Canonical(record);
            if (++count % 2000 == 0) progress?.Report($"Comparing conversation content: {Path.GetFileName(path)} ({count:N0} records)");
        }
    }

    private static IEnumerable<string> Lines(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[32768];
        var line = new StringBuilder();
        long total = 0;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            if ((total += read) > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Conversation exceeds the decompressed comparison limit.");
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != '\n') continue;
                line.Append(buffer, start, i - start);
                if (line.Length > 32 * 1024 * 1024) throw new InvalidDataException("Conversation record exceeds the comparison limit.");
                yield return line.ToString(); line.Clear(); start = i + 1;
            }
            line.Append(buffer, start, read - start);
            if (line.Length > 32 * 1024 * 1024) throw new InvalidDataException("Conversation record exceeds the comparison limit.");
        }
        if (line.Length > 0) yield return line.ToString();
    }
}
