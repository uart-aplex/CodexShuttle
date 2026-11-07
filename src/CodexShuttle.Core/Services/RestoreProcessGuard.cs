using System.Diagnostics;
using CodexShuttle.Core.Models;

namespace CodexShuttle.Core.Services;

public sealed class RestoreProcessGuard
{
    internal sealed record Executable(int Id, string Path);
    private readonly Func<IEnumerable<Executable>> _processes;

    public RestoreProcessGuard() : this(RunningExecutables) { }
    internal RestoreProcessGuard(Func<IEnumerable<Executable>> processes) => _processes = processes;

    public (IReadOnlyList<MirrorPlanItem> Plan, IReadOnlyList<string> Warnings) PreserveActiveBrowserCache(
        IReadOnlyList<MirrorPlanItem> plan, string codexHome)
    {
        var chrome = Path.GetFullPath(Path.Combine(codexHome, "plugins", "cache", "openai-bundled", "chrome"));
        var active = _processes().Any(process =>
        {
            if (!IsWithin(chrome, process.Path)) return false;
            var parts = Path.GetRelativePath(chrome, process.Path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return parts.Length == 5 && parts[1].Equals("extension-host", StringComparison.OrdinalIgnoreCase)
                && parts[2].Equals("windows", StringComparison.OrdinalIgnoreCase)
                && (parts[3].Equals("x64", StringComparison.OrdinalIgnoreCase) || parts[3].Equals("arm64", StringComparison.OrdinalIgnoreCase))
                && parts[4].Equals("extension-host.exe", StringComparison.OrdinalIgnoreCase);
        });
        if (!active) return (plan, []);
        var result = new List<MirrorPlanItem>();
        var preserved = false;
        var preservedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in plan)
        {
            if (Path.GetFullPath(item.DestinationPath).Equals(chrome, StringComparison.OrdinalIgnoreCase) || IsWithin(chrome, item.DestinationPath))
            { preserved = true; preservedRoots.Add(item.DestinationPath); continue; }
            if (!item.IsDirectory || !IsWithin(item.DestinationPath, chrome)) { result.Add(item); continue; }
            preserved = true;
            var protectedRoot = chrome;
            // Robocopy can purge a missing ancestor without honoring a deeper /XD entry.
            // Keep the highest missing source subtree; it has no incoming files to restore.
            while (Path.GetDirectoryName(protectedRoot) is { } parent && IsWithin(item.DestinationPath, parent)
                && !Directory.Exists(Path.Combine(item.SourcePath, Path.GetRelativePath(item.DestinationPath, parent))))
                protectedRoot = parent;
            preservedRoots.Add(protectedRoot);
            result.Add(new MirrorPlanItem
            {
                Name = item.Name, SourcePath = item.SourcePath, DestinationPath = item.DestinationPath,
                IsDirectory = item.IsDirectory, RemapUserProfilePaths = item.RemapUserProfilePaths,
                MirrorOptions = new FileMirrorOptions
                {
                    ExcludedFiles = item.MirrorOptions.ExcludedFiles,
                    ExcludedDirectories = item.MirrorOptions.ExcludedDirectories.Append(Path.GetRelativePath(item.DestinationPath, protectedRoot)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                }
            });
        }
        return (result, preserved ? [$"Active Chrome plugin cache kept locally, not restored or path-remapped: {chrome}. Preserved subtrees: {string.Join("; ", preservedRoots)}. Source-missing parent folders are also retained to prevent mirror deletion. Conversations and other selected data can still be restored. Close the extension host before a later restore if this cache also needs replacement."] : []);
    }

    public OperationResult Check(IEnumerable<MirrorPlanItem> plan)
    {
        var targets = plan.ToArray();
        var blockers = _processes().Where(process => targets.Any(item =>
            process.Path.Equals(item.DestinationPath, StringComparison.OrdinalIgnoreCase)
            || (item.IsDirectory && IsWithin(item.DestinationPath, process.Path)
                && !item.MirrorOptions.ExcludesDirectory(item.DestinationPath, process.Path))))
            .Select(process => $"PID {process.Id}: {process.Path}").Distinct().ToArray();
        return blockers.Length == 0 ? OperationResult.Ok("No running executables found in restore targets.")
            : OperationResult.Fail("Restore blocked: close programs running inside folders that will be replaced. Run Shuttle from outside the restore targets. Recognized active Chrome plugin caches can be kept locally; other running executables remain protected. No files were copied.", blockers);
    }

    internal static bool IsWithin(string root, string path) => Path.GetFullPath(path)
        .StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<Executable> RunningExecutables()
    {
        var result = new List<Executable>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { } path) result.Add(new(process.Id, path));
                }
                catch (System.ComponentModel.Win32Exception) { /* Other-account processes can deny inspection. */ }
                catch (InvalidOperationException) { /* The process may have exited. */ }
            }
        }
        return result;
    }
}
