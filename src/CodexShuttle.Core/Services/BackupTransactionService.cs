using System.Text.Json;
using CodexShuttle.Core.Models;

namespace CodexShuttle.Core.Services;

public sealed class BackupTransactionService
{
    private const string JournalFileName = "journal.json";
    private const string CommittedMarkerFileName = "committed.marker";
    private readonly DryRunService _dryRunService = new();

    public async Task PrepareAsync(
        string backupRoot,
        IReadOnlyList<BackupDataSet> dataSets,
        IReadOnlyList<string> protectedRelativeFiles,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        if (!File.Exists(Path.Combine(backupRoot, BackupService.CompleteMarkerFileName)))
        {
            return;
        }

        await RecoverAsync(backupRoot, cancellationToken, progress);
        var transactionRoot = GetTransactionRoot(backupRoot);
        Directory.CreateDirectory(transactionRoot);

        var journal = new BackupTransactionJournal
        {
            BackupRoot = Path.GetFullPath(backupRoot)
        };

        foreach (var dataSet in dataSets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Preparing safe differential update: {dataSet.Name}");
            var dryRun = _dryRunService.CompareMirror(
                dataSet.SourcePath,
                dataSet.DestinationPath,
                dataSet.MirrorOptions);

            foreach (var operation in dryRun.Operations)
            {
                var destination = Path.GetFullPath(operation.DestinationPath);
                var relative = Path.GetRelativePath(backupRoot, destination);
                if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Transaction path escapes backup package: {destination}");
                }

                journal.Entries.Add(new BackupTransactionEntry
                {
                    Operation = operation.Operation,
                    RelativeDestinationPath = relative
                });
            }
        }

        foreach (var relativeFile in protectedRelativeFiles)
        {
            var path = Path.Combine(backupRoot, relativeFile);
            journal.Entries.Add(new BackupTransactionEntry
            {
                Operation = File.Exists(path) ? "Overwrite" : "Add",
                RelativeDestinationPath = relativeFile
            });
        }

        journal.Entries = journal.Entries
            .GroupBy(item => item.RelativeDestinationPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var journalPath = Path.Combine(transactionRoot, JournalFileName);
        await File.WriteAllTextAsync(
            journalPath,
            JsonSerializer.Serialize(journal, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        foreach (var entry in journal.Entries.Where(item => item.Operation is "Overwrite" or "Delete"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(backupRoot, entry.RelativeDestinationPath);
            if (!File.Exists(destination))
            {
                continue;
            }

            var rollbackPath = Path.Combine(transactionRoot, "data", entry.RelativeDestinationPath);
            Directory.CreateDirectory(Path.GetDirectoryName(rollbackPath) ?? transactionRoot);
            File.Move(destination, rollbackPath, overwrite: true);
        }
    }

    public async Task CommitAsync(string backupRoot)
    {
        var transactionRoot = GetTransactionRoot(backupRoot);
        if (!Directory.Exists(transactionRoot))
        {
            return;
        }

        await File.WriteAllTextAsync(
            Path.Combine(transactionRoot, CommittedMarkerFileName),
            $"CommittedAt={DateTimeOffset.Now:O}");
        DeleteTransactionDirectory(transactionRoot);
    }

    public async Task RecoverAsync(
        string backupRoot,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        var transactionRoot = GetTransactionRoot(backupRoot);
        var journalPath = Path.Combine(transactionRoot, JournalFileName);
        if (!Directory.Exists(transactionRoot))
        {
            return;
        }

        if (File.Exists(Path.Combine(transactionRoot, CommittedMarkerFileName)))
        {
            progress?.Report("Cleaning a committed backup transaction...");
            DeleteTransactionDirectory(transactionRoot);
            return;
        }

        if (!File.Exists(journalPath))
        {
            progress?.Report("Cleaning an orphaned backup transaction folder...");
            DeleteTransactionDirectory(transactionRoot);
            return;
        }

        progress?.Report("Recovering the last complete backup after an interrupted update...");
        var json = await File.ReadAllTextAsync(journalPath, cancellationToken);
        var journal = JsonSerializer.Deserialize<BackupTransactionJournal>(json)
            ?? throw new InvalidDataException("Backup rollback journal is invalid.");
        if (!Path.GetFullPath(backupRoot).Equals(Path.GetFullPath(journal.BackupRoot), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Backup rollback journal points to another package.");
        }

        foreach (var entry in journal.Entries.AsEnumerable().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.IsPathRooted(entry.RelativeDestinationPath)
                || entry.RelativeDestinationPath.StartsWith("..", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Backup rollback journal contains an unsafe path.");
            }

            var destination = Path.Combine(backupRoot, entry.RelativeDestinationPath);
            var rollbackPath = Path.Combine(transactionRoot, "data", entry.RelativeDestinationPath);

            if (entry.Operation == "Add")
            {
                if (File.Exists(destination))
                {
                    ClearReadOnly(destination);
                    File.Delete(destination);
                }
                continue;
            }

            if (!File.Exists(rollbackPath))
            {
                continue;
            }

            if (File.Exists(destination))
            {
                ClearReadOnly(destination);
                File.Delete(destination);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? backupRoot);
            File.Move(rollbackPath, destination, overwrite: true);
        }

        DeleteTransactionDirectory(transactionRoot);
    }

    private static string GetTransactionRoot(string backupRoot) =>
        Path.GetFullPath(backupRoot).TrimEnd('\\', '/') + ".rollback";

    private static void DeleteTransactionDirectory(string transactionRoot)
    {
        if (!Directory.Exists(transactionRoot))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(transactionRoot, "*", SearchOption.AllDirectories))
        {
            ClearReadOnly(file);
        }

        Directory.Delete(transactionRoot, recursive: true);
    }

    private static void ClearReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private sealed class BackupTransactionJournal
    {
        public string BackupRoot { get; set; } = string.Empty;
        public List<BackupTransactionEntry> Entries { get; set; } = new();
    }

    private sealed class BackupTransactionEntry
    {
        public string Operation { get; set; } = string.Empty;
        public string RelativeDestinationPath { get; set; } = string.Empty;
    }
}
