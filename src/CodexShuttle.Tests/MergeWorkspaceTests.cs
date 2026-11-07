using CodexShuttle.Core.Merge;
using CodexShuttle.Core.Models;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class MergeWorkspaceTests
{
    private static WorkspaceEntry Workspace(string destination) => new() { SourcePath = destination, PackagePath = "project-files", Enabled = true, Exists = true };
    private static void FileInPackage(MergeFixture f, string name, string contents) => f.Write(f.PackageRoot, "project-files/" + name, contents);
    private static string Destination(MergeFixture f) => Path.Combine(f.Root, "missing-project");

    [TestMethod]
    public async Task MissingProjectAndPaginatedChat_AreAddedTogether_WithUserMapping()
    {
        using var f = new MergeFixture(); PaginatedMergeTests.Initialize(f);
        var original = @"C:\Users\OfficeUser\Projects\Example";
        var destination = Path.Combine(f.Root, "home", "Projects", "Example");
        FileInPackage(f, "src/main.cs", "new code"); FileInPackage(f, ".git/objects/pack/example.pack", "pack bytes");
        FileInPackage(f, ".env", "synthetic credential must not migrate");
        Directory.CreateDirectory(Path.Combine(f.PackageRoot, "project-files", "empty"));
        var id = f.AddSession(f.Source, "new project conversation", cwd: original);
        PaginatedMergeTests.AddHistory(f.Source, id, "new history");
        await f.Package(workspaces: [Workspace(original)]);
        var package = f.HashTree(f.PackageRoot);
        var service = f.Service(); var preview = await service.PreviewAsync(f.Request(id));
        Assert.AreEqual(MergeAction.Add, preview.Entries.Single().Action, preview.Entries.Single().Reason);
        Assert.AreEqual(2, preview.Workspaces.Files.Count);
        Assert.AreEqual(1, preview.Workspaces.Roots.Single().ExcludedFiles);
        Assert.IsFalse(Directory.Exists(destination), "Dry Run must not create placeholder directories.");
        var result = await service.ApplyAsync(preview);
        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual("new code", File.ReadAllText(Path.Combine(destination, "src", "main.cs")));
        Assert.IsTrue(Directory.Exists(Path.Combine(destination, "empty")));
        Assert.IsFalse(File.Exists(Path.Combine(destination, ".env")));
        using (var db = MergeDatabase.Open(Path.Combine(f.Target, "state_5.sqlite"))) Assert.AreEqual(destination, MergeDatabase.Rows(db)[id]["cwd"]);
        CollectionAssert.AreEquivalent(package.ToArray(), f.HashTree(f.PackageRoot).ToArray());
        Assert.IsFalse((await service.PreviewAsync(f.Request(id))).Workspaces.HasChanges);
    }

    [TestMethod]
    public async Task ReadOnlyGitPack_IsCopiedWithoutTransferringReadOnlyAttribute()
    {
        using var f = new MergeFixture(); var destination = Destination(f);
        FileInPackage(f, ".git/objects/pack/test.pack", "synthetic pack");
        var source = Path.Combine(f.PackageRoot, "project-files", ".git", "objects", "pack", "test.pack");
        File.SetAttributes(source, FileAttributes.ReadOnly);
        try
        {
            await f.Package(workspaces: [Workspace(destination)]);
            var service = f.Service();
            var result = await service.ApplyAsync(await service.PreviewAsync(f.Request()));
            Assert.IsTrue(result.Success, result.Message);
            var target = Path.Combine(destination, ".git", "objects", "pack", "test.pack");
            Assert.AreEqual("synthetic pack", File.ReadAllText(target));
            Assert.IsFalse(File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly));
        }
        finally { File.SetAttributes(source, FileAttributes.Normal); }
    }

    [TestMethod]
    public async Task ExistingWorkspace_KeepsLocalChangesAndOnlyAddsMissingFiles_WithoutChatSnapshot()
    {
        using var f = new MergeFixture(); var destination = Destination(f);
        FileInPackage(f, "existing.txt", "old source"); FileInPackage(f, "missing.txt", "incoming");
        f.Write(destination, "existing.txt", "today's local fix"); f.Write(destination, "local-only.txt", "personal");
        await f.Package(workspaces: [Workspace(destination)]);
        var beforeProfile = f.HashTree(f.Target); var service = f.Service();
        var preview = await service.PreviewAsync(f.Request());
        Assert.AreEqual(1, preview.Workspaces.Files.Count);
        Assert.AreEqual(1, preview.Workspaces.Roots.Single().RetainedFiles);
        var result = await service.ApplyAsync(preview);
        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual("today's local fix", File.ReadAllText(Path.Combine(destination, "existing.txt")));
        Assert.AreEqual("personal", File.ReadAllText(Path.Combine(destination, "local-only.txt")));
        Assert.AreEqual("incoming", File.ReadAllText(Path.Combine(destination, "missing.txt")));
        Assert.AreEqual(0, service.RestorePoints().Count);
        CollectionAssert.AreEquivalent(beforeProfile.ToArray(), f.HashTree(f.Target).ToArray());
        Assert.IsFalse((await service.PreviewAsync(f.Request())).Workspaces.HasChanges);
    }

    [TestMethod]
    public async Task CancelAfterOneFile_RetryOnlyAddsRemainderAndPreservesSubsequentLocalEdits()
    {
        using var f = new MergeFixture(); var destination = Destination(f);
        FileInPackage(f, "a.txt", "a"); FileInPackage(f, "b.txt", "b");
        var id = f.AddSession(f.Source, "incoming", cwd: destination);
        await f.Package(workspaces: [Workspace(destination)]);
        using var cancel = new CancellationTokenSource();
        var service = f.Service(stage => { if (stage == "WorkspaceFileWritten") cancel.Cancel(); });
        var result = await service.ApplyAsync(await service.PreviewAsync(f.Request(id)), cancel.Token);
        Assert.IsFalse(result.Success);
        Assert.IsTrue(File.Exists(Path.Combine(destination, "a.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(destination, "b.txt")));
        Assert.IsFalse(File.Exists(f.SessionPath(f.Target, id)));
        File.WriteAllText(Path.Combine(destination, "a.txt"), "local change after cancellation");
        var retry = f.Service(); var preview = await retry.PreviewAsync(f.Request(id));
        Assert.AreEqual(1, preview.Workspaces.Files.Count);
        Assert.IsTrue((await retry.ApplyAsync(preview)).Success);
        Assert.AreEqual("local change after cancellation", File.ReadAllText(Path.Combine(destination, "a.txt")));
        Assert.IsTrue(File.Exists(f.SessionPath(f.Target, id)));
        Assert.AreEqual(0, Directory.GetFiles(destination, "*.codexshuttle-*.tmp", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task ConversationFailure_RollsBackChatButKeepsVerifiedWorkspaceAdditions()
    {
        using var f = new MergeFixture(); var destination = Destination(f);
        FileInPackage(f, "code.txt", "valid workspace bytes");
        var id = f.AddSession(f.Source, "incoming", cwd: destination);
        await f.Package(workspaces: [Workspace(destination)]);
        var service = f.Service(stage => { if (stage == "SessionWritten") throw new IOException("injected"); });
        var result = await service.ApplyAsync(await service.PreviewAsync(f.Request(id)));
        Assert.IsFalse(result.Success);
        Assert.AreEqual("valid workspace bytes", File.ReadAllText(Path.Combine(destination, "code.txt")));
        Assert.IsFalse(File.Exists(f.SessionPath(f.Target, id)));
        Assert.AreEqual("RolledBack", service.RestorePoints().Single().Status);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SourceOrDestinationChangedAfterPreview_IsNotOverwritten(bool sourceChanged)
    {
        using var f = new MergeFixture(); var destination = Destination(f);
        FileInPackage(f, "code.txt", "incoming");
        await f.Package(workspaces: [Workspace(destination)]);
        var service = f.Service(); var preview = await service.PreviewAsync(f.Request());
        if (sourceChanged) FileInPackage(f, "code.txt", "changed source");
        else f.Write(destination, "code.txt", "new local file");
        Assert.IsFalse((await service.ApplyAsync(preview)).Success);
        if (sourceChanged) Assert.IsFalse(Directory.Exists(destination));
        else Assert.AreEqual("new local file", File.ReadAllText(Path.Combine(destination, "code.txt")));
    }

    [TestMethod]
    public async Task CopyChecksContentHash_EvenWhenLengthAndTimestampMatch()
    {
        using var f = new MergeFixture(); var destination = Destination(f);
        FileInPackage(f, "code.txt", "before");
        await f.Package(workspaces: [Workspace(destination)]);
        var preview = await f.Service().PreviewAsync(f.Request());
        var file = preview.Workspaces.Files.Single();
        File.WriteAllText(file.Source, "after!"); File.SetLastWriteTimeUtc(file.Source, new DateTime(file.ModifiedUtcTicks, DateTimeKind.Utc));
        Assert.ThrowsException<IOException>(() => MergeWorkspaces.Apply(preview.Workspaces, Path.Combine(f.Root, "private-store"), preview.Request, default, null, null));
        Assert.IsFalse(File.Exists(file.Target));
        Assert.AreEqual(0, Directory.GetFiles(destination, "*.tmp").Length);
        Directory.Delete(destination); // Empty test directory from the failed transfer; restore the original preview state.
        var cachedResult = await f.Service().ApplyAsync(preview);
        Assert.IsFalse(cachedResult.Success, "An approved preview hash must still be checked during copying even when metadata is unchanged.");
        StringAssert.Contains(cachedResult.Message, "source/content changed after preview");
        Assert.IsFalse(File.Exists(file.Target));
    }

    [TestMethod]
    public async Task DisabledOrAbsentWorkspace_DoesNotCreateEmptyPlaceholderForChat()
    {
        using var f = new MergeFixture(); var destination = Destination(f);
        var id = f.AddSession(f.Source, "missing project", cwd: destination);
        FileInPackage(f, "code.txt", "incoming");
        await f.Package(workspaces: [Workspace(destination)]);
        var preview = await f.Service().PreviewAsync(f.Request(id) with { IncludeMissingWorkspaceFiles = false });
        Assert.AreEqual(MergeAction.Blocked, preview.Entries.Single().Action);
        Assert.IsFalse(preview.Workspaces.HasChanges);
        Assert.IsFalse(Directory.Exists(destination));
        await f.Package();
        Assert.AreEqual(MergeAction.Blocked, (await f.Service().PreviewAsync(f.Request(id))).Entries.Single().Action);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task FileDirectoryCollision_IsPreservedAndBlocksPreview(bool localIsFile)
    {
        using var f = new MergeFixture(); var destination = Destination(f);
        FileInPackage(f, localIsFile ? "src/code.txt" : "src", "incoming");
        var localPath = localIsFile ? "src" : "src/keep.txt";
        f.Write(destination, localPath, "local file");
        await f.Package(workspaces: [Workspace(destination)]);
        await Assert.ThrowsExceptionAsync<IOException>(() => f.Service().PreviewAsync(f.Request()));
        Assert.AreEqual("local file", File.ReadAllText(Path.Combine(destination, localPath)));
    }

    [TestMethod]
    public async Task WorkspaceCannotOverlapProfileOrUseDriveRootOrAmbiguousSubfolderMapping()
    {
        using var f = new MergeFixture(); FileInPackage(f, "code.txt", "incoming");
        await f.Package(workspaces: [Workspace(f.Target)]);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => f.Service().PreviewAsync(f.Request()));
        await f.Package(workspaces: [Workspace(Path.GetPathRoot(f.Root)!)]);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => f.Service().PreviewAsync(f.Request()));
        var destination = Destination(f);
        await f.Package(workspaces: [Workspace(destination)]);
        var request = f.Request() with { WorkspaceMappings = new() { [Path.Combine(destination, "sub")] = Path.Combine(f.Root, "other") } };
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => f.Service().PreviewAsync(request));
    }

    [TestMethod]
    public async Task RelocatedUsbPackage_UsesSelectedPackageForMissingWorkspaceFiles()
    {
        using var f = new MergeFixture(); var destination = Destination(f);
        FileInPackage(f, "code.txt", "incoming");
        await f.Package(workspaces: [Workspace(destination)]);
        var moved = Path.Combine(f.Root, "other-usb-mount"); Directory.Move(f.PackageRoot, moved);
        var request = f.Request() with { PackageRoot = moved };
        var service = f.Service(); var preview = await service.PreviewAsync(request);
        Assert.IsTrue(preview.Workspaces.Files.Single().Source.StartsWith(moved, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue((await service.ApplyAsync(preview)).Success);
        Assert.AreEqual("incoming", File.ReadAllText(Path.Combine(destination, "code.txt")));
    }
}
