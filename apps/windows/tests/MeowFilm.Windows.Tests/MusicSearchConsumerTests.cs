using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MeowFilm.Capabilities;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicSearchConsumerTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.AggregateSearchConsumerCases();
}
public static class MusicSyncConsumerLifecycleTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.SyncResumeConsumerCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> AggregateSearchConsumerCases()
    {
        yield return Case("native search saved policy rejects draft targets before any HTTP", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], ["wy"]);
            var denied = await f.App.ExecuteAsync("music.search.start",
                new { keyword = "原生", kind = "songs", platforms = new[] { "tx" } });
            Equal("SEARCH_SETTINGS_CHANGED", denied.GetProperty("code").GetString());
            Equal(0, f.Http.Requests.Count); Equal(0, f.Runtime.Calls.Count);
            await f.Policy([], [], automaticPlaylists: false);
            var empty = await f.Call("music.search.start", new { keyword = "空", kind = "songs", platforms = Array.Empty<string>() });
            Equal("NO_PLATFORMS", empty.GetProperty("code").GetString()); Equal(0, f.Http.Requests.Count);
        });
        yield return Case("native aggregate search publishes comprehensive results without requesting platform home", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], ["wy"]);
            int accountReads = f.Store.AccountReads;
            f.Http.Hold = true;
            await f.Search("原生");
            await SearchUntil(() => f.Http.SearchRequests == 2 && f.Module.CatalogSections.Count == 2);
            True(f.Module.CatalogSections.All(section => section.IsLoading),
                "both visible kinds publish loading sections before either physical search settles");
            f.Http.Release.TrySetResult();
            await SearchUntil(() => f.Module.Songs.Count == 1 &&
                f.Module.CatalogSections.All(section => !section.IsLoading && section.Items.Count == 1));
            Equal(2, f.Http.SearchRequests); Equal(0, f.Http.HomeRequests);
            True(f.Module.IsAggregateOverview && f.Module.HasAggregateSearch, "actual comprehensive page");
            Equal(1, f.Module.CatalogSections.Single(section => section.Title == "单曲").Items.Count);
            Equal(1, f.Module.CatalogSections.Single(section => section.Title == "歌单").Items.Count);
            var history = (await f.Call("music.search.history.get")).GetProperty("history").GetProperty("items")[0];
            Equal("原生", history.GetProperty("text").GetString());
            Equal("songs", history.GetProperty("kind").GetString());
            Equal("aggregate", history.GetProperty("platform").GetString());
            await f.Module.OpenAggregateSearchTabAsync("playlists");
            await SearchUntil(() => f.Module.CatalogSections.SelectMany(s => s.Items).Any(c => c.Kind == "playlist"));
            var playlist = f.Module.CatalogSections.Single().Items.Single();
            True(playlist.CanOpen && playlist.Platform == "wy", "public playlist consumer");
            await f.Module.OpenAggregateSearchTabAsync("songs");
            Equal(1, f.Module.Songs.Count); Equal(2, f.Http.SearchRequests);
            Equal(accountReads + 3, f.Store.AccountReads);
            Equal(0, f.Runtime.Calls.Count);
        });
        yield return Case("duplicate visible aggregate start shares one owner flight and one initial account observation", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], [], automaticPlaylists: false);
            int accountReads = f.Store.AccountReads;
            var hold = f.Store.HoldNextAccountRead();
            var first = f.Search("单飞");
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var duplicate = f.Module.SearchAsync("单飞", default);
            await Drain();
            Equal(accountReads + 1, f.Store.AccountReads);
            Equal(0, f.Http.SearchRequests);
            hold.Release();
            await Task.WhenAll(first, duplicate).WaitAsync(TimeSpan.FromSeconds(5));
            await SearchUntil(() => f.Module.Songs.Count == 1 && !f.Module.SearchProgress.Any(p => p.Loading));
            Equal(1, f.Http.SearchRequests);
            Equal(accountReads + 2, f.Store.AccountReads);
        });
        yield return Case("native aggregate card favorite and queue preserve recording and search provenance", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], ["wy"]);
            await f.Search("来源");
            await SearchUntil(() => f.Module.Songs.Count == 1);
            var card = f.Module.CatalogSections.SelectMany(section => section.Items).Single(item => item.Song is not null);
            await f.Module.FavoriteCatalogCardAsync(card);
            var favorites = await f.Call("music.library.detail", new { id = "favorites", offset = 0, limit = 100 });
            Equal(1, favorites.GetProperty("songs").GetArrayLength());
            // The favorite operation can publish a later fused snapshot. Use
            // the current card, never a detached UI object's stale callback.
            card = f.Module.CatalogSections.SelectMany(section => section.Items).Single(item => item.Song is not null);
            await f.Module.QueueCatalogCardAsync(card);
            var queue = await f.Call("music.queue.get", new { offset = 0, limit = 100 });
            Equal(1, queue.GetProperty("entries").GetArrayLength());
            var song = queue.GetProperty("entries")[0].GetProperty("song");
            Equal("wy", song.GetProperty("source_data").GetProperty("source").GetString());
            True(song.GetProperty("sources").GetArrayLength() > 0, "fused recording retained");
            string queueJson = queue.GetRawText();
            True(queueJson.Contains("searchKeyword") && queueJson.Contains("来源"), "search provenance retained");
            True(!queueJson.Contains("\"sourceType\":\"playlist\""), "not a fictitious playlist");
            Equal(0, f.Runtime.Calls.Count);
        });
        yield return Case("native aggregate playlist back restores song and playlist tabs without refetch", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], ["wy"]);
            await f.Search("返回");
            await f.Module.OpenAggregateSearchTabAsync("playlists");
            await SearchUntil(() => f.Module.CatalogSections.SelectMany(s => s.Items).Any(c => c.Kind == "playlist"));
            await f.Module.OpenCatalogCardAsync(f.Module.CatalogSections.Single().Items.Single());
            True(f.Module.IsCatalogPlaylist && f.Module.Songs.Count == 1, "native playlist opened");
            int searches = f.Http.SearchRequests;
            await f.Module.ReturnToAggregateSearchAsync();
            True(f.Module.IsAggregatePlaylists && f.Module.AggregateSearchTab == "playlists", "same result tab");
            Equal(searches, f.Http.SearchRequests);
            await f.Module.OpenAggregateSearchTabAsync("songs");
            Equal(1, f.Module.Songs.Count); Equal(searches, f.Http.SearchRequests);
        });
        yield return Case("native aggregate policy changes save immediately and retire old results", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], ["wy"], automaticPlaylists: false);
            await f.Search("草稿");
            await SearchUntil(() => f.Module.Songs.Count == 1);
            Equal(1, f.Http.SearchRequests);
            var wy = f.Module.SongSearch.Platforms.Single(p => p.Platform == "wy");
            await f.Module.SetSearchPlatformEnabledAsync("songs", wy, false);
            True(!f.Module.HasAggregateSearch && f.Module.CatalogSections.Count == 0, "old search retired");
            int requests = f.Http.SearchRequests;
            await f.Module.SearchAsync("保存后", default);
            Equal("未启用搜索平台", f.Module.AggregateSearchStatus);
            Equal(requests, f.Http.SearchRequests);
        });
        yield return Case("native aggregate cancellation waits for physical HTTP and ignores late results", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], [], automaticPlaylists: false);
            f.Http.Hold = true;
            await f.Search("取消");
            await f.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cancel = f.Module.CancelAggregateSearchAsync();
            await Drain();
            True(!cancel.IsCompleted, "physical request has not returned");
            f.Http.Release.TrySetResult();
            await cancel.WaitAsync(TimeSpan.FromSeconds(5));
            await Drain();
            Equal(0, f.Module.Songs.Count); Equal("已取消", f.Module.AggregateSearchStatus);
            True(!f.Module.SearchProgress.Any(p => p.Loading), "actual loading drained");
        });
        yield return Case("native aggregate background result cannot reopen a later local page", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], [], automaticPlaylists: false);
            f.Http.Hold = true;
            await f.Search("迟到");
            await f.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Module.NavigateAsync("favorites", default);
            f.Http.Release.TrySetResult();
            await SearchUntil(() => f.Http.Completions > 0);
            await Drain(); True(f.Module.IsCollectionPage && !f.Module.IsAggregateSearch, "local route retained");
            await f.Module.ReturnToAggregateSearchAsync();
            await SearchUntil(() => f.Module.Songs.Count == 1);
            Equal(1, f.Http.SearchRequests);
        });
        yield return Case("native aggregate transient failure retry and real WPF search controls", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], [], automaticPlaylists: false);
            f.Http.FailOnce = true;
            await f.Search("重试");
            await SearchUntil(() => f.Module.SearchProgress.Any(p => p.CanRetry));
            var row = f.Module.SearchProgress.Single();
            True(row.Error.Length > 0, "per-platform failure visible");
            await f.Module.RetryAggregateSearchAsync(row);
            await SearchUntil(() => f.Module.Songs.Count == 1 && !f.Module.SearchProgress.Any(p => p.Loading));
            Equal(2, f.Http.SearchRequests);
            f.Module.View.Measure(new Size(1000, 850)); f.Module.View.Arrange(new Rect(0, 0, 1000, 850)); f.Module.View.UpdateLayout();
            var buttons = Visuals(f.Module.View).OfType<Button>().Select(b => b.Content as string).ToArray();
            True(buttons.Contains("综合") && buttons.Contains("单曲") && buttons.Contains("歌单") &&
                buttons.Count(text => text == "查看更多") == 2, "real result tabs and section actions");
            True(!buttons.Contains("更多结果") && !buttons.Contains("取消搜索"), "retired utility controls");
            var grid = Visuals(f.Module.View).OfType<DataGrid>().Single();
            var catalog = Visuals(f.Module.View).OfType<MusicCatalogView>().Single();
            var moreStyle = (Style)catalog.FindResource("Catalog.SectionMore");
            var moreButtons = Visuals(catalog).OfType<Button>()
                .Where(button => button.Content as string == "查看更多").ToArray();
            True(moreButtons.All(button => ReferenceEquals(button.Style, moreStyle) &&
                button.MinHeight == 30 && button.BorderThickness == new Thickness(1)),
                "section actions use the visible button treatment");
            var loadingIndicators = Visuals(catalog).OfType<Grid>()
                .Where(element => AutomationProperties.GetName(element).EndsWith("搜索中", StringComparison.Ordinal))
                .ToArray();
            Equal(2, loadingIndicators.Length);
            True(loadingIndicators.All(element => element.Visibility == Visibility.Collapsed),
                "settled section spinners are hidden");
            await f.Module.OpenAggregateSearchTabAsync("songs"); await Drain();
            True(grid.Visibility == Visibility.Visible && catalog.Visibility == Visibility.Collapsed,
                "song tab reuses the playlist song table");
            await f.Module.OpenAggregateSearchTabAsync("playlists"); await Drain();
            True(grid.Visibility == Visibility.Collapsed && catalog.Visibility == Visibility.Visible,
                "playlist tab uses the card surface");
        });
        yield return Case("native aggregate shutdown resume drops old sessions but retains saved history", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], [], automaticPlaylists: false);
            await f.Search("恢复");
            await SearchUntil(() => f.Module.Songs.Count == 1);
            await f.Module.PrepareShutdownAsync();
            var closed = await f.App.ExecuteAsync("music.search.start", new { keyword = "禁止", kind = "songs", platforms = new[] { "wy" } });
            Equal("CLOSED", closed.GetProperty("code").GetString());
            await f.Module.CancelShutdownPreparationAsync();
            True(!f.Module.HasAggregateSearch, "old search handles retired on resume");
            Equal("恢复", (await f.Call("music.search.history.get")).GetProperty("history")
                .GetProperty("items")[0].GetProperty("text").GetString());
            await f.Module.SearchAsync("恢复", default);
            await SearchUntil(() => f.Module.Songs.Count == 1);
            Equal(0, f.Runtime.Calls.Count);
        });
        yield return Case("native aggregate disabling providers waits for the admitted physical request", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], [], automaticPlaylists: false);
            f.Http.Hold = true; await f.Search("撤权");
            await f.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => f.Module.CanEditSearchPreferences);
            var save = f.Module.SetSearchPlatformEnabledAsync("songs",
                f.Module.SongSearch.Platforms.Single(p => p.Platform == "wy"), false);
            await Drain(); True(!save.IsCompleted, "consent save has not skipped physical drain");
            f.Http.Release.TrySetResult(); await save.WaitAsync(TimeSpan.FromSeconds(5));
            Equal("", f.Module.SearchPreferenceStatus);
            var settings = (await f.Call("music.search.settings.get")).GetProperty("settings");
            Equal(0, settings.GetProperty("songs").GetProperty("enabled").GetArrayLength());
            True(!f.Module.HasAggregateSearch, "disabled results cannot revive");
        });
        foreach (bool holdDispose in new[] { false, true })
            yield return Case("native aggregate shutdown drains " + (holdDispose ? "async stream disposal" : "physical handler"), async () =>
            {
                await using var f = await SearchScene.Create();
                await f.Policy(["wy"], [], automaticPlaylists: false);
                f.Http.Hold = !holdDispose; f.Http.HoldDispose = holdDispose;
                await f.Search("关闭");
                await (holdDispose ? f.Http.Disposing.Task : f.Http.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
                var prepare = f.Module.PrepareShutdownAsync();
                await Drain(); True(!prepare.IsCompleted, "product preparation has not detached physical resources");
                f.Http.Release.TrySetResult(); f.Http.DisposalRelease.TrySetResult();
                await prepare.WaitAsync(TimeSpan.FromSeconds(5));
                True(!f.Module.HasCurrent && f.Module.Songs.Count == 0, "no late content or media publication");
                await f.Module.CancelShutdownPreparationAsync();
                True(!f.Module.HasAggregateSearch, "resumed product has no old handles");
            });
    }

    internal static IEnumerable<TestCase> SyncResumeConsumerCases()
    {
        yield return Case("native sync resume reconciles completion delivered before the module unseals", async () =>
        {
            await using var store = ServiceStore();
            await using var runtime = new NativeMusicScriptTestRuntime();
            var inner = new MusicNativeApplication(store, runtime, new NoUpstream());
            var consumer = new SyncDuringResume(inner);
            await using var module = new MusicCapability(consumer, store, new PlaybackProbe(), new Dialogs());
            await module.InitializeAsync(default);
            await module.PrepareShutdownAsync();
            await module.CancelShutdownPreparationAsync();
            await SearchUntil(() => module.Playlists.Any(p => p.Name == "恢复时已同步"));
            Equal(1, consumer.EarlyNotifications); Equal(0, runtime.Calls.Count);
        });
    }
    private sealed class SyncDuringResume(MusicNativeApplication inner) : MusicApplicationForwarder(inner)
    {
        public override event Action? LibrarySyncChanged;
        internal int EarlyNotifications;
        public override async Task CancelShutdownPreparationAsync(CancellationToken token = default)
        {
            await inner.CancelShutdownPreparationAsync(token);
            await ServiceCall(inner, "music.library.create", new { name = "恢复时已同步" });
            EarlyNotifications++; LibrarySyncChanged?.Invoke();
            await Drain(); // Deliver it while MusicCapability.stopping is still true.
        }
    }

    private static async Task SearchUntil(Func<bool> done)
    {
        for (int i = 0; i < 500; i++)
        {
            if (done()) return;
            await Task.Delay(10); await Drain();
        }
        throw new InvalidOperationException("Search consumer did not settle");
    }
    private sealed class SearchScene : IAsyncDisposable
    {
        internal readonly SearchStore Store = new(ServiceStore());
        internal readonly NativeMusicScriptTestRuntime Runtime = new();
        internal readonly SearchHttp Http = new();
        internal readonly MusicNativeApplication App;
        internal readonly MusicCapability Module;
        private SearchScene()
        {
            App = new(Store, Runtime, Http); Runtime.Register(App);
            Module = new(App, Store, new PlaybackProbe(), new Dialogs());
        }
        internal static async Task<SearchScene> Create()
        {
            var f = new SearchScene();
            try { await f.Module.InitializeAsync(default); await Drain(); return f; }
            catch { await f.DisposeAsync(); throw; }
        }
        internal async Task<JsonElement> Call(string method, object? data = null)
        {
            var result = await App.ExecuteAsync(method, data ?? new { });
            True(result.GetProperty("ok").GetBoolean(), method + ":" + result.GetRawText()); return result;
        }
        internal async Task Policy(string[] songs, string[] playlists, bool automaticPlaylists = true)
        {
            var songSet = songs.ToHashSet(StringComparer.Ordinal);
            var playlistSet = (automaticPlaylists ? playlists : []).ToHashSet(StringComparer.Ordinal);
            foreach (var row in Module.SongSearch.Platforms.ToArray())
            {
                await Module.SetSearchPlatformEnabledAsync("songs", row, songSet.Contains(row.Platform));
                await Until(() => Module.CanEditSearchPreferences);
            }
            foreach (var row in Module.PlaylistSearch.Platforms.ToArray())
            {
                await Module.SetSearchPlatformEnabledAsync("playlists", row, playlistSet.Contains(row.Platform));
                await Until(() => Module.CanEditSearchPreferences);
            }
            await Module.SetSearchModeAsync("songs", "aggregate");
            await Until(() => Module.CanEditSearchPreferences);
            await Module.SetSearchModeAsync("playlists", "aggregate");
            await Until(() => Module.CanEditSearchPreferences);
        }
        internal async Task Search(string word)
        {
            await Module.NavigateAsync("catalog-search", default);
            await Module.SearchAsync(word, default);
        }
        public async ValueTask DisposeAsync()
        {
            Http.Release.TrySetResult(); Http.DisposalRelease.TrySetResult();
            try { await Module.DisposeAsync(); }
            finally { await Runtime.DisposeAsync(); await Store.DisposeAsync(); }
        }
    }
    private sealed class SearchStore(IProtectedDocumentStore inner) : IProtectedDocumentStore
    {
        private AccountReadHold? nextAccountRead;
        internal int AccountReads;

        internal AccountReadHold HoldNextAccountRead()
        {
            var hold = new AccountReadHold();
            if (Interlocked.CompareExchange(ref nextAccountRead, hold, null) is not null)
                throw new InvalidOperationException("Account read already held");
            return hold;
        }

        public async Task<ProtectedDocument> ReadAsync(string documentNamespace,
            CancellationToken cancellationToken = default)
        {
            if (documentNamespace == "music.accounts.v1")
            {
                Interlocked.Increment(ref AccountReads);
                var hold = Interlocked.Exchange(ref nextAccountRead, null);
                if (hold is not null)
                {
                    hold.Arrived.TrySetResult();
                    await hold.Released.Task;
                }
            }
            return await inner.ReadAsync(documentNamespace, cancellationToken);
        }

        public Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> data,
            DocumentFingerprint expected, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(documentNamespace, data, expected, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref nextAccountRead, null)?.Release();
            await inner.DisposeAsync();
        }
    }
    private sealed class AccountReadHold
    {
        internal TaskCompletionSource Arrived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Released { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release() => Released.TrySetResult();
    }
    private sealed class SearchHttp : HttpMessageHandler
    {
        internal readonly ConcurrentQueue<string> Requests = new();
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Disposing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DisposalRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Hold, HoldDispose, FailOnce;
        internal int SearchRequests, HomeRequests, Completions;
        private static object Track() => new { id = 1001, name = "合成歌曲", dt = 180000, fee = 0,
            ar = new[] { new { id = 1, name = "歌手" } }, al = new { id = 2, name = "专辑", picUrl = "" },
            h = new { br = 320000 }, m = new { br = 192000 }, l = new { br = 128000 } };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            True(uri.Host is "music.163.com" or "interfacepc.music.163.com", "only synthetic WY origin admitted");
            Requests.Enqueue(uri.AbsolutePath);
            object body;
            if (uri.AbsolutePath.Contains("/cloudsearch/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref SearchRequests); Entered.TrySetResult();
                if (Hold) await Release.Task; // Deliberately ignore cancellation until actual completion.
                if (FailOnce) { FailOnce = false; return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }; }
                body = new { code = 200, result = new { songCount = 1, songs = new[] { Track() },
                    playlistCount = 1, playlists = new[] { new { id = "500", name = "合成歌单", trackCount = 1 } } } };
            }
            else if (uri.AbsolutePath.Contains("/playlist/detail", StringComparison.Ordinal))
                body = new { code = 200, playlist = new { id = "500", name = "合成歌单", trackCount = 1,
                    trackIds = new[] { new { id = "1001" } }, updateTime = 1 } };
            else if (uri.AbsolutePath.Contains("/song/detail", StringComparison.Ordinal))
                body = new { code = 200, songs = new[] { Track() } };
            else { Interlocked.Increment(ref HomeRequests); throw new InvalidOperationException("Unexpected metadata request"); }
            Interlocked.Increment(ref Completions);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body);
            HttpContent content = HoldDispose ? new StreamContent(new SearchDisposeStream(bytes, Disposing, DisposalRelease))
                : new ByteArrayContent(bytes);
            content.Headers.ContentType = new("application/json");
            return new(HttpStatusCode.OK) { Content = content };
        }
    }
    private sealed class SearchDisposeStream(byte[] bytes, TaskCompletionSource entered, TaskCompletionSource release)
        : System.IO.MemoryStream(bytes)
    {
        public override async ValueTask DisposeAsync()
        {
            entered.TrySetResult(); await release.Task; await base.DisposeAsync();
        }
    }
}
