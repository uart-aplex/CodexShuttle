using System.Security.Cryptography;
using CodexShuttle.Core.Models;

namespace CodexShuttle.Core.Services;

public sealed class BackupService
{
    public const string InProgressMarkerFileName = "backup-in-progress.marker";
    public const string CompleteMarkerFileName = "backup-complete.marker";

    private readonly CodexDetector _detector;
    private readonly CodexInspector _inspector;
    private readonly FileMirrorService _fileMirrorService;
    private readonly ManifestService _manifestService;
    private readonly ReportService _reportService;
    private readonly PathSafetyService _pathSafetyService = new();
    private readonly BackupTransactionService _transactionService = new();
    private readonly ChecksumService _checksumService = new();
    private readonly SensitiveDataService _sensitiveDataService = new();

    public BackupService(
        CodexDetector detector,
        CodexInspector inspector,
        FileMirrorService fileMirrorService,
        ManifestService manifestService,
        ReportService reportService)
    {
        _detector = detector;
        _inspector = inspector;
        _fileMirrorService = fileMirrorService;
        _manifestService = manifestService;
        _reportService = reportService;
    }

    public async Task<OperationResult> CreateBackupAsync(
        string backupRoot,
        bool includeAppData,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(backupRoot))
        {
            return OperationResult.Fail("Backup destination is required.");
        }

        backupRoot = Path.GetFullPath(backupRoot);
        progress?.Report("Scanning Codex profile, migration tools and workspaces...");
        var detection = _detector.Detect();
        var inspection = _inspector.Inspect(detection.CodexHome);
        var manifest = _manifestService.CreateManifest(detection, inspection, backupRoot, includeAppData);

        IReadOnlyList<BackupDataSet> dataSets;
        try
        {
            dataSets = BuildDataSets(backupRoot, detection, includeAppData);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("Backup path validation failed.", ex.Message);
        }

        Directory.CreateDirectory(backupRoot);
        using var packageLock = PackageOperationLock.Acquire(backupRoot);
        await _transactionService.RecoverAsync(backupRoot, cancellationToken, progress);

        var inProgressMarker = Path.Combine(backupRoot, InProgressMarkerFileName);
        var completeMarker = Path.Combine(backupRoot, CompleteMarkerFileName);
        await File.WriteAllTextAsync(inProgressMarker, $"StartedAt={DateTimeOffset.Now:O}", cancellationToken);
        var completionMarkerWritten = false;

        try
        {
            var protectedFiles = new List<string> { "manifest.json", "backup-report.txt", manifest.ChecksumFile };
            protectedFiles.AddRange(_sensitiveDataService
                .FindSensitiveFiles(Path.Combine(backupRoot, manifest.CodexPackagePath))
                .Select(path => Path.GetRelativePath(backupRoot, path)));
            protectedFiles.AddRange(_sensitiveDataService
                .FindSensitiveFiles(Path.Combine(backupRoot, manifest.AgentsPackagePath))
                .Select(path => Path.GetRelativePath(backupRoot, path)));

            await _transactionService.PrepareAsync(
                backupRoot,
                dataSets,
                protectedFiles,
                cancellationToken,
                progress);

            foreach (var dataSet in dataSets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Backing up {dataSet.Name}");
                var copyResult = await _fileMirrorService.MirrorAsync(
                    dataSet.SourcePath,
                    dataSet.DestinationPath,
                    cancellationToken,
                    progress,
                    dataSet.MirrorOptions);
                if (!copyResult.Success)
                {
                    await RollBackFailedBackupAsync(backupRoot, inProgressMarker, progress);
                    return copyResult;
                }
            }

            _sensitiveDataService.RemoveSensitiveArtifacts(Path.Combine(backupRoot, manifest.CodexPackagePath));
            _sensitiveDataService.RemoveSensitiveArtifacts(Path.Combine(backupRoot, manifest.AgentsPackagePath));

            if (detection.CodexHomeExists)
            {
                progress?.Report("Validating copied Codex sessions and SQLite state...");
                var backupInspection = _inspector.Inspect(Path.Combine(backupRoot, manifest.CodexPackagePath));
                var inspectionError = ValidateCopiedCodex(inspection, backupInspection);
                if (inspectionError is not null)
                {
                    await RollBackFailedBackupAsync(backupRoot, inProgressMarker, progress);
                    return OperationResult.Fail("Copied Codex state did not pass validation.", inspectionError);
                }

                manifest.CodexInspection = backupInspection;
            }

            progress?.Report("Writing manifest and backup report...");
            await _manifestService.SaveAsync(manifest, Path.Combine(backupRoot, "manifest.json"), cancellationToken);
            await _reportService.SaveBackupReportAsync(manifest, Path.Combine(backupRoot, "backup-report.txt"), cancellationToken);

            progress?.Report("Checksumming Codex history, skills and plugins...");
            var checksumRoots = new List<string> { manifest.CodexPackagePath, "manifest.json", "backup-report.txt" };
            if (manifest.AgentsHomeExists)
            {
                checksumRoots.Add(manifest.AgentsPackagePath);
            }

            var checksumPath = Path.Combine(backupRoot, manifest.ChecksumFile);
            await _checksumService.SaveSha256FileAsync(
                backupRoot,
                checksumRoots,
                checksumPath,
                cancellationToken,
                progress);

            await using (var checksumStream = File.OpenRead(checksumPath))
            {
                var checksumHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(checksumStream, cancellationToken)).ToLowerInvariant();
                await File.WriteAllTextAsync(
                    completeMarker,
                    $"CompletedAt={DateTimeOffset.Now:O}{Environment.NewLine}SchemaVersion={manifest.SchemaVersion}{Environment.NewLine}ChecksumFileSha256={checksumHash}",
                    cancellationToken);
                completionMarkerWritten = true;
            }

            await _transactionService.CommitAsync(backupRoot);
            File.Delete(inProgressMarker);
            progress?.Report("Backup completed, verified and safe to restore.");
            return OperationResult.Ok("Backup completed.");
        }
        catch (OperationCanceledException)
        {
            await RollBackFailedBackupAsync(backupRoot, inProgressMarker, progress);
            throw;
        }
        catch (Exception ex)
        {
            if (completionMarkerWritten)
            {
                if (File.Exists(inProgressMarker))
                {
                    File.Delete(inProgressMarker);
                }

                progress?.Report("Backup completed and verified, but rollback cleanup will be retried next time.");
                var completedResult = OperationResult.Ok("Backup completed with a cleanup warning.");
                completedResult.Warnings.Add(ex.Message);
                return completedResult;
            }

            try
            {
                await RollBackFailedBackupAsync(backupRoot, inProgressMarker, progress);
            }
            catch (Exception rollbackException)
            {
                return OperationResult.Fail(
                    "Backup failed and automatic rollback also failed. Do not restore this package.",
                    ex.Message,
                    rollbackException.Message);
            }

            return OperationResult.Fail("Backup failed; the previous complete package was restored.", ex.Message);
        }
    }

    private IReadOnlyList<BackupDataSet> BuildDataSets(
        string backupRoot,
        CodexDetectionResult detection,
        bool includeAppData)
    {
        var dataSets = new List<BackupDataSet>();
        if (detection.CodexHomeExists)
        {
            AddDataSet(dataSets, "Codex history, settings, skills and plugins", detection.CodexHome, Path.Combine(backupRoot, ".codex"), CodexMigrationPolicy.SanitizedProfileOptions);
        }

        if (detection.AgentsHomeExists)
        {
            AddDataSet(dataSets, "personal plugin marketplace (.agents)", detection.AgentsHome, Path.Combine(backupRoot, ".agents"), CodexMigrationPolicy.SanitizedProfileOptions);
        }

        foreach (var workspace in detection.WorkspacePaths.Where(path => path.Enabled && path.Exists))
        {
            AddDataSet(dataSets, $"workspace {workspace.SourcePath}", workspace.SourcePath, Path.Combine(backupRoot, workspace.PackagePath), new FileMirrorOptions());
        }

        if (includeAppData)
        {
            foreach (var appData in detection.AppDataPaths.Where(path => path.Exists && path.Included))
            {
                AddDataSet(dataSets, $"optional AppData {appData.SourcePath}", appData.SourcePath, Path.Combine(backupRoot, appData.PackagePath), CodexMigrationPolicy.SanitizedProfileOptions);
            }
        }

        return dataSets;
    }

    private void AddDataSet(
        List<BackupDataSet> dataSets,
        string name,
        string sourcePath,
        string destinationPath,
        FileMirrorOptions options)
    {
        _pathSafetyService.ValidateMirrorPair(sourcePath, destinationPath);
        dataSets.Add(new BackupDataSet
        {
            Name = name,
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            MirrorOptions = options
        });
    }

    private async Task RollBackFailedBackupAsync(
        string backupRoot,
        string inProgressMarker,
        IProgress<string>? progress)
    {
        await _transactionService.RecoverAsync(backupRoot, CancellationToken.None, progress);
        if (File.Exists(inProgressMarker))
        {
            File.Delete(inProgressMarker);
        }
    }

    private static string? ValidateCopiedCodex(
        CodexInspectionResult source,
        CodexInspectionResult backup)
    {
        if (source.SessionsFileCount != backup.SessionsFileCount)
        {
            return $"Session file count differs: source {source.SessionsFileCount:N0}, backup {backup.SessionsFileCount:N0}.";
        }

        if (source.ArchivedSessionsFileCount != backup.ArchivedSessionsFileCount)
        {
            return $"Archived session count differs: source {source.ArchivedSessionsFileCount:N0}, backup {backup.ArchivedSessionsFileCount:N0}.";
        }

        if (source.StateSqliteExists && !backup.StateSqliteReadable)
        {
            return "The copied state_5.sqlite did not pass SQLite quick_check.";
        }

        if (source.SessionIndexExists && !backup.SessionIndexReadable)
        {
            return "The copied session_index.jsonl could not be read.";
        }

        if (source.GlobalStateExists && !backup.GlobalStateJsonValid)
        {
            return "The copied .codex-global-state.json is invalid.";
        }

        return null;
    }
}
