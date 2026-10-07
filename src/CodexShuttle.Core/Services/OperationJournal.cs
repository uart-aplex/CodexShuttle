namespace CodexShuttle.Core.Services;

public sealed class OperationJournal : IProgress<string>, IDisposable
{
    private readonly object _sync = new();
    private readonly Action<Action> _dispatch;
    private readonly Action<string> _display;
    private StreamWriter? _writer;
    private bool _finished;
    public string FilePath { get; }
    public string? Error { get; private set; }

    public OperationJournal(string directory, string operation, Action<Action> dispatch, Action<string> display)
    {
        _dispatch = dispatch;
        _display = display;
        FilePath = Path.Combine(directory, $"{operation}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        try
        {
            Directory.CreateDirectory(directory);
            _writer = new StreamWriter(new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { AutoFlush = true };
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    public void Report(string message)
    {
        lock (_sync)
        {
            if (_finished) return;
            Write(message);
            _dispatch(() =>
            {
                lock (_sync)
                {
                    // Queued progress must never overwrite a terminal result or a later run.
                    if (!_finished) _display(message);
                }
            });
        }
    }

    public void Finish(string message)
    {
        lock (_sync)
        {
            if (_finished) return;
            _finished = true;
            Write(message);
            Close();
        }
    }

    private void Write(string message)
    {
        try { _writer?.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {message}"); }
        catch (Exception ex) { Error = ex.Message; }
    }

    private void Close()
    {
        try { _writer?.Dispose(); }
        catch (Exception ex) { Error = ex.Message; }
        _writer = null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (!_finished) Write("Operation ended without a confirmed final result.");
            _finished = true;
            Close();
        }
    }
}
