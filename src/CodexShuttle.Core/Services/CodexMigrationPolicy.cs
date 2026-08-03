using CodexShuttle.Core.Models;

namespace CodexShuttle.Core.Services;

public static class CodexMigrationPolicy
{
    public static readonly IReadOnlyList<string> SensitiveFiles =
    [
        "auth.json",
        ".env",
        "*.key",
        "*.pem",
        "secrets.json",
        "credentials.json"
    ];

    public static readonly IReadOnlyList<string> SensitiveDirectories =
    [
        ".sandbox-secrets"
    ];

    public static readonly IReadOnlyList<string> MachineSpecificFiles =
    [
        "logs_*.sqlite*",
        "installation_id",
        "cap_sid",
        "chrome-native-hosts*.json",
        "models_cache.json"
    ];

    public static readonly IReadOnlyList<string> MachineSpecificDirectories =
    [
        ".sandbox",
        ".sandbox-bin",
        ".tmp",
        "tmp",
        "cache",
        "node_repl",
        "process_manager",
        "computer-use-turn-ended"
    ];

    public static readonly IReadOnlyList<string> HistoryAndToolDirectories =
    [
        "sessions",
        "archived_sessions",
        "attachments",
        "memories",
        "skills",
        "plugins",
        "rules",
        "agents",
        "vendor_imports"
    ];

    public static readonly IReadOnlyList<string> HistoryAndToolFiles =
    [
        "session_index.jsonl",
        ".codex-global-state.json",
        ".codex-global-state.json.bak",
        "state_5.sqlite",
        "state_5.sqlite-shm",
        "state_5.sqlite-wal",
        "goals_1.sqlite",
        "goals_1.sqlite-shm",
        "goals_1.sqlite-wal",
        "memories_1.sqlite",
        "AGENTS.md"
    ];

    public static FileMirrorOptions SanitizedProfileOptions { get; } = new()
    {
        ExcludedFiles = SensitiveFiles.Concat(MachineSpecificFiles).ToArray(),
        ExcludedDirectories = SensitiveDirectories.Concat(MachineSpecificDirectories).ToArray()
    };

    public static FileMirrorOptions CredentialOnlyOptions { get; } = new()
    {
        ExcludedFiles = SensitiveFiles,
        ExcludedDirectories = SensitiveDirectories
    };
}
