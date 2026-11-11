using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Recommendations;
using MeowFilm.Capabilities.Search;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

// Real WPF -> public application seam -> real Native catalog/Library/CAS.
// Typed platform, account and script-resolution fixtures never contact an upstream.
// This group is independent of the existing five application/UI test groups.
public static partial class MusicCatalogPersonalTests
{
    public static IEnumerable<TestCase> Cases()
    {
        foreach (var homeCase in HomeCases()) yield return homeCase;
        foreach (var test in LocalHeartbeatCases()) yield return test;
        yield return Case("song cards replace only their own complete column and start at the clicked song", SectionQueue);
        yield return Case("song cards with a playlist source prepare all members and retain starting identity", SectionPlaylistQueue);
        yield return Case("playlist playback has no implicit search recommendation and can explicitly enter", PlaylistQueuePreview);
        yield return Case("playlist next preserves live FM and never starts playback or queue preview", PlaylistNextDuringFm);
        yield return Case("card loading belongs to the initiating card and retired receipt cannot clear its successor", CardLoading);
        yield return Case("native chart opens real album details and keeps album playback separate", ChartNavigation);
        yield return Case("navigation and WPF actions are real entry points", Entries);
        foreach (string platform in new[] { "wy", "tx", "kg" })
            yield return Case("personal directory explicit account and complete cursor " + platform, () => DirectoryPages(platform));
        yield return Case("directory filters nullable totals empty intermediate page and failed refresh", DirectoryFilters);
        yield return Case("KG locator 30-page membership selection survives global sorting", KgMembers);
        yield return Case("QQ favorites complete save never stores only visible page", TxSave);
        yield return Case("KG UI navigates past one hundred pages by platform total", LongKg);
        yield return Case("updated private locator cannot restore an old cached revision", LocatorRefresh);
        yield return Case("private tail failure preserves display and durable queue", TailFailure);
        yield return Case("duplicate member and changed snapshot cannot publish second page", PageDefects);
        yield return Case("empty complete playlist may save but not play", EmptyPlaylist);
        yield return Case("daily style full batch saves and replaces only on explicit actions", DailyActions);
        yield return Case("daily entry initialization shares each branch and never changes playback", DailyEntrySummaries);
        yield return Case("channel daily entries initialize songs without hover", ChannelDailySummaries);
        yield return Case("supplied and unavailable daily summaries never trigger extra reads", SuppliedDailySummaries);
        yield return Case("empty or failed daily summaries keep the page and do not retry on hover", FailedDailySummaries);
        yield return Case("retired daily summary cannot overwrite a newer page and close drains its receipt", RetiredDailySummary);
        yield return Case("daily preview expiry does not invalidate unrelated home content", DailySummaryExpiry);
        yield return Case("cancel after admitted batch commit waits receipt and does not autoplay", CancelledBatchCommitReceipt);
        yield return Case("FM modes rule cards and explicit next batch never auto-play", FmActions);
        yield return Case("similar native IDs album outlet and unsupported actions", SimilarAlbum);
        yield return Case("heartbeat and queue recommendations retain explicit context", RecommendationContext);
        yield return Case("home heartbeat and cloud favorites start native favorites without browser playlist context", HeartbeatFavoritesEntries);
        yield return Case("player heartbeat captures the real occurrence and rejects a retired presentation", HeartbeatCurrentEntry);
        yield return Case("official heartbeat entry stays reachable without login but never inherits another account", HeartbeatAccountQualification);
        yield return Case("source operations update controls silently while real failures use the shared notice", SourceNotices);
        yield return Case("search queue candidates append one batch and reject old cards", QueueCandidateActions);
        foreach (string tab in new[] { "overview", "songs" })
            yield return Case("search single playback starts one queue preview and preserves original search context: " + tab,
                () => SearchQueuePreview(tab));
        foreach (string retirement in new[] { "stopped", "owner-replaced", "account-intent" })
            foreach (bool failed in new[] { false, true })
                yield return Case("queue preview rejects a retired " + retirement + (failed ? " error" : " success"),
                    () => QueuePreviewRetirement(retirement, failed));
        yield return Case("queue preview admission failure reaches the WPF notice before a session exists", QueuePreviewFailureNotice);
        yield return Case("search paused after Started still admits read-only candidates", PausedQueuePreview);
        foreach (string outcome in new[] { "started", "paused-before-start", "failed", "stopped", "owner-replaced", "source-replaced", "account-intent" })
        {
            string action = outcome;
            yield return Case("search preview waits for its real first Started: " + outcome, () => SearchStartedGate(action));
        }
        yield return Case("search preview accepts synchronous Started before Open completion once", SearchSynchronousStarted);
        yield return Case("queue preview publication cannot replace a reentrant explicit FM intent", QueuePreviewReentrantIntent);
        yield return Case("FM footer consumes committed modes and all public scenes while failure keeps the previous mode", PlaybackFmChoices);
        foreach (bool restore in new[] { false, true })
            foreach (bool failed in new[] { false, true })
                yield return Case((restore ? "return" : "cancel") + " retires a recommendation awaiting shared account " +
                    (failed ? "failure" : "success"), () => RecommendationAccountWaitRetirement(restore, failed));
        yield return Case("WPF footer switches only live WY FM and retires old popup buttons", PlaybackFmPopup);
        yield return Case("WPF queue keeps recommendation candidates separate and exposes continuation", QueueCandidateSurface);
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
        yield return Case("unsupported routes leave content status and requests untouched", UnsupportedRoutes);
        yield return Case("missing account and device errors remain business states", Errors);
        yield return Case("partial recommendation batch cannot replace successful content", PartialBatch);
        yield return Case("cached navigation detaches a retired noncooperative catalog read", CachedNavigationRetiresRead);
        yield return Case("ordinary channel cursor cache refresh and cancellation remain intact", LegacyPaging);
        yield return Case("closing while personal read is delayed cannot revive content", CloseRead);
        yield return Case("catalog source default reads without writes and saved source restores before browsing", CatalogSourceRestore);
        yield return Case("catalog source restore is shared before the first channel request", CatalogSourceFirstRequest);
        yield return Case("catalog source failed restore blocks browsing until authoritative retry", CatalogSourceReadFailure);
        yield return Case("catalog source changes only after the saved receipt and reconciles unknown CAS", CatalogSourceSaveReceipts);
        yield return Case("catalog source failed save retains authority and failed reconciliation blocks new browsing", CatalogSourceSaveFailure);
        yield return Case("catalog source switch retains explicit detail queue and handed-off media", CatalogSourceDetail);
        yield return Case("generic channel placement preserves provider order tags and opaque page identity", GenericChannels);
        yield return Case("generic channel descriptors require current kind placement and availability", GenericChannelContract);
        foreach (var operation in new[] { "home", "channel" })
            foreach (var failure in new[] { false, true })
                yield return Case("catalog source retires delayed " + operation + (failure ? " failure" : " success"),
                    () => CatalogSourceLateReply(operation, failure));
        yield return Case("catalog source retirement cancels the old read and close publishes no retired receipt", CatalogSourceDrain);
        yield return Case("catalog source change reloads a retired landing only when the music page is shown again", CatalogSourceVisibleReload);
        yield return Case("catalog source startup read cancels and drains without an activated settings view", CatalogSourceRestoreDrain);
        foreach (string platform in new[] { "wy", "tx", "kg", "kw", "mg" })
            yield return Case("catalog source remains independent of explicit " + platform + " resource navigation",
                () => CatalogSourceResource(platform));
    }

    private static object SongSections(bool playlist = false) => new {
        platform = "wy", more = false, sections = new object[] {
            new { id = "first", title = "第一栏", type = "songs", layout = "grid",
                items = Enumerable.Range(0, 8).Select(i => new { song = Song("wy", i),
                    playbackSource = playlist ? new { type = "playlist", id = "500" } : null }).ToArray() },
            new { id = "other", title = "另一栏", type = "songs", layout = "grid", items = new[] { Song("wy", 80) } }
        }
    };
    private static async Task SectionQueue()
    {
        await using var f = await Fixture.Create();
        f.App.TransformNext("music.catalog.home", _ => Edit(E(SongSections()), n => n["ok"] = true));
        await f.Module.NavigateAsync("catalog-home", default);
        var card = f.Module.CatalogSections[0].Items[4];
        await f.Module.OpenCatalogCardAsync(card);
        var queue = await f.Queue();
        Equal(8L, N(queue, "total"));
        Equal(card.Song!.Id, S(queue.GetProperty("current").GetProperty("song"), "songId"));
        Equal(4, f.App.Last("music.queue.replace").GetProperty("index").GetInt32());
        Check(queue.GetProperty("entries").EnumerateArray().Select(row => S(row.GetProperty("song"), "songId"))
            .SequenceEqual(Enumerable.Range(0, 8).Select(i => S(Song("wy", i), "songId"))));
        Equal(1, f.Output.Opens); Check(!card.IsPreparing);
    }
    private static async Task SectionPlaylistQueue()
    {
        await using var f = await Fixture.Create();
        f.App.TransformNext("music.catalog.home", _ => Edit(E(SongSections(playlist: true)), n => n["ok"] = true));
        await f.Module.NavigateAsync("catalog-home", default);
        var card = f.Module.CatalogSections[0].Items[4];
        await f.Module.OpenCatalogCardAsync(card);
        var queue = await f.Queue();
        Equal((long)f.Platform.Total, N(queue, "total"));
        Equal(card.Song!.Id, S(queue.GetProperty("current").GetProperty("song"), "songId"));
        Equal("500", S(f.App.Last("music.catalog.playlist.prepare"), "id"));
        Check(!f.App.Last("music.catalog.playlist.commit").TryGetProperty("index", out _), "complete source resolves the selected identity, not a preview ordinal");
        Equal(1, f.Output.Opens);
    }
    private static async Task PlaylistQueuePreview()
    {
        await using var f = await Fixture.Create(); f.Platform.Total = 5;
        await f.Module.NavigateAsync("catalog-playlist/wy/500", default); await f.Module.PlayCatalogPlaylistAsync();
        var before = await f.Queue();
        Equal(5L, N(before, "total")); Equal(1, f.Store.LibraryWrites); Equal(1, f.Output.Opens);
        Check(!f.Module.HasQueueRecommendations && f.Platform.Requests.OfType<MusicQueueRecommendationsRequest>().Count() == 0);
        await f.Module.NavigateAsync("catalog-search", default);
        Check(!f.Module.HasQueueRecommendations, "browsing search never changes an existing source");
        await f.Module.RecommendCurrentAsync(f.Module.PlaybackPresentationKey);
        Check(f.Module.HasQueueRecommendations && f.Module.CanReturnRecommendation && f.Module.RecommendationContinue);
        var request = f.Platform.Last<MusicQueueRecommendationsRequest>();
        Equal(Id("wy", 0), request.SeedSongId); Equal(0, request.Offset);
        Equal(S(before, "currentId"), S(await f.Queue(), "currentId")); Equal(1, f.Output.Opens);
        await f.Module.ReturnRecommendationAsync();
        Equal(before.GetProperty("entries").GetRawText(), (await f.Queue()).GetProperty("entries").GetRawText());
    }

    private static async Task PlaylistNextDuringFm()
    {
        await using var f = await Fixture.Create();
        await f.Module.NavigateAsync("catalog-fm", default);
        await f.Module.PlayCatalogPlaylistAsync();
        await Until(() => f.Module.HasPlaybackFm && !f.Module.RecommendationLoading);
        f.Platform.Total = 2;
        f.Platform.HomeActions = [new { id = "500", name = "完整歌单", kind = "playlist" }];
        await f.Module.NavigateAsync("catalog-home", default);
        var card = f.Module.CatalogSections.Single().Items.Single();
        var state = await f.App.ExecuteAsync("music.recommendations.get");
        var before = await f.Queue();
        int starts = f.App.Log.Count(call => call.Method == "music.recommendations.start");
        int requests = f.Platform.Requests.OfType<MusicQueueRecommendationsRequest>().Count();
        int writes = f.Store.LibraryWrites, opens = f.Output.Opens;
        await f.Module.QueueCatalogCardAsync(card);
        await Drain();
        var after = await f.Queue();
        var retained = await f.App.ExecuteAsync("music.recommendations.get");
        Equal(S(state, "id"), S(retained, "id")); Equal("fm", S(retained, "kind"));
        Check(retained.GetProperty("active").GetBoolean() && f.Module.HasPlaybackFm && !f.Module.HasQueueRecommendations);
        Equal(S(state, "mode"), S(retained, "mode"));
        Equal(S(before, "currentId"), S(after, "currentId"));
        Equal(N(before, "mode"), N(after, "mode"));
        Equal(N(before, "version") + 1, N(after, "version"));
        Equal(N(before, "total") + 2, N(after, "total"));
        var original = before.GetProperty("entries").EnumerateArray().ToArray();
        var entries = after.GetProperty("entries").EnumerateArray().ToArray();
        Equal(original[0].GetRawText(), entries[0].GetRawText());
        Check(entries.Skip(3).Select(row => row.GetRawText())
            .SequenceEqual(original.Skip(1).Select(row => row.GetRawText())), "next retains original occurrences around the complete inserted segment");
        Check(entries.Skip(1).Take(2).Select(row => MusicSongFixture.NativeId(row.GetProperty("song")))
            .SequenceEqual(new[] { Id("wy", 0), Id("wy", 1) }));
        var commit = f.App.Last("music.catalog.playlist.commit");
        Equal("next", S(commit, "action"));
        Check(commit.EnumerateObject().Select(p => p.Name).Order()
            .SequenceEqual(new[] { "action", "expectedVersion", "token" }));
        Equal(starts, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
        Equal(requests, f.Platform.Requests.OfType<MusicQueueRecommendationsRequest>().Count());
        Equal(writes + 1, f.Store.LibraryWrites); Equal(opens, f.Output.Opens);
    }
    private static async Task CardLoading()
    {
        await using var f = await Fixture.Create();
        f.Platform.HomeActions = new[] { "500", "501" }.Select(id => new { id, name = "歌单 " + id, kind = "playlist" }).ToArray();
        await f.Module.NavigateAsync("catalog-home", default);
        var a = f.Module.CatalogSections.Single().Items[0]; var b = f.Module.CatalogSections.Single().Items[1];
        var first = f.App.HoldNext("music.catalog.playlist.prepare", before: true);
        var playingA = f.Module.PlayCatalogCardAsync(a); await first.Arrived.Task;
        Check(a.IsPreparing && !b.IsPreparing && f.Module.Status == "", "only the originating card has a loading state");
        var entry = new EntryCard { DataContext = b };
        entry.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
            { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
        Check(a.IsPreparing && !b.IsPreparing, "hover cannot transfer playback intent");
        var second = f.App.HoldNext("music.catalog.playlist.prepare", before: true);
        var playingB = f.Module.PlayCatalogCardAsync(b); await second.Arrived.Task;
        Check(!a.IsPreparing && b.IsPreparing, "an explicit new play supersedes A");
        first.Release(); await playingA;
        Check(b.IsPreparing, "A's late finally cannot clear B's state");
        f.Module.CancelCatalog(); second.Release(); await playingB;
        Check(!a.IsPreparing && !b.IsPreparing); Equal(0L, N(await f.Queue(), "total")); Equal(0, f.Output.Opens);
    }
    private static async Task ChartNavigation()
    {
        await using var f = await Fixture.Create();
        f.Platform.HomeActions = [new { id = "digital", name = "畅销榜", canPlay = false,
            action = new { type = "chart", id = "albums" } }];
        await f.Module.NavigateAsync("catalog-home", default);
        var chart = f.Module.CatalogSections.Single().Items.Single(); Check(chart.CanOpen && !chart.CanPlay);
        await f.Module.PlayCatalogCardAsync(chart); Check(!f.Platform.Requests.OfType<MusicChartRequest>().Any());
        await f.Module.OpenCatalogCardAsync(chart); Equal("albums", f.Platform.Last<MusicChartRequest>().Kind);
        Check(f.Module.IsCatalogSalesChart && !f.Module.IsCatalogCollection, "album ranking cannot replace the queue with all albums");
        f.Module.CatalogChartKind = "singles"; f.Module.CatalogChartPeriod = "yearly"; f.Module.CatalogChartYear = 2024;
        await f.Module.ChangeCatalogChartAsync();
        var request = f.Platform.Last<MusicChartRequest>(); Equal("singles", request.Kind); Equal("yearly", request.Period); Equal(2024, request.Year);
        var album = f.Module.CatalogSections.Single().Items.Single(); Equal("70", album.ActionId);
        await f.Module.OpenCatalogCardAsync(album); Equal("70", f.Platform.Last<MusicAlbumRequest>().Id);
        Check(f.Module.IsCatalogBatch); await f.Module.PlayCatalogPlaylistAsync();
        Equal(3L, N(await f.Queue(), "total")); Equal(1, f.Output.Opens);
    }
    private static TestCase Case(string name, Func<Task> run) => new("Music personal UI " + name, () => OnSta(run));
    private static JsonElement E(object value) => JsonSerializer.SerializeToElement(value);
    private static string S(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    private static long N(JsonElement value, string name) => value.GetProperty(name).GetInt64();
    private static void Check(bool value, string message = "assertion failed") { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual, [CallerLineNumber] int line = 0)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Line {line}: Expected {expected}, got {actual}"); }
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
    private static void Layout(FrameworkElement view, double width = 860, double height = 720)
    { view.Measure(new(width, height)); view.Arrange(new(0, 0, width, height)); view.UpdateLayout(); }
    private static void SaveRecommendationFrame(FrameworkElement view, string fileName)
    {
        view.UpdateLayout();
        int width = (int)Math.Ceiling(view.ActualWidth), height = (int)Math.Ceiling(view.ActualHeight);
        Check(width > 0 && height > 0, "recommendation snapshot requires the actual arranged WPF surface");
        var frame = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        frame.Render(view);
        string path = TestWorkspace.DataPath("recommendations-ui", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame)); encoder.Save(stream);
        Console.WriteLine("VISUAL " + path);
    }
    private static MusicCatalogCard FirstSong(Fixture f) => f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.Song is not null);
    private static async Task CatalogSourceRestore()
    {
        Store store;
        await using (var f = await Fixture.Create())
        {
            store = f.Store;
            Equal("wy", f.Module.CatalogSourcePlatform);
            Equal(0, f.Store.LibraryWrites);
            await f.Module.NavigateAsync("catalog-home", default);
            Equal("wy", f.Platform.Last<MusicHomeRequest>().Platform);
            await f.Module.SetCatalogSourceAsync("kg");
            Equal("kg", f.Module.CatalogSourcePlatform);
            Equal(1, f.Store.LibraryWrites);
        }
        int writes = store.LibraryWrites;
        await using var restored = await Fixture.Create(store: store);
        Equal("kg", restored.Module.CatalogSourcePlatform);
        Equal(0, restored.Platform.Requests.Count);
        await restored.Module.NavigateAsync("catalog-home", default);
        Equal("kg", restored.Platform.Last<MusicHomeRequest>().Platform);
        Equal(writes, store.LibraryWrites);
    }

    private static async Task CatalogSourceFirstRequest()
    {
        await using var f = await Fixture.Create(initialize: false);
        var saved = await f.App.Library.ExecuteAsync("music.catalogSource.settings.save", new { ifRevision = 0, platform = "kg" });
        Check(saved.GetProperty("ok").GetBoolean());
        var hold = f.App.HoldNext("music.catalogSource.settings.get");
        var first = f.Module.NavigateAsync("catalog-channels", default);
        await hold.Arrived.Task;
        var second = f.Module.NavigateAsync("catalog-channels", default);
        await Drain();
        Equal(0, f.Platform.Requests.Count);
        Equal(1, f.App.Log.Count(call => call.Method == "music.catalogSource.settings.get"));
        hold.Release();
        await Task.WhenAll(first, second);
        Equal(1, f.App.Log.Count(call => call.Method == "music.catalogSource.settings.get"));
        Equal(1, f.Platform.Requests.OfType<MusicChannelsRequest>().Count());
        Equal("kg", f.Platform.Last<MusicChannelsRequest>().Platform);
        Equal("kg", f.Platform.Last<MusicChannelRequest>().Platform);
    }

    private static async Task CatalogSourceReadFailure()
    {
        await using var f = await Fixture.Create(initialize: false);
        f.App.FailNext("music.catalogSource.settings.get", "STORAGE_UNAVAILABLE");
        try
        {
            await f.Module.InitializeAsync(default);
            throw new InvalidOperationException("Startup accepted a failed local source read");
        }
        catch (MusicOperationException error) { Equal("STORAGE_UNAVAILABLE", error.Code); }
        Check(!f.Module.CanEditCatalogSource && f.Module.HasCatalogSourceError);
        await f.Module.NavigateAsync("favorites", default);
        f.App.FailNext("music.catalogSource.settings.get", "STORAGE_UNAVAILABLE");
        await f.Module.NavigateAsync("catalog-home", default);
        Equal(0, f.Platform.Requests.Count);
        Equal("数据源设置读取失败", f.Module.Status);
        Equal("catalog-home", f.Module.CaptureNavigationLocation());
        Equal(0, f.Module.HomeSections.Count);
        await f.Module.ReadCatalogSourceSettingsAsync();
        Check(f.Module.CanEditCatalogSource && !f.Module.HasCatalogSourceError);
        Equal(0, f.Platform.Requests.Count);
        await f.Module.ActivateCatalogLandingAsync();
        await f.Module.HomeInitialization;
        Equal(2, f.Platform.Requests.Count);
        Equal("wy", f.Platform.Last<MusicHomeRequest>().Platform);
        Check(f.Module.HomeSections.Count > 0, "showing the unavailable landing initializes all batches once after source restore");
        await f.Module.ActivateCatalogLandingAsync();
        await f.Module.HomeInitialization;
        Equal(2, f.Platform.Requests.Count);
        Equal(0, f.Store.LibraryWrites);
    }

    private static async Task CatalogSourceSaveReceipts()
    {
        await using var f = await Fixture.Create();
        await f.Module.NavigateAsync("catalog-home", default);
        await f.Module.HomeInitialization;
        var prior = f.Module.HomeSections.ToArray();
        var hold = f.App.HoldNext("music.catalogSource.settings.save");
        var save = f.Module.SetCatalogSourceAsync("kg");
        await hold.Arrived.Task;
        Equal("wy", f.Module.CatalogSourcePlatform);
        Check(prior.SequenceEqual(f.Module.HomeSections), "an unreceived save cannot change the source surface");
        hold.Release(); await save;
        Equal("kg", f.Module.CatalogSourcePlatform);
        Equal(0, f.Module.HomeSections.Count);
        int requests = f.Platform.Requests.Count;
        await f.Module.ActivateCatalogLandingAsync();
        await f.Module.HomeInitialization;
        Equal(requests + 2, f.Platform.Requests.Count);
        Equal("kg", f.Platform.Last<MusicHomeRequest>().Platform);
        Check(f.Module.HomeSections.Count > 0 && !f.Module.HomeSections.Intersect(prior, ReferenceEqualityComparer.Instance).Any(),
            "the retired landing reads the saved source when shown again");
        f.App.TransformNext("music.catalogSource.settings.save", _ => E(new { ok = false, code = "COMMIT_UNKNOWN" }));
        int gets = f.App.Log.Count(call => call.Method == "music.catalogSource.settings.get");
        await f.Module.SetCatalogSourceAsync("wy");
        Equal("wy", f.Module.CatalogSourcePlatform);
        Equal(gets + 1, f.App.Log.Count(call => call.Method == "music.catalogSource.settings.get"));
        Check(!f.Module.HasCatalogSourceError, "a matching authoritative read settles the unknown receipt");

        var settings = (await f.App.Library.ExecuteAsync("music.catalogSource.settings.get")).GetProperty("settings");
        await f.App.Library.ExecuteAsync("music.catalogSource.settings.save",
            new { ifRevision = N(settings, "revision"), platform = "kg" });
        gets = f.App.Log.Count(call => call.Method == "music.catalogSource.settings.get");
        await f.Module.SetCatalogSourceAsync("kg");
        Equal("kg", f.Module.CatalogSourcePlatform);
        Equal(gets + 1, f.App.Log.Count(call => call.Method == "music.catalogSource.settings.get"));
    }

    private static async Task CatalogSourceSaveFailure()
    {
        await using var f = await Fixture.Create();
        await f.Module.NavigateAsync("catalog-home", default);
        var prior = f.Module.HomeSections.ToArray();
        f.App.FailNext("music.catalogSource.settings.save", "STORAGE_UNAVAILABLE");
        await f.Module.SetCatalogSourceAsync("kg");
        Equal("wy", f.Module.CatalogSourcePlatform);
        Check(f.Module.CanEditCatalogSource && f.Module.HasCatalogSourceError);
        Check(prior.SequenceEqual(f.Module.HomeSections), "a rejected save retains the known surface");
        Equal(0, f.Store.LibraryWrites);

        f.App.TransformNext("music.catalogSource.settings.save", _ => E(new { ok = false, code = "COMMIT_UNKNOWN" }));
        f.App.FailNext("music.catalogSource.settings.get", "STORAGE_UNAVAILABLE");
        await f.Module.SetCatalogSourceAsync("kg");
        Equal("wy", f.Module.CatalogSourcePlatform);
        Check(!f.Module.CanEditCatalogSource && f.Module.HasCatalogSourceError);
        int requests = f.Platform.Requests.Count;
        f.App.FailNext("music.catalogSource.settings.get", "STORAGE_UNAVAILABLE");
        await f.Module.NavigateAsync("catalog-channels", default);
        Equal(requests, f.Platform.Requests.Count);
        Equal("catalog-channels", f.Module.CaptureNavigationLocation());
        Equal("数据源设置读取失败", f.Module.Status);
        await f.Module.ReadCatalogSourceSettingsAsync();
        Equal("kg", f.Module.CatalogSourcePlatform);
        await f.Module.ActivateCatalogLandingAsync();
        Equal("kg", f.Platform.Last<MusicChannelsRequest>().Platform);
        Equal("kg", f.Platform.Last<MusicChannelRequest>().Platform);
        await f.Module.NavigateAsync("catalog-home", default);
        Equal("kg", f.Platform.Last<MusicHomeRequest>().Platform);
    }

    private static async Task CatalogSourceDetail()
    {
        await using var f = await Fixture.Create();
        f.Select("tx");
        await f.Module.NavigateAsync("catalog-playlist/tx/500", default);
        var cards = f.Module.CatalogSections.ToArray();
        await f.Module.PlayCatalogPlaylistAsync();
        var before = await f.Queue();
        var identity = f.Output.Identity;
        Equal(1, f.Output.Opens);
        await f.Module.SetCatalogSourceAsync("kg");
        Equal("kg", f.Module.CatalogSourcePlatform);
        Equal("tx", f.Module.SelectedCatalogPlatform.Key);
        Equal("catalog-playlist/tx/500", f.Module.CaptureNavigationLocation());
        Check(cards.SequenceEqual(f.Module.CatalogSections), "source changes preserve an explicit detail snapshot");
        var after = await f.Queue();
        Equal(N(before, "version"), N(after, "version"));
        Equal(before.GetProperty("entries").GetRawText(), after.GetProperty("entries").GetRawText());
        Equal(identity, f.Output.Identity);
        Equal(1, f.Output.Opens); Check(f.Output.HasMedia);
        await f.Module.NavigateAsync("catalog-home", default);
        Equal("kg", f.Platform.Last<MusicHomeRequest>().Platform);
        await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
        Equal("wy", f.Platform.Last<MusicPlaylistRequest>().Platform);
        Equal("kg", f.Module.CatalogSourcePlatform);
        await f.Module.NavigateAsync("catalog-channels", default);
        Equal("kg", f.Platform.Last<MusicChannelsRequest>().Platform);
    }

    private static async Task GenericChannels()
    {
        await using var f = await Fixture.Create();
        await f.Module.SetCatalogSourceAsync("kg");
        f.Platform.ChannelDescriptors =
        [
            new { id = "extra-b", kind = "channel", placement = "more", available = true, name = "更多二" },
            new { id = "disabled", kind = "channel", placement = "primary", available = false, name = "不可用" },
            new { id = "分类/二", kind = "channel", placement = "primary", available = true, name = "分类二", pageCode = "opaque:page/2" },
            new { id = "分类/一", kind = "channel", placement = "primary", available = true, name = "分类一" },
            new { id = "extra-a", kind = "channel", placement = "more", available = true, name = "更多一" }
        ];
        f.Platform.ChannelTags = true;
        await f.Module.NavigateAsync("catalog-channels", default);
        Equal("disabled,分类/二,分类/一", string.Join(",", f.Module.VisibleChannelTabs.Select(card => card.ActionId)));
        Equal("extra-b,extra-a", string.Join(",", f.Module.MoreChannelTabs.Select(card => card.ActionId)));
        Check(!f.Module.VisibleChannelTabs.First().CanOpen);
        Equal("kg/" + Uri.EscapeDataString("分类/二"), f.Module.CurrentChannelKey);
        Equal("opaque:page/2", f.Platform.Last<MusicChannelRequest>().PageCode);
        await f.Module.CatalogPageAsync(1);
        await f.Module.CatalogTagAsync(f.Module.CatalogTags.Single(tag => tag.Id == "genre:two"));
        var first = f.Platform.Last<MusicChannelRequest>();
        Equal("genre:two", first.Tag); Check(first.Continuation is null);
        await f.Module.CatalogPageAsync(1);
        var next = f.Platform.Last<MusicChannelRequest>();
        Equal("kg", next.Platform); Equal("genre:two", next.Tag); Equal(first.PageCode, next.PageCode);
        Equal("retained", S(next.Continuation!.Value.GetProperty("provider"), "opaque"));
        Equal("kg:分类/二", S(next.Continuation.Value, "target"));
        Equal("genre:two", S(next.Continuation.Value, "tag"));
        Equal(1, f.Module.CatalogTagGroups.Count); Equal("genre", f.Module.CatalogTagGroups[0].Id);
        await f.Module.SetCatalogSourceAsync("wy");
        Equal(0, f.Module.ChannelTabs.Count); Equal(0, f.Module.CatalogTags.Count); Equal(0, f.Module.CatalogTagGroups.Count);
        f.Platform.ChannelDescriptors = null; f.Platform.ChannelTags = false;
        await f.Module.NavigateAsync("catalog-channels", default);
        var switched = f.Platform.Last<MusicChannelRequest>();
        Equal("wy", switched.Platform); Equal("rock", switched.Id);
        Check(switched.Continuation is null && switched.PageCode is null && switched.Tag == "");
        Equal(0, f.Output.Opens);
        Check(!f.App.Log.Any(call => call.Method is "music.online.resolve" or "music.search.start" or "music.catalog.playlist.prepare"),
            "directory reads do not resolve audio, match recordings or expand playlists");
    }

    private static Task GenericChannelContract()
    {
        var valid = E(new { platform = "kg", items = new[] {
            new { id = "provider-category", name = "频道", kind = "channel", placement = "primary", available = true } } });
        var card = MusicCatalogDto.Read(valid, "kg", "channels", "").Single().Items.Single();
        Check(card.CanOpen && card.Kind == "channel" && card.Placement == "primary");
        foreach (var invalid in new Action<JsonObject>[]
        {
            item => item["kind"] = "cube",
            item => item.Remove("placement"),
            item => item["placement"] = "unknown",
            item => item.Remove("available"),
            item => item["available"] = "true"
        })
        {
            var reply = Edit(valid, root => invalid(root["items"]![0]!.AsObject()));
            bool rejected = false;
            try { _ = MusicCatalogDto.Read(reply, "kg", "channels", ""); }
            catch (MusicOperationException) { rejected = true; }
            Check(rejected, "only the current channel descriptor contract is accepted");
        }
        return Task.CompletedTask;
    }

    private static async Task CatalogSourceLateReply(string operation, bool failure)
    {
        await using var f = await Fixture.Create();
        var route = operation == "home" ? "catalog-home" : "catalog-channels";
        await f.Module.NavigateAsync(route, default);
        var hold = f.App.HoldNext("music.catalog." + operation);
        if (failure) f.App.TransformNext("music.catalog." + operation, _ => E(new { ok = false, code = "NETWORK_ERROR" }));
        var old = f.Module.RefreshCatalogAsync();
        await hold.Arrived.Task;
        Check(f.Module.CanEditCatalogSource, "browsing does not lock the source preference");
        await f.Module.SetCatalogSourceAsync("kg");
        await hold.Cancelled.Task;
        await f.Module.NavigateAsync(route, default);
        var sections = f.Module.CatalogSections.ToArray();
        var status = f.Module.Status;
        hold.Release(); await old;
        Equal("kg", f.Module.CatalogSourcePlatform);
        Equal("kg", f.Module.SelectedCatalogPlatform.Key);
        Check(sections.SequenceEqual(f.Module.CatalogSections), "retired receipts cannot replace the new source");
        Equal(status, f.Module.Status);
        if (operation == "channel") Check(f.Module.CurrentChannelKey.StartsWith("kg/", StringComparison.Ordinal));
    }

    private static async Task CatalogSourceDrain()
    {
        await using var f = await Fixture.Create();
        await f.Module.NavigateAsync("catalog-home", default);
        var hold = f.App.HoldNext("music.catalog.home");
        var old = f.Module.RefreshCatalogAsync(); await hold.Arrived.Task;
        await f.Module.SetCatalogSourceAsync("kg");
        await hold.Cancelled.Task;
        await f.Module.NavigateAsync("catalog-home", default);
        var sections = f.Module.HomeSections.ToArray();
        Check(sections.Length > 0 && sections.SelectMany(section => section.Items).All(card => card.Platform == "kg"),
            "the new source publishes its own landing");
        var closing = f.Module.PrepareShutdownAsync(); await Drain();
        hold.Release();
        await Task.WhenAll(old, closing);
        Check(sections.SequenceEqual(f.Module.HomeSections, ReferenceEqualityComparer.Instance),
            "a retired receipt released during close cannot publish");
    }

    private static async Task CatalogSourceVisibleReload()
    {
        await using var f = await Fixture.Create();
        var window = new Window
        {
            Content = f.Module.View, Width = 1080, Height = 720, Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None
        };
        try
        {
            window.Show(); window.UpdateLayout(); await Drain();
            await f.Module.NavigateAsync("catalog-home", default);
            await f.Module.HomeInitialization;
            Equal("wy", f.Platform.Last<MusicHomeRequest>().Platform);
            int requests = f.Platform.Requests.Count;
            f.Module.View.Visibility = Visibility.Collapsed; await Drain();
            await f.Module.SetCatalogSourceAsync("kg");
            await Drain();
            Equal(requests, f.Platform.Requests.Count);
            Equal(0, f.Module.HomeSections.Count);
            f.Module.View.Visibility = Visibility.Visible;
            await Until(() => f.Platform.Requests.Count >= requests + 1 && f.Module.HomeSections.Count > 0);
            await f.Module.HomeInitialization;
            Equal(requests + 2, f.Platform.Requests.Count);
            Equal("kg", f.Platform.Last<MusicHomeRequest>().Platform);
            f.Module.View.Visibility = Visibility.Collapsed; await Drain();
            f.Module.View.Visibility = Visibility.Visible; await Drain();
            Equal(requests + 2, f.Platform.Requests.Count);
        }
        finally { window.Content = null; window.Close(); }
    }

    private static async Task CatalogSourceRestoreDrain()
    {
        await using var f = await Fixture.Create(initialize: false);
        Check((await f.App.Library.ExecuteAsync("music.catalogSource.settings.save",
            new { ifRevision = 0, platform = "kg" })).GetProperty("ok").GetBoolean());
        var hold = f.App.HoldNext("music.catalogSource.settings.get");
        var initializing = f.Module.InitializeAsync(default); await hold.Arrived.Task;
        var closing = f.Module.PrepareShutdownAsync(); await hold.Cancelled.Task; await Drain();
        Check(!closing.IsCompleted, "closing retains the cancelled physical startup source read");
        Equal(0, f.Platform.Requests.Count);
        hold.Release();
        try { await initializing; throw new InvalidOperationException("Startup must observe close cancellation"); }
        catch (OperationCanceledException) { }
        await closing;
        Equal("wy", f.Module.CatalogSourcePlatform);
        Check(!f.Module.HasCatalogSourceError && f.Module.HomeSections.Count == 0,
            "a retired startup receipt publishes neither saved source nor a spurious restore error");
    }

    private static async Task CatalogSourceResource(string platform)
    {
        await using var f = await Fixture.Create();
        await f.Module.SetCatalogSourceAsync("kg");
        f.Select(platform);
        await f.Module.NavigateAsync("catalog-playlist/" + platform + "/500", default);
        Equal(platform, f.Platform.Last<MusicPlaylistRequest>().Platform);
        Equal(platform, f.Module.SelectedCatalogPlatform.Key);
        Equal("kg", f.Module.CatalogSourcePlatform);
        await f.Module.NavigateAsync("catalog-home", default);
        Equal("kg", f.Platform.Last<MusicHomeRequest>().Platform);
        Equal("kg", f.Module.SelectedCatalogPlatform.Key);
        await f.Module.NavigateAsync("catalog-channels", default);
        Equal("kg", f.Platform.Last<MusicChannelsRequest>().Platform);
        Equal("kg", f.Platform.Last<MusicChannelRequest>().Platform);
        Equal(1, f.App.Log.Count(call => call.Method == "music.catalogSource.settings.save"));
        Check(!f.Module.HasCatalogSourceError);
    }

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
        Equal(65L, N(queue, "total")); Equal("1031", S(MusicSongFixture.Info(queue.GetProperty("current").GetProperty("song")), "fileid"));
        var ids = queue.GetProperty("entries").EnumerateArray().Select(e => S(MusicSongFixture.Info(e.GetProperty("song")), "fileid")).ToArray();
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
        id, key = id, name = "每日推荐", subtitle = "从「合成歌曲」听起",
        dayOfMonth = 29, presentation = "preview", display = available,
        available, errorCode = available ? "" : "AUTH_REQUIRED", action = new { type = "daily", branch }
    };
    private static async Task DailyEntrySummaries()
    {
        await using var f = await Fixture.Create();
        f.Platform.DailyCount = 8;
        f.Platform.HomeEntries = [DailyEntry(), DailyEntry("daily-copy"), DailyEntry("daily-style", "style"),
            new { id = "500", name = "普通歌单", presentation = "static", action = new { type = "playlist", id = "500" } }];
        await f.Module.NavigateAsync("catalog-home", default);
        var cards = f.Module.CatalogSections.Single().Items.Where(card => card.Kind == "daily").ToArray();
        Equal(3, cards.Length);
        await f.Module.CatalogEntryInitialization;
        Check(cards.All(card => card.DayOfMonth == 29 && card.Subtitle.Contains("合成歌曲", StringComparison.Ordinal)));
        Check(cards.All(card => card.PreviewTitles.SequenceEqual(["1 Track 1", "2 Track 2", "3 Track 3"])));
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
        await f.Module.CatalogEntryInitialization;
        Equal(4, f.Platform.Requests.OfType<MusicDailyRequest>().Count());
        CheckNoDisplayEnrichment(f);
    }
    private static async Task ChannelDailySummaries()
    {
        await using var f = await Fixture.Create();
        f.Platform.ChannelEntries = [DailyEntry()];
        await f.Module.NavigateAsync("catalog-channel/wy/rock", default);
        var card = f.Module.CatalogSections.Single().Items.Single();
        await f.Module.CatalogEntryInitialization;
        Equal(3, card.PreviewSongs.Count);
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
        Check(card.PreviewTitles.SequenceEqual(["1 已有曲目"]));
        Equal(1, f.Module.DisplayCatalogSections.Single().Items.Count);
        Equal(0, f.Store.LibraryWrites); Equal(0, f.Output.Opens);
    }
    private static async Task FailedDailySummaries()
    {
        foreach (bool empty in new[] { false, true })
        {
            await using var f = await Fixture.Create();
            f.Platform.HomeEntries = [DailyEntry(),
                new { id = "500", name = "普通歌单", presentation = "static", action = new { type = "playlist", id = "500" } }];
            if (empty)
                f.App.TransformNext("music.catalog.daily", reply => Edit(reply, row => {
                    row["items"] = new JsonArray(); row["count"] = 0; }));
            else f.App.FailNext("music.catalog.daily", "UPSTREAM_ERROR");
            await f.Module.NavigateAsync("catalog-home", default);
            await f.Module.CatalogEntryInitialization;
            Equal("", f.Module.Status); Equal(2, f.Module.CatalogSections.Single().Items.Count);
            var daily = f.Module.CatalogSections.Single().Items.First();
            Check(!daily.HasPreview, "no fabricated songs");
            var entry = new EntryCard { DataContext = daily };
            for (int i = 0; i < 3; i++) entry.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
            Equal(0, daily.PreviewTitles.Count);
            Equal(1, f.App.Log.Count(call => call.Method == "music.catalog.daily"));
            await f.Module.RefreshCatalogAsync();
            await f.Module.CatalogEntryInitialization;
            Equal(3, f.Module.CatalogSections.Single().Items.First().PreviewTitles.Count);
            Equal(2, f.App.Log.Count(call => call.Method == "music.catalog.daily"));
            Equal(0, f.Store.LibraryWrites); Equal(0, f.Output.Opens);
        }
    }
    private static async Task RetiredDailySummary()
    {
        await using var f = await Fixture.Create();
        f.Platform.HomeEntries = [DailyEntry()];
        var held = f.App.HoldNext("music.catalog.daily");
        await f.Module.NavigateAsync("catalog-home", default);
        var old = f.Module.CatalogEntryInitialization;
        await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Module.CancelCatalog();
        await f.Module.NavigateAsync("favorites", default);
        await f.Module.NavigateAsync("catalog-home", default);
        var published = f.Module.CatalogSections.Single();
        await f.Module.CatalogEntryInitialization;
        Equal(3, published.Items.Single().PreviewTitles.Count);
        var closing = f.Module.PrepareShutdownAsync();
        await Task.Delay(25); await Drain();
        Check(!closing.IsCompleted && !old.IsCompleted, "close retains the noncooperative physical summary receipt");
        held.Release(); await old; await closing;
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
        await f.Module.CatalogEntryInitialization;
        Equal(3, f.Module.CatalogSections.Single().Items.Single().PreviewTitles.Count);
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
        await f.Module.NavigateAsync("catalog-playlist/wy/500", default); await f.Module.PlayCatalogPlaylistAsync();
        var original = await f.Queue();
        Check(!f.Module.CanHeartbeatCurrent && f.Module.CanRecommendCurrent, "ordinary remote playlist only supports single-song recommendation");
        int calls = f.Platform.Requests.OfType<MusicHeartbeatRequest>().Count();
        await f.Module.HeartbeatCurrentAsync(f.Module.PlaybackPresentationKey);
        Equal(calls, f.Platform.Requests.OfType<MusicHeartbeatRequest>().Count());
        await f.Module.CatalogQueueRecommendationsAsync();
        var request = f.Platform.Last<MusicQueueRecommendationsRequest>();
        Equal(MusicSongFixture.NativeId(original.GetProperty("current").GetProperty("song")), request.SeedSongId);
        Equal(0, request.Offset);
        var input = f.App.Last("music.recommendations.start");
        Equal("queue", S(input, "kind")); Check(input.GetProperty("restore").GetBoolean());
        Equal(2, f.Store.LibraryWrites); Equal(1, f.Output.Opens);
        Equal(S(original, "currentId"), S(await f.Queue(), "currentId"));
    }

    private static async Task HeartbeatFavoritesEntries()
    {
        await using var f = await Fixture.Create();
        f.Platform.HomeEntries = [new { id = "heart", name = "心动模式", available = true, presentation = "static",
            action = new { type = "heartbeat", entry = "favorites", songId = "101" } }];
        await f.Module.NavigateAsync("catalog-home", default);
        var home = f.Module.CatalogSections.Single().Items.Single();
        Check(home.CanPlay && home.CanOpen && home.ActionId.Length > 0, "home heartbeat survives action validation");
        await f.Module.PlayCatalogCardAsync(home);
        Equal(MusicHeartbeatEntry.Favorites, f.Platform.Last<MusicHeartbeatRequest>().Entry);
        var input = f.App.Last("music.recommendations.start");
        Equal("heartbeat", S(input, "kind")); Equal("favorites", S(input, "entry"));
        Check(!input.TryGetProperty("continue", out _) && S(input, "songId") == "101" &&
            !input.TryGetProperty("seedEntryId", out _), "home heartbeat uses the displayed seed, not a random replacement");
        Equal("101", MusicSongFixture.NativeId((await f.Queue()).GetProperty("current").GetProperty("song")));
        var recommendation = await f.Queue();
        int opens = f.Output.Opens, resumes = f.Output.Resumes;
        await f.Module.ReturnRecommendationAsync();
        var returned = await f.Queue();
        Equal(1L, N(returned, "total")); Equal(S(recommendation, "currentId"), S(returned, "currentId"));
        Equal(recommendation.GetProperty("current").GetRawText(), returned.GetProperty("current").GetRawText());
        Equal(S(returned, "currentId"), f.Output.Identity!);
        Equal(opens, f.Output.Opens); Equal(resumes, f.Output.Resumes);
        Check(f.Output.HasMedia, "empty original queue keeps only the currently playing recommendation");
        await f.Module.NavigateAsync("catalog-personal", default);
        var favorite = f.Module.CatalogSections.Single().Items.Single();
        Check(favorite.CanHeartbeatFavorites && favorite.Kind == "playlist", "owned cloud favorites keep playlist navigation");
        await f.Module.HeartbeatFavoritesCatalogCardAsync(favorite);
        Equal(MusicHeartbeatEntry.Favorites, f.Platform.Last<MusicHeartbeatRequest>().Entry);
        int starts = f.App.Log.Count(call => call.Method == "music.recommendations.start");
        await f.Module.NavigateAsync("catalog-daily", default);
        await f.Module.HeartbeatFavoritesCatalogCardAsync(favorite);
        Equal(starts, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
    }
    private static async Task HeartbeatCurrentEntry()
    {
        await using var f = await Fixture.Create();
        f.Platform.PlaylistChange = request => Edit(Page(request, 3), value => value["heartbeatAccountId"] = "42");
        await f.Module.NavigateAsync("catalog-playlist/wy/555", default); await f.Module.PlayCatalogPlaylistAsync();
        Check(f.Module.CanHeartbeatCurrent, "current occurrence carries verified official playlist/account context");
        var before = await f.Queue(); string key = f.Module.PlaybackPresentationKey;
        var footer = (MusicPlayerView)f.Module.PlayerView;
        using var surface = new HwndSource(new HwndSourceParameters("recommendation-current-heartbeat-fixture") {
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
            PositionX = -32000, PositionY = -32000, Width = 1110, Height = 100 });
        surface.RootVisual = footer; Layout(footer, 1110, 100); await Drain();
        var mode = (Button)footer.FindName("ModeButton");
        var menu = (ContextMenu)footer.FindName("ModeMenu");
        var sourceButton = (Button)footer.FindName("PlaybackSessionButton");
        var source = (ContextMenu)footer.FindName("PlaybackSessionMenu");
        f.App.FailNext("music.recommendations.start", "NETWORK_ERROR");
        await f.Module.HeartbeatCurrentAsync(key); await Drain();
        Check(!f.Module.HasPlaybackHeartbeat && f.Module.PlaybackModeIcon == "repeat");
        Equal(before.GetProperty("entries").GetRawText(), (await f.Queue()).GetProperty("entries").GetRawText());
        sourceButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
        var heartbeat = source.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "从当前歌曲进入心动"));
        Check(heartbeat.IsEnabled);
        var hold = f.App.HoldNext("music.recommendations.start", before: true);
        ((IInvokeProvider)new MenuItemAutomationPeer(heartbeat).GetPattern(PatternInterface.Invoke)).Invoke();
        await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { Check(!f.Module.HasPlaybackHeartbeat && f.Module.PlaybackModeIcon == "repeat"); }
        finally { hold.Release(); }
        await Until(() => f.Module.HasPlaybackHeartbeat && !f.Module.RecommendationLoading);
        Equal("repeat", f.Module.PlaybackModeIcon); Equal("列表循环", AutomationProperties.GetName(mode));
        var input = f.App.Last("music.recommendations.start");
        Equal("heartbeat", S(input, "kind")); Equal("current", S(input, "entry")); Equal("555", S(input, "playlistId"));
        Equal(S(before, "currentId"), S(input, "seedEntryId")); Equal(S(before, "currentId"), S(await f.Queue(), "currentId"));
        Check(!f.Module.CanChangeRecommendationContinue && !f.Module.RecommendationContinue, "heartbeat has no unverified continuation");
        Equal("fm", ((MusicPlaybackIcon)sourceButton.Content).Key);
        mode.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
        Equal(3, menu.Items.Count); SaveRecommendationFrame(menu, "heartbeat-global-loop-menu.png");
        MeowFilm.Desktop.DesktopTheme.SetIsHighContrast(menu, true); await Drain();
        var selected = menu.Items.OfType<MenuItem>().Single(item => item.IsChecked);
        Equal(1, (int)selected.Tag!);
        Equal(SystemColors.HighlightTextBrush.Color, ((SolidColorBrush)selected.Foreground).Color);
        Equal(SystemColors.HighlightBrush.Color, ((SolidColorBrush)selected.Background).Color);
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            var content = (StackPanel)item.Header;
            Equal(((SolidColorBrush)item.Foreground).Color,
                ((SolidColorBrush)content.Children.OfType<TextBlock>().Single().Foreground).Color);
            Equal(((SolidColorBrush)item.Foreground).Color,
                ((SolidColorBrush)content.Children.OfType<MusicPlaybackIcon>().Single().Foreground).Color);
        }
        SaveRecommendationFrame(menu, "heartbeat-global-loop-menu-high-contrast.png");
        MeowFilm.Desktop.DesktopTheme.SetIsHighContrast(menu, false); await Drain();
        menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, 3)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => f.Module.Mode == 3 && !f.Module.Busy); Check(f.Module.HasPlaybackHeartbeat);
        await f.Module.StepAsync(true);
        int starts = f.App.Log.Count(call => call.Method == "music.recommendations.start");
        await f.Module.HeartbeatCurrentAsync(key); Equal(starts, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
        var recommendation = await f.Queue(); string playing = S(recommendation, "currentId");
        int opens = f.Output.Opens, resumes = f.Output.Resumes;
        sourceButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
        Check(source.Items.OfType<MenuItem>().Select(item => item.Header)
            .SequenceEqual(new object[] { "心动模式", "退出心动" }));
        var returning = source.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "退出心动"));
        ((IInvokeProvider)new MenuItemAutomationPeer(returning).GetPattern(PatternInterface.Invoke)).Invoke();
        await Until(() => !f.Module.HasPlaybackHeartbeat && !f.Module.HasActiveRecommendation);
        var returned = await f.Queue(); var original = before.GetProperty("entries").EnumerateArray().ToList();
        original.Insert(1, recommendation.GetProperty("current"));
        Equal(string.Join(',', original.Select(item => S(item, "id"))),
            string.Join(',', returned.GetProperty("entries").EnumerateArray().Select(item => S(item, "id"))));
        Equal(playing, S(returned, "currentId")); Equal(playing, f.Output.Identity!);
        Equal(opens, f.Output.Opens); Equal(resumes, f.Output.Resumes); Check(f.Output.HasMedia);
        Equal("single", f.Module.PlaybackModeIcon); Equal("单曲循环", AutomationProperties.GetName(mode));
        Equal("fm-off", ((MusicPlaybackIcon)sourceButton.Content).Key);
        surface.RootVisual = null;
    }

    private static async Task HeartbeatAccountQualification()
    {
        await using var f = await Fixture.Create();
        f.Platform.PlaylistChange = request => Edit(Page(request, 3), value => value["heartbeatAccountId"] = "42");
        await f.Module.NavigateAsync("catalog-playlist/wy/555", default); await f.Module.PlayCatalogPlaylistAsync();
        var before = await f.Queue();
        var view = f.Module.View; var notices = new List<string>();
        view.AddHandler(MeowFilm.Desktop.NoticeOutlet.NoticeEvent,
            new EventHandler<MeowFilm.Desktop.DesktopNoticeEventArgs>((_, args) => notices.Add(args.Message)));
        using var surface = new HwndSource(new HwndSourceParameters("heartbeat-account-entry-fixture") {
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
            PositionX = -32000, PositionY = -32000, Width = 860, Height = 120 });
        surface.RootVisual = view; Layout(view, 860, 120); await Drain();
        f.App.Authenticated = false; f.App.AccountGeneration++;
        await f.Module.ReadAccountsAsync();
        Check(f.Module.CanHeartbeatCurrent, "only missing login must not hide a qualified official entry");
        await f.Module.HeartbeatCurrentAsync(f.Module.PlaybackPresentationKey); await Drain();
        Check(notices.Count > 0 && !f.Platform.Requests.OfType<MusicHeartbeatRequest>().Any());
        Equal(before.GetProperty("entries").GetRawText(), (await f.Queue()).GetProperty("entries").GetRawText());
        Equal(1, f.Output.Opens);
        f.App.Authenticated = true; f.App.AccountUserId = "43"; f.App.AccountGeneration++;
        await f.Module.ReadAccountsAsync();
        Check(!f.Module.CanHeartbeatCurrent, "another known account does not inherit the official context");
        await f.Module.HeartbeatCurrentAsync(f.Module.PlaybackPresentationKey);
        Check(!f.Platform.Requests.OfType<MusicHeartbeatRequest>().Any());
        f.App.AccountUserId = "42"; f.App.AccountGeneration++;
        await f.Module.ReadAccountsAsync(); Check(f.Module.CanHeartbeatCurrent);
        surface.RootVisual = null;
    }
    private static async Task SourceNotices()
    {
        await using var f = await Fixture.Create();
        f.Platform.PlaylistChange = request => Edit(Page(request, 3), value => value["heartbeatAccountId"] = "42");
        await f.Module.NavigateAsync("catalog-playlist/wy/555", default); await f.Module.PlayCatalogPlaylistAsync();
        var view = f.Module.View; var notices = new List<string>();
        view.AddHandler(MeowFilm.Desktop.NoticeOutlet.NoticeEvent,
            new EventHandler<MeowFilm.Desktop.DesktopNoticeEventArgs>((_, args) => notices.Add(args.Message)));
        using var surface = new HwndSource(new HwndSourceParameters("source-notice-fixture") {
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
            PositionX = -32000, PositionY = -32000, Width = 860, Height = 120 });
        surface.RootVisual = view; Layout(view, 860, 120); await Drain(); notices.Clear();
        await f.Module.HeartbeatCurrentAsync(f.Module.PlaybackPresentationKey); await Drain();
        Check(f.Module.HasPlaybackHeartbeat && f.Module.CanReturnRecommendation);
        await f.Module.ChangeModeAsync(2);
        await f.Module.ReturnRecommendationAsync(); await Drain();
        await f.Module.RecommendCurrentAsync(f.Module.PlaybackPresentationKey); await Drain();
        Check(f.Module.HasQueueRecommendations && f.Module.CanReturnRecommendation);
        await f.Module.ChangeRecommendationContinueAsync(false);
        await f.Module.ReturnRecommendationAsync(); await Drain();
        Equal(0, notices.Count);
        await f.Module.NavigateAsync("catalog-fm", default); notices.Clear();
        await f.Module.PlayCatalogPlaylistAsync(); await Drain();
        await f.Module.ChangePlaybackFmAsync("scene", "ELECTRONIC", f.Module.PlaybackFmActionKey); await Drain();
        await f.Module.ChangeRecommendationContinueAsync(false); await f.Module.CancelRecommendationAsync(); await Drain();
        Equal(0, notices.Count);
        await f.Module.NavigateAsync("catalog-daily", default); notices.Clear();
        await f.Module.SimilarCatalogCardAsync(FirstSong(f)); await Drain();
        Check(f.Module.RecommendationTitle == "相似歌曲" && f.Module.HasActiveRecommendation);
        await f.Module.CancelRecommendationAsync(); await Drain(); Equal(0, notices.Count);
        f.App.FailNext("music.recommendations.start", "NETWORK_ERROR");
        await f.Module.SimilarCatalogCardAsync(FirstSong(f)); await Drain();
        Check(notices.Count == 1 && notices[0] == f.Module.Status);
        surface.RootVisual = null;
    }

    private static async Task PrepareSearch(Fixture f)
    {
        await f.Module.NavigateAsync("catalog-search", default); await f.Module.SearchAsync("合成歌曲", default);
        await Until(() => f.Module.Songs.Count > 0 && !f.Module.SearchSongsLoading);
        f.Module.SelectedSong = f.Module.Songs.Last();
    }
    private static async Task PlaySearch(Fixture f)
    {
        await f.Module.PlaySelectionAsync();
        f.Output.Start();
    }
    private static async Task QueueCandidateActions()
    {
        await using var f = await Fixture.Create();
        await PrepareSearch(f);
        int writes = f.Store.LibraryWrites;
        await PlaySearch(f);
        await Until(() => f.Module.HasQueueRecommendations && !f.Module.RecommendationLoading);
        var before = await f.Queue();
        Equal(1L, N(before, "total")); Equal(writes + 2, f.Store.LibraryWrites);
        Equal(1, f.App.Log.Count(call => call.Method == "music.history.recordPlayed"));
        Check(f.Module.HasQueueRecommendations && f.Module.RecommendationContinue);
        Equal(3, f.Module.RecommendationCandidates.Count);
        Equal(1, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
        Equal("search", S(before.GetProperty("source"), "kind"));
        var footer = (MusicPlayerView)f.Module.PlayerView;
        using var surface = new HwndSource(new HwndSourceParameters("search-source-controls-fixture") {
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
            PositionX = -32000, PositionY = -32000, Width = 1110, Height = 100 });
        surface.RootVisual = footer; Layout(footer, 1110, 100); await Drain();
        var sessionButton = (Button)footer.FindName("PlaybackSessionButton");
        sessionButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
        var sourceMenu = (ContextMenu)footer.FindName("PlaybackSessionMenu");
        Check(sourceMenu.Items.OfType<MenuItem>().Select(item => item.Header)
            .SequenceEqual(new object[] { "搜索结果", "关闭续播", "重试取歌" }));
        Equal("fm", ((MusicPlaybackIcon)sessionButton.Content).Key);
        sourceMenu.IsOpen = false; surface.RootVisual = null;
        var candidate = f.Module.RecommendationCandidates[1];
        var stale = f.Module.RecommendationCandidates[0];
        await f.Module.ChooseRecommendationAsync(candidate);
        var chosen = await f.Queue();
        Equal(4L, N(chosen, "total")); Equal(writes + 3, f.Store.LibraryWrites); Equal(2, f.Output.Opens);
        Equal(candidate.Song.Id, S(chosen.GetProperty("current").GetProperty("song"), "songId"));
        Check(chosen.GetProperty("entries").EnumerateArray().Take(1).Select(row => row.GetRawText())
            .SequenceEqual(before.GetProperty("entries").EnumerateArray().Select(row => row.GetRawText())),
            "preview and append preserve original occurrence context");
        Equal("queue", S(chosen.GetProperty("source"), "kind"));
        int chooses = f.App.Log.Count(call => call.Method == "music.recommendations.choose");
        await f.Module.ChooseRecommendationAsync(stale);
        Equal(chooses, f.App.Log.Count(call => call.Method == "music.recommendations.choose"));
        await f.Module.CancelRecommendationAsync();
        await f.Module.ChooseRecommendationAsync(candidate);
        Equal(chooses, f.App.Log.Count(call => call.Method == "music.recommendations.choose"));
        await f.Module.NavigateAsync("catalog-fm", default); await f.Module.PlayCatalogPlaylistAsync();
        await Drain();
        int starts = f.App.Log.Count(call => call.Method == "music.recommendations.start");
        await f.Module.TogglePlaybackAsync(); await Drain();
        Equal(starts, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
        Check(!f.Module.HasQueueRecommendations, "FM stays its own session");
    }
    private static async Task SearchQueuePreview(string tab)
    {
        await using var f = await Fixture.Create();
        f.Platform.SearchCount = 2;
        await f.Module.NavigateAsync("catalog-search", default);
        await f.Module.SearchAsync("合成歌曲", default);
        await Until(() => f.Module.Songs.Count > 0 && !f.Module.SearchSongsLoading);
        if (tab == "songs") await f.Module.OpenSearchTabAsync(tab);
        Equal(tab, f.Module.SearchTab);
        int writes = f.Store.LibraryWrites;
        f.Module.SelectedSong = f.Module.Songs.Last();
        string clicked = RecommendationJson.NativeId(f.Module.SelectedSong.Raw);
        Check(clicked != RecommendationJson.NativeId(f.Module.Songs[0].Raw), "fixture must distinguish clicked seed from the first search result");
        int reads = f.App.Log.Count(call => call.Method == "music.recommendations.get");
        await f.Module.PlaySelectionAsync(); await Drain();
        Equal(1, f.Output.Opens);
        var beforeStarted = await f.Queue();
        Equal(1L, N(beforeStarted, "total"));
        Equal(clicked, MusicSongFixture.NativeId(beforeStarted.GetProperty("current").GetProperty("song")));
        Equal(reads, f.App.Log.Count(call => call.Method == "music.recommendations.get"));
        Equal(0, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
        Equal(0, f.Platform.Requests.OfType<MusicQueueRecommendationsRequest>().Count());
        Check(!f.Module.HasQueueRecommendations, "accepted Open is not a real playback start");
        f.Output.Start();
        await Until(() => f.Module.HasQueueRecommendations && !f.Module.RecommendationLoading);
        Equal(1, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
        Equal("queue", S(f.App.Last("music.recommendations.start"), "kind"));
        var queue = await f.Queue();
        Equal(1L, N(queue, "total")); Equal(writes + 2, f.Store.LibraryWrites);
        Equal(1, queue.GetProperty("entries").GetArrayLength()); Equal(1, f.Output.Opens);
        Equal(clicked, MusicSongFixture.NativeId(queue.GetProperty("current").GetProperty("song")));
        Equal("search", S(queue.GetProperty("current").GetProperty("context"), "sourceType"));
        Equal("合成歌曲", S(queue.GetProperty("current").GetProperty("context"), "searchKeyword"));
        var source = queue.GetProperty("source");
        Equal("search", S(source, "id")); Equal("搜索结果", S(source, "title")); Equal("search", S(source, "kind"));
        Check(!source.TryGetProperty("platform", out _), "cross-platform search source never invents a platform");
        Equal(1, f.Platform.Requests.OfType<MusicQueueRecommendationsRequest>().Count());
        Equal(clicked, f.Platform.Last<MusicQueueRecommendationsRequest>().SeedSongId);
        Check(!f.Module.CanReturnRecommendation, "search preview never captures the replaced queue");
    }
    private static async Task QueuePreviewRetirement(string retirement, bool failed)
    {
        await using var f = await Fixture.Create();
        await f.Module.ReadAccountsAsync();
        await PrepareSearch(f);
        var held = f.App.HoldNext("music.recommendations.get");
        if (failed) f.App.TransformNext("music.recommendations.get", _ => E(new { ok = false, code = "NETWORK_ERROR" }));
        await PlaySearch(f);
        await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var before = await f.Queue();
        string status = f.Module.Status;
        if (retirement == "stopped") await f.Output.StopAsync();
        else if (retirement == "owner-replaced")
            await f.Output.OpenAsync("film", "film-entry", new Uri("https://media.invalid/film"), "合成影片", default);
        else
        {
            f.App.FailNext("music.accounts.logout", "NETWORK_ERROR");
            await f.Module.LogoutAccountAsync(f.Module.Accounts.Single(row => row.Platform == "wy"), confirmed: true);
            Equal(1, f.App.Log.Count(call => call.Method == "music.accounts.logout"));
        }
        held.Release();
        await held.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Drain();
        Equal(0, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
        Equal(0, f.Platform.Requests.OfType<MusicQueueRecommendationsRequest>().Count());
        Equal(status, f.Module.Status);
        Equal(before.GetRawText(), (await f.Queue()).GetRawText());
        Check(!f.Module.HasQueueRecommendations, "retired preview cannot create a session or publish its error");
    }
    private static async Task QueuePreviewFailureNotice()
    {
        await using var f = await Fixture.Create();
        var view = f.Module.View;
        var notices = new List<string>();
        view.AddHandler(MeowFilm.Desktop.NoticeOutlet.NoticeEvent,
            new EventHandler<MeowFilm.Desktop.DesktopNoticeEventArgs>((_, args) => notices.Add(args.Message)));
        using var surface = new HwndSource(new HwndSourceParameters("recommendation-preview-notice-fixture") {
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80,
            PositionX = -32000, PositionY = -32000, Width = 860, Height = 120 });
        surface.RootVisual = view; Layout(view, 860, 120); await Drain();
        await PrepareSearch(f);
        int writes = f.Store.LibraryWrites;
        f.App.FailNext("music.recommendations.get", "NETWORK_ERROR");
        await PlaySearch(f);
        await Until(() => f.Module.Status.Length > 0 && notices.Contains(f.Module.Status));
        Check(!f.Module.HasRecommendationSession && !f.Module.HasQueueRecommendations,
            "an admission read failure notifies without inventing a recommendation session");
        Equal(0, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
        Equal(1L, N(await f.Queue(), "total")); Equal(writes + 2, f.Store.LibraryWrites); Equal(1, f.Output.Opens);
        Equal(1, f.App.Log.Count(call => call.Method == "music.history.recordPlayed"));
        await f.Module.CatalogQueueRecommendationsAsync();
        Check(f.Module.HasQueueRecommendations && f.Module.CanReturnRecommendation,
            "explicit recommendation can enter after admission failure and has its own checkpoint");
        Equal(4L, N(await f.Queue(), "total")); Equal(writes + 3, f.Store.LibraryWrites); Equal(1, f.Output.Opens);
        surface.RootVisual = null;
    }
    private static async Task PausedQueuePreview()
    {
        await using var f = await Fixture.Create();
        await PrepareSearch(f);
        int writes = f.Store.LibraryWrites;
        var held = f.App.HoldNext("music.recommendations.get");
        await PlaySearch(f);
        await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Output.PauseAsync();
        Check(f.Output.HasMedia && !f.Output.IsPlaying);
        var before = await f.Queue();
        held.Release();
        await Until(() => f.Module.HasQueueRecommendations && !f.Module.RecommendationLoading);
        Equal(1, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
        Equal(3, f.Module.RecommendationCandidates.Count);
        Equal(before.GetRawText(), (await f.Queue()).GetRawText());
        Equal(writes + 2, f.Store.LibraryWrites); Equal(1, f.Output.Opens);
        Equal(1, f.App.Log.Count(call => call.Method == "music.history.recordPlayed"));
        Equal(0, f.Output.Resumes);
        Check(!f.Output.IsPlaying, "read-only preview does not resume paused playback");
    }
    private static async Task SearchStartedGate(string outcome)
    {
        await using var f = await Fixture.Create();
        await f.Module.ReadAccountsAsync();
        await PrepareSearch(f);
        string seed = RecommendationJson.NativeId(f.Module.SelectedSong!.Raw);
        int reads = f.App.Log.Count(call => call.Method == "music.recommendations.get");
        await f.Module.PlaySelectionAsync();
        await Drain();
        Equal(1, f.Output.Opens); Equal(1L, N(await f.Queue(), "total"));
        Equal(reads, f.App.Log.Count(call => call.Method == "music.recommendations.get"));
        Equal(0, f.Platform.Requests.OfType<MusicQueueRecommendationsRequest>().Count());
        Check(!f.Module.HasQueueRecommendations, "accepted Open is not a real playback start");
        if (outcome == "paused-before-start")
        {
            await f.Output.PauseAsync(); await f.Output.SeekAsync(12);
            await f.Module.ChangeModeAsync(2);
            await f.Module.NavigateAsync("catalog-home", default); await Drain();
            Equal(reads, f.App.Log.Count(call => call.Method == "music.recommendations.get"));
            Equal(0, f.Platform.Requests.OfType<MusicQueueRecommendationsRequest>().Count());
            await f.Output.ResumeAsync();
        }
        else
        {
            if (outcome == "failed") f.Output.Fail();
            else if (outcome == "stopped") await f.Output.StopAsync();
            else if (outcome == "owner-replaced")
                await f.Output.OpenAsync("film", "film-entry", new Uri("https://media.invalid/film"), "合成影片", default);
            else if (outcome == "source-replaced")
            {
                await f.Module.NavigateAsync("catalog-daily", default);
                await f.Module.PlayCatalogPlaylistAsync();
            }
            else if (outcome == "account-intent")
            {
                f.App.FailNext("music.accounts.logout", "NETWORK_ERROR");
                await f.Module.LogoutAccountAsync(f.Module.Accounts.Single(row => row.Platform == "wy"), confirmed: true);
            }
            else await f.Module.NavigateAsync("catalog-home", default);
            f.Output.Start();
        }
        if (outcome is "started" or "paused-before-start")
        {
            await Until(() => f.Module.HasQueueRecommendations && !f.Module.RecommendationLoading);
            Equal(1, f.Platform.Requests.OfType<MusicQueueRecommendationsRequest>().Count());
            Equal(seed, f.Platform.Last<MusicQueueRecommendationsRequest>().SeedSongId);
            await f.Output.PauseAsync(); await f.Output.SeekAsync(24); await f.Output.ResumeAsync();
            await f.Output.OpenAsync(f.Output.OwnerId!, f.Output.Identity!, new Uri("https://media.invalid/other-source"),
                "合成换源", default);
            f.Output.Start(); await Drain();
            Equal(1, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
            Equal(1L, N(await f.Queue(), "total"));
        }
        else
        {
            await Drain();
            Equal(0, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
            Equal(0, f.Platform.Requests.OfType<MusicQueueRecommendationsRequest>().Count());
            Check(!f.Module.HasQueueRecommendations, "retired pre-start intent must not revive");
        }
    }
    private static async Task SearchSynchronousStarted()
    {
        await using var f = await Fixture.Create();
        await PrepareSearch(f); f.Output.StartOnOpen = true;
        await f.Module.PlaySelectionAsync();
        await Until(() => f.Module.HasQueueRecommendations && !f.Module.RecommendationLoading);
        f.Output.Start(); await Drain();
        Equal(1, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
        Equal(1, f.Platform.Requests.OfType<MusicQueueRecommendationsRequest>().Count());
        Equal(1L, N(await f.Queue(), "total")); Equal(1, f.Output.Opens);
    }
    private static async Task QueuePreviewReentrantIntent()
    {
        await using var f = await Fixture.Create();
        await PrepareSearch(f);
        var held = f.App.HoldNext("music.recommendations.get");
        await PlaySearch(f);
        await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Module.NavigateAsync("catalog-fm", default);
        Task? started = null;
        bool issued = false;
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (!issued && args.PropertyName == nameof(MusicCapability.HasRecommendationSession))
            {
                issued = true;
                started = f.Module.PlayCatalogPlaylistAsync();
            }
        };
        f.Module.PropertyChanged += handler;
        try
        {
            held.Release();
            await Until(() => started is not null);
            await started!;
            await Drain();
            Equal(1, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
            Equal("fm", S(f.App.Last("music.recommendations.start"), "kind"));
            Check(f.Module.HasPlaybackFm && !f.Module.HasQueueRecommendations,
                "publication reentry preserves the explicit recommendation intent");
        }
        finally { f.Module.PropertyChanged -= handler; }
    }
    private static async Task PlaybackFmChoices()
    {
        await using var f = await Fixture.Create();
        await f.Module.NavigateAsync("catalog-fm", default);
        Check(!f.Module.HasPlaybackFm, "catalog preview is not active FM");
        await f.Module.PlayCatalogPlaylistAsync(); await Drain();
        Check(f.Module.HasPlaybackFm); Equal("default", f.Module.PlaybackFmMode);
        Equal(4, f.Module.PlaybackFmModes.Length); Equal(43, f.Module.CatalogFmScenes.Length);
        Equal("放克", f.Module.CatalogFmScenes.Single(choice => choice.Key == "PUNK").Name);
        foreach (string mode in new[] { "familiar", "explore", "puzzle", "default" })
        {
            await f.Module.ChangePlaybackFmAsync(mode, "", f.Module.PlaybackFmActionKey);
            Equal(mode, f.Module.PlaybackFmMode);
        }
        await f.Module.ChangePlaybackFmAsync("scene", "HOT_SONG", f.Module.PlaybackFmActionKey);
        Equal("scene", f.Module.PlaybackFmMode); Equal("HOT_SONG", f.Module.PlaybackFmScene);
        Equal("热歌", f.Module.PlaybackFmTitle); Equal(f.Module.ModeText, f.Module.PlaybackModeText);
        f.Module.CatalogFmMode = "familiar";
        Equal("热歌", f.Module.PlaybackFmTitle); Equal(f.Module.ModeText, f.Module.PlaybackModeText);
        var held = f.Platform.HoldNextFm();
        string oldKey = f.Module.PlaybackFmActionKey;
        var failed = f.Module.ChangePlaybackFmAsync("explore", "", oldKey);
        await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Drain();
        Equal("热歌", f.Module.PlaybackFmTitle); Equal(f.Module.ModeText, f.Module.PlaybackModeText);
        held.Fail("NETWORK_ERROR"); await failed; await Drain();
        Equal("热歌", f.Module.PlaybackFmTitle); Equal(f.Module.ModeText, f.Module.PlaybackModeText);
        int requests = f.Platform.Requests.Count;
        await f.Module.ChangePlaybackFmAsync("default", "", oldKey);
        Equal(requests, f.Platform.Requests.Count);
        await f.Module.CancelRecommendationAsync();
        Check(!f.Module.HasPlaybackFm); Equal(f.Module.ModeText, f.Module.PlaybackModeText);
    }
    private static async Task RecommendationAccountWaitRetirement(bool restore, bool failed)
    {
        await using var f = await Fixture.Create();
        JsonElement checkpoint = default;
        if (restore)
        {
            f.Platform.PlaylistChange = request => Edit(Page(request, 3), value => value["heartbeatAccountId"] = "42");
            await f.Module.NavigateAsync("catalog-playlist/wy/555", default); await f.Module.PlayCatalogPlaylistAsync();
            checkpoint = await f.Queue();
            await f.Module.HeartbeatCurrentAsync(f.Module.PlaybackPresentationKey); await Drain();
            Equal("心动模式", f.Module.RecommendationTitle); Check(f.Module.CanReturnRecommendation);
        }
        else
        {
            await f.Module.NavigateAsync("catalog-fm", default);
            await f.Module.PlayCatalogPlaylistAsync();
            await Drain();
            Check(f.Module.HasPlaybackFm && f.Module.CanCancelRecommendation);
        }

        int accountReads = f.App.Log.Count(call => call.Method == "music.accounts.list");
        int starts = f.App.Log.Count(call => call.Method == "music.recommendations.start");
        int fmRequests = f.Platform.Requests.OfType<MusicFmRequest>().Count();
        var held = f.App.HoldNext("music.accounts.list", before: true);
        if (failed) f.App.FailNext("music.accounts.list", "TIMEOUT");
        var reading = f.Module.ReadAccountsAsync();
        Task starting = Task.CompletedTask;
        try
        {
            await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            starting = restore
                ? f.Module.HeartbeatCurrentAsync(f.Module.PlaybackPresentationKey)
                : f.Module.ChangePlaybackFmAsync("explore", "", f.Module.PlaybackFmActionKey);
            await Drain();
            Check(!reading.IsCompleted && !starting.IsCompleted,
                "the new recommendation waits for the single held account read");
            Check(!held.Cancelled.Task.IsCompleted, "the pending start shares rather than cancels account authority");
            Equal(accountReads + 1, f.App.Log.Count(call => call.Method == "music.accounts.list"));
            Equal(starts, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
            Equal(fmRequests, f.Platform.Requests.OfType<MusicFmRequest>().Count());

            await (restore ? f.Module.ReturnRecommendationAsync() : f.Module.CancelRecommendationAsync())
                .WaitAsync(TimeSpan.FromSeconds(5));
            await Drain();
            var settled = await f.Queue();
            if (restore)
            {
                Equal(checkpoint.GetProperty("entries").GetRawText(), settled.GetProperty("entries").GetRawText());
                Equal(S(checkpoint, "currentId"), S(settled, "currentId"));
                Equal(N(checkpoint, "mode"), N(settled, "mode"));
            }
            Check(!f.Module.HasActiveRecommendation, "exit completes before the shared account read");
            Check(!reading.IsCompleted && !starting.IsCompleted && !held.Cancelled.Task.IsCompleted,
                "exit retires the intent without cancelling the shared account read");
            int writes = f.Store.LibraryWrites, opens = f.Output.Opens, resumes = f.Output.Resumes;
            string? owner = f.Output.OwnerId, identity = f.Output.Identity;
            bool hasMedia = f.Output.HasMedia;
            string status = f.Module.Status;
            Equal(accountReads + 1, f.App.Log.Count(call => call.Method == "music.accounts.list"));
            Equal(starts, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
            Equal(fmRequests, f.Platform.Requests.OfType<MusicFmRequest>().Count());

            held.Release();
            await Task.WhenAll(reading, starting).WaitAsync(TimeSpan.FromSeconds(5));
            await Drain();
            Equal(accountReads + 1, f.App.Log.Count(call => call.Method == "music.accounts.list"));
            Check(!held.Cancelled.Task.IsCompleted, "late account settlement remains a shared uncancelled read");
            Equal(starts, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
            Equal(fmRequests, f.Platform.Requests.OfType<MusicFmRequest>().Count());
            Equal(writes, f.Store.LibraryWrites);
            Equal(settled.GetRawText(), (await f.Queue()).GetRawText());
            Equal(opens, f.Output.Opens); Equal(resumes, f.Output.Resumes);
            Equal(owner, f.Output.OwnerId); Equal(identity, f.Output.Identity); Equal(hasMedia, f.Output.HasMedia);
            Equal(status, f.Module.Status);
            Check(!f.Module.HasActiveRecommendation, "late success or failure cannot revive the retired intent");
        }
        finally
        {
            held.Release();
            await Task.WhenAll(reading, starting).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
    private static async Task PlaybackFmPopup()
    {
        await using var f = await Fixture.Create();
        var footer = (MusicPlayerView)f.Module.PlayerView;
        using var surface = new HwndSource(new HwndSourceParameters("recommendation-fm-footer-fixture") {
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80,
            PositionX = -32000, PositionY = -32000, Width = 1110, Height = 100 });
        surface.RootVisual = footer; Layout(footer, 1110, 100); await Drain();
        var mode = (Button)footer.FindName("ModeButton");
        var menu = (ContextMenu)footer.FindName("ModeMenu");
        var sessionButton = (Button)footer.FindName("PlaybackSessionButton");
        var popup = (Popup)footer.FindName("FmPopup");
        mode.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
        Equal(1, f.Module.Mode); Check(menu.IsOpen && !popup.IsOpen, "ordinary playback opens choices without a write");
        var ordinary = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, 2));
        Equal(f.Module.ModeText, AutomationProperties.GetName(mode));
        await f.Module.NavigateAsync("catalog-fm", default); await f.Module.PlayCatalogPlaylistAsync(); await Drain();
        Check(!menu.IsOpen, "playback identity change closes the old global menu");
        ordinary.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Drain();
        Equal(1, f.Module.Mode);
        mode.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
        Check(menu.IsOpen && !popup.IsOpen, "FM opens the independent global loop menu");
        mode.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
        Check(!menu.IsOpen, "global menu closes independently of FM entry");
        Equal("repeat", f.Module.PlaybackModeIcon);
        sessionButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
        var sourceMenu = (ContextMenu)footer.FindName("PlaybackSessionMenu");
        Check(sourceMenu.Items.OfType<MenuItem>().Select(item => item.Header)
            .SequenceEqual(new object[] { "私人 FM", "关闭续播", "频道切换", "重试取歌" }));
        var channel = sourceMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "频道切换"));
        ((IInvokeProvider)new MenuItemAutomationPeer(channel).GetPattern(PatternInterface.Invoke)).Invoke(); await Drain();
        Check(popup.IsOpen, "live WY FM opens mode selection");
        var modes = (StackPanel)footer.FindName("FmModeChoices");
        var scenes = (UniformGrid)footer.FindName("FmSceneChoices");
        Equal(4, modes.Children.Count); Equal(43, scenes.Children.Count); Equal(4, scenes.Columns);
        SaveRecommendationFrame(footer, "fm-footer-default-width.png");
        SaveRecommendationFrame((FrameworkElement)popup.Child, "fm-popup-default.png");
        SaveRecommendationFrame(scenes, "fm-scenes-all.png");
        Check(scenes.Children.OfType<Button>().All(button => Visuals<MusicPlaybackIcon>(button).Count() == 1),
            "every scene has its native icon");
        var old = modes.Children.OfType<Button>().Single(button => AutomationProperties.GetName(button) == "探索模式");
        var hot = scenes.Children.OfType<Button>().Single(button => AutomationProperties.GetName(button) == "热歌");
        hot.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Until(() => f.Module.PlaybackFmScene == "HOT_SONG" && !f.Module.RecommendationLoading);
        Check(!popup.IsOpen);
        sessionButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
        channel = sourceMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "频道切换"));
        ((IInvokeProvider)new MenuItemAutomationPeer(channel).GetPattern(PatternInterface.Invoke)).Invoke(); await Drain();
        Check(popup.IsOpen);
        SaveRecommendationFrame((FrameworkElement)popup.Child, "fm-popup-hot-song.png");
        MeowFilm.Desktop.DesktopTheme.SetIsHighContrast(popup.Child, true); await Drain();
        var selected = scenes.Children.OfType<Button>().Single(button => AutomationProperties.GetItemStatus(button) == "已选中");
        Equal(SystemColors.HighlightTextBrush.Color, ((SolidColorBrush)selected.Foreground).Color);
        Equal(SystemColors.HighlightBrush.Color, ((SolidColorBrush)selected.Background).Color);
        Equal(SystemColors.HighlightTextBrush.Color,
            ((SolidColorBrush)((StackPanel)selected.Content).Children.OfType<TextBlock>().Single().Foreground).Color);
        SaveRecommendationFrame((FrameworkElement)popup.Child, "fm-popup-high-contrast.png");
        MeowFilm.Desktop.DesktopTheme.SetIsHighContrast(popup.Child, false); await Drain();
        int requests = f.Platform.Requests.Count;
        old.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
        Equal(requests, f.Platform.Requests.Count);
        var retired = ((StackPanel)footer.FindName("FmModeChoices")).Children.OfType<Button>().First();
        await f.Module.CancelRecommendationAsync(); await Drain();
        Check(!popup.IsOpen && !f.Module.HasPlaybackFm, "session exit closes the popup");
        retired.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
        Equal(requests, f.Platform.Requests.Count);
        mode.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
        Check(!popup.IsOpen && menu.IsOpen); Equal(1, f.Module.Mode);
        menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, 2)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => f.Module.Mode == 2 && !f.Module.Busy);
        var gallery = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.White };
        foreach (var (icon, label) in new[] { ("repeat", "列表循环"), ("single", "单曲循环"),
            ("shuffle", "随机播放"), ("heartbeat", "心动模式"), ("fm", "私人漫游") })
        {
            var cell = new StackPanel { Width = 90, Margin = new(0, 12, 0, 12) };
            cell.Children.Add(new MusicPlaybackIcon { Key = icon, Foreground = new SolidColorBrush(Color.FromRgb(119, 126, 144)),
                Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Center });
            cell.Children.Add(new TextBlock { Text = label, FontSize = 12, TextAlignment = TextAlignment.Center, Margin = new(0, 10, 0, 0) });
            gallery.Children.Add(cell);
        }
        Layout(gallery, 540, 74); SaveRecommendationFrame(gallery, "playback-mode-icons.png");
        surface.RootVisual = null;
    }
    private static async Task QueueCandidateSurface()
    {
        await using var f = await Fixture.Create();
        await PrepareSearch(f);
        await PlaySearch(f);
        await Until(() => f.Module.HasQueueRecommendations && !f.Module.RecommendationLoading);
        await f.Module.ActivateGlobalSurfaceAsync(MeowFilm.Desktop.DesktopGlobalSurface.PlayQueue, default);
        var view = (MusicQueueView)f.Module.GetGlobalSurface(MeowFilm.Desktop.DesktopGlobalSurface.PlayQueue);
        var host = new Border { Background = (Brush)view.FindResource("MeowFilm.Brush.Surface"), Child = view };
        using var surface = new HwndSource(new HwndSourceParameters("recommendation-queue-fixture") {
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80,
            PositionX = -32000, PositionY = -32000, Width = 386, Height = 720 });
        surface.RootVisual = host; Layout(host, 386, 720); await Drain();
        var queueList = (ListBox)view.FindName("QueueList");
        var candidates = (ListBox)view.FindName("RecommendationList");
        Equal(1, queueList.Items.Count); Equal(3, candidates.Items.Count);
        var toggle = Visuals<CheckBox>(view).Single(box => Equals(box.Content, "续播"));
        Check(toggle.IsEnabled && toggle.IsChecked == true);
        SaveRecommendationFrame(host, "queue-candidates-default-width.png");
        var before = await f.Queue();
        toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Until(() => !f.Module.RecommendationContinue);
        Equal(before.GetRawText(), (await f.Queue()).GetRawText());
        var candidate = f.Module.RecommendationCandidates[1];
        var choice = Visuals<Button>(candidates).Single(button => ReferenceEquals(button.Tag, candidate));
        choice.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Until(() => f.Module.CurrentTitle == candidate.Title && !f.Module.RecommendationLoading);
        Equal(4L, N(await f.Queue(), "total"));
        surface.RootVisual = null;
        f.Module.DeactivateGlobalSurface(MeowFilm.Desktop.DesktopGlobalSurface.PlayQueue);
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
        await using var f = await Fixture.Create(initialize: false);
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
    private static async Task UnsupportedRoutes()
    {
        await using var f = await Fixture.Create();
        await f.Module.NavigateAsync("catalog-daily", default);
        f.App.FailNext("music.catalog.daily", "DEVICE_CONTEXT_REQUIRED");
        await f.Module.RefreshCatalogAsync();
        Equal("平台客户端状态不可用", f.Module.Status);
        var location = f.Module.CaptureNavigationLocation();
        var songs = f.Module.Songs.ToArray();
        Check(songs.Length > 0);
        int calls = f.App.Log.Count, requests = f.Platform.Requests.Count;
        int statusChanges = 0, navigationRequests = 0;
        f.Module.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(f.Module.Status)) statusChanges++; };
        f.Module.NavigationRequested += (_, _) => navigationRequests++;
        foreach (var platform in new[] { "tx", "kg", "kw", "mg" })
        {
            f.Select(platform);
            foreach (var route in new[] { "catalog-daily", "catalog-fm", $"catalog-daily/{platform}/default",
                $"catalog-album/{platform}/123", $"catalog-artist/{platform}/123" })
                await f.Module.NavigateAsync(route, default);
            await f.Module.OpenCatalogFeatureAsync("daily");
            await f.Module.OpenCatalogFeatureAsync("fm");
            await f.Module.CatalogOptionsChangedAsync();
            await f.Module.RefreshCatalogAsync();
            Equal(platform, f.Module.SelectedCatalogPlatform.Key);
        }
        Equal(calls, f.App.Log.Count); Equal(requests, f.Platform.Requests.Count);
        Equal(0, statusChanges); Equal(0, navigationRequests);
        Equal(location, f.Module.CaptureNavigationLocation());
        Check(songs.SequenceEqual(f.Module.Songs), "ignored content keeps the current displayed batch");
        Equal("平台客户端状态不可用", f.Module.Status);
        Equal(0, f.Store.LibraryWrites); Equal(0, f.Output.Opens);
        f.Select("wy");
        await f.Module.RefreshCatalogAsync();
        Equal("", f.Module.Status);
        Check(f.App.Log.Count > calls, "supported content still refreshes explicitly");
    }
    private static async Task Errors()
    {
        await using var f = await Fixture.Create(initialize: false); f.App.Authenticated = false;
        await f.Module.InitializeAsync(default);
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
        await f.Module.HomeInitialization;
        Equal("第 2 页", f.Module.CatalogPageText);
        var hold = f.App.HoldNext("music.catalog.home");
        var refresh = f.Module.RefreshCatalogAsync();
        await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Module.NavigateAsync("catalog-home", default);
        Equal("第 2 页", f.Module.CatalogPageText);
        Check(!f.Module.CanCatalogPrevious && !f.Module.CanCatalogNext, "home is one accumulated surface, not manual pages");
        Check(!f.Module.CanCancelCatalog, "retired noncooperative request must be detached from current UI state");
        Check(!refresh.IsCompleted, "fixture request must remain physically blocked");
        hold.Release();
        await refresh;
        Equal("第 2 页", f.Module.CatalogPageText);
    }
    private static async Task LegacyPaging()
    {
        await using var f = await Fixture.Create();
        await f.Module.NavigateAsync("catalog-home", default); await f.Module.HomeInitialization;
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
    private sealed class Platform : IMusicPlatformCatalog, IMusicChartCatalog
    {
        public ConcurrentQueue<object> Requests { get; } = new();
        public int Total = 65;
        public int DailyCount = 3;
        public int SearchCount = 1;
        public bool EmptyDirectoryFirst, Expired;
        public string LocatorVersion = "7";
        public object[]? HomeActions;
        public object[]? HomeEntries, ChannelEntries;
        public object[]? ChannelDescriptors;
        public bool ChannelTags;
        public Func<MusicPlaylistRequest, JsonElement>? PlaylistChange;
        private int fmBatch, queueBatch;
        private FmHold? nextFm;
        private readonly List<FmHold> fmHolds = [];
        public FmHold HoldNextFm() => nextFm = new();
        public void ReleaseAll() { foreach (var hold in fmHolds) hold.Release(); nextFm?.Release(); }
        public sealed class FmHold
        {
            public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<string> released = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public void Fail(string code) => released.TrySetResult(code);
            public void Release() => released.TrySetResult("");
            public async Task Wait()
            {
                Arrived.TrySetResult();
                string code = await released.Task;
                if (code.Length > 0) throw new MusicPlatformException(code, "wy", "fm");
            }
        }
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
                ? new { id, name = "完整歌单", kind = r.Platform == "wy" ? "favorites" : "playlist",
                    relationship = "owned", ownerId = "42", available = true, locator, action = new { type = "playlist", id, locator } }
                : new { id = "701", name = "第二页", kind = "playlist", available = true, action = new { type = "playlist", id = "701" } };
            return Result(r, E(new { platform = r.Platform, accountId = "42", collection = r.Kind.ToString().ToLowerInvariant(),
                page = r.Page, limit = r.Limit, total = (int?)null, more = r.Page == 1,
                continuation = r.Page == 1 ? Cursor(2) : null, snapshot = (string?)null, version = (string?)null,
                items = EmptyDirectoryFirst && r.Page == 1 ? Array.Empty<object>() : new[] { card } }), t);
        }
        private JsonElement Batch(string kind, string title, int start = 0)
        {
            var items = Enumerable.Range(start,
                kind == "daily" ? DailyCount : 3).Select(i => Song("wy", i)).ToArray();
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
        public async Task<JsonElement> FmAsync(MusicFmRequest r, CancellationToken t = default)
        {
            var result = Result(r, Batch("fm", "私人 FM", r.Preview ? 0 : 1000 + Interlocked.Increment(ref fmBatch) * 3), t);
            var hold = nextFm; nextFm = null;
            if (hold is not null) { fmHolds.Add(hold); await hold.Wait(); }
            return await result;
        }
        public Task<JsonElement> SimilarAsync(MusicSimilarRequest r, CancellationToken t = default) => Result(r, Batch("similar", "相似歌曲"), t);
        public Task<JsonElement> HeartbeatAsync(MusicHeartbeatRequest r, CancellationToken t = default) =>
            Result(r, Edit(Batch("heartbeat", "心动推荐", 100), row => { row["context"] = JsonSerializer.SerializeToNode(new {
                playlistId = "555", startMusicId = r.SongId ?? "101", songId = r.SongId ?? "101",
                fromHome = r.Entry == MusicHeartbeatEntry.Favorites, count = 65 });
                row["entrySong"] = JsonNode.Parse(Song("wy", int.Parse(r.SongId ?? "101") - 1).GetRawText()); }), t);
        public Task<JsonElement> QueueRecommendationsAsync(MusicQueueRecommendationsRequest r, CancellationToken t = default) =>
            Result(r, Edit(Batch("queue-recommendations", "队列推荐", 200 + Interlocked.Increment(ref queueBatch) * 3),
                row => { row["offset"] = r.Offset + 15; row["hasMore"] = true; }), t);
        public Task<JsonElement> ChartAsync(MusicChartRequest r, CancellationToken t = default) =>
            Result(r, E(new { platform = r.Platform, name = "畅销榜", more = false, count = 1, total = 1,
                sections = new[] { new { id = "chart", title = "畅销榜", type = "albums", layout = "ranking",
                    items = new[] { new { id = "70", name = "合成专辑", rankText = "1", salesText = "10 张", action = new { type = "album", id = "70" } } } } } }), t);
        public Task<JsonElement> AlbumAsync(MusicAlbumRequest r, CancellationToken t = default) =>
            Result(r, Edit(Batch("album", "合成专辑"), n => { n["id"] = r.Id; n["total"] = 3; }), t);
        public Task<JsonElement> HomeAsync(MusicHomeRequest r, CancellationToken t = default) =>
            Result(r, HomeEntries is { } entries ? E(new { platform = r.Platform, entries, more = false }) :
                E(new { platform = r.Platform, items = HomeActions ?? [new { id = r.Continuation is null ? "500" : "501", name = "歌单", kind = "playlist" }],
                more = r.Continuation is null && HomeActions is null, continuation = r.Continuation is null && HomeActions is null ? Cursor(2) : null }), t);
        public Task<JsonElement> ChannelsAsync(MusicChannelsRequest r, CancellationToken t = default) =>
            Result(r, E(new { platform = r.Platform, items = ChannelDescriptors ??
                [new { id = "rock", kind = "channel", placement = "primary", available = true, name = "摇滚" }] }), t);
        public Task<JsonElement> ChannelAsync(MusicChannelRequest r, CancellationToken t = default)
        {
            if (ChannelEntries is { } entries)
                return Result(r, E(new { platform = r.Platform, channel = new { id = r.Id }, entries, more = false }), t);
            var selectedTag = ChannelTags ? r.Tag is { Length: > 0 } tag ? tag : "genre/one" : "";
            object? next = r.Continuation is not null ? null : ChannelTags
                ? new { page = 2, provider = new { opaque = "retained", cursor = 42 }, scope = new string('b', 64),
                    target = r.Platform + ":" + r.Id, tag = selectedTag } : Cursor(2);
            return Result(r, E(new { platform = r.Platform, channel = new { id = r.Id },
                items = new[] { Song(r.Platform, r.Continuation is null ? 0 : 1) },
                tags = ChannelTags ? new[] { "genre/one", "genre:two" }.Select(id =>
                    new { id, name = id, group = "genre", selected = id == selectedTag }).ToArray() : [],
                selectedTag, more = r.Continuation is null, continuation = next }), t);
        }
        public Task<JsonElement> SearchAsync(MusicSearchRequest r, CancellationToken t = default) =>
            Result(r, E(new { platform = r.Platform,
                items = r.Kind == MusicSearchKind.Songs ? Enumerable.Range(0, SearchCount).Select(i => Song(r.Platform, i)).ToArray() : Array.Empty<JsonElement>(),
                page = r.Page, limit = r.Limit, total = r.Kind == MusicSearchKind.Songs ? SearchCount : 0, more = false }), t);
        public Task<JsonElement> SongAsync(MusicSongRequest r, CancellationToken t = default) =>
            Result(r, E(new { platform = r.Platform, song = Song(r.Platform, 0) }), t);
        public JsonElement Describe() => E(new { execution = "native",
            platforms = new[] { "wy", "tx", "kw", "kg", "mg" }.ToDictionary(platform => platform,
                _ => new { search = new[] { "songs", "playlists" } }) });
        public void Invalidate(string? platform = null) { }
        public void Dispose() { }
    }
    private sealed record Invocation(string Method, JsonElement Parameters, bool Authenticated, long AccountGeneration);
    private sealed class Held
    {
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Wait(CancellationToken token = default)
        {
            using var registration = token.Register(() => Cancelled.TrySetResult());
            Arrived.TrySetResult(); await released.Task;
        }
        public void Release() => released.TrySetResult();
    }
    private sealed class Application : MusicApplicationTestDouble
    {
        public readonly MusicLibraryOwner Library;
        public readonly MusicNativeCatalogOwner Catalog;
        private readonly MusicRecommendationQueuePort recommendationQueue;
        private readonly MusicRecommendationCoordinator recommendations;
        private readonly MusicSearchCoordinator search;
        private readonly object recommendationGate = new();
        private Func<MusicRecommendationSelection, CancellationToken, Task<bool>>? recommendationSelection;
        private bool recommendationPrepared;
        public ConcurrentQueue<Invocation> Log { get; } = new();
        public bool Authenticated = true;
        public string AccountUserId = "42";
        public long AccountGeneration = 1;
        private readonly HashSet<string> revokedAccounts = [];
        public int InitializationCalls, AccountDetailReads;
        public override event Action<string>? AccountDetailsInvalidated;
        public void RevokeAccount(string platform)
        {
            Authenticated = false;
            // Native lease revocation retains the stored generation, but hides the profile.
            revokedAccounts.Add(platform);
            Catalog.Invalidate(platform);
            AccountDetailsInvalidated?.Invoke(platform);
        }
        public override Task InitializeAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); InitializationCalls++; return Task.CompletedTask; }
        public override Task<MusicAccountDetails> ReadDetailsAsync(string platform, CancellationToken token = default)
        { AccountDetailReads++; return base.ReadDetailsAsync(platform, token); }
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
            search = new(platform);
        }
        public override event Action<JsonElement>? SearchChanged
        {
            add => search.Changed += value;
            remove => search.Changed -= value;
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
            // Keep lyrics disabled in this port fixture without adding a library write.
            // The real owner's quality policy and signature remain authoritative.
            result["onlineSettings"] = JsonSerializer.SerializeToNode(new {
                schema = 3, revision = 0, lyrics = false });
            return E(result);
        }
        private static JsonElement ScriptSource() => E(new {
            installationId = "fixture-script", name = "Catalog fixture", version = "1",
            contentHash = new string('c', 64), executionHash = new string('d', 64), sourceRevision = 1,
            generation = 1, enabled = true, state = "ready", rollbackAvailable = false, lastFailure = (string?)null,
            sites = new[] { "wy", "tx", "kg" }.ToDictionary(platform => platform, _ => new {
                actions = new[] { "musicUrl" }, qualitys = new[] { "320k" } }) });
        private async Task<JsonElement> Resolve(JsonElement p, CancellationToken token)
        {
            var input = JsonNode.Parse(p.GetRawText())!.AsObject();
            LJ.Fields(input, "installationId platform song sourceKey quality qualityPolicySignature qualityCacheSignature",
                "installationId platform song quality");
            var installation = ScriptSource();
            string platform = S(p, "platform");
            Check(S(p, "installationId") == S(installation, "installationId") &&
                installation.GetProperty("sites").TryGetProperty(platform, out _),
                "resolution uses the enabled fixture installation and its live declaration");
            var songNode = NativeMusicSong.Normalize(input["song"]);
            var source = songNode["sources"]!.AsArray().OfType<JsonObject>().Single(s =>
                LJ.S(s["source"]) == platform &&
                (input["sourceKey"] is null || LJ.S(s["sourceKey"]) == LJ.Text(input["sourceKey"])));
            var quality = await ReadQualitySettings(token);
            if (!quality.GetProperty("ok").GetBoolean()) return quality;
            if (S(p, "quality") != "320k") return E(new { ok = false, code = "CAPABILITY_UNAVAILABLE" });
            string policy = S(quality, "signature");
            var song = p.GetProperty("song");
            string sourceKey = LJ.Text(source["sourceKey"]);
            string cache = LJ.Digest(LJ.Obj(("contract", "music.quality.cache.v2"), ("policy", policy),
                ("installation", S(installation, "installationId")), ("platform", platform),
                ("content", S(installation, "contentHash")), ("sourceKey", sourceKey),
                ("recording", NativeMusicSong.SourceData(source)),
                ("songId", songNode["songId"]),
                ("duration", songNode["duration"]), ("quality", S(p, "quality"))));
            if (p.TryGetProperty("qualityPolicySignature", out var expectedPolicy) && expectedPolicy.GetString() != policy)
                return E(new { ok = false, code = "QUALITY_POLICY_CHANGED" });
            if (p.TryGetProperty("qualityCacheSignature", out var expectedCache) && expectedCache.GetString() != cache)
                return E(new { ok = false, code = "QUALITY_CACHE_CHANGED" });
            return E(new { ok = true, installationId = S(installation, "installationId"), platform,
                songId = S(song, "songId"), generation = N(installation, "generation"),
                contentHash = S(installation, "contentHash"), quality = S(p, "quality"),
                requestedQuality = S(p, "quality"), expiresAt = (long?)null, sourceKey,
                qualityPolicySignature = policy, qualityCacheSignature = cache, qualityVerified = false,
                url = "https://media.invalid/synthetic", headers = new { }, playbackVerified = false });
        }
        public override async Task<JsonElement> ExecuteAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            var p = E(parameters ?? new { }); Log.Enqueue(new(method, p, Authenticated, AccountGeneration));
            holds.Remove(method, out var held); transforms.Remove(method, out var transform);
            if (held.Hold is not null && held.Before) await held.Hold.Wait(cancellationToken);
            if (failures.Remove(method, out var code)) return E(new { ok = false, code });
            JsonElement result;
            if (method == "music.catalog.playlist.commit" && BeforeCommit is { } before)
            { BeforeCommit = null; await before(); }
            if (method == "music.accounts.list")
                result = E(new { ok = true, items = new[] { "wy", "tx", "kg" }.Select(platform => new {
                    platform, state = revokedAccounts.Contains(platform) ? "revoked" : Authenticated ? "verified" : "none",
                    generation = AccountGeneration,
                    profile = Authenticated && !revokedAccounts.Contains(platform)
                        ? new { platform, userId = AccountUserId, displayName = "Fixture " + platform } : null }) });
            else if (method.StartsWith("music.recommendations.", StringComparison.Ordinal))
                result = await recommendations.ExecuteAsync(method, p, cancellationToken);
            else if (method is "music.search.start" or "music.search.get" or "music.search.next" or "music.search.cancel")
            {
                var searchInput = JsonNode.Parse(p.GetRawText())!.AsObject();
                if (method == "music.search.start")
                {
                    searchInput["pages"] = 1; searchInput["concurrency"] = 1; searchInput["cacheMinutes"] = 1;
                }
                result = await search.ExecuteAsync(method, E(searchInput), cancellationToken);
            }
            else if (method.StartsWith("music.catalog.", StringComparison.Ordinal))
            {
                if (method == "music.catalog.playlist.commit" &&
                    p.TryGetProperty("action", out var action) && action.GetString() is "play" or "next")
                    result = await recommendationQueue.ExecuteExternalAsync(
                        action.GetString() == "play" ? MusicRecommendationQueueChange.Replace : MusicRecommendationQueueChange.PlayNext,
                        token => Catalog.ExecuteAsync(method, p, token), cancellationToken);
                else if (method == "music.catalog.playlist.commit")
                    result = await recommendationQueue.InLaneAsync(
                        (_, token) => Catalog.ExecuteAsync(method, p, token), cancellationToken);
                else
                    result = await Catalog.ExecuteAsync(method, p, cancellationToken);
            }
            else if (method == "music.quality.settings.get") result = await ReadQualitySettings(cancellationToken);
            else if (method == "music.official.candidates")
            {
                var input = JsonNode.Parse(p.GetRawText())!.AsObject();
                LJ.Fields(input, "song quality qualityPolicySignature manualSource", "song quality qualityPolicySignature");
                LJ.Require(input["manualSource"] is null || LJ.IsBool(input["manualSource"]));
                bool manualSource = LJ.B(input["manualSource"]);
                _ = NativeMusicSong.Normalize(input["song"]);
                string request = LJ.S(input["quality"]);
                LJ.Require(request == "auto" || MusicQualityPolicy.OfficialTypes.Contains(request), "QUALITY_UNAVAILABLE");
                var quality = await ReadQualitySettings(cancellationToken);
                if (!quality.GetProperty("ok").GetBoolean()) result = quality;
                else
                {
                    LJ.Require(LJ.S(input["qualityPolicySignature"]) == S(quality, "signature"), "QUALITY_POLICY_CHANGED");
                    var policy = JsonNode.Parse(quality.GetProperty("settings").GetRawText())!.AsObject();
                    LJ.Require(manualSource && request != "auto" || (request == "auto" ? MusicQualityPolicy.HasEnabled(policy) :
                        MusicQualityPolicy.Enabled(request, policy)), "QUALITY_DISABLED");
                    result = E(new { ok = true, song = p.GetProperty("song"), candidates = Array.Empty<object>(),
                        qualityPolicySignature = S(quality, "signature"), authorizationRequired = false });
                }
            }
            else if (method.StartsWith("music.official.", StringComparison.Ordinal))
                result = E(new { ok = false, code = "CAPABILITY_UNAVAILABLE" });
            else if (method == "music.songs.complete")
            {
                var input = JsonNode.Parse(p.GetRawText())!.AsObject();
                LJ.Fields(input, "song platforms contentFailed", "song platforms");
                _ = NativeMusicSong.Normalize(input["song"]);
                var platforms = LJ.Array(input["platforms"]).Select(value => LJ.Text(value)).Distinct(StringComparer.Ordinal).ToArray();
                LJ.Require(platforms.Length is > 0 and <= 5 && platforms.All(NativeMusicSong.Platforms.Contains));
                var contentFailed = input.ContainsKey("contentFailed")
                    ? LJ.Array(input["contentFailed"]).Select(value => LJ.Text(value)).ToHashSet(StringComparer.Ordinal) : [];
                LJ.Require(contentFailed.All(NativeMusicSong.Platforms.Contains));
                result = E(new { ok = true, song = p.GetProperty("song"), failures = Array.Empty<object>() });
            }
            else if (method == "music.sources.list")
                result = E(new { ok = true, revision = 1, items = new[] { ScriptSource() } });
            else if (method == "music.sources.detail")
                result = S(p, "installationId") == "fixture-script"
                    ? E(new { ok = true, item = ScriptSource(), permissions = new { storage = Array.Empty<string>() } })
                    : E(new { ok = false, code = "NOT_FOUND" });
            else if (method == "music.online.resolve") result = await Resolve(p, cancellationToken);
            else result = await recommendationQueue.ExecuteAsync(method, p, cancellationToken);
            if (held.Hold is not null && !held.Before) await held.Hold.Wait(cancellationToken);
            var reply = transform is null ? result : transform(result);
            held.Hold?.Delivered.TrySetResult();
            return reply;
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
            await search.DisposeAsync();
            await recommendations.DisposeAsync();
            await Catalog.DisposeAsync();
            await Library.DisposeAsync();
        }
    }
    private sealed class Store : IDocumentStore
    {
        private readonly object gate = new();
        private readonly Dictionary<string, byte[]> data = [];
        public ConcurrentQueue<string> Reads { get; } = new();
        public int LibraryWrites;
        public bool UnknownAfterNextLibraryWrite;
        public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads.Enqueue(ns);
            lock (gate) return Task.FromResult(data.TryGetValue(ns, out var value)
                ? new DocumentSnapshot(value.ToArray(), DocumentFingerprint.FromBytes(value)) : new(null, DocumentFingerprint.Missing));
        }
        public Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> value, DocumentFingerprint expected,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                var previous = data.TryGetValue(ns, out var bytes) ? DocumentFingerprint.FromBytes(bytes) : DocumentFingerprint.Missing;
                if (previous != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
                data[ns] = value.ToArray();
                if (ns == MusicLibraryOwner.DocumentNamespace)
                {
                    LibraryWrites++;
                    if (UnknownAfterNextLibraryWrite) { UnknownAfterNextLibraryWrite = false; throw new DocumentStoreException(DocumentStoreError.Storage); }
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
        public int Opens, Resumes;
        public bool StartOnOpen;
        public string? OwnerId { get; private set; }
        public string? Identity { get; private set; }
        public bool HasMedia { get; private set; }
        public bool IsPlaying { get; private set; }
        public double PositionSeconds { get; private set; }
        public double DurationSeconds => 180;
        public int Volume { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler? Started;
        public event EventHandler? Ended { add { } remove { } }
        public event EventHandler? Failed;
        public Task OpenAsync(string owner, string identity, Uri uri, string title, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); OwnerId = owner; Identity = identity; HasMedia = true;
            IsPlaying = false; PositionSeconds = 0; Opens++;
            PropertyChanged?.Invoke(this, new(nameof(OwnerId)));
            PropertyChanged?.Invoke(this, new(nameof(HasMedia)));
            if (StartOnOpen) Start();
            return Task.CompletedTask;
        }
        public void Start()
        {
            if (!HasMedia) return;
            IsPlaying = true; Started?.Invoke(this, EventArgs.Empty);
            PropertyChanged?.Invoke(this, new(nameof(IsPlaying)));
        }
        public void Fail() => Failed?.Invoke(this, EventArgs.Empty);
        public Task PauseAsync()
        { IsPlaying = false; PropertyChanged?.Invoke(this, new(nameof(IsPlaying))); return Task.CompletedTask; }
        public Task ResumeAsync() { Resumes++; Start(); return Task.CompletedTask; }
        public Task StopAsync()
        {
            HasMedia = IsPlaying = false;
            PropertyChanged?.Invoke(this, new(nameof(HasMedia))); return Task.CompletedTask;
        }
        public Task SeekAsync(double seconds)
        { PositionSeconds = seconds; PropertyChanged?.Invoke(this, new(nameof(PositionSeconds))); return Task.CompletedTask; }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public Store Store { get; }
        public Platform Platform { get; } = new();
        public Output Output { get; } = new();
        public Dialogs Dialogs { get; } = new();
        public Application App { get; }
        public MusicCapability Module { get; }
        private Fixture(Store? store = null) { Store = store ?? new(); App = new(Store, Platform); Module = new(App, Store, Output, Dialogs); }
        public static async Task<Fixture> Create(bool initialize = true, Store? store = null)
        {
            var f = new Fixture(store);
            try { if (initialize) await f.Module.InitializeAsync(default); return f; } catch { await f.DisposeAsync(); throw; }
        }
        public void Select(string platform)
        { Module.SelectedCatalogPlatform = Module.CatalogPlatforms.Single(p => p.Key == platform); }
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
        public async ValueTask DisposeAsync() { App.ReleaseAll(); Platform.ReleaseAll(); await Module.DisposeAsync(); await Store.DisposeAsync(); }
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
