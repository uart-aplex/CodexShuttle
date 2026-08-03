using CodexShuttle.Core.Models;
using CodexShuttle.Core.Services;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class BackupTransactionServiceTests
{
    [TestMethod]
    public async Task RecoverAsync_AfterInterruptedUpdate_RestoresPreviousFiles()
    {
        var parent = CreateTempDirectory();
        var source = Path.Combine(parent, "source");
        var package = Path.Combine(parent, "CodexShuttle-Current");
        var destination = Path.Combine(package, ".codex");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(source, "state.txt"), "new-state");
        await File.WriteAllTextAsync(Path.Combine(source, "added.txt"), "new-file");
        await File.WriteAllTextAsync(Path.Combine(destination, "state.txt"), "old-state");
        await File.WriteAllTextAsync(Path.Combine(destination, "deleted.txt"), "old-file");
        File.SetLastWriteTimeUtc(Path.Combine(source, "state.txt"), DateTime.UtcNow.AddMinutes(1));
        File.WriteAllText(Path.Combine(package, BackupService.CompleteMarkerFileName), "complete");

        try
        {
            var service = new BackupTransactionService();
            await service.PrepareAsync(
                package,
                [new BackupDataSet { Name = "Codex", SourcePath = source, DestinationPath = destination }],
                [],
                CancellationToken.None,
                null);

            await File.WriteAllTextAsync(Path.Combine(destination, "state.txt"), "new-state");
            await File.WriteAllTextAsync(Path.Combine(destination, "added.txt"), "new-file");
            await service.RecoverAsync(package, CancellationToken.None, null);

            Assert.AreEqual("old-state", await File.ReadAllTextAsync(Path.Combine(destination, "state.txt")));
            Assert.AreEqual("old-file", await File.ReadAllTextAsync(Path.Combine(destination, "deleted.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(destination, "added.txt")));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "CodexShuttleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
