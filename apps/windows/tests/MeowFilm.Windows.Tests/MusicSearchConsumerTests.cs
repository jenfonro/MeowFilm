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
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicSearchConsumerTests
{
    public static IEnumerable<TestCase> Cases() =>
        MusicUiTests.SearchConsumerCases().Concat(MusicUiTests.SearchPlaylistPresentationCases());
}
public static class MusicSyncConsumerLifecycleTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.SyncResumeConsumerCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> SearchConsumerCases()
    {
        yield return Case("search overview second song menu preserves the complete section order and current index", async () =>
        {
            // Exercise the real search/menu/queue path with no available media sources.
            var scripts = new QualityFixture.ScriptRuntime();
            await using var f = await SearchScene.Create(scriptRuntime: scripts); await f.Policy(["wy"], []);
            var sources = await f.Call("music.officialSources.settings.get");
            await f.Call("music.officialSources.settings.save", new
            {
                ifRevision = sources.GetProperty("settings").GetProperty("revision").GetInt64(),
                enabled = Array.Empty<string>()
            });
            f.Http.SearchResult = _ => new { code = 200, result = new { songCount = 3,
                songs = Enumerable.Range(1001, 3).Select(id => SearchHttp.WyTrack(id, "歌曲" + id, "歌手", "专辑")).ToArray() } };
            await f.Search("栏目播放");
            await SearchUntil(() => !f.Module.SearchSongsLoading && f.Module.CatalogSections.Any(s => s.Items.Count == 3));
            var section = f.Module.CatalogSections.Single(s => s.Items.Count == 3);
            string id = section.Id;
            True(!string.IsNullOrWhiteSpace(id), "real search producer supplies a queue source identity");
            await f.Module.OpenSearchTabAsync("songs"); await f.Module.OpenSearchTabAsync("overview");
            Equal(id, f.Module.CatalogSections.Single(s => s.Items.Count == 3).Id);
            var cards = f.Module.CatalogSections.Single(s => s.Items.Count == 3).Items;
            f.Module.SelectedSong = cards[0].Song;
            var window = SearchPlaylistWindow(f.Module.View, 1042, 800);
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                var row = Visuals(f.Module.View).OfType<FrameworkElement>().First(e => e.IsVisible &&
                    ReferenceEquals(e.DataContext, cards[1]) && e.ContextMenu is not null);
                RaiseSongMenu(row);
                var play = (MenuItem)row.ContextMenu.Items[0];
                play.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await SearchUntil(() => !f.Module.Busy && f.Module.HasCurrent);
                var queue = await f.Call("music.queue.get", new { limit = 100 });
                var entries = queue.GetProperty("entries").EnumerateArray().ToArray();
                True(entries.Select(e => SongRow.Read(e.GetProperty("song")).Id)
                    .SequenceEqual(cards.Select(c => c.Song!.Id)), "whole overview column retains source order");
                Equal(entries[1].GetProperty("id").GetString(), queue.GetProperty("currentId").GetString());
                Equal(id, queue.GetProperty("source").GetProperty("id").GetString());
                Equal("未找到可用的完整音频", f.Module.Status);
                True(scripts.Calls.SequenceEqual(["music.script.sources.list"]) && !f.Runtime.HasStarted,
                    "only the empty script inventory is simulated; no legacy runtime or media resolution runs");
            }
            finally { window.Content = null; window.Close(); }
        });
        yield return Case("search song more and row menus open with shared templates and dynamic playlist styles", async () =>
        {
            await using var f = await SearchScene.Create(); await f.Policy(["wy"], []);
            await f.Search("菜单歌曲"); await f.Module.OpenSearchTabAsync("songs");
            await SearchUntil(() => f.Module.Songs.Count == 1 && !f.Module.SearchSongsLoading);
            var window = SearchPlaylistWindow(f.Module.View, 900, 700);
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                var grid = (DataGrid)f.Module.View.FindName("SearchSongsGrid");
                var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
                var more = Visuals(row).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "更多");
                SimulateFocus(row, UIElement.IsKeyboardFocusWithinProperty, true); row.UpdateLayout();
                Click(more);
                try
                {
                    await AssertOpenedSongMenuStyles(grid.ContextMenu, more);
                    True(grid.ContextMenu.Items.OfType<MenuItem>().Single(i => i.Header as string == "添加到歌单").Items.Count > 0,
                        "actual destination playlist children are covered");
                }
                finally { grid.ContextMenu.IsOpen = false; }
                f.Module.SelectedSong = f.Module.Songs[0]; RaiseSongMenu(grid); grid.ContextMenu.IsOpen = true;
                try { await AssertOpenedSongMenuStyles(grid.ContextMenu, grid); }
                finally { grid.ContextMenu.IsOpen = false; }
                True(!grid.ContextMenu.Items.OfType<MenuItem>().Single(i => i.Header as string == "移除").IsEnabled,
                    "search songs retain removal permissions");
            }
            finally { window.Content = null; window.Close(); }
        });
        yield return Case("captured local and online searches isolate identical path-like keywords", async () =>
        {
            foreach (string query in new[] { "同词", "search-local/catalog-search/词%2F/尾" })
            {
                await using var f = await SearchScene.Create();
                await f.Policy(["wy"], []);
                await f.Call("music.library.create", new { name = "本地位置", songs = Enumerable.Range(0, 210)
                    .Select(index => Song("restore:" + index, query + index.ToString("D3"))).ToArray() });
                await f.Module.NavigateAsync("favorites", default);
                await f.Module.SearchAsync(query, default); await f.Module.PageAsync(1);
                f.Module.SelectedSong = f.Module.Songs[4];
                string selected = f.Module.SelectedSong.SelectionKey;
                var window = SearchPlaylistWindow(f.Module.View, 1042, 420);
                try
                {
                    window.Show(); window.UpdateLayout(); await Drain();
                    var grid = (DataGrid)f.Module.View.FindName("SongsGrid");
                    var scroll = Visuals(grid).OfType<ScrollViewer>().First();
                    scroll.ScrollToVerticalOffset(10); await Drain();
                    double offset = scroll.VerticalOffset;
                    True(offset > 0, "local location has a non-top scroll anchor");
                    string local = f.Module.CaptureNavigationLocation();
                    Equal("search-local/" + Uri.EscapeDataString(query), local);
                    Equal(0, f.Http.SearchRequests);
                    await f.Search(query); await f.Module.OpenSearchTabAsync("songs");
                    await SearchUntil(() => f.Module.Songs.Count == 1 && !f.Module.SearchSongsLoading);
                    string online = f.Module.CaptureNavigationLocation();
                    Equal("catalog-search/" + Uri.EscapeDataString(query), online);
                    True(local != online, "same keyword has distinct domain positions");
                    var source = f.Module.Songs;
                    var song = source[0];
                    int reads = f.Http.SearchRequests;
                    for (int round = 0; round < 2; round++)
                    {
                        await f.Module.RestoreNavigationAsync(local, default); window.UpdateLayout(); await Drain();
                        True(f.Module.IsSongList && !f.Module.IsSearchPage, "local history restores local library search");
                        Equal("101–200 / 210", f.Module.PageText);
                        Equal(selected, f.Module.SelectedSong?.SelectionKey);
                        True(Math.Abs(offset - scroll.VerticalOffset) < 1, "local history restores saved scroll");
                        Equal(local, f.Module.CaptureNavigationLocation());
                        Equal(reads, f.Http.SearchRequests);
                        await f.Module.RestoreNavigationAsync(online, default); window.UpdateLayout(); await Drain();
                        True(f.Module.IsSearchSongs, "online history restores the retained selected tab");
                        True(ReferenceEquals(source, f.Module.Songs) && ReferenceEquals(song, f.Module.Songs[0]),
                            "online source and row survive local search restoration");
                        Equal(online, f.Module.CaptureNavigationLocation());
                        Equal(reads, f.Http.SearchRequests);
                    }
                }
                finally { window.Content = null; window.Close(); }
            }
        });
        yield return Case("restored search locations reject empty oversized and control queries", async () =>
        {
            await using var f = await SearchScene.Create();
            foreach (string prefix in new[] { "search-local/", "catalog-search/" })
            foreach (string query in new[] { "", new string('x', 201), "a\nb" })
            {
                bool rejected = false;
                try { await f.Module.RestoreNavigationAsync(prefix + Uri.EscapeDataString(query), default); }
                catch (ArgumentException) { rejected = true; }
                True(rejected, "invalid restored keyword is rejected by domain validation");
            }
            Equal(0, f.Http.SearchRequests);
        });
        yield return Case("search skeletons follow overview sections and dedicated playlist or song layout", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], ["wy"]); f.Http.Hold = true;
            var window = SearchPlaylistWindow(f.Module.View, 1042, 800);
            try
            {
                window.Show(); await f.Search("骨架");
                await SearchUntil(() => f.Http.SearchRequests == 2 && f.Module.CatalogSections.Count == 2);
                window.UpdateLayout(); await Drain();
                var catalog = (MusicCatalogView)f.Module.View.FindName("CatalogView");
                var overview = (ScrollViewer)catalog.FindName("SearchOverviewScroll");
                var visible = Visuals(f.Module.View).OfType<MeowFilm.Desktop.LoadingSkeleton>().Where(s => s.IsVisible).ToArray();
                True(visible.Length > 0, "empty overview has section skeletons");
                True(visible.All(skeleton => Visuals(overview).Contains(skeleton)), "overview has no global skeleton overlay");
                await f.Module.OpenSearchTabAsync("playlists"); window.UpdateLayout(); await Drain();
                True(Visuals(f.Module.View).OfType<MeowFilm.Desktop.LoadingSkeleton>().Any(s => s.IsVisible), "playlist page has card skeletons");
                await f.Module.OpenSearchTabAsync("songs"); window.UpdateLayout(); await Drain();
                visible = Visuals(f.Module.View).OfType<MeowFilm.Desktop.LoadingSkeleton>().Where(s => s.IsVisible).ToArray();
                True(visible.Length > 0 && visible.All(s => s.Height == 34), "song page only shows row skeletons");
                f.Http.Release.TrySetResult();
                await SearchUntil(() => !f.Module.SearchSongsLoading && !f.Module.SearchPlaylistsLoading);
                window.UpdateLayout(); await Drain();
                True(!Visuals(f.Module.View).OfType<MeowFilm.Desktop.LoadingSkeleton>().Any(s => s.IsVisible), "settled results remove all skeletons");
            }
            finally { f.Http.Release.TrySetResult(); window.Content = null; window.Close(); }
        });
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
            True(f.Module.SearchSongsLoading && f.Module.SearchPlaylistsLoading,
                "both visible kinds publish loading state before either physical search settles");
            f.Http.Release.TrySetResult();
            await SearchUntil(() => f.Module.Songs.Count == 1 &&
                !f.Module.SearchSongsLoading && !f.Module.SearchPlaylistsLoading &&
                f.Module.CatalogSections.All(section => section.Items.Count == 1));
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
            Equal(accountReads + 2, f.Store.AccountReads);
            Equal(0, f.Runtime.Calls.Count);
        });
        yield return Case("duplicate visible search start shares one owner flight and one current account read after startup", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], [], automaticPlaylists: false);
            int accountReads = f.Store.AccountReads;
            var hold = f.Store.HoldNextAccountRead();
            var first = f.Search("单飞");
            Task duplicate = Task.CompletedTask;
            try
            {
                await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                duplicate = f.Module.SearchAsync("单飞", default);
                await Drain();
                Equal(accountReads + 1, f.Store.AccountReads);
                Equal(0, f.Http.SearchRequests);
                hold.Release();
                await Task.WhenAll(first, duplicate).WaitAsync(TimeSpan.FromSeconds(5));
                await SearchUntil(() => f.Module.Songs.Count == 1 &&
                    !f.Module.SearchSongsLoading && !f.Module.SearchPlaylistsLoading);
                Equal(1, f.Http.SearchRequests);
                Equal(accountReads + 1, f.Store.AccountReads);
            }
            finally { hold.Release(); await Task.WhenAll(first, duplicate); }
        });
        yield return Case("visible search resubmission uses page TTL while navigation only restores results", async () =>
        {
            var clock = new SearchClock();
            await using var f = await SearchScene.Create(clock);
            await f.Policy(["wy"], ["wy"]);
            await f.Search("缓存");
            await SearchUntil(() => f.Http.SearchRequests == 2 &&
                !f.Module.SearchSongsLoading && !f.Module.SearchPlaylistsLoading);
            await f.Module.SearchAsync("缓存", default);
            await SearchUntil(() => f.Module.CatalogSections.Count == 2 &&
                !f.Module.SearchSongsLoading && !f.Module.SearchPlaylistsLoading);
            Equal(2, f.Http.SearchRequests);
            await f.Module.NavigateAsync("favorites", default);
            clock.Advance(TimeSpan.FromMinutes(61));
            await f.Module.ReturnToSearchAsync();
            await f.Module.OpenSearchTabAsync("playlists");
            await f.Module.OpenSearchTabAsync("overview");
            Equal(2, f.Http.SearchRequests);
            await f.Module.SearchAsync("缓存", default);
            await SearchUntil(() => f.Http.SearchRequests == 4 &&
                !f.Module.SearchSongsLoading && !f.Module.SearchPlaylistsLoading);
            Equal(1, f.Module.Songs.Count);
            Equal(1, f.Module.CatalogSections.Single(s => s.Title == "歌单").Items.Count);
        });
        yield return Case("refresh keeps the selected search row while replacement results are pending", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], ["wy"]);
            await f.Search("保留选择");
            await SearchUntil(() => f.Module.Songs.Count == 1 &&
                !f.Module.SearchSongsLoading && !f.Module.SearchPlaylistsLoading);
            await f.Module.OpenSearchTabAsync("songs");
            var selected = f.Module.Songs[0];
            f.Module.SelectedSong = selected;
            string key = selected.SelectionKey;
            Equal(key, f.Module.SearchSelectedSong?.SelectionKey);

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Http.BeforeSearchReply = _ => gate.Task;
            int requests = f.Http.SearchRequests;
            var refresh = f.Module.RefreshCatalogAsync();
            try
            {
                await SearchUntil(() => f.Http.SearchRequests == requests + 1 &&
                    f.Module.SearchSongsLoading && !f.Module.SearchPlaylistsLoading);
                True(ReferenceEquals(selected, f.Module.SelectedSong),
                    "the existing selected row remains selected during refresh");
                Equal(key, f.Module.SearchSelectedSong?.SelectionKey);
            }
            finally { gate.TrySetResult(); }
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            await SearchUntil(() => !f.Module.SearchSongsLoading && !f.Module.SearchPlaylistsLoading);
            Equal(key, f.Module.SelectedSong?.SelectionKey);
            True(ReferenceEquals(selected, f.Module.SelectedSong),
                "same result identity reuses the selected row after refresh");
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
            True(!f.Http.Release.Task.IsCompleted && f.Module.SearchSongsLoading, "same live request");
            f.Http.Release.TrySetResult();
            await SearchUntil(() => f.Module.Songs.Count == 1 && !f.Module.SearchSongsLoading);
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
        yield return Case("search song snapshots append in place and merge existing sources", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], [], automaticPlaylists: false);
            f.Http.SearchResult = count => new { code = 200, result = new { songCount = 58,
                songs = SearchSongPage(count).ToArray() } };
            await f.Search("追加");
            await f.Module.OpenSearchTabAsync("songs");
            await SearchUntil(() => f.Module.Songs.Count == 30 && f.Module.CanLoadMoreSearchSongs);
            var oldRows = f.Module.Songs.ToArray();
            f.Module.SelectedSong = oldRows[0];
            string selectedKey = oldRows[0].SelectionKey;
            var oldSources = oldRows.Select(row => row.Raw.GetProperty("sources").Clone()).ToArray();
            var events = new List<(System.Collections.Specialized.NotifyCollectionChangedAction Action,
                int? NewStartingIndex, int? OldStartingIndex, int? NewCount)>();
            f.Module.Songs.CollectionChanged += (_, e) =>
                events.Add((e.Action, e.NewStartingIndex, e.OldStartingIndex, e.NewItems?.Count));
            await f.Module.MoreSearchAsync();
            await SearchUntil(() => f.Module.Songs.Count == 58 && !f.Module.SearchSongsLoading);
            Equal(2, f.Http.SearchRequests);
            for (int index = 2; index < oldRows.Length; index++)
                True(ReferenceEquals(oldRows[index], f.Module.Songs[index]),
                    "an unchanged row keeps its identity and position");
            Equal(selectedKey, f.Module.Songs[0].SelectionKey);
            True(!ReferenceEquals(oldRows[0], f.Module.Songs[0]), "a merged row is replaced in place");
            Equal(2, f.Module.Songs[0].Raw.GetProperty("sources").GetArrayLength());
            Equal(1, oldSources[0].GetArrayLength());
            True(!ReferenceEquals(oldRows[1], f.Module.Songs[1]), "the second merged row is replaced in place");
            Equal(2, f.Module.Songs[1].Raw.GetProperty("sources").GetArrayLength());
            for (int index = 0; index < oldRows.Length; index++)
                True(JsonElement.DeepEquals(oldSources[index],
                    oldRows[index].Raw.GetProperty("sources")),
                    "the original row payload remains the old snapshot");
            var nativeIds = f.Module.Songs.Take(2)
                .Select(row => row.Raw.GetProperty("sources").EnumerateArray()
                    .Select(source => source.GetProperty("musicInfo").GetProperty("songmid").GetString()))
                .ToArray();
            True(nativeIds[0].SequenceEqual(["1001", "2001"]),
                "the first merged row retains both native sources in order");
            True(nativeIds[1].SequenceEqual(["1002", "2002"]),
                "the second merged row retains both native sources in order");
            var changedIndexes = events.Where(change =>
                change.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Replace)
                .Select(change => change.OldStartingIndex).ToArray();
            Equal(2, changedIndexes.Length);
            True(changedIndexes.SequenceEqual([0, 1]), "the two merged rows are replaced in place");
            var added = events.Where(change =>
                change.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
                .ToArray();
            Equal(28, added.Length);
            for (int index = 0; index < added.Length; index++)
            {
                Equal(30 + index, added[index].NewStartingIndex);
                Equal(1, added[index].NewCount);
            }
            True(!events.Any(change =>
                change.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset),
                "a prefix append does not reset the song collection");
            True(ReferenceEquals(f.Module.SelectedSong, f.Module.Songs[0]),
                "the selected object belongs to the current collection");
            var currentCards = f.Module.CatalogSections.Single().Items;
            var mergedCard = currentCards[0];
            await f.Module.FavoriteCatalogCardAsync(mergedCard);
            var newCard = f.Module.CatalogSections.Single().Items[^1];
            await f.Module.QueueCatalogCardAsync(newCard);
            var favorites = await f.Call("music.library.detail", new { id = "favorites", offset = 0, limit = 100 });
            Equal(1, favorites.GetProperty("songs").GetArrayLength());
            Equal(2, favorites.GetProperty("songs")[0].GetProperty("sources").GetArrayLength());
            var queue = await f.Call("music.queue.get", new { offset = 0, limit = 100 });
            Equal(1, queue.GetProperty("entries").GetArrayLength());
            True(MusicSongFixture.NativeId(queue.GetProperty("entries")[0].GetProperty("song")) is "2030",
                "a newly appended song is available from its current card");
            int eventCount = events.Count;
            await f.Module.OpenSearchTabAsync("overview");
            await f.Module.OpenSearchTabAsync("songs");
            Equal(58, f.Module.Songs.Count); Equal(2, f.Http.SearchRequests);
            True(Equals(eventCount, events.Count), "tab switching does not touch Songs");
            True(!events.Any(change =>
                change.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset),
                "tab switching reuses the song rows");
        });
        yield return Case("unchanged search snapshots including loading keep collections stable", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], ["wy"]);
            f.Http.SearchResult = count => new { code = 200, result = new { songCount = 60,
                songs = SearchSongPage(1).ToArray(), playlistCount = 60,
                playlists = SearchPlaylistPage(1).ToArray() } };
            await f.Search("稳定");
            await SearchUntil(() => f.Module.Songs.Count == 30 && f.Module.CatalogSections.Count == 2 &&
                f.Module.CatalogSections.All(section => section.Items.Count == 6) &&
                !f.Module.SearchSongsLoading && !f.Module.SearchPlaylistsLoading);
            Equal(2, f.Http.SearchRequests);
            var songSection = f.Module.CatalogSections.Single(section => section.Title == "单曲");
            var playlistSection = f.Module.CatalogSections.Single(section => section.Title == "歌单");
            var songItems = songSection.Items.ToArray();
            var playlistItems = playlistSection.Items.ToArray();
            var events = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
            f.Module.Songs.CollectionChanged += (_, e) => events.Add(e.Action);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Http.BeforeSearchReply = _ => gate.Task;
            int eventCount = events.Count;
            try
            {
                await f.Module.OpenSearchTabAsync("songs");
                await f.Module.MoreSearchAsync();
                await SearchUntil(() => f.Http.SearchRequests == 3 && f.Module.SearchSongsLoading);
                await f.Module.OpenSearchTabAsync("overview");
                var overviewSongSection = f.Module.CatalogSections.Single(section => section.Title == "单曲");
                var overviewPlaylistSection = f.Module.CatalogSections.Single(section => section.Title == "歌单");
                gate.TrySetResult();
                await SearchUntil(() => f.Http.SearchRequests == 3 &&
                    !f.Module.SearchSongsLoading && !f.Module.SearchPlaylistsLoading);
                True(f.Module.CatalogSections.SelectMany(section => section.Items).Count() == 12,
                    "the repeated snapshot retains both overview result sets");
                True(Equals(0, events.Count), "an unchanged songs snapshot does not touch Songs");
                True(ReferenceEquals(overviewSongSection,
                    f.Module.CatalogSections.Single(section => section.Title == "单曲")),
                    "the song section is not replaced for an unchanged snapshot");
                True(ReferenceEquals(overviewPlaylistSection,
                    f.Module.CatalogSections.Single(section => section.Title == "歌单")),
                    "the playlist section is not replaced for an unchanged snapshot");
                True(f.Module.CatalogSections.Single(section => section.Title == "单曲").Items
                    .SequenceEqual(songItems), "song items remain unchanged");
                True(f.Module.CatalogSections.Single(section => section.Title == "歌单").Items
                    .SequenceEqual(playlistItems), "playlist items remain unchanged");
                await f.Module.OpenSearchTabAsync("songs");
                await f.Module.OpenSearchTabAsync("overview");
                Equal(3, f.Http.SearchRequests);
                True(Equals(eventCount, events.Count), "tab switching does not touch Songs");
            }
            finally { gate.TrySetResult(); }
        });
        yield return Case("first search results reset Songs and capture the current song-table scroll", async () =>
        {
            await using var store = ServiceStore();
            await using var runtime = new NativeMusicScriptTestRuntime();
            var http = new SearchHttp { SearchResult = count => new { code = 200, result =
                new { songCount = 30, songs = SearchSongPage(count).ToArray() } } };
            var inner = new MusicNativeApplication(store, runtime, http);
            var forwarder = new SearchSnapshotForwarder(inner);
            await using var module = new MusicCapability(forwarder, store, new PlaybackProbe(), new Dialogs());
            await module.InitializeAsync(default); await Drain();
            await SetSearchPolicy(module, ["wy"], []);
            await module.NavigateAsync("catalog-search", default);
            await module.SearchAsync("重排", default);
            await SearchUntil(() => module.Songs.Count == 30 && !module.SearchSongsLoading);
            var initial = (await forwarder.ExecuteAsync("music.search.get",
                new { id = forwarder.SearchIds["songs"] })).Clone();
            await module.OpenSearchTabAsync("songs");
            var view = (MusicView)module.View;
            var window = new Window { Content = view, Width = 1042, Height = 700,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                var grid = (DataGrid)view.FindName("SearchSongsGrid");
                var scroll = Visuals(grid).OfType<ScrollViewer>()
                    .Single(viewScroll => viewScroll.IsVisible && viewScroll.ScrollableHeight > 0);
                double target = Math.Min(scroll.ScrollableHeight, scroll.ViewportHeight * 1.5);
                scroll.ScrollToVerticalOffset(target); await Drain(); window.UpdateLayout();
                module.SelectedSong = module.Songs[15];
                await Drain(); window.UpdateLayout();
                double offset = scroll.VerticalOffset;
                True(offset > 0, "the song table starts at a real non-top offset");
                string selected = module.Songs[15].SelectionKey;
                int resets = 0;
                double? atReset = null;
                module.Songs.CollectionChanged += (_, e) =>
                {
                    if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Reset) return;
                    resets++;
                    atReset ??= scroll.VerticalOffset;
                };
                var reordered = SearchReorderedSnapshot(initial);
                forwarder.Publish(reordered);
                await SearchUntil(() => resets == 1 && module.SelectedSong is not null);
                Equal(selected, module.SelectedSong.SelectionKey);
                True(module.Songs.Any(song => ReferenceEquals(song, module.SelectedSong)),
                    "the selected row is projected into the current collection");
                window.UpdateLayout(); await Drain();
                True(Math.Abs(scroll.VerticalOffset - offset) < 1,
                    "the reset restores the actual song-table offset");
                True(atReset.HasValue && Math.Abs(atReset.Value - offset) < 1,
                    "the reset captures the actual scroll before replacing rows");
            }
            finally { window.Content = null; window.Close(); }
        });
        yield return Case("native search playlist back restores song and playlist tabs without refetch", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], ["wy"]);
            await f.Search("返回");
            await SearchUntil(() => !f.Module.SearchSongsLoading && !f.Module.SearchPlaylistsLoading &&
                f.Module.Songs.Count == 1 &&
                f.Module.CatalogSections.SelectMany(s => s.Items).Any(c => c.Kind == "playlist"));
            var song = f.Module.Songs.Single();
            await f.Module.OpenSearchTabAsync("playlists");
            await f.Module.OpenCatalogCardAsync(f.Module.CatalogSections.Single().Items.Single());
            True(f.Module.IsCatalogPlaylist && f.Module.Songs.Count == 1, "native playlist opened");
            int searches = f.Http.SearchRequests;
            Equal(2, searches);
            await f.Module.ReturnToSearchAsync();
            True(f.Module.IsSearchPlaylists && f.Module.SearchTab == "playlists", "same result tab");
            Equal(searches, f.Http.SearchRequests);
            await f.Module.OpenSearchTabAsync("songs");
            Equal(1, f.Module.Songs.Count); Equal(searches, f.Http.SearchRequests);
            True(ReferenceEquals(song, f.Module.Songs.Single()), "return reuses the completed search song row");
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
            True(!f.Module.SearchSongsLoading && !f.Module.SearchPlaylistsLoading,
                "actual loading drained");
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
            True(!Visuals(f.Module.View).OfType<DataGrid>().Any(), "overview does not mount the song table");
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
            f.Module.View.UpdateLayout();
            var grid = Visuals(f.Module.View).OfType<DataGrid>().Single();
            True(ReferenceEquals(grid, f.Module.View.FindName("SearchSongsGrid")) &&
                !Visuals(f.Module.View).OfType<MusicCatalogView>().Any(),
                "song tab restores the retained search song table");
            await f.Module.OpenSearchTabAsync("playlists"); await Drain();
            f.Module.View.UpdateLayout();
            True(!Visuals(f.Module.View).OfType<DataGrid>().Any() &&
                ReferenceEquals(catalog, Visuals(f.Module.View).OfType<MusicCatalogView>().Single()),
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

    private sealed class SearchSnapshotForwarder(MusicNativeApplication inner) : MusicApplicationForwarder(inner)
    {
        private event Action<JsonElement>? replayed;
        public override event Action<JsonElement>? SearchChanged
        {
            add { replayed += value; Inner.SearchChanged += value; }
            remove { replayed -= value; Inner.SearchChanged -= value; }
        }
        internal readonly Dictionary<string, string> SearchIds = new(StringComparer.Ordinal);

        public override async Task<JsonElement> ExecuteAsync(string method, object? parameters = null,
            CancellationToken cancellationToken = default)
        {
            var reply = await Inner.ExecuteAsync(method, parameters, cancellationToken);
            if (method == "music.search.start" && reply.TryGetProperty("id", out var id) &&
                reply.TryGetProperty("kind", out var kind) && id.ValueKind == JsonValueKind.String &&
                kind.ValueKind == JsonValueKind.String && reply.TryGetProperty("ok", out var ok) &&
                ok.ValueKind == JsonValueKind.True)
                SearchIds[kind.GetString()!] = id.GetString()!;
            return reply;
        }

        internal void Publish(JsonElement snapshot) => replayed?.Invoke(snapshot);
    }

    private static JsonElement SearchReorderedSnapshot(JsonElement snapshot)
    {
        var node = JsonNode.Parse(snapshot.GetRawText())!.AsObject();
        node["revision"] = snapshot.GetProperty("revision").GetInt64() + 1;
        node["items"] = new JsonArray(node["items"]!.AsArray().Reverse().Select(item => item!.DeepClone()).ToArray());
        return JsonSerializer.SerializeToElement(node);
    }

    private static IEnumerable<object> SearchSongPage(int requestIndex)
    {
        if (requestIndex is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(requestIndex));
        for (int index = 0; index < 30; index++)
        {
            int ordinal = requestIndex == 1 ? index : index < 2 ? index : index + 28;
            int id = requestIndex == 1 ? 1001 + index : 2001 + index;
            yield return SearchHttp.WyTrack(id, "歌曲" + ordinal.ToString("D2"),
                "歌手" + ordinal, "专辑" + ordinal);
        }
    }

    private static IEnumerable<object> SearchPlaylistPage(int requestIndex)
    {
        int start = 500 + (requestIndex - 1) * 30;
        for (int index = 0; index < 30; index++)
            yield return new
            {
                id = (start + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                name = "歌单" + requestIndex + "-" + index,
                trackCount = 1
            };
    }

    private static async Task SetSearchPolicy(MusicCapability module, string[] songs, string[] playlists)
    {
        var songSet = songs.ToHashSet(StringComparer.Ordinal);
        var playlistSet = playlists.ToHashSet(StringComparer.Ordinal);
        foreach (var row in module.SongSearch.Platforms.ToArray())
        {
            await module.SetSearchPlatformEnabledAsync("songs", row, songSet.Contains(row.Platform));
            await Until(() => module.CanEditSearchPreferences);
        }
        foreach (var row in module.PlaylistSearch.Platforms.ToArray())
        {
            await module.SetSearchPlatformEnabledAsync("playlists", row, playlistSet.Contains(row.Platform));
            await Until(() => module.CanEditSearchPreferences);
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
        private SearchScene(TimeProvider? clock = null, IRuntimeClient? scriptRuntime = null)
        {
            App = new(Store, scriptRuntime ?? Runtime, Http, null, null, clock);
            Module = new(App, Store, new PlaybackProbe(), new Dialogs());
        }
        internal static async Task<SearchScene> Create(TimeProvider? clock = null, IRuntimeClient? scriptRuntime = null)
        {
            var f = new SearchScene(clock, scriptRuntime);
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
    private sealed class SearchStore(IDocumentStore inner) : IDocumentStore
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

        public async Task<DocumentSnapshot> ReadAsync(string documentNamespace,
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
            if (FailNextWrite) { FailNextWrite = false; throw new DocumentStoreException(DocumentStoreError.Storage); }
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
        internal Func<int, Task>? BeforeSearchReply;
        internal static object Track(int id = 1001) => WyTrack(id, "合成歌曲", "歌手", "专辑");
        internal static object WyTrack(int id, string name, string artist, string album) => new { id, name, dt = 180000, fee = 0,
            ar = new[] { new { id = 1, name = artist } }, al = new { id = 2, name = album, picUrl = "" },
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
                if (BeforeSearchReply is { } before) await before(count);
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
