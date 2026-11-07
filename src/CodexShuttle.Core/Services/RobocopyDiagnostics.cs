using System.Text.RegularExpressions;

namespace CodexShuttle.Core.Services;

internal sealed class RobocopyDiagnostics
{
    private readonly List<string> _errors = new();
    private string? _previous;
    private int _contextLines;
    public IReadOnlyList<string> Errors => _errors;

    public bool Observe(string line)
    {
        var error = Regex.IsMatch(line, @"(?:ERROR|\u932f\u8aa4|\u9519\u8bef)\s+\d+\s+\(0x[0-9a-f]+\)", RegexOptions.IgnoreCase)
            || line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase);
        if (error)
        {
            if (_previous != null) Remember(_previous);
            _contextLines = 3;
        }
        var show = _contextLines > 0;
        if (show) { Remember(line); _contextLines--; }
        _previous = line;
        return show;
    }

    private void Remember(string line)
    {
        if (_errors.Count == 80) _errors.RemoveAt(0);
        _errors.Add(line);
    }

    internal static bool IsSummary(string line) => new[]
    {
        "Files :", "Bytes :", "Times :", "Ended", "Total", "Dirs :",
        "\u7e3d\u8a08", "\u76ee\u9304 :", "\u6a94\u6848 :", "\u4f4d\u5143\u7d44 :", "\u6642\u9593 :", "\u5df2\u7d50\u675f",
        "\u603b\u6570", "\u76ee\u5f55 :", "\u6587\u4ef6 :", "\u5b57\u8282 :", "\u65f6\u95f4 :", "\u7ed3\u675f"
    }.Any(prefix => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
