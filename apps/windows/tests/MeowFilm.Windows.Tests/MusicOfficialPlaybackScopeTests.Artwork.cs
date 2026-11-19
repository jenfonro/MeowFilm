using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Platform.Storage;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Window = MeowFilm.Desktop.DesktopWindow;
using WindowState = MeowFilm.Desktop.DesktopWindowState;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Windows.Tests;

public static partial class MusicOfficialPlaybackScopeTests
{
    private static IEnumerable<TestCase> ArtworkCases()
    {
        yield return Case("shared music images reuse original bytes across a genuinely cold loader and keep query variants distinct", SharedArtworkCold);
        yield return Case("shared music image corruption and disk write failure are recoverable misses", SharedArtworkFailures);
        yield return Case("shared music image subscribers cancel independently and close waits for all cancelled physical work", SharedArtworkCancellation);
        yield return Case("same-key image demand waits for cancelled or cleared physical work before sharing one fresh read", SharedArtworkRetiredDemand);
        yield return Case("shared music image clearing fences late writes without deleting healthy lyrics or displayed leases", SharedArtworkClear);
        yield return Case("same-song delayed artwork replacement retains pixels while switching songs clears them", SharedArtworkReplacement);
        yield return Case("cached catalog songs and previews restore current repository metadata without requests or queue changes", CachedCatalogMetadata);
        yield return Case("real catalog song template retains successful pixels across repository updates and clears recycled songs", CatalogArtworkReplacement);
        yield return Case("actual catalog playlist search queue and recommendation templates bind permanent artwork identities", ArtworkConsumerBindings);
        yield return Case("shared music image and lyric bytes obey the existing total disk budget while displayed leases survive eviction", SharedArtworkBudget);
        yield return Case("shared music resources expire after thirty days and only actual playback renews them", SharedArtworkLifetime);
        yield return Case("canonical chorus aliases remain consumable for the current real playback", async () =>
        {
            await using var f = await ArtworkScene.Create();
            f.ChorusResult = Element(new { platform = "canonical-platform", id = "canonical-alias",
                point = new { start = .2, end = .8 } });
            await f.Play(ArtworkSong("wy", "chorus-input", ""));
            await Until(() => f.Module.HasChorus);
            Check(f.Module.ChorusPositionSeconds == .2 && f.Output.NetworkOpens == 1);
            var request = f.Runtime.Requests.Single(call => call.Operation == "chorus");
            Check(request.Platform == "wy" && request.Parameters.GetProperty("id").GetString() == "chorus-input",
                "the actual request locator is retained while ordinary result aliases do not veto the point");
        });
        yield return Case("both players reuse listed artwork through discovery source and quality changes", async () =>
        {
            await using var f = await ArtworkScene.Create();
            const string original = "https://art.example.test/kg.png", discovered = "https://art.example.test/tx.png";
            var http = new ArtworkHttpProbe();
            await using var images = new ArtworkService(http, byteCache: f.App.PresentationCache,
                loadCompleted: f.App.ArtworkLoadCompletedAsync);
            using var listed = await images.AcquireAsync(original);
            using var players = new ArtworkPlayers(f.Module, images);
            f.DiscoveryArtwork = discovered;
            f.Failures["kg"] = "NETWORK_ERROR";
            f.HoldDiscovery = true;
            var playing = f.Play(ArtworkSong("kg", "first", original));
            try
            {
                await f.DiscoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await players.Loaded(original);
                Check(players.Covers.All(cover => ReferenceEquals(cover.Source, listed.Image)),
                    "the already decoded song image is reused before platform discovery finishes");
                var changes = new List<string>();
                f.Module.PropertyChanged += (_, e) =>
                { if (e.PropertyName == nameof(MusicCapability.CurrentArtwork)) changes.Add(f.Module.CurrentArtwork); };
                f.DiscoveryRelease.TrySetResult();
                await playing;
                Check(f.Module.CurrentSource == "QQ 音乐" && f.Module.CurrentArtwork == original,
                    "the newly found higher-priority metadata provider does not replace the existing cover");
                await f.SetQuality("320k");
                await f.Replay();
                Check(f.Module.CurrentQualityLabel == "高音质", "the test really changes the accepted audio quality");
                await players.Loaded(original);
                Check(changes.All(address => address == original) &&
                    players.Covers.All(cover => ReferenceEquals(cover.Source, listed.Image)),
                    "source and quality publication never clear or replace the image");
                Check(http.Addresses.SequenceEqual([original]), "neither player fetches another platform's artwork");
            }
            finally { f.DiscoveryRelease.TrySetResult(); await playing; }
        });
        yield return Case("missing artwork accepts discovered metadata while explicit preferences still apply", async () =>
        {
            await using var f = await ArtworkScene.Create();
            const string discovered = "https://art.example.test/discovered.png";
            f.DiscoveryArtwork = discovered;
            f.Failures["wy"] = "NETWORK_ERROR";
            await f.Play(ArtworkSong("wy", "missing", ""));
            Check(f.Module.CurrentArtwork == discovered, "an accepted alternate recording fills missing artwork");
            await f.Module.SetMetadataEnabledAsync(f.Module.MetadataPlatforms.Single(row => row.Platform == "tx"), false);
            Check(f.Module.CurrentArtwork == "", "disabled provider metadata cannot reenter from an accepted playback snapshot");
            await f.Module.SetMetadataEnabledAsync(f.Module.MetadataPlatforms.Single(row => row.Platform == "tx"), true);
            Check(f.Module.CurrentArtwork == discovered);
        });
        yield return Case("initial displayed cover survives priority differences until the user changes preferences", async () =>
        {
            await using var f = await ArtworkScene.Create();
            const string original = "https://art.example.test/primary.png", preferred = "https://art.example.test/preferred.png";
            var song = ArtworkSong("tx", "primary", original);
            song["sources"]!.AsArray().Add(ArtworkSong("wy", "alternate", preferred, "Track primary")["sources"]![0]!.DeepClone());
            await f.Play(song);
            Check(f.Module.CurrentArtwork == original, "starting a displayed song reuses its allowed primary cover");
            await f.Module.MoveMetadataPlatformAsync(f.Module.MetadataPlatforms.Single(row => row.Platform == "mg"),
                f.Module.MetadataPlatforms.Single(row => row.Platform == "kg"));
            Check(f.Module.CurrentArtwork == preferred, "an explicit preference change reapplies saved provider order");
            await f.Module.SetMetadataEnabledAsync(f.Module.MetadataPlatforms.Single(row => row.Platform == "wy"), false);
            Check(f.Module.CurrentArtwork == original, "disabling the selected provider falls back to enabled metadata");
        });
        yield return Case("artwork failures try distinct URLs and both players retain an explicit successful retry", async () =>
        {
            await using var f = await ArtworkScene.Create();
            const string original = "https://art.example.test/failed.png", fallback = "https://art.example.test/fallback.png";
            var song = ArtworkSong("wy", "first", original);
            song["sources"]!.AsArray().Add(ArtworkSong("tx", "duplicate", original, "Track first")["sources"]![0]!.DeepClone());
            song["sources"]!.AsArray().Add(ArtworkSong("kg", "fallback", fallback, "Track first")["sources"]![0]!.DeepClone());
            await f.Play(song);
            var http = new ArtworkHttpProbe();
            http.Failures.UnionWith([original, fallback]);
            var originalRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fallbackRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retryRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            http.Gates[original] = originalRelease; http.Gates[fallback] = fallbackRelease;
            await using var images = new ArtworkService(http, byteCache: f.App.PresentationCache,
                loadCompleted: f.App.ArtworkLoadCompletedAsync);
            using var players = new ArtworkPlayers(f.Module, images);
            try
            {
                await http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await DesktopTestUi.DrainAsync();
                originalRelease.TrySetResult();
                await Until(() => http.Addresses.Contains(fallback));
                await DesktopTestUi.DrainAsync();
                fallbackRelease.TrySetResult();
                await Until(() => players.Covers.All(cover => DesktopVisual.IsVisible(cover.Children.OfType<Button>().Single())));
                Check(http.Addresses.SequenceEqual([original, fallback]) && f.Module.CurrentArtwork == fallback,
                    "exhausted candidates keep the failed image's retry action without looping");
                http.Failures.Clear(); http.Gates[fallback] = retryRelease;
                foreach (var cover in players.Covers)
                    DesktopTestUi.Invoke(cover.Children.OfType<Button>().Single());
                await Until(() => http.Addresses.Count == 3);
                await DesktopTestUi.DrainAsync();
                retryRelease.TrySetResult();
                await players.Loaded(fallback);
                await f.SetQuality("320k");
                await f.Replay();
                Check(f.Module.CurrentArtwork == fallback && http.Addresses.SequenceEqual([original, fallback, fallback]),
                    "successfully retried artwork remains selected after audio changes");
            }
            finally { originalRelease.TrySetResult(); fallbackRelease.TrySetResult(); retryRelease.TrySetResult(); }
        });
        foreach (bool success in new[] { false, true })
            yield return Case("changing songs rejects a late artwork " + (success ? "success" : "failure"), async () =>
            {
                await using var f = await ArtworkScene.Create();
                const string first = "https://art.example.test/first.png", next = "https://art.example.test/next.png";
                await f.Play(ArtworkSong("wy", "first", first), ArtworkSong("wy", "next", next));
                var http = new ArtworkHttpProbe { Hold = true, HeldAddress = first };
                if (!success) http.Failures.Add(first);
                await using var images = new ArtworkService(http, byteCache: f.App.PresentationCache,
                    loadCompleted: f.App.ArtworkLoadCompletedAsync);
                using var players = new ArtworkPlayers(f.Module, images);
                try
                {
                    await http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await f.Module.StepAsync(true);
                    await players.Loaded(next);
                    Check(http.Cancelled, "all old-song subscribers detach from their shared physical request");
                    var current = players.Covers.Select(cover => cover.Source).ToArray();
                    http.Release.TrySetResult();
                    await DesktopTestUi.DrainAsync();
                    Check(f.Module.CurrentArtwork == next && players.Covers.Select(cover => cover.Source).SequenceEqual(current),
                        "old results cannot replace or fail the next song's cover");
                    http.Failures.Clear(); http.Hold = false;
                    await f.Module.StepAsync(false);
                    await players.Loaded(first);
                    Check(http.Addresses.SequenceEqual([first, next, first]), "a new demand may retry only its own image");
                }
                finally { http.Release.TrySetResult(); }
            });
    }

    private static JsonObject ArtworkSong(string platform, string id, string cover, string? title = null) =>
        MusicSongFixture.Online(platform, id, title ?? "Track " + id, "Artist", duration: 1, cover: cover);

    private sealed class ArtworkScene : IAsyncDisposable
    {
        internal readonly MusicOfficialAudioTests.MemoryStore Store = new();
        internal readonly MusicPlatformRuntimeFixture Runtime = new();
        internal readonly NativeTestDatabase Cache;
        internal readonly MusicNativeApplication App;
        internal readonly MusicCapability Module;
        internal readonly Output Output = new();
        internal readonly Dictionary<string, string> Failures = [];
        internal readonly TaskCompletionSource DiscoveryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DiscoveryRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string DiscoveryArtwork = "", Title = "", Artist = "";
        internal JsonElement? ChorusResult;
        internal bool HoldDiscovery;
        internal ArtworkScene(string? presentationCacheRoot = null)
        {
            Cache = new(Store);
            Runtime.OnRequest = Read;
            Runtime.OnOtherCall = (call, _) => call.Method == "music.script.sources.list"
                ? Task.FromResult(MusicPlatformRuntimeFixture.Json(new { ok = true, contract = "music.script.v3",
                    value = new { items = Array.Empty<object>() } }))
                : throw new InvalidOperationException("unexpected script operation: " + call.Method);
            App = new(Store, MusicTestStores.For(Store), Runtime);
            Module = new(App, Store, Output, new Dialogs());
        }
        internal static async Task<ArtworkScene> Create(string? presentationCacheRoot = null)
        {
            var result = new ArtworkScene(presentationCacheRoot);
            await result.Initialize();
            return result;
        }
        internal async Task Initialize()
        {
            await Module.InitializeAsync(default);
            await Ok("music.accounts.import",
                new { platform = "tx", cookie = "musicid=123; musickey=synthetic; encrypt_uin=FixtureEUIN" });
            await Ok("music.settings.save", new { ifRevision = 0, lyrics = false });
            await SetQuality("128k");
            await Until(() => !Module.Busy);
        }
        private async Task<JsonElement> Read(MusicPlatformRuntimeFixture.Call call, CancellationToken token)
        {
            switch (call.Operation)
            {
                case "account.verify":
                    var verified = JsonNode.Parse(MusicPlatformRuntimeFixture.Observation(call.Platform, "123").GetRawText())!.AsObject();
                    verified["cookie"] = call.Payload.GetProperty("candidate").GetProperty("cookie").GetString();
                    return MusicPlatformRuntimeFixture.Json(verified);
                case "account.profile":
                    return MusicPlatformRuntimeFixture.Observation(call.Platform, "123");
                case "audio":
                    if (Failures.TryGetValue(call.Platform, out var failure)) throw new MusicPlatformException(failure);
                    string quality = call.Parameters.GetProperty("qualities")[0].GetString()!;
                    return Element(new { url = "https://audio.example.test/" + call.Platform + ".mp3",
                        requestQuality = quality, actualQuality = quality, qualityVerified = true,
                        duration = 1d, size = 4096L, full = true,
                        expiresAt = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds() });
                case "search":
                    if (call.Platform == "tx" && HoldDiscovery)
                    {
                        DiscoveryEntered.TrySetResult();
                        await DiscoveryRelease.Task.WaitAsync(token);
                    }
                    return Element(new { platform = call.Platform, kind = "songs",
                        items = call.Platform == "tx" && DiscoveryArtwork.Length > 0
                            ? new[] { MusicSongFixture.Online("tx", "discovered", Title, Artist, duration: 1, cover: DiscoveryArtwork) }
                            : Array.Empty<JsonObject>(), more = false, continuation = (object?)null });
                case "chorus": return ChorusResult ?? Element(new { platform = call.Platform,
                    id = call.Parameters.GetProperty("id").GetString(), point = (object?)null });
                default: throw new InvalidOperationException("unexpected platform operation: " + call.Operation);
            }
        }
        internal async Task<JsonElement> Ok(string method, object? input = null)
        {
            var result = await App.ExecuteAsync(method, input);
            Check(result.GetProperty("ok").GetBoolean(), method + ": " + result.GetRawText());
            return result;
        }
        internal async Task SetQuality(string request)
        {
            var current = (await Ok("music.quality.settings.get")).GetProperty("settings");
            var policy = JsonNode.Parse(current.GetRawText())!.AsObject();
            policy["ifRevision"] = policy["revision"]!.DeepClone();
            policy.Remove("schema"); policy.Remove("revision");
            policy["request"] = request;
            await Ok("music.quality.settings.save", policy);
        }
        internal async Task Play(params JsonObject[] songs)
        {
            Title = LJ.S(songs[0]["title"]); Artist = LJ.S(songs[0]["artist"]);
            await Ok("music.queue.replace", new { songs, index = 0 });
            await Replay();
        }
        internal async Task Replay()
        {
            await Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            await Module.PlayQueueSurfaceItemAsync(Module.QueueSurfaceRows.First());
            Check(Output.HasMedia, Module.Status);
        }
        public async ValueTask DisposeAsync()
        {
            DiscoveryRelease.TrySetResult();
            try { await Module.DisposeAsync(); }
            finally { await Runtime.DisposeAsync(); await Cache.DisposeAsync(); await Store.DisposeAsync(); }
        }
    }

    private sealed class ArtworkPlayers : IDisposable
    {
        private readonly Window window;
        internal ArtworkImage[] Covers { get; }
        internal ArtworkPlayers(MusicCapability module, IArtworkLoader images)
        {
            var mini = (MusicPlayerView)module.PlayerView;
            var detail = (MusicPlayerDetailView)module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            var layout = new Grid();
            layout.RowDefinitions.Add(new() { Height = new GridLength(100) });
            layout.RowDefinitions.Add(new());
            layout.Children.Add(mini); layout.Children.Add(detail); Grid.SetRow(detail, 1);
            window = DesktopTestUi.CreateWindow(layout, 1100, 900);
            ArtworkScope.SetLoader(DesktopTestUi.WindowRoot(window), images);
            var miniRecord = (MusicRecordView)((Button)mini.FindName("ArtworkButton")).Content;
            var detailRecord = (MusicRecordView)detail.FindName("DetailRecord");
            ArtworkScope.SetLoader(miniRecord, images);
            ArtworkScope.SetLoader(detailRecord, images);
            Covers = [(ArtworkImage)miniRecord.FindName("Cover"), (ArtworkImage)detailRecord.FindName("Cover")];
            window.Show(); DesktopTestUi.WindowRoot(window).UpdateLayout();
        }
        internal Task Loaded(string address) =>
            Until(() => Covers.All(cover => cover.Address == address && cover.Source is not null));
        public void Dispose() { window.Content = null; window.Close(); }
    }

    private sealed class ControlledArtwork : IArtworkLoader, IDisposable
    {
        internal sealed record Call(string Address, CancellationToken Token, TaskCompletionSource<IArtworkLease> Completion);
        internal readonly List<Call> Calls = [];
        public Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<IArtworkLease>(TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Add(new(address, cancellationToken, completion));
            return completion.Task;
        }
        public void Dispose() { foreach (var call in Calls) call.Completion.TrySetCanceled(); }
    }
    private sealed class ArtworkTestLease : IArtworkLease
    {
        public MeowFilm.Desktop.Artwork.ArtworkPixels Image { get; } = new(1, 1, new byte[] { 60, 120, 180, 255 });
        internal bool Disposed;
        public void Dispose() => Disposed = true;
    }
    private static System.Windows.Media.Imaging.BitmapSource ArtworkPixels(int size = 1)
    {
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        { pixels[i] = 60; pixels[i + 1] = 120; pixels[i + 2] = 180; pixels[i + 3] = 255; }
        var image = System.Windows.Media.Imaging.BitmapSource.Create(size, size, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, size * 4);
        image.Freeze(); return image;
    }
    private sealed class ArtworkHttpProbe(int size = 1) : HttpMessageHandler
    {
        internal readonly List<string> Addresses = [];
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Hold, Cancelled;
        internal string? HeldAddress;
        internal readonly HashSet<string> Failures = new(StringComparer.Ordinal);
        internal readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource> Gates = new(StringComparer.Ordinal);
        private readonly byte[] bytes = Encode(size);
        internal static byte[] Encode(int size = 1)
        {
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(ArtworkPixels(size)));
            using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Addresses) Addresses.Add(request.RequestUri!.AbsoluteUri);
            using var registration = cancellationToken.Register(() => Cancelled = true);
            Entered.TrySetResult();
            if (Gates.TryGetValue(request.RequestUri!.AbsoluteUri, out var gated)) await gated.Task;
            if (Hold && (HeldAddress is null || HeldAddress == request.RequestUri!.AbsoluteUri))
                await Release.Task; // Physical response deliberately ignores cancellation until released.
            if (Failures.Contains(request.RequestUri!.AbsoluteUri)) throw new HttpRequestException("synthetic image failure");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }

    private static async Task SharedArtworkCold()
    {
        const string original = "https://images.example.test/shared.png?size=128&tokenless=1";
        const string alias = "HTTPS://IMAGES.EXAMPLE.TEST:443/shared.png?size=128&tokenless=1";
        const string variant = "https://images.example.test/shared.png?size=256&tokenless=1";
        await using var backing = new MusicOfficialAudioTests.MemoryStore();
        await using (var documents = new NativeTestDatabase(backing))
        await using (var loader = new ArtworkService(new ArtworkHttpProbe(), byteCache: new MusicPresentationCache(documents)))
        {
            using var first = await loader.AcquireAsync(original);
            using var second = await loader.AcquireAsync(alias);
            Check(ReferenceEquals(first.Image, second.Image), "memory decoded pixels are shared independently of song or display size");
        }
        await using var coldDocuments = new NativeTestDatabase(backing);
        var http = new ArtworkHttpProbe();
        await using var cold = new ArtworkService(http, byteCache: new MusicPresentationCache(coldDocuments));
        using var restored = await cold.AcquireAsync(alias);
        Check(http.Addresses.Count == 0 && restored.Image.PixelWidth == 1, "cold loader must decode disk before issuing image HTTP");
        using var otherSize = await cold.AcquireAsync(variant);
        Check(http.Addresses.SequenceEqual([variant]), "real URL query variants remain separate resources");
        var entries = await coldDocuments.ReadPresentationEntriesAsync();
        Check(entries.Count == 2 && entries.All(pair => pair.Value!["kind"]!.GetValue<string>() == "image"));
    }

    private static async Task SharedArtworkFailures()
    {
        const string address = "https://images.example.test/corrupt.png";
        const string healthy = "https://images.example.test/healthy.png";
        string songId = Guid.NewGuid().ToString("D");
        await using var backing = new MusicOfficialAudioTests.MemoryStore();
        await using var documents = new NativeTestDatabase(backing);
        var cache = new MusicPresentationCache(documents);
        await cache.WriteAsync(address, new byte[] { 1, 2, 3 }, cache.Epoch, default);
        await cache.WriteAsync(healthy, ArtworkHttpProbe.Encode(), cache.Epoch, default);
        await cache.SaveLyricsAsync(songId, "source", new JsonArray(new JsonObject { ["time"] = 0, ["text"] = "healthy" }), cache.LyricEpoch, default);
        var http = new ArtworkHttpProbe();
        await using (var loader = new ArtworkService(http, byteCache: cache))
        {
            using var repaired = await loader.AcquireAsync(address);
            Check(http.Addresses.SequenceEqual([address]) && repaired.Image.PixelWidth == 1,
                "a corrupt disk resource misses and repairs only its own image");
            using var intact = await loader.AcquireAsync(healthy);
            Check(http.Addresses.SequenceEqual([address]) && intact.Image.PixelWidth == 1);
            Check((await cache.ReadLyricsAsync(songId, default))!["lines"]![0]!["text"]!.GetValue<string>() == "healthy");
        }
        Check((await cache.ReadAsync(address, default))!.Content.SequenceEqual(ArtworkHttpProbe.Encode()));
        await using var failedStore = new NativeTestDatabase(backing);
        failedStore.BeforeWrite = _ => throw new DocumentStoreException(DocumentStoreError.Storage);
        await using var failedDisk = new MusicPresentationCache(failedStore);
        var fallbackHttp = new ArtworkHttpProbe();
        await using var fallback = new ArtworkService(fallbackHttp, byteCache: failedDisk);
        using var decoded = await fallback.AcquireAsync(address + "?new=1");
        Check(decoded.Image.PixelWidth == 1 && await failedDisk.ReadAsync(address + "?new=1", default) is null,
            "failed persistence cannot turn successful decoding into a failed image");
        using var reused = await fallback.AcquireAsync(address + "?new=1");
        Check(ReferenceEquals(decoded.Image, reused.Image) && fallbackHttp.Addresses.Count == 1 &&
            (await cache.ReadAsync(healthy, default))!.Content.SequenceEqual(ArtworkHttpProbe.Encode()));
    }

    private static async Task SharedArtworkCancellation()
    {
        const string address = "https://images.example.test/in-flight.png";
        var http = new ArtworkHttpProbe { Hold = true };
        var loader = new ArtworkService(http);
        using var departed = new CancellationTokenSource();
        var first = loader.AcquireAsync(address, departed.Token);
        var second = loader.AcquireAsync(address);
        try
        {
            await http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            departed.Cancel();
            try { using var unexpected = await first; throw new InvalidOperationException("cancelled image subscriber succeeded"); }
            catch (OperationCanceledException) { }
            Check(!http.Cancelled && http.Addresses.Count == 1, "one cancelled page cannot stop another page's image");
            http.Release.TrySetResult();
            using (var lease = await second) Check(lease.Image.PixelWidth == 1);
        }
        finally { http.Release.TrySetResult(); await loader.DisposeAsync(); }

        var allHttp = new ArtworkHttpProbe { Hold = true };
        var all = new ArtworkService(allHttp);
        using var cancelled = new CancellationTokenSource();
        var pending = all.AcquireAsync(address, cancelled.Token);
        try
        {
            await allHttp.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel();
            try { using var unexpected = await pending; throw new InvalidOperationException("cancelled image subscriber succeeded"); }
            catch (OperationCanceledException) { }
            await Until(() => allHttp.Cancelled);
            var close = all.DisposeAsync().AsTask();
            Check(!close.IsCompleted, "closing retains the last cancelled subscriber's physical HTTP cleanup");
            allHttp.Release.TrySetResult();
            await close.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { allHttp.Release.TrySetResult(); await all.DisposeAsync(); }
    }

    private static async Task SharedArtworkRetiredDemand()
    {
        const string address = "https://images.example.test/retired.png";
        foreach (bool clear in new[] { false, true })
        {
            await using var backing = new MusicOfficialAudioTests.MemoryStore();
            await using var documents = new NativeTestDatabase(backing);
            var cache = new MusicPresentationCache(documents);
            var http = new ArtworkHttpProbe { Hold = true };
            await using var loader = new ArtworkService(http, byteCache: cache);
            using var departed = new CancellationTokenSource();
            var first = loader.AcquireAsync(address, departed.Token);
            Task<IArtworkLease>? successor = null, anotherPage = null;
            try
            {
                await http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (clear)
                {
                    loader.ClearMemory();
                    await cache.ClearImagesAsync(default);
                }
                else
                {
                    departed.Cancel();
                    try { using var unexpected = await first; throw new InvalidOperationException("cancelled image subscriber succeeded"); }
                    catch (OperationCanceledException) { }
                    await Until(() => http.Cancelled);
                }
                successor = loader.AcquireAsync(address);
                anotherPage = loader.AcquireAsync(address);
                await DesktopTestUi.DrainAsync();
                Check(http.Addresses.Count == 1 && !successor.IsCompleted && !anotherPage.IsCompleted,
                    "new same-key demand cannot overlap retired non-cooperative HTTP");
                http.Hold = false;
                http.Release.TrySetResult();
                if (clear)
                {
                    using var oldVisible = await first;
                    Check(oldVisible.Image.PixelWidth == 1, "clearing does not revoke the old subscriber's pixels");
                }
                using var fresh = await successor;
                using var shared = await anotherPage;
                Check(http.Addresses.Count == 2 && ReferenceEquals(fresh.Image, shared.Image),
                    "successor pages share exactly one new read after old physical completion");
                Check(await cache.ReadAsync(address, default) is not null,
                    "only the new current-epoch demand persists the resource");
            }
            finally
            {
                http.Hold = false; http.Release.TrySetResult();
                foreach (var work in new[] { first, successor, anotherPage })
                {
                    if (work is null) continue;
                    try { using var lease = await work; }
                    catch (OperationCanceledException) { }
                }
            }
        }
    }

    private static async Task SharedArtworkClear()
    {
        const string address = "https://images.example.test/late.png";
        await using var backing = new MusicOfficialAudioTests.MemoryStore();
        await using var documents = new NativeTestDatabase(backing);
        var cache = new MusicPresentationCache(documents);
        string songId = Guid.NewGuid().ToString("D");
        await cache.SaveLyricsAsync(songId, "source", new JsonArray(new JsonObject { ["time"] = 0, ["text"] = "healthy" }), cache.LyricEpoch, default);
        var http = new ArtworkHttpProbe { Hold = true };
        await using var loader = new ArtworkService(http, byteCache: cache);
        var pending = loader.AcquireAsync(address);
        try
        {
            await http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            loader.ClearMemory();
            await cache.ClearImagesAsync(default);
            http.Release.TrySetResult();
            using var visible = await pending;
            Check(visible.Image.PixelWidth == 1 && await cache.ReadAsync(address, default) is null,
                "an old image may finish for its subscriber but cannot refill cleared resources");
            Check((await cache.ReadLyricsAsync(songId, default))!["lines"]![0]!["text"]!.GetValue<string>() == "healthy");
            loader.ClearMemory();
            Check(visible.Image.PixelWidth == 1, "cache clear does not revoke an already displayed lease");
            using var fresh = await loader.AcquireAsync(address);
            Check(http.Addresses.Count == 2 && await cache.ReadAsync(address, default) is not null,
                "only a new real demand may persist a cleared resource again");
        }
        finally { http.Release.TrySetResult(); }
    }

    private static async Task SharedArtworkReplacement()
    {
        using var controlled = new ControlledArtwork();
        var image = new ArtworkImage { Address = "https://images.example.test/first.png", DisplayIdentity = "song-a" };
        var window = DesktopTestUi.CreateWindow(image, 200, 200);
        ArtworkScope.SetLoader(DesktopTestUi.WindowRoot(window), controlled);
        try
        {
            window.Show(); DesktopTestUi.WindowRoot(window).UpdateLayout();
            await Until(() => controlled.Calls.Count == 1);
            var initial = new ArtworkTestLease(); controlled.Calls[0].Completion.TrySetResult(initial);
            await Until(() => image.Source is not null);
            var pixels = image.Source;
            image.Address = "https://images.example.test/second.png";
            await Until(() => controlled.Calls.Count == 2);
            Check(ReferenceEquals(image.Source, pixels) && !initial.Disposed, "same-song loading cannot flash a placeholder");
            controlled.Calls[1].Completion.TrySetException(new ArtworkException("ARTWORK_NETWORK"));
            await Until(() => ToolTipService.GetToolTip(image) is not null);
            Check(ReferenceEquals(image.Source, pixels), "failed same-song replacement retains the last successful pixels");
            image.Address = "https://images.example.test/third.png";
            await Until(() => controlled.Calls.Count == 3);
            var replacement = new ArtworkTestLease(); controlled.Calls[2].Completion.TrySetResult(replacement);
            await Until(() => ReferenceEquals(image.Source, replacement.Image));
            Check(initial.Disposed);
            image.DisplayIdentity = "song-b";
            image.Address = "https://images.example.test/different.png";
            await Until(() => controlled.Calls.Count >= 4);
            Check(image.Source is null, "another song cannot display the previous song's pixels");
        }
        finally { window.Content = null; window.Close(); }
    }

    private static async Task CachedCatalogMetadata()
    {
        await using var f = await ArtworkScene.Create();
        var original = MusicSongFixture.Online("wy", "cached", "Original title", "Original artist",
            "Original album", 100, "https://images.example.test/original.png");
        var latest = MusicSongFixture.Online("wy", "cached", "Current title", "Current artist",
            "Current album", 103, "https://images.example.test/current.png");
        var respond = f.Runtime.OnRequest;
        f.Runtime.OnRequest = (call, token) => call.Operation switch
        {
            "channel" => Task.FromResult(Element(new
            {
                platform = "wy", more = false, continuation = (object?)null,
                sections = call.Parameters.GetProperty("id").GetString() == "cache-a" ? new object[]
                {
                    new { id = "songs", title = "Songs", type = "songs", items = new[] { original } },
                    new { id = "rankings", title = "Rankings", type = "rankings", layout = "ranked-songs",
                        items = new[] { new { id = "rank", name = "Rank", kind = "playlist",
                            preview = new[] { new { song = original, rank = 1 } } } } }
                } : Array.Empty<object>()
            })),
            "song" => Task.FromResult(Element(latest)),
            _ => respond(call, token)
        };
        await f.Module.NavigateAsync("catalog-channel/wy/cache-a", default);
        var card = f.Module.CatalogSections.SelectMany(section => section.Items).Single(item => item.Song is not null);
        var ranking = f.Module.CatalogSections.Single(section => section.Id == "rankings").Items.Single();
        string id = card.Song!.Id;
        Check(Guid.TryParse(id, out _));
        await f.Ok("music.queue.replace", new { songs = new[] { card.Song.Raw, card.Song.Raw }, index = 1 });
        var queue = await f.Ok("music.queue.get");
        await f.Module.NavigateAsync("catalog-channel/wy/cache-b", default);
        var current = await f.Ok("music.catalog.song", new { platform = "wy", id = "cached", refresh = true });
        Check(current.GetProperty("song").GetProperty("songId").GetString() == id);
        await DesktopTestUi.DrainAsync();
        int requests = f.Runtime.Requests.Count, writes = f.Store.Writes;
        await f.Module.NavigateAsync("catalog-channel/wy/cache-a", default);
        var restored = f.Module.CatalogSections.SelectMany(section => section.Items).Single(item => item.Song is not null);
        Check(ReferenceEquals(restored, card) && restored.Song!.Id == id &&
            restored.Title == "Current title" && restored.Subtitle == "Current artist" &&
            restored.Song.Album == "Current album" && restored.Song.Duration == "1:43" &&
            restored.Cover == "https://images.example.test/current.png" &&
            ranking.PreviewSongs.Single().Song!.Id == id && ranking.PreviewSongs.Single().Title == restored.Title &&
            ranking.FirstPreviewCover == restored.Cover);
        Check(f.Runtime.Requests.Count == requests && f.Store.Writes == writes,
            "cache restoration only projects permanent references; no adoption or request");
        var after = await f.Ok("music.queue.get");
        Check(after.GetProperty("version").GetInt64() == queue.GetProperty("version").GetInt64() &&
            after.GetProperty("current").GetProperty("id").GetString() == queue.GetProperty("current").GetProperty("id").GetString() &&
            after.GetProperty("entries").EnumerateArray().Select(item => item.GetProperty("id").GetString())
                .SequenceEqual(queue.GetProperty("entries").EnumerateArray().Select(item => item.GetProperty("id").GetString())));
    }

    private static async Task CatalogArtworkReplacement()
    {
        await using var f = await ArtworkScene.Create();
        var source = ArtworkSong("wy", "template", "https://images.example.test/template-1.png");
        var respond = f.Runtime.OnRequest;
        f.Runtime.OnRequest = (call, token) => call.Operation switch
        {
            "channel" => Task.FromResult(Element(new { platform = "wy", items = new[] { source }, more = false })),
            "song" => Task.FromResult(Element(source)),
            _ => respond(call, token)
        };
        await f.Module.NavigateAsync("catalog-channel/wy/template", default);
        var card = f.Module.CatalogSections.Single().Items.Single();
        var view = new MusicCatalogView();
        var content = (FrameworkElement)((DataTemplate)DesktopVisual.Resource(view, "Catalog.SongRow")).LoadContent();
        content.DataContext = card; view.Content = content;
        using var controlled = new ControlledArtwork();
        ArtworkScope.SetLoader(view, controlled);
        var window = DesktopTestUi.CreateWindow(view, 430, 130);
        try
        {
            window.Show(); DesktopTestUi.WindowRoot(window).UpdateLayout();
            await Until(() => controlled.Calls.Count == 1);
            var image = ArtworkDescendants<ArtworkImage>(view).Single();
            Check(image.DisplayIdentity == card.Song!.Id);
            var initial = new ArtworkTestLease(); controlled.Calls[0].Completion.TrySetResult(initial);
            await Until(() => image.Source is not null);
            source = ArtworkSong("wy", "template", "https://images.example.test/template-2.png");
            await f.Ok("music.catalog.song", new { platform = "wy", id = "template", refresh = true });
            await Until(() => controlled.Calls.Count == 2);
            Check(ReferenceEquals(image.Source, initial.Image) && !initial.Disposed);
            controlled.Calls[1].Completion.TrySetException(new ArtworkException("ARTWORK_NETWORK"));
            await Until(() => ToolTipService.GetToolTip(image) is not null);
            Check(ReferenceEquals(image.Source, initial.Image), "real template keeps success on replacement failure");
            source = ArtworkSong("wy", "template", "https://images.example.test/template-3.png");
            await f.Ok("music.catalog.song", new { platform = "wy", id = "template", refresh = true });
            await Until(() => controlled.Calls.Count == 3);
            Check(ReferenceEquals(image.Source, initial.Image));
            var replacement = new ArtworkTestLease(); controlled.Calls[2].Completion.TrySetResult(replacement);
            await Until(() => ReferenceEquals(image.Source, replacement.Image));
            Check(initial.Disposed);
            var other = await MusicSongFixture.AdoptAsync(f.App,
                ArtworkSong("wy", "different-template-song", "https://images.example.test/different-template.png"));
            var otherRow = SongRow.Read(Element(other));
            content.DataContext = new MusicCatalogCard(otherRow.Id, otherRow.Title, otherRow.Artist, otherRow.Cover,
                "song", "wy", otherRow);
            await Until(() => image.DisplayIdentity == otherRow.Id &&
                image.Address == otherRow.Cover && controlled.Calls.Any(call => call.Address == otherRow.Cover));
            Check(image.Source is null && replacement.Disposed, "recycled template cannot show another song's successful pixels");
        }
        finally { window.Content = null; window.Close(); }
    }

    private static async Task ArtworkConsumerBindings()
    {
        await using var f = await ArtworkScene.Create();
        var song = await MusicSongFixture.AdoptAsync(f.App,
            ArtworkSong("wy", "bindings", "https://images.example.test/bindings.png"));
        var row = SongRow.Read(Element(song));
        var card = new MusicCatalogCard(row.Id, row.Title, row.Artist, row.Cover, "song", "wy", row);
        var catalog = new MusicCatalogView();
        var queue = new MusicQueueView(f.Module);
        var page = (MusicView)f.Module.View;
        var subjects = new List<(FrameworkElement View, object Data, string Name)>();
        foreach (string name in new[] { "Catalog.SongRow", "Catalog.PlaylistCard", "Catalog.Banner", "Catalog.Entry" })
            subjects.Add(((FrameworkElement)((DataTemplate)DesktopVisual.Resource(catalog, name)).LoadContent(), card, name));
        foreach (string name in new[] { "SongsGrid", "SearchSongsGrid" })
            subjects.Add(((FrameworkElement)((ListView)page.FindName(name)).ItemTemplate.LoadContent(), row, name));
        subjects.Add(((FrameworkElement)((ListView)queue.FindName("QueueList")).ItemTemplate.LoadContent(),
            new MusicQueueSurfaceRow(row, false), "QueueList"));
        subjects.Add(((FrameworkElement)((ListView)queue.FindName("RecommendationList")).ItemTemplate.LoadContent(),
            new MusicRecommendationCandidate("session", "candidate", row), "RecommendationList"));
        using var controlled = new ControlledArtwork();
        var host = new ContentControl();
        ArtworkScope.SetLoader(host, controlled);
        var window = DesktopTestUi.CreateWindow(host, 500, 380);
        try
        {
            window.Show();
            foreach (var (view, data, name) in subjects)
            {
                view.DataContext = data; host.Content = view; DesktopTestUi.WindowRoot(window).UpdateLayout();
                await DesktopTestUi.DrainAsync();
                var image = ArtworkDescendants<ArtworkImage>(view).Single(value => value.Address == row.Cover);
                Check(image.DisplayIdentity == row.Id, name + " must use the permanent song, not occurrence or platform ID");
            }
            var rank = new MusicCatalogCard("rank", "Rank", "", "https://images.example.test/collection.png",
                "playlist", "wy", Preview:
                [new("", "", "", 1, null, row), new("", "", "", 2, null, row), new("", "", "", 3, null, row)]);
            var ranking = (FrameworkElement)((DataTemplate)DesktopVisual.Resource(catalog, "Catalog.Ranking")).LoadContent();
            ranking.DataContext = rank; host.Content = ranking; DesktopTestUi.WindowRoot(window).UpdateLayout();
            await DesktopTestUi.DrainAsync();
            Check(ArtworkDescendants<ArtworkImage>(ranking).Count(image => image.DisplayIdentity == row.Id) == 3);
            var withoutCover = SongRow.Read(Element(await MusicSongFixture.AdoptAsync(f.App,
                ArtworkSong("wy", "bindings-without-cover", ""))));
            ranking.DataContext = rank with { Preview = [new("", "", "", 1, null, withoutCover)] };
            DesktopTestUi.WindowRoot(window).UpdateLayout();
            await DesktopTestUi.DrainAsync();
            Check(ArtworkDescendants<ArtworkImage>(ranking).Single(image => image.Address == rank.Cover).DisplayIdentity == "",
                "a collection fallback is not the preview song's image");
            var collection = card with { Song = null, Cover = "https://images.example.test/collection.png" };
            var entry = subjects.Single(subject => subject.Name == "Catalog.Entry").View;
            entry.DataContext = collection; host.Content = entry; DesktopTestUi.WindowRoot(window).UpdateLayout();
            await DesktopTestUi.DrainAsync();
            Check(ArtworkDescendants<ArtworkImage>(entry).Single(image => image.Address == collection.Cover).DisplayIdentity == "",
                "collection cover cannot borrow a song's identity after recycling");
            Check(new MusicCatalogCard("rank", "Rank", "", collection.Cover, "playlist", "wy").FirstPreviewIdentity == "");
        }
        finally { window.Content = null; window.Close(); }
    }

    private static IEnumerable<T> ArtworkDescendants<T>(DependencyObject value) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++)
        {
            var child = VisualTreeHelper.GetChild(value, i);
            if (child is T found) yield return found;
            foreach (var nested in ArtworkDescendants<T>(child)) yield return nested;
        }
    }

    private static async Task SharedArtworkBudget()
    {
        const string displayed = "https://images.example.test/displayed.png";
        var http = new ArtworkHttpProbe();
        await using var backing = new MusicOfficialAudioTests.MemoryStore();
        await using var documents = new NativeTestDatabase(backing);
        var clock = new MusicOfficialAudioTests.Clock();
        await using var cache = new MusicPresentationCache(documents, clock);
        await using var loader = new ArtworkService(http, byteCache: cache, time: clock);
        using var visible = await loader.AcquireAsync(displayed);
        var pixels = visible.Image;
        clock.Now++;
        string songId = Guid.NewGuid().ToString("D");
        for (int i = 0; i < 12; i++)
        {
            await cache.SaveLyricsAsync(i == 0 ? songId : Guid.NewGuid().ToString("D"), "source",
                new JsonArray(new JsonObject { ["text"] = new string('l', 500 * 1024) }), cache.LyricEpoch, default);
            clock.Now++;
        }
        clock.Now++;
        await cache.WriteAsync(displayed + "?large=1", new byte[3 * 1024 * 1024], cache.Epoch, default);
        long actual = await documents.ReadAsync(db => db.Scalar<long>("SELECT (SELECT coalesce(sum(bytes),0) FROM windows_images)+(SELECT coalesce(sum(storedBytes),0) FROM windows_lyrics)"));
        Check(actual <= 8 * 1024 * 1024 &&
            await cache.ReadAsync(displayed, default) is null && cache.ReadLyricsAsync(songId, default).GetAwaiter().GetResult() is null,
            "images and lyrics compete in the common 8 MiB logical content budget, not two independent allocations");
        Check(ReferenceEquals(visible.Image, pixels), "disk eviction cannot revoke displayed pixels");

        var memoryHttp = new ArtworkHttpProbe(size: 512);
        await using var memory = new ArtworkService(memoryHttp);
        var held = await memory.AcquireAsync(displayed);
        var heldPixels = held.Image;
        for (int i = 0; i <= ArtworkService.CacheBytes / (512 * 512 * 8); i++)
        { using var item = await memory.AcquireAsync(displayed + "?item=" + i); }
        using (var stillVisible = await memory.AcquireAsync(displayed))
            Check(ReferenceEquals(stillVisible.Image, heldPixels), "a display lease survives decoded-byte cache eviction");
        held.Dispose();
        using var reloaded = await memory.AcquireAsync(displayed);
        Check(!ReferenceEquals(reloaded.Image, heldPixels) && memoryHttp.Addresses.Count > 1,
            "released evicted resources are recoverable misses");
    }

    private static async Task SharedArtworkLifetime()
    {
        const string address = "https://images.example.test/lifetime.png";
        var clock = new MusicOfficialAudioTests.Clock();
        await using var backing = new MusicOfficialAudioTests.MemoryStore();
        await using var documents = new NativeTestDatabase(backing);
        var cache = new MusicPresentationCache(documents, clock);
        var http = new ArtworkHttpProbe();
        await using var loader = new ArtworkService(http, byteCache: cache, time: clock);
        string songId = Guid.NewGuid().ToString("D");
        using var original = await loader.AcquireAsync(address);
        await cache.SaveLyricsAsync(songId, "source", new JsonArray(new JsonObject { ["time"] = 0, ["text"] = "line" }), cache.LyricEpoch, default);
        clock.Now += 29L * 86400000;
        using (var rebound = await loader.AcquireAsync(address))
            Check(ReferenceEquals(rebound.Image, original.Image));
        clock.Now += 86400000;
        Check(await cache.ReadAsync(address, default) is null && await cache.ReadLyricsAsync(songId, default) is null,
            "UI binding alone cannot extend either resource's thirty-day life");
        using var replaced = await loader.AcquireAsync(address);
        Check(http.Addresses.Count == 2 && !ReferenceEquals(original.Image, replaced.Image));

        string playedDirectory = NewArtworkResourceRoot();
        await using var scene = new ArtworkScene(playedDirectory);
        var old = new MusicOfficialAudioTests.Clock { Now = DateTimeOffset.UtcNow.AddDays(-29).ToUnixTimeMilliseconds() };
        var played = await MusicSongFixture.AdoptAsync(scene.App, ArtworkSong("wy", "played", address));
        string playedId = played["songId"]!.GetValue<string>();
        await using (var seeded = new MusicPresentationCache(scene.Store, old))
        {
            await seeded.WriteAsync(address, ArtworkHttpProbe.Encode(), seeded.Epoch, default);
            await seeded.SaveLyricsAsync(playedId, "source", new JsonArray(new JsonObject { ["time"] = 0, ["text"] = "played line" }),
                seeded.LyricEpoch, default);
        }
        await scene.Initialize();
        await scene.Play(played);
        var queue = await scene.Ok("music.queue.get");
        Check(queue.GetProperty("current").GetProperty("song").GetProperty("songId").GetString() == playedId);
        await scene.Replay();
        await scene.Module.PrepareShutdownAsync();
        await scene.Cache.DrainAsync();
        var entries = await scene.Cache.ReadPresentationEntriesAsync();
        Check(entries.Count == 2 && entries.All(pair => pair.Value!["updatedAt"]!.GetValue<long>() > old.Now + 28L * 86400000),
            "the real Started playback consumer renews shared image and lyric resources");
    }

    private static string NewArtworkResourceRoot()
    {
        string path = TestWorkspace.DataPath("music-artwork-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    private static string ArtworkResourceFile(string directory, string group, string resource) =>
        Path.Combine(directory, "r-" + group + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(resource))) + ".resource");
}
