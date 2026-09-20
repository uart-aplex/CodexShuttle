using System.IO.Enumeration;
using System.Text;
using CodexShuttle.Core.Models;
using Microsoft.Data.Sqlite;

namespace CodexShuttle.Core.Services;

public sealed class ProfilePathRemapService
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".jsonl", ".toml", ".yaml", ".yml", ".xml", ".md", ".txt",
        ".ini", ".cfg", ".conf", ".rules", ".csv", ".log", ".url", ".bak",
        ".ps1", ".psm1", ".cmd", ".bat", ".sh", ".js", ".mjs", ".cjs",
        ".ts", ".tsx", ".jsx", ".py", ".cs", ".props", ".targets"
    };

    private readonly RestorePathResolver _pathResolver;

    public ProfilePathRemapService(RestorePathResolver pathResolver)
    {
        _pathResolver = pathResolver;
    }

    public async Task<OperationResult> RemapAsync(
        BackupManifest manifest,
        IReadOnlyList<MirrorPlanItem> plan,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        var mappings = BuildMappings(manifest);
        if (mappings.Count == 0)
        {
            return OperationResult.Ok("The source and destination user-profile paths already match.");
        }

        var filesChanged = 0;
        var sqliteRowsChanged = 0;
        try
        {
            foreach (var path in EnumerateRestoredFiles(plan))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsSqliteDatabase(path))
                {
                    progress?.Report($"Remapping profile paths in database: {Path.GetFileName(path)}");
                    var changedRows = await RemapSqliteAsync(path, mappings, cancellationToken);
                    sqliteRowsChanged += changedRows;
                    if (changedRows > 0)
                    {
                        filesChanged++;
                    }

                    continue;
                }

                if (!TextExtensions.Contains(Path.GetExtension(path)))
                {
                    continue;
                }

                progress?.Report($"Checking profile paths: {path}");
                if (await RemapTextFileAsync(path, mappings, cancellationToken))
                {
                    filesChanged++;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return OperationResult.Fail(
                "Restore copied the package, but embedded Windows user-profile paths could not be remapped safely.",
                ex.Message);
        }

        return filesChanged == 0
            ? OperationResult.Ok("No embedded source user-profile paths required changes.")
            : OperationResult.Ok(
                $"Mapped embedded user-profile paths in {filesChanged:N0} file(s), including {sqliteRowsChanged:N0} SQLite row(s).");
    }

    private IReadOnlyList<PathMapping> BuildMappings(BackupManifest manifest)
    {
        var baseMappings = new List<PathMapping>();
        AddBaseMapping(baseMappings, manifest.CodexHome, _pathResolver.ResolveCodexHome(manifest));
        AddBaseMapping(baseMappings, manifest.AgentsHome, _pathResolver.ResolveAgentsHome(manifest));
        foreach (var appData in manifest.AppDataPaths.Where(item => item.Included))
        {
            AddBaseMapping(baseMappings, appData.SourcePath, _pathResolver.ResolveAppDataTarget(appData));
        }

        AddBaseMapping(baseMappings, manifest.SourceUserProfile, _pathResolver.GetCurrentUserProfile());

        var variants = new List<PathMapping>();
        foreach (var mapping in baseMappings.OrderByDescending(item => item.Source.Length))
        {
            AddVariant(variants, mapping.Source, mapping.Destination);
            AddVariant(
                variants,
                mapping.Source.Replace("\\", "\\\\", StringComparison.Ordinal),
                mapping.Destination.Replace("\\", "\\\\", StringComparison.Ordinal));
            AddVariant(
                variants,
                mapping.Source.Replace('\\', '/'),
                mapping.Destination.Replace('\\', '/'));
        }

        return variants.OrderByDescending(item => item.Source.Length).ToArray();
    }

    private static void AddBaseMapping(List<PathMapping> mappings, string source, string destination)
    {
        source = TrimDirectoryEnding(source);
        destination = TrimDirectoryEnding(destination);
        if (string.IsNullOrWhiteSpace(source)
            || string.IsNullOrWhiteSpace(destination)
            || source.Equals(destination, StringComparison.OrdinalIgnoreCase)
            || mappings.Any(item => item.Source.Equals(source, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        mappings.Add(new PathMapping(source, destination));
    }

    private static void AddVariant(List<PathMapping> mappings, string source, string destination)
    {
        if (!mappings.Any(item => item.Source.Equals(source, StringComparison.OrdinalIgnoreCase)))
        {
            mappings.Add(new PathMapping(source, destination));
        }
    }

    private static string TrimDirectoryEnding(string path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static IEnumerable<string> EnumerateRestoredFiles(IReadOnlyList<MirrorPlanItem> plan)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in plan.Where(item => item.RemapUserProfilePaths))
        {
            if (!item.IsDirectory)
            {
                if (File.Exists(item.DestinationPath) && seen.Add(Path.GetFullPath(item.DestinationPath)))
                {
                    yield return item.DestinationPath;
                }

                continue;
            }

            if (!Directory.Exists(item.SourcePath))
            {
                continue;
            }

            foreach (var sourceFile in Directory.EnumerateFiles(item.SourcePath, "*", SearchOption.AllDirectories))
            {
                if (IsExcluded(item.SourcePath, sourceFile, item.MirrorOptions))
                {
                    continue;
                }

                var destinationFile = Path.Combine(
                    item.DestinationPath,
                    Path.GetRelativePath(item.SourcePath, sourceFile));
                var fullDestination = Path.GetFullPath(destinationFile);
                if (File.Exists(fullDestination) && seen.Add(fullDestination))
                {
                    yield return fullDestination;
                }
            }
        }
    }

    private static bool IsExcluded(string root, string filePath, FileMirrorOptions options)
    {
        var relative = Path.GetRelativePath(root, filePath);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Length > 1
            && options.ExcludedDirectories.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var fileName = Path.GetFileName(filePath);
        return options.ExcludedFiles.Any(
            pattern => FileSystemName.MatchesSimpleExpression(pattern, fileName, ignoreCase: true));
    }

    private static bool IsSqliteDatabase(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".sqlite", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".sqlite3", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".db", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Span<byte> header = stackalloc byte[16];
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return stream.Read(header) == header.Length
               && header.SequenceEqual("SQLite format 3\0"u8);
    }

    private static async Task<int> RemapSqliteAsync(
        string path,
        IReadOnlyList<PathMapping> mappings,
        CancellationToken cancellationToken)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }

        try
        {
            return await RemapWritableSqliteAsync(path, mappings, cancellationToken);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, attributes);
            }
        }
    }

    private static async Task<int> RemapWritableSqliteAsync(
        string path,
        IReadOnlyList<PathMapping> mappings,
        CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);

        var tables = new List<(string Name, string Sql)>();
        await using (var tableCommand = connection.CreateCommand())
        {
            tableCommand.CommandText = "SELECT name, COALESCE(sql, '') FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
            await using var reader = await tableCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var changedRows = 0;
        await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken))
        {
            foreach (var table in tables.Where(item => !item.Sql.Contains("VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase)))
            {
                var columns = new List<string>();
                await using (var columnCommand = connection.CreateCommand())
                {
                    columnCommand.Transaction = transaction;
                    columnCommand.CommandText = "SELECT name FROM pragma_table_info($table)";
                    columnCommand.Parameters.AddWithValue("$table", table.Name);
                    await using var reader = await columnCommand.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        columns.Add(reader.GetString(0));
                    }
                }

                foreach (var column in columns)
                {
                    await using var update = connection.CreateCommand();
                    update.Transaction = transaction;
                    var valueExpression = QuoteIdentifier(column);
                    var predicates = new List<string>();
                    for (var index = 0; index < mappings.Count; index++)
                    {
                        valueExpression = $"replace({valueExpression}, $old{index}, $new{index})";
                        predicates.Add($"instr({QuoteIdentifier(column)}, $old{index}) > 0");
                        update.Parameters.AddWithValue($"$old{index}", mappings[index].Source);
                        update.Parameters.AddWithValue($"$new{index}", mappings[index].Destination);
                    }

                    update.CommandText =
                        $"UPDATE {QuoteIdentifier(table.Name)} SET {QuoteIdentifier(column)} = {valueExpression} " +
                        $"WHERE typeof({QuoteIdentifier(column)}) = 'text' AND ({string.Join(" OR ", predicates)})";
                    changedRows += await update.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }

        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA quick_check(1)";
            var result = Convert.ToString(await check.ExecuteScalarAsync(cancellationToken));
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"SQLite quick_check failed after profile-path remapping: {path}");
            }
        }

        await using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            await checkpoint.ExecuteNonQueryAsync(cancellationToken);
        }

        return changedRows;
    }

    private static async Task<bool> RemapTextFileAsync(
        string path,
        IReadOnlyList<PathMapping> mappings,
        CancellationToken cancellationToken)
    {
        var encodingInfo = DetectEncoding(path);
        if (encodingInfo is null)
        {
            return false;
        }

        var tempPath = path + $".codexshuttle-remap-{Guid.NewGuid():N}.tmp";
        var replacements = 0;
        var originalAttributes = File.GetAttributes(path);
        try
        {
            await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
            {
                input.Position = encodingInfo.Value.PreambleLength;
                using var reader = new StreamReader(
                    input,
                    encodingInfo.Value.Encoding,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 65536,
                    leaveOpen: false);
                await using var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                await using var writer = new StreamWriter(output, encodingInfo.Value.Encoding, 65536, leaveOpen: false);

                var buffer = new char[65536];
                var carry = string.Empty;
                while (true)
                {
                    var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                    var final = read == 0;
                    var inputText = carry + (read > 0 ? new string(buffer, 0, read) : string.Empty);
                    var transformed = TransformAvailableText(inputText, final, mappings, out carry, ref replacements);
                    await writer.WriteAsync(transformed.AsMemory(), cancellationToken);
                    if (final)
                    {
                        break;
                    }
                }
            }

            if (replacements == 0)
            {
                File.Delete(tempPath);
                return false;
            }

            var lastWriteTime = File.GetLastWriteTimeUtc(path);
            if ((originalAttributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, originalAttributes & ~FileAttributes.ReadOnly);
            }

            File.Move(tempPath, path, overwrite: true);
            File.SetLastWriteTimeUtc(path, lastWriteTime);
            File.SetAttributes(path, originalAttributes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            return false;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            if (File.Exists(path) && File.GetAttributes(path) != originalAttributes)
            {
                File.SetAttributes(path, originalAttributes);
            }
        }
    }

    private static string TransformAvailableText(
        string input,
        bool final,
        IReadOnlyList<PathMapping> mappings,
        out string carry,
        ref int replacements)
    {
        var maxSourceLength = mappings.Max(item => item.Source.Length);
        var processLimit = final
            ? input.Length
            : Math.Max(0, input.Length - maxSourceLength + 1);
        var output = new StringBuilder(input.Length);
        var position = 0;
        while (position < processLimit)
        {
            PathMapping? match = null;
            foreach (var mapping in mappings)
            {
                if (position + mapping.Source.Length <= input.Length
                    && string.Compare(
                        input,
                        position,
                        mapping.Source,
                        0,
                        mapping.Source.Length,
                        StringComparison.OrdinalIgnoreCase) == 0)
                {
                    match = mapping;
                    break;
                }
            }

            if (match is not null)
            {
                output.Append(match.Destination);
                position += match.Source.Length;
                replacements++;
            }
            else
            {
                output.Append(input[position]);
                position++;
            }
        }

        carry = input[position..];
        return output.ToString();
    }

    private static (Encoding Encoding, int PreambleLength)? DetectEncoding(string path)
    {
        var sample = new byte[8192];
        int count;
        using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            count = stream.Read(sample, 0, sample.Length);
        }

        if (count >= 4 && sample.AsSpan(0, 4).SequenceEqual(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
        {
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true), 4);
        }

        if (count >= 4 && sample.AsSpan(0, 4).SequenceEqual(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
        {
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true), 4);
        }

        if (count >= 3 && sample.AsSpan(0, 3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true), 3);
        }

        if (count >= 2 && sample.AsSpan(0, 2).SequenceEqual(new byte[] { 0xFF, 0xFE }))
        {
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true), 2);
        }

        if (count >= 2 && sample.AsSpan(0, 2).SequenceEqual(new byte[] { 0xFE, 0xFF }))
        {
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true), 2);
        }

        return sample.AsSpan(0, count).Contains((byte)0)
            ? null
            : (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), 0);
    }

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private sealed record PathMapping(string Source, string Destination);
}
