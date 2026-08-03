namespace CodexShuttle.Core.Services;

internal static class PathPackageName
{
    public static string FromAbsolutePath(string path)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd('\\');
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var rootName = root.TrimEnd('\\', ':');
        var withoutRoot = fullPath[root.Length..].TrimStart('\\');
        var safe = $"{rootName}_{withoutRoot}".Replace('\\', '_').Replace(':', '_');
        return safe;
    }
}
