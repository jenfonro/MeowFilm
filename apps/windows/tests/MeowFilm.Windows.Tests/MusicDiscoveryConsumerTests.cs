using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Search;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicDiscoveryConsumerTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.DiscoveryConsumerCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> DiscoveryConsumerCases()
    {
        yield return Case("native discovery only starts on demand and local rotation makes no HTTP or Node call", async () =>
        {
            await using var f = await DiscoveryScene.Create();
            Equal(0, f.Http.Requests.Count); True(!f.Runtime.HasStarted, "ordinary native startup");
            await f.Open();
            Equal(8, f.Module.DiscoveryHot.Count); Equal(6, f.Module.DiscoveryRecommended.Count);
            Equal(1, f.Module.DiscoveryCharts.Count); Equal(3, f.Module.DiscoveryCharts[0].Songs.Length);
            True(f.Module.CanUseDiscovery && !f.Module.IsSongList && !f.Module.IsLocalLibraryPage, "exclusive page");
            int before = f.Http.Requests.Count;
            var words = f.Module.DiscoveryRecommended.Select(w => w.Text).ToArray();
            await f.Module.RotateDiscoveryAsync();
            True(!words.SequenceEqual(f.Module.DiscoveryRecommended.Select(w => w.Text)), "six-word local rotation");
            Equal(before, f.Http.Requests.Count); Equal(0, f.Runtime.Calls.Count);
        });
        yield return Case("native discovery keyword consumes saved search policy and history without JS", async () =>
        {
            await using var f = await DiscoveryScene.Create();
            await f.Policy(); await f.Open();
            var word = f.Module.DiscoveryHot[0];
            await f.Module.SearchDiscoveryKeywordAsync(word);
            await DiscoveryUntil(() => f.Module.Songs.Count == 3);
            True(f.Module.IsSearchPage, "actual music search results");
            Equal(1, f.Http.Searches);
            var history = (await f.Call("music.search.history.get")).GetProperty("history").GetProperty("items");
            True(history.EnumerateArray().Any(h => h.GetProperty("text").GetString() == word.Text &&
                h.GetProperty("kind").GetString() == "songs"), "durable search history");
            Equal(0, f.Module.DiscoveryCharts.Count); Equal(0, f.Runtime.Calls.Count);
        });
        yield return Case("native discovery chart queue retains recording and chart provenance then opens full playlist", async () =>
        {
            await using var f = await DiscoveryScene.Create(); await f.Open();
            var chart = f.Module.DiscoveryCharts[0]; var song = chart.Songs[1];
            await f.Module.UseDiscoverySongAsync(song, false);
            var queue = await f.Call("music.queue.get", new { offset = 0, limit = 100 });
            Equal(1, queue.GetProperty("entries").GetArrayLength());
            var raw = queue.GetProperty("entries")[0].GetProperty("song");
            Equal("1002", MusicSongFixture.NativeId(raw));
            string saved = queue.GetRawText();
            True(saved.Contains("\"sourcePlatform\":\"wy\"") && saved.Contains("\"sourceType\":\"playlist\"") &&
                saved.Contains("\"sourceId\":\"500\""), "actual chart origin");
            await f.Module.OpenDiscoveryChartAsync(chart);
            True(f.Module.IsCatalogPlaylist && f.Module.Songs.Count == 3, "complete existing playlist loader");
            Equal(0, f.Runtime.Calls.Count);
        });
        yield return Case("native discovery selected song reaches real script resolver and same playback port", async () =>
        {
            await using var f = await DiscoveryScene.Create();
            await f.EnableScript(); await f.Open();
            var chart = f.Module.DiscoveryCharts[0];
            await f.Module.UseDiscoverySongAsync(chart.Songs[1], true);
            Equal(1, f.Playback.Opens.Count);
            Equal("https://media.invalid/1002", f.Playback.Opens[0].Uri.AbsoluteUri);
            var queue = await f.Call("music.queue.get", new { limit = 100 });
            Equal(3, queue.GetProperty("entries").GetArrayLength());
            Equal("发现歌曲1002", f.Module.CurrentTitle);
            True(f.Runtime.Calls.All(c => c.StartsWith("music.script.", StringComparison.Ordinal)), "only authored JS execution uses Node");
        });
        yield return Case("native discovery partial failure stays visible and both primary failures remain retryable", async () =>
        {
            await using var f = await DiscoveryScene.Create(); f.Http.FailTop = true;
            await f.Open();
            Equal(8, f.Module.DiscoveryHot.Count); Equal(0, f.Module.DiscoveryCharts.Count);
            Equal("toplist", f.Module.DiscoveryFailures.Single().Stage);
            f.Http.FailHot = true; await f.Module.RefreshDiscoveryAsync();
            Equal(8, f.Module.DiscoveryHot.Count);
            True(f.Module.DiscoveryStatus.Length > 0 && f.Module.CanUseDiscovery, "failed refresh retains valid completed data and explicit failure");
            f.Http.FailHot = false; f.Http.FailTop = false; await f.Module.RefreshDiscoveryAsync();
            True(f.Module.CanUseDiscovery && f.Module.DiscoveryCharts.Count == 1, "explicit refresh recovers");
        });
        yield return Case("native discovery leave unsubscribes without waiting for HTTP and ignores late content", async () =>
        {
            await using var f = await DiscoveryScene.Create(); f.Http.HoldHot = true;
            var read = f.Open(); await f.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var leave = f.Module.NavigateAsync("favorites", default);
            await Task.WhenAll(read, leave).WaitAsync(TimeSpan.FromSeconds(2));
            True(f.Module.IsCollectionPage, "navigation must not await retired HTTP");
            f.Http.Release.TrySetResult(); await Task.WhenAll(read, leave);
            True(f.Module.IsCollectionPage && !f.Module.CanUseDiscovery, "late response cannot republish");
            Equal(0, f.Module.DiscoveryCharts.Count); Equal(0, f.Runtime.Calls.Count);
        });
        yield return Case("native discovery cancel and shutdown include asynchronous stream disposal", async () =>
        {
            await using var f = await DiscoveryScene.Create(); f.Http.HoldDisposal = true;
            var read = f.Open(); await f.Http.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cancel = f.Module.CancelDiscoveryAsync(); var close = f.Module.PrepareShutdownAsync();
            await cancel.WaitAsync(TimeSpan.FromSeconds(2));
            await Drain(); True(!close.IsCompleted, "service shutdown must await response disposal");
            f.Http.DisposeRelease.TrySetResult(); await Task.WhenAll(read, cancel, close);
            True(!f.Module.CanUseDiscovery, "sealed after full drain");
            int count = f.Http.Requests.Count;
            await f.Module.CancelShutdownPreparationAsync();
            Equal(count, f.Http.Requests.Count); // Unmounted view does not silently issue a new request.
            f.Http.HoldDisposal = false; await f.Open();
            True(f.Module.CanUseDiscovery, "explicit post-resume navigation works");
        });
        yield return Case("native discovery invalidation synchronously refuses detached callbacks before UI dispatch", async () =>
        {
            await using var f = await DiscoveryScene.Create(); await f.Open();
            var chart = f.Module.DiscoveryCharts[0]; var word = f.Module.DiscoveryHot[0];
            await f.Call("music.accounts.logout", new { platform = "wy" });
            True(!f.Module.CanUseDiscovery, "native-issued generation revoked");
            await f.Module.UseDiscoverySongAsync(chart.Songs[0], false);
            await f.Module.SearchDiscoveryKeywordAsync(word);
            Equal(0, (await f.Call("music.queue.get", new { limit = 1 })).GetProperty("total").GetInt32());
            Equal(0, f.Http.Searches);
            await f.Module.RefreshDiscoveryAsync(); True(f.Module.CanUseDiscovery, "new anonymous generation");
            await f.Module.UseDiscoverySongAsync(chart.Songs[0], false);
            Equal(0, (await f.Call("music.queue.get", new { limit = 1 })).GetProperty("total").GetInt32());
        });
        yield return Case("native discovery stale row after rotation and immediate view reopen cannot mutate or lose new flight", async () =>
        {
            await using var f = await DiscoveryScene.Create(); await f.Open();
            var word = f.Module.DiscoveryHot[0]; var chart = f.Module.DiscoveryCharts[0];
            await f.Module.RotateDiscoveryAsync();
            await f.Module.SearchDiscoveryKeywordAsync(word); await f.Module.UseDiscoverySongAsync(chart.Songs[0], false);
            Equal(0, f.Http.Searches);
            var oldClose = f.Module.DeactivateDiscoveryViewAsync();
            var reopen = f.Module.ActivateDiscoveryViewAsync();
            await Task.WhenAll(oldClose, reopen);
            True(f.Module.CanUseDiscovery, "old retirement cannot cancel new page");
            int count = f.Http.Requests.Count;
            await f.Module.NavigateAsync("favorites", default); await f.Open();
            Equal(count, f.Http.Requests.Count); // Finished current cache is retained on leave, not shutdown.
        });
        yield return Case("native discovery empty online search opens discovery while empty local search stays local", async () =>
        {
            await using var f = await DiscoveryScene.Create();
            await f.Module.NavigateAsync("favorites", default);
            await f.Module.SearchAsync("", default); True(f.Module.IsCollectionPage, "local empty search keeps the actual page");
            Equal(0, f.Http.Requests.Count);
            await f.Open(); await f.Module.SearchAsync("", default);
            True(f.Module.IsDiscovery && f.Module.CanUseDiscovery, "online empty query");
            var bad = await f.App.ExecuteAsync("music.discovery.read", new { refresh = false, cookie = "not-a-credential" });
            Equal("INVALID_REQUEST", bad.GetProperty("code").GetString());
        });
        yield return Case("native discovery view reactivation during retiring HTTP waits and then restores exactly once", async () =>
        {
            await using var f = await DiscoveryScene.Create(); f.Http.HoldHot = true;
            var read = f.Open(); await f.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var hide = f.Module.DeactivateDiscoveryViewAsync();
            var show = f.Module.ActivateDiscoveryViewAsync();
            await Drain(); True(!show.IsCompleted, "reactivation intent waits for actual retired work");
            f.Http.Release.TrySetResult(); await Task.WhenAll(read, hide, show);
            True(f.Module.CanUseDiscovery, "same visible page automatically restored after real drain");
            Equal(1, f.Http.Requests.Count(p => p.Contains("hotsearchlist", StringComparison.Ordinal)));
        });
        yield return Case("native discovery second unload cancels pending reactivation without background refetch", async () =>
        {
            await using var f = await DiscoveryScene.Create(); f.Http.HoldHot = true;
            var read = f.Open(); await f.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var hide = f.Module.DeactivateDiscoveryViewAsync();
            var show = f.Module.ActivateDiscoveryViewAsync();
            var hideAgain = f.Module.DeactivateDiscoveryViewAsync();
            f.Http.Release.TrySetResult(); await Task.WhenAll(read, hide, show, hideAgain);
            True(!f.Module.CanUseDiscovery, "hidden view cannot publish");
            Equal(1, f.Http.Requests.Count(p => p.Contains("hotsearchlist", StringComparison.Ordinal)));
        });
        yield return Case("native discovery initial Loaded waits without automatically retrying a failed first request", async () =>
        {
            await using var f = await DiscoveryScene.Create(); f.Http.HoldHot = true; f.Http.FailHot = true; f.Http.FailTop = true;
            var read = f.Open(); await f.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var visible = f.Module.ActivateDiscoveryViewAsync();
            f.Http.Release.TrySetResult(); await Task.WhenAll(read, visible);
            True(!f.Module.CanUseDiscovery && f.Module.DiscoveryStatus.Length > 0, "failure remains an explicit state");
            Equal(1, f.Http.Requests.Count(p => p.Contains("hotsearchlist", StringComparison.Ordinal)));
        });
        yield return Case("native discovery WPF controls render business content and route a real button to search", async () =>
        {
            await using var f = await DiscoveryScene.Create(); await f.Policy(); await f.Open();
            var view = (FrameworkElement)f.Module.View;
            var host = new Border { Background = (System.Windows.Media.Brush)view.FindResource("MeowFilm.Brush.Window"), Child = view };
            Render(host, 900, 760);
            var discovery = Visuals(view).OfType<MusicDiscoveryView>().Single();
            True(discovery.IsVisible || discovery.Visibility == Visibility.Visible, "discovery is the selected control");
            True(!Visuals(view).OfType<MusicCatalogView>().Any(),
                "discovery owns the content body; inactive catalog is unmounted");
            var button = Visuals(discovery).OfType<Button>().First(b => b.Tag is MusicDiscoveryKeyword word && word.Text == "热搜1");
            Click(button);
            await DiscoveryUntil(() => f.Module.IsSearchPage && f.Module.Songs.Count == 3);
            Equal(1, f.Http.Searches);
        });
        yield return Case("native discovery navigation keeps scroll before clearing the retired display", async () =>
        {
            await using var f = await DiscoveryScene.Create(); await f.Open();
            var view = (FrameworkElement)f.Module.View;
            view.Measure(new Size(900, 380)); view.Arrange(new Rect(0, 0, 900, 380)); view.UpdateLayout(); await Drain();
            var control = Visuals(view).OfType<MusicDiscoveryView>().Single();
            var scroll = Visuals(control).OfType<ScrollViewer>().Single();
            scroll.ScrollToVerticalOffset(150); await Drain();
            double offset = scroll.VerticalOffset; True(offset > 20, "real scrollable discovery");
            await f.Module.NavigateAsync("favorites", default); await f.Open(); await Drain();
            True(Math.Abs(scroll.VerticalOffset - offset) < 1, "back restores original offset, not cleared-content clamp");
        });
        yield return Case("native discovery anonymous cache cannot be reissued inside an account invalidation callback", async () =>
        {
            await using var store = ServiceStore();
            await using var runtime = new NativeMusicScriptTestRuntime();
            var service = new PausedInvalidationDiscovery();
            await using var app = new MusicNativeApplication(store, runtime, new NoUpstream(), null, (_, _) => service);
            runtime.Register(app);
            var old = await app.ReadDiscoveryAsync(false);
            True(app.IsDiscoveryCurrent(old.Generation), "anonymous result initially current");
            service.Block = true;
            var logout = Task.Run(() => app.ExecuteAsync("music.accounts.logout", new { platform = "wy" }));
            try
            {
                await service.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                True(!app.IsDiscoveryCurrent(old.Generation), "invalidation synchronously seals old issuance");
                var concurrent = await app.ExecuteAsync("music.discovery.read", new { refresh = false });
                Equal("STALE_SESSION", concurrent.GetProperty("code").GetString());
                True(!app.IsDiscoveryCurrent(old.Generation), "no stale cache reissued before callback finishes");
            }
            finally { service.Release.Set(); }
            True((await logout).GetProperty("ok").GetBoolean(), "native logout settled");
            True(!app.IsDiscoveryCurrent(old.Generation), "final invalidation fence");
            var current = await app.ReadDiscoveryAsync(false);
            True(app.IsDiscoveryCurrent(current.Generation) && current.Generation != old.Generation, "new anonymous snapshot admitted");
        });
        foreach (int readNumber in new[] { 1, 2 })
        yield return Case($"native discovery page cancellation drains physical account read {readNumber} before reopening", async () =>
        {
            var store = new DiscoveryHeldStore(ServiceStore());
            await using var f = await DiscoveryScene.Create(store);
            store.Arm(readNumber);
            var read = f.Open(); await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cancel = f.Module.CancelDiscoveryAsync();
            await Drain();
            if (readNumber == 1)
                True(!read.IsCompleted && !cancel.IsCompleted, "pre-admission account observation still owns its disk read");
            else await Task.WhenAll(read, cancel).WaitAsync(TimeSpan.FromSeconds(2));
            True(store.Active == 1, "service still owns the admitted account read");
            Equal(0, f.Http.Requests.Count);
            store.Release.TrySetResult(); await Task.WhenAll(read, cancel);
            True(!f.Module.CanUseDiscovery, "late disk completion cannot publish");
            await f.Open(); True(f.Module.CanUseDiscovery, "same account owner remains open and subsequent read succeeds");
        });
    }
    private static async Task DiscoveryUntil(Func<bool> done)
    {
        for (int n = 0; n < 500; n++)
        { if (done()) return; await Task.Delay(10); await Drain(); }
        throw new InvalidOperationException("Discovery consumer did not settle");
    }
    private sealed class DiscoveryScene : IAsyncDisposable
    {
        internal readonly string Root = NewRoot();
        private readonly IDocumentStore store;
        internal readonly NativeMusicScriptTestRuntime Runtime = new();
        internal readonly DiscoveryHttp Http = new();
        internal readonly PlaybackProbe Playback = new();
        internal readonly Dialogs Dialog = new();
        internal readonly MusicNativeApplication App;
        internal readonly MusicCapability Module;
        private DiscoveryScene(IDocumentStore? selectedStore = null)
        {
            store = selectedStore ?? ServiceStore();
            App = new(store, Runtime, Http); Runtime.Register(App);
            Module = new(App, store, Playback, Dialog);
        }
        internal static async Task<DiscoveryScene> Create(IDocumentStore? store = null)
        {
            var f = new DiscoveryScene(store);
            try { await f.Module.InitializeAsync(default); await Drain(); return f; }
            catch { await f.DisposeAsync(); throw; }
        }
        internal Task Open() => Module.NavigateAsync("catalog-discovery", default);
        internal async Task<JsonElement> Call(string method, object? input = null)
        {
            var result = await App.ExecuteAsync(method, input ?? new { });
            True(result.GetProperty("ok").GetBoolean(), method + ":" + result.GetRawText()); return result;
        }
        internal async Task Policy()
        {
            foreach (var row in Module.SongSearch.Platforms.ToArray())
                await Module.SetSearchPlatformEnabledAsync("songs", row, row.Platform == "wy");
            foreach (var row in Module.PlaylistSearch.Platforms.ToArray())
                await Module.SetSearchPlatformEnabledAsync("playlists", row, false);
        }
        internal async Task EnableScript()
        {
            var before = (await Call("music.settings.get")).GetProperty("settings");
            Dialog.ScriptPath = Path.Combine(Root, "discovery-authored-lx.js");
            File.WriteAllText(Dialog.ScriptPath, TestScript(), new UTF8Encoding(false));
            await Module.ImportSourceAsync(); Equal(1, Module.Sources.Count);
            Module.SelectedSource = Module.Sources.Single();
            await Module.LoadSourceAsync(Module.SelectedSource); await Module.SourceActionAsync("enable");
            await Until(() => Module.CanEdit);
            var installed = (await Call("music.sources.list")).GetProperty("items").EnumerateArray().Single();
            Equal(Module.Sources.Single().InstallationId, installed.GetProperty("installationId").GetString());
            True(installed.GetProperty("enabled").GetBoolean(), "enabled script participates in discovery playback");
            var saved = (await Call("music.settings.get")).GetProperty("settings");
            Equal(3, saved.GetProperty("schema").GetInt32());
            Equal(before.GetRawText(), saved.GetRawText());
            True(Module.Status.Length == 0, "script enable accepted");
        }
        public async ValueTask DisposeAsync()
        {
            Http.Release.TrySetResult(); Http.DisposeRelease.TrySetResult();
            if (store is DiscoveryHeldStore held) held.Release.TrySetResult();
            try { await Module.DisposeAsync(); }
            finally { await Runtime.DisposeAsync(); await store.DisposeAsync(); }
        }
    }
    private sealed class DiscoveryHeldStore(IDocumentStore inner) : IDocumentStore
    {
        private int target, reads, active;
        internal int Active => Volatile.Read(ref active);
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Arm(int number) { reads = 0; target = number; }
        public async Task<DocumentSnapshot> ReadAsync(string documentNamespace, CancellationToken cancellationToken = default)
        {
            if (documentNamespace == "music.accounts.v1" && target > 0 && Interlocked.Increment(ref reads) == target)
            {
                Interlocked.Increment(ref active); Entered.TrySetResult();
                try
                {
                    await Release.Task; // Deliberately uncooperative disk read, not a simulated completed read.
                    return await inner.ReadAsync(documentNamespace, CancellationToken.None);
                }
                finally { Interlocked.Decrement(ref active); }
            }
            return await inner.ReadAsync(documentNamespace, cancellationToken);
        }
        public Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> data,
            DocumentFingerprint expected, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(documentNamespace, data, expected, cancellationToken);
        public ValueTask DisposeAsync() { Release.TrySetResult(); return inner.DisposeAsync(); }
    }
    private sealed class DiscoveryHttp : HttpMessageHandler
    {
        internal readonly ConcurrentQueue<string> Requests = new();
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Disposing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DisposeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HoldHot, HoldDisposal, FailHot, FailTop;
        internal int Searches;
        private static object Track(int id) => new { id, name = "发现歌曲" + id, dt = 180000, fee = 0,
            ar = new[] { new { id = 1, name = "发现歌手" } }, al = new { id = 2, name = "发现专辑", picUrl = "" } };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            True(uri.Host is "music.163.com" or "interfacepc.music.163.com", "fixed native WY transport");
            Requests.Enqueue(uri.AbsolutePath); object body;
            if (uri.AbsolutePath.Contains("hotsearchlist", StringComparison.Ordinal))
            {
                Entered.TrySetResult(); if (HoldHot) await Release.Task; // actual native physical-drain test
                if (FailHot) return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
                body = new { code = 200, data = Enumerable.Range(1, 8).Select(i => new { searchWord = "热搜" + i, score = i * 100 }) };
            }
            else if (uri.AbsolutePath.Contains("defaultkeyword", StringComparison.Ordinal))
                body = new { code = 200, data = new { realkeyword = "发现推荐" } };
            else if (uri.AbsolutePath.Contains("toplist", StringComparison.Ordinal))
            {
                if (FailTop) return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
                body = new { code = 200, list = new[] { new { id = 500, name = "发现榜单", trackCount = 3, coverImgUrl = "", creator = new { nickname = "榜单作者" } } } };
            }
            else if (uri.AbsolutePath.Contains("/playlist/detail", StringComparison.Ordinal))
                body = new { code = 200, playlist = new { id = 500, name = "发现榜单", trackCount = 3, updateTime = 1,
                    trackIds = Enumerable.Range(1001, 3).Select(id => new { id }) } };
            else if (uri.AbsolutePath.Contains("/song/detail", StringComparison.Ordinal))
                body = new { code = 200, songs = Enumerable.Range(1001, 3).Select(Track) };
            else if (uri.AbsolutePath.Contains("/cloudsearch/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Searches);
                body = new { code = 200, result = new { songCount = 3, songs = Enumerable.Range(1001, 3).Select(Track) } };
            }
            else throw new InvalidOperationException("Unexpected discovery transport route");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body);
            HttpContent content = HoldDisposal ? new StreamContent(new DiscoveryDisposal(bytes, Disposing, DisposeRelease)) : new ByteArrayContent(bytes);
            content.Headers.ContentType = new("application/json");
            return new(HttpStatusCode.OK) { Content = content };
        }
    }
    private sealed class DiscoveryDisposal(byte[] bytes, TaskCompletionSource entered, TaskCompletionSource release) : MemoryStream(bytes)
    {
        public override async ValueTask DisposeAsync()
        { entered.TrySetResult(); await release.Task; await base.DisposeAsync(); }
    }
    private sealed class PausedInvalidationDiscovery : IMusicSearchDiscovery
    {
        private long generation = 1;
        private MusicSearchDiscoverySnapshot? cache = new(1, [], [], [], [], [], 1);
        internal bool Block;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        public Task<MusicSearchDiscoverySnapshot> ReadAsync(bool refresh = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(cache ??= new(++generation, [], [], [], [], [], 1));
        }
        public MusicSearchDiscoverySnapshot Rotate(long expectedGeneration) => cache!;
        public void Invalidate(string? platform = null)
        {
            if (platform is not null and not "wy") return;
            if (Block) { Block = false; Entered.TrySetResult(); Release.Wait(); }
            cache = null;
        }
        public Task CancelAndDrainAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() { Release.Set(); Release.Dispose(); return ValueTask.CompletedTask; }
    }
}
