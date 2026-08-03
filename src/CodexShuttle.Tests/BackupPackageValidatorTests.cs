using CodexShuttle.Core.Models;
using CodexShuttle.Core.Services;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class BackupPackageValidatorTests
{
    [TestMethod]
    public void Validate_WhenCompleteAndInProgressMarkersExist_RejectsPackage()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, BackupService.CompleteMarkerFileName), "complete");
            File.WriteAllText(Path.Combine(root, BackupService.InProgressMarkerFileName), "in-progress");
            Directory.CreateDirectory(Path.Combine(root, ".codex"));
            var manifest = new BackupManifest { CodexHomeExists = true };

            var result = new BackupPackageValidator().Validate(root, manifest, requireChecksums: false);

            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.Message, "unfinished");
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
