using System.Text.Json;
using CodexShuttle.Core.Models;

namespace CodexShuttle.Core.Services;

public sealed class ManifestService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public BackupManifest CreateManifest(
        CodexDetectionResult detection,
        CodexInspectionResult inspection,
        string backupRoot,
        bool includeAppData)
    {
        foreach (var appData in detection.AppDataPaths)
        {
            appData.Included = includeAppData && appData.Exists;
        }

        return new BackupManifest
        {
            CreatedAt = DateTimeOffset.Now,
            SourceComputer = Environment.MachineName,
            SourceUser = Environment.UserName,
            SourceUserProfile = detection.UserProfile,
            CodexHome = detection.CodexHome,
            CodexHomeExists = detection.CodexHomeExists,
            AgentsHome = detection.AgentsHome,
            AgentsHomeExists = detection.AgentsHomeExists,
            CredentialsExcluded = true,
            BackupRoot = backupRoot,
            WorkspacePaths = detection.WorkspacePaths,
            AppDataPaths = detection.AppDataPaths,
            CodexInspection = inspection
        };
    }

    public async Task SaveAsync(BackupManifest manifest, string manifestPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath) ?? ".");
        await using var stream = File.Create(manifestPath);
        await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
    }

    public async Task<BackupManifest?> LoadAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(manifestPath);
        return await JsonSerializer.DeserializeAsync<BackupManifest>(stream, JsonOptions, cancellationToken);
    }
}
