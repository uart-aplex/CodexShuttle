using System.IO.Enumeration;

namespace CodexShuttle.Core.Services;

public sealed class SensitiveDataService
{
    public IReadOnlyList<string> FindSensitiveFiles(string profilePackagePath)
    {
        if (!Directory.Exists(profilePackagePath))
        {
            return [];
        }

        return Directory.EnumerateFiles(profilePackagePath, "*", SearchOption.AllDirectories)
            .Where(file =>
            {
                var relativeSegments = Path.GetRelativePath(profilePackagePath, file)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return relativeSegments.Length > 1
                       && CodexMigrationPolicy.SanitizedProfileOptions.ExcludedDirectories.Contains(relativeSegments[0], StringComparer.OrdinalIgnoreCase)
                       || CodexMigrationPolicy.SanitizedProfileOptions.ExcludedFiles.Any(pattern => MatchesPattern(Path.GetFileName(file), pattern));
            })
            .ToList();
    }

    public void RemoveSensitiveArtifacts(string profilePackagePath)
    {
        if (!Directory.Exists(profilePackagePath))
        {
            return;
        }

        foreach (var directoryName in CodexMigrationPolicy.SanitizedProfileOptions.ExcludedDirectories)
        {
            var directory = Path.Combine(profilePackagePath, directoryName);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        foreach (var file in Directory.EnumerateFiles(profilePackagePath, "*", SearchOption.AllDirectories).ToList())
        {
            var fileName = Path.GetFileName(file);
            if (CodexMigrationPolicy.SanitizedProfileOptions.ExcludedFiles.Any(pattern => MatchesPattern(fileName, pattern)))
            {
                File.Delete(file);
            }
        }
    }

    private static bool MatchesPattern(string fileName, string pattern)
    {
        return FileSystemName.MatchesSimpleExpression(pattern, fileName, ignoreCase: true);
    }
}
