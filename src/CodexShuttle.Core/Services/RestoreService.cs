using CodexShuttle.Core.Models;

namespace CodexShuttle.Core.Services;

public sealed class RestoreService
{
    private readonly FileMirrorService _fileMirrorService;
    private readonly ManifestService _manifestService;
    private readonly RestorePlanner _planner;
    private readonly BackupPackageValidator _packageValidator;
    private readonly ChecksumService _checksumService;
    private readonly StoragePreflightService _storagePreflightService;
    private readonly ProfilePathRemapService _profilePathRemapService;
    private readonly RestorePathResolver _pathResolver;
    private readonly RolloutPathService _rolloutPaths = new();
    private readonly RestoreProcessGuard _processGuard;

    public RestoreService(
        FileMirrorService fileMirrorService,
        ManifestService manifestService,
        RestorePathResolver? pathResolver = null,
        RestoreProcessGuard? processGuard = null)
    {
        _fileMirrorService = fileMirrorService;
        _processGuard = processGuard ?? new RestoreProcessGuard();
        _pathResolver = pathResolver ?? new RestorePathResolver();
        _manifestService = manifestService;
        _planner = new RestorePlanner(pathResolver);
        _packageValidator = new BackupPackageValidator();
        _checksumService = new ChecksumService();
        _storagePreflightService = new StoragePreflightService();
        _profilePathRemapService = new ProfilePathRemapService(pathResolver ?? new RestorePathResolver());
    }

    public async Task<OperationResult> RestoreAsync(
        string backupRoot,
        RestoreOptions options,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        using var packageLock = PackageOperationLock.Acquire(backupRoot, usePackageFileLock: false);
        progress?.Report($"Reading restore package: {backupRoot}");
        var manifest = await _manifestService.LoadAsync(Path.Combine(backupRoot, "manifest.json"), cancellationToken);
        if (manifest is null)
        {
            return OperationResult.Fail("Could not load manifest.json from restore package.");
        }

        var validation = _packageValidator.Validate(backupRoot, manifest, requireChecksums: true);
        if (!validation.Success)
        {
            return validation;
        }

        IReadOnlyList<MirrorPlanItem> plan;
        try
        {
            plan = _planner.CreatePlan(backupRoot, manifest, options);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("Restore plan is unsafe or incomplete.", ex.Message);
        }

        progress?.Report("Checking restore destinations and free space...");
        var targetHome = _pathResolver.ResolveCodexHome(manifest);
        var warnings = new List<string>();
        void PreserveBrowserCache()
        {
            var prepared = _processGuard.PreserveActiveBrowserCache(plan, targetHome);
            plan = prepared.Plan;
            foreach (var warning in prepared.Warnings.Where(warning => !warnings.Contains(warning)))
            { warnings.Add(warning); progress?.Report("Warning: " + warning); }
        }
        PreserveBrowserCache();
        var processCheck = _processGuard.Check(plan);
        if (!processCheck.Success) return processCheck;
        var storageResult = _storagePreflightService.Validate(plan);
        if (!storageResult.Success)
        {
            return storageResult;
        }

        progress?.Report("Verifying Codex history, skills and plugin files...");
        var checksumResult = await _checksumService.VerifySha256FileAsync(
            backupRoot,
            Path.Combine(backupRoot, manifest.ChecksumFile),
            cancellationToken,
            progress);
        if (!checksumResult.Success)
        {
            return checksumResult;
        }

        if (manifest.CodexHomeExists)
        {
            var packagePaths = await _rolloutPaths.CheckAsync(
                Path.Combine(backupRoot, manifest.CodexPackagePath), false, cancellationToken, progress);
            if (!packagePaths.Success) return packagePaths;
        }

        PreserveBrowserCache();
        processCheck = _processGuard.Check(plan);
        if (!processCheck.Success) return processCheck;
        var codexCopyStarted = false;
        foreach (var item in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Restoring {item.Name}");
            codexCopyStarted |= manifest.CodexHomeExists &&
                (item.DestinationPath.Equals(targetHome, StringComparison.OrdinalIgnoreCase)
                    || RestoreProcessGuard.IsWithin(targetHome, item.DestinationPath));
            var result = item.IsDirectory
                ? await _fileMirrorService.MirrorAsync(
                    item.SourcePath,
                    item.DestinationPath,
                    cancellationToken,
                    progress,
                    item.MirrorOptions)
                : await _fileMirrorService.CopyFileAsync(
                    item.SourcePath,
                    item.DestinationPath,
                    cancellationToken,
                    progress);

            if (!result.Success)
            {
                if (codexCopyStarted && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var idle = _processGuard.Check(plan);
                        var recovery = idle.Success
                            ? await PartialRestorePathRecovery.RepairAsync(
                                Path.Combine(backupRoot, manifest.CodexPackagePath), targetHome, cancellationToken, progress)
                            : OperationResult.Fail("Automatic path recovery skipped: a program is running in a restore target.", idle.Errors.ToArray());
                        progress?.Report(recovery.Message);
                        result.Warnings.AddRange(recovery.Warnings);
                        result.Errors.AddRange(recovery.Errors);
                        result.Message = $"Restore incomplete. {result.Message} {recovery.Message}";
                    }
                    catch (OperationCanceledException)
                    {
                        result.Message = $"Restore incomplete. {result.Message} Automatic path recovery canceled.";
                    }
                }
                return result;
            }
        }

        var repairSummary = string.Empty;
        if (manifest.CodexHomeExists)
        {
            progress?.Report("Automatically repairing restored conversation paths...");
            var repaired = await _rolloutPaths.CheckAsync(
                _pathResolver.ResolveCodexHome(manifest), true, cancellationToken, progress);
            if (!repaired.Success) return repaired;
            repairSummary = repaired.Message;
            warnings.AddRange(repaired.Warnings);
            progress?.Report(repairSummary);
        }

        progress?.Report("Mapping source user-profile paths to this Windows account...");
        var remapResult = await _profilePathRemapService.RemapAsync(
            manifest,
            plan,
            cancellationToken,
            progress);
        if (!remapResult.Success)
        {
            return remapResult;
        }

        if (manifest.CodexHomeExists)
        {
            progress?.Report("Verifying saved conversation paths after all restore steps...");
            var restoredPaths = await _rolloutPaths.CheckAsync(
                _pathResolver.ResolveCodexHome(manifest), false, cancellationToken, progress, requireResolvedPaths: true);
            if (!restoredPaths.Success) return restoredPaths;
            progress?.Report(restoredPaths.Message);
        }

        var cacheNote = warnings.Any(warning => warning.StartsWith("Active Chrome plugin cache", StringComparison.Ordinal))
            ? " Active Chrome plugin cache was kept locally (not restored)." : "";
        var completed = OperationResult.Ok($"Restore completed.{cacheNote} {repairSummary} {remapResult.Message}".Trim());
        completed.Warnings.AddRange(warnings);
        progress?.Report(completed.Message);
        return completed;
    }

    public Task<OperationResult> RestoreAsync(
        string backupRoot,
        bool includeAppData,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        return RestoreAsync(
            backupRoot,
            new RestoreOptions { IncludeAppData = includeAppData },
            cancellationToken,
            progress);
    }
}
