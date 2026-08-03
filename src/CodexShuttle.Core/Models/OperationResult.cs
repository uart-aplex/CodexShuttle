namespace CodexShuttle.Core.Models;

public sealed class OperationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();

    public static OperationResult Ok(string message) => new() { Success = true, Message = message };
    public static OperationResult Fail(string message, params string[] errors) => new()
    {
        Success = false,
        Message = message,
        Errors = errors.ToList()
    };
}
