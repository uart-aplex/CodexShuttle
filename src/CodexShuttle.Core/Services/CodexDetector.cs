using CodexShuttle.Core.Models;

namespace CodexShuttle.Core.Services;

public sealed class CodexDetector
{
    public static readonly string[] DefaultWorkspacePaths =
    [
        @"E:\app",
        @"E:\doc",
        @"E:\CodexWorkspace"
    ];

    private readonly IReadOnlyList<string> _workspacePaths;

    private readonly string? _codexHomeOverride;

    public CodexDetector(IEnumerable<string>? workspacePaths = null, string? codexHomeOverride = null)
    {
        _workspacePaths = (workspacePaths ?? DefaultWorkspacePaths)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _codexHomeOverride = string.IsNullOrWhiteSpace(codexHomeOverride)
            ? null
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(codexHomeOverride));
    }

    public CodexDetectionResult Detect()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configuredCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var codexHome = _codexHomeOverride ?? (!string.IsNullOrWhiteSpace(configuredCodexHome)
            ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredCodexHome))
            : Path.Combine(userProfile, ".codex"));
        var agentsHome = Path.Combine(userProfile, ".agents");

        return new CodexDetectionResult
        {
            UserProfile = userProfile,
            CodexHome = codexHome,
            CodexHomeExists = Directory.Exists(codexHome),
            AgentsHome = agentsHome,
            AgentsHomeExists = Directory.Exists(agentsHome),
            WorkspacePaths = _workspacePaths.Select(CreateWorkspaceEntry).ToList(),
            AppDataPaths = CreateAppDataEntries()
        };
    }

    private static WorkspaceEntry CreateWorkspaceEntry(string sourcePath)
    {
        var stats = DirectoryStats.Get(sourcePath);
        return new WorkspaceEntry
        {
            SourcePath = sourcePath,
            PackagePath = PathPackageName.FromAbsolutePath(sourcePath),
            Enabled = Directory.Exists(sourcePath),
            Exists = Directory.Exists(sourcePath),
            FileCount = stats.FileCount,
            TotalBytes = stats.TotalBytes,
            LatestWriteTime = stats.LatestWriteTime
        };
    }

    private static List<AppDataEntry> CreateAppDataEntries()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var paths = new[]
        {
            Path.Combine(appData, "Codex"),
            Path.Combine(localAppData, "Codex"),
            Path.Combine(appData, "OpenAI"),
            Path.Combine(localAppData, "OpenAI")
        };

        return paths.Select(path =>
        {
            var stats = DirectoryStats.Get(path);
            return new AppDataEntry
            {
                SourcePath = path,
                PackagePath = Path.Combine("appdata_optional", PathPackageName.FromAbsolutePath(path)),
                Exists = Directory.Exists(path),
                Included = false,
                FileCount = stats.FileCount,
                TotalBytes = stats.TotalBytes
            };
        }).ToList();
    }
}
