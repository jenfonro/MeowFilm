using System.Reflection;
using System.Text;
using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Platform.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class RuntimeSettingsPipeTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("runtime-settings.large-request-response-and-persisted-fields", LargeFrames);
        yield return new("runtime-settings.incomplete-frame-is-not-accepted", IncompleteFrame);
    }

    private static async Task LargeFrames()
    {
        string root = TestWorkspace.DataPath("settings-pipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string value = new('x', 8 * 1024 * 1024 + 17);
            string write = JsonSerializer.Serialize(new
            {
                kind = "settings.pan.update", id = 1, block = "pan.accounts",
                command = new { provider = "baidu", mode = "replace", accountRevision = 0,
                    rowRevision = 0, credentials = new { cookie = value } }
            });
            using var stream = new DuplexFixture(write + "\n" +
                """{"kind":"settings.read","id":2,"block":"pan.accounts"}""" + "\n");
            await using var database = new NativeTestDatabase(root);
            var store = new FilmRuntimeSettings(database);
            Check(await Run(stream, store) == "CLOSED");
            string[] replies = Encoding.UTF8.GetString(stream.Output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Check(replies.Length == 2);
            for (int index = 0; index < replies.Length; index++)
            {
                using var reply = JsonDocument.Parse(replies[index]);
                Check(reply.RootElement.GetProperty("ok").GetBoolean() &&
                    reply.RootElement.GetProperty("id").GetInt32() == index + 1 &&
                    reply.RootElement.GetProperty("revision").GetInt64() == 1 &&
                    reply.RootElement.GetProperty("document").GetProperty("accounts")
                        .GetProperty("baidu").GetProperty("credentials").GetProperty("cookie").GetString() == value);
            }
            Check(await database.ReadAsync(db => db.Scalar<string>(
                "SELECT cookie FROM windows_pan_accounts WHERE provider='baidu'")) == value);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task IncompleteFrame()
    {
        using var stream = new DuplexFixture("""{"kind":"settings.read","id":1""");
        Check(await Run(stream, null) == "INVALID_RESPONSE" && stream.Output.Length == 0);
    }

    private static Task<string> Run(Stream stream, IRuntimeSettingsStore? store)
    {
        var type = typeof(NodeRuntime).Assembly.GetType("MeowFilm.Platform.Runtime.RuntimeSettingsPipe", throwOnError: true)!;
        var pipe = Activator.CreateInstance(type, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, args: [stream, store], culture: null)!;
        return (Task<string>)type.GetMethod("RunAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(pipe, null)!;
    }

    private static void Check(bool value)
    {
        if (!value) throw new InvalidOperationException("Settings pipe assertion failed.");
    }

    private sealed class DuplexFixture(string input) : Stream
    {
        private readonly MemoryStream source = new(Encoding.UTF8.GetBytes(input));
        internal readonly MemoryStream Output = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => source.ReadAsync(buffer, token);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => Output.WriteAsync(buffer, token);
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => Output.Write(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { source.Dispose(); Output.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
