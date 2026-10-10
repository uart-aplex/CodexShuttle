using System.Diagnostics;
using System.Text.Json;
using CodexShuttle.Core.Merge;

namespace CodexShuttle.Tests;

[TestClass]
public sealed class MergeAppServerTests
{
    [TestMethod]
    public async Task InstalledAppServer_CanReadAndResumeImportedThread_InIsolatedProfile()
    {
        var executable = Environment.GetEnvironmentVariable("CODEXSHUTTLE_TEST_CODEX");
        if (string.IsNullOrEmpty(executable)) Assert.Inconclusive("Set CODEXSHUTTLE_TEST_CODEX to opt into the isolated app-server compatibility test.");
        using var fixture = new MergeFixture();
        File.Delete(Path.Combine(fixture.Target, "state_5.sqlite"));
        await using (var bootstrap = new IsolatedAppServer(executable!, fixture))
        {
            await bootstrap.Initialize();
            await bootstrap.Call("thread/start", new { cwd = fixture.Workspace, approvalPolicy = "never", sandbox = "read-only" });
        }
        var id = fixture.AddSession(fixture.Source, "Synthetic migration acceptance message");
        using (var source = MergeDatabase.Open(Path.Combine(fixture.Source, "state_5.sqlite"), true))
        using (var command = source.CreateCommand())
        {
            command.CommandText = """
                ALTER TABLE threads ADD COLUMN source TEXT NOT NULL DEFAULT 'cli';
                ALTER TABLE threads ADD COLUMN sandbox_policy TEXT NOT NULL DEFAULT '{"type":"read-only"}';
                ALTER TABLE threads ADD COLUMN approval_mode TEXT NOT NULL DEFAULT 'never';
                ALTER TABLE threads ADD COLUMN tokens_used INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE threads ADD COLUMN has_user_event INTEGER NOT NULL DEFAULT 1;
                ALTER TABLE threads ADD COLUMN archived INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE threads ADD COLUMN cli_version TEXT NOT NULL DEFAULT '0.162.0-alpha.2';
                ALTER TABLE threads ADD COLUMN first_user_message TEXT NOT NULL DEFAULT 'Synthetic migration acceptance message';
                UPDATE threads SET created_at=1791590400, updated_at=1791590401;
                """;
            command.ExecuteNonQuery();
        }
        await fixture.Package();
        var service = fixture.Service();
        var preview = await service.PreviewAsync(fixture.Request(id));
        Assert.AreEqual(MergeAction.Add, preview.Entries.Single().Action, preview.Entries.Single().Reason);
        var result = await service.ApplyAsync(preview);
        Assert.IsTrue(result.Success, result.Message + string.Join(";", result.Errors));
        await using var server = new IsolatedAppServer(executable!, fixture);
        await server.Initialize();
        var listed = await server.Call("thread/list", new { limit = 100 });
        Assert.IsTrue(listed.GetProperty("data").EnumerateArray().Any(t => t.GetProperty("id").GetString() == id), "Imported thread must appear in the public thread inventory.");
        var read = await server.Call("thread/read", new { threadId = id, includeTurns = true });
        Assert.AreEqual(id, read.GetProperty("thread").GetProperty("id").GetString());
        StringAssert.Contains(read.GetRawText(), "Synthetic migration acceptance message");
        var resumed = await server.Call("thread/resume", new { threadId = id, cwd = fixture.Workspace, approvalPolicy = "never", sandbox = "read-only" });
        Assert.AreEqual(id, resumed.GetProperty("thread").GetProperty("id").GetString());
        // No turn/start: this check does not invoke a model or use production credentials.
    }

    private sealed class IsolatedAppServer : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _errors;
        private int _id;
        public IsolatedAppServer(string executable, MergeFixture fixture)
        {
            var start = new ProcessStartInfo(executable, "app-server") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = fixture.Workspace };
            start.Environment["CODEX_HOME"] = fixture.Target;
            start.Environment["CODEX_SQLITE_HOME"] = fixture.Target;
            start.Environment["HOME"] = Path.GetDirectoryName(fixture.Target)!;
            start.Environment["USERPROFILE"] = Path.GetDirectoryName(fixture.Target)!;
            start.Environment["APPDATA"] = Path.Combine(fixture.Root, "appdata");
            start.Environment["LOCALAPPDATA"] = Path.Combine(fixture.Root, "localappdata");
            foreach (var key in start.Environment.Keys.Where(k => k.Contains("API_KEY", StringComparison.OrdinalIgnoreCase) || k.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(key);
            _process = Process.Start(start)!;
            _errors = _process.StandardError.ReadToEndAsync();
        }
        public async Task Initialize()
        {
            await Call("initialize", new { clientInfo = new { name = "codex_shuttle_test", version = "0.3.0" } });
            await _process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}");
            await _process.StandardInput.FlushAsync();
        }
        public async Task<JsonElement> Call(string method, object parameters)
        {
            var id = ++_id;
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }));
            await _process.StandardInput.FlushAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null) throw new IOException("Isolated app-server exited: " + await _errors);
                using var response = JsonDocument.Parse(line);
                if (!response.RootElement.TryGetProperty("id", out var resultId) || resultId.ValueKind != JsonValueKind.Number || resultId.GetInt32() != id) continue;
                if (response.RootElement.TryGetProperty("error", out var error)) throw new IOException(method + ": " + error.GetRawText());
                return response.RootElement.GetProperty("result").Clone();
            }
        }
        public async ValueTask DisposeAsync()
        {
            _process.StandardInput.Close();
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); }
            await _errors;
            _process.Dispose();
        }
    }
}
