using CodexShuttle.Core.Services;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class ChecksumServiceTests
{
    [TestMethod]
    public async Task VerifySha256FileAsync_WhenFileChanges_Fails()
    {
        var root = CreateTempDirectory();
        try
        {
            var codex = Path.Combine(root, ".codex");
            Directory.CreateDirectory(codex);
            var state = Path.Combine(codex, "state.txt");
            await File.WriteAllTextAsync(state, "original");
            var checksum = Path.Combine(root, "checksums.sha256");
            var service = new ChecksumService();
            await service.SaveSha256FileAsync(root, [".codex"], checksum);

            Assert.IsTrue((await service.VerifySha256FileAsync(root, checksum)).Success);
            await File.WriteAllTextAsync(state, "changed");
            Assert.IsFalse((await service.VerifySha256FileAsync(root, checksum)).Success);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "CodexShuttleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
