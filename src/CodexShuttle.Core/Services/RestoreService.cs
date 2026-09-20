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

    public RestoreService(
        FileMirrorService fileMirrorService,
        ManifestService manifestService,
        RestorePathResolver? pathResolver = null)
    {
        _fileMirrorService = fileMirrorService;
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

        foreach (var item in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Restoring {item.Name}");
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
                return result;
            }
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

        progress?.Report($"Restore completed. {remapResult.Message}");
        return OperationResult.Ok($"Restore completed. {remapResult.Message}");
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
