using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MeowFilm.Capabilities;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicSearchConsumerTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.SearchConsumerCases();
}
public static class MusicSyncConsumerLifecycleTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.SyncResumeConsumerCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> SearchConsumerCases()
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
        yield return Case("native search publishes comprehensive results without requesting platform home", async () =>
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
            True(f.Module.IsSearchOverview && f.Module.HasSearchSession, "actual comprehensive page");
            Equal(1, f.Module.CatalogSections.Single(section => section.Title == "单曲").Items.Count);
            Equal(1, f.Module.CatalogSections.Single(section => section.Title == "歌单").Items.Count);
            var history = (await f.Call("music.search.history.get")).GetProperty("history").GetProperty("items")[0];
            Equal("原生", history.GetProperty("text").GetString());
            Equal("songs", history.GetProperty("kind").GetString());
            True(!history.TryGetProperty("platform", out _), "history has no search mode");
            await f.Module.OpenSearchTabAsync("playlists");
            await SearchUntil(() => f.Module.CatalogSections.SelectMany(s => s.Items).Any(c => c.Kind == "playlist"));
            var playlist = f.Module.CatalogSections.Single().Items.Single();
            True(playlist.CanOpen && playlist.Platform == "wy", "public playlist consumer");
            await f.Module.OpenSearchTabAsync("songs");
            Equal(1, f.Module.Songs.Count); Equal(2, f.Http.SearchRequests);
            Equal(accountReads + 3, f.Store.AccountReads);
            Equal(0, f.Runtime.Calls.Count);
        });
        yield return Case("duplicate visible search start shares one owner flight and one initial account observation", async () =>
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
            await SearchUntil(() => f.Module.Songs.Count == 1 && f.Module.CatalogSections.All(s => !s.IsLoading));
            Equal(1, f.Http.SearchRequests);
            Equal(accountReads + 2, f.Store.AccountReads);
        });
        yield return Case("visible search resubmission uses page TTL while navigation only restores results", async () =>
        {
            var clock = new SearchClock();
            await using var f = await SearchScene.Create(clock);
            await f.Policy(["wy"], ["wy"]);
            await f.Search("缓存");
            await SearchUntil(() => f.Http.SearchRequests == 2 && f.Module.CatalogSections.All(s => !s.IsLoading));
            await f.Module.SearchAsync("缓存", default);
            await SearchUntil(() => f.Module.CatalogSections.Count == 2 && f.Module.CatalogSections.All(s => !s.IsLoading));
            Equal(2, f.Http.SearchRequests);
            await f.Module.NavigateAsync("favorites", default);
            clock.Advance(TimeSpan.FromMinutes(61));
            await f.Module.ReturnToSearchAsync();
            await f.Module.OpenSearchTabAsync("playlists");
            await f.Module.OpenSearchTabAsync("overview");
            Equal(2, f.Http.SearchRequests);
            await f.Module.SearchAsync("缓存", default);
            await SearchUntil(() => f.Http.SearchRequests == 4 && f.Module.CatalogSections.All(s => !s.IsLoading));
            Equal(1, f.Module.Songs.Count);
            Equal(1, f.Module.CatalogSections.Single(s => s.Title == "歌单").Items.Count);
        });
        yield return Case("resubmitting an active initial search shares physical requests without cancellation", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], [], automaticPlaylists: false);
            f.Http.Hold = true;
            await f.Search("进行中");
            await f.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Module.SearchAsync("进行中", default).WaitAsync(TimeSpan.FromSeconds(5));
            Equal(1, f.Http.SearchRequests);
            True(!f.Http.Release.Task.IsCompleted && f.Module.CatalogSections[0].IsLoading, "same live request");
            f.Http.Release.TrySetResult();
            await SearchUntil(() => f.Module.Songs.Count == 1 && f.Module.CatalogSections.All(s => !s.IsLoading));
            Equal(1, f.Http.SearchRequests);
        });
        yield return Case("native search card favorite and queue preserve recording and search provenance", async () =>
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
            Equal("wy", MusicSongFixture.Info(song).GetProperty("source").GetString());
            True(song.GetProperty("sources").GetArrayLength() > 0, "fused recording retained");
            string queueJson = queue.GetRawText();
            True(queueJson.Contains("searchKeyword") && queueJson.Contains("来源"), "search provenance retained");
            True(!queueJson.Contains("\"sourceType\":\"playlist\""), "not a fictitious playlist");
            Equal(0, f.Runtime.Calls.Count);
        });
        yield return Case("native search page consumes canonical merged recordings through three scroll rounds", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], [], automaticPlaylists: false);
            f.Http.SearchResult = count => new { code = 200, result = new { songCount = 61,
                songs = Enumerable.Range(0, count == 3 ? 1 : 30)
                    .Select(index => SearchHttp.Track(1001 + (count - 1) * 30 + index)).ToArray() } };
            await f.Search("分页录音");
            await f.Module.OpenSearchTabAsync("songs");
            await SearchUntil(() => f.Module.Songs.Count == 1 && f.Module.CanLoadMoreSearchSongs);
            string id = f.Module.Songs[0].Id;
            for (int round = 2; round <= 3; round++)
            {
                True(f.Module.CanLoadMoreSearchSongs, "provider continuation is available");
                await f.Module.MoreSearchAsync();
                await SearchUntil(() => f.Module.Songs.Count == 1 &&
                    f.Module.Songs[0].Raw.GetProperty("sources").GetArrayLength() == (round == 3 ? 61 : round * 30));
                Equal(id, f.Module.Songs[0].Id);
                Equal("1001", MusicSongFixture.NativeId(f.Module.Songs[0].Raw));
                _ = SongRow.Read(f.Module.Songs[0].Raw);
            }
            Equal(3, f.Http.SearchRequests); Equal(0, f.Runtime.Calls.Count);
            True(!f.Module.CanLoadMoreSearchSongs, "final native page ends the search");
        });
        yield return Case("native search playlist back restores song and playlist tabs without refetch", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], ["wy"]);
            await f.Search("返回");
            await f.Module.OpenSearchTabAsync("playlists");
            await SearchUntil(() => f.Module.CatalogSections.SelectMany(s => s.Items).Any(c => c.Kind == "playlist"));
            await f.Module.OpenCatalogCardAsync(f.Module.CatalogSections.Single().Items.Single());
            True(f.Module.IsCatalogPlaylist && f.Module.Songs.Count == 1, "native playlist opened");
            int searches = f.Http.SearchRequests;
            await f.Module.ReturnToSearchAsync();
            True(f.Module.IsSearchPlaylists && f.Module.SearchTab == "playlists", "same result tab");
            Equal(searches, f.Http.SearchRequests);
            await f.Module.OpenSearchTabAsync("songs");
            Equal(1, f.Module.Songs.Count); Equal(searches, f.Http.SearchRequests);
        });
        yield return Case("native search policy changes save immediately and retire old results", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], ["wy"], automaticPlaylists: false);
            await f.Search("草稿");
            await SearchUntil(() => f.Module.Songs.Count == 1);
            Equal(1, f.Http.SearchRequests);
            var wy = f.Module.SongSearch.Platforms.Single(p => p.Platform == "wy");
            await f.Module.SetSearchPlatformEnabledAsync("songs", wy, false);
            True(!f.Module.HasSearchSession && f.Module.CatalogSections.Count == 0, "old search retired");
            int requests = f.Http.SearchRequests;
            await f.Module.SearchAsync("保存后", default);
            Equal("未启用搜索平台", f.Module.SearchStatus);
            Equal(requests, f.Http.SearchRequests);
        });
        yield return Case("native search cancellation waits for physical HTTP and ignores late results", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], [], automaticPlaylists: false);
            f.Http.Hold = true;
            await f.Search("取消");
            await f.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cancel = f.Module.CancelSearchAsync();
            await Drain();
            True(!cancel.IsCompleted, "physical request has not returned");
            f.Http.Release.TrySetResult();
            await cancel.WaitAsync(TimeSpan.FromSeconds(5));
            await Drain();
            Equal(0, f.Module.Songs.Count); Equal("已取消", f.Module.SearchStatus);
            True(f.Module.CatalogSections.All(s => !s.IsLoading), "actual loading drained");
        });
        yield return Case("native search background result cannot reopen a later local page", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], [], automaticPlaylists: false);
            f.Http.Hold = true;
            await f.Search("迟到");
            await f.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Module.NavigateAsync("favorites", default);
            f.Http.Release.TrySetResult();
            await SearchUntil(() => f.Http.Completions > 0);
            await Drain(); True(f.Module.IsCollectionPage && !f.Module.IsSearchPage, "local route retained");
            await f.Module.ReturnToSearchAsync();
            await SearchUntil(() => f.Module.Songs.Count == 1);
            Equal(1, f.Http.SearchRequests);
        });
        yield return Case("search start draining an old query does not block get next or cancel behind the policy lane", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], [], automaticPlaylists: false);
            f.Http.Hold = true;
            await f.Search("旧查询");
            await f.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            string old = (await f.Call("music.search.start",
                new { keyword = "旧查询", kind = "songs", platforms = new[] { "wy" } })).GetProperty("id").GetString()!;
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(JsonElement value)
            {
                if (value.TryGetProperty("id", out var id) && id.GetString() == old &&
                    value.TryGetProperty("cancelled", out var cancelled) && cancelled.GetBoolean())
                    stopped.TrySetResult();
            }
            f.App.SearchChanged += Changed;
            try
            {
                var replacement = f.App.ExecuteAsync("music.search.start",
                    new { keyword = "新查询", kind = "songs", platforms = new[] { "wy" } });
                await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
                True(!replacement.IsCompleted, "replacement must wait for the old physical request");
                var get = f.App.ExecuteAsync("music.search.get", new { id = old });
                var next = f.App.ExecuteAsync("music.search.next", new { id = old });
                await Task.WhenAll(get, next).WaitAsync(TimeSpan.FromSeconds(2));
                Equal("SEARCH_BUSY", (await f.App.ExecuteAsync("music.search.start",
                    new { keyword = "第三个", kind = "songs", platforms = new[] { "wy" } })).GetProperty("code").GetString());
                var cancel = f.App.ExecuteAsync("music.search.cancel", new { id = old });
                True(!cancel.IsCompleted, "cancel still waits for the old physical request");
                f.Http.Release.TrySetResult();
                await Task.WhenAll(cancel, replacement).WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                f.App.SearchChanged -= Changed;
                f.Http.Release.TrySetResult();
            }
        });
        yield return Case("search start queued during a settings save cannot dispatch the replaced policy", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], [], automaticPlaylists: false);
            f.Http.Hold = true;
            await f.Search("旧策略");
            await f.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var settings = (await f.Call("music.search.settings.get")).GetProperty("settings");
            var songs = JsonNode.Parse(settings.GetProperty("songs").GetRawText())!.AsObject();
            songs["enabled"] = new JsonArray("tx");
            var save = f.App.ExecuteAsync("music.search.settings.save", new
            {
                ifRevision = settings.GetProperty("revision").GetInt64(),
                songs,
                playlists = settings.GetProperty("playlists")
            });
            await Drain();
            True(!save.IsCompleted, "save must wait for the old physical request");
            var start = f.App.ExecuteAsync("music.search.start",
                new { keyword = "新策略", kind = "songs", platforms = new[] { "wy" } });
            await Drain();
            True(!start.IsCompleted, "start waits for the pending policy save");
            f.Http.Release.TrySetResult();
            True((await save.WaitAsync(TimeSpan.FromSeconds(5))).GetProperty("ok").GetBoolean(),
                "the settings save commits");
            Equal("SEARCH_SETTINGS_CHANGED", (await start.WaitAsync(TimeSpan.FromSeconds(5))).GetProperty("code").GetString());
            Equal(1, f.Http.SearchRequests);
        });
        yield return Case("unconfirmed search settings block start until the next authoritative read", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], [], automaticPlaylists: false);
            var settings = (await f.Call("music.search.settings.get")).GetProperty("settings");
            var songs = JsonNode.Parse(settings.GetProperty("songs").GetRawText())!.AsObject();
            songs["pages"] = 2;
            f.Store.FailNextWrite = true;
            var save = await f.App.ExecuteAsync("music.search.settings.save", new
            {
                ifRevision = settings.GetProperty("revision").GetInt64(),
                songs,
                playlists = settings.GetProperty("playlists")
            });
            Equal("COMMIT_UNKNOWN", save.GetProperty("code").GetString());
            var blocked = await f.App.ExecuteAsync("music.search.start",
                new { keyword = "未确认", kind = "songs", platforms = new[] { "wy" } });
            Equal("SEARCH_SETTINGS_UNCONFIRMED", blocked.GetProperty("code").GetString());
            Equal(0, f.Http.SearchRequests);
            var current = await f.Call("music.search.settings.get");
            Equal(1, current.GetProperty("settings").GetProperty("songs").GetProperty("pages").GetInt32());
            True((await f.Call("music.search.start",
                new { keyword = "已确认", kind = "songs", platforms = new[] { "wy" } })).GetProperty("ok").GetBoolean(),
                "start succeeds after the authoritative read");
        });
        yield return Case("native search transient failure is retried by the next round and real WPF search controls", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], [], automaticPlaylists: false);
            f.Http.FailOnce = true;
            await f.Search("重试");
            await SearchUntil(() => f.Module.SearchStatus == "部分平台搜索失败");
            await f.Module.OpenSearchTabAsync("songs");
            True(f.Module.CanLoadMoreSearchSongs, "a failed platform keeps the next round available");
            await f.Module.MoreSearchAsync();
            await SearchUntil(() => f.Module.Songs.Count == 1 && f.Module.SearchStatus == "");
            Equal(2, f.Http.SearchRequests);
            await f.Module.OpenSearchTabAsync("overview"); await Drain();
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
            await f.Module.OpenSearchTabAsync("songs"); await Drain();
            True(grid.Visibility == Visibility.Visible && catalog.Visibility == Visibility.Collapsed,
                "song tab reuses the playlist song table");
            await f.Module.OpenSearchTabAsync("playlists"); await Drain();
            True(grid.Visibility == Visibility.Collapsed && catalog.Visibility == Visibility.Visible,
                "playlist tab uses the card surface");
        });
        yield return Case("native search shutdown resume drops old sessions but retains saved history", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], [], automaticPlaylists: false);
            await f.Search("恢复");
            await SearchUntil(() => f.Module.Songs.Count == 1);
            await f.Module.PrepareShutdownAsync();
            var closed = await f.App.ExecuteAsync("music.search.start", new { keyword = "禁止", kind = "songs", platforms = new[] { "wy" } });
            Equal("CLOSED", closed.GetProperty("code").GetString());
            await f.Module.CancelShutdownPreparationAsync();
            True(!f.Module.HasSearchSession, "old search handles retired on resume");
            Equal("恢复", (await f.Call("music.search.history.get")).GetProperty("history")
                .GetProperty("items")[0].GetProperty("text").GetString());
            await f.Module.SearchAsync("恢复", default);
            await SearchUntil(() => f.Module.Songs.Count == 1);
            Equal(0, f.Runtime.Calls.Count);
        });
        yield return Case("native search disabling providers waits for the admitted physical request", async () =>
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
            True(!f.Module.HasSearchSession, "disabled results cannot revive");
        });
        foreach (bool holdDispose in new[] { false, true })
            yield return Case("native search shutdown drains " + (holdDispose ? "async stream disposal" : "physical handler"), async () =>
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
                True(!f.Module.HasSearchSession, "resumed product has no old handles");
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
        private SearchScene(TimeProvider? clock = null)
        {
            App = new(Store, Runtime, Http, null, null, clock); Runtime.Register(App);
            Module = new(App, Store, new PlaybackProbe(), new Dialogs());
        }
        internal static async Task<SearchScene> Create(TimeProvider? clock = null)
        {
            var f = new SearchScene(clock);
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
    private sealed class SearchClock : TimeProvider
    {
        private long ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero)
            .AddTicks(Interlocked.Read(ref ticks));
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }
    private sealed class SearchStore(IProtectedDocumentStore inner) : IProtectedDocumentStore
    {
        private AccountReadHold? nextAccountRead;
        internal int AccountReads;
        internal bool FailNextWrite;

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
            DocumentFingerprint expected, CancellationToken cancellationToken = default)
        {
            if (FailNextWrite) { FailNextWrite = false; throw new ProtectedDocumentException(DocumentStoreError.Storage); }
            return inner.WriteAsync(documentNamespace, data, expected, cancellationToken);
        }

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
        internal Func<int, object>? SearchResult;
        internal static object Track(int id = 1001) => new { id, name = "合成歌曲", dt = 180000, fee = 0,
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
                int count = Interlocked.Increment(ref SearchRequests); Entered.TrySetResult();
                if (Hold) await Release.Task; // Deliberately ignore cancellation until actual completion.
                if (FailOnce) { FailOnce = false; return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }; }
                body = SearchResult?.Invoke(count) ?? new { code = 200, result = new { songCount = 1, songs = new[] { Track() },
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
