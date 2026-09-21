using System.Windows;
using CodexShuttle.App.ViewModels;

namespace CodexShuttle.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = $"Codex Shuttle {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        DataContext = new MainWindowViewModel();
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
