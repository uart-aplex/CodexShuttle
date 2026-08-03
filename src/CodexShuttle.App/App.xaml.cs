namespace CodexShuttle.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, "Local\\CodexShuttle.SingleInstance", out var createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "Codex Shuttle is already running. Only one backup or restore instance can run at a time.",
                "Codex Shuttle",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
