using CodexShuttle.Core.Services;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class FileMirrorServiceTests
{
    [DataTestMethod]
    [DataRow("2026/10/19 ERROR 32 (0x00000020) Copying File C:\\profile\\plugin.exe")]
    [DataRow("2026/10/19 \u932f\u8aa4 32 (0x00000020) C:\\profile\\plugin.exe")]
    [DataRow("2026/10/19 \u9519\u8bef 5 (0x00000005) C:\\profile\\plugin.exe")]
    public void ErrorCapture_PreservesLocalizedErrorAndReasonPastTail(string error)
    {
        var capture = new RobocopyDiagnostics();
        capture.Observe("plugin.exe");
        Assert.IsTrue(capture.Observe(error));
        Assert.IsTrue(capture.Observe("File is in use by another process."));
        for (var i = 0; i < 4000; i++) capture.Observe($"other file {i}");
        var details = FileMirrorService.BuildFailureDiagnostics(11, ["summary"], [], capture.Errors);
        CollectionAssert.Contains(details.ToList(), error);
        CollectionAssert.Contains(details.ToList(), "File is in use by another process.");
        Assert.IsTrue(details.Count <= 41);
    }

    [DataTestMethod]
    [DataRow("\u6a94\u6848 : 2988 953 2034 0 1 753")]
    [DataRow("\u4f4d\u5143\u7d44 : 1.931 g")]
    [DataRow("Files : 2988 953 2034 0 1 753")]
    public void Summaries_AreRecognizedInLocalizedOutput(string line) => Assert.IsTrue(RobocopyDiagnostics.IsSummary(line));

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
