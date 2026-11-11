using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static partial class MusicCatalogPersonalTests
{
    private static readonly TimeSpan HomeGuard = TimeSpan.FromSeconds(5);

    private static IEnumerable<TestCase> HomeCases()
    {
        yield return Case("startup initialization survives single and repeated passive home activation", StartupPassiveHomeActivation);
        yield return Case("Shell first loaded startup survives repeated passive home activation", StartupShellPassiveHomeActivation);
        yield return Case("startup caller cancellation survives passive home activation and retries normally", StartupCallerCancellation);
        yield return Case("explicit home refresh before navigation survives repeated passive activation", HomeRefreshDuringPassiveActivation);
        yield return Case("startup preparation reads only local accounts source and home snapshot", PrepareLocalHome);
        yield return Case("concurrent startup preparation shares account read and isolates cancelled waiter", PrepareSharedAccounts);
        foreach (bool cancel in new[] { false, true })
            yield return Case("private navigation shares pending startup account read" + (cancel ? " with cancellation" : ""),
                () => PrivateReadDuringPreparation(cancel));
        foreach (bool fail in new[] { false, true })
            yield return Case("retired recommendation ignores late account " + (fail ? "failure" : "success"),
                () => RetiredRecommendationAccountRead(fail));
        foreach (bool replaceDuringPlay in new[] { false, true })
            yield return Case("rebuilt home snapshot allows public play" + (replaceDuringPlay ? " across display replacement" : " during held refresh"),
                () => RestoredHomePlay(replaceDuringPlay));
        yield return Case("rebuilt home snapshot opens public playlist during held refresh", RestoredHomeOpen);
        yield return Case("new public playlist read survives delayed native account cleanup", HomeReadAfterAccountNotification);
        yield return Case("new public home playback survives delayed native account cleanup during prepare receipt", HomePlayAfterAccountNotification);
        yield return Case("native account revocation rejects an issued private playlist token without UI cancellation", HomeRevocationRetiresPrivateToken);
        foreach (bool failTail in new[] { false, true })
            yield return Case("home first page replaces old tail and retains successful continuation" + (failTail ? " after failure" : " through completion"),
                () => HomeRefreshPages(failTail));
        yield return Case("home network failure preserves display durable snapshot and account", HomeNetworkFailure);
        foreach (bool cached in new[] { false, true })
            yield return Case("home replacement fades existing content and releases its visual snapshot cached=" + cached,
                () => HomeRefreshTransition(cached));
        foreach (bool settings in new[] { false, true })
            yield return Case("home cards survive " + (settings ? "settings activation and return" : "first explicit account read without blocking play"),
                () => HomeAfterAccountSettings(settings));
        yield return Case("guest private home card uses notice without private request or commit", GuestHomeCard);
        foreach (bool staleFailure in new[] { false, true })
            yield return Case("native account revocation refreshes guest home and rejects old " + (staleFailure ? "failure" : "success"),
                () => HomeAfterRevocation(staleFailure));
        yield return Case("fast guest home success retains account notice through deferred notification", FastGuestHomeAccountNotice);
        yield return Case("KG home source still notifies revoked WY private request after cancellation", OtherHomeSourceAccountNotice);
        foreach (string code in new[] { "AUTH_REQUIRED", "AUTH_EXPIRED" })
            yield return Case("guest home " + code + " preserves snapshot without recovery loop",
                () => GuestHomeAuthenticationFailure(code));
        yield return Case("private home network error does not revoke account or restart home", PrivateHomeNetworkFailure);
    }

    private static async Task PrepareLocalHome()
    {
        await using var f = await Fixture.Create(initialize: false);
        var source = await f.App.Library.ExecuteAsync("music.catalogSource.settings.save",
            new { ifRevision = 0, platform = "kg" });
        Check(source.GetProperty("ok").GetBoolean());
        await new MusicHomeSnapshotStore(f.Store).SaveAsync(new("kg", [HomePage("801", platform: "kg")]));
        var before = await f.Store.ReadAsync(MusicHomeSnapshotStore.Namespace);
        int writes = f.Store.LibraryWrites;
        f.Store.Reads.Clear();

        await f.Module.PrepareStartupAsync();
        await f.Module.PrepareStartupAsync();

        Check(f.App.Log.Select(call => call.Method).Order(StringComparer.Ordinal).SequenceEqual(
            new[] { "music.accounts.list", "music.catalogSource.settings.get" }),
            "preparation admits only local account and source reads, not home, verification or full initialization");
        Equal(0, f.App.InitializationCalls);
        Equal(0, f.App.AccountDetailReads);
        Equal(0, f.Platform.Requests.Count);
        Equal("kg", f.Module.CatalogSourcePlatform);
        Equal("verified", f.Module.Accounts.Single(account => account.Platform == "wy").State);
        Equal(1, f.Store.Reads.Count(ns => ns == MusicHomeSnapshotStore.Namespace));
        Equal(writes, f.Store.LibraryWrites);
        var after = await f.Store.ReadAsync(MusicHomeSnapshotStore.Namespace);
        Check(before.Data!.SequenceEqual(after.Data!), "preparation reads the saved display without rewriting it");
        Equal(0, f.Output.Opens);
    }

    private static async Task PrepareSharedAccounts()
    {
        await using var f = await Fixture.Create(initialize: false);
        using var cancellation = new CancellationTokenSource();
        var held = f.App.HoldNext("music.accounts.list", before: true);
        var first = f.Module.PrepareStartupAsync(cancellation.Token);
        await held.Arrived.Task.WaitAsync(HomeGuard);
        var second = f.Module.PrepareStartupAsync();
        cancellation.Cancel();
        await CancelledHomeWait(first, cancellation.Token);
        Check(!second.IsCompleted && !held.Cancelled.Task.IsCompleted,
            "cancelling one waiter neither cancels the local account owner nor releases another waiter");
        Equal(1, Calls(f, "music.accounts.list"));
        Equal(0, Calls(f, "music.catalog.home"));
        held.Release();
        await second.WaitAsync(HomeGuard);
        await f.Module.InitializeAsync(default);
        Equal(1, Calls(f, "music.accounts.list"));
        Equal(1, Calls(f, "music.catalogSource.settings.get"));
        Equal("verified", f.Module.Accounts.Single(account => account.Platform == "wy").State);
        Equal(0, f.Platform.Requests.Count);
    }

    private static async Task PrivateReadDuringPreparation(bool cancel)
    {
        await using var f = await Fixture.Create(initialize: false);
        using var cancellation = new CancellationTokenSource();
        var held = f.App.HoldNext("music.accounts.list", before: true);
        var preparation = f.Module.PrepareStartupAsync();
        await held.Arrived.Task.WaitAsync(HomeGuard);
        var navigating = f.Module.NavigateAsync("catalog-daily", cancellation.Token);
        await Drain();
        Check(!navigating.IsCompleted, "unknown account waits for its authoritative local read");
        Equal(1, Calls(f, "music.accounts.list"));
        Equal(0, Calls(f, "music.catalog.daily"));
        if (cancel)
        {
            cancellation.Cancel();
            await CancelledHomeWait(navigating, cancellation.Token);
            Check(!preparation.IsCompleted && !held.Cancelled.Task.IsCompleted,
                "a cancelled private caller leaves startup's shared local read alive");
        }
        held.Release();
        await preparation.WaitAsync(HomeGuard);
        if (!cancel)
        {
            await navigating.WaitAsync(HomeGuard);
            Check(f.Module.IsCatalogDaily && f.Module.Songs.Count == 3, "resolved account admits the private resource");
        }
        Equal(1, Calls(f, "music.accounts.list"));
        Equal(cancel ? 0 : 1, Calls(f, "music.catalog.daily"));
        AssertNoHomeCommit(f);
    }

    private static async Task HomeReadAfterAccountNotification()
    {
        await using var f = await Fixture.Create(store: await SavedHome(HomePage("801")));
        f.Platform.Total = 165;
        f.Platform.HomeActions = [new { id = "801", name = "歌单 801", kind = "playlist" }];
        await f.Module.NavigateAsync("catalog-home", default).WaitAsync(HomeGuard);
        await f.Module.HomeRefresh.WaitAsync(HomeGuard);
        var before = await f.Queue();
        var invalidated = new List<string>();
        f.App.AccountDetailsInvalidated += invalidated.Add;
        var held = f.App.HoldNext("music.catalog.playlist");
        var expected = Enumerable.Range(0, 100).Select(i => S(Song("wy", i), "songId")).ToArray();
        Task reading = Task.CompletedTask;
        try
        {
            f.App.RevokeAccount("wy");
            Equal("verified", f.Module.Accounts.Single(account => account.Platform == "wy").State);
            reading = f.Module.NavigateAsync("catalog-playlist/wy/801", default);
            await held.Arrived.Task.WaitAsync(HomeGuard);
            await Drain();
            Equal("wy", string.Join(',', invalidated));
            Equal("revoked", f.Module.Accounts.Single(account => account.Platform == "wy").State);
            Equal("catalog-playlist/wy/801", f.Module.CaptureNavigationLocation());
            Check(!held.Cancelled.Task.IsCompleted, "old account cleanup cannot cancel the notification-after public read");
            Check(!reading.IsCompleted && f.Module.Songs.Count == 0, "the native read remains held without publishing its receipt");
            held.Release();
            await reading.WaitAsync(HomeGuard);
            Check(f.Module.Songs.Select(song => song.Id).SequenceEqual(expected), "the fresh public playlist publishes its own first page");
            Equal(1, Calls(f, "music.catalog.playlist"));
            Equal("wy", S(f.App.Last("music.catalog.playlist"), "platform"));
            Equal("801", S(f.App.Last("music.catalog.playlist"), "id"));
            var after = await f.Queue();
            Equal(N(before, "version"), N(after, "version"));
            Equal(before.GetProperty("entries").GetRawText(), after.GetProperty("entries").GetRawText());
            AssertNoHomeCommit(f);
        }
        finally
        {
            held.Release();
            await reading.WaitAsync(HomeGuard);
        }
    }

    private static async Task HomePlayAfterAccountNotification()
    {
        await using var f = await Fixture.Create(store: await SavedHome(HomePage("801")));
        f.Platform.Total = 165;
        f.Platform.HomeActions = [new { id = "801", name = "歌单 801", kind = "playlist" }];
        await f.Module.NavigateAsync("catalog-home", default).WaitAsync(HomeGuard);
        await f.Module.HomeRefresh.WaitAsync(HomeGuard);
        var card = f.Module.HomeSections.SelectMany(section => section.Items).Single();
        Check(card.Kind == "playlist" && !card.RequiresAccount && card.Locator is null, "the entry is an ordinary public playlist");
        var before = await f.Queue();
        var invalidated = new List<string>();
        f.App.AccountDetailsInvalidated += invalidated.Add;
        long generation = f.App.AccountGeneration;
        var held = f.App.HoldNext("music.catalog.playlist.prepare");
        Task playing = Task.CompletedTask;
        try
        {
            f.App.RevokeAccount("wy");
            Equal("verified", f.Module.Accounts.Single(account => account.Platform == "wy").State);
            playing = f.Module.PlayCatalogCardAsync(card);
            Check(card.IsPreparing, "the notification-after action is captured in the same Dispatcher continuation");
            await held.Arrived.Task.WaitAsync(HomeGuard);
            await Drain();
            Equal("wy", string.Join(',', invalidated));
            Equal(generation, f.App.AccountGeneration);
            Equal("revoked", f.Module.Accounts.Single(account => account.Platform == "wy").State);
            Check(card.IsPreparing && !playing.IsCompleted && !held.Cancelled.Task.IsCompleted,
                "old account cleanup cannot cancel the new public intent or its held native receipt");
            Equal(1, Calls(f, "music.catalog.playlist.prepare"));
            Equal(0, Calls(f, "music.catalog.playlist.commit"));
            Equal(N(before, "version"), N(await f.Queue(), "version"));
            Equal(0, f.Output.Opens);
            held.Release();
            await playing.WaitAsync(HomeGuard);
            Equal(1, Calls(f, "music.catalog.playlist.prepare"));
            var prepare = f.App.Last("music.catalog.playlist.prepare");
            Equal("wy", S(prepare, "platform"));
            Equal("801", S(prepare, "id"));
            Check(!prepare.TryGetProperty("personal", out _), "public playback cannot reuse an old private locator");
            Check(!f.App.Log.Single(call => call.Method == "music.catalog.playlist.prepare").Authenticated,
                "the fresh native preparation uses notification-after guest authority");
            Equal(1, Calls(f, "music.catalog.playlist.commit"));
            var commit = f.App.Last("music.catalog.playlist.commit");
            Equal("play", S(commit, "action"));
            Equal(N(before, "version"), N(commit, "expectedVersion"));
            Equal(0L, N(commit, "index"));
            var queue = await f.Queue();
            var tail = await f.App.Library.ExecuteAsync("music.queue.get", new { offset = 100, limit = 100 });
            Equal(N(before, "version") + 1, N(queue, "version"));
            Equal(N(queue, "version"), N(tail, "version"));
            Equal((long)f.Platform.Total, N(queue, "total"));
            var entries = queue.GetProperty("entries").EnumerateArray().Concat(tail.GetProperty("entries").EnumerateArray());
            Check(entries.Select(entry => S(entry.GetProperty("song"), "songId"))
                .SequenceEqual(Enumerable.Range(0, f.Platform.Total).Select(i => S(Song("wy", i), "songId"))),
                "the committed queue contains every ordered playlist member, not only the first visible page");
            Equal(S(Song("wy", 0), "songId"), S(queue.GetProperty("current").GetProperty("song"), "songId"));
            Equal(1, f.Output.Opens);
            Check(f.Output.HasMedia && !card.IsPreparing, "the completed native commit opens media and releases its card intent");
            Check(f.Module.IsCatalogHome, "direct public playback keeps the home route");
        }
        finally
        {
            held.Release();
            await playing.WaitAsync(HomeGuard);
        }
    }

    private static async Task HomeRevocationRetiresPrivateToken()
    {
        await using var f = await Fixture.Create();
        f.Select("kg");
        await f.Module.NavigateAsync("catalog-personal", default).WaitAsync(HomeGuard);
        var card = f.Module.CatalogSections.Single().Items.Single();
        var personal = card.Locator ?? throw new InvalidOperationException("private playlist fixture must supply its real locator");
        Equal("kugou-owned", S(personal, "kind"));
        var before = await f.Queue();
        var prepared = await f.App.ExecuteAsync("music.catalog.playlist.prepare",
            new { platform = card.Platform, id = card.ActionId, personal, refresh = true }).WaitAsync(HomeGuard);
        Check(prepared.GetProperty("ok").GetBoolean(), "the native owner issued a private stage before revocation");
        Equal((long)f.Platform.Total, N(prepared, "count"));
        var invalidated = new List<string>();
        f.App.AccountDetailsInvalidated += invalidated.Add;
        f.App.RevokeAccount("kg");
        Equal("verified", f.Module.Accounts.Single(account => account.Platform == "kg").State);
        var rejected = await f.App.ExecuteAsync("music.catalog.playlist.commit",
            new { token = S(prepared, "token"), action = "play", index = 0, expectedVersion = N(before, "version") }).WaitAsync(HomeGuard);
        Check(!rejected.GetProperty("ok").GetBoolean(), "a fresh uncancelled caller cannot commit the retired private token");
        Equal("CATALOG_TOKEN_EXPIRED", S(rejected, "code"));
        await Drain();
        Equal("kg", string.Join(',', invalidated));
        Equal("revoked", f.Module.Accounts.Single(account => account.Platform == "kg").State);
        var after = await f.Queue();
        Equal(N(before, "version"), N(after, "version"));
        Equal(before.GetProperty("entries").GetRawText(), after.GetProperty("entries").GetRawText());
        Equal(1, Calls(f, "music.catalog.playlist.prepare"));
        Equal(1, Calls(f, "music.catalog.playlist.commit"));
        Equal(0, Calls(f, "music.queue.replace"));
        Equal(0, f.Store.LibraryWrites);
        Equal(0, f.Output.Opens);
    }

    private static async Task RestoredHomePlay(bool replaceDuringPlay)
    {
        var store = await SavedHome(HomePage("801"), HomePage("802"));
        await using var f = await Fixture.Create(store: store);
        f.App.TransformNext("music.catalog.home", _ => HomePage("901"));
        var home = f.App.HoldNext("music.catalog.home");
        await f.Module.NavigateAsync("catalog-home", default).WaitAsync(HomeGuard);
        await home.Arrived.Task.WaitAsync(HomeGuard);
        var refresh = f.Module.HomeRefresh;
        Equal("歌单 801,歌单 802", HomeTitles(f));
        Check(!f.Module.ShowCatalogSkeleton, "saved display is visible while its replacement is pending");
        var card = f.Module.HomeSections.SelectMany(section => section.Items).First();
        var preparing = replaceDuringPlay ? f.App.HoldNext("music.catalog.playlist.prepare") : null;
        var play = f.Module.PlayCatalogCardAsync(card);
        if (preparing is not null)
        {
            await preparing.Arrived.Task.WaitAsync(HomeGuard);
            Check(card.IsPreparing, "the captured card owns the play intent");
            home.Release();
            await refresh.WaitAsync(HomeGuard);
            Equal("歌单 901", HomeTitles(f));
            Check(card.IsPreparing && !preparing.Cancelled.Task.IsCompleted,
                "ordinary display replacement cannot cancel the captured playback intent");
            preparing.Release();
        }
        await play.WaitAsync(HomeGuard);
        Equal("801", S(f.App.Last("music.catalog.playlist.prepare"), "id"));
        Equal("wy", S(f.App.Last("music.catalog.playlist.prepare"), "platform"));
        Equal(1, Calls(f, "music.catalog.playlist.commit"));
        Equal((long)f.Platform.Total, N(await f.Queue(), "total"));
        Equal(1, f.Output.Opens);
        Check(f.Module.IsCatalogHome, "direct play keeps the home route");
        if (!replaceDuringPlay)
        {
            Check(!refresh.IsCompleted, "public play completes independently of the held home request");
            Equal("歌单 801,歌单 802", HomeTitles(f));
            home.Release();
            await refresh.WaitAsync(HomeGuard);
        }
        Equal("歌单 901", HomeTitles(f));
        Equal(1, Calls(f, "music.catalog.home"));
    }

    private static async Task RetiredRecommendationAccountRead(bool fail)
    {
        await using var f = await Fixture.Create();
        await f.Module.NavigateAsync("catalog-fm", default);
        Check(f.Module.CanCatalogAction, "the published private resource permits explicit playback");
        var held = f.App.HoldNext("music.accounts.list", before: true);
        if (fail) f.App.FailNext("music.accounts.list", "TIMEOUT");
        var reading = f.Module.ReadAccountsAsync();
        Task playing = Task.CompletedTask;
        try
        {
            await held.Arrived.Task.WaitAsync(HomeGuard);
            playing = f.Module.PlayCatalogPlaylistAsync();
            await Drain();
            Check(!playing.IsCompleted, "recommendation waits for the same pending local account read");
            Equal(2, Calls(f, "music.accounts.list"));
            await f.Module.NavigateAsync("favorites", default);
            string location = f.Module.CaptureNavigationLocation();
            string status = f.Module.Status;
            held.Release();
            await Task.WhenAll(reading, playing).WaitAsync(HomeGuard);
            Equal(location, f.Module.CaptureNavigationLocation());
            Equal(status, f.Module.Status);
            Equal(0, Calls(f, "music.recommendations.start"));
            Check(!f.Module.HasActiveRecommendation, "a retired intent cannot start a session");
            AssertNoHomeCommit(f);
        }
        finally
        {
            held.Release();
            await Task.WhenAll(reading, playing).WaitAsync(HomeGuard);
        }
    }

    private static async Task RestoredHomeOpen()
    {
        await using var f = await Fixture.Create(store: await SavedHome(HomePage("801")));
        f.App.TransformNext("music.catalog.home", _ => HomePage("901"));
        var home = f.App.HoldNext("music.catalog.home");
        await f.Module.NavigateAsync("catalog-home", default).WaitAsync(HomeGuard);
        await home.Arrived.Task.WaitAsync(HomeGuard);
        var refresh = f.Module.HomeRefresh;
        var card = f.Module.HomeSections.Single().Items.Single();
        await f.Module.OpenCatalogCardAsync(card).WaitAsync(HomeGuard);
        Check(f.Module.IsCatalogPlaylist && f.Module.Songs.Count == f.Platform.Total,
            "a displayed public card reads its playlist before the home request settles");
        Equal("801", S(f.App.Last("music.catalog.playlist"), "id"));
        Equal("wy", S(f.App.Last("music.catalog.playlist"), "platform"));
        var location = f.Module.CaptureNavigationLocation();
        home.Release();
        await refresh.WaitAsync(HomeGuard);
        Equal(location, f.Module.CaptureNavigationLocation());
        Check(f.Module.IsCatalogPlaylist, "late home data cannot undo actual navigation");
        AssertNoHomeCommit(f);
    }

    private static async Task HomeRefreshPages(bool failTail)
    {
        await using var f = await Fixture.Create(store: await SavedHome(HomePage("801"), HomePage("802")));
        f.App.TransformNext("music.catalog.home", _ => HomePage("901", nextPage: 2));
        var first = f.App.HoldNext("music.catalog.home");
        await f.Module.NavigateAsync("catalog-home", default).WaitAsync(HomeGuard);
        await first.Arrived.Task.WaitAsync(HomeGuard);
        var refresh = f.Module.HomeRefresh;
        Equal("歌单 801,歌单 802", HomeTitles(f));

        f.App.TransformNext("music.catalog.home", _ => HomePage("902", nextPage: 3));
        var second = f.App.HoldNext("music.catalog.home");
        first.Release();
        await second.Arrived.Task.WaitAsync(HomeGuard);
        Equal("歌单 901", HomeTitles(f));
        Equal("歌单 901", await SavedHomeTitles(f.Store));
        Check(!refresh.IsCompleted, "full refresh includes the continuation");

        f.App.TransformNext("music.catalog.home", _ => failTail ? HomeError("NETWORK_ERROR") : HomePage("903"));
        var third = f.App.HoldNext("music.catalog.home");
        second.Release();
        await third.Arrived.Task.WaitAsync(HomeGuard);
        Equal("歌单 901,歌单 902", HomeTitles(f));
        Equal("歌单 901,歌单 902", await SavedHomeTitles(f.Store));
        third.Release();
        await refresh.WaitAsync(HomeGuard);
        await f.Module.HomeInitialization.WaitAsync(HomeGuard);
        string expected = failTail ? "歌单 901,歌单 902" : "歌单 901,歌单 902,歌单 903";
        Equal(expected, HomeTitles(f));
        Equal(expected, await SavedHomeTitles(f.Store));
        await Drain();
        var reads = f.App.Log.Where(call => call.Method == "music.catalog.home").ToArray();
        Equal(3, reads.Length);
        Check(!reads[0].Parameters.TryGetProperty("continuation", out _),
            "restored display never supplies an old execution cursor");
        Check(reads[0].Parameters.GetProperty("refresh").GetBoolean(), "background refresh starts at page one");
        Check(reads.All(read => read.Parameters.GetProperty("refresh").GetBoolean()),
            "the whole refresh must bypass service page caches, not mix a new head with an old tail");
        Equal(2L, N(reads[1].Parameters.GetProperty("continuation"), "page"));
        Equal(3L, N(reads[2].Parameters.GetProperty("continuation"), "page"));
        if (failTail) Equal("平台网络请求失败", f.Module.Status);
        AssertNoHomeCommit(f);
    }

    private static async Task HomeNetworkFailure()
    {
        await using var f = await Fixture.Create(store: await SavedHome(HomePage("801"), HomePage("802")));
        var before = await f.Store.ReadAsync(MusicHomeSnapshotStore.Namespace);
        f.App.TransformNext("music.catalog.home", _ => HomeError("NETWORK_ERROR"));
        var held = f.App.HoldNext("music.catalog.home");
        await f.Module.NavigateAsync("catalog-home", default).WaitAsync(HomeGuard);
        await held.Arrived.Task.WaitAsync(HomeGuard);
        Equal("歌单 801,歌单 802", HomeTitles(f));
        held.Release();
        await f.Module.HomeRefresh.WaitAsync(HomeGuard);
        Equal("歌单 801,歌单 802", HomeTitles(f));
        Equal("平台网络请求失败", f.Module.Status);
        var after = await f.Store.ReadAsync(MusicHomeSnapshotStore.Namespace);
        Check(before.Data!.SequenceEqual(after.Data!), "network failure cannot save an empty or error display");
        Equal("verified", f.Module.Accounts.Single(account => account.Platform == "wy").State);
        Check(f.App.Authenticated);
        await Drain();
        Equal(1, Calls(f, "music.catalog.home"));
        Equal(1, Calls(f, "music.accounts.list"));
        AssertNoHomeCommit(f);
    }

    private static async Task HomeRefreshTransition(bool cached)
    {
        await using var f = await Fixture.Create(store: cached ? await SavedHome(HomePage("801")) : null);
        f.App.TransformNext("music.catalog.home", _ => HomePage("901"));
        var first = f.App.HoldNext("music.catalog.home");
        var navigation = f.Module.NavigateAsync("catalog-home", default);
        await first.Arrived.Task.WaitAsync(HomeGuard);
        using var mount = new HomeNoticeMount(f.Module.View);
        await Drain();
        Layout(f.Module.View);
        var view = Visuals<MusicCatalogView>(f.Module.View).Single();
        var overlay = (Image)view.FindName("HomeRefreshOverlay");
        var scroll = (ScrollViewer)view.FindName("HomeScroll");
        Check(overlay.Source is null && scroll.Opacity == 1,
            "initial presentation and a pending background read do not fade or clear existing content");
        bool observedPublication = false;
        f.Module.HomeSections.CollectionChanged += (_, _) =>
        {
            observedPublication = true;
            bool animate = cached && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
            Check((overlay.Source is BitmapSource) == animate,
                "successful replacement retains the previous visible frame only when animation is appropriate");
            Check(!overlay.IsHitTestVisible, "the transition does not intercept the newly published cards");
        };
        first.Release();
        await navigation.WaitAsync(HomeGuard);
        await f.Module.HomeRefresh.WaitAsync(HomeGuard);
        Check(observedPublication && HomeTitles(f) == "歌单 901");
        await Until(() => overlay.Source is null && scroll.Opacity == 1);
        Check(overlay.Visibility == Visibility.Collapsed, "completed transitions release the temporary bitmap");
        f.Module.View.Visibility = Visibility.Collapsed;
        Check(overlay.Source is null && scroll.Opacity == 1);
    }

    private static async Task HomeAfterAccountSettings(bool settings)
    {
        await using var f = await Fixture.Create();
        f.Platform.HomeActions = [new { id = "801", name = "歌单 801", kind = "playlist" }];
        await f.Module.NavigateAsync("catalog-home", default);
        await f.Module.HomeInitialization;
        var card = f.Module.HomeSections.Single().Items.Single();
        if (settings)
        {
            await f.Module.ActivateSettingsAsync();
            await f.Module.DeactivateSettingsAsync();
            await f.Module.ActivateCatalogLandingAsync();
        }
        else
        {
            var held = f.App.HoldNext("music.accounts.list", before: true);
            var reading = f.Module.ReadAccountsAsync();
            await held.Arrived.Task.WaitAsync(HomeGuard);
            await f.Module.PlayCatalogCardAsync(card).WaitAsync(HomeGuard);
            Check(!reading.IsCompleted, "local account observation cannot occupy the public playback command lane");
            held.Release();
            await reading.WaitAsync(HomeGuard);
            Equal(2, Calls(f, "music.accounts.list"));
        }
        Equal("歌单 801", HomeTitles(f));
        Check(f.Module.IsCatalogHome, "settings return and account hydration retain the home page");
        // The captured card must remain usable after the first explicit read or settings visit.
        int commits = Calls(f, "music.catalog.playlist.commit");
        await f.Module.PlayCatalogCardAsync(card).WaitAsync(HomeGuard);
        Equal(commits + 1, Calls(f, "music.catalog.playlist.commit"));
        Equal("801", S(f.App.Last("music.catalog.playlist.prepare"), "id"));
        Equal(commits + 1, f.Output.Opens);
        Equal("", f.Module.Status);
        Equal(1, Calls(f, "music.catalog.home"));
    }

    private static async Task GuestHomeCard()
    {
        await using var f = await Fixture.Create(initialize: false, store: await SavedHome(PrivateHomePage()));
        f.App.Authenticated = false;
        await f.Module.InitializeAsync(default);
        f.App.TransformNext("music.catalog.home", _ => PrivateHomePage());
        var home = f.App.HoldNext("music.catalog.home");
        await f.Module.NavigateAsync("catalog-home", default).WaitAsync(HomeGuard);
        await home.Arrived.Task.WaitAsync(HomeGuard);
        using var notices = new HomeNoticeMount(f.Module.View);
        await Drain();
        var card = f.Module.HomeSections.SelectMany(section => section.Items).Single(item => item.Kind == "daily");
        await f.Module.PlayCatalogCardAsync(card).WaitAsync(HomeGuard);
        await Drain();
        Check(notices.Messages.Contains("当前内容需要平台登录"), "guest denial reaches the shared notice outlet");
        Equal("歌单 801,每日推荐", HomeTitles(f));
        Equal(0, Calls(f, "music.catalog.daily"));
        Equal(0, Calls(f, "music.catalog.playlist.prepare"));
        AssertNoHomeCommit(f);
        Equal(0L, N(await f.Queue(), "total"));
        home.Release();
        await f.Module.HomeRefresh.WaitAsync(HomeGuard);
        Equal(1, Calls(f, "music.catalog.home"));
    }

    private static async Task HomeAfterRevocation(bool staleFailure)
    {
        await using var f = await Fixture.Create(store: await SavedHome(PrivateHomePage()));
        f.App.TransformNext("music.catalog.home", _ => staleFailure ? HomeError("NETWORK_ERROR") : HomePage("899"));
        var old = f.App.HoldNext("music.catalog.home");
        await f.Module.NavigateAsync("catalog-home", default).WaitAsync(HomeGuard);
        await old.Arrived.Task.WaitAsync(HomeGuard);
        var oldRefresh = f.Module.HomeRefresh;
        using var notices = new HomeNoticeMount(f.Module.View);
        await Drain();
        var invalidated = new List<string>();
        f.App.AccountDetailsInvalidated += invalidated.Add;
        long generation = f.App.AccountGeneration;
        var card = f.Module.HomeSections.SelectMany(section => section.Items).Single(item => item.Kind == "daily");
        f.App.TransformNext("music.catalog.daily", _ =>
        {
            f.App.RevokeAccount("wy");
            return HomeError("AUTH_EXPIRED");
        });
        var privateRead = f.App.HoldNext("music.catalog.daily");
        var playing = f.Module.PlayCatalogCardAsync(card);
        await privateRead.Arrived.Task.WaitAsync(HomeGuard);
        f.App.TransformNext("music.catalog.home", _ => HomePage("990"));
        var guest = f.App.HoldNext("music.catalog.home");
        privateRead.Release();
        await playing.WaitAsync(HomeGuard);
        await guest.Arrived.Task.WaitAsync(HomeGuard);
        await old.Cancelled.Task.WaitAsync(HomeGuard);
        await Drain();
        Equal("wy", string.Join(',', invalidated));
        Check(!f.App.Authenticated);
        Equal(generation, f.App.AccountGeneration);
        var account = f.Module.Accounts.Single(row => row.Platform == "wy");
        Equal("revoked", account.State);
        Equal(generation, account.Generation);
        Equal("", account.UserId);
        Check(notices.Messages.Contains("当前内容需要平台登录"),
            "authoritative revocation notifies even if its original auth receipt was cancelled");
        Equal("歌单 801,每日推荐", HomeTitles(f));
        var reads = f.App.Log.Where(call => call.Method == "music.catalog.home").ToArray();
        Equal(2, reads.Length);
        Check(reads[0].Authenticated && !reads[1].Authenticated, "new home uses guest authority after native revocation");
        Equal(generation, reads[1].AccountGeneration);
        Check(!reads[1].Parameters.TryGetProperty("continuation", out _), "guest recovery starts from its own first page");
        AssertNoHomeCommit(f);
        var guestRefresh = f.Module.HomeRefresh;
        guest.Release();
        await guestRefresh.WaitAsync(HomeGuard);
        Equal("歌单 990", HomeTitles(f));
        string status = f.Module.Status;
        old.Release();
        await oldRefresh.WaitAsync(HomeGuard);
        await Drain();
        Equal("歌单 990", HomeTitles(f));
        Equal("歌单 990", await SavedHomeTitles(f.Store));
        Equal(status, f.Module.Status);
        Equal(2, Calls(f, "music.catalog.home"));
        Equal(2, Calls(f, "music.accounts.list"));
    }

    private static async Task FastGuestHomeAccountNotice()
    {
        await using var f = await Fixture.Create(store: await SavedHome(PrivateHomePage()));
        f.App.TransformNext("music.catalog.home", _ => PrivateHomePage());
        await f.Module.NavigateAsync("catalog-home", default).WaitAsync(HomeGuard);
        await f.Module.HomeRefresh.WaitAsync(HomeGuard);
        using var notices = new HomeNoticeMount(f.Module.View);
        await Drain();
        var guestReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.App.TransformNext("music.catalog.home", _ =>
        {
            guestReturned.TrySetResult();
            return HomePage("990");
        });
        f.App.TransformNext("music.catalog.daily", _ =>
        {
            f.App.RevokeAccount("wy");
            return HomeError("AUTH_EXPIRED");
        });
        var card = f.Module.HomeSections.SelectMany(section => section.Items).Single(item => item.Kind == "daily");
        await f.Module.PlayCatalogCardAsync(card).WaitAsync(HomeGuard);
        await guestReturned.Task.WaitAsync(HomeGuard);
        await f.Module.HomeRefresh.WaitAsync(HomeGuard);
        Equal("歌单 990", HomeTitles(f));
        Equal("当前内容需要平台登录", f.Module.Status);
        // No guest hold or dispatcher drain separates revocation from fast home success.
        // The real outlet must still deliver the notice at its deferred Loaded priority.
        await Drain();
        Check(notices.Messages.Contains("当前内容需要平台登录"),
            "fast successful guest content cannot consume the deferred account notification");
        Equal("当前内容需要平台登录", f.Module.Status);
        Equal("revoked", f.Module.Accounts.Single(account => account.Platform == "wy").State);
        var reads = f.App.Log.Where(call => call.Method == "music.catalog.home").ToArray();
        Equal(2, reads.Length);
        Check(reads[0].Authenticated && !reads[1].Authenticated, "fast replacement uses guest authority");
        Equal(2, Calls(f, "music.accounts.list"));
        AssertNoHomeCommit(f);

        await f.Module.NavigateAsync("favorites", default).WaitAsync(HomeGuard);
        Equal("", f.Module.Status);
    }

    private static async Task OtherHomeSourceAccountNotice()
    {
        await using var f = await Fixture.Create(initialize: false);
        var source = await f.App.Library.ExecuteAsync("music.catalogSource.settings.save",
            new { ifRevision = 0, platform = "kg" });
        Check(source.GetProperty("ok").GetBoolean());
        await f.Module.InitializeAsync(default);
        f.App.TransformNext("music.catalog.home", _ => HomePage("880", platform: "kg"));
        await f.Module.NavigateAsync("catalog-home", default).WaitAsync(HomeGuard);
        await f.Module.HomeRefresh.WaitAsync(HomeGuard);
        Equal("歌单 880", HomeTitles(f));
        await f.Module.NavigateAsync("catalog-daily/wy/default", default).WaitAsync(HomeGuard);
        Check(f.Module.IsCatalogDaily && f.Module.Songs.Count == 3, "WY private content is independent of the KG home source");
        Equal("kg", f.Module.CatalogSourcePlatform);
        using var notices = new HomeNoticeMount(f.Module.View);
        await Drain();
        long generation = f.App.AccountGeneration;
        int writes = f.Store.LibraryWrites;
        f.App.TransformNext("music.catalog.daily", _ => HomeError("AUTH_EXPIRED"));
        var privateRead = f.App.HoldNext("music.catalog.daily");
        var refreshing = f.Module.RefreshCatalogAsync();
        await privateRead.Arrived.Task.WaitAsync(HomeGuard);
        Equal("wy", S(f.App.Last("music.catalog.daily"), "platform"));
        f.App.RevokeAccount("wy");
        await privateRead.Cancelled.Task.WaitAsync(HomeGuard);
        Equal("歌单 880", HomeTitles(f));
        privateRead.Release();
        await refreshing.WaitAsync(HomeGuard);
        await Until(() => f.Module.Accounts.Single(account => account.Platform == "wy").State == "revoked");
        await Drain();
        Check(notices.Messages.Contains("当前内容需要平台登录"),
            "the changed WY account owns the notice even when its original request is cancelled and home uses KG");
        Equal("当前内容需要平台登录", f.Module.Status);
        Equal(generation, f.Module.Accounts.Single(account => account.Platform == "wy").Generation);
        Check(f.Module.Accounts.Single(account => account.Platform == "kg").State != "revoked",
            "the fixture revokes WY only; KG cannot supply the revocation notice");
        Equal("kg", f.Module.CatalogSourcePlatform);
        Equal("歌单 880", HomeTitles(f));
        var home = f.App.Log.Single(call => call.Method == "music.catalog.home");
        Equal("kg", S(home.Parameters, "platform"));
        Equal(2, Calls(f, "music.catalog.daily"));
        Equal(2, Calls(f, "music.accounts.list"));
        Equal(0, Calls(f, "music.catalog.playlist.commit"));
        Equal(0, Calls(f, "music.queue.replace"));
        Equal(writes, f.Store.LibraryWrites);
        Equal(0, f.Output.Opens);
    }

    private static async Task GuestHomeAuthenticationFailure(string code)
    {
        await using var f = await Fixture.Create(initialize: false, store: await SavedHome(HomePage("801")));
        f.App.Authenticated = false;
        await f.Module.InitializeAsync(default);
        var before = await f.Store.ReadAsync(MusicHomeSnapshotStore.Namespace);
        f.App.TransformNext("music.catalog.home", _ => HomeError(code));
        var held = f.App.HoldNext("music.catalog.home");
        await f.Module.NavigateAsync("catalog-home", default).WaitAsync(HomeGuard);
        await held.Arrived.Task.WaitAsync(HomeGuard);
        using var notices = new HomeNoticeMount(f.Module.View);
        await Drain();
        held.Release();
        await f.Module.HomeRefresh.WaitAsync(HomeGuard);
        await f.Module.HomeInitialization.WaitAsync(HomeGuard);
        await Drain();
        Equal("歌单 801", HomeTitles(f));
        Check(notices.Messages.Contains("当前内容需要平台登录"), "guest auth failure uses the existing notification");
        Equal(1, Calls(f, "music.catalog.home"));
        Equal(1, Calls(f, "music.accounts.list"));
        Equal("none", f.Module.Accounts.Single(account => account.Platform == "wy").State);
        Check(f.App.Log.Where(call => call.Method == "music.catalog.home").All(call => !call.Authenticated));
        var after = await f.Store.ReadAsync(MusicHomeSnapshotStore.Namespace);
        Check(before.Data!.SequenceEqual(after.Data!), "failed guest recovery cannot erase durable display");
        AssertNoHomeCommit(f);
    }

    private static async Task PrivateHomeNetworkFailure()
    {
        await using var f = await Fixture.Create(store: await SavedHome(PrivateHomePage()));
        f.App.TransformNext("music.catalog.home", _ => PrivateHomePage());
        var home = f.App.HoldNext("music.catalog.home");
        await f.Module.NavigateAsync("catalog-home", default).WaitAsync(HomeGuard);
        await home.Arrived.Task.WaitAsync(HomeGuard);
        using var notices = new HomeNoticeMount(f.Module.View);
        await Drain();
        f.App.FailNext("music.catalog.daily", "NETWORK_ERROR");
        var card = f.Module.HomeSections.SelectMany(section => section.Items).Single(item => item.Kind == "daily");
        await f.Module.PlayCatalogCardAsync(card).WaitAsync(HomeGuard);
        await Drain();
        Check(notices.Messages.Contains("平台网络请求失败"));
        Equal("歌单 801,每日推荐", HomeTitles(f));
        Equal("verified", f.Module.Accounts.Single(account => account.Platform == "wy").State);
        Check(f.App.Authenticated);
        Equal(1, Calls(f, "music.accounts.list"));
        Equal(1, Calls(f, "music.catalog.home"));
        AssertNoHomeCommit(f);
        home.Release();
        await f.Module.HomeRefresh.WaitAsync(HomeGuard);
        Equal(1, Calls(f, "music.catalog.home"));
    }

    private static JsonElement HomePage(string id, int? nextPage = null, string platform = "wy") => E(new
    {
        ok = true, platform, items = new[] { new { id, name = "歌单 " + id, kind = "playlist" } },
        more = nextPage.HasValue,
        continuation = nextPage is { } page ? new { page, provider = new { cursor = "home-" + page } } : null
    });
    private static JsonElement PrivateHomePage() => E(new
    {
        ok = true, platform = "wy", more = false, items = new object[]
        {
            new { id = "801", name = "歌单 801", kind = "playlist" },
            new { id = "daily", name = "每日推荐", kind = "daily", requiresAccount = true,
                action = new { type = "daily", branch = "default" } }
        }
    });
    private static JsonElement HomeError(string code) => E(new { ok = false, code });
    private static async Task<Store> SavedHome(params JsonElement[] pages)
    {
        var store = new Store();
        await new MusicHomeSnapshotStore(store).SaveAsync(new("wy", pages));
        return store;
    }
    private static async Task<string> SavedHomeTitles(Store store)
    {
        var snapshot = await new MusicHomeSnapshotStore(store).ReadAsync();
        Check(snapshot is not null && snapshot.Platform == "wy", "successful display is persisted with its platform");
        return string.Join(',', snapshot!.Pages.SelectMany(page => page.GetProperty("items").EnumerateArray())
            .Select(item => S(item, "name")));
    }
    private static string HomeTitles(Fixture f) =>
        string.Join(',', f.Module.HomeSections.SelectMany(section => section.Items).Select(card => card.Title));
    private static int Calls(Fixture f, string method) => f.App.Log.Count(call => call.Method == method);
    private static void AssertNoHomeCommit(Fixture f)
    {
        Equal(0, Calls(f, "music.catalog.playlist.commit"));
        Equal(0, Calls(f, "music.queue.replace"));
        Equal(0, f.Store.LibraryWrites);
        Equal(0, f.Output.Opens);
    }
    private static async Task CancelledHomeWait(Task work, CancellationToken token)
    {
        try
        {
            await work.WaitAsync(HomeGuard);
            throw new InvalidOperationException("Cancelled caller completed successfully");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private sealed class HomeNoticeMount : IDisposable
    {
        private readonly Window window;
        public List<string> Messages { get; } = [];
        public HomeNoticeMount(FrameworkElement view)
        {
            window = new() { Content = view, Width = 900, Height = 720, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            window.AddHandler(NoticeOutlet.NoticeEvent,
                new EventHandler<DesktopNoticeEventArgs>((_, notice) => Messages.Add(notice.Message)));
            window.Show();
        }
        public void Dispose() { window.Content = null; window.Close(); }
    }
}
