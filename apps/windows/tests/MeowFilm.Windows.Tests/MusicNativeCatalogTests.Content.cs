using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativeCatalogTests
{
    private static IEnumerable<TestCase> ContentCases()
    {
        yield return new("Native catalog shared browse fixture preserves partial cards layouts and independent covers", SharedBrowseContent);
        yield return new("Native catalog shared home directory and single song list fixtures use fixed DTOs", SharedContentKinds);
        yield return new("Native catalog explicit playlist summaries preserve IDs and do not adopt songs", PlaylistSummaries);
        yield return new("Native catalog incomplete recording locators keep cards without weakening durable source integrity", PartialRecordingLocators);
        yield return new("Native catalog style write readback uses the single repository without changing its receipt", DailyStyleReadback);
    }
    internal static JsonElement ContentFixture(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestWorkspace.SourceRoot,
            "tests", "fixtures", "music-content", name + ".json")));
        return document.RootElement.Clone();
    }
    private static async Task SharedBrowseContent()
    {
        await using var f = new Fixture();
        var input = ContentFixture("browse-page");
        f.Platform.Handler = _ => Task.FromResult(input);
        var reply = Ok(await f.Run("channel", new { platform = "wy", id = "synthetic:channel" }));
        var sections = MusicCatalogDto.Read(reply, "wy", "channel", "");
        Check(sections.Select(section => section.Layout).SequenceEqual(
            ["banner", "feature-rail", "song-list", "playlist-grid", "ranking", "song-list"]));
        Check(sections.Select(section => section.Items.Count).SequenceEqual([2, 1, 2, 2, 1, 0]),
            "partial cards and the empty section retain their positions");
        var banners = sections[0].Items;
        Check(banners[0].Cover == "https://images.invalid/banner.jpg" && banners[1].Cover == "" &&
            banners.All(card => card.CoverRef is null && card.Song is not null),
            "a banner never borrows the nested song image, even when its own image is missing");
        Check(banners[0].Title == "活动横幅" && banners[0].Song!.Title == "合成歌曲");
        var incomplete = sections[2].Items[1];
        Check(incomplete.Title == "仅展示歌曲" && incomplete.Song is null && !incomplete.CanPlay,
            "display-only material cannot manufacture a permanent recording");
        Check(sections[3].Items.All(card => !card.CanOpen) &&
            sections[3].Items[1].Cover == "https://images.invalid/unknown.jpg");
        Check(sections[1].Items[0].Caption == "电音歌单" &&
            sections[1].Items[0].CoverRef == new MusicCatalogCoverReference("playlist", "synthetic-list"));
        Check(sections[1].Tabs!.Count == 2 && sections[1].SelectedTab == "recommend");
        sections[1].SelectTab(sections[1].Tabs![1]);
        Check(sections[1].SelectedTab == "other" && sections[1].Items[0].CoverRef!.Id == "synthetic-other");
        Check(sections[4].Presentation == MusicCatalogSectionPresentation.RankingPreviews &&
            sections[4].Items[0].CanOpen && !sections[4].Items[0].CanPlay &&
            sections[4].Action!.ChannelKind == "rankings");
        Check(MusicTestStores.For(f.Store).Root["songs"]!.AsObject().Count == 1 &&
            N(await f.Queue(), "total") == 0, "only the repeated real recording is adopted, without queue side effects");
        // Field absence and wrong optional types are local display gaps, including inside previews.
        var partial = O(input);
        partial["sections"]![0]!["items"]![0]!["dayOfMonth"] = "unknown";
        partial["sections"]![0]!["items"]![0]!["preview"] = new JsonArray(new JsonObject { ["song"] = new JsonObject { ["sources"] = false } });
        partial["sections"]![3]!["items"]!.AsArray().Add(null);
        partial["sections"]!.AsArray().Add(new JsonObject { ["title"] = "保留栏目", ["items"] = false });
        f.Platform.Handler = _ => Task.FromResult(J(partial));
        var changed = MusicCatalogDto.Read(Ok(await f.Run("home", new { platform = "wy" })), "wy", "home", "");
        Check(changed.Count == 7 && changed[3].Items.Count == 3 && changed[0].Items[0].DayOfMonth is null);
        Check(changed[^1].Title == "保留栏目" && changed[^1].Items.Count == 0);
    }
    private static async Task SharedContentKinds()
    {
        await using var f = new Fixture();
        foreach (var (fixture, operation) in new[] { ("home-page", "home"), ("channels", "channels"), ("list-page", "playlist") })
        {
            var input = ContentFixture(fixture);
            f.Platform.Handler = _ => Task.FromResult(input);
            var platform = S(input, "platform");
            var reply = Ok(await f.Run(operation, new { platform, id = "list" }));
            var sections = MusicCatalogDto.Read(reply, platform, operation, "");
            if (operation == "home")
            {
                var card = sections[0].Items[0];
                Check(card.Label.Length > 0 && card.Caption == card.Subtitle && card.HasLabel);
            }
            else if (operation == "channels")
                Check(sections[0].Items.Select(card => card.ChannelKind).SequenceEqual(
                    input.GetProperty("items").EnumerateArray().Select(item => S(item, "channelKind"))));
            else
            {
                var card = sections.Single().Items.Single();
                Check(card.Title == "合成歌曲" && card.Cover == "https://images.invalid/song.jpg" && card.Song is null);
                Check(reply.GetProperty("items")[0].GetProperty("rank").GetInt32() == 1);
            }
        }
    }
    private static async Task PlaylistSummaries()
    {
        await using var f = new Fixture();
        var result = E(new { platform = "wy", items = new[] { new { id = "b", cover = "" }, new { id = "a", cover = "https://images.invalid/a" } } });
        f.Platform.Handler = _ => Task.FromResult(result);
        var reply = Ok(await f.Run("playlists", new { platform = "wy", ids = new[] { "a", "b" } }));
        var call = f.Platform.Calls.Single();
        Check(call.Method == "playlists" && call.Request is MeowFilm.Capabilities.Platforms.MusicPlaylistsRequest request &&
            request.Ids.SequenceEqual(["a", "b"]));
        Check(JsonElement.DeepEquals(reply.GetProperty("items"), result.GetProperty("items")) && f.Store.Writes == 0);
    }
    private static async Task DailyStyleReadback()
    {
        foreach (bool retire in new[] { false, true })
        {
            await using var f = new Fixture();
            var song = Song("wy", 0);
            f.Platform.Handler = _ =>
            {
                if (retire) f.Owner.Invalidate("wy");
                return Task.FromResult(E(new { items = new[] { song }, category = "1", tags = new[] { new { id = "11", name = "Current" } } }));
            };
            var editor = await f.Owner.OpenDailyStyleEditorAsync();
            var result = await f.Owner.SaveDailyStyleAsync(editor, new("1", ["11"]));
            Check(result.Status == MeowFilm.Capabilities.Platforms.MusicDailyStyleWriteStatus.Confirmed &&
                result.SelectionMatches == true && f.Platform.Calls.Length == 1, "a known write receipt is never downgraded or replayed");
            if (retire)
                Check(result.Readback is null && f.Store.Writes == 0, "retired metadata has no publication or adoption authority");
            else
            {
                var rows = MusicCatalogDto.Read(result.Readback!.Value, "wy", "daily", "");
                Check(rows.Single().Items.Single().Song is { Id.Length: > 0 } && f.Store.Writes == 1,
                    "readback playback borrows the same permanent recording as ordinary reads");
                Check(N(await f.Queue(), "total") == 0, "metadata readback cannot change playback");
            }
        }
    }
    private static async Task PartialRecordingLocators()
    {
        foreach (string defect in new[] { "missing-id", "missing-platform", "mismatched-key", "wrong-info" })
        {
            await using var f = new Fixture();
            var song = MusicSongFixture.Online("wy", "partial", "Retained song");
            var source = song["sources"]![0]!;
            if (defect == "missing-id") source["musicInfo"]!["songmid"] = "";
            else if (defect == "missing-platform") source["musicInfo"]!["source"] = "";
            else if (defect == "mismatched-key") source["sourceKey"] = "not-the-recording-key";
            else source["musicInfo"] = false;
            var page = E(new { platform = "wy", sections = new[] { new { layout = "song-list",
                items = new[] { new { kind = "song", title = "Retained card", song } } } } });
            f.Platform.Handler = _ => Task.FromResult(page);
            var reply = Ok(await f.Run("home", new { platform = "wy" }));
            var card = MusicCatalogDto.Read(reply, "wy", "home", "").Single().Items.Single();
            Check(card.Title == "Retained card" && card.Song is null && !card.CanPlay,
                "an unusable durable locator cannot reject or erase its display card: " + defect);
            Check(f.Store.Writes == 0, "partial identities never bypass durable source validation");
        }
    }
}
