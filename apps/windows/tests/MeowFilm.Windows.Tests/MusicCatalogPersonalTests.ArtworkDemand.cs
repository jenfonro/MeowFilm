using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Windows.Tests;

public static partial class MusicCatalogPersonalTests
{
    private static IEnumerable<TestCase> ArtworkDemandCases()
    {
        yield return Case("visible missing artwork coalesces resources and keeps supplied covers", ArtworkDemandCoalescing);
        yield return Case("hidden artwork stops dispatch and retains admitted settlement for redisplay", ArtworkDemandHidden);
        yield return Case("missing and failed artwork are distinct finite outcomes across pagination", ArtworkDemandTerminal);
        yield return Case("preview and detail reuse collection root artwork without borrowing song covers", ArtworkDemandCollectionRoot);
        yield return Case("visible playlist metadata reuses preview names and play counts even with supplied artwork", CollectionMetadataPreview);
        yield return Case("playlist summaries fill channel titles and keep home category labels", CollectionMetadataSummary);
        yield return Case("failed playlist members still allow one visible metadata summary", CollectionMetadataAfterPreviewFailure);
        yield return Case("visible FM artwork shares explicit configurations without playlist previews or playback", FMArtworkCoalescing);
        yield return Case("hidden FM artwork retains admitted settlement without dispatching new reads", FMArtworkHidden);
        yield return Case("missing and failed FM artwork stay finite across pagination", FMArtworkTerminal);
        foreach (string change in new[] { "refresh", "account", "source", "close" })
        {
            yield return Case("artwork " + change + " retires late results and drains actual reads", () => ArtworkDemandRetirement(change));
            yield return Case("FM artwork " + change + " retires late results and drains actual reads", () => ArtworkDemandRetirement(change, fm: true));
        }
    }
    private static object MissingArtworkCard(string id, string key, string presentation = "static", string cover = "") => new
    {
        id, key, kind = "playlist", title = "歌单 " + key, subtitle = "保留说明", cover,
        coverRef = new { kind = "playlist", id }, presentation, action = new { type = "playlist", id }
    };
    private static object FMArtworkCard(string key, object action, string cover = "") => new
    {
        id = key, key, kind = "fm", title = "FM " + key, subtitle = "保留说明",
        cover, presentation = "animated", action
    };
    private static MusicCatalogCard[] VisibleCards(Fixture f) =>
        f.Module.DisplayCatalogSections.SelectMany(section => section.Items).ToArray();
    private static async Task DemandArtwork(Fixture f)
    {
        f.Module.UpdateCatalogVisibleCards(VisibleCards(f));
        await f.Module.CatalogEntryInitialization.WaitAsync(TimeSpan.FromSeconds(5));
    }
    private static async Task ArtworkDemandCoalescing()
    {
        await using var f = await Fixture.Create();
        f.Platform.SampleCover = "https://art.example.test/summary";
        f.Platform.HomeActions = [MissingArtworkCard("500", "a"), MissingArtworkCard("500", "b"),
            MissingArtworkCard("501", "c"), MissingArtworkCard("502", "existing", cover: "https://art.example.test/supplied")];
        await f.Module.NavigateAsync("catalog-home", default);
        Equal(0, Calls(f, "music.catalog.playlists"));
        var cards = VisibleCards(f);
        var identities = cards.Select(card => (card.Key, card.Title, card.Subtitle, card.ActionId)).ToArray();
        await DemandArtwork(f);
        var request = f.Platform.Requests.OfType<MusicPlaylistsRequest>().Single();
        Check(request.Ids.SequenceEqual(["500", "501"]), "one explicit batch contains only the visible missing resources");
        Check(cards.Take(3).All(card => card.Cover == f.Platform.SampleCover));
        Equal("https://art.example.test/supplied", cards[3].Cover);
        Check(identities.SequenceEqual(cards.Select(card => (card.Key, card.Title, card.Subtitle, card.ActionId))));
        await DemandArtwork(f);
        Equal(1, Calls(f, "music.catalog.playlists"));
        Equal(0, Calls(f, "music.catalog.playlist")); Equal(0, f.Store.LibraryWrites); Equal(0, f.Output.Opens);
    }
    private static async Task ArtworkDemandHidden()
    {
        await using var f = await Fixture.Create();
        f.Platform.SampleCover = "https://art.example.test/settled";
        f.Platform.HomeActions = [MissingArtworkCard("500", "a")];
        await f.Module.NavigateAsync("catalog-home", default);
        var card = VisibleCards(f).Single();
        f.Module.UpdateCatalogVisibleCards([card]);
        var queued = f.Module.CatalogEntryInitialization;
        f.Module.UpdateCatalogVisibleCards([]);
        await queued;
        Equal(0, Calls(f, "music.catalog.playlists"));
        var held = f.App.HoldNext("music.catalog.playlists");
        f.Module.UpdateCatalogVisibleCards([card]);
        var read = f.Module.CatalogEntryInitialization;
        await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Module.UpdateCatalogVisibleCards([]);
        held.Release(); await read;
        Equal("", card.Cover);
        Check(!held.Cancelled.Task.IsCompleted, "temporary hiding does not cancel an admitted metadata read");
        await DemandArtwork(f);
        Equal(f.Platform.SampleCover, card.Cover); Equal(1, Calls(f, "music.catalog.playlists"));
        await f.Module.NavigateAsync("favorites", default);
        await f.Module.NavigateAsync("catalog-home", default);
        await DemandArtwork(f);
        Equal(1, Calls(f, "music.catalog.playlists"));
    }
    private static async Task ArtworkDemandTerminal()
    {
        foreach (bool failure in new[] { false, true })
        {
            await using var f = await Fixture.Create();
            f.Platform.ChannelEntries = [MissingArtworkCard("500", "first")];
            f.App.TransformNext("music.catalog.channel", reply => Edit(reply, row =>
            { row["more"] = true; row["continuation"] = new JsonObject { ["page"] = 2 }; }));
            if (failure) f.App.FailNext("music.catalog.playlists", "NETWORK_ERROR");
            await f.Module.NavigateAsync("catalog-channel/wy/rock", default);
            await DemandArtwork(f);
            Equal(failure ? "NETWORK_ERROR" : "empty", f.Module.CatalogCoverReadOutcome("500"));
            for (int i = 0; i < 3; i++)
            {
                f.Module.UpdateCatalogVisibleCards([]);
                await DemandArtwork(f);
            }
            f.Platform.ChannelEntries = [MissingArtworkCard("500", "second")];
            await f.Module.CatalogPageAsync(1);
            await DemandArtwork(f);
            Equal(2, VisibleCards(f).Length); Equal(1, Calls(f, "music.catalog.playlists"));
            Check(VisibleCards(f).All(card => card.Cover.Length == 0));
            await f.Module.RefreshCatalogAsync(); await DemandArtwork(f);
            Equal(2, Calls(f, "music.catalog.playlists"));
        }
    }
    private static async Task ArtworkDemandCollectionRoot()
    {
        foreach (bool rootCover in new[] { false, true })
        {
            await using var f = await Fixture.Create();
            f.Platform.HomeEntries = [MissingArtworkCard("500", "preview", "preview"), MissingArtworkCard("500", "static")];
            f.App.TransformNext("music.catalog.playlist", reply => Edit(reply, row =>
            {
                row["cover"] = rootCover ? "https://art.example.test/root" : "";
                row["playCount"] = 0;
                row["items"]![0]!["cover_url"] = "https://art.example.test/song";
            }));
            await f.Module.NavigateAsync("catalog-home", default);
            var held = f.App.HoldNext("music.catalog.playlist");
            f.Module.UpdateCatalogVisibleCards(VisibleCards(f));
            var read = f.Module.CatalogEntryInitialization;
            await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(0, Calls(f, "music.catalog.playlists"));
            held.Release(); await read;
            Equal(rootCover ? 0 : 1, Calls(f, "music.catalog.playlists"));
            Check(VisibleCards(f).All(card => card.Cover == (rootCover ? "https://art.example.test/root" : "")),
                "the first song's image never fills a collection cover");
            Check(VisibleCards(f)[0].HasPreview);
        }
        await using var detail = await Fixture.Create();
        detail.Platform.HomeActions = [MissingArtworkCard("500", "detail")];
        await detail.Module.NavigateAsync("catalog-home", default);
        var card = VisibleCards(detail).Single();
        detail.Platform.SampleCover = "https://art.example.test/detail-root";
        await detail.Module.OpenCatalogCardAsync(card);
        await detail.Module.NavigateAsync("catalog-home", default);
        await DemandArtwork(detail);
        Equal(detail.Platform.SampleCover, card.Cover);
        Equal(0, Calls(detail, "music.catalog.playlists"));
    }
    private static async Task CollectionMetadataPreview()
    {
        await using var f = await Fixture.Create();
        f.Platform.ChannelEntries = [MissingArtworkCard("500", "first", "preview", "https://art.example.test/supplied"),
            MissingArtworkCard("500", "copy", "preview", "https://art.example.test/supplied"),
            new { id = "500", key = "link", kind = "playlist", title = "活动标题", cover = "", action = new { type = "playlist", id = "500" } }];
        f.App.TransformNext("music.catalog.playlist", reply => Edit(reply, row =>
        {
            row["name"] = "欧美红心宝藏 | 高收藏率英文歌推荐";
            row["cover"] = "https://art.example.test/root"; row["playCount"] = 3031000;
        }));
        await f.Module.NavigateAsync("catalog-channel/wy/rock", default);
        var cards = VisibleCards(f);
        await DemandArtwork(f);
        Check(cards.Take(2).All(card => card.Title == "欧美红心宝藏 | 高收藏率英文歌推荐" &&
            card.Caption == card.Title && card.PlayCount == 3031000 && card.PlayCountText == "303.1万" && card.HasPreview));
        Check(cards.Take(2).All(card => card.Cover == "https://art.example.test/supplied"));
        Check(cards[2] is { Title: "活动标题", Cover: "", HasPreview: false, PlayCount: null },
            "a link to the same playlist never acquires the destination's display materials");
        Equal(1, Calls(f, "music.catalog.playlist")); Equal(0, Calls(f, "music.catalog.playlists"));
        var request = f.Platform.Requests.OfType<MusicPlaylistRequest>().Single();
        Equal(1, request.Page); Equal(3, request.Limit);
        f.Module.UpdateCatalogVisibleCards([]); await DemandArtwork(f);
        Equal(1, Calls(f, "music.catalog.playlist")); Equal(0, f.Output.Opens);
    }
    private static async Task CollectionMetadataSummary()
    {
        await using var f = await Fixture.Create();
        f.Platform.HomeEntries = [new { id = "500", key = "home", kind = "playlist", title = "私人雷达", label = "私人雷达",
            subtitle = "你爱的歌值得反复聆听", cover = "https://art.example.test/supplied", presentation = "preview",
            coverRef = new { kind = "playlist", id = "500" }, action = new { type = "playlist", id = "500" } }];
        f.App.TransformNext("music.catalog.playlist", reply => Edit(reply, row =>
        {
            row["name"] = ""; row["cover"] = "";
        }));
        f.App.TransformNext("music.catalog.playlists", reply => Edit(reply, row =>
        {
            row["items"]![0]!["name"] = "今日动态歌单"; row["items"]![0]!["playCount"] = 0;
        }));
        await f.Module.NavigateAsync("catalog-home", default); await DemandArtwork(f);
        var home = VisibleCards(f).Single();
        Equal("私人雷达", home.Label); Equal("私人雷达", home.Title); Equal("今日动态歌单", home.Caption);
        Check(home.HasPlayCount && home.PlayCountText == "0");
        Equal(1, Calls(f, "music.catalog.playlists"));
        f.Platform.ChannelEntries = [new { id = "501", key = "channel", kind = "playlist", title = "", cover = "https://art.example.test/supplied",
            coverRef = new { kind = "playlist", id = "501" }, action = new { type = "playlist", id = "501" } }];
        await f.Module.NavigateAsync("catalog-channel/wy/rock", default); await DemandArtwork(f);
        Equal("歌单", VisibleCards(f).Single().Title);
    }
    private static async Task CollectionMetadataAfterPreviewFailure()
    {
        await using var f = await Fixture.Create();
        f.Platform.ChannelEntries = [MissingArtworkCard("500", "entry", "preview")];
        f.Platform.SampleCover = "https://art.example.test/summary";
        f.App.FailNext("music.catalog.playlist", "NETWORK_ERROR");
        await f.Module.NavigateAsync("catalog-channel/wy/rock", default); await DemandArtwork(f);
        var card = VisibleCards(f).Single();
        Check(card.Title == "歌单" && card.Cover == f.Platform.SampleCover && !card.HasPreview && !card.HasPlayCount);
        f.Module.UpdateCatalogVisibleCards([]); await DemandArtwork(f);
        Equal(1, Calls(f, "music.catalog.playlist")); Equal(1, Calls(f, "music.catalog.playlists"));
    }
    private static async Task FMArtworkCoalescing()
    {
        foreach (bool rule in new[] { false, true })
        {
            await using var f = await Fixture.Create();
            object action = rule ? new { type = "fm", ruleId = "71", tabTagId = "72" } :
                new { type = "fm", mode = "scene", submode = "HOT_SONG" };
            f.Platform.HomeEntries = [FMArtworkCard("a", action), FMArtworkCard("b", action),
                FMArtworkCard("supplied", new { type = "fm", ruleId = "73" }, "https://art.example.test/supplied")];
            f.Platform.SampleCover = "https://art.example.test/fm";
            if (!rule) f.App.TransformNext("music.catalog.fm", reply => Edit(reply, row =>
            {
                row["cover"] = "https://art.example.test/fm";
                row["items"]![0]!["cover_url"] = "https://art.example.test/song";
            }));
            await f.Module.NavigateAsync("catalog-home", default);
            Equal(0, Calls(f, "music.catalog.fm"));
            var cards = VisibleCards(f);
            var identities = cards.Select(card => (card.Key, card.Title, card.Subtitle, card.Action)).ToArray();
            await DemandArtwork(f);
            var request = f.Platform.Requests.OfType<MusicFmRequest>().Single();
            Check(request.Preview);
            Equal(rule ? MusicFmMode.Default : MusicFmMode.Scene, request.Mode);
            Equal(rule ? "" : "HOT_SONG", request.Submode);
            Equal(rule ? "71" : null, request.RuleId); Equal(rule ? "72" : null, request.TabTagId);
            Check(cards.Take(2).All(card => card.Cover == "https://art.example.test/fm" && !card.HasPreview && !card.CanRequestPreview));
            Equal("https://art.example.test/supplied", cards[2].Cover);
            Check(identities.SequenceEqual(cards.Select(card => (card.Key, card.Title, card.Subtitle, card.Action))));
            Equal("cover", f.Module.CatalogFMArtworkOutcome(cards[0]));
            await DemandArtwork(f);
            Equal(1, Calls(f, "music.catalog.fm")); Equal(0, Calls(f, "music.catalog.playlist"));
            // A mode preview adopts its real song batch through the repository; a rule cover has no songs.
            Equal(0, Calls(f, "music.catalog.playlists")); Equal(rule ? 0 : 1, f.Store.LibraryWrites); Equal(0, f.Output.Opens);
        }
    }
    private static async Task FMArtworkHidden()
    {
        await using var f = await Fixture.Create();
        f.Platform.HomeEntries = [FMArtworkCard("a", new { type = "fm", ruleId = "71" })];
        f.Platform.SampleCover = "https://art.example.test/fm";
        await f.Module.NavigateAsync("catalog-home", default);
        var card = VisibleCards(f).Single();
        f.Module.UpdateCatalogVisibleCards([card]);
        var queued = f.Module.CatalogEntryInitialization;
        f.Module.UpdateCatalogVisibleCards([]);
        await queued;
        Equal(0, Calls(f, "music.catalog.fm"));
        var held = f.App.HoldNext("music.catalog.fm");
        f.Module.UpdateCatalogVisibleCards([card]);
        var read = f.Module.CatalogEntryInitialization;
        await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Module.NavigateAsync("favorites", default);
        held.Release(); await read;
        Equal("", card.Cover);
        Check(!held.Cancelled.Task.IsCompleted, "temporary hiding does not cancel an admitted FM artwork read");
        await f.Module.NavigateAsync("catalog-home", default);
        await DemandArtwork(f);
        Equal("https://art.example.test/fm", card.Cover); Equal(1, Calls(f, "music.catalog.fm"));
        Equal(0, f.Store.LibraryWrites); Equal(0, f.Output.Opens);
    }
    private static async Task FMArtworkTerminal()
    {
        foreach (bool failure in new[] { false, true })
        {
            await using var f = await Fixture.Create();
            f.Platform.ChannelEntries = [FMArtworkCard("first", new { type = "fm", ruleId = "71" })];
            f.App.TransformNext("music.catalog.channel", reply => Edit(reply, row =>
            { row["more"] = true; row["continuation"] = new JsonObject { ["page"] = 2 }; }));
            if (failure) f.App.FailNext("music.catalog.fm", "NETWORK_ERROR");
            await f.Module.NavigateAsync("catalog-channel/wy/rock", default);
            await DemandArtwork(f);
            Equal(failure ? "NETWORK_ERROR" : "empty", f.Module.CatalogFMArtworkOutcome(VisibleCards(f).Single()));
            for (int i = 0; i < 3; i++)
            {
                f.Module.UpdateCatalogVisibleCards([]);
                await DemandArtwork(f);
            }
            f.Platform.ChannelEntries = [FMArtworkCard("second", new { type = "fm", ruleId = "71" })];
            await f.Module.CatalogPageAsync(1); await DemandArtwork(f);
            Equal(2, VisibleCards(f).Length); Equal(1, Calls(f, "music.catalog.fm"));
            Check(VisibleCards(f).All(card => card.Cover.Length == 0 && !card.HasPreview));
            await f.Module.RefreshCatalogAsync(); await DemandArtwork(f);
            Equal(2, Calls(f, "music.catalog.fm")); Equal(0, f.Output.Opens);
        }
    }
    private static async Task ArtworkDemandRetirement(string change, bool fm = false)
    {
        await using var f = await Fixture.Create();
        f.Platform.SampleCover = "https://art.example.test/old";
        f.Platform.HomeActions = [fm ? FMArtworkCard("old", new { type = "fm", ruleId = "71" }) : MissingArtworkCard("500", "old")];
        await f.Module.NavigateAsync("catalog-home", default);
        var card = VisibleCards(f).Single();
        var held = f.App.HoldNext(fm ? "music.catalog.fm" : "music.catalog.playlists");
        f.Module.UpdateCatalogVisibleCards([card]);
        var old = f.Module.CatalogEntryInitialization;
        await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task closing = Task.CompletedTask;
        if (change == "refresh") await f.Module.RefreshCatalogAsync();
        else if (change == "account") { f.App.RevokeAccount("wy"); await Drain(); }
        else if (change == "source") await f.Module.SetCatalogSourceAsync("kg");
        else
        {
            closing = f.Module.PrepareShutdownAsync();
            await Drain();
            Check(!closing.IsCompleted && !old.IsCompleted, "closing waits for the actual noncooperative receipt");
        }
        held.Release(); await old; await closing;
        Equal("", card.Cover);
        Equal(change == "source" ? 1 : 0, f.Store.LibraryWrites); Equal(0, f.Output.Opens);
    }
}
