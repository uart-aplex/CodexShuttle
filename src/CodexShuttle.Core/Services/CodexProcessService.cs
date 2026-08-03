using System.Diagnostics;

namespace CodexShuttle.Core.Services;

public sealed class CodexProcessService
{
    private static readonly string[] ProcessNames = ["Codex", "codex"];

    public bool IsCodexRunning() => GetRunningProcesses().Any();

    public IReadOnlyList<Process> GetRunningProcesses()
    {
        return ProcessNames
            .SelectMany(Process.GetProcessesByName)
            .GroupBy(process => process.Id)
            .Select(group => group.First())
            .ToList();
    }

    public void KillCodexProcesses()
    {
        foreach (var process in GetRunningProcesses())
        {
            process.Kill(entireProcessTree: true);
        }
    }
}
