using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CodexShuttle.Core.Merge;
using CodexShuttle.Core.Services;
using Forms = System.Windows.Forms;
using MergeAction = CodexShuttle.Core.Merge.MergeAction;

namespace CodexShuttle.App.ViewModels;

public sealed class MergeViewModel : ViewModelBase
{
    private readonly MergeRestoreService _service = new();
    private readonly Func<string> _profile;
    private readonly Func<bool> _otherBusy;
    private readonly Action<string> _savePackage;
    private MergePreview? _preview;
    private CancellationTokenSource? _cancel;
    private string _package;
    private string _status = "Preview release. Desktop GUI acceptance is pending. Automatic sync is off.";
    private bool _busy;
    private bool _acknowledged;
    private RestorePointInfo? _selectedPoint;
    public MergeViewModel(Func<string> profile, Func<bool> otherBusy, string package, Action<string> savePackage)
    {
        _profile = profile; _otherBusy = otherBusy; _package = package; _savePackage = savePackage;
        BrowseCommand = new(() => { var path = Browse("Select a completed backup package", Package); if (path != null) Package = PackagePathResolver.NormalizeRestorePackage(path); return Task.CompletedTask; }, Available);
        PreviewCommand = new(Preview, () => Available() && !string.IsNullOrWhiteSpace(Package));
        ApplyCommand = new(Apply, () => Available() && _acknowledged && _preview != null && _preview.Request.SelectedIds.Length > 0
            && !_preview.Entries.Any(e => _preview.Request.SelectedIds.Contains(e.Id) && e.Action == MergeAction.Blocked));
        CancelCommand = new(() => { _cancel?.Cancel(); return Task.CompletedTask; }, () => IsBusy);
        AddMappingCommand = new(() => { var row = new MergeMapping(Invalidate, mapping => { Mappings.Remove(mapping); Invalidate(); }); Mappings.Add(row); Invalidate(); return Task.CompletedTask; }, Available);
        RefreshPointsCommand = new(() => Run(async (_, _) => { var points = await Task.Run(_service.RestorePoints); Points.Clear(); foreach (var p in points.OrderByDescending(p => p.CreatedAt)) Points.Add(p); Status = $"{Points.Count} local restore points."; }), Available);
        RecoverCommand = new(Recover, () => Available() && SelectedPoint?.Status is "Applying" or "Completed");
    }
    public ObservableCollection<MergeSelection> Sessions { get; } = new();
    public ObservableCollection<MergeMapping> Mappings { get; } = new();
    public ObservableCollection<MergeEntry> Conflicts { get; } = new();
    public ObservableCollection<RestorePointInfo> Points { get; } = new();
    public ObservableCollection<string> Log { get; } = new();
    public RelayCommand BrowseCommand { get; }
    public RelayCommand PreviewCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand AddMappingCommand { get; }
    public RelayCommand RefreshPointsCommand { get; }
    public RelayCommand RecoverCommand { get; }
    public string ProfileRoot => _profile();
    public string Package { get => _package; set { if (SetProperty(ref _package, value)) { Sessions.Clear(); Conflicts.Clear(); Invalidate(); _savePackage(value); } } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) { OnPropertyChanged(nameof(CanEdit)); RaiseCommands(); } } }
    public bool CanEdit => Available();
    public bool Acknowledged { get => _acknowledged; set { SetProperty(ref _acknowledged, value); RaiseCommands(); } }
    public RestorePointInfo? SelectedPoint { get => _selectedPoint; set { SetProperty(ref _selectedPoint, value); RaiseCommands(); } }
    private bool Available() => !IsBusy && !_otherBusy();
    public void Invalidate() { _preview = null; Status = "Selection or paths changed. Run Dry Run to approve the current plan."; OnPropertyChanged(nameof(ProfileRoot)); RaiseCommands(); }
    public void RaiseCommands()
    {
        foreach (var command in new[] { BrowseCommand, PreviewCommand, ApplyCommand, CancelCommand, AddMappingCommand, RefreshPointsCommand, RecoverCommand }) command?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanEdit));
    }
    public void Cancel() => _cancel?.Cancel();
    private MergeRequest Request() => new(Package, ProfileRoot, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Sessions.Where(s => s.Selected).Select(s => s.Entry.Id).ToArray(),
        Mappings.Where(m => !string.IsNullOrWhiteSpace(m.Source) || !string.IsNullOrWhiteSpace(m.Target))
            .ToDictionary(m => m.Source, m => m.Target, StringComparer.OrdinalIgnoreCase));
    private Task Preview() => Run(async (token, progress) =>
    {
        var request = Request();
        _preview = null;
        var preview = await Task.Run(() => _service.PreviewAsync(request, token, progress), token);
        Sessions.Clear(); Conflicts.Clear();
        foreach (var entry in preview.Entries)
        {
            Sessions.Add(new(entry, request.SelectedIds.Contains(entry.Id), Invalidate));
            if (entry.Action is MergeAction.Conflict or MergeAction.ConflictAlreadyPreserved) Conflicts.Add(entry);
        }
        _preview = preview;
        Status = $"{preview.Summary} Selected: {request.SelectedIds.Length}. Source: {preview.SourceComputer}, {preview.BackupDate:yyyy-MM-dd HH:mm zzz}.";
    });
    private Task Apply()
    {
        var plan = _preview!;
        if (System.Windows.MessageBox.Show($"Import {plan.Request.SelectedIds.Length} selected conversations/revisions into:\n{plan.Request.ProfileRoot}\n\nExisting conversations and workspaces will not be overwritten. Conflicts are retained outside Codex, not opened as new chats. A verified local restore point is required.\n\nClose Codex Desktop, CLI and IDE sessions before proceeding.",
            "Confirm Merge Restore (Preview)", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return Task.CompletedTask;
        return Run(async (token, progress) =>
        {
            _preview = null;
            var result = await Task.Run(() => _service.ApplyAsync(plan, token, progress), token);
            Status = result.Message + (result.Errors.Count == 0 ? "" : " Details: " + string.Join("; ", result.Errors));
        });
    }
    private Task Recover()
    {
        var point = SelectedPoint!;
        if (System.Windows.MessageBox.Show($"Undo merge {point.Id} on:\n{point.ProfileRoot}\n\nImported revisions will be removed only if the original state can be verified and no newer changes are detected. Close Codex first.",
            "Confirm Rollback", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return Task.CompletedTask;
        return Run(async (_, _) =>
        {
            _preview = null;
            var result = await Task.Run(() => _service.Recover(point.Path));
            Status = result.Message + (result.Errors.Count == 0 ? "" : " Details: " + string.Join("; ", result.Errors));
            Points.Clear();
            foreach (var p in await Task.Run(_service.RestorePoints)) Points.Add(p);
        });
    }
    private async Task Run(Func<CancellationToken, IProgress<string>, Task> operation)
    {
        IsBusy = true;
        _cancel = new();
        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexShuttle", "logs");
        using var journal = new OperationJournal(logs, "merge", action => System.Windows.Application.Current.Dispatcher.BeginInvoke(action), message => { Status = message; AddLog(message); });
        try { await operation(_cancel.Token, journal); }
        catch (OperationCanceledException) { Status = "Canceled. No import was approved."; _preview = null; }
        catch (Exception ex) { Status = "Stopped: " + ex.Message; _preview = null; }
        finally
        {
            journal.Finish(Status); AddLog(Status);
            if (journal.Error != null) AddLog("Log file error: " + journal.Error);
            _cancel.Dispose(); _cancel = null; IsBusy = false;
        }
    }
    private void AddLog(string message) { Log.Add($"[{DateTime.Now:HH:mm:ss}] {message}"); if (Log.Count > 2000) Log.RemoveAt(0); }
    internal static string? Browse(string title, string path)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = title, UseDescriptionForTitle = true, SelectedPath = Directory.Exists(path) ? path : "" };
        return dialog.ShowDialog() == Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }
}

public sealed class MergeSelection : ViewModelBase
{
    private bool _selected;
    private readonly Action _changed;
    public MergeSelection(MergeEntry entry, bool selected, Action changed) { Entry = entry; _selected = selected; _changed = changed; }
    public MergeEntry Entry { get; }
    public bool Selected { get => _selected; set { if (SetProperty(ref _selected, value)) _changed(); } }
}

public sealed class MergeMapping : ViewModelBase
{
    private string _source = "", _target = "";
    private readonly Action _changed;
    public MergeMapping(Action changed, Action<MergeMapping> remove)
    {
        _changed = changed;
        BrowseCommand = new(() => { var path = MergeViewModel.Browse("Select this computer's workspace", Target); if (path != null) Target = path; return Task.CompletedTask; });
        RemoveCommand = new(() => { remove(this); return Task.CompletedTask; });
    }
    public string Source { get => _source; set { if (SetProperty(ref _source, value)) _changed(); } }
    public string Target { get => _target; set { if (SetProperty(ref _target, value)) _changed(); } }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand RemoveCommand { get; }
}
