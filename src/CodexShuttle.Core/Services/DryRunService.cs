using CodexShuttle.Core.Models;
using System.IO.Enumeration;
using System.Diagnostics;

namespace CodexShuttle.Core.Services;

public sealed class DryRunService
{
    public DryRunResult ComparePlan(IEnumerable<MirrorPlanItem> plan, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var aggregate = new DryRunResult();
        foreach (var item in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Dry Run target: {item.Name}; source: {item.SourcePath}; destination: {item.DestinationPath}");
            var itemResult = item.IsDirectory
                ? CompareMirror(item.SourcePath, item.DestinationPath, item.MirrorOptions, progress, cancellationToken)
                : CompareFile(item.SourcePath, item.DestinationPath);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Target scanned: {item.Name}; add {itemResult.FilesToCopy:N0}, overwrite {itemResult.FilesToOverwrite:N0}, delete {itemResult.FilesToDelete:N0} (preview only).");
            foreach (var warning in itemResult.Warnings) progress?.Report($"Warning: {item.Name}: {warning}");
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
        FileMirrorOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new DryRunResult();

        if (!Directory.Exists(sourceRoot))
        {
            result.Warnings.Add($"Source folder does not exist: {sourceRoot}");
            return result;
        }

        options ??= new FileMirrorOptions();
        var sourceFiles = EnumerateRelativeFiles(sourceRoot, options, "source", progress, cancellationToken);
        var destinationFiles = Directory.Exists(destinationRoot)
            ? EnumerateRelativeFiles(destinationRoot, options, "destination", progress, cancellationToken)
            : new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);

        var comparison = new ScanProgress("Comparing", destinationRoot, progress);

        foreach (var (relativePath, sourceFile) in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            comparison.Visit(sourceFile.FullName);
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
            cancellationToken.ThrowIfCancellationRequested();
            comparison.Visit(destinationFile.FullName);
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

        comparison.Finish();
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

    private static Dictionary<string, FileInfo> EnumerateRelativeFiles(string root, FileMirrorOptions options,
        string side, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        var scan = new ScanProgress($"Scanning {side}", root, progress);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            scan.Visit(path);
            if (!IsExcluded(root, path, options)) files.Add(Path.GetRelativePath(root, path), new FileInfo(path));
        }
        scan.Finish();
        return files;
    }

    private sealed class ScanProgress
    {
        private readonly string _stage;
        private readonly string _root;
        private readonly IProgress<string>? _progress;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _count;
        public ScanProgress(string stage, string root, IProgress<string>? progress)
        {
            _stage = stage; _root = root; _progress = progress;
            progress?.Report($"{stage}: {root}");
        }
        public void Visit(string path)
        {
            _count++;
            // Bound UI and journal traffic on large trees while showing the first file immediately.
            if (_count != 1 && _clock.ElapsedMilliseconds < 1000) return;
            _progress?.Report($"{_stage}: {_count:N0} files examined; current file: {path}");
            _clock.Restart();
        }
        public void Finish() => _progress?.Report($"{_stage} finished: {_count:N0} files examined; {_root}");
    }

    private static bool IsExcluded(string root, string filePath, FileMirrorOptions options)
    {
        if (options.ExcludesDirectory(root, filePath))
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
