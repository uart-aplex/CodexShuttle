using CodexShuttle.Core.Models;
using System.IO.Enumeration;

namespace CodexShuttle.Core.Services;

public sealed class DryRunService
{
    public DryRunResult ComparePlan(IEnumerable<MirrorPlanItem> plan)
    {
        var aggregate = new DryRunResult();
        foreach (var item in plan)
        {
            var itemResult = item.IsDirectory
                ? CompareMirror(item.SourcePath, item.DestinationPath, item.MirrorOptions)
                : CompareFile(item.SourcePath, item.DestinationPath);
            aggregate.FilesToCopy += itemResult.FilesToCopy;
            aggregate.FilesToOverwrite += itemResult.FilesToOverwrite;
            aggregate.FilesToDelete += itemResult.FilesToDelete;
            aggregate.TotalBytesToCopy += itemResult.TotalBytesToCopy;
            aggregate.Operations.AddRange(itemResult.Operations);
            aggregate.Warnings.AddRange(itemResult.Warnings.Select(warning => $"{item.Name}: {warning}"));
        }

        return aggregate;
    }

    public DryRunResult CompareMirror(
        string sourceRoot,
        string destinationRoot,
        FileMirrorOptions? options = null)
    {
        var result = new DryRunResult();

        if (!Directory.Exists(sourceRoot))
        {
            result.Warnings.Add($"Source folder does not exist: {sourceRoot}");
            return result;
        }

        options ??= new FileMirrorOptions();
        var sourceFiles = EnumerateRelativeFiles(sourceRoot, options);
        var destinationFiles = Directory.Exists(destinationRoot)
            ? EnumerateRelativeFiles(destinationRoot, options)
            : new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var (relativePath, sourceFile) in sourceFiles)
        {
            var destinationPath = Path.Combine(destinationRoot, relativePath);
            if (!destinationFiles.TryGetValue(relativePath, out var destinationFile))
            {
                result.FilesToCopy++;
                result.TotalBytesToCopy += sourceFile.Length;
                result.Operations.Add(new FileOperationPreview
                {
                    Operation = "Add",
                    SourcePath = sourceFile.FullName,
                    DestinationPath = destinationPath,
                    Bytes = sourceFile.Length
                });
                continue;
            }

            if (sourceFile.Length != destinationFile.Length
                || Math.Abs((sourceFile.LastWriteTimeUtc - destinationFile.LastWriteTimeUtc).TotalSeconds) > 2)
            {
                result.FilesToOverwrite++;
                result.TotalBytesToCopy += sourceFile.Length;
                result.Operations.Add(new FileOperationPreview
                {
                    Operation = "Overwrite",
                    SourcePath = sourceFile.FullName,
                    DestinationPath = destinationFile.FullName,
                    Bytes = sourceFile.Length
                });
            }
        }

        foreach (var (relativePath, destinationFile) in destinationFiles)
        {
            if (sourceFiles.ContainsKey(relativePath))
            {
                continue;
            }

            result.FilesToDelete++;
            result.Operations.Add(new FileOperationPreview
            {
                Operation = "Delete",
                DestinationPath = destinationFile.FullName,
                Bytes = destinationFile.Length
            });
        }

        return result;
    }

    public DryRunResult CompareFile(string sourcePath, string destinationPath)
    {
        var result = new DryRunResult();
        if (!File.Exists(sourcePath))
        {
            result.Warnings.Add($"Source file does not exist: {sourcePath}");
            return result;
        }

        var source = new FileInfo(sourcePath);
        if (!File.Exists(destinationPath))
        {
            result.FilesToCopy = 1;
            result.TotalBytesToCopy = source.Length;
            result.Operations.Add(new FileOperationPreview
            {
                Operation = "Add",
                SourcePath = sourcePath,
                DestinationPath = destinationPath,
                Bytes = source.Length
            });
            return result;
        }

        var destination = new FileInfo(destinationPath);
        if (source.Length != destination.Length
            || Math.Abs((source.LastWriteTimeUtc - destination.LastWriteTimeUtc).TotalSeconds) > 2)
        {
            result.FilesToOverwrite = 1;
            result.TotalBytesToCopy = source.Length;
            result.Operations.Add(new FileOperationPreview
            {
                Operation = "Overwrite",
                SourcePath = sourcePath,
                DestinationPath = destinationPath,
                Bytes = source.Length
            });
        }

        return result;
    }

    private static Dictionary<string, FileInfo> EnumerateRelativeFiles(string root, FileMirrorOptions options)
    {
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => new FileInfo(file))
            .Where(file => !IsExcluded(root, file.FullName, options))
            .ToDictionary(
                file => Path.GetRelativePath(root, file.FullName),
                file => file,
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsExcluded(string root, string filePath, FileMirrorOptions options)
    {
        var relative = Path.GetRelativePath(root, filePath);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Length > 1
            && options.ExcludedDirectories.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var fileName = Path.GetFileName(filePath);
        return options.ExcludedFiles.Any(pattern => MatchesPattern(fileName, pattern));
    }

    private static bool MatchesPattern(string fileName, string pattern)
    {
        return FileSystemName.MatchesSimpleExpression(pattern, fileName, ignoreCase: true);
    }
}
