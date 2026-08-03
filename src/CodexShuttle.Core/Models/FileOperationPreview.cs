namespace CodexShuttle.Core.Models;

public sealed class FileOperationPreview
{
    public string Operation { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public long Bytes { get; set; }
}
