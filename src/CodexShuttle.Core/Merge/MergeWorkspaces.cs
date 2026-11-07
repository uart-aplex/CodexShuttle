using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text.Json;
using CodexShuttle.Core.Models;
using CodexShuttle.Core.Services;

namespace CodexShuttle.Core.Merge;

// Additive workspace transfer is independent of conversation rollback: never delete project files.
internal static class MergeWorkspaces
{
    internal static string Normalize(string path)
    {
        path = path.Replace('/', '\\');
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return MergeFiles.Canonical(path);
    }

    private static bool Overlap(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase) || MergeFiles.Within(a, b) || MergeFiles.Within(b, a);
    private static void ValidateTarget(string target, MergeRequest request, string store)
    {
        new PathSafetyService().ValidateDestination(target, true);
        foreach (var forbidden in new[] { request.PackageRoot, request.ProfileRoot, store,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(p => !string.IsNullOrWhiteSpace(p)))
            if (Overlap(target, Normalize(forbidden))) throw new InvalidDataException($"Workspace overlaps protected data: {target}");
        MergeFiles.NoLinks(target);
        if (!Directory.Exists(Path.GetPathRoot(target))) throw new DirectoryNotFoundException($"Workspace drive/share is unavailable: {target}");
    }

    internal static WorkspaceMergePlan Plan(MergeRequest request, BackupManifest manifest, string store, CancellationToken token, IProgress<string>? progress, WorkspaceMergePlan? approved = null)
    {
        if (!request.IncludeMissingWorkspaceFiles) return WorkspaceMergePlan.Empty;
        var files = new List<WorkspaceAddition>();
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var summaries = new List<WorkspaceMergeSummary>();
        var previous = approved?.Files.ToDictionary(f => f.Source, StringComparer.OrdinalIgnoreCase);
        foreach (var workspace in manifest.WorkspacePaths.Where(w => w.Enabled && w.Exists))
        {
            token.ThrowIfCancellationRequested();
            if (!Path.IsPathFullyQualified(workspace.SourcePath)) throw new InvalidDataException("Workspace source metadata must contain an absolute path.");
            var source = Normalize(new PathSafetyService().ResolvePackagePath(request.PackageRoot, workspace.PackagePath));
            var target = Normalize(MergeFiles.MapCwd(workspace.SourcePath, request, manifest.SourceUserProfile));
            if (request.WorkspaceMappings.Keys.Any(key => MergeFiles.Within(Normalize(workspace.SourcePath), Normalize(key))))
                throw new InvalidDataException("Subfolder mappings require separate backup workspace roots, or turn off workspace copying for this merge.");
            if (Overlap(source, Normalize(new PathSafetyService().ResolvePackagePath(request.PackageRoot, manifest.CodexPackagePath))))
                throw new InvalidDataException("A workspace package cannot overlap the Codex profile package.");
            ValidateTarget(target, request, store);
            new PathSafetyService().ValidateMirrorPair(source, target);
            if (summaries.Any(r => Overlap(r.Target, target) || Overlap(r.Source, source))) throw new InvalidDataException("Overlapping workspace packages/mappings require separate backup roots.");
            var startFiles = files.Count; var startDirs = directories.Count; var retained = 0; var excluded = 0; var scanned = 0;
            progress?.Report($"Scanning missing workspace files: {source} -> {target}");
            void Scan(string from, string to)
            {
                token.ThrowIfCancellationRequested();
                MergeFiles.NoLinks(from); MergeFiles.NoLinks(to);
                if (File.Exists(to)) throw new IOException($"A local file occupies a required workspace directory; nothing will be overwritten: {to}");
                if (!Directory.Exists(to)) directories.Add(to);
                foreach (var entry in Directory.EnumerateFileSystemEntries(from).Order(StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(entry);
                    var destination = Path.Combine(to, name);
                    var isDirectory = File.GetAttributes(entry).HasFlag(FileAttributes.Directory);
                    var patterns = isDirectory ? CodexMigrationPolicy.SensitiveDirectories : CodexMigrationPolicy.SensitiveFiles;
                    if (patterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name, true))) { excluded++; continue; }
                    MergeFiles.NoLinks(entry); MergeFiles.NoLinks(destination);
                    if (isDirectory) Scan(entry, destination);
                    else if (Directory.Exists(destination)) throw new IOException($"A local directory occupies an incoming workspace file; nothing will be overwritten: {destination}");
                    else if (File.Exists(destination)) retained++;
                    else
                    {
                        var info = new FileInfo(entry);
                        var length = info.Length; var modified = info.LastWriteTimeUtc.Ticks;
                        string hash;
                        if (previous is not null && previous.TryGetValue(entry, out var prior) && prior.Target.Equals(destination, StringComparison.OrdinalIgnoreCase)
                            && prior.Length == length && prior.ModifiedUtcTicks == modified)
                            hash = prior.Hash; // Apply verifies the copied bytes against the approved hash before publishing the file.
                        else
                        {
                            progress?.Report($"Checking missing file: {destination}");
                            using var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                            hash = Hash(stream, token, progress, destination);
                        }
                        info.Refresh();
                        if (info.Length != length || info.LastWriteTimeUtc.Ticks != modified) throw new IOException($"Workspace source changed during preview: {entry}");
                        files.Add(new(entry, destination, length, modified, hash));
                    }
                    if (++scanned % 500 == 0) progress?.Report($"Workspace scan: {scanned:N0} entries, {files.Count - startFiles:N0} missing files; current: {destination}");
                }
            }
            Scan(source, target);
            summaries.Add(new(source, target, files.Count - startFiles, directories.Count - startDirs, retained, excluded, files.Skip(startFiles).Sum(f => f.Length)));
        }
        return new(files, directories.OrderBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(), summaries);
    }

    internal static bool SuppliesDirectory(WorkspaceMergePlan plan, string cwd) => Path.IsPathFullyQualified(cwd)
        && plan.Directories.Contains(Normalize(cwd), StringComparer.OrdinalIgnoreCase);

    internal static string Hash(Stream input, CancellationToken token, IProgress<string>? progress, string path)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024]; long bytes = 0; int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); bytes += read;
            if (bytes % (64 * 1024 * 1024) == 0) progress?.Report($"Checking: {path} ({ReportService.FormatBytes(bytes)})");
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static void Apply(WorkspaceMergePlan plan, string store, MergeRequest request, CancellationToken token, IProgress<string>? progress, Action<string>? checkpoint)
    {
        if (!plan.HasChanges) return;
        foreach (var root in plan.Roots) ValidateTarget(root.Target, request, store);
        foreach (var group in plan.Files.GroupBy(f => Path.GetPathRoot(f.Target), StringComparer.OrdinalIgnoreCase))
        {
            var drive = new DriveInfo(group.Key!);
            var bytes = group.Sum(f => f.Length);
            if (!drive.IsReady || drive.AvailableFreeSpace < bytes + Math.Max(512L * 1024 * 1024, bytes / 20)) throw new IOException($"Insufficient workspace destination space: {group.Key}");
            if (drive.DriveFormat.Equals("FAT32", StringComparison.OrdinalIgnoreCase) && group.Any(f => f.Length > uint.MaxValue)) throw new IOException("A workspace file exceeds FAT32's file size limit.");
        }
        var receipt = Path.Combine(store, "workspace-runs", Guid.NewGuid().ToString("N") + ".jsonl");
        MergeFiles.NoLinks(receipt); Directory.CreateDirectory(Path.GetDirectoryName(receipt)!);
        using var log = new StreamWriter(new FileStream(receipt, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough)) { AutoFlush = true };
        progress?.Report($"Adding missing workspace files. Existing files are kept. Receipt: {receipt}");
        foreach (var directory in plan.Directories)
        {
            token.ThrowIfCancellationRequested();
            if (!plan.Roots.Any(r => directory.Equals(r.Target, StringComparison.OrdinalIgnoreCase) || MergeFiles.Within(r.Target, directory))) throw new InvalidDataException("Workspace directory escapes approved roots.");
            MergeFiles.NoLinks(directory);
            if (File.Exists(directory)) throw new IOException($"Workspace destination changed: {directory}");
            Directory.CreateDirectory(directory);
            log.WriteLine(JsonSerializer.Serialize(new { Directory = directory }));
        }
        var completed = 0;
        foreach (var file in plan.Files)
        {
            token.ThrowIfCancellationRequested();
            if (!plan.Roots.Any(r => MergeFiles.Within(r.Source, file.Source) && MergeFiles.Within(r.Target, file.Target))) throw new InvalidDataException("Workspace file escapes approved roots.");
            MergeFiles.NoLinks(file.Source); MergeFiles.NoLinks(file.Target);
            if (File.Exists(file.Target) || Directory.Exists(file.Target)) throw new IOException($"A local file appeared after preview; it was not overwritten: {file.Target}");
            var temp = file.Target + ".codexshuttle-" + Guid.NewGuid().ToString("N") + ".tmp";
            log.WriteLine(JsonSerializer.Serialize(new { Pending = file.Target, Temporary = temp, file.Hash }));
            var ownsTemp = false;
            try
            {
                using (var input = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ownsTemp = true;
                    var buffer = new byte[1024 * 1024]; long copied = 0; int read;
                    progress?.Report($"Adding workspace file {completed + 1:N0}/{plan.Files.Count:N0}: {file.Target}");
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); copied += read;
                        if (copied % (64 * 1024 * 1024) == 0) progress?.Report($"Copying: {file.Target} ({ReportService.FormatBytes(copied)} / {ReportService.FormatBytes(file.Length)})");
                    }
                    output.Flush(true);
                    if (copied != file.Length) throw new IOException($"Workspace source length changed: {file.Source}");
                }
                using (var verify = File.OpenRead(temp))
                    if (Hash(verify, token, progress, file.Target) != file.Hash) throw new IOException($"Workspace source/content changed after preview: {file.Source}");
                token.ThrowIfCancellationRequested(); MergeFiles.NoLinks(file.Target);
                File.SetLastWriteTimeUtc(temp, new DateTime(file.ModifiedUtcTicks, DateTimeKind.Utc));
                File.Move(temp, file.Target, overwrite: false);
                log.WriteLine(JsonSerializer.Serialize(new { Added = file.Target, file.Hash }));
                completed++;
                checkpoint?.Invoke("WorkspaceFileWritten");
            }
            finally { if (ownsTemp && File.Exists(temp)) File.Delete(temp); }
        }
        log.WriteLine(JsonSerializer.Serialize(new { Completed = true, AddedFiles = completed }));
        progress?.Report($"Workspace additions completed: {completed:N0} files. Existing files were not changed.");
    }
}
