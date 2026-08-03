using CodexShuttle.Core.Models;

namespace CodexShuttle.Core.Services;

public sealed class RestorePathResolver
{
    private readonly string? _codexHomeOverride;

    public RestorePathResolver(string? codexHomeOverride = null)
    {
        _codexHomeOverride = string.IsNullOrWhiteSpace(codexHomeOverride)
            ? null
            : Path.GetFullPath(codexHomeOverride);
    }

    public string GetCurrentCodexHome()
    {
        if (_codexHomeOverride is not null)
        {
            return _codexHomeOverride;
        }

        var configuredCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        return !string.IsNullOrWhiteSpace(configuredCodexHome)
            ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredCodexHome))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    public string ResolveCodexHome(BackupManifest manifest)
    {
        return GetCurrentCodexHome();
    }

    public string ResolveWorkspaceTarget(WorkspaceEntry workspace)
    {
        return workspace.SourcePath;
    }

    public string ResolveAgentsHome(BackupManifest manifest)
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents");
    }

    public string ResolveAppDataTarget(AppDataEntry appData)
    {
        var sourcePath = appData.SourcePath;
        var roamingMarker = $"{Path.DirectorySeparatorChar}AppData{Path.DirectorySeparatorChar}Roaming{Path.DirectorySeparatorChar}";
        var localMarker = $"{Path.DirectorySeparatorChar}AppData{Path.DirectorySeparatorChar}Local{Path.DirectorySeparatorChar}";

        var roamingIndex = sourcePath.IndexOf(roamingMarker, StringComparison.OrdinalIgnoreCase);
        if (roamingIndex >= 0)
        {
            var relative = sourcePath[(roamingIndex + roamingMarker.Length)..];
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), relative);
        }

        var localIndex = sourcePath.IndexOf(localMarker, StringComparison.OrdinalIgnoreCase);
        if (localIndex >= 0)
        {
            var relative = sourcePath[(localIndex + localMarker.Length)..];
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), relative);
        }

        return sourcePath;
    }
}
