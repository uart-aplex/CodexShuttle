using CodexShuttle.Core.Services;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class FileMirrorServiceTests
{
    [TestMethod]
    public void BuildFailureDiagnostics_IncludesExitCodeAndTailWithoutFloodingLog()
    {
        var output = Enumerable.Range(1, 100).Select(index => $"output {index}").ToList();
        var errors = new List<string> { "localized copy error" };

        var diagnostics = FileMirrorService.BuildFailureDiagnostics(8, output, errors);

        Assert.AreEqual("Robocopy failed with exit code 8.", diagnostics[0]);
        CollectionAssert.Contains(diagnostics.ToList(), "localized copy error");
        CollectionAssert.Contains(diagnostics.ToList(), "output 100");
        Assert.IsTrue(diagnostics.Count <= 41);
    }

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
