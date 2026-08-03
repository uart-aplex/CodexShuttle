using CodexShuttle.Core.Services;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class FileMirrorServiceTests
{
    [TestMethod]
    public async Task MirrorAsync_WithSanitizedProfile_PreservesDestinationCredentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexShuttleTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(source, "config.toml"), "source-config");
        File.WriteAllText(Path.Combine(source, "auth.json"), "source-secret");
        File.WriteAllText(Path.Combine(destination, "auth.json"), "destination-secret");
        File.WriteAllText(Path.Combine(destination, "obsolete.txt"), "delete-me");

        try
        {
            var result = await new FileMirrorService().MirrorAsync(
                source,
                destination,
                options: CodexMigrationPolicy.SanitizedProfileOptions);

            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual("source-config", File.ReadAllText(Path.Combine(destination, "config.toml")));
            Assert.AreEqual("destination-secret", File.ReadAllText(Path.Combine(destination, "auth.json")));
            Assert.IsFalse(File.Exists(Path.Combine(destination, "obsolete.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
