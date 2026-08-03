using CodexShuttle.Core.Models;

namespace CodexShuttle.Core.Services;

public sealed class RestorePlanner
{
    private readonly PathSafetyService _pathSafety = new();
    private readonly RestorePathResolver _pathResolver;

    public RestorePlanner(RestorePathResolver? pathResolver = null)
    {
        _pathResolver = pathResolver ?? new RestorePathResolver();
    }

    public IReadOnlyList<MirrorPlanItem> CreatePlan(
        string packageRoot,
        BackupManifest manifest,
        RestoreOptions options)
    {
        var plan = new List<MirrorPlanItem>();

        foreach (var workspace in manifest.WorkspacePaths.Where(item => item.Enabled && item.Exists))
        {
            if (!options.WorkspaceTargets.TryGetValue(workspace.SourcePath, out var target)
                || string.IsNullOrWhiteSpace(target))
            {
                target = workspace.SourcePath;
            }

            if (options.RequireOriginalWorkspacePaths
                && !Path.GetFullPath(target).Equals(Path.GetFullPath(workspace.SourcePath), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Workspace paths must remain unchanged so paths stored in Codex conversations stay valid: {workspace.SourcePath}");
            }

            var source = _pathSafety.ResolvePackagePath(packageRoot, workspace.PackagePath);
            _pathSafety.ValidateMirrorPair(source, target);
            plan.Add(new MirrorPlanItem
            {
                Name = $"Workspace: {workspace.SourcePath}",
                SourcePath = source,
                DestinationPath = Path.GetFullPath(target)
            });
        }

        if (manifest.SchemaVersion >= 2 && manifest.AgentsHomeExists)
        {
            var agentsSource = _pathSafety.ResolvePackagePath(packageRoot, manifest.AgentsPackagePath);
            if (Directory.Exists(agentsSource))
            {
                var agentsTarget = _pathResolver.ResolveAgentsHome(manifest);
                _pathSafety.ValidateMirrorPair(agentsSource, agentsTarget);
                plan.Add(new MirrorPlanItem
                {
                    Name = "Personal plugin marketplace (.agents)",
                    SourcePath = agentsSource,
                    DestinationPath = agentsTarget,
                    MirrorOptions = CodexMigrationPolicy.CredentialOnlyOptions
                });
            }
        }

        if (manifest.CodexHomeExists || manifest.SchemaVersion == 1)
        {
            AddCodexItems(packageRoot, manifest, options.MigrationMode, plan);
        }

        if (options.IncludeAppData)
        {
            foreach (var appData in manifest.AppDataPaths.Where(item => item.Included))
            {
                var source = _pathSafety.ResolvePackagePath(packageRoot, appData.PackagePath);
                var target = _pathResolver.ResolveAppDataTarget(appData);
                _pathSafety.ValidateMirrorPair(source, target);
                plan.Add(new MirrorPlanItem
                {
                    Name = $"AppData: {appData.SourcePath}",
                    SourcePath = source,
                    DestinationPath = target,
                    MirrorOptions = CodexMigrationPolicy.SanitizedProfileOptions
                });
            }
        }

        return plan;
    }

    private void AddCodexItems(
        string packageRoot,
        BackupManifest manifest,
        MigrationMode migrationMode,
        List<MirrorPlanItem> plan)
    {
        var codexSource = _pathSafety.ResolvePackagePath(packageRoot, manifest.CodexPackagePath);
        var codexTarget = _pathResolver.ResolveCodexHome(manifest);

        if (migrationMode == MigrationMode.FullProfile)
        {
            _pathSafety.ValidateMirrorPair(codexSource, codexTarget);
            plan.Add(new MirrorPlanItem
            {
                Name = "Codex profile, history, skills and plugins",
                SourcePath = codexSource,
                DestinationPath = codexTarget,
                MirrorOptions = CodexMigrationPolicy.SanitizedProfileOptions
            });
            return;
        }

        foreach (var directoryName in CodexMigrationPolicy.HistoryAndToolDirectories)
        {
            var source = Path.Combine(codexSource, directoryName);
            if (!Directory.Exists(source))
            {
                continue;
            }

            var target = Path.Combine(codexTarget, directoryName);
            _pathSafety.ValidateMirrorPair(source, target);
            plan.Add(new MirrorPlanItem
            {
                Name = $"Codex {directoryName}",
                SourcePath = source,
                DestinationPath = target,
                MirrorOptions = CodexMigrationPolicy.CredentialOnlyOptions
            });
        }

        foreach (var fileName in CodexMigrationPolicy.HistoryAndToolFiles)
        {
            var source = Path.Combine(codexSource, fileName);
            if (!File.Exists(source))
            {
                continue;
            }

            var target = Path.Combine(codexTarget, fileName);
            _pathSafety.ValidateMirrorPair(source, target, sourceIsDirectory: false);
            plan.Add(new MirrorPlanItem
            {
                Name = $"Codex state: {fileName}",
                SourcePath = source,
                DestinationPath = target,
                IsDirectory = false
            });
        }
    }
}
