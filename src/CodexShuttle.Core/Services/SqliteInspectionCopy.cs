namespace CodexShuttle.Core.Services;

// Read-only SQLite connections can still write WAL read marks to the source SHM.
// Inspect a private copy of a quiescent database instead; never use immutable=1
// to suppress locking, since that can hide committed WAL contents.
internal sealed class SqliteInspectionCopy : IDisposable
{
    private readonly string _directory;
    public string DatabasePath { get; }

    private SqliteInspectionCopy()
    {
        _directory = Path.Combine(Path.GetTempPath(), "CodexShuttle", "sqlite-inspection", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        DatabasePath = Path.Combine(_directory, "inspection.sqlite");
    }

    public static async Task<SqliteInspectionCopy> CreateAsync(string source, CancellationToken token)
    {
        var copy = new SqliteInspectionCopy();
        var inputs = new List<(string Suffix, FileStream Stream)>();
        try
        {
            // Hold all durable source files read-only while copying. Fail on an
            // active writer rather than presenting an inconsistent inspection.
            foreach (var suffix in new[] { "", "-wal", "-journal" })
            {
                token.ThrowIfCancellationRequested();
                if (suffix.Length > 0 && !File.Exists(source + suffix)) continue;
                if (File.GetAttributes(source + suffix).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("Database inspection does not follow file links.");
                inputs.Add((suffix, new FileStream(source + suffix, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan)));
            }
            foreach (var (suffix, input) in inputs)
            {
                await using var output = new FileStream(copy.DatabasePath + suffix, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                await input.CopyToAsync(output, token);
            }
            return copy;
        }
        catch
        {
            copy.Dispose();
            throw;
        }
        finally
        {
            foreach (var (_, stream) in inputs) stream.Dispose();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
