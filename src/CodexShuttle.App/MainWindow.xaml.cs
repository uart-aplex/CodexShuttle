using System.Windows;
using CodexShuttle.App.Services;
using CodexShuttle.App.ViewModels;

namespace CodexShuttle.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "Codex Shuttle " + (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(MainWindow).Assembly)?.InformationalVersion.Split('+')[0]
            ?? typeof(MainWindow).Assembly.GetName().Version?.ToString(3));
        DataContext = new MainWindowViewModel();
        var model = (MainWindowViewModel)DataContext;
        var backupScroll = new LogAutoScroll(BackupLogList, () => BackupFollow.IsChecked == true);
        var restoreScroll = new LogAutoScroll(RestoreLogList, () => RestoreFollow.IsChecked == true);
        var mergeScroll = new LogAutoScroll(MergeLogList, () => MergeFollow.IsChecked == true);
        model.BackupLog.CollectionChanged += (_, _) => backupScroll.Request();
        model.RestoreLog.CollectionChanged += (_, _) => restoreScroll.Request();
        model.Merge.Log.CollectionChanged += (_, _) => mergeScroll.Request();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && !viewModel.RequestClose())
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }
}
