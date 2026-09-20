using CodexShuttle.Core.Models;

namespace CodexShuttle.Core.Services;

public sealed class RestorePathResolver
{
    private readonly string? _codexHomeOverride;
    private readonly string _currentUserProfile;
    private readonly bool _hasUserProfileOverride;

    public RestorePathResolver(string? codexHomeOverride = null, string? userProfileOverride = null)
    {
        _codexHomeOverride = string.IsNullOrWhiteSpace(codexHomeOverride)
            ? null
            : Path.GetFullPath(codexHomeOverride);
        _hasUserProfileOverride = !string.IsNullOrWhiteSpace(userProfileOverride);
        _currentUserProfile = !_hasUserProfileOverride
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.GetFullPath(userProfileOverride!);
    }

    public string GetCurrentUserProfile() => _currentUserProfile;

    public string GetCurrentCodexHome()
    {
        if (_codexHomeOverride is not null)
        {
            return _codexHomeOverride;
        }

        var configuredCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        return !string.IsNullOrWhiteSpace(configuredCodexHome)
            ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredCodexHome))
            : Path.Combine(_currentUserProfile, ".codex");
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
        return Path.Combine(_currentUserProfile, ".agents");
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
            var roamingRoot = _hasUserProfileOverride
                ? Path.Combine(_currentUserProfile, "AppData", "Roaming")
                : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(roamingRoot, relative);
        }

        var localIndex = sourcePath.IndexOf(localMarker, StringComparison.OrdinalIgnoreCase);
        if (localIndex >= 0)
        {
            var relative = sourcePath[(localIndex + localMarker.Length)..];
            var localRoot = _hasUserProfileOverride
                ? Path.Combine(_currentUserProfile, "AppData", "Local")
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localRoot, relative);
        }

        return sourcePath;
    }
}
