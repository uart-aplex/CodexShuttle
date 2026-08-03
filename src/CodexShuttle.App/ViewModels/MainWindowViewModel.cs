using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CodexShuttle.App.Services;
using CodexShuttle.Core.Models;
using CodexShuttle.Core.Services;
using Forms = System.Windows.Forms;

namespace CodexShuttle.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private CodexDetector _detector;
    private readonly CodexInspector _inspector = new();
    private readonly CodexProcessService _processService = new();
    private readonly FileMirrorService _fileMirrorService = new();
    private readonly ManifestService _manifestService = new();
    private readonly ReportService _reportService = new();
    private readonly DryRunService _dryRunService = new();
    private readonly AppSettingsService _settingsService = new();
    private readonly AppSettings _settings;
    private CancellationTokenSource? _backupCancellation;
    private CancellationTokenSource? _restoreCancellation;

    private string _codexHome = string.Empty;
    private string _codexRunningStatus = string.Empty;
    private string _backupDestination;
    private string _restorePackage = string.Empty;
    private string _lastDryRunFingerprint = string.Empty;
    private string _loadedRestorePackage = string.Empty;
    private string _restorePackageInfo = "Select a backup package.";
    private string _dryRunSummary = "Run Dry Run before restore.";
    private MigrationMode _selectedMigrationMode = MigrationMode.HistoryAndTools;
    private string _operationStatus = "Ready.";
    private string _sessionsStatus = "-";
    private string _archivedSessionsStatus = "-";
    private string _latestSessionStatus = "-";
    private string _stateSqliteStatus = "-";
    private string _sessionIndexStatus = "-";
    private string _globalStateStatus = "-";
    private string _configStatus = "-";
    private bool _isScanning;
    private bool _isBackupRunning;
    private bool _isRestoreRunning;
    private string _backupProgressStatus = "Idle.";
    private string _restoreProgressStatus = "Idle.";
    private PathStatusViewModel? _selectedWorkspace;

    public MainWindowViewModel()
    {
        _settings = _settingsService.Load();
        if (_settings.WorkspacePaths.Count == 0)
        {
            _settings.WorkspacePaths.AddRange(CodexDetector.DefaultWorkspacePaths);
        }
        _detector = new CodexDetector(_settings.WorkspacePaths, _settings.CodexHomeOverride);
        _backupDestination = string.IsNullOrWhiteSpace(_settings.BackupDestination)
            ? CreateDefaultBackupDestination()
            : PackagePathResolver.NormalizeBackupDestination(_settings.BackupDestination);
        _restorePackage = string.IsNullOrWhiteSpace(_settings.RestorePackage)
            ? string.Empty
            : PackagePathResolver.NormalizeRestorePackage(_settings.RestorePackage);
        if (Enum.TryParse<MigrationMode>(_settings.MigrationMode, out var savedMode))
        {
            _selectedMigrationMode = savedMode;
        }

        RefreshCommand = new RelayCommand(RefreshAsync, () => !IsAnyOperationRunning);
        BrowseBackupDestinationCommand = new RelayCommand(BrowseBackupDestinationAsync, () => !IsAnyOperationRunning);
        BrowseRestorePackageCommand = new RelayCommand(BrowseRestorePackageAsync, () => !IsAnyOperationRunning);
        CreateBackupCommand = new RelayCommand(CreateBackupAsync, () => !string.IsNullOrWhiteSpace(BackupDestination) && !IsAnyOperationRunning);
        CancelBackupCommand = new RelayCommand(CancelBackupAsync, () => IsBackupRunning);
        LoadRestorePackageCommand = new RelayCommand(LoadRestorePackageAsync, () => !string.IsNullOrWhiteSpace(RestorePackage) && !IsAnyOperationRunning);
        AddWorkspaceCommand = new RelayCommand(AddWorkspaceAsync, () => !IsAnyOperationRunning);
        RemoveWorkspaceCommand = new RelayCommand(RemoveWorkspaceAsync, () => SelectedWorkspace is not null && !IsAnyOperationRunning);
        BrowseCodexHomeCommand = new RelayCommand(BrowseCodexHomeAsync, () => !IsAnyOperationRunning);
        DryRunCommand = new RelayCommand(DryRunAsync, () => !string.IsNullOrWhiteSpace(RestorePackage) && !IsAnyOperationRunning);
        RestoreCommand = new RelayCommand(RestoreAsync, () => !string.IsNullOrWhiteSpace(RestorePackage) && HasCurrentDryRun && !IsAnyOperationRunning);
        CancelRestoreCommand = new RelayCommand(CancelRestoreAsync, () => IsRestoreRunning);

        _ = RefreshAsync();
        if (!string.IsNullOrWhiteSpace(_restorePackage))
        {
            _ = LoadRestorePackageAsync();
        }
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand BrowseBackupDestinationCommand { get; }
    public RelayCommand BrowseRestorePackageCommand { get; }
    public RelayCommand CreateBackupCommand { get; }
    public RelayCommand CancelBackupCommand { get; }
    public RelayCommand LoadRestorePackageCommand { get; }
    public RelayCommand AddWorkspaceCommand { get; }
    public RelayCommand RemoveWorkspaceCommand { get; }
    public RelayCommand BrowseCodexHomeCommand { get; }
    public RelayCommand DryRunCommand { get; }
    public RelayCommand RestoreCommand { get; }
    public RelayCommand CancelRestoreCommand { get; }
    public ObservableCollection<PathStatusViewModel> Workspaces { get; } = new();
    public ObservableCollection<PathStatusViewModel> AppDataPaths { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();
    public ObservableCollection<string> BackupLog { get; } = new();
    public ObservableCollection<string> RestoreLog { get; } = new();
    public ObservableCollection<RestoreTargetViewModel> RestoreTargets { get; } = new();
    public IReadOnlyList<MigrationMode> MigrationModes { get; } = Enum.GetValues<MigrationMode>();

    public PathStatusViewModel? SelectedWorkspace
    {
        get => _selectedWorkspace;
        set
        {
            if (SetProperty(ref _selectedWorkspace, value))
            {
                RemoveWorkspaceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string CodexHome
    {
        get => _codexHome;
        set => SetProperty(ref _codexHome, value);
    }

    public string CodexRunningStatus
    {
        get => _codexRunningStatus;
        set => SetProperty(ref _codexRunningStatus, value);
    }

    public string BackupDestination
    {
        get => _backupDestination;
        set
        {
            if (SetProperty(ref _backupDestination, value))
            {
                _settings.BackupDestination = value;
                SaveSettings();
                CreateBackupCommand.RaiseCanExecuteChanged();
                CancelBackupCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string RestorePackage
    {
        get => _restorePackage;
        set
        {
            if (SetProperty(ref _restorePackage, value))
            {
                _settings.RestorePackage = value;
                SaveSettings();
                InvalidateDryRun();
            }
        }
    }

    public MigrationMode SelectedMigrationMode
    {
        get => _selectedMigrationMode;
        set
        {
            if (SetProperty(ref _selectedMigrationMode, value))
            {
                _settings.MigrationMode = value.ToString();
                SaveSettings();
                InvalidateDryRun();
            }
        }
    }

    public string RestorePackageInfo
    {
        get => _restorePackageInfo;
        set => SetProperty(ref _restorePackageInfo, value);
    }

    public string DryRunSummary
    {
        get => _dryRunSummary;
        set => SetProperty(ref _dryRunSummary, value);
    }

    public string OperationStatus
    {
        get => _operationStatus;
        set => SetProperty(ref _operationStatus, value);
    }

    public string SessionsStatus
    {
        get => _sessionsStatus;
        set => SetProperty(ref _sessionsStatus, value);
    }

    public string ArchivedSessionsStatus
    {
        get => _archivedSessionsStatus;
        set => SetProperty(ref _archivedSessionsStatus, value);
    }

    public string LatestSessionStatus
    {
        get => _latestSessionStatus;
        set => SetProperty(ref _latestSessionStatus, value);
    }

    public string StateSqliteStatus
    {
        get => _stateSqliteStatus;
        set => SetProperty(ref _stateSqliteStatus, value);
    }

    public string SessionIndexStatus
    {
        get => _sessionIndexStatus;
        set => SetProperty(ref _sessionIndexStatus, value);
    }

    public string GlobalStateStatus
    {
        get => _globalStateStatus;
        set => SetProperty(ref _globalStateStatus, value);
    }

    public string ConfigStatus
    {
        get => _configStatus;
        set => SetProperty(ref _configStatus, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        set => SetProperty(ref _isScanning, value);
    }

    public bool IsBackupRunning
    {
        get => _isBackupRunning;
        set
        {
            if (SetProperty(ref _isBackupRunning, value))
            {
                RaiseOperationCommandStates();
                OnPropertyChanged(nameof(IsAnyOperationRunning));
            }
        }
    }

    public bool IsRestoreRunning
    {
        get => _isRestoreRunning;
        set
        {
            if (SetProperty(ref _isRestoreRunning, value))
            {
                RaiseOperationCommandStates();
                OnPropertyChanged(nameof(IsAnyOperationRunning));
            }
        }
    }

    public string BackupProgressStatus
    {
        get => _backupProgressStatus;
        set => SetProperty(ref _backupProgressStatus, value);
    }

    public string RestoreProgressStatus
    {
        get => _restoreProgressStatus;
        set => SetProperty(ref _restoreProgressStatus, value);
    }

    public bool IsAnyOperationRunning => IsBackupRunning || IsRestoreRunning;

    private bool HasCurrentDryRun => !string.IsNullOrWhiteSpace(_lastDryRunFingerprint)
        && _lastDryRunFingerprint.Equals(CreateRestoreFingerprint(), StringComparison.Ordinal);

    private async Task RefreshAsync()
    {
        IsScanning = true;
        OperationStatus = "Scanning Codex files and workspaces...";

        CodexDetectionResult detection;
        CodexInspectionResult inspection;

        try
        {
            (detection, inspection) = await Task.Run(() =>
            {
                var detected = _detector.Detect();
                var inspected = _inspector.Inspect(detected.CodexHome);
                return (detected, inspected);
            });
        }
        catch (Exception ex)
        {
            OperationStatus = $"Scan failed: {ex.Message}";
            IsScanning = false;
            return;
        }

        CodexHome = detection.CodexHome;
        CodexRunningStatus = _processService.IsCodexRunning()
            ? "Codex is running. Close it before backup or restore."
            : "Codex is not running.";

        Workspaces.Clear();
        foreach (var workspace in detection.WorkspacePaths)
        {
            Workspaces.Add(ToPathStatus(Path.GetFileName(workspace.SourcePath.TrimEnd('\\')), workspace));
        }

        AppDataPaths.Clear();
        foreach (var appData in detection.AppDataPaths)
        {
            AppDataPaths.Add(new PathStatusViewModel
            {
                Name = Path.GetFileName(appData.SourcePath),
                Path = appData.SourcePath,
                Exists = YesNo(appData.Exists),
                FileCount = appData.FileCount.ToString("N0"),
                Size = ReportService.FormatBytes(appData.TotalBytes),
                LatestWrite = "-"
            });
        }

        ApplyInspection(inspection);
        OperationStatus = "Inspection refreshed.";
        IsScanning = false;
    }

    private Task BrowseBackupDestinationAsync()
    {
        var selected = BrowseForFolder("Select backup destination", BackupDestination);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            BackupDestination = PackagePathResolver.NormalizeBackupDestination(selected);
        }

        return Task.CompletedTask;
    }

    private async Task BrowseRestorePackageAsync()
    {
        var selected = BrowseForFolder("Select restore package folder", RestorePackage);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            RestorePackage = PackagePathResolver.NormalizeRestorePackage(selected);
            await LoadRestorePackageAsync();
        }
    }

    private async Task LoadRestorePackageAsync()
    {
        try
        {
            var package = PackagePathResolver.NormalizeRestorePackage(RestorePackage);
            if (!package.Equals(RestorePackage, StringComparison.OrdinalIgnoreCase))
            {
                RestorePackage = package;
            }

            var manifest = await _manifestService.LoadAsync(Path.Combine(package, "manifest.json"));
            if (manifest is null)
            {
                RestorePackageInfo = "manifest.json was not found.";
                RestoreTargets.Clear();
                return;
            }

            var validation = new BackupPackageValidator().Validate(package, manifest, requireChecksums: false);
            if (!validation.Success)
            {
                RestorePackageInfo = validation.Message;
                RestoreTargets.Clear();
                return;
            }

            RestoreTargets.Clear();
            foreach (var workspace in manifest.WorkspacePaths.Where(item => item.Enabled && item.Exists))
            {
                var item = new RestoreTargetViewModel()
                {
                    Name = Path.GetFileName(workspace.SourcePath.TrimEnd('\\', '/')),
                    SourcePath = workspace.SourcePath,
                    PackagePath = workspace.PackagePath,
                    TargetPath = workspace.SourcePath
                };
                RestoreTargets.Add(item);
            }

            _loadedRestorePackage = package;
            var currentCodexHome = new RestorePathResolver(_settings.CodexHomeOverride).GetCurrentCodexHome();
            RestorePackageInfo = $"From {manifest.SourceComputer} / {manifest.SourceUser}, {manifest.CreatedAt:yyyy-MM-dd HH:mm}; user data restores to {currentCodexHome}; schema {manifest.SchemaVersion}; credentials excluded: {(manifest.CredentialsExcluded ? "Yes" : "No")}.";
            OperationStatus = "Restore package loaded. Workspace paths are fixed to their original locations. Run Dry Run to verify them.";
            InvalidateDryRun();
        }
        catch (Exception ex)
        {
            RestorePackageInfo = $"Could not load package: {ex.Message}";
            RestoreTargets.Clear();
        }
    }

    private async Task AddWorkspaceAsync()
    {
        var selected = BrowseForFolder("Select workspace to include in backup", string.Empty);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        selected = Path.GetFullPath(selected);
        if (!_settings.WorkspacePaths.Contains(selected, StringComparer.OrdinalIgnoreCase))
        {
            _settings.WorkspacePaths.Add(selected);
            SaveSettings();
            _detector = new CodexDetector(_settings.WorkspacePaths, _settings.CodexHomeOverride);
            await RefreshAsync();
        }
    }

    private async Task RemoveWorkspaceAsync()
    {
        if (SelectedWorkspace is null)
        {
            return;
        }

        _settings.WorkspacePaths.RemoveAll(path =>
            path.Equals(SelectedWorkspace.Path, StringComparison.OrdinalIgnoreCase));
        SaveSettings();
        _detector = new CodexDetector(_settings.WorkspacePaths, _settings.CodexHomeOverride);
        SelectedWorkspace = null;
        await RefreshAsync();
    }

    private async Task BrowseCodexHomeAsync()
    {
        var selected = BrowseForFolder("Select Codex Home (the folder containing sessions, skills and config.toml)", CodexHome);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        _settings.CodexHomeOverride = Path.GetFullPath(selected);
        SaveSettings();
        _detector = new CodexDetector(_settings.WorkspacePaths, _settings.CodexHomeOverride);
        await RefreshAsync();
    }

    private async Task CreateBackupAsync()
    {
        if (_processService.IsCodexRunning())
        {
            OperationStatus = "Close Codex before creating a backup.";
            System.Windows.MessageBox.Show("Please close Codex before backup.", "Codex Shuttle", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        OperationStatus = "Creating backup...";
        IsBackupRunning = true;
        _backupCancellation = new CancellationTokenSource();
        BackupLog.Clear();
        AddBackupLog($"Backup destination: {BackupDestination}");

        var progress = new Progress<string>(message =>
        {
            BackupProgressStatus = message;
            OperationStatus = message;
            AddBackupLog(message);
        });

        var service = new BackupService(_detector, _inspector, _fileMirrorService, _manifestService, _reportService);
        try
        {
            var backupRoot = BackupDestination;
            var cancellationToken = _backupCancellation.Token;
            var result = await Task.Run(
                () => service.CreateBackupAsync(
                    backupRoot,
                    includeAppData: false,
                    cancellationToken: cancellationToken,
                    progress: progress),
                cancellationToken);
            OperationStatus = result.Success ? $"Backup created: {BackupDestination}" : result.Message;
            BackupProgressStatus = OperationStatus;
            AddBackupLog(OperationStatus);
        }
        catch (OperationCanceledException)
        {
            OperationStatus = "Backup canceled.";
            BackupProgressStatus = OperationStatus;
            AddBackupLog(OperationStatus);
        }
        catch (Exception ex)
        {
            OperationStatus = $"Backup failed: {ex.Message}";
            BackupProgressStatus = OperationStatus;
            AddBackupLog(OperationStatus);
            AddBackupLog("Please check that the destination drive exists and is writable.");
        }
        finally
        {
            _backupCancellation?.Dispose();
            _backupCancellation = null;
            IsBackupRunning = false;
        }
    }

    private Task CancelBackupAsync()
    {
        if (!IsBackupRunning)
        {
            return Task.CompletedTask;
        }

        AddBackupLog("Cancel requested. Stopping active copy process...");
        OperationStatus = "Canceling backup...";
        BackupProgressStatus = "Canceling backup...";
        _backupCancellation?.Cancel();
        return Task.CompletedTask;
    }

    public bool RequestClose()
    {
        if (!IsBackupRunning && !IsRestoreRunning)
        {
            return true;
        }

        var answer = System.Windows.MessageBox.Show(
            "A backup or restore is still running. Closing now will stop the copy process and may leave the target incomplete. Close anyway?",
            "Cancel Operation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return false;
        }

        _backupCancellation?.Cancel();
        _restoreCancellation?.Cancel();
        return true;
    }

    private async Task DryRunAsync()
    {
        OperationStatus = "Building complete restore plan...";
        DryRunSummary = "Scanning every selected restore target...";
        try
        {
            if (!_loadedRestorePackage.Equals(RestorePackage, StringComparison.OrdinalIgnoreCase))
            {
                await LoadRestorePackageAsync();
            }

            var package = RestorePackage;
            var manifest = await _manifestService.LoadAsync(Path.Combine(package, "manifest.json"));
            if (manifest is null)
            {
                throw new InvalidDataException("manifest.json was not found in the restore package.");
            }

            var validation = new BackupPackageValidator().Validate(package, manifest, requireChecksums: true);
            if (!validation.Success)
            {
                throw new InvalidDataException(validation.Message);
            }

            var options = CreateRestoreOptions();
            var pathResolver = new RestorePathResolver(_settings.CodexHomeOverride);
            var result = await Task.Run(() =>
            {
                var plan = new RestorePlanner(pathResolver).CreatePlan(package, manifest, options);
                return _dryRunService.ComparePlan(plan);
            });

            DryRunSummary = $"All targets: add {result.FilesToCopy:N0}, overwrite {result.FilesToOverwrite:N0}, delete {result.FilesToDelete:N0}; copy {ReportService.FormatBytes(result.TotalBytesToCopy)}.";
            OperationStatus = result.FilesToDelete > 0
                ? $"Dry Run completed. Review warning: {result.FilesToDelete:N0} destination-only files will be deleted."
                : "Dry Run completed. No destination-only files will be deleted.";
            _lastDryRunFingerprint = CreateRestoreFingerprint();
            RestoreCommand.RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            InvalidateDryRun();
            DryRunSummary = $"Dry Run failed: {ex.Message}";
            OperationStatus = DryRunSummary;
            System.Windows.MessageBox.Show(DryRunSummary, "Dry Run Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RestoreAsync()
    {
        if (!HasCurrentDryRun)
        {
            OperationStatus = "The package, migration mode, or a target path changed. Run Dry Run again.";
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            $"Restore mode: {SelectedMigrationMode}\n\n{DryRunSummary}\n\nMirror restore can overwrite files and delete destination-only files. Credentials are not migrated. Continue?",
            "Confirm Restore",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            OperationStatus = "Restore canceled.";
            return;
        }

        if (_processService.IsCodexRunning())
        {
            OperationStatus = "Close Codex before restore.";
            System.Windows.MessageBox.Show("Please close Codex before restore.", "Codex Shuttle", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        OperationStatus = "Restoring...";
        RestoreProgressStatus = "Starting restore...";
        IsRestoreRunning = true;
        _restoreCancellation = new CancellationTokenSource();
        RestoreLog.Clear();
        var pathResolver = new RestorePathResolver(_settings.CodexHomeOverride);
        var service = new RestoreService(_fileMirrorService, _manifestService, pathResolver);
        var progress = new Progress<string>(message =>
        {
            OperationStatus = message;
            RestoreProgressStatus = message;
            AddRestoreLog(message);
        });
        try
        {
            var restorePackage = RestorePackage;
            var options = CreateRestoreOptions();
            var cancellationToken = _restoreCancellation.Token;
            var result = await Task.Run(
                () => service.RestoreAsync(
                    restorePackage,
                    options,
                    cancellationToken: cancellationToken,
                    progress: progress),
                cancellationToken);
            OperationStatus = result.Success ? "Restore completed." : result.Message;
            RestoreProgressStatus = OperationStatus;
            AddRestoreLog(OperationStatus);
        }
        catch (OperationCanceledException)
        {
            OperationStatus = "Restore canceled.";
            RestoreProgressStatus = OperationStatus;
            AddRestoreLog(OperationStatus);
        }
        catch (Exception ex)
        {
            OperationStatus = $"Restore failed: {ex.Message}";
            RestoreProgressStatus = OperationStatus;
            AddRestoreLog(OperationStatus);
        }
        finally
        {
            _restoreCancellation?.Dispose();
            _restoreCancellation = null;
            IsRestoreRunning = false;
        }
    }

    private Task CancelRestoreAsync()
    {
        if (IsRestoreRunning)
        {
            OperationStatus = "Canceling restore...";
            RestoreProgressStatus = OperationStatus;
            AddRestoreLog("Cancel requested. Stopping active copy process...");
            _restoreCancellation?.Cancel();
        }

        return Task.CompletedTask;
    }

    private void ApplyInspection(CodexInspectionResult inspection)
    {
        SessionsStatus = inspection.SessionsFolderExists ? $"{inspection.SessionsFileCount:N0} files" : "Missing";
        ArchivedSessionsStatus = inspection.ArchivedSessionsFolderExists ? $"{inspection.ArchivedSessionsFileCount:N0} files" : "Missing";
        LatestSessionStatus = inspection.LatestSessionWriteTime?.ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "None";
        StateSqliteStatus = Status(inspection.StateSqliteExists, inspection.StateSqliteReadable, "Readable");
        SessionIndexStatus = Status(inspection.SessionIndexExists, inspection.SessionIndexReadable, "Readable");
        GlobalStateStatus = Status(inspection.GlobalStateExists, inspection.GlobalStateJsonValid, "Valid JSON");
        ConfigStatus = inspection.ConfigTomlExists ? "Exists" : "Missing";

        Warnings.Clear();
        foreach (var warning in inspection.Warnings)
        {
            Warnings.Add(warning);
        }
    }

    private static PathStatusViewModel ToPathStatus(string name, WorkspaceEntry workspace)
    {
        return new PathStatusViewModel
        {
            Name = name,
            Path = workspace.SourcePath,
            Exists = YesNo(workspace.Exists),
            FileCount = workspace.FileCount.ToString("N0"),
            Size = ReportService.FormatBytes(workspace.TotalBytes),
            LatestWrite = workspace.LatestWriteTime?.ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "-"
        };
    }

    private static string YesNo(bool value) => value ? "Yes" : "No";

    private static string Status(bool exists, bool secondaryOk, string secondaryLabel)
    {
        if (!exists)
        {
            return "Missing";
        }

        return secondaryOk ? $"Exists, {secondaryLabel}" : "Exists, check failed";
    }

    private static string CreateDefaultBackupDestination()
    {
        return PackagePathResolver.CreateDefaultBackupDestination();
    }

    private void SaveSettings()
    {
        try
        {
            _settingsService.Save(_settings);
        }
        catch (Exception ex)
        {
            OperationStatus = $"Could not save settings: {ex.Message}";
        }
    }

    private void AddBackupLog(string message)
    {
        BackupLog.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (BackupLog.Count > 300)
        {
            BackupLog.RemoveAt(0);
        }
    }

    private void AddRestoreLog(string message)
    {
        RestoreLog.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (RestoreLog.Count > 300)
        {
            RestoreLog.RemoveAt(0);
        }
    }

    private RestoreOptions CreateRestoreOptions()
    {
        return new RestoreOptions
        {
            MigrationMode = SelectedMigrationMode,
            IncludeAppData = false,
            RequireOriginalWorkspacePaths = true,
            WorkspaceTargets = RestoreTargets.ToDictionary(
                item => item.SourcePath,
                item => item.TargetPath,
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private string CreateRestoreFingerprint()
    {
        var manifestPath = Path.Combine(RestorePackage, "manifest.json");
        var checksumPath = Path.Combine(RestorePackage, "checksums.sha256");
        var manifestStamp = File.Exists(manifestPath)
            ? $"{new FileInfo(manifestPath).Length}:{File.GetLastWriteTimeUtc(manifestPath).Ticks}"
            : "missing";
        var checksumStamp = File.Exists(checksumPath)
            ? $"{new FileInfo(checksumPath).Length}:{File.GetLastWriteTimeUtc(checksumPath).Ticks}"
            : "missing";
        var targets = string.Join("|", RestoreTargets
            .OrderBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Select(item => $"{item.SourcePath}>{item.TargetPath}"));
        return $"{Path.GetFullPath(RestorePackage)}|{manifestStamp}|{checksumStamp}|{SelectedMigrationMode}|{targets}";
    }

    private void InvalidateDryRun()
    {
        _lastDryRunFingerprint = string.Empty;
        DryRunSummary = "Run Dry Run before restore.";
        RestoreCommand.RaiseCanExecuteChanged();
        DryRunCommand.RaiseCanExecuteChanged();
    }

    private void RaiseOperationCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        BrowseBackupDestinationCommand.RaiseCanExecuteChanged();
        BrowseRestorePackageCommand.RaiseCanExecuteChanged();
        CreateBackupCommand.RaiseCanExecuteChanged();
        CancelBackupCommand.RaiseCanExecuteChanged();
        LoadRestorePackageCommand.RaiseCanExecuteChanged();
        AddWorkspaceCommand.RaiseCanExecuteChanged();
        RemoveWorkspaceCommand.RaiseCanExecuteChanged();
        BrowseCodexHomeCommand.RaiseCanExecuteChanged();
        DryRunCommand.RaiseCanExecuteChanged();
        RestoreCommand.RaiseCanExecuteChanged();
        CancelRestoreCommand.RaiseCanExecuteChanged();
    }

    private static string? BrowseForFolder(string description, string currentPath)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            dialog.SelectedPath = Directory.Exists(currentPath)
                ? currentPath
                : Path.GetDirectoryName(currentPath) ?? string.Empty;
        }

        return dialog.ShowDialog() == Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }
}
