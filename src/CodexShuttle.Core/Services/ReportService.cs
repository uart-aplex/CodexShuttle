using System.Text;
using CodexShuttle.Core.Models;

namespace CodexShuttle.Core.Services;

public sealed class ReportService
{
    public async Task SaveBackupReportAsync(BackupManifest manifest, string reportPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");
        await File.WriteAllTextAsync(reportPath, BuildBackupReport(manifest), Encoding.UTF8, cancellationToken);
    }

    public string BuildBackupReport(BackupManifest manifest)
    {
        var builder = new StringBuilder();
        var inspection = manifest.CodexInspection;

        builder.AppendLine("Codex Shuttle Backup Report");
        builder.AppendLine("===========================");
        builder.AppendLine();
        builder.AppendLine($"Backup time: {manifest.CreatedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Source computer: {manifest.SourceComputer}");
        builder.AppendLine($"Source user: {manifest.SourceUser}");
        builder.AppendLine();
        builder.AppendLine("Codex Home:");
        builder.AppendLine($"  Path: {manifest.CodexHome}");
        builder.AppendLine($"  Exists: {YesNo(inspection.CodexHomeExists)}");
        builder.AppendLine($"  Sessions: {inspection.SessionsFileCount}");
        builder.AppendLine($"  Archived sessions: {inspection.ArchivedSessionsFileCount}");
        builder.AppendLine($"  Latest session: {inspection.LatestSessionWriteTime?.ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "None"}");
        builder.AppendLine($"  state_5.sqlite: {Status(inspection.StateSqliteExists, inspection.StateSqliteReadable, "Readable")}");
        builder.AppendLine($"  session_index.jsonl: {Status(inspection.SessionIndexExists, inspection.SessionIndexReadable, "Readable")}");
        builder.AppendLine($"  .codex-global-state.json: {Status(inspection.GlobalStateExists, inspection.GlobalStateJsonValid, "Valid JSON")}");
        builder.AppendLine($"  config.toml: {(inspection.ConfigTomlExists ? "Exists" : "Missing")}");
        builder.AppendLine();
        builder.AppendLine("Workspaces:");

        foreach (var workspace in manifest.WorkspacePaths)
        {
            builder.AppendLine($"  {workspace.SourcePath}");
            builder.AppendLine($"    Files: {workspace.FileCount}");
            builder.AppendLine($"    Size: {FormatBytes(workspace.TotalBytes)}");
            builder.AppendLine($"    Status: {(workspace.Enabled && workspace.Exists ? "Included" : "Skipped")}");
        }

        builder.AppendLine();
        builder.AppendLine("Warnings:");
        if (inspection.Warnings.Count == 0)
        {
            builder.AppendLine("  None");
        }
        else
        {
            foreach (var warning in inspection.Warnings)
            {
                builder.AppendLine($"  {warning}");
            }
        }

        return builder.ToString();
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private static string YesNo(bool value) => value ? "Yes" : "No";

    private static string Status(bool exists, bool secondaryOk, string secondaryLabel)
    {
        if (!exists)
        {
            return "Missing";
        }

        return secondaryOk ? $"Exists, {secondaryLabel}" : "Exists, Check failed";
    }
}
