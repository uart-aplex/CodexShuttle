namespace CodexShuttle.App.ViewModels;

public sealed class PathStatusViewModel
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Exists { get; init; } = string.Empty;
    public string FileCount { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public string LatestWrite { get; init; } = string.Empty;
}
