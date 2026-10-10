param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\layout'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$xamlPath = Join-Path $PSScriptRoot '..\src\CodexShuttle.App\MainWindow.xaml'
[xml]$document = Get-Content -LiteralPath $xamlPath -Raw
$document.DocumentElement.RemoveAttribute('Class', 'http://schemas.microsoft.com/winfx/2006/xaml')
$document.DocumentElement.RemoveAttribute('Ignorable', 'http://schemas.openxmlformats.org/markup-compatibility/2006')
$window = [Windows.Markup.XamlReader]::Load([Xml.XmlNodeReader]::new($document))
$log = @(1..150 | ForEach-Object { "[09:03:44] Current file: E:\CodexWorkspace\SoftwareDevelopment\Example\src\nested-folder\sample-file-$_.cs" })
$window.DataContext = [pscustomobject]@{
    BackupDestination = 'L:\CodexTransfer\CodexShuttle-Current'
    RestorePackage = 'L:\CodexTransfer\CodexShuttle-Current'
    BackupResult = 'Backup failed; no new backup was saved. Previous backup retained: 2026-09-29 18:59:38 +08:00.'
    RestoreResult = 'Restore completed. Verified 134 conversation paths.'
    BackupResultBrush = '#B42318'
    RestoreResultBrush = '#166534'
    OperationStatus = 'Backup failed; previous backup retained.'
    RestorePackageInfo = 'From OFFICE / source-user, 2026-09-29 18:59. User data restores to this computer.'
    DryRunSummary = '3 workspaces selected. Destination-only files may be deleted.'
    MigrationModes = @('HistoryAndTools', 'FullProfile')
    SelectedMigrationMode = 'FullProfile'
    BackupLog = $log
    RestoreLog = $log
    Merge = [pscustomobject]@{
        Package = 'L:\CodexTransfer\CodexShuttle-Current'
        ProfileRoot = 'C:\Users\HomeUser\.codex'
        Status = '3 additions, 2 identical, 1 conflict, 0 blocked. Selected: 4. Source: OFFICE, 2026-10-10 09:02 +08:00.'
        CanEdit = $true
        IsBusy = $false
        Log = $log
        Sessions = @([pscustomobject]@{Selected=$false; Entry=[pscustomobject]@{Title='Example project';Action='Conflict';Id='11111111-1111-1111-1111-111111111111';TargetPath='C:\Users\HomeUser\AppData\Local\CodexShuttle\merge\conflicts\example.jsonl';Reason='Same ID, different contents'}})
        Mappings = @()
        Conflicts = @()
        Points = @()
    }
    IsScanning = $false
    IsBackupRunning = $false
    IsRestoreRunning = $false
    Workspaces = @('E:\app', 'E:\doc', 'E:\CodexWorkspace') | ForEach-Object {
        [pscustomobject]@{Path=$_; Exists='Yes'; FileCount='12,000'; Size='12 GB'}
    }
    RestoreTargets = @([pscustomobject]@{SourcePath='L:\CodexTransfer\CodexShuttle-Current\E_CodexWorkspace';TargetPath='E:\CodexWorkspace'})
}
$tabs = $window.FindName('MainTabs')
$backupLog = $window.FindName('BackupLogList')
$restoreLog = $window.FindName('RestoreLogList')
$content = $window.Content
$content.DataContext = $window.DataContext
$content.Resources = $window.Resources
$window.Content = $null
[void][IO.Directory]::CreateDirectory($OutputDirectory)
foreach ($size in @(@(760,440), @(800,560), @(1024,680), @(1366,720))) {
    foreach ($tabIndex in @(1,2,3,5)) {
        $tabs.SelectedIndex = $tabIndex
        $content.Measure([Windows.Size]::new($size[0], $size[1]))
        $content.Arrange([Windows.Rect]::new(0,0,$size[0],$size[1]))
        $content.UpdateLayout()
        $list = switch ($tabIndex) { 1 { $backupLog } 2 { $restoreLog } 3 { $window.FindName('MergeLogList') } 5 { $window.FindName('MergeRecoveryLogList') } }
        if ($list.ActualHeight -lt ($size[1] * 0.45) -or $list.ActualWidth -lt 280) {
            throw "Log area too small: $($list.ActualWidth) x $($list.ActualHeight)"
        }
        $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size[0],$size[1],96,96,[Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($content)
        $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $path = Join-Path $OutputDirectory ("tab{0}-{1}x{2}.png" -f $tabIndex,$size[0],$size[1])
        $stream = [IO.File]::Create($path)
        try { $encoder.Save($stream) } finally { $stream.Dispose() }
        Write-Output "PASS $path (log $($list.ActualWidth) x $($list.ActualHeight))"
    }
}
