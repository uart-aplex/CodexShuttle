namespace CodexShuttle.Core.Models;

public sealed class DryRunResult
{
    public long FilesToCopy { get; set; }
    public long FilesToOverwrite { get; set; }
    public long FilesToDelete { get; set; }
    public long TotalBytesToCopy { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<FileOperationPreview> Operations { get; set; } = new();
}
