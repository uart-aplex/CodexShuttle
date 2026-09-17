using CodexShuttle.Core.Models;
using CodexShuttle.Core.Services;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class ManifestServiceTests
{
    [TestMethod]
    public async Task LoadAsync_LegacyManifestWithoutCredentialFlag_DefaultsToUnsafe()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexShuttleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "manifest.json");
        await File.WriteAllTextAsync(path, "{\"SchemaVersion\":1,\"SourceUser\":\"OfficeUser\"}");

        try
        {
            var manifest = await new ManifestService().LoadAsync(path);

            Assert.IsNotNull(manifest);
            Assert.IsFalse(manifest.CredentialsExcluded);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void CreateManifest_CurrentSchema_ExplicitlyMarksCredentialsExcluded()
    {
        var detection = new CodexDetectionResult();

        var manifest = new ManifestService().CreateManifest(
            detection,
            new CodexInspectionResult(),
            @"M:\CodexTransfer\CodexShuttle-Current",
            includeAppData: false);

        Assert.AreEqual(2, manifest.SchemaVersion);
        Assert.IsTrue(manifest.CredentialsExcluded);
    }
}
