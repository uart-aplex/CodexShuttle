using System.Text.Json;
using CodexShuttle.Core.Models;
using Microsoft.Data.Sqlite;

namespace CodexShuttle.Core.Services;

public sealed class CodexInspector
{
    public CodexInspectionResult Inspect(string codexHome)
    {
        var result = new CodexInspectionResult
        {
            CodexHomeExists = Directory.Exists(codexHome)
        };

        if (!result.CodexHomeExists)
        {
            result.Warnings.Add("Codex home folder was not found.");
            return result;
        }

        InspectSessions(codexHome, result);
        InspectStateFiles(codexHome, result);
        return result;
    }

    private static void InspectSessions(string codexHome, CodexInspectionResult result)
    {
        var sessionsFolder = Path.Combine(codexHome, "sessions");
        result.SessionsFolderExists = Directory.Exists(sessionsFolder);
        if (result.SessionsFolderExists)
        {
            var files = Directory.EnumerateFiles(sessionsFolder, "*", SearchOption.AllDirectories).ToList();
            result.SessionsFileCount = files.Count;
            result.LatestSessionWriteTime = files
                .Select(file => new FileInfo(file).LastWriteTime)
                .OrderByDescending(time => time)
                .Select(time => new DateTimeOffset(time))
                .FirstOrDefault();
        }

        var archivedFolder = Path.Combine(codexHome, "archived_sessions");
        result.ArchivedSessionsFolderExists = Directory.Exists(archivedFolder);
        if (result.ArchivedSessionsFolderExists)
        {
            result.ArchivedSessionsFileCount = Directory.EnumerateFiles(archivedFolder, "*", SearchOption.AllDirectories).Count();
        }
    }

    private static void InspectStateFiles(string codexHome, CodexInspectionResult result)
    {
        var stateSqlite = Path.Combine(codexHome, "state_5.sqlite");
        result.StateSqliteExists = File.Exists(stateSqlite);
        result.StateSqliteReadable = result.StateSqliteExists && CanOpenForRead(stateSqlite);

        var sessionIndex = Path.Combine(codexHome, "session_index.jsonl");
        result.SessionIndexExists = File.Exists(sessionIndex);
        result.SessionIndexReadable = result.SessionIndexExists && CanReadJsonLines(sessionIndex);

        var globalState = Path.Combine(codexHome, ".codex-global-state.json");
        result.GlobalStateExists = File.Exists(globalState);
        result.GlobalStateJsonValid = result.GlobalStateExists && IsJsonValid(globalState);

        result.ConfigTomlExists = File.Exists(Path.Combine(codexHome, "config.toml"));
    }

    private static bool CanOpenForRead(string filePath)
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = filePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check(1);";
            return string.Equals(command.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool CanReadJsonLines(string filePath)
    {
        try
        {
            foreach (var line in File.ReadLines(filePath).Where(line => !string.IsNullOrWhiteSpace(line)).Take(10))
            {
                JsonDocument.Parse(line);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsJsonValid(string filePath)
    {
        try
        {
            using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            JsonDocument.Parse(stream);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
