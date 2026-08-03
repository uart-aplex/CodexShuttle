namespace CodexShuttle.Core.Services;

public sealed class PathSafetyService
{
    public string ResolvePackagePath(string packageRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Package path must be relative: {relativePath}");
        }

        var root = NormalizeDirectory(packageRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsStrictChild(root, candidate))
        {
            throw new InvalidDataException($"Package path escapes the selected package: {relativePath}");
        }

        return candidate;
    }

    public void ValidateMirrorPair(string sourcePath, string destinationPath, bool sourceIsDirectory = true)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);

        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase)
            || IsStrictChild(source, destination)
            || IsStrictChild(destination, source))
        {
            throw new InvalidOperationException($"Source and destination overlap: {source} -> {destination}");
        }

        if (sourceIsDirectory && !Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Source folder does not exist: {source}");
        }

        if (!sourceIsDirectory && !File.Exists(source))
        {
            throw new FileNotFoundException("Source file does not exist.", source);
        }

        ValidateDestination(destination, sourceIsDirectory);
    }

    public void ValidateDestination(string destinationPath, bool isDirectory)
    {
        var destination = Path.GetFullPath(destinationPath);
        var directory = isDirectory ? destination : Path.GetDirectoryName(destination) ?? destination;
        var root = Path.GetPathRoot(directory)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalized = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!string.IsNullOrWhiteSpace(root) && normalized.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"A drive or share root cannot be used as a restore target: {directory}");
        }

        var protectedDirectories = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (protectedDirectories.Any(path => !string.IsNullOrWhiteSpace(path)
            && normalized.Equals(path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Protected system/profile folder cannot be mirrored directly: {directory}");
        }
    }

    private static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsStrictChild(string parentPath, string candidatePath)
    {
        var parent = NormalizeDirectory(parentPath) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(candidatePath);
        return candidate.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }
}
