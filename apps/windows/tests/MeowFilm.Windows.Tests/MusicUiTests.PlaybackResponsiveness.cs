using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Files;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static partial class MusicUiTests
{
    private static IEnumerable<TestCase> PlaybackResponsivenessCases()
    {
        yield return Case("native next-track hot writes leave the dispatcher responsive and select the exact next entry", async () =>
        {
            await using var f = await PlaybackResponsivenessFixture.Create();
            int writes = f.Store.LibraryWrites;
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                var rejected = await f.Application.ExecuteAsync("music.queue.next", new { reason = "manual" }, cancelled.Token);
                True(!rejected.GetProperty("ok").GetBoolean(), "a pre-cancelled next-track request is rejected");
                Equal("CANCELLED", rejected.GetProperty("code").GetString());
            }
            Equal(writes, f.Store.LibraryWrites);
            var before = await f.Rpc("music.queue.get", new { offset = 0, limit = 100 });
            Equal(f.EntryIds[0], before.GetProperty("currentId").GetString());

            using var hold = f.Store.HoldNextLibraryWrite();
            var dispatcher = Dispatcher.CurrentDispatcher;
            int uiThread = Environment.CurrentManagedThreadId;
            var observation = hold.ObserveDispatcher(dispatcher);
            Task step = Task.CompletedTask;
            try
            {
                step = f.Module.StepAsync(true);
                bool responsive = await observation;
                await step;
                True(responsive, "the dispatcher executes while the synchronous next-track CAS is still held");
                True(hold.WriterThreadId != uiThread, "the admitted native library write does not run on the UI thread");
                Equal(writes + 1, f.Store.LibraryWrites);
                var after = await f.Rpc("music.queue.get", new { offset = 0, limit = 100 });
                Equal(f.EntryIds[1], after.GetProperty("currentId").GetString());
                Equal(f.EntryIds[1], after.GetProperty("current").GetProperty("id").GetString());
                Equal(f.Songs[1]["songId"]!.GetValue<string>(), f.Module.CurrentSongId);
                Equal(f.Module.Id, f.Playback.OwnerId);
                Equal(f.EntryIds[1], f.Playback.Identity);
                Equal(f.EntryIds[1], f.Playback.Opens.Single().Entry);
                Equal("responsiveness-1.wav", Path.GetFileName(f.Playback.Opens.Single().Uri.LocalPath));
                True(f.Playback.HasMedia && !f.Module.Busy, "the selected next entry has opened and the command has settled");
                Equal("", f.Module.Status);
            }
            finally { hold.Release(); await observation; await step; }
        });
    }

    private sealed class PlaybackResponsivenessFixture : IAsyncDisposable
    {
        internal readonly BlockingLibraryDocumentStore Store = new();
        internal readonly CacheDocumentStore Cache = new();
        internal readonly NativeMusicScriptTestRuntime Runtime = new();
        internal readonly PlaybackProbe Playback = new();
        internal readonly MusicNativeApplication Application;
        internal readonly MusicCapability Module;
        internal JsonObject[] Songs = [];
        internal string[] EntryIds = [];
        private PlaybackResponsivenessFixture()
        {
            Application = new MusicNativeApplication(Store, Runtime, Cache);
            Module = new MusicCapability(Application, Store, Playback, cacheStore: Cache);
        }
        internal static async Task<PlaybackResponsivenessFixture> Create()
        {
            var f = new PlaybackResponsivenessFixture();
            try
            {
                string root = NewRoot();
                string[] paths = Enumerable.Range(0, 2).Select(index =>
                {
                    string path = Path.Combine(root, $"responsiveness-{index}.wav");
                    File.WriteAllBytes(path, [0, 1, 2, 3]); return path;
                }).ToArray();
                var files = await new GrantedMediaFiles(f.Store, "music.files.v2", [".wav"]).GrantAsync(paths);
                f.Songs = files.Select(file => MusicSongFixture.Local(file.Id, file.Title, duration: 180)).ToArray();
                await f.Rpc("music.queue.replace", new { songs = f.Songs, index = 0 });
                await f.Module.InitializeAsync(default);
                var queue = await f.Rpc("music.queue.get", new { offset = 0, limit = 100 });
                f.EntryIds = queue.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("id").GetString()!).ToArray();
                f.Songs = queue.GetProperty("entries").EnumerateArray()
                    .Select(entry => JsonNode.Parse(entry.GetProperty("song").GetRawText())!.AsObject()).ToArray();
                Equal(2, f.EntryIds.Length);
                Equal(f.EntryIds[0], queue.GetProperty("currentId").GetString());
                True(f.Playback.Opens.Count == 0 && !f.Runtime.HasStarted, "fixture warms native state without opening media or starting scripts");
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal async Task<JsonElement> Rpc(string method, object parameters)
        {
            var result = await Application.ExecuteAsync(method, parameters);
            True(result.GetProperty("ok").GetBoolean(), "native responsiveness fixture: " + method + ": " + result.GetRawText());
            return result;
        }
        public async ValueTask DisposeAsync()
        {
            try { await Module.DisposeAsync(); }
            finally
            {
                try { await Cache.DisposeAsync(); }
                finally { try { await Runtime.DisposeAsync(); } finally { await Store.DisposeAsync(); } }
            }
        }
    }

    private sealed class BlockingLibraryDocumentStore : IDocumentStore
    {
        private readonly DocumentProbe inner = new();
        private SynchronousLibraryWriteHold? nextWrite;
        private int libraryWrites;
        internal int LibraryWrites => Volatile.Read(ref libraryWrites);
        internal SynchronousLibraryWriteHold HoldNextLibraryWrite()
        {
            var hold = new SynchronousLibraryWriteHold();
            True(Interlocked.CompareExchange(ref nextWrite, hold, null) is null, "only one library write is held");
            return hold;
        }
        public Task<DocumentSnapshot> ReadAsync(string documentNamespace, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(documentNamespace, cancellationToken);
        public Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> data,
            DocumentFingerprint expected, CancellationToken cancellationToken = default)
        {
            if (documentNamespace == MusicLibraryOwner.DocumentNamespace)
            {
                Interlocked.Increment(ref libraryWrites);
                Interlocked.Exchange(ref nextWrite, null)?.Wait();
            }
            return inner.WriteAsync(documentNamespace, data, expected, cancellationToken);
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class SynchronousLibraryWriteHold : IDisposable
    {
        private readonly ManualResetEventSlim entered = new(), release = new();
        private int released;
        internal int WriterThreadId { get; private set; }
        private bool Released => Volatile.Read(ref released) != 0;
        internal void Wait()
        {
            WriterThreadId = Environment.CurrentManagedThreadId;
            entered.Set(); release.Wait();
        }
        internal Task<bool> ObserveDispatcher(Dispatcher dispatcher) => Task.Factory.StartNew(() =>
        {
            try
            {
                if (!entered.Wait(TimeSpan.FromSeconds(10))) return false;
                // The deadline is only a hang guard. Success requires dispatch before
                // release, not completing a song operation within a performance budget.
                return dispatcher.InvokeAsync(() => !Released, DispatcherPriority.Send).Task
                    .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch (TimeoutException) { return false; }
            finally { Release(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        internal void Release()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) release.Set();
        }
        public void Dispose() { Release(); entered.Dispose(); release.Dispose(); }
    }
}
