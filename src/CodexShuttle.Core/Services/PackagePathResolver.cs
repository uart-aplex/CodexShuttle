namespace CodexShuttle.Core.Services;

public static class PackagePathResolver
{
    public const string TransferFolderName = "CodexTransfer";
    public const string CurrentPackageName = "CodexShuttle-Current";

    public static string NormalizeBackupDestination(string selectedPath)
    {
        return NormalizePackagePath(selectedPath);
    }

    public static string NormalizeRestorePackage(string selectedPath)
    {
        return NormalizePackagePath(selectedPath);
    }

    public static string CreateDefaultBackupDestination()
    {
        var removable = DriveInfo.GetDrives()
            .Where(drive => drive.DriveType == DriveType.Removable && drive.IsReady)
            .OrderByDescending(drive => Directory.Exists(Path.Combine(drive.RootDirectory.FullName, TransferFolderName)))
            .FirstOrDefault();
        var root = removable?.RootDirectory.FullName;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = new[] { @"L:\", @"F:\" }.FirstOrDefault(Directory.Exists);
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        return Path.Combine(root, TransferFolderName, CurrentPackageName);
    }

    private static string NormalizePackagePath(string selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return selectedPath;
        }

        var fullPath = Path.GetFullPath(selectedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directoryName = Path.GetFileName(fullPath);

        if (directoryName.Equals(CurrentPackageName, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        if (directoryName.Equals(TransferFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(fullPath, CurrentPackageName);
        }

        var root = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrWhiteSpace(root) && fullPath.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(fullPath + Path.DirectorySeparatorChar, TransferFolderName, CurrentPackageName);
        }

        return fullPath;
    }
}
