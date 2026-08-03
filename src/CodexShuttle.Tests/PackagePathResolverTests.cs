using CodexShuttle.Core.Services;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class PackagePathResolverTests
{
    [TestMethod]
    public void NormalizeRestorePackage_WhenUsbRootIsSelected_UsesPackageOnThatDrive()
    {
        var path = PackagePathResolver.NormalizeRestorePackage(@"U:\");

        Assert.AreEqual(@"U:\CodexTransfer\CodexShuttle-Current", path);
    }

    [TestMethod]
    public void NormalizeRestorePackage_WhenTransferFolderIsSelected_AppendsCurrentPackage()
    {
        var path = PackagePathResolver.NormalizeRestorePackage(@"U:\CodexTransfer");

        Assert.AreEqual(@"U:\CodexTransfer\CodexShuttle-Current", path);
    }

    [TestMethod]
    public void NormalizeRestorePackage_WhenPackageFolderIsSelected_KeepsSelectedPackage()
    {
        var path = PackagePathResolver.NormalizeRestorePackage(@"U:\CodexTransfer\CodexShuttle-Current");

        Assert.AreEqual(@"U:\CodexTransfer\CodexShuttle-Current", path);
    }
}
