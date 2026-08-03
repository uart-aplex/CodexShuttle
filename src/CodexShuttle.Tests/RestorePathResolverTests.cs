using CodexShuttle.Core.Models;
using CodexShuttle.Core.Services;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class RestorePathResolverTests
{
    [TestMethod]
    public void ResolveCodexHome_WhenBackupUserIsDifferent_UsesCurrentUserProfile()
    {
        var manifest = new BackupManifest
        {
            SourceUserProfile = @"C:\Users\OfficeUser",
            CodexHome = @"C:\Users\OfficeUser\.codex"
        };
        var resolver = new RestorePathResolver();

        var target = resolver.ResolveCodexHome(manifest);

        Assert.AreEqual(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), target);
        Assert.AreNotEqual(manifest.CodexHome, target, ignoreCase: true);
    }

    [TestMethod]
    public void ResolveCodexHome_WhenCurrentComputerHasOverride_UsesOverride()
    {
        var targetCodexHome = Path.Combine(Path.GetTempPath(), "CurrentUserCodexHome");
        var manifest = new BackupManifest
        {
            SourceUserProfile = @"C:\Users\OfficeUser",
            CodexHome = @"C:\Users\OfficeUser\.codex"
        };
        var resolver = new RestorePathResolver(targetCodexHome);

        var target = resolver.ResolveCodexHome(manifest);

        Assert.AreEqual(Path.GetFullPath(targetCodexHome), target);
    }

    [TestMethod]
    public void ResolveAgentsHome_WhenBackupUserIsDifferent_UsesCurrentUserProfile()
    {
        var manifest = new BackupManifest
        {
            SourceUserProfile = @"C:\Users\OfficeUser",
            AgentsHome = @"C:\Users\OfficeUser\.agents"
        };
        var resolver = new RestorePathResolver();

        var target = resolver.ResolveAgentsHome(manifest);

        Assert.AreEqual(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents"), target);
        Assert.AreNotEqual(manifest.AgentsHome, target, ignoreCase: true);
    }

    [TestMethod]
    public void ResolveAppDataTarget_WhenBackupUserIsDifferent_UsesCurrentRoamingAppData()
    {
        var entry = new AppDataEntry
        {
            SourcePath = @"C:\Users\OfficeUser\AppData\Roaming\Codex"
        };
        var resolver = new RestorePathResolver();

        var target = resolver.ResolveAppDataTarget(entry);

        Assert.AreEqual(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Codex"), target);
    }

    [TestMethod]
    public void ResolveAppDataTarget_WhenBackupUserIsDifferent_UsesCurrentLocalAppData()
    {
        var entry = new AppDataEntry
        {
            SourcePath = @"C:\Users\OfficeUser\AppData\Local\OpenAI"
        };
        var resolver = new RestorePathResolver();

        var target = resolver.ResolveAppDataTarget(entry);

        Assert.AreEqual(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI"), target);
    }
}
