using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CodexShuttle.App.Services;

namespace CodexShuttle.App.Tests;

[TestClass]
public sealed class LogAutoScrollTests
{
    [TestMethod]
    public void CollectionNotification_DoesNotSynchronouslyScroll() => OnSta(() =>
    {
        using var view = new LogView();
        var inNotification = false;
        var scrolledInsideNotification = false;
        view.List.RequestBringIntoView += (_, _) => scrolledInsideNotification |= inNotification;
        inNotification = true;
        view.Log.Add("new line");
        inNotification = false;
        Assert.IsFalse(scrolledInsideNotification, "Layout must not run inside CollectionChanged.");
        Drain();
        Assert.IsNotNull(view.List.ItemContainerGenerator.ContainerFromIndex(0));
    });

    [DataTestMethod]
    [DataRow(300)]
    [DataRow(2000)]
    public void BoundedLog_BurstsTrimmingAndClear_KeepGeneratorConsistent(int capacity) => OnSta(() =>
    {
        using var view = new LogView();
        for (var i = 0; i < 6000; i++)
        {
            view.Log.Add($"line-{i:D5}");
            while (view.Log.Count > capacity) view.Log.RemoveAt(0);
            if (i % 2503 == 2502) view.Log.Clear();
            if (i % 17 == 0) Drain();
        }
        Drain();
        Assert.AreEqual(view.Log.Count, view.List.Items.Count);
        var last = view.List.ItemContainerGenerator.ContainerFromIndex(view.Log.Count - 1);
        Assert.IsNotNull(last, "Auto-scroll must still show the latest line.");
        Assert.AreEqual(view.Log[^1], ((ListBoxItem)last).Content);
    });

    [TestMethod]
    public void Burst_QueuesOnlyOneScroll() => OnSta(() =>
    {
        using var view = new LogView();
        var queued = 0;
        DispatcherHookEventHandler hook = (_, e) => { if (e.Operation.Priority == DispatcherPriority.Background) queued++; };
        Dispatcher.CurrentDispatcher.Hooks.OperationPosted += hook;
        try
        {
            for (var i = 0; i < 100; i++) view.Log.Add($"line-{i}");
            Assert.AreEqual(1, queued, "Coalesce a burst into one pending scroll.");
        }
        finally { Dispatcher.CurrentDispatcher.Hooks.OperationPosted -= hook; }
        Drain();
    });

    [TestMethod]
    public void DisablingFollowBeforeDispatch_DoesNotScroll() => OnSta(() =>
    {
        using var view = new LogView();
        var requests = 0;
        view.List.RequestBringIntoView += (_, _) => requests++;
        view.Log.Add("pending line");
        view.Follow = false;
        Drain();
        Assert.AreEqual(0, requests);
        view.Follow = true;
        view.Scroll.Request();
        Drain();
        Assert.IsTrue(requests > 0);
    });

    [TestMethod]
    public void ClearOrUnloadBeforeDispatch_DoesNotScrollStaleItems() => OnSta(() =>
    {
        using var view = new LogView();
        var requests = 0;
        view.List.RequestBringIntoView += (_, _) => requests++;
        view.Log.Add("removed before scroll");
        view.Log.Clear();
        Drain();
        Assert.AreEqual(0, requests);
        view.Log.Add("window closed before scroll");
        view.Window.Close();
        Drain();
        Assert.AreEqual(0, requests);
    });

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void OnSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "WPF test timed out.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class LogView : IDisposable
    {
        public ObservableCollection<string> Log { get; } = new();
        public ListBox List { get; } = new();
        public Window Window { get; }
        public LogAutoScroll Scroll { get; }
        public bool Follow { get; set; } = true;

        public LogView()
        {
            Scroll = new(List, () => Follow);
            // Match MainWindow: subscribe before WPF binds the ItemsSource.
            Log.CollectionChanged += (_, _) => Scroll.Request();
            List.ItemsSource = Log;
            VirtualizingPanel.SetIsVirtualizing(List, true);
            VirtualizingPanel.SetVirtualizationMode(List, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(List, true);
            Window = new Window { Content = List, Width = 400, Height = 200, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            Window.Show();
            Drain();
        }

        public void Dispose() => Window.Close();
    }
}
