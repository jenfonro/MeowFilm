using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Desktop.Artwork;
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
        yield return Case("playback artwork acquires synchronous leases off the dispatcher and reuses the hot cache", async () =>
        {
            await using var f = await PlaybackResponsivenessFixture.Create(online: true);
            var dispatcher = Dispatcher.CurrentDispatcher;
            int calls = 0, acquireThread = 0;
            var lease = new ResponsivenessArtworkLease(FrozenResponsivenessArtwork());
            var inner = new CallbackArtworkLoader((address, token) =>
            {
                Equal(f.Module.CurrentArtwork, address);
                token.ThrowIfCancellationRequested();
                acquireThread = Environment.CurrentManagedThreadId;
                Interlocked.Increment(ref calls);
                return Task.FromResult<IArtworkLease>(lease);
            });
            var adapter = PlaybackArtworkAdapter(f.Module, inner);
            True(dispatcher.CheckAccess(), "the presentation calls its artwork adapter on the dispatcher");
            using (var first = await adapter.AcquireAsync(f.Module.CurrentArtwork))
            {
                Equal(1, calls);
                True(acquireThread != Environment.CurrentManagedThreadId, "even a synchronously returned inner lease is acquired off the UI thread");
                True(ReferenceEquals(lease, first), "the first load retains the actual inner lease");
                AssertResponsivenessArtwork(first.Image);
            }
            Equal(1, lease.DisposeCalls);
            string key = MusicRecordingPresentationCache.Key(f.Songs[0]);
            var saved = await new MusicRecordingPresentationCache(f.Cache).ReadAsync(key, default);
            True(saved?["artwork"] is JsonValue value && value.TryGetValue<string>(out var encoded) &&
                !string.IsNullOrEmpty(encoded), "the frozen image is encoded into the already warm presentation document");
            using (var cached = await adapter.AcquireAsync(f.Module.CurrentArtwork))
            {
                Equal(1, calls);
                True(!ReferenceEquals(lease, cached), "the next acquisition decodes the persistent hot-cache image");
                AssertResponsivenessArtwork(cached.Image);
            }
            Equal(1, lease.DisposeCalls);
        });
        yield return Case("playback artwork cancellation releases a synchronously returned lease", async () =>
        {
            await using var f = await PlaybackResponsivenessFixture.Create(online: true);
            using var cancellation = new CancellationTokenSource();
            int uiThread = Environment.CurrentManagedThreadId, acquireThread = 0;
            var lease = new ResponsivenessArtworkLease(FrozenResponsivenessArtwork());
            var adapter = PlaybackArtworkAdapter(f.Module, new CallbackArtworkLoader((_, _) =>
            {
                acquireThread = Environment.CurrentManagedThreadId;
                cancellation.Cancel();
                return Task.FromResult<IArtworkLease>(lease); // A completed loader can race its caller's cancellation.
            }));
            await Cancelled(adapter.AcquireAsync(f.Module.CurrentArtwork, cancellation.Token));
            True(acquireThread != uiThread, "the synchronously completed loader still runs outside the dispatcher");
            Equal(1, lease.DisposeCalls);
            var saved = await new MusicRecordingPresentationCache(f.Cache)
                .ReadAsync(MusicRecordingPresentationCache.Key(f.Songs[0]), default);
            True(saved?["artwork"] is null, "a cancelled acquisition does not publish artwork");
        });
    }

    private static IArtworkLoader PlaybackArtworkAdapter(MusicCapability module, IArtworkLoader inner)
    {
        var record = new MusicRecordView();
        ArtworkScope.SetLoader(record, inner);
        module.AttachPlaybackArtwork(record);
        record.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        try
        {
            var adapter = ArtworkScope.GetLoader(record)!;
            True(!ReferenceEquals(inner, adapter), "the record uses the real playback artwork cache adapter");
            return adapter;
        }
        finally { record.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); }
    }

    private static BitmapSource FrozenResponsivenessArtwork()
    {
        byte[] pixels = Enumerable.Repeat(new byte[] { 31, 73, 149, 255 }, 4).SelectMany(pixel => pixel).ToArray();
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        image.Freeze(); return image;
    }

    private static void AssertResponsivenessArtwork(BitmapSource image)
    {
        True(image.IsFrozen && image.PixelWidth > 0 && image.PixelHeight > 0, "artwork is frozen and usable on the UI thread");
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        True(pixel.SequenceEqual(new byte[] { 31, 73, 149, 255 }), "the cached PNG preserves the actual cover pixel");
    }

    private sealed class ResponsivenessArtworkLease(BitmapSource image) : IArtworkLease
    {
        private int disposeCalls;
        public BitmapSource Image => image;
        internal int DisposeCalls => Volatile.Read(ref disposeCalls);
        public void Dispose() => Interlocked.Increment(ref disposeCalls);
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
            Application = new MusicNativeApplication(Store, Runtime, Cache, new NoUpstream());
            Module = new MusicCapability(Application, Store, Playback, cacheStore: Cache);
        }
        internal static async Task<PlaybackResponsivenessFixture> Create(bool online = false)
        {
            var f = new PlaybackResponsivenessFixture();
            try
            {
                if (online)
                    f.Songs =
                    [
                        MusicSongFixture.Online("wy", "responsiveness-first", cover: "https://art.example.test/first.png"),
                        MusicSongFixture.Online("wy", "responsiveness-next", cover: "https://art.example.test/next.png")
                    ];
                else
                {
                    string root = NewRoot();
                    string[] paths = Enumerable.Range(0, 2).Select(index =>
                    {
                        string path = Path.Combine(root, $"responsiveness-{index}.wav");
                        File.WriteAllBytes(path, [0, 1, 2, 3]); return path;
                    }).ToArray();
                    var files = await new GrantedMediaFiles(f.Store, "music.files.v1", [".wav"]).GrantAsync(paths);
                    f.Songs = files.Select(file => MusicSongFixture.Local(file.Id, file.Title, duration: 180)).ToArray();
                }
                f.Cache.Save(MusicRecordingPresentationCache.Namespace,
                    JsonSerializer.SerializeToUtf8Bytes(new { version = 1, entries = new { } }));
                await f.Rpc("music.queue.replace", new { songs = f.Songs, index = 0 });
                await f.Module.InitializeAsync(default);
                var queue = await f.Rpc("music.queue.get", new { offset = 0, limit = 100 });
                f.EntryIds = queue.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("id").GetString()!).ToArray();
                Equal(2, f.EntryIds.Length);
                Equal(f.EntryIds[0], queue.GetProperty("currentId").GetString());
                True(f.Playback.Opens.Count == 0 && !f.Runtime.HasStarted, "fixture warms native state without opening media or starting scripts");
                if (online) Equal("https://art.example.test/first.png", f.Module.CurrentArtwork);
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
