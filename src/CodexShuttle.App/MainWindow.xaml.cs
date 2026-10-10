using System.Windows;
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
        model.BackupLog.CollectionChanged += (_, _) => FollowLog(BackupLogList, BackupFollow.IsChecked == true);
        model.RestoreLog.CollectionChanged += (_, _) => FollowLog(RestoreLogList, RestoreFollow.IsChecked == true);
        model.Merge.Log.CollectionChanged += (_, _) => FollowLog(MergeLogList, MergeFollow.IsChecked == true);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
    }

    private static void FollowLog(System.Windows.Controls.ListBox list, bool follow)
    {
        if (follow && list.Items.Count > 0) list.ScrollIntoView(list.Items[list.Items.Count - 1]);
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
