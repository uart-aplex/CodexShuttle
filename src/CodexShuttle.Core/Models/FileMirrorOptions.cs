namespace CodexShuttle.Core.Models;

public sealed class FileMirrorOptions
{
    public IReadOnlyList<string> ExcludedFiles { get; init; } = [];
    public IReadOnlyList<string> ExcludedDirectories { get; init; } = [];

    public bool ExcludesDirectory(string root, string path)
    {
        var fullPath = Path.GetFullPath(path);
        return ExcludedDirectories.Any(directory =>
        {
            var excluded = Path.GetFullPath(Path.Combine(root, directory)).TrimEnd('\\', '/');
            return fullPath.Equals(excluded, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(excluded + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        });
    }
}
