using CodexShuttle.Core.Services;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class PathSafetyServiceTests
{
    [TestMethod]
    public void ResolvePackagePath_WhenPathEscapesPackage_Throws()
    {
        var service = new PathSafetyService();
        var root = CreateTempDirectory();
        try
        {
            Assert.ThrowsException<InvalidDataException>(() => service.ResolvePackagePath(root, @"..\outside"));
            Assert.ThrowsException<InvalidDataException>(() => service.ResolvePackagePath(root, @"C:\Windows"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void ValidateMirrorPair_WhenDestinationIsInsideSource_Throws()
    {
        var service = new PathSafetyService();
        var root = CreateTempDirectory();
        try
        {
            Assert.ThrowsException<InvalidOperationException>(() =>
                service.ValidateMirrorPair(root, Path.Combine(root, "backup")));
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
