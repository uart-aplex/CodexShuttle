using System.Security.Cryptography;
using CodexShuttle.Core.Models;

namespace CodexShuttle.Core.Services;

internal static class PartialRestorePathRecovery
{
    // A failed plugin copy must not strand intact imported indexes on another PC's
    // user path. Never repair a mixed/incomplete history, and never clear the copy failure.
    internal static async Task<OperationResult> RepairAsync(string sourceHome, string targetHome,
        CancellationToken token, IProgress<string>? progress)
    {
        try
        {
            progress?.Report("Restore is incomplete. Verifying copied conversation bytes before attempting index-path recovery...");
            var sourceFiles = HistoryFiles(sourceHome);
            var targetFiles = HistoryFiles(targetHome);
            if (!sourceFiles.Any(path => path.StartsWith("state_", StringComparison.OrdinalIgnoreCase)
                    && path.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase))
                || !sourceFiles.SetEquals(targetFiles))
                return OperationResult.Fail("Automatic path recovery skipped: the conversation file sets are incomplete or different. Keep the package and retry after resolving the copy error.");

            foreach (var relative in sourceFiles)
            {
                token.ThrowIfCancellationRequested();
                var source = Path.Combine(sourceHome, relative);
                var target = Path.Combine(targetHome, relative);
                CheckNoLinks(source);
                CheckNoLinks(target);
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                await using var output = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                if (input.Length != output.Length
                    || !(await SHA256.HashDataAsync(input, token)).SequenceEqual(await SHA256.HashDataAsync(output, token)))
                    return OperationResult.Fail("Automatic path recovery skipped: copied conversation data does not match the verified package.", relative);
            }

            var service = new RolloutPathService();
            var check = await service.CheckAsync(targetHome, false, token, progress);
            if (!check.Success) return check;
            var repaired = await service.CheckAsync(targetHome, true, token, progress);
            if (!repaired.Success) return repaired;
            var final = await service.CheckAsync(targetHome, false, token, progress, requireResolvedPaths: true);
            if (!final.Success) return final;
            repaired.Message = "Conversation index paths recovered. " + repaired.Message
                + " Restore is still incomplete; other profile-path conversions were not performed.";
            return repaired;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return OperationResult.Fail("Automatic path recovery could not be completed; the original restore failure remains.", ex.Message);
        }
    }

    private static HashSet<string> HistoryFiles(string home)
    {
        CheckNoLinks(home);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in new[] { "sessions", "archived_sessions" })
        {
            var root = Path.Combine(home, directory);
            if (Directory.Exists(root)) AddTree(root);
        }
        foreach (var root in new[] { home, Path.Combine(home, "sqlite") })
        {
            if (!Directory.Exists(root)) continue;
            CheckNoLinks(root);
            foreach (var path in Directory.EnumerateFiles(root))
            {
                var name = Path.GetFileName(path);
                if ((name.StartsWith("state_", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("thread_history_", StringComparison.OrdinalIgnoreCase))
                    && name.Contains(".sqlite", StringComparison.OrdinalIgnoreCase))
                    files.Add(Path.GetRelativePath(home, path));
            }
        }
        foreach (var name in new[] { "session_index.jsonl", ".app-server-state-reconciled-v1" })
            if (File.Exists(Path.Combine(home, name))) files.Add(name);
        return files;

        void AddTree(string root)
        {
            CheckNoLinks(root);
            foreach (var path in Directory.EnumerateFileSystemEntries(root))
            {
                CheckNoLinks(path);
                if (Directory.Exists(path)) AddTree(path);
                else files.Add(Path.GetRelativePath(home, path));
            }
        }
    }

    private static void CheckNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException($"Path recovery does not follow reparse points: {current}");
    }
}
