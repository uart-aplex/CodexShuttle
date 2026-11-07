using System.Reflection;
using CodexShuttle.App.ViewModels;
using CodexShuttle.Core.Merge;

namespace CodexShuttle.App.Tests;

[TestClass]
public sealed class MergeViewModelTests
{
    private static MergeViewModel Model() => new(() => @"C:\Users\Home\.codex", () => false, @"L:\Backup", _ => { });

    [TestMethod]
    public void CheckingSelectionAfterPreview_EnablesImportWithoutAnotherScan()
    {
        var vm = Model();
        var entry = new MergeEntry(Guid.NewGuid().ToString(), "Test", MergeAction.Add, "", "", "", "", "", "");
        var request = new MergeRequest(vm.Package, vm.ProfileRoot, @"C:\Users\Home", [], new());
        var preview = new MergePreview(request, "approved", [entry], "Test", DateTimeOffset.UtcNow);
        var field = typeof(MergeViewModel).GetField("_preview", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(vm, preview);
        var changed = typeof(MergeViewModel).GetMethod("SelectionChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var selection = new MergeSelection(entry, false, () => changed.Invoke(vm, null));
        vm.Sessions.Add(selection);
        vm.Acknowledged = true;
        Assert.IsFalse(vm.ApplyCommand.CanExecute(null));
        selection.Selected = true;
        Assert.IsTrue(vm.ApplyCommand.CanExecute(null));
        Assert.AreEqual("approved", ((MergePreview)field.GetValue(vm)!).Fingerprint);
        selection.Selected = false;
        Assert.IsFalse(vm.ApplyCommand.CanExecute(null));
        selection.Selected = true;
        vm.Package = @"M:\Backup";
        Assert.IsFalse(vm.ApplyCommand.CanExecute(null));
    }

    [TestMethod]
    public void SelectNew_ChoosesAdditionsAndContinuations_ButNotLocalAheadOrConflicts()
    {
        var vm = Model();
        var entries = Enum.GetValues<MergeAction>().Select(action => new MergeEntry(Guid.NewGuid().ToString(), action.ToString(), action, "", "", "", "", "", "")).ToArray();
        var conflict = entries.Single(e => e.Action == MergeAction.Conflict);
        var request = new MergeRequest(vm.Package, vm.ProfileRoot, @"C:\Users\Home", [conflict.Id], new());
        var preview = new MergePreview(request, "approved", entries, "Test", DateTimeOffset.UtcNow);
        var field = typeof(MergeViewModel).GetField("_preview", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(vm, preview);
        var changed = typeof(MergeViewModel).GetMethod("SelectionChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var entry in entries) vm.Sessions.Add(new(entry, entry == conflict, () => changed.Invoke(vm, null)));
        StringAssert.Contains(preview.SelectionSummary, "Will add 0 new conversations");
        StringAssert.Contains(preview.SelectionSummary, "save 1 conflicts outside Codex");
        StringAssert.Contains(preview.SelectionSummary, "2 available additions/updates not selected");
        vm.Acknowledged = true;
        Assert.IsTrue(vm.SelectNewCommand.CanExecute(null));
        vm.SelectNewCommand.Execute(null);
        CollectionAssert.AreEquivalent(new[] { MergeAction.Add, MergeAction.Update }, vm.Sessions.Where(s => s.Selected).Select(s => s.Entry.Action).ToArray());
        StringAssert.Contains(vm.Status, "Will add 1 new conversations");
        StringAssert.Contains(vm.Status, "update 1 conversations");
        StringAssert.Contains(vm.Status, "save 0 conflicts");
        Assert.IsTrue(vm.ApplyCommand.CanExecute(null));
        vm.Invalidate();
        Assert.IsFalse(vm.SelectNewCommand.CanExecute(null));
    }

    [TestMethod]
    public void WorkspaceOnlyPlan_CanApply_AndChangingCopyOptionInvalidatesApproval()
    {
        var vm = Model();
        Assert.IsTrue(vm.IncludeMissingWorkspaceFiles);
        var request = new MergeRequest(vm.Package, vm.ProfileRoot, @"C:\Users\Home", [], new());
        var preview = new MergePreview(request, "approved", [], "Test", DateTimeOffset.UtcNow)
        {
            Workspaces = new([new(@"L:\Backup\project\file.txt", @"E:\Project\file.txt", 1, 0, "hash")], [], [])
        };
        typeof(MergeViewModel).GetField("_preview", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, preview);
        vm.Acknowledged = true;
        Assert.IsTrue(vm.ApplyCommand.CanExecute(null));
        StringAssert.Contains(preview.SelectionSummary, "1 missing to add");
        vm.IncludeMissingWorkspaceFiles = false;
        Assert.IsFalse(vm.ApplyCommand.CanExecute(null));
    }

    [TestMethod]
    public void RefreshWithoutPreview_PreservesFailureStatus()
    {
        var vm = Model();
        typeof(MergeViewModel).GetProperty(nameof(vm.Status))!.SetValue(vm, "Stopped: unsupported layout");
        vm.Invalidate();
        Assert.AreEqual("Stopped: unsupported layout", vm.Status);
    }
}
