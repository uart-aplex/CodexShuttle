using CodexShuttle.Core.Models;

namespace CodexShuttle.Core.Services;

public sealed class StoragePreflightService
{
    private readonly DryRunService _dryRunService = new();

    public OperationResult Validate(IReadOnlyList<MirrorPlanItem> plan)
    {
        try
        {
            var bytesByRoot = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in plan)
            {
                var dryRun = item.IsDirectory
                    ? _dryRunService.CompareMirror(item.SourcePath, item.DestinationPath, item.MirrorOptions)
                    : _dryRunService.CompareFile(item.SourcePath, item.DestinationPath);
                var root = Path.GetPathRoot(item.DestinationPath);
                if (!string.IsNullOrWhiteSpace(root))
                {
                    bytesByRoot[root] = bytesByRoot.GetValueOrDefault(root) + dryRun.TotalBytesToCopy;
                }
            }

            foreach (var (root, requiredBytes) in bytesByRoot)
            {
                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                {
                    return OperationResult.Fail("A restore destination drive is not ready.", root);
                }

                var reserve = Math.Max(512L * 1024 * 1024, requiredBytes / 20);
                if (drive.AvailableFreeSpace < requiredBytes + reserve)
                {
                    return OperationResult.Fail(
                        "A restore destination does not have enough free space.",
                        $"{root}: need about {ReportService.FormatBytes(requiredBytes + reserve)}, available {ReportService.FormatBytes(drive.AvailableFreeSpace)}");
                }

                if (drive.DriveFormat.Equals("FAT32", StringComparison.OrdinalIgnoreCase))
                {
                    var oversized = plan.Where(item => Path.GetPathRoot(item.DestinationPath)?.Equals(root, StringComparison.OrdinalIgnoreCase) == true)
                        .SelectMany(item => EnumerateSourceFiles(item))
                        .FirstOrDefault(file => file.Length > uint.MaxValue);
                    if (oversized is not null)
                    {
                        return OperationResult.Fail("FAT32 cannot store a file larger than 4 GB.", oversized.FullName);
                    }
                }
            }

            return OperationResult.Ok("Storage preflight passed.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("Storage preflight failed.", ex.Message);
        }
    }

    private static IEnumerable<FileInfo> EnumerateSourceFiles(MirrorPlanItem item)
    {
        if (!item.IsDirectory)
        {
            yield return new FileInfo(item.SourcePath);
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(item.SourcePath, "*", SearchOption.AllDirectories))
        {
            yield return new FileInfo(file);
        }
    }
}
