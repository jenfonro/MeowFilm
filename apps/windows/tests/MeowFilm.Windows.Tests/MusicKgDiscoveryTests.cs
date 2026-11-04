using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Windows.Tests;

// Synthetic excerpts of the captured KG discovery responses. No tmp files or
// production normalizers construct the fixtures or expected public results.
public static class MusicKgDiscoveryTests
{
    private const string Recommend = "/v2/special_recommend";
    private const string NewSongs = "/musicadservice/container/v1/newsong_publish";
    private const string RankList = "/ocean/v6/rank/list";
    private const string Tags = "/pubsongs/v1/get_tags_by_type";
    private const string RankAudio = "/openapi/kmr/v2/rank/audio";

    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("home reads only three summaries and preserves resource identities", Home);
        yield return Case("default recommendation overflow is retained and continuation reads only recommendations", HomePaging);
        yield return Case("directory publishes four primary channels and six real category groups", Directory);
        yield return Case("playlist tags retain real groups selection and public playlist previews", PlaylistTags);
        yield return Case("tag continuation advances and changing tags starts at page one", TagPaging);
        yield return Case("continuations reject another platform target or tag without fetching mixed content", ContinuationBindings);
        yield return Case("featured channel continuation remains distinct from home", FeaturePaging);
        yield return Case("rank card opens through revalidated rank ID CID and the dedicated audio POST", OpenRank);
        yield return Case("rank continuation requests only its next member page", RankPaging);
        yield return Case("rank locators and returned member identities reject mismatches", RankIdentity);
        yield return Case("new songs preserve hashes filename metadata and millisecond duration across pages", NewSongPaging);
        yield return Case("explicit empty terminal catalogs remain successful without continuation", EmptyPages);
        yield return Case("missing member pages and empty nonterminal recommendations fail explicitly", IncompletePages);
        yield return Case("missing or malformed discovery fields never become an empty success", MalformedResponses);
        yield return Case("upstream errors retain their public codes without retries or fallback", UpstreamErrors);
    }

    private static async Task Home()
    {
        int recommendations = 0;
        using var f = new Fixture(request =>
        {
            if (request.Path == Recommend)
            {
                Equal(HttpMethod.Post, request.Method);
                Equal("specialrec.service.kugou.com", request.Router);
                Equal(1, Number(request.Body, "page"));
                Equal(0, Number(request.Body["special_recommend"]!.AsObject(), "categoryid"));
                recommendations = Number(request.Body, "pagesize");
                return Playlists(recommendations, more: true);
            }
            if (request.Path == NewSongs)
            {
                Equal(HttpMethod.Post, request.Method);
                Equal(1, Number(request.Body, "page"));
                Equal(21608, Number(request.Body, "rank_id"));
                return NewSongPage(1, 1);
            }
            if (request.Path == RankList)
            {
                Equal(HttpMethod.Get, request.Method);
                Equal("2", request.Query["plat"]);
                Equal("1", request.Query["withsong"]);
                Equal("0", request.Query["parentid"]);
                return Ranks();
            }
            throw Unexpected(request);
        });
        var page = await f.Catalog.HomeAsync(new("kg"));
        Equal("kg", page.GetProperty("platform").GetString());
        Equal(3, page.GetProperty("sections").GetArrayLength());
        var playlists = Items(page, "playlists");
        Equal(recommendations, playlists.Length);
        Equal("collection_3_123456_1_0", playlists[0].GetProperty("id").GetString());
        Equal("playlist", playlists[0].GetProperty("action").GetProperty("type").GetString());
        Equal("collection_3_123456_1_0", playlists[0].GetProperty("action").GetProperty("id").GetString());
        Song(Items(page, "songs").Single(), 1, "Synthetic new song 1", "Synthetic singer", 279);
        var rank = Items(page, "rankings").Single();
        Rank(rank, "8888", "156133");
        var card = MusicCatalogDto.Read(page, "kg", "home", "").Single(section => section.Type == "rankings").Items.Single();
        Check(card.Kind == "channel" && card.CanOpen && card.ActionId == "rank-8888" && card.PageCode == "156133",
            "a rank opens as its channel, never as a public playlist");
        Equal("", card.PreviewSongs.Single().Movement);
        Check(page.GetProperty("more").GetBoolean(), "recommendations have a next page");
        f.Requests(Recommend, NewSongs, RankList);
    }

    private static async Task HomePaging()
    {
        int firstCount = 0;
        using var f = new Fixture(request =>
        {
            if (request.Path == Recommend)
            {
                int page = Number(request.Body, "page");
                if (page == 1)
                {
                    firstCount = Number(request.Body, "pagesize") + 5;
                    return Playlists(firstCount, more: true);
                }
                Equal(2, page);
                return Playlists(1, start: 100, more: false);
            }
            return request.Path switch
            {
                NewSongs => NewSongPage(0, 0),
                RankList => Ranks(),
                _ => throw Unexpected(request)
            };
        });
        var first = await f.Catalog.HomeAsync(new("kg"));
        Equal(firstCount, Items(first, "playlists").Length);
        Check(firstCount > Number(f.Snapshot().Single(r => r.Path == Recommend).Body, "pagesize"),
            "the server returned more default recommendations than requested");
        var second = await f.Catalog.HomeAsync(new("kg", first.GetProperty("continuation")));
        Equal(1, second.GetProperty("sections").GetArrayLength());
        Equal("collection_3_123456_100_0", Items(second, "playlists").Single().GetProperty("id").GetString());
        Terminal(second);
        await Error("STALE_SESSION", () => f.Catalog.ChannelAsync(new("kg", "feature",
            Continuation: first.GetProperty("continuation"))));
        f.Requests(Recommend, NewSongs, RankList, Recommend);
    }

    private static async Task Directory()
    {
        using var f = new Fixture(request =>
        {
            Equal(Tags, request.Path);
            Equal(HttpMethod.Post, request.Method);
            Equal("collection", request.Body["tag_type"]!.GetValue<string>());
            Equal(0, Number(request.Body, "tag_id"));
            Equal(3, Number(request.Body, "source"));
            return TagGroups();
        });
        var page = await f.Catalog.ChannelsAsync(new("kg"));
        Equal("kg", page.GetProperty("platform").GetString());
        var items = page.GetProperty("items").EnumerateArray().ToArray();
        Equal("feature,playlist,chart,newsong", string.Join(',', items.Where(item =>
            item.GetProperty("placement").GetString() == "primary").Select(item => item.GetProperty("id").GetString())));
        Equal("category-5,category-1,category-2,category-8,category-4,category-7", string.Join(',', items.Where(item =>
            item.GetProperty("placement").GetString() == "more").Select(item => item.GetProperty("id").GetString())));
        Equal("场景,主题,语种,风格,心情,年代", string.Join(',', items.Skip(4).Select(item => item.GetProperty("name").GetString())));
        Equal(10, items.Length);
        Check(items.All(item => item.GetProperty("kind").GetString() == "channel" &&
            item.GetProperty("available").GetBoolean()), "all KG descriptors are available neutral channels");
        f.Requests(Tags);
    }

    private static async Task PlaylistTags()
    {
        using var f = new Fixture(request => request.Path switch
        {
            Tags => TagGroups(),
            Recommend => Playlists(1, more: false, preview: true),
            _ => throw Unexpected(request)
        });
        var defaults = await f.Catalog.ChannelAsync(new("kg", "playlist"));
        Equal("0", defaults.GetProperty("selectedTag").GetString());
        var page = await f.Catalog.ChannelAsync(new("kg", "playlist", Tag: "587"));
        var tags = page.GetProperty("tags").EnumerateArray().ToArray();
        Equal(13, tags.Length);
        Equal("0,587,660,1084,12,84,20,9,574,1107,578,13,14",
            string.Join(',', tags.Select(tag => tag.GetProperty("id").GetString())));
        Equal("场景,场景,主题,主题,语种,语种,风格,风格,心情,心情,年代,年代",
            string.Join(',', tags.Skip(1).Select(tag => tag.GetProperty("group").GetString())));
        var selected = tags.Single(tag => tag.GetProperty("selected").GetBoolean());
        Equal("587", selected.GetProperty("id").GetString());
        Equal("学习", selected.GetProperty("name").GetString());
        Equal("587", page.GetProperty("selectedTag").GetString());
        var card = Items(page, "playlists").Single();
        Equal("collection_3_123456_1_0", card.GetProperty("id").GetString());
        Equal(196, card.GetProperty("count").GetInt32());
        Equal("Synthetic preview", card.GetProperty("preview")[0].GetProperty("title").GetString());
        Equal("Synthetic singer", card.GetProperty("preview")[0].GetProperty("artist").GetString());
        Equal(1, card.GetProperty("preview").GetArrayLength());
        Check(card.GetProperty("available").GetBoolean(), "public playlists remain openable without an account");
        Equal("0,587", string.Join(',', f.Snapshot().Where(r => r.Path == Recommend)
            .Select(r => Number(r.Body["special_recommend"]!.AsObject(), "categoryid"))));
        f.PlaylistRequests(2);
    }

    private static async Task TagPaging()
    {
        using var f = new Fixture(request =>
        {
            if (request.Path == Tags) return TagGroups();
            Equal(Recommend, request.Path);
            int page = Number(request.Body, "page");
            int tag = Number(request.Body["special_recommend"]!.AsObject(), "categoryid");
            Check(tag is 587 or 660, "only the selected category may be fetched");
            return Playlists(page == 1 ? Number(request.Body, "pagesize") : 1, tag * 1000 + page * 100, more: page == 1);
        });
        var first = await f.Catalog.ChannelAsync(new("kg", "category-5", Tag: "587"));
        Equal("场景", first.GetProperty("title").GetString());
        Equal("587,660", string.Join(',', first.GetProperty("tags").EnumerateArray()
            .Select(tag => tag.GetProperty("id").GetString())));
        var second = await f.Catalog.ChannelAsync(new("kg", "category-5",
            Continuation: first.GetProperty("continuation"), Tag: "587"));
        Equal("collection_3_123456_587200_0", Items(second, "playlists").Single().GetProperty("id").GetString());
        Terminal(second);
        var changed = await f.Catalog.ChannelAsync(new("kg", "category-5", Tag: "660"));
        Equal("660", changed.GetProperty("selectedTag").GetString());
        Equal("collection_3_123456_660100_0", Items(changed, "playlists")[0].GetProperty("id").GetString());
        Equal("1,2,1", string.Join(',', f.Snapshot().Where(r => r.Path == Recommend).Select(r => Number(r.Body, "page"))));
        f.PlaylistRequests(3);
    }

    private static async Task ContinuationBindings()
    {
        using var f = new Fixture(request => request.Path switch
        {
            Tags => TagGroups(),
            Recommend => Playlists(Number(request.Body, "pagesize"), more: true),
            _ => throw Unexpected(request)
        }, allowWy: true);
        var first = await f.Catalog.ChannelAsync(new("kg", "playlist", Tag: "587"));
        var continuation = first.GetProperty("continuation");
        await Error("SNAPSHOT_CHANGED", () => f.Catalog.ChannelAsync(new("kg", "playlist",
            Continuation: continuation, Tag: "660")));
        await Error("STALE_SESSION", () => f.Catalog.ChannelAsync(new("kg", "category-5",
            Continuation: continuation, Tag: "587")));
        await Error("STALE_SESSION", () => f.Catalog.HomeAsync(new("kg", continuation)));
        await Error("INVALID_REQUEST", () => f.Catalog.HomeAsync(new("wy", continuation)));
        await Error("INVALID_REQUEST", () => f.Catalog.ChannelAsync(new("kg", "category-5", Tag: "84")));
        await Error("NOT_FOUND", () => f.Catalog.ChannelAsync(new("kg", "category-999")));
        Equal(1, f.Snapshot().Count(request => request.Path == Recommend));
        Check(f.Snapshot().All(request => request.Path is Tags or Recommend),
            "invalid continuation must not fetch home, another platform, or another content page");
    }

    private static async Task FeaturePaging()
    {
        using var f = new Fixture(request => request.Path switch
        {
            Recommend => Playlists(Number(request.Body, "page") == 1 ? Number(request.Body, "pagesize") : 1,
                start: Number(request.Body, "page") == 1 ? 100 : 1, more: Number(request.Body, "page") == 1),
            NewSongs => NewSongPage(0, 0),
            RankList => Ranks(),
            _ => throw Unexpected(request)
        });
        var first = await f.Catalog.ChannelAsync(new("kg", "feature"));
        Equal(3, first.GetProperty("sections").GetArrayLength());
        var continuation = first.GetProperty("continuation");
        await Error("STALE_SESSION", () => f.Catalog.HomeAsync(new("kg", continuation)));
        var second = await f.Catalog.ChannelAsync(new("kg", "feature", Continuation: continuation));
        Equal(1, second.GetProperty("sections").GetArrayLength());
        Equal("collection_3_123456_1_0", Items(second, "playlists").Single().GetProperty("id").GetString());
        Terminal(second);
        f.Requests(Recommend, NewSongs, RankList, Recommend);
    }

    private static async Task OpenRank()
    {
        using var f = new Fixture(request =>
        {
            if (request.Path == RankList) return Ranks();
            Equal(RankAudio, request.Path);
            Equal(HttpMethod.Post, request.Method);
            Equal("369", request.Tid);
            Equal(8888, Number(request.Body, "rank_id"));
            Equal(156133, Number(request.Body, "rank_cid"));
            Equal(1, Number(request.Body, "page"));
            var response = RankPage(2, 2);
            response["data"]!["songlist"]![1]!["audio_info"]!["duration_128"] = 185500;
            return response;
        });
        var chart = await f.Catalog.ChannelAsync(new("kg", "chart"));
        var card = Items(chart, "rankings").Single();
        Rank(card, "8888", "156133");
        var action = card.GetProperty("action").GetProperty("channel");
        var songs = await f.Catalog.ChannelAsync(new("kg", action.GetProperty("id").GetString()!,
            PageCode: action.GetProperty("pageCode").GetString()));
        var items = Items(songs, "songs");
        Equal(2, items.Length);
        Song(items[0], 1, "Synthetic rank song 1", "Synthetic singer", 216);
        Song(items[1], 2, "Synthetic rank song 2", "Synthetic singer", 185.5);
        Terminal(songs);
        Equal(string.Join(',', RankList, RankList, RankAudio), string.Join(',', f.Snapshot().Select(request => request.Path)));
        f.Requests(RankList, RankList, RankAudio);
    }

    private static async Task RankPaging()
    {
        int size = 0;
        using var f = new Fixture(request =>
        {
            if (request.Path == RankList) return Ranks();
            Equal(RankAudio, request.Path);
            Equal("369", request.Tid);
            Equal(8888, Number(request.Body, "rank_id"));
            Equal(156133, Number(request.Body, "rank_cid"));
            size = Number(request.Body, "pagesize");
            int page = Number(request.Body, "page");
            return RankPage(page == 1 ? size : 1, size + 1, start: page == 1 ? 1 : size + 1);
        });
        var first = await f.Catalog.ChannelAsync(new("kg", "rank-8888", PageCode: "156133"));
        Equal(size, Items(first, "songs").Length);
        Check(first.GetProperty("more").GetBoolean(), "rank has exactly one remaining member");
        var next = await f.Catalog.ChannelAsync(new("kg", "rank-8888", PageCode: "156133",
            Continuation: first.GetProperty("continuation")));
        Song(Items(next, "songs").Single(), size + 1, "Synthetic rank song " + (size + 1), "Synthetic singer", 216);
        Terminal(next);
        Equal("1,2", string.Join(',', f.Snapshot().Where(r => r.Path == RankAudio).Select(r => Number(r.Body, "page"))));
        f.Requests(RankList, RankAudio, RankList, RankAudio);
    }

    private static async Task RankIdentity()
    {
        using (var f = new Fixture(request => request.Path == RankList ? Ranks() : throw Unexpected(request)))
        {
            await Error("IDENTITY_MISMATCH", () => f.Catalog.ChannelAsync(new("kg", "rank-8888", PageCode: "147778")));
            await Error("NOT_FOUND", () => f.Catalog.ChannelAsync(new("kg", "rank-9999")));
            f.Requests(RankList, RankList);
        }
        foreach (string fault in new[] { "changed-cid", "member-cid", "conflicting-total" })
        {
            int lists = 0;
            using var f = new Fixture(request =>
            {
                if (request.Path == RankList) return Ranks(fault == "changed-cid" && ++lists > 1 ? 156134 : 156133);
                Equal(RankAudio, request.Path);
                int size = Number(request.Body, "pagesize"), page = Number(request.Body, "page");
                var response = RankPage(page == 1 ? size : 1, size + 1, start: page == 1 ? 1 : size + 1);
                if (page == 2)
                {
                    if (fault == "member-cid") response["data"]!["songlist"]![0]!["rank_cid"] = 147778;
                    if (fault == "conflicting-total") response["total"] = size + 2;
                }
                return response;
            });
            var first = await f.Catalog.ChannelAsync(new("kg", "rank-8888"));
            await Error(fault == "changed-cid" ? "SNAPSHOT_CHANGED" : "IDENTITY_MISMATCH",
                () => f.Catalog.ChannelAsync(new("kg", "rank-8888", Continuation: first.GetProperty("continuation"))));
            Equal(fault == "changed-cid" ? 1 : 2, f.Snapshot().Count(request => request.Path == RankAudio));
        }
    }

    private static async Task NewSongPaging()
    {
        int size = 0;
        using var f = new Fixture(request =>
        {
            Equal(NewSongs, request.Path);
            Equal(HttpMethod.Post, request.Method);
            Equal(21608, Number(request.Body, "rank_id"));
            size = Number(request.Body, "pagesize");
            int page = Number(request.Body, "page");
            return NewSongPage(page == 1 ? size : 1, size + 1, page == 1 ? 1 : size + 1);
        });
        var first = await f.Catalog.ChannelAsync(new("kg", "newsong"));
        Song(Items(first, "songs")[0], 1, "Synthetic new song 1", "Synthetic singer", 279);
        Equal(size, Items(first, "songs").Length);
        var next = await f.Catalog.ChannelAsync(new("kg", "newsong", Continuation: first.GetProperty("continuation")));
        Song(Items(next, "songs").Single(), size + 1, "Synthetic new song " + (size + 1), "Synthetic singer", 279);
        Terminal(next);
        Equal("1,2", string.Join(',', f.Snapshot().Select(request => Number(request.Body, "page"))));
        f.Requests(NewSongs, NewSongs);
    }

    private static async Task EmptyPages()
    {
        using var f = new Fixture(request => request.Path switch
        {
            Recommend => Playlists(0),
            NewSongs => NewSongPage(0, 0),
            RankList => Ranks(),
            RankAudio => RankPage(0, 0),
            Tags => TagGroups(),
            _ => throw Unexpected(request)
        });
        var home = await f.Catalog.HomeAsync(new("kg"));
        Equal(0, Items(home, "playlists").Length);
        Equal(0, Items(home, "songs").Length);
        Terminal(home);
        foreach (string id in new[] { "playlist", "newsong", "rank-8888" })
        {
            var page = await f.Catalog.ChannelAsync(new("kg", id));
            Equal(0, page.GetProperty("sections").EnumerateArray().Sum(section => section.GetProperty("items").GetArrayLength()));
            Terminal(page);
        }
        using var emptyRanks = new Fixture(request => request.Path == RankList
            ? Json("""{"status":1,"errcode":0,"error":"","data":{"info":[],"total":0}}""") : throw Unexpected(request));
        var chart = await emptyRanks.Catalog.ChannelAsync(new("kg", "chart"));
        Equal(0, Items(chart, "rankings").Length);
        Terminal(chart);
        using var emptyTags = new Fixture(request => request.Path == Tags
            ? Json("""{"status":1,"error_code":0,"data":[]}""") : throw Unexpected(request));
        var directory = await emptyTags.Catalog.ChannelsAsync(new("kg"));
        Equal("feature,playlist,chart,newsong", string.Join(',', directory.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())));
        f.Requests(Recommend, NewSongs, RankList, Tags, Recommend, NewSongs, RankList, RankAudio);
        emptyRanks.Requests(RankList);
        emptyTags.Requests(Tags);
    }

    private static async Task IncompletePages()
    {
        foreach (string endpoint in new[] { NewSongs, RankAudio })
        foreach (string fault in new[] { "short-first", "empty-first", "missing-second", "overflow-first" })
        {
            using var f = new Fixture(request =>
            {
                if (request.Path == RankList) return Ranks();
                Equal(endpoint, request.Path);
                int size = Number(request.Body, "pagesize"), page = Number(request.Body, "page");
                int count = fault switch
                {
                    "short-first" => size - 1, "empty-first" => 0, "overflow-first" => size + 1,
                    _ => page == 2 ? 0 : size
                };
                return endpoint == NewSongs ? NewSongPage(count, size + 2) : RankPage(count, size + 2);
            });
            string id = endpoint == NewSongs ? "newsong" : "rank-8888";
            JsonElement? continuation = null;
            if (fault == "missing-second")
                continuation = (await f.Catalog.ChannelAsync(new("kg", id))).GetProperty("continuation");
            await Error(fault == "overflow-first" ? "INVALID_RESPONSE" : "INCOMPLETE_PAGE",
                () => f.Catalog.ChannelAsync(new("kg", id, Continuation: continuation)));
            Equal(fault == "missing-second" ? 2 : 1, f.Snapshot().Count(request => request.Path == endpoint));
        }
        using var emptyMore = new Fixture(request => request.Path switch
        {
            Tags => TagGroups(),
            Recommend => Playlists(0, more: true),
            _ => throw Unexpected(request)
        });
        await Error("PAGINATION_STALLED", () => emptyMore.Catalog.ChannelAsync(new("kg", "playlist", Tag: "587")));
        emptyMore.Requests(Tags, Recommend);
    }

    private static async Task MalformedResponses()
    {
        foreach (var (endpoint, body) in new[]
        {
            (Tags, """{"status":1,"error_code":0}"""),
            (Tags, """{"status":1,"error_code":0,"data":{}}"""),
            (Recommend, """{"status":1,"error_code":0,"data":{"has_next":0}}"""),
            (Recommend, """{"status":1,"error_code":0,"data":{"has_next":0,"special_list":{}}}"""),
            (RankList, """{"status":1,"errcode":0,"error":"","data":{}}"""),
            (RankList, """{"status":1,"errcode":0,"error":"","data":{"info":null}}"""),
            (NewSongs, """{"status":1,"error_code":0,"total":1}"""),
            (NewSongs, """{"status":1,"error_code":0,"total":1,"data":null}"""),
            (RankAudio, """{"status":1,"error_code":0,"total":1,"data":{"total":1}}"""),
            (RankAudio, """{"status":1,"error_code":0,"total":1,"data":{"total":1,"songlist":{}}}""")
        })
        {
            using var f = new Fixture(request => request.Path == endpoint ? Json(body) : request.Path switch
            {
                Tags => TagGroups(),
                RankList => Ranks(),
                _ => throw Unexpected(request)
            });
            await Error("INVALID_RESPONSE", () => Read(f.Catalog, endpoint));
            f.Requests(PathsFor(endpoint));
        }
        using var wrongParent = new Fixture(request =>
        {
            Equal(Tags, request.Path);
            var tags = TagGroups();
            tags["data"]![0]!["son"]![0]!["parent_id"] = "8";
            return tags;
        });
        await Error("IDENTITY_MISMATCH", () => wrongParent.Catalog.ChannelsAsync(new("kg")));
        wrongParent.Requests(Tags);
    }

    private static async Task UpstreamErrors()
    {
        foreach (string endpoint in new[] { Tags, Recommend, NewSongs, RankList, RankAudio })
        foreach (var (code, expected) in new[] { (7000, "UPSTREAM_ERROR"), (20018, "AUTH_REQUIRED"), (20028, "VERIFICATION_REQUIRED") })
        {
            using var f = new Fixture(request => request.Path == endpoint
                ? Object(new { status = 0, error_code = code, data = new { } })
                : request.Path switch
                {
                    Tags => TagGroups(),
                    RankList => Ranks(),
                    _ => throw Unexpected(request)
                });
            await Error(expected, () => Read(f.Catalog, endpoint));
            f.Requests(PathsFor(endpoint));
        }
    }

    private static Task<JsonElement> Read(IMusicPlatformCatalog catalog, string endpoint) => endpoint switch
    {
        Tags => catalog.ChannelsAsync(new("kg")),
        Recommend => catalog.ChannelAsync(new("kg", "playlist")),
        NewSongs => catalog.ChannelAsync(new("kg", "newsong")),
        RankList => catalog.ChannelAsync(new("kg", "chart")),
        RankAudio => catalog.ChannelAsync(new("kg", "rank-8888", PageCode: "156133")),
        _ => throw new InvalidOperationException("Unknown test endpoint")
    };

    private static string[] PathsFor(string endpoint) => endpoint switch
    {
        Recommend => [Tags, Recommend],
        RankAudio => [RankList, RankAudio],
        _ => [endpoint]
    };

    private static JsonObject Playlists(int count, int start = 1, bool more = false, bool preview = false) => Object(new
    {
        status = 1, error_code = 0,
        data = new
        {
            has_next = more ? 1 : 0,
            special_list = Enumerable.Range(start, count).Select(id => new
            {
                specialid = 900000 + id, global_collection_id = "collection_3_123456_" + id + "_0",
                specialname = "Synthetic playlist " + id, songcount = 196, nickname = "Synthetic creator",
                imgurl = "https://imge.kugou.com/synthetic/{size}/playlist.jpg",
                songs = preview ? new[] { new { hash = Hash(id), filename = "Synthetic singer - Synthetic preview", duration = 146 } } : []
            }).ToArray()
        }
    });

    private static JsonObject NewSongPage(int count, int total, int start = 1) => Object(new
    {
        status = 1, error_code = 0, total,
        data = Enumerable.Range(start, count).Select(id => new
        {
            hash = Hash(id).ToLowerInvariant(), filename = "Synthetic singer - Synthetic new song " + id,
            timelength = 279000, album_id = 212839391, album_audio_id = 1000 + id, album_name = "Synthetic album"
        }).ToArray()
    });

    private static JsonObject RankPage(int count, int total, int start = 1) => Object(new
    {
        status = 1, error_code = 0, total,
        data = new
        {
            total,
            songlist = Enumerable.Range(start, count).Select(id => new
            {
                audio_info = new { hash_128 = Hash(id).ToLowerInvariant(), duration_128 = 216000, filesize_128 = 3473110 },
                songname = "Synthetic rank song " + id, author_name = "Synthetic singer",
                album_info = new { album_name = "Synthetic album", sizable_cover = "https://imge.kugou.com/synthetic/{size}/album.jpg" },
                album_id = 213380582, album_audio_id = 2000 + id, rank_cid = 156133
            }).ToArray()
        }
    });

    private static JsonObject Ranks(int cid = 156133) => Object(new
    {
        status = 1, errcode = 0, error = "",
        data = new
        {
            total = 1,
            info = new[] { new
            {
                id = 2, rankid = 8888, rank_cid = cid, rankname = "Synthetic TOP500",
                imgurl = "https://imge.kugou.com/synthetic/{size}/rank.jpg", update_frequency = "每天",
                songinfo = new[] { new { name = "Synthetic preview", author = "Synthetic singer" } },
                children = Array.Empty<object>()
            } }
        }
    });

    // First two children of each real group, in provider order, from tags.json.
    private static JsonObject TagGroups() => Json("""
        {"status":1,"error_code":0,"data":[
          {"parent_id":"0","tag_id":"5","tag_name":"场景","son":[
            {"parent_id":"5","tag_id":"587","tag_name":"学习"},{"parent_id":"5","tag_id":"660","tag_name":"工作"}]},
          {"parent_id":"0","tag_id":"1","tag_name":"主题","son":[
            {"parent_id":"1","tag_id":"1084","tag_name":"精选"},{"parent_id":"1","tag_id":"12","tag_name":"经典"}]},
          {"parent_id":"0","tag_id":"2","tag_name":"语种","son":[
            {"parent_id":"2","tag_id":"84","tag_name":"国语"},{"parent_id":"2","tag_id":"20","tag_name":"英语"}]},
          {"parent_id":"0","tag_id":"8","tag_name":"风格","son":[
            {"parent_id":"8","tag_id":"9","tag_name":"流行"},{"parent_id":"8","tag_id":"574","tag_name":"古风"}]},
          {"parent_id":"0","tag_id":"4","tag_name":"心情","son":[
            {"parent_id":"4","tag_id":"1107","tag_name":"怀旧"},{"parent_id":"4","tag_id":"578","tag_name":"伤感"}]},
          {"parent_id":"0","tag_id":"7","tag_name":"年代","son":[
            {"parent_id":"7","tag_id":"13","tag_name":"70后"},{"parent_id":"7","tag_id":"14","tag_name":"80后"}]}
        ]}
        """);

    private static void Song(JsonElement song, int id, string title, string artist, double duration)
    {
        Equal(title, song.GetProperty("title").GetString());
        Equal(artist, song.GetProperty("artist").GetString());
        Equal(duration, song.GetProperty("duration").GetDouble());
        Equal(1, song.GetProperty("sources").GetArrayLength());
        var source = song.GetProperty("sources")[0];
        Equal("kg", source.GetProperty("source").GetString());
        Equal(Hash(id), source.GetProperty("musicInfo").GetProperty("hash").GetString());
        Equal(Hash(id), source.GetProperty("musicInfo").GetProperty("songmid").GetString());
        Check(song.GetProperty("songId").GetString()!.StartsWith("song:", StringComparison.Ordinal), "canonical song identity");
        Check(!song.TryGetProperty("url", out _) && !source.GetProperty("musicInfo").TryGetProperty("url", out _),
            "directory songs must not contain resolved audio");
    }

    private static void Rank(JsonElement card, string id, string cid)
    {
        Equal("rank-" + id, card.GetProperty("id").GetString());
        Equal("kg", card.GetProperty("source").GetString());
        Equal("channel", card.GetProperty("action").GetProperty("type").GetString());
        var channel = card.GetProperty("action").GetProperty("channel");
        Equal("rank-" + id, channel.GetProperty("id").GetString());
        Equal(cid, channel.GetProperty("pageCode").GetString());
        Check(card.GetProperty("available").GetBoolean(), "public rank is openable without an account");
        Check(!card.GetProperty("action").TryGetProperty("id", out _), "a rank must not masquerade as a public playlist");
        Equal("Synthetic preview", card.GetProperty("preview")[0].GetProperty("title").GetString());
        Equal("Synthetic singer", card.GetProperty("preview")[0].GetProperty("artist").GetString());
    }

    private static JsonElement[] Items(JsonElement page, string type) =>
        page.GetProperty("sections").EnumerateArray().Single(section => section.GetProperty("type").GetString() == type)
            .GetProperty("items").EnumerateArray().ToArray();

    private static void Terminal(JsonElement page)
    {
        Check(!page.GetProperty("more").GetBoolean(), "page must be terminal");
        Equal(JsonValueKind.Null, page.GetProperty("continuation").ValueKind);
    }

    private static async Task Error(string expected, Func<Task<JsonElement>> read)
    {
        try { await read(); }
        catch (MusicPlatformException error) { Equal(expected, error.Code); return; }
        throw new InvalidOperationException("Expected " + expected);
    }

    private static TestCase Case(string name, Func<Task> run) => new("KG discovery " + name, run);
    private static string Hash(int id) => id.ToString("X32", CultureInfo.InvariantCulture);
    private static JsonObject Object(object value) => JsonSerializer.SerializeToNode(value)!.AsObject();
    private static JsonObject Json(string value) => JsonNode.Parse(value)!.AsObject();
    private static int Number(JsonObject value, string field) => value[field]!.GetValue<int>();
    private static Exception Unexpected(Request request) => new InvalidOperationException("Unexpected discovery request " + request.Path);
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}");

    private sealed record Request(string Path, HttpMethod Method, Dictionary<string, string> Query,
        JsonObject Body, string Router, string Tid);

    private sealed class Source(bool allowWy) : IMusicPlatformSessionSource
    {
        private readonly List<string> platforms = [];
        public ValueTask<MusicPlatformSession?> AcquireAsync(string platform, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (platforms) platforms.Add(platform);
            return ValueTask.FromResult<MusicPlatformSession?>(null);
        }
        public bool IsCurrent(MusicPlatformSession session) => false;
        internal void Verify()
        {
            lock (platforms)
                Check(platforms.All(platform => platform == "kg" || allowWy && platform == "wy"),
                    "discovery acquired another platform session");
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(send(request));
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly MusicClientContextFixture clients = new();
        private readonly Source source;
        private readonly object gate = new();
        private readonly List<Request> requests = [];
        private readonly List<Exception> failures = [];
        internal IMusicPlatformCatalog Catalog { get; }

        internal Fixture(Func<Request, JsonObject> respond, bool allowWy = false)
        {
            source = new(allowWy);
            Catalog = MusicPlatformCatalog.CreateWithHandler(source, new Handler(message =>
            {
                try
                {
                    Equal("https", message.RequestUri!.Scheme);
                    Equal("gateway.kugou.com", message.RequestUri.Host);
                    Check(!message.Headers.Contains("Cookie") && !message.Headers.Contains("Authorization"),
                        "anonymous discovery must not send account credentials");
                    string path = message.RequestUri.AbsolutePath;
                    Check(path is Recommend or NewSongs or RankList or Tags or RankAudio,
                        "discovery must not resolve audio, search, match, or expand a playlist");
                    var query = message.RequestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                        .Select(part => part.Split('=', 2)).ToDictionary(part => Uri.UnescapeDataString(part[0]),
                            part => Uri.UnescapeDataString(part[1].Replace("+", " ", StringComparison.Ordinal)));
                    var body = message.Content is null ? new JsonObject()
                        : Json(message.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                    string Header(string name) => message.Headers.TryGetValues(name, out var values) ? values.Single() : "";
                    var request = new Request(path, message.Method, query, body, Header("x-router"), Header("kg-tid"));
                    lock (gate) requests.Add(request);
                    return new(HttpStatusCode.OK)
                        { Content = new StringContent(respond(request).ToJsonString(), Encoding.UTF8, "application/json") };
                }
                catch (Exception error)
                {
                    // Catalog maps exceptions to public errors. Keep fixture failures
                    // independently observable so error-path tests cannot hide them.
                    lock (gate) failures.Add(error);
                    throw;
                }
            }), clients.Owner);
        }
        internal Request[] Snapshot() { lock (gate) return requests.ToArray(); }
        internal void Requests(params string[] expected) =>
            Equal(string.Join(',', expected.Order(StringComparer.Ordinal)),
                string.Join(',', Snapshot().Select(request => request.Path).Order(StringComparer.Ordinal)));
        internal void PlaylistRequests(int pages)
        {
            var all = Snapshot();
            Equal(pages, all.Count(request => request.Path == Recommend));
            Check(all.Any(request => request.Path == Tags) && all.All(request => request.Path is Tags or Recommend),
                "playlist browsing reads only its tags and the requested recommendation pages");
        }
        public void Dispose()
        {
            try { Catalog.Dispose(); }
            finally { clients.Dispose(); }
            source.Verify();
            lock (gate)
                if (failures.Count > 0) throw new InvalidOperationException("Discovery HTTP fixture failed", failures[0]);
        }
    }
}
