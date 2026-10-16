using ListBox = System.Windows.Controls.ListBox;
using System.Windows.Threading;

namespace CodexShuttle.App.Services;

internal sealed class LogAutoScroll(ListBox list, Func<bool> enabled)
{
    private DispatcherOperation? _pending;

    public void Request()
    {
        if (!enabled() || list.Dispatcher.HasShutdownStarted || list.Dispatcher.HasShutdownFinished
            || _pending?.Status == DispatcherOperationStatus.Pending) return;

        // CollectionChanged may reach us before WPF's item generator. Defer layout
        // until all subscribers have updated, coalescing bursts into one scroll.
        _pending = list.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _pending = null;
            if (enabled() && list.IsLoaded && list.IsVisible && list.Items.Count > 0)
                list.ScrollIntoView(list.Items[list.Items.Count - 1]);
        }));
    }
}
