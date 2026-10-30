using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Recommendations;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

// Real WPF -> public application seam -> real Native catalog/Library/CAS.
// Typed platform, account and script-resolution fixtures never contact an upstream.
// This group is independent of the existing five application/UI test groups.
public static class MusicCatalogPersonalTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("navigation and WPF actions are real entry points", Entries);
        foreach (string platform in new[] { "wy", "tx", "kg" })
            yield return Case("personal directory explicit account and complete cursor " + platform, () => DirectoryPages(platform));
        yield return Case("directory filters nullable totals empty intermediate page and failed refresh", DirectoryFilters);
        yield return Case("KG locator 30-page membership selection survives global sorting", KgMembers);
        yield return Case("QQ favorites complete save never stores only visible page", TxSave);
        yield return Case("KG UI can navigate beyond the old hundred-page limit", LongKg);
        yield return Case("updated private locator cannot restore an old cached revision", LocatorRefresh);
        yield return Case("private tail failure preserves display and durable queue", TailFailure);
        yield return Case("duplicate member and changed snapshot cannot publish second page", PageDefects);
        yield return Case("empty complete playlist may save but not play", EmptyPlaylist);
        yield return Case("daily style full batch saves and replaces only on explicit actions", DailyActions);
        yield return Case("daily entries read complete members only when the expanded display needs them", DailyEntrySummaries);
        yield return Case("channel daily entries do not expand members during page loading", ChannelDailySummaries);
        yield return Case("supplied and unavailable daily summaries never trigger extra reads", SuppliedDailySummaries);
        yield return Case("empty or failed daily summaries keep the page and do not retry on hover", FailedDailySummaries);
        yield return Case("retired daily summary cannot overwrite a newer page and close drains its receipt", RetiredDailySummary);
        yield return Case("daily preview expiry does not invalidate unrelated home content", DailySummaryExpiry);
        yield return Case("cancel after admitted batch commit waits receipt and does not autoplay", CancelledBatchCommitReceipt);
        yield return Case("FM modes rule cards and explicit next batch never auto-play", FmActions);
        yield return Case("similar native IDs album outlet and unsupported actions", SimilarAlbum);
        yield return Case("heartbeat and queue recommendations retain explicit context", RecommendationContext);
        yield return Case("expired batch blocks actions and navigation rereads instead of stale restore", Expiry);
        yield return Case("account invalidation rejects late response and stale cards", AccountInvalidation);
        yield return Case("private paging reuses first account observation and explicit read is authoritative", AccountReadReuse);
        yield return Case("duplicate private navigation shares one request and isolates caller cancellation", DuplicateNavigation);
        yield return Case("last cancelled catalog subscriber detaches a noncooperative read and permits retry", LastSubscriberCancellation);
        yield return Case("private account observation preserves the authoritative read error", AccountReadFailure);
        yield return Case("account read detects external generation replacement", AccountRead);
        yield return Case("rapid FM changes ignore an old receipt and retain newest options", RapidOptions);
        yield return Case("cancelling private preparation discards intent without commit", CancelPreparation);
        yield return Case("queue CAS conflict cannot autoplay or replay consumed stage", CasConflict);
        yield return Case("unknown batch save retains actual persistence without automatic retry", UnknownSave);
        yield return Case("unsupported platform missing account and device error remain business states", Errors);
        yield return Case("partial recommendation batch cannot replace successful content", PartialBatch);
        yield return Case("cached navigation detaches a retired noncooperative catalog read", CachedNavigationRetiresRead);
        yield return Case("ordinary channel cursor cache refresh and cancellation remain intact", LegacyPaging);
        yield return Case("closing while personal read is delayed cannot revive content", CloseRead);
    }

    private static TestCase Case(string name, Func<Task> run) => new("Music personal UI " + name, () => OnSta(run));
    private static JsonElement E(object value) => JsonSerializer.SerializeToElement(value);
    private static string S(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    private static long N(JsonElement value, string name) => value.GetProperty(name).GetInt64();
    private static void Check(bool value, string message = "assertion failed") { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}"); }
    private static Task Drain() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private static async Task Until(Func<bool> done)
    {
        for (int i = 0; i < 1000 && !done(); i++) { await Drain(); await Task.Delay(1); }
        Check(done(), "bounded UI checkpoint");
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Visuals<T>(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
    private static void Layout(FrameworkElement view)
    { view.Measure(new(860, 720)); view.Arrange(new(0, 0, 860, 720)); view.UpdateLayout(); }
    private static MusicCatalogCard FirstSong(Fixture f) => f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.Song is not null);
    private static async Task Entries()
    {
        await using var f = await Fixture.Create();
        foreach (string route in new[] { "catalog-home", "catalog-channels", "catalog-personal", "catalog-daily", "catalog-fm" })
            Check(f.Module.Navigation.Any(n => n.Route == route), "navigation " + route);
        await f.Module.NavigateAsync("catalog-daily", default); Layout(f.Module.View);
        Equal(0, f.Store.LibraryWrites); Equal(0, f.Output.Opens);
        var buttons = Visuals<Button>(f.Module.View).ToArray();
        var save = buttons.Single(b => Equals(b.Content, "保存到本地歌单"));
        Check(save.IsEnabled && save.IsVisible == f.Module.View.IsVisible, "batch save control");
        Check(Visuals<ComboBox>(f.Module.View).Any(c => AutomationProperties.GetName(c) == "每日推荐类型"), "bound daily picker");
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => f.Module.Status == "已保存到本地歌单");
        Equal(3, (await f.Detail()).GetProperty("songs").GetArrayLength());
        Check(f.App.Log.All(c => !c.Method.Contains("rpc", StringComparison.Ordinal)), "ordinary UI is native");
    }
    private static async Task DirectoryPages(string platform)
    {
        await using var f = await Fixture.Create(); f.Select(platform);
        await f.Module.NavigateAsync("catalog-personal", default);
        Check(f.Module.IsCatalogPersonal && f.Module.CanCatalogNext, f.Module.Status);
        Check(f.Module.CatalogAccountText.Contains("Fixture " + platform, StringComparison.Ordinal), "explicit platform account");
        var card = f.Module.CatalogSections.Single().Items.Single();
        Equal("playlist", card.Kind);
        if (platform != "wy") Check(card.Locator is not null, "private locator retained");
        await f.Module.CatalogPageAsync(1);
        var last = f.Platform.Last<MusicPersonalPlaylistsRequest>();
        Equal(platform, last.Platform); Equal(2, last.Page); Equal(30, last.Limit);
        Check(last.Continuation!.Value.GetProperty("provider").GetProperty("opaque").GetString() == "retained", "opaque cursor not reconstructed");
        Equal("第 2 页", f.Module.CatalogPageText); Check(!f.Module.CanCatalogNext);
        await f.Module.CatalogPageAsync(-1);
        Equal("第 1 页", f.Module.CatalogPageText);
        var count = f.Platform.Requests.Count;
        await f.Module.NavigateAsync("favorites", default);
        await f.Module.NavigateAsync("catalog-personal", default);
        Equal(count, f.Platform.Requests.Count); Equal(0, f.Store.LibraryWrites);
    }
    private static async Task DirectoryFilters()
    {
        await using var f = await Fixture.Create(); f.Select("tx");
        await f.Module.NavigateAsync("catalog-personal", default);
        f.Module.CatalogPersonalKind = "subscribed"; f.Platform.EmptyDirectoryFirst = true;
        await f.Module.CatalogOptionsChangedAsync();
        Equal(MusicPersonalListKind.Subscribed, f.Platform.Last<MusicPersonalPlaylistsRequest>().Kind);
        Check(f.Module.CanCatalogNext, "empty intermediate page with advancing provider cursor");
        await f.Module.CatalogPageAsync(1);
        var before = f.Module.CatalogSections.ToArray();
        f.App.FailNext("music.catalog.personalPlaylists", "NETWORK_ERROR");
        await f.Module.RefreshCatalogAsync();
        Equal("第 2 页", f.Module.CatalogPageText); Check(before.SequenceEqual(f.Module.CatalogSections));
        var p = f.App.Last("music.catalog.personalPlaylists");
        Check(p.GetProperty("refresh").GetBoolean() && !p.TryGetProperty("continuation", out _), "refresh from first page");
        Equal(3, f.Module.CatalogPersonalKinds.Length);
        f.Select("kg"); await f.Module.ChangeCatalogPlatformAsync();
        Equal("all", f.Module.CatalogPersonalKind); Equal(1, f.Module.CatalogPersonalKinds.Length);
        Equal(MusicPersonalListKind.All, f.Platform.Last<MusicPersonalPlaylistsRequest>().Kind);
    }
    private static async Task KgMembers()
    {
        await using var f = await Fixture.Create(); f.Select("kg");
        await f.OpenPersonalPlaylist();
        Equal(30, f.Module.Songs.Count); Equal(30, f.Platform.Last<MusicPlaylistRequest>().Limit);
        await f.Module.CatalogPageAsync(1);
        var member = f.Module.CatalogSections.Single().Items.Single(c => c.MemberId == "1031");
        Check(member.ActionId == FirstId("kg"), "equal hash is a distinct member");
        await f.Module.OpenCatalogCardAsync(member);
        var commit = f.App.Last("music.catalog.playlist.commit");
        Equal("1031", S(commit, "memberId")); Check(!commit.TryGetProperty("index", out _));
        var queue = await f.Queue();
        Equal(65L, N(queue, "total")); Equal("1031", S(queue.GetProperty("current").GetProperty("song").GetProperty("source_data"), "fileid"));
        var ids = queue.GetProperty("entries").EnumerateArray().Select(e => S(e.GetProperty("song").GetProperty("source_data"), "fileid")).ToArray();
        Equal("1064", ids[0]); Check(ids.Contains("1000") && ids.Contains("1031"));
        Check(f.Platform.Requests.OfType<MusicPlaylistRequest>().All(r => r.Limit == 30 && r.Personal?.ListId == "42"), "every page keeps locator");
        Equal(1, f.Output.Opens);
    }
    private static async Task TxSave()
    {
        await using var f = await Fixture.Create(); f.Select("tx"); f.Platform.Total = 235;
        await f.OpenPersonalPlaylist(); Equal(100, f.Module.Songs.Count);
        await f.Module.SaveCatalogPlaylistAsync();
        var detail = await f.Detail(); Equal(235L, N(detail, "total"));
        var all = detail.GetProperty("songs").EnumerateArray().ToList();
        foreach (int offset in new[] { 100, 200 }) all.AddRange((await f.Detail(offset)).GetProperty("songs").EnumerateArray());
        Equal(235, all.Count); Equal("MID235", MusicSongFixture.NativeId(all[^1]));
        var prepare = f.App.Last("music.catalog.playlist.prepare");
        Equal("favorites", S(prepare, "id")); Equal("201", S(prepare.GetProperty("personal"), "listId"));
        Equal(1, f.Store.LibraryWrites); Check(f.Platform.Requests.OfType<MusicPlaylistRequest>().Count(r => r.Refresh) == 6, "QQ full reread");
    }
    private static async Task LongKg()
    {
        await using var f = await Fixture.Create(); f.Select("kg"); f.Platform.Total = 3001;
        await f.OpenPersonalPlaylist();
        for (int i = 1; i <= 100; i++) { Check(f.Module.CanCatalogNext, "KG next at page " + i); await f.Module.CatalogPageAsync(1); }
        Equal("第 101 页", f.Module.CatalogPageText); Equal(3001, f.Module.Songs.Count); Check(!f.Module.CanCatalogNext);
        Equal(0, f.Store.LibraryWrites);
    }
    private static async Task LocatorRefresh()
    {
        await using var f = await Fixture.Create(); f.Select("kg");
        await f.OpenPersonalPlaylist();
        await f.Module.NavigateAsync("catalog-personal", default);
        f.Platform.LocatorVersion = "8"; await f.Module.RefreshCatalogAsync();
        await f.Module.OpenCatalogCardAsync(f.Module.CatalogSections.Single().Items.Single());
        Equal("8", f.Platform.Last<MusicPlaylistRequest>().Personal!.Version);
        Check(f.Module.IsCatalogPlaylist && f.Module.Songs.Count == 30, f.Module.Status);
    }
    private static async Task TailFailure()
    {
        await using var f = await Fixture.Create(); f.Select("kg");
        await f.OpenPersonalPlaylist(); var first = f.Module.Songs[0];
        await f.App.Library.ExecuteAsync("music.queue.replace", new { songs = new[] { Song("kg", 90) }, index = 0 });
        int writes = f.Store.LibraryWrites;
        f.Platform.PlaylistChange = r => r.Page == 3 ? Edit(Page(r, f.Platform.Total), n => n["items"] = new JsonArray()) : Page(r, f.Platform.Total);
        await f.Module.PlayCatalogPlaylistAsync();
        Equal(writes, f.Store.LibraryWrites); Equal(1L, N(await f.Queue(), "total"));
        Check(ReferenceEquals(first, f.Module.Songs[0])); Equal(0, f.Output.Opens);
        Check(!f.App.Log.Any(c => c.Method == "music.catalog.playlist.commit"), "no incomplete token commit");
    }
    private static async Task PageDefects()
    {
        foreach (string defect in new[] { "snapshot", "duplicate" })
        {
            await using var f = await Fixture.Create(); f.Select("kg"); await f.OpenPersonalPlaylist();
            var before = f.Module.Songs.ToArray();
            f.Platform.PlaylistChange = r => Edit(Page(r, f.Platform.Total), n =>
            {
                if (defect == "snapshot") n["snapshot"] = new string('c', 64);
                else n["memberIds"]![0] = "1000";
            });
            await f.Module.CatalogPageAsync(1);
            Equal("第 1 页", f.Module.CatalogPageText); Check(before.SequenceEqual(f.Module.Songs));
            Check(f.Module.Status.Length > 0); Equal(0, f.Store.LibraryWrites);
        }
    }
    private static async Task EmptyPlaylist()
    {
        await using var f = await Fixture.Create(); f.Platform.Total = 0;
        await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
        Check(f.Module.CanCatalogAction && !f.Module.CanPlayCatalogPlaylist);
        await f.Module.SaveCatalogPlaylistAsync(); Equal(0L, N(await f.Detail(), "total"));
    }
    private static async Task DailyActions()
    {
        await using var f = await Fixture.Create();
        await f.Module.NavigateAsync("catalog-daily", default);
        Equal(MusicDailyBranch.Default, f.Platform.Last<MusicDailyRequest>().Branch);
        f.Module.CatalogDailyBranch = "style"; await f.Module.CatalogOptionsChangedAsync();
        Equal(MusicDailyBranch.Style, f.Platform.Last<MusicDailyRequest>().Branch);
        Equal(0, f.Store.LibraryWrites); Equal(0, f.Output.Opens);
        await f.Module.SaveCatalogPlaylistAsync(); Equal(3L, N(await f.Detail(), "total"));
        await f.Module.PlayCatalogPlaylistAsync();
        Equal(3L, N(await f.Queue(), "total")); Equal(1, f.Output.Opens);
        Check(!f.App.Log.Any(c => c.Method == "music.catalog.playlist.prepare"), "batch is not a fabricated cloud playlist");
        Check(!f.App.Last("music.queue.replace").TryGetProperty("expectedVersion", out _), "ordinary native envelope, not the full-list overload");
        Equal("daily", S(f.App.Last("music.queue.replace").GetProperty("context").GetProperty("queueSource"), "kind"));
    }
    private static async Task CancelledBatchCommitReceipt()
    {
        await using var f = await Fixture.Create();
        await f.Module.NavigateAsync("catalog-daily", default);
        var held = f.App.HoldNext("music.queue.replace");
        var play = f.Module.PlayCatalogPlaylistAsync();
        await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Module.CancelCatalog(); await Task.Delay(35); await Drain();
        Check(!play.IsCompleted, "admitted batch commit was reported cancelled before its receipt settled");
        held.Release(); await play;
        Equal(1, f.App.Log.Count(call => call.Method == "music.queue.replace"));
        Equal(3L, N(await f.Queue(), "total"));
        Equal(0, f.Output.Opens);
    }
    private static object DailyEntry(string id = "daily-default", string branch = "default", bool available = true) => new
    {
        id, key = id, name = "每日推荐", subtitle = "符合你口味的新鲜好歌",
        previewSubtitle = "每日推荐 | 从「合成歌曲」听起", dayOfMonth = 29, presentation = "preview",
        available, errorCode = available ? "" : "AUTH_REQUIRED", action = new { type = "daily", branch }
    };
    private static async Task DailyEntrySummaries()
    {
        await using var f = await Fixture.Create();
        f.Platform.DailyCount = 8;
        f.Platform.HomeEntries = [DailyEntry(), DailyEntry("daily-copy"), DailyEntry("daily-style", "style"),
            new { id = "500", name = "普通歌单", presentation = "preview", action = new { type = "playlist", id = "500" } }];
        await f.Module.NavigateAsync("catalog-home", default);
        var cards = f.Module.CatalogSections.Single().Items.Where(card => card.Kind == "daily").ToArray();
        Equal(3, cards.Length);
        Check(cards.All(card => !card.HasPreview), "an entry is not an instruction to preload its members");
        Check(cards.All(card => card.DayOfMonth == 29 && card.ExpandedSubtitle.Contains("合成歌曲", StringComparison.Ordinal)));
        Equal(0, f.Platform.Requests.OfType<MusicDailyRequest>().Count());
        f.App.TransformNext("music.catalog.daily", reply => {
            Equal(8, reply.GetProperty("items").GetArrayLength()); return reply; });
        foreach (var card in new[] { cards[0], cards[2] })
        {
            Check((await f.Module.ReadCatalogCardPreviewAsync(card, default)).SequenceEqual(["1 Track 1", "2 Track 2", "3 Track 3"]));
            Check((await f.Module.ReadCatalogCardPreviewAsync(card, default)).SequenceEqual(["1 Track 1", "2 Track 2", "3 Track 3"]));
        }
        Equal(2, f.Platform.Requests.OfType<MusicDailyRequest>().Count());
        Check(f.Platform.Requests.OfType<MusicDailyRequest>().Select(request => request.Branch)
            .ToHashSet().SetEquals([MusicDailyBranch.Default, MusicDailyBranch.Style]));
        Check(!f.Platform.Requests.OfType<MusicPlaylistRequest>().Any(), "ordinary playlists stay lazy");
        Check(!f.Platform.Requests.OfType<MusicFmRequest>().Any(), "summary does not start a radio session");
        Equal(0, f.Store.LibraryWrites); Equal(0, f.Output.Opens); Equal(0L, N(await f.Queue(), "total"));
        CheckNoDisplayEnrichment(f);
        int calls = f.Platform.Requests.Count;
        await f.Module.NavigateAsync("favorites", default);
        await f.Module.NavigateAsync("catalog-home", default);
        Equal(calls, f.Platform.Requests.Count);
        await f.Module.RefreshCatalogAsync();
        Equal(2, f.Platform.Requests.OfType<MusicDailyRequest>().Count());
        CheckNoDisplayEnrichment(f);
    }
    private static async Task ChannelDailySummaries()
    {
        await using var f = await Fixture.Create();
        f.Platform.ChannelEntries = [DailyEntry()];
        await f.Module.NavigateAsync("catalog-channel/wy/rock", default);
        var card = f.Module.CatalogSections.Single().Items.Single();
        Equal(0, card.PreviewSongs.Count);
        Equal(0, f.Platform.Requests.OfType<MusicDailyRequest>().Count());
        Equal(3, (await f.Module.ReadCatalogCardPreviewAsync(card, default)).Count);
        Equal(1, f.Platform.Requests.OfType<MusicDailyRequest>().Count());
        Equal(0, f.Store.LibraryWrites); Equal(0, f.Output.Opens);
        CheckNoDisplayEnrichment(f);
    }
    private static async Task SuppliedDailySummaries()
    {
        await using var f = await Fixture.Create();
        var supplied = Edit(E(DailyEntry()), row => row["preview"] = JsonSerializer.SerializeToNode(new[] {
            new { title = "已有曲目", artist = "合成艺人", cover = "", rank = 1, lastRank = 0 } }));
        f.Platform.HomeEntries = [supplied, DailyEntry("daily-unavailable", available: false)];
        await f.Module.NavigateAsync("catalog-home", default);
        Check(!f.Platform.Requests.OfType<MusicDailyRequest>().Any());
        var card = f.Module.CatalogSections.Single().Items.First();
        Check((await f.Module.ReadCatalogCardPreviewAsync(card, default)).SequenceEqual(["1 已有曲目"]));
        Equal(1, f.Module.DisplayCatalogSections.Single().Items.Count);
        Equal(0, f.Store.LibraryWrites); Equal(0, f.Output.Opens);
    }
    private static async Task FailedDailySummaries()
    {
        foreach (bool empty in new[] { false, true })
        {
            await using var f = await Fixture.Create();
            f.Platform.HomeEntries = [DailyEntry(),
                new { id = "500", name = "普通歌单", presentation = "preview", action = new { type = "playlist", id = "500" } }];
            if (empty)
                f.App.TransformNext("music.catalog.daily", reply => Edit(reply, row => {
                    row["items"] = new JsonArray(); row["count"] = 0; }));
            else f.App.FailNext("music.catalog.daily", "UPSTREAM_ERROR");
            await f.Module.NavigateAsync("catalog-home", default);
            Equal("", f.Module.Status); Equal(2, f.Module.CatalogSections.Single().Items.Count);
            var daily = f.Module.CatalogSections.Single().Items.First();
            Check(!daily.HasPreview, "no fabricated songs");
            Equal(0, f.App.Log.Count(call => call.Method == "music.catalog.daily"));
            Equal(0, (await f.Module.ReadCatalogCardPreviewAsync(daily, default)).Count);
            Equal(0, (await f.Module.ReadCatalogCardPreviewAsync(daily, default)).Count);
            Equal(1, f.App.Log.Count(call => call.Method == "music.catalog.daily"));
            await f.Module.RefreshCatalogAsync();
            Equal(1, f.App.Log.Count(call => call.Method == "music.catalog.daily"));
            Equal(3, (await f.Module.ReadCatalogCardPreviewAsync(f.Module.CatalogSections.Single().Items.First(), default)).Count);
            Equal(2, f.App.Log.Count(call => call.Method == "music.catalog.daily"));
            Equal(0, f.Store.LibraryWrites); Equal(0, f.Output.Opens);
        }
    }
    private static async Task RetiredDailySummary()
    {
        await using var f = await Fixture.Create();
        f.Platform.HomeEntries = [DailyEntry()];
        await f.Module.NavigateAsync("catalog-home", default);
        var card = f.Module.CatalogSections.Single().Items.Single();
        var held = f.App.HoldNext("music.catalog.daily");
        var old = f.Module.ReadCatalogCardPreviewAsync(card, default);
        await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Module.CancelCatalog();
        await f.Module.NavigateAsync("favorites", default);
        await f.Module.NavigateAsync("catalog-home", default);
        var published = f.Module.CatalogSections.Single();
        Equal(3, (await f.Module.ReadCatalogCardPreviewAsync(published.Items.Single(), default)).Count);
        var closing = f.Module.PrepareShutdownAsync();
        await Task.Delay(25); await Drain();
        Check(!closing.IsCompleted && !old.IsCompleted, "close retains the noncooperative physical summary receipt");
        held.Release(); Equal(0, (await old).Count); await closing;
        Check(ReferenceEquals(published, f.Module.CatalogSections.Single()), "late summary must not replace the new page");
        Equal(0, f.Store.LibraryWrites); Equal(0, f.Output.Opens);
    }
    private static async Task DailySummaryExpiry()
    {
        await using var f = await Fixture.Create();
        f.Platform.HomeEntries = [DailyEntry()];
        long expiry = 0;
        f.App.TransformNext("music.catalog.daily", reply => Edit(reply, row => {
            expiry = DateTimeOffset.UtcNow.AddMilliseconds(300).ToUnixTimeMilliseconds(); row["expiresAt"] = expiry; }));
        await f.Module.NavigateAsync("catalog-home", default);
        Equal(0, f.Platform.Requests.OfType<MusicDailyRequest>().Count());
        Equal(3, (await f.Module.ReadCatalogCardPreviewAsync(f.Module.CatalogSections.Single().Items.Single(), default)).Count);
        await f.Module.NavigateAsync("favorites", default);
        await Until(() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= expiry);
        await f.Module.NavigateAsync("catalog-home", default);
        Equal(1, f.Platform.Requests.OfType<MusicHomeRequest>().Count());
        Equal(1, f.Platform.Requests.OfType<MusicDailyRequest>().Count());
        Equal(0, f.Store.LibraryWrites); Equal(0, f.Output.Opens);
    }
    private static void CheckNoDisplayEnrichment(Fixture f) =>
        Check(!f.App.Log.Any(call => call.Method.StartsWith("music.official.", StringComparison.Ordinal) ||
            call.Method is "music.online.resolve" or "music.search.start" or "music.catalog.playlist.prepare"),
            "displaying supplied metadata never resolves audio, discovers other platforms or prepares a full playlist");
    private static async Task FmActions()
    {
        await using var f = await Fixture.Create();
        await f.Module.NavigateAsync("catalog-fm", default);
        f.Module.CatalogFmMode = "scene"; f.Module.CatalogFmScene = "FOCUS"; await f.Module.CatalogOptionsChangedAsync();
        var fm = f.Platform.Last<MusicFmRequest>(); Equal(MusicFmMode.Scene, fm.Mode); Equal("FOCUS", fm.Submode);
        await f.Module.RefreshCatalogAsync(); Equal(0, f.Store.LibraryWrites); Equal(0, f.Output.Opens);
        f.Platform.HomeActions = [new { id = "rule", name = "规则 FM", available = true, action = new { type = "fm", ruleId = "71" } }];
        await f.Module.NavigateAsync("catalog-home", default);
        await f.Module.OpenCatalogCardAsync(f.Module.CatalogSections.Single().Items.Single());
        fm = f.Platform.Last<MusicFmRequest>(); Equal("71", fm.RuleId); Check(!fm.Preview);
        await f.Module.PlayCatalogPlaylistAsync(); Equal(3L, N(await f.Queue(), "total"));
    }
    private static async Task SimilarAlbum()
    {
        await using var f = await Fixture.Create();
        await f.Module.NavigateAsync("catalog-daily", default);
        var first = FirstSong(f); Check(first.CanSimilar && first.CanAlbum && first.ActionId != first.Id);
        await f.Module.SimilarCatalogCardAsync(first);
        Equal(first.ActionId, f.Platform.Last<MusicSimilarRequest>().SourceIds.Single());
        Equal(1, f.Store.LibraryWrites); Equal(1, f.Output.Opens);
        var similarQueue = await f.Queue();
        Equal(3L, N(similarQueue, "total"));
        Equal(FirstId("wy"), MusicSongFixture.NativeId(
            similarQueue.GetProperty("current").GetProperty("song")));
        var similar = FirstSong(f); await f.Module.AlbumCatalogCardAsync(similar);
        Equal("70", f.Platform.Last<MusicAlbumRequest>().Id); Equal("合成专辑", f.Module.Heading);
        await f.Module.SaveCatalogPlaylistAsync(); Equal(3L, N(await f.Detail(), "total"));
        f.Platform.HomeActions = [
            new { id = "bad", name = "不支持", available = true, action = new { type = "video", id = "1" } },
            new { id = "daily", name = "不可用推荐", available = false, errorCode = "AUTH_REQUIRED", action = new { type = "daily" } },
            new { id = "album", name = "专辑", available = true, action = new { type = "album", id = "70" } }];
        await f.Module.NavigateAsync("catalog-home", default);
        var cards = f.Module.CatalogSections.Single().Items;
        Check(!cards[0].CanOpen && !cards[1].CanOpen && cards[2].CanOpen);
        int calls = f.Platform.Requests.Count; await f.Module.OpenCatalogCardAsync(cards[0]); Equal(calls, f.Platform.Requests.Count);
        await f.Module.OpenCatalogCardAsync(cards[2]); Equal("70", f.Platform.Last<MusicAlbumRequest>().Id);
    }
    private static async Task RecommendationContext()
    {
        await using var f = await Fixture.Create();
        await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
        var song = FirstSong(f); await f.Module.HeartbeatCatalogCardAsync(song);
        var heart = f.Platform.Last<MusicHeartbeatRequest>();
        Equal("500", heart.PlaylistId); Equal(song.ActionId, heart.SongId); Check(!heart.FromHome);
        Equal(1, f.Store.LibraryWrites); Equal(1, f.Output.Opens);
        var heartbeatQueue = await f.Queue();
        Equal(3L, N(heartbeatQueue, "total"));
        Equal(FirstId("wy"), MusicSongFixture.NativeId(
            heartbeatQueue.GetProperty("current").GetProperty("song")));
        await f.Module.CatalogQueueRecommendationsAsync();
        var queue = f.Platform.Last<MusicQueueRecommendationsRequest>();
        Equal(3, queue.SongIds.Count); Check(queue.SongIds.Contains(queue.CurrentPlaySongId));
        Equal(1, f.Store.LibraryWrites); Equal(1, f.Output.Opens);
    }
    private static async Task Expiry()
    {
        await using var f = await Fixture.Create(); f.Platform.Expired = true;
        await f.Module.NavigateAsync("catalog-daily", default);
        await f.Module.PlayCatalogPlaylistAsync(); Equal(0, f.Store.LibraryWrites); Equal("内容已过期，请刷新", f.Module.Status);
        int requests = f.Platform.Requests.Count;
        await f.Module.NavigateAsync("favorites", default); f.Platform.Expired = false;
        await f.Module.NavigateAsync("catalog-daily", default);
        Check(f.Platform.Requests.Count > requests); await f.Module.PlayCatalogPlaylistAsync(); Equal(1, f.Output.Opens);
    }
    private static async Task AccountInvalidation()
    {
        await using var f = await Fixture.Create(); await f.Module.NavigateAsync("catalog-daily", default);
        var stale = FirstSong(f);
        var held = f.App.HoldNext("music.catalog.daily");
        var loading = f.Module.RefreshCatalogAsync(); await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.App.AccountGeneration++; f.App.Catalog.Invalidate("wy"); f.Module.RetireCatalogSession();
        held.Release(); await loading;
        Equal(0, f.Module.CatalogSections.Count); Equal(0, f.Module.Songs.Count);
        int calls = f.App.Log.Count; await f.Module.OpenCatalogCardAsync(stale);
        Equal(calls, f.App.Log.Count); Equal(0, f.Store.LibraryWrites);
    }
    private static async Task AccountReadReuse()
    {
        await using var f = await Fixture.Create();
        await f.Module.NavigateAsync("catalog-personal", default);
        Equal(1, f.App.Log.Count(call => call.Method == "music.accounts.list"));
        await f.Module.CatalogPageAsync(1);
        Equal(1, f.App.Log.Count(call => call.Method == "music.accounts.list"));
        await f.Module.RefreshCatalogAsync();
        Equal(1, f.App.Log.Count(call => call.Method == "music.accounts.list"));
        await f.Module.ReadCatalogAccountAsync();
        Equal(2, f.App.Log.Count(call => call.Method == "music.accounts.list"));
    }
    private static async Task DuplicateNavigation()
    {
        await using var f = await Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        var held = f.App.HoldNext("music.catalog.personalPlaylists");
        var first = f.Module.NavigateAsync("catalog-personal", cancellation.Token);
        await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = f.Module.NavigateAsync("catalog-personal", default);
        await Drain();
        Equal(1, f.App.Log.Count(call => call.Method == "music.accounts.list"));
        Equal(1, f.App.Log.Count(call => call.Method == "music.catalog.personalPlaylists"));
        cancellation.Cancel();
        try
        {
            await first;
            throw new InvalidOperationException("cancelled subscriber completed successfully");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        await Drain();
        Check(!second.IsCompleted, "remaining subscriber must keep the shared physical request");
        held.Release();
        await second;
        Equal(1, f.App.Log.Count(call => call.Method == "music.catalog.personalPlaylists"));
        Check(f.Module.CatalogSections.Count > 0, "shared private result was not published");
    }
    private static async Task LastSubscriberCancellation()
    {
        await using var f = await Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        var held = f.App.HoldNext("music.catalog.personalPlaylists");
        var first = f.Module.NavigateAsync("catalog-personal", cancellation.Token);
        await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        try
        {
            await first;
            throw new InvalidOperationException("cancelled subscriber completed successfully");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Check(!f.Module.CanCancelCatalog, "cancelled last subscriber must detach the retired owner");
        var second = f.Module.NavigateAsync("catalog-personal", default);
        await second;
        Equal(2, f.App.Log.Count(call => call.Method == "music.catalog.personalPlaylists"));
        var published = f.Module.CatalogSections.Single();
        held.Release();
        await Drain();
        Check(ReferenceEquals(published, f.Module.CatalogSections.Single()),
            "late retired result replaced the retry result");
    }
    private static async Task AccountReadFailure()
    {
        await using var f = await Fixture.Create();
        f.App.FailNext("music.accounts.list", "TIMEOUT");
        await f.Module.NavigateAsync("catalog-personal", default);
        Equal("请求超时", f.Module.Status);
        Equal(0, f.App.Log.Count(call => call.Method == "music.catalog.personalPlaylists"));
        await f.Module.NavigateAsync("catalog-personal", default);
        Equal("", f.Module.Status);
        Equal(2, f.App.Log.Count(call => call.Method == "music.accounts.list"));
        Equal(1, f.App.Log.Count(call => call.Method == "music.catalog.personalPlaylists"));
    }
    private static async Task AccountRead()
    {
        await using var f = await Fixture.Create(); await f.Module.NavigateAsync("catalog-personal", default);
        f.App.AccountGeneration++;
        await f.Module.ReadCatalogAccountAsync();
        Equal(0, f.Module.CatalogSections.Count); Check(f.Module.CatalogAccountText.Contains("Fixture wy", StringComparison.Ordinal));
    }
    private static async Task RapidOptions()
    {
        await using var f = await Fixture.Create(); await f.Module.NavigateAsync("catalog-fm", default);
        var held = f.App.HoldNext("music.catalog.fm");
        f.Module.CatalogFmMode = "explore"; var old = f.Module.CatalogOptionsChangedAsync(); await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Module.CatalogFmMode = "familiar"; await f.Module.CatalogOptionsChangedAsync();
        var displayed = f.Module.Songs[0]; held.Release(); await old;
        Equal("familiar", f.Module.CatalogFmMode); Check(ReferenceEquals(displayed, f.Module.Songs[0]));
    }
    private static async Task CancelPreparation()
    {
        await using var f = await Fixture.Create(); f.Select("kg"); await f.OpenPersonalPlaylist();
        var held = f.App.HoldNext("music.catalog.playlist.prepare", before: true);
        var save = f.Module.SaveCatalogPlaylistAsync(); await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Module.CancelCatalog(); held.Release(); await save;
        Check(!f.App.Log.Any(c => c.Method == "music.catalog.playlist.commit")); Equal(0, f.Store.LibraryWrites);
    }
    private static async Task CasConflict()
    {
        await using var f = await Fixture.Create(); await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
        f.App.BeforeCommit = () => f.App.Library.ExecuteAsync("music.queue.clear");
        await f.Module.PlayCatalogPlaylistAsync();
        Equal(0, f.Output.Opens); Equal(0L, N(await f.Queue(), "total"));
        Equal(1, f.App.Log.Count(c => c.Method == "music.catalog.playlist.commit"));
        Check(f.Module.Status.Contains("已变更", StringComparison.Ordinal));
    }
    private static async Task UnknownSave()
    {
        await using var f = await Fixture.Create(); await f.Module.NavigateAsync("catalog-daily", default);
        f.Store.UnknownAfterNextLibraryWrite = true;
        await f.Module.SaveCatalogPlaylistAsync();
        Equal("保存结果未确认", f.Module.Status);
        Equal(1, f.App.Log.Count(c => c.Method == "music.library.create"));
        await f.App.Library.ReconcileAsync();
        Equal(3L, N(await f.Detail(), "total")); Equal(1, f.Store.LibraryWrites);
    }
    private static async Task Errors()
    {
        await using var f = await Fixture.Create(); f.Select("tx");
        await f.Module.NavigateAsync("catalog-daily", default); Equal("当前内容暂不支持", f.Module.Status);
        Check(!f.Platform.Requests.OfType<MusicDailyRequest>().Any(), "never switches to wy");
        f.Select("wy"); f.App.Authenticated = false;
        await f.Module.NavigateAsync("catalog-daily", default); Equal("当前内容需要平台登录", f.Module.Status);
        f.App.Authenticated = true; f.App.AccountGeneration++;
        await f.Module.ReadCatalogAccountAsync();
        f.App.FailNext("music.catalog.daily", "DEVICE_CONTEXT_REQUIRED");
        await f.Module.NavigateAsync("catalog-daily", default); Equal("平台客户端状态不可用", f.Module.Status);
        Equal(0, f.Store.LibraryWrites);
    }
    private static async Task PartialBatch()
    {
        await using var f = await Fixture.Create(); await f.Module.NavigateAsync("catalog-daily", default);
        var first = f.Module.Songs[0];
        f.App.TransformNext("music.catalog.daily", reply => Edit(reply, o => o["count"] = 9));
        await f.Module.RefreshCatalogAsync();
        Check(ReferenceEquals(first, f.Module.Songs[0])); Equal("平台内容格式无效", f.Module.Status);
    }
    private static async Task CachedNavigationRetiresRead()
    {
        await using var f = await Fixture.Create();
        await f.Module.NavigateAsync("catalog-home", default);
        await f.Module.CatalogPageAsync(1);
        Equal("第 2 页", f.Module.CatalogPageText);
        var hold = f.App.HoldNext("music.catalog.home");
        var refresh = f.Module.RefreshCatalogAsync();
        await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Module.NavigateAsync("catalog-home", default);
        Equal("第 2 页", f.Module.CatalogPageText);
        Check(f.Module.CanCatalogPrevious, "restored cache must not retain the retired request as current");
        Check(!f.Module.CanCancelCatalog, "retired noncooperative request must be detached from current UI state");
        Check(!refresh.IsCompleted, "fixture request must remain physically blocked");
        hold.Release();
        await refresh;
        Equal("第 2 页", f.Module.CatalogPageText);
    }
    private static async Task LegacyPaging()
    {
        await using var f = await Fixture.Create();
        await f.Module.NavigateAsync("catalog-home", default); await f.Module.CatalogPageAsync(1);
        Equal("第 2 页", f.Module.CatalogPageText);
        await f.Module.NavigateAsync("favorites", default); int count = f.Platform.Requests.Count;
        await f.Module.NavigateAsync("catalog-home", default); Equal(count, f.Platform.Requests.Count);
        f.App.FailNext("music.catalog.home", "UPSTREAM_ERROR"); await f.Module.RefreshCatalogAsync();
        Equal("第 2 页", f.Module.CatalogPageText);
        await f.Module.NavigateAsync("catalog-channel/wy/rock", default);
        await f.Module.CatalogPageAsync(1);
        Check(f.Platform.Last<MusicChannelRequest>().Continuation!.Value.GetProperty("provider").GetProperty("opaque").GetString() == "retained");
        var hold = f.App.HoldNext("music.catalog.channel");
        var pending = f.Module.RefreshCatalogAsync(); await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5)); f.Module.CancelCatalog(); hold.Release(); await pending;
        Equal("已取消", f.Module.Status);
    }
    private static async Task CloseRead()
    {
        await using var f = await Fixture.Create();
        var hold = f.App.HoldNext("music.catalog.personalPlaylists");
        var loading = f.Module.NavigateAsync("catalog-personal", default); await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var close = f.Module.PrepareShutdownAsync(); hold.Release(); await loading; await close;
        Check(!f.Module.CanCatalogBrowse); Equal(0, f.Module.CatalogSections.Count); Equal(0, f.Store.LibraryWrites);
    }

    private static JsonElement Edit(JsonElement value, Action<JsonObject> action)
    { var node = JsonNode.Parse(value.GetRawText())!.AsObject(); action(node); return E(node); }
    private static string FirstId(string platform) => Id(platform, 0);
    private static string Id(string platform, int i) => platform switch { "kg" => (i + 1).ToString("x32"), "tx" => "MID" + (i + 1), _ => (i + 1).ToString() };
    private static JsonElement Song(string platform, int index, string? member = null)
    {
        int recording = platform == "kg" && index == 31 ? 0 : index;
        string id = Id(platform, recording);
        var native = new JsonObject { ["albumId"] = "70" };
        if (platform == "kg")
        {
            native["hash"] = id;
            if (member is not null) native["fileid"] = member;
        }
        return MusicSongFixture.Element(MusicSongFixture.Online(platform, id,
            "Track " + (recording + 1), "Fixture artist", "合成专辑", 180, native: native));
    }
    private static JsonElement Page(MusicPlaylistRequest r, int total)
    {
        int offset = (r.Page - 1) * r.Limit, count = Math.Max(0, Math.Min(r.Limit, total - offset));
        bool kg = r.Personal?.Kind == MusicPersonalPlaylistKind.KugouOwned;
        var p = E(new { platform = r.Platform, id = r.Id, name = "完整歌单", page = r.Page, limit = r.Limit, total,
            snapshot = new string('a', 64), snapshotStrength = r.Platform == "wy" ? "ordered-membership" : "metadata-only",
            more = offset + count < total, continuation = offset + count < total ? new { page = r.Page + 1 } : null,
            items = Enumerable.Range(offset, count).Select(i => Song(r.Platform, i, kg ? (1000 + i).ToString() : null)).ToArray() });
        return kg ? Edit(p, n => {
            n["memberIds"] = JsonSerializer.SerializeToNode(Enumerable.Range(offset, count).Select(i => (1000 + i).ToString()));
            n["memberSorts"] = JsonSerializer.SerializeToNode(Enumerable.Range(offset, count).Select(i => total - i)); }) : p;
    }
    private sealed class Platform : IMusicPlatformCatalog
    {
        public ConcurrentQueue<object> Requests { get; } = new();
        public int Total = 65;
        public int DailyCount = 3;
        public bool EmptyDirectoryFirst, Expired;
        public string LocatorVersion = "7";
        public object[]? HomeActions;
        public object[]? HomeEntries, ChannelEntries;
        public Func<MusicPlaylistRequest, JsonElement>? PlaylistChange;
        public T Last<T>() => Requests.OfType<T>().Last();
        private Task<JsonElement> Result(object request, JsonElement result, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Requests.Enqueue(request); return Task.FromResult(result); }
        public Task<JsonElement> PlaylistAsync(MusicPlaylistRequest r, CancellationToken t = default) =>
            Result(r, PlaylistChange?.Invoke(r) ?? Page(r, Total), t);
        private static object Cursor(int page) => new { page, provider = new { opaque = "retained", cursor = 42 }, scope = new string('b', 64) };
        public Task<JsonElement> PersonalPlaylistsAsync(MusicPersonalPlaylistsRequest r, CancellationToken t = default)
        {
            string id = r.Platform == "kg" ? "0:42" : r.Platform == "tx" ? "favorites" : "500";
            object? locator = r.Platform == "kg"
                ? new { scope = new string('b', 64), kind = "kugou-owned", listId = "42", total = Total, version = LocatorVersion,
                    name = "完整歌单", pageSize = 30, listType = 0, globalCollectionId = (string?)null, source = 1, listVersion = 7 }
                : r.Platform == "tx" ? new { scope = new string('b', 64), kind = "qq-favorites", listId = "201", total = (int?)null,
                    version = (string?)null, name = "完整歌单", pageSize = 100, playlistId = "favorites", dirId = "201" } : null;
            object card = r.Page == 1
                ? new { id, name = "完整歌单", kind = "playlist", available = true, locator, action = new { type = "playlist", id, locator } }
                : new { id = "701", name = "第二页", kind = "playlist", available = true, action = new { type = "playlist", id = "701" } };
            return Result(r, E(new { platform = r.Platform, accountId = "42", collection = r.Kind.ToString().ToLowerInvariant(),
                page = r.Page, limit = r.Limit, total = (int?)null, more = r.Page == 1,
                continuation = r.Page == 1 ? Cursor(2) : null, snapshot = (string?)null, version = (string?)null,
                items = EmptyDirectoryFirst && r.Page == 1 ? Array.Empty<object>() : new[] { card } }), t);
        }
        private JsonElement Batch(string kind, string title)
        {
            var items = Enumerable.Range(0, kind == "daily" ? DailyCount : 3).Select(i => Song("wy", i)).ToArray();
            return E(new { platform = "wy", kind, name = title, count = items.Length, more = false, items,
                recommendations = items.Select(item => new
                {
                    id = MusicSongFixture.NativeId(item),
                    source = kind,
                    alg = "fixture-" + kind
                }).ToArray(),
                expiresAt = kind == "daily" ? (long?)DateTimeOffset.UtcNow.AddMinutes(Expired ? -1 : 10).ToUnixTimeMilliseconds() : null });
        }
        public Task<JsonElement> DailyAsync(MusicDailyRequest r, CancellationToken t = default) => Result(r, Batch("daily", r.Branch == MusicDailyBranch.Style ? "风格推荐" : "每日推荐"), t);
        public Task<JsonElement> FmAsync(MusicFmRequest r, CancellationToken t = default) => Result(r, Batch("fm", "私人 FM"), t);
        public Task<JsonElement> SimilarAsync(MusicSimilarRequest r, CancellationToken t = default) => Result(r, Batch("similar", "相似歌曲"), t);
        public Task<JsonElement> HeartbeatAsync(MusicHeartbeatRequest r, CancellationToken t = default) => Result(r, Batch("heartbeat", "心动推荐"), t);
        public Task<JsonElement> QueueRecommendationsAsync(MusicQueueRecommendationsRequest r, CancellationToken t = default) => Result(r, Batch("queue-recommendations", "队列推荐"), t);
        public Task<JsonElement> AlbumAsync(MusicAlbumRequest r, CancellationToken t = default) =>
            Result(r, Edit(Batch("album", "合成专辑"), n => { n["id"] = r.Id; n["total"] = 3; }), t);
        public Task<JsonElement> HomeAsync(MusicHomeRequest r, CancellationToken t = default) =>
            Result(r, HomeEntries is { } entries ? E(new { platform = r.Platform, entries, more = false }) :
                E(new { platform = r.Platform, items = HomeActions ?? [new { id = r.Continuation is null ? "500" : "501", name = "歌单", kind = "playlist" }],
                more = r.Continuation is null && HomeActions is null, continuation = r.Continuation is null && HomeActions is null ? Cursor(2) : null }), t);
        public Task<JsonElement> ChannelsAsync(MusicChannelsRequest r, CancellationToken t = default) =>
            Result(r, E(new { platform = r.Platform, items = new[] { new { id = "rock", kind = "cube", name = "摇滚" } } }), t);
        public Task<JsonElement> ChannelAsync(MusicChannelRequest r, CancellationToken t = default) =>
            Result(r, ChannelEntries is { } entries ? E(new { platform = r.Platform, channel = new { id = r.Id }, entries, more = false }) :
                E(new { platform = r.Platform, channel = new { id = r.Id }, items = new[] { Song(r.Platform, r.Continuation is null ? 0 : 1) },
                more = r.Continuation is null, continuation = r.Continuation is null ? Cursor(2) : null }), t);
        public Task<JsonElement> SearchAsync(MusicSearchRequest r, CancellationToken t = default) =>
            Result(r, E(new { platform = r.Platform, items = new[] { Song(r.Platform, 0) }, page = r.Page, limit = r.Limit, total = 1, more = false }), t);
        public Task<JsonElement> SongAsync(MusicSongRequest r, CancellationToken t = default) =>
            Result(r, E(new { platform = r.Platform, song = Song(r.Platform, 0) }), t);
        public JsonElement Describe() => E(new { execution = "native" });
        public void Invalidate(string? platform = null) { }
        public void Dispose() { }
    }
    private sealed record Invocation(string Method, JsonElement Parameters);
    private sealed class Held
    {
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Wait() { Arrived.TrySetResult(); await released.Task; }
        public void Release() => released.TrySetResult();
    }
    private sealed class Application : MusicApplicationTestDouble
    {
        public readonly MusicLibraryOwner Library;
        public readonly MusicNativeCatalogOwner Catalog;
        private readonly MusicRecommendationQueuePort recommendationQueue;
        private readonly MusicRecommendationCoordinator recommendations;
        private readonly object recommendationGate = new();
        private Func<MusicRecommendationSelection, CancellationToken, Task<bool>>? recommendationSelection;
        private bool recommendationPrepared;
        public ConcurrentQueue<Invocation> Log { get; } = new();
        public bool Authenticated = true;
        public long AccountGeneration = 1;
        public string TargetPlatform = "wy";
        public Func<Task>? BeforeCommit;
        private readonly Dictionary<string, (Held Hold, bool Before)> holds = [];
        private readonly List<Held> allHolds = [];
        private readonly Dictionary<string, Func<JsonElement, JsonElement>> transforms = [];
        private readonly Dictionary<string, string> failures = [];
        public Application(Store store, Platform platform)
        {
            Library = new(store);
            Catalog = new(platform, Library);
            recommendationQueue = new(Library);
            recommendations = new(platform, recommendationQueue, SelectRecommendationAsync);
        }
        public override event Action<JsonElement>? Changed
        {
            add => recommendations.Changed += value;
            remove => recommendations.Changed -= value;
        }
        public override IDisposable BindSelection(
            Func<MusicRecommendationSelection, CancellationToken, Task<bool>> selectAsync)
        {
            ArgumentNullException.ThrowIfNull(selectAsync);
            lock (recommendationGate)
            {
                if (recommendationSelection is not null)
                    throw new InvalidOperationException("PLAYBACK_ALREADY_BOUND");
                recommendationSelection = selectAsync;
            }
            return new Binding(this, selectAsync);
        }
        private sealed class Binding(Application owner,
            Func<MusicRecommendationSelection, CancellationToken, Task<bool>> callback) : IDisposable
        {
            private int disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                lock (owner.recommendationGate)
                    if (ReferenceEquals(owner.recommendationSelection, callback))
                        owner.recommendationSelection = null;
                owner.recommendations.Invalidate();
            }
        }
        private Task<bool> SelectRecommendationAsync(MusicRecommendationSelection selection,
            CancellationToken token)
        {
            Func<MusicRecommendationSelection, CancellationToken, Task<bool>>? callback;
            lock (recommendationGate) callback = recommendationSelection;
            return callback is null ? Task.FromResult(false) : callback(selection, token);
        }
        public override Task OnPlaybackAsync(MusicRecommendationPlaybackState state,
            CancellationToken cancellationToken = default) =>
            recommendations.OnPlaybackAsync(state, cancellationToken);
        public override Task<bool> TryAdvanceAsync(string entryId, bool naturalEnd, bool play,
            CancellationToken cancellationToken = default) =>
            recommendations.TryAdvanceAsync(entryId, naturalEnd, play, cancellationToken);
        public override MusicRecommendationFeedback? GetFeedback(string entryId) =>
            recommendations.GetFeedback(entryId);
        public override async Task PrepareRecommendationsAsync(CancellationToken cancellationToken = default)
        {
            await recommendations.PrepareCloseAsync(cancellationToken);
            recommendationPrepared = true;
        }
        public JsonElement Last(string method) => Log.Last(c => c.Method == method).Parameters;
        public Held HoldNext(string method, bool before = false)
        { var hold = new Held(); holds[method] = (hold, before); allHolds.Add(hold); return hold; }
        public void ReleaseAll() { foreach (var hold in allHolds) hold.Release(); }
        public void FailNext(string method, string code) => failures[method] = code;
        public void TransformNext(string method, Func<JsonElement, JsonElement> action) => transforms[method] = action;
        private async Task<JsonElement> ReadQualitySettings(CancellationToken token)
        {
            var reply = await Library.ExecuteAsync("music.quality.settings.get", cancellationToken: token);
            if (!reply.GetProperty("ok").GetBoolean()) return reply;
            var result = JsonNode.Parse(reply.GetRawText())!.AsObject();
            // Preserve the original write-count assertions: the fixture target
            // is a read-only port view, not a new persistent settings mutation.
            result["onlineSettings"] = JsonSerializer.SerializeToNode(new {
                schema = 2, revision = 1, lyrics = false,
                target = new { installationId = "fixture-script", platform = TargetPlatform } });
            result["signature"] = LJ.Digest(LJ.Obj(("contract", "music.quality.v1"), ("scope", "local"),
                ("policy", result["settings"]), ("online", result["onlineSettings"])));
            return E(result);
        }
        private async Task<JsonElement> Resolve(JsonElement p, CancellationToken token)
        {
            var quality = await ReadQualitySettings(token);
            if (!quality.GetProperty("ok").GetBoolean()) return quality;
            if (S(p, "quality") != "320k") return E(new { ok = false, code = "CAPABILITY_UNAVAILABLE" });
            string policy = S(quality, "signature");
            var song = p.GetProperty("song");
            var source = song.GetProperty("sources").EnumerateArray().Single(s => S(s, "source") == TargetPlatform);
            var songNode = JsonNode.Parse(song.GetRawText())!.AsObject();
            string sourceKey = S(source, "sourceKey");
            string cache = LJ.Digest(LJ.Obj(("contract", "music.quality.cache.v1"), ("policy", policy),
                ("installation", "fixture-script"), ("platform", TargetPlatform), ("revision", 1), ("generation", 1),
                ("sourceKey", sourceKey), ("recording", source.GetProperty("source_data")),
                ("songId", songNode["songId"]), ("songRevision", songNode["revision"]),
                ("duration", songNode["duration"]), ("quality", S(p, "quality"))));
            if (p.TryGetProperty("qualityPolicySignature", out var expectedPolicy) && expectedPolicy.GetString() != policy)
                return E(new { ok = false, code = "QUALITY_POLICY_CHANGED" });
            if (p.TryGetProperty("qualityCacheSignature", out var expectedCache) && expectedCache.GetString() != cache)
                return E(new { ok = false, code = "QUALITY_CACHE_CHANGED" });
            return E(new { ok = true, installationId = "fixture-script", platform = TargetPlatform,
                songId = S(song, "songId"), generation = 1, quality = S(p, "quality"), sourceKey,
                qualityPolicySignature = policy, qualityCacheSignature = cache, qualityVerified = false,
                url = "https://media.invalid/synthetic", headers = new { }, playbackVerified = false });
        }
        public override async Task<JsonElement> ExecuteAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            var p = E(parameters ?? new { }); Log.Enqueue(new(method, p));
            holds.Remove(method, out var held); transforms.Remove(method, out var transform);
            if (held.Hold is not null && held.Before) await held.Hold.Wait();
            if (failures.Remove(method, out var code)) return E(new { ok = false, code });
            JsonElement result;
            if (method == "music.catalog.playlist.commit" && BeforeCommit is { } before)
            { BeforeCommit = null; await before(); }
            if (method == "music.accounts.list")
                result = E(new { ok = true, items = new[] { "wy", "tx", "kg" }.Select(platform => new {
                    platform, state = Authenticated ? "verified" : "none", generation = AccountGeneration,
                    profile = Authenticated ? new { platform, userId = "42", displayName = "Fixture " + platform } : null }) });
            else if (method.StartsWith("music.recommendations.", StringComparison.Ordinal))
                result = await recommendations.ExecuteAsync(method, p, cancellationToken);
            else if (method.StartsWith("music.catalog.", StringComparison.Ordinal))
            {
                if (method == "music.catalog.playlist.commit" &&
                    p.TryGetProperty("action", out var action) && action.GetString() == "play")
                    result = await recommendationQueue.ExecuteExternalAsync(
                        MusicRecommendationQueueChange.Replace,
                        token => Catalog.ExecuteAsync(method, p, token), cancellationToken);
                else if (method == "music.catalog.playlist.commit")
                    result = await recommendationQueue.InLaneAsync(
                        (_, token) => Catalog.ExecuteAsync(method, p, token), cancellationToken);
                else
                    result = await Catalog.ExecuteAsync(method, p, cancellationToken);
            }
            else if (method == "music.quality.settings.get") result = await ReadQualitySettings(cancellationToken);
            else if (method == "music.sources.detail") result = E(new { ok = true, item = new {
                enabled = true, generation = 1, executionHash = new string('d', 64) } });
            else if (method == "music.online.resolve") result = await Resolve(p, cancellationToken);
            else result = await recommendationQueue.ExecuteAsync(method, p, cancellationToken);
            if (held.Hold is not null && !held.Before) await held.Hold.Wait();
            return transform is null ? result : transform(result);
        }
        public override async Task PrepareShutdownAsync(CancellationToken cancellationToken = default)
        {
            if (!recommendationPrepared)
                await PrepareRecommendationsAsync(cancellationToken);
            Catalog.Invalidate();
            await Library.PrepareShutdownAsync(cancellationToken);
        }
        public override async Task CancelShutdownPreparationAsync(CancellationToken cancellationToken = default)
        {
            await Library.CancelShutdownPreparationAsync(cancellationToken);
            if (recommendationPrepared)
            {
                recommendations.Resume();
                recommendationPrepared = false;
            }
        }
        public override async ValueTask DisposeAsync()
        {
            ReleaseAll();
            await recommendations.DisposeAsync();
            await Catalog.DisposeAsync();
            await Library.DisposeAsync();
        }
    }
    private sealed class Store : IProtectedDocumentStore
    {
        private readonly object gate = new();
        private readonly Dictionary<string, byte[]> data = [];
        public int LibraryWrites;
        public bool UnknownAfterNextLibraryWrite;
        public Task<ProtectedDocument> ReadAsync(string ns, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate) return Task.FromResult(data.TryGetValue(ns, out var value)
                ? new ProtectedDocument(value.ToArray(), DocumentFingerprint.FromBytes(value)) : new(null, DocumentFingerprint.Missing));
        }
        public Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> value, DocumentFingerprint expected,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                var previous = data.TryGetValue(ns, out var bytes) ? DocumentFingerprint.FromBytes(bytes) : DocumentFingerprint.Missing;
                if (previous != expected) throw new ProtectedDocumentException(DocumentStoreError.Conflict);
                data[ns] = value.ToArray();
                if (ns == MusicLibraryOwner.DocumentNamespace)
                {
                    LibraryWrites++;
                    if (UnknownAfterNextLibraryWrite) { UnknownAfterNextLibraryWrite = false; throw new ProtectedDocumentException(DocumentStoreError.Storage); }
                }
                return Task.FromResult(DocumentFingerprint.FromBytes(value.Span));
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Dialogs : IMusicDialogs
    {
        public bool Confirm(string message, string title) => true;
        public string[]? SelectAudioFiles() => null;
    }
    private sealed class Output : IMusicPlayback
    {
        public int Opens;
        public string? OwnerId { get; private set; }
        public string? Identity { get; private set; }
        public bool HasMedia { get; private set; }
        public bool IsPlaying => false;
        public double PositionSeconds => 0;
        public double DurationSeconds => 180;
        public int Volume { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public event EventHandler? Started { add { } remove { } }
        public event EventHandler? Ended { add { } remove { } }
        public event EventHandler? Failed { add { } remove { } }
        public Task OpenAsync(string owner, string identity, Uri uri, string title, CancellationToken token)
        { token.ThrowIfCancellationRequested(); OwnerId = owner; Identity = identity; HasMedia = true; Opens++; return Task.CompletedTask; }
        public Task PauseAsync() => Task.CompletedTask;
        public Task ResumeAsync() => Task.CompletedTask;
        public Task StopAsync() { HasMedia = false; return Task.CompletedTask; }
        public Task SeekAsync(double seconds) => Task.CompletedTask;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public Store Store { get; } = new();
        public Platform Platform { get; } = new();
        public Output Output { get; } = new();
        public Dialogs Dialogs { get; } = new();
        public Application App { get; }
        public MusicCapability Module { get; }
        private Fixture() { App = new(Store, Platform); Module = new(App, Store, Output, Dialogs); }
        public static async Task<Fixture> Create()
        {
            var f = new Fixture(); try { await f.Module.InitializeAsync(default); return f; } catch { await f.DisposeAsync(); throw; }
        }
        public void Select(string platform)
        { Module.SelectedCatalogPlatform = Module.CatalogPlatforms.Single(p => p.Key == platform); App.TargetPlatform = platform; }
        public async Task OpenPersonalPlaylist()
        {
            await Module.NavigateAsync("catalog-personal", default);
            await Module.OpenCatalogCardAsync(Module.CatalogSections.Single().Items.Single());
            Check(Module.IsCatalogPlaylist && Module.Status.Length == 0, "private playlist did not publish: " + Module.Status);
        }
        public Task<JsonElement> Queue() => App.Library.ExecuteAsync("music.queue.get", new { offset = 0, limit = 100 });
        public async Task<JsonElement> Detail(int offset = 0)
        {
            var list = await App.Library.ExecuteAsync("music.library.list", new { offset = 0, limit = 100 });
            var id = list.GetProperty("items").EnumerateArray().First(p => S(p, "kind") == "playlist").GetProperty("id").GetString();
            return await App.Library.ExecuteAsync("music.library.detail", new { id, offset, limit = 100 });
        }
        public async ValueTask DisposeAsync() { App.ReleaseAll(); await Module.DisposeAsync(); await Store.DisposeAsync(); }
    }
    private static async Task OnSta(Func<Task> action)
    {
        var completed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                Exception? error = null;
                try { await action(); await Drain(); } catch (Exception e) { error = e; }
                finally { completed.TrySetResult(error); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            try { Dispatcher.Run(); } finally { stopped.TrySetResult(); }
        }) { IsBackground = true, Name = "music-personal-ui" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        var failure = await completed.Task.WaitAsync(TimeSpan.FromSeconds(90));
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
