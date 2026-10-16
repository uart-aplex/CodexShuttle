using System.Security.Cryptography;
using CodexShuttle.Core.Services;
using CodexShuttle.Core.Models;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class CompressedRestoreAppServerTests
{
    [TestMethod]
    public async Task NativeCompressedRollout_CanBeReadAndResumedAfterIsolatedRestore()
    {
        var executable = Environment.GetEnvironmentVariable("CODEXSHUTTLE_TEST_CODEX");
        if (string.IsNullOrEmpty(executable)) Assert.Inconclusive("Set CODEXSHUTTLE_TEST_CODEX for the isolated compressed restore acceptance test.");
        using var fixture = new MergeFixture();
        var id = fixture.AddSession(fixture.Source, "Synthetic compressed restore message");
        // Discard only this test's simplified schema; let the installed server create its real schema.
        File.Delete(Path.Combine(fixture.Source, "state_5.sqlite"));
        var plain = fixture.SessionPath(fixture.Source, id);
        var compressed = plain + ".zst";
        await using (var source = new MergeAppServerTests.IsolatedAppServer(executable!, fixture, fixture.Source))
        {
            await source.Initialize(experimental: true);
            await source.Call("thread/read", new { threadId = id, includeTurns = true });
            File.SetLastWriteTimeUtc(plain, DateTime.UtcNow.AddDays(-10));
            await source.Call("rollout/compress", null);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (File.Exists(plain) || !File.Exists(compressed)) await Task.Delay(50, timeout.Token);
        }
        var sourceHash = SHA256.HashData(File.ReadAllBytes(compressed));
        await fixture.Package();
        var restore = new RestoreService(new FileMirrorService(), new ManifestService(), new RestorePathResolver(fixture.Target, Path.Combine(fixture.Root, "home")));
        var result = await restore.RestoreAsync(fixture.PackageRoot, new RestoreOptions());
        Assert.IsTrue(result.Success, result.Message + string.Join(";", result.Errors));
        var targetPlain = fixture.SessionPath(fixture.Target, id);
        Assert.IsFalse(File.Exists(targetPlain), "Restore must not decompress a native compressed rollout.");
        CollectionAssert.AreEqual(sourceHash, SHA256.HashData(File.ReadAllBytes(targetPlain + ".zst")));
        CollectionAssert.AreEqual(sourceHash, SHA256.HashData(File.ReadAllBytes(compressed)));
        // This fixture is legacy history; the database/WAL transfer is tested separately.
        await using var target = new MergeAppServerTests.IsolatedAppServer(executable!, fixture);
        await target.Initialize();
        var read = await target.Call("thread/read", new { threadId = id, includeTurns = true });
        StringAssert.Contains(read.GetRawText(), "Synthetic compressed restore message");
        var resumed = await target.Call("thread/resume", new { threadId = id, cwd = fixture.Workspace, approvalPolicy = "never", sandbox = "read-only" });
        Assert.AreEqual(id, resumed.GetProperty("thread").GetProperty("id").GetString());
    }
}
