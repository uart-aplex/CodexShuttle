namespace CodexShuttle.App.ViewModels;

public sealed class RestoreTargetViewModel
{
    public string Name { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public string PackagePath { get; init; } = string.Empty;
    public string TargetPath { get; init; } = string.Empty;
}
