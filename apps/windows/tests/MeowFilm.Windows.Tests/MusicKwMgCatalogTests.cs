using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Core.Runtime;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Scripting;
using MeowFilm.Capabilities.Search;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

// Synthetic wire + fixed LX recipe vectors, real native catalog/library/LX consumer.
// No reference project execution, user accounts, platform requests or media downloads.
public static class MusicKwMgCatalogTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("capabilities are public-only and do not acquire accounts", Capabilities);
        yield return Case("KW search retains precise recording quality and identity", KwSearch);
        yield return Case("KW search consumes the final duplicate field value", KwDuplicateFields);
        yield return Case("KW search artwork reaches song rows without detail requests", KwSearchArtwork);
        yield return Case("KW short search page against a larger total is terminal", () => ShortSearch("kw"));
        yield return Case("KW empty search page against a larger total is terminal", () => EmptySearch("kw"));
        yield return Case("KW single-quoted playlist card opens complete detail", KwQuotedPlaylist);
        yield return Case("KW detail has exact ID and no invented quality", KwDetail);
        yield return Case("KW specific missing-detail response uses literal Nuxt aliases", KwPage);
        foreach (string defect in new[] { "show", "quality", "code", "id", "cross-id" })
            yield return Case("KW rejects " + defect, () => KwFailure(defect));
        foreach (string defect in new[] { "call", "duplicate", "object-alias", "wrong-id", "missing-alias", "trailing-code" })
            yield return Case("KW SSR rejects " + defect, () => KwUnsafePage(defect));
        yield return Case("MG fixed anonymous signature vector", MgSignature);
        yield return Case("MG groups expand beyond limit without corrupting total cursor or rank", MgGroups);
        yield return Case("MG short grouped search page against a larger total is terminal", () => ShortSearch("mg"));
        yield return Case("MG empty grouped search page against a larger total is terminal", () => EmptySearch("mg"));
        yield return Case("MG expanded metadata reaches the search consumer without page-wide truncation", MgExpandedMetadata);
        yield return Case("MG grouped search preserves every returned recording", MgExpandedRecordings);
        foreach (string defect in new[] { "empty-group", "wrong-shape", "missing-id" })
            yield return Case("MG grouped search rejects " + defect, () => MgGroupFailure(defect));
        yield return Case("MG native songId is resourceId and copyrightId remains separate", MgDetail);
        foreach (string defect in new[] { "wrong-id", "two-resources", "business", "wrong-copyright" })
            yield return Case("MG detail rejects " + defect, () => MgDetailFailure(defect));
        yield return Case("MG playlist search card opens full exact detail", MgPlaylistCard);
        yield return Case("KW native 1000-row windows bridge arbitrary consumer page boundaries", KwWindow);
        yield return Case("MG native 50-row windows bridge arbitrary consumer page boundaries", MgWindow);
        foreach (string platform in new[] { "kw", "mg" })
        {
            yield return Case(platform + " search card reaches real native LX payload builder", () => SearchToLx(platform));
            yield return Case(platform + " uncached search refresh and explicit invalidation do not acquire accounts", () => Cache(platform));
            foreach (var kind in new[] { MusicSearchKind.Songs, MusicSearchKind.Playlists })
                yield return Case(platform + " " + kind + " native search continues beyond one hundred pages", () => HighPage(platform, kind));
            yield return Case(platform + " duplicate playlist recordings preserve unique occurrences", () => Duplicates(platform));
            yield return Case(platform + " large playlist continues by platform total", () => LargePlaylist(platform));
            yield return Case(platform + " declared repeated native page is rejected", () => RepeatedPage(platform));
            yield return Case(platform + " metadata snapshot changes reject exact old snapshot", () => Snapshot(platform));
            yield return Case(platform + " complete native owner double scan queue and LX payload", () => OwnerChain(platform));
            yield return Case(platform + " changed second-scan tail never creates partial queue", () => OwnerFailure(platform, "change"));
            yield return Case(platform + " short tail never creates partial queue", () => OwnerFailure(platform, "short"));
            yield return Case(platform + " caller cancellation physically drains before old result fails", () => PublicLifecycle(platform, "cancel"));
            yield return Case(platform + " invalidation physically drains before old result fails", () => PublicLifecycle(platform, "invalidate"));
            yield return Case(platform + " close physically drains without owning borrowed HTTP", () => PublicLifecycle(platform, "close"));
            yield return Case(platform + " pre-cancel invalid IDs and personal routes send nothing", () => Invalid(platform));
        }
    }
    // Explicit new lifecycle inventory; not an alias for the old platform matrix.
    public static IEnumerable<TestCase> DrainCases()
    {
        foreach (string reason in new[] { "cancel", "invalidate", "close", "timeout", "generation" })
            yield return Case("native acquisition physically drains on " + reason, () => AcquireDrain(reason));
    }
    private static TestCase Case(string name, Func<Task> action) => new("MusicKwMgCatalogTests." + name, action);
    private static void Check(bool condition, string message = "Assertion failed")
    { if (!condition) throw new InvalidOperationException(message); }
    private static JsonObject O(object value) => JsonSerializer.SerializeToNode(value)!.AsObject();
    private static JsonElement E(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonArray A(IEnumerable<JsonObject> items) => new(items.Select(x => (JsonNode)x.DeepClone()).ToArray());
    private static string S(JsonElement value, string field) => value.GetProperty(field).GetString()!;
    private static string NativeId(JsonElement song) => MusicSongFixture.NativeId(song);
    private static JsonElement SearchSong(string platform, JsonElement item) =>
        platform == "mg" ? item.GetProperty("song") : item;
    private static int N(JsonElement value, string field) => value.GetProperty(field).GetInt32();
    private static JsonElement Ok(JsonElement value)
    { Check(value.GetProperty("ok").GetBoolean(), value.GetRawText()); return value; }
    private static void Failed(JsonElement value, string code)
    { Check(!value.GetProperty("ok").GetBoolean() && S(value, "code") == code, value.GetRawText()); }
    private static async Task Error(string code, Func<Task> action)
    {
        try { await action(); }
        catch (MusicPlatformException e) { Check(e.Code == code, $"Expected {code}, got {e.Code}"); return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static HttpResponseMessage Json(object value) => Text(JsonSerializer.Serialize(value));
    private static HttpResponseMessage Text(string value) => new(HttpStatusCode.OK)
    { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(value)) };
    private static Dictionary<string, string> Query(HttpRequestMessage request) => request.RequestUri!.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
        .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]), StringComparer.Ordinal);
    private static JsonObject Kw(int i = 1) => O(new
    {
        id = i.ToString(), name = "Synthetic track " + i, artist = "A&amp;B", album = "Synthetic album", albumid = "2", duration = 180,
        N_MINFO = "level:p,bitrate:128,format:mp3,size:3.2Mb;level:s,bitrate:2000,format:flac,size:12Mb"
    });
    private static JsonObject Mg(int i = 1, string? copyright = null) => O(new
    {
        songId = "S" + i, copyrightId = copyright ?? "C" + i, songName = "Synthetic track " + i, album = "Synthetic album",
        singerList = new[] { new { name = "Synthetic artist" } }, albumId = "A1", duration = 180, contentId = "CONTENT" + i,
        audioFormats = new[] { new { formatType = "HQ", asize = 1048576, hash = "fixturehash", url = "https://media.invalid/private?token=DO_NOT_PERSIST" } }
    });
    private static HttpResponseMessage KwResults(params JsonObject[] rows) =>
        Json(new { TOTAL = rows.Length.ToString(), SHOW = "1", abslist = rows });
    private static HttpResponseMessage MgResults(IEnumerable<JsonObject[]> groups, int total) =>
        Json(new { code = "000000", songResultData = new { totalCount = total, resultList = groups } });
    private sealed class ForbiddenAccounts : IMusicPlatformSessionSource
    {
        internal int Reads, Checks;
        public ValueTask<MusicPlatformSession?> AcquireAsync(string p, CancellationToken t)
        { Reads++; throw new InvalidOperationException("Public catalogs must not acquire saved/anonymous accounts"); }
        public bool IsCurrent(MusicPlatformSession s)
        { Checks++; throw new InvalidOperationException("No public account lease"); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            Check(!request.Headers.Contains("Cookie") && !request.Headers.Contains("Authorization") &&
                !request.Headers.Contains("Proxy-Authorization"), "Credentials reached metadata handler");
            return send(request, token);
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly ForbiddenAccounts Accounts = new();
        internal readonly Handler Handler;
        internal readonly MusicPlatformHttp Http;
        internal readonly MusicPlatformCatalog Catalog;
        internal readonly MusicClientContextFixture Clients;
        internal Fixture(Func<HttpRequestMessage, HttpResponseMessage> handler)
            : this((r, _) => Task.FromResult(handler(r))) { }
        internal Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        { Handler = new(handler); Http = MusicPlatformHttp.CreateWithHandler(Handler); Clients = new(Http); Catalog = new(Accounts, Http, Clients.Owner); }
        public void Dispose()
        {
            Catalog.Dispose(); Clients.Dispose(); Http.Dispose();
            Check(Accounts.Reads == 0 && Accounts.Checks == 0, "Public catalog used accounts");
        }
    }
    private static async Task Capabilities()
    {
        using var f = new Fixture(_ => throw new InvalidOperationException("No request"));
        var platforms = f.Catalog.Describe().GetProperty("platforms");
        foreach (string p in new[] { "kw", "mg" })
        {
            var d = platforms.GetProperty(p);
            Check(d.GetProperty("song").GetBoolean() && d.GetProperty("playlist").GetBoolean());
            Check(!d.GetProperty("accounts").GetBoolean() && !d.GetProperty("home").GetBoolean() &&
                !d.GetProperty("personalPlaylists").GetBoolean());
            Check(!MusicPlatformHttp.IsAllowedOrigin(p, new Uri(p == "kw" ? "http://search.kuwo.cn/r.s" :
                "https://jadeite.migu.cn/music_search/v3/search/searchAll")));
            await Error("UNSUPPORTED", () => f.Catalog.HomeAsync(new(p)));
        }
        Check(f.Handler.Calls == 0);
    }
    private static async Task KwSearch()
    {
        using var f = new Fixture(r =>
        {
            var q = Query(r);
            Check(r.Method == HttpMethod.Get && r.RequestUri!.Scheme == "http" && r.RequestUri.Host == "search.kuwo.cn");
            Check(q["all"] == "合成 & ? keyword" && q["uid"] == "794762570" && q["pn"] == "0" && q["rn"] == "1");
            var raw = Kw(); raw.Remove("id"); raw["MUSICRID"] = "MUSIC_1";
            return KwResults(raw);
        });
        var data = await f.Catalog.SearchAsync(new("kw", " 合成 & ? keyword ", Limit: 1));
        var song = data.GetProperty("items")[0]; var native = MusicSongFixture.Info(song);
        Check(NativeId(song) == "1" && S(native, "songmid") == "1" && S(native, "source") == "kw");
        Check(S(song, "artist") == "A、B" && native.GetProperty("types").GetArrayLength() == 2);
        Check(S(native.GetProperty("_types").GetProperty("flac"), "size") == "12MB");
        Check(!song.TryGetProperty("id", out _) && !song.TryGetProperty("plugin_entry_path", out _) &&
            S(song, "songId").StartsWith("song:", StringComparison.Ordinal));
    }
    private static async Task KwDuplicateFields()
    {
        using var f = new Fixture(_ => Text("""{"TOTAL":"1","TOTAL":"0","abslist":[]}"""));
        var result = await f.Catalog.SearchAsync(new("kw", "x", Limit: 1));
        Check(N(result, "total") == 0 && result.GetProperty("items").GetArrayLength() == 0 &&
            !result.GetProperty("more").GetBoolean() && result.GetProperty("continuation").ValueKind == JsonValueKind.Null);
        Check(f.Handler.Calls == 1);
    }
    private static async Task KwSearchArtwork()
    {
        const string shortPath = "120/s4s12/76/3598052169.jpg";
        const string shortCover = "http://img1.kwcdn.kuwo.cn/star/albumcover/120/s4s12/76/3598052169.jpg";
        const string pic = "https://artwork.example/album.jpg";
        const string img = "http://img1.kwcdn.kuwo.cn/star/albumcover/240/s4s12/76/3598052169.jpg";
        (string Name, JsonObject Fields, string Cover)[] samples =
        [
            ("short only", O(new { web_albumpic_short = shortPath }), shortCover),
            ("second observed short path", O(new { web_albumpic_short = "120/s4s43/64/2138370659.jpg" }),
                "http://img1.kwcdn.kuwo.cn/star/albumcover/120/s4s43/64/2138370659.jpg"),
            ("other path size and format", O(new { web_albumpic_short = "512/cover_name-2.webp" }),
                "http://img1.kwcdn.kuwo.cn/star/albumcover/512/cover_name-2.webp"),
            ("pic wins", O(new { pic, img, web_albumpic_short = shortPath }), pic),
            ("img wins over short", O(new { img, web_albumpic_short = shortPath }), img),
            ("empty pic falls through", O(new { pic = "", img, web_albumpic_short = shortPath }), img),
            ("invalid pic falls through", O(new { pic = "not a URL", img, web_albumpic_short = shortPath }), img),
            ("query pic falls through", O(new { pic = pic + "?token=SYNTHETIC", img }), img),
            ("fragment pic falls through", O(new { pic = pic + "#cover", img }), img),
            ("local pic falls through", O(new { pic = "https://artwork.local./cover.jpg", img }), img),
            ("credential pic falls through", O(new { pic = "https://fixture@artwork.example/cover.jpg", img }), img),
            ("IP pic falls through", O(new { pic = "https://192.0.2.1/cover.jpg", img }), img),
            ("non-HTTP pic falls through", O(new { pic = "file:///cover.jpg", img }), img),
            ("unsupported pic port falls through", O(new { pic = "https://artwork.example:8443/album.jpg", img }), img),
            ("unsupported img port uses short", O(new { img = "http://artwork.example:8080/album.jpg",
                web_albumpic_short = shortPath }), shortCover),
            ("unsupported ports without fallback", O(new { pic = "https://artwork.example:8443/album.jpg",
                img = "http://artwork.example:8080/album.jpg" }), ""),
            ("empty candidates use short", O(new { pic = "", img = "", web_albumpic_short = shortPath }), shortCover),
            ("invalid candidates use short", O(new { pic = "invalid", img = img + "?token=SYNTHETIC",
                web_albumpic_short = shortPath }), shortCover),
            ("non-string candidates use short", O(new { pic = new { url = pic }, img = 12,
                web_albumpic_short = shortPath }), shortCover),
            ("valid pic ignores invalid short", O(new { pic, web_albumpic_short = "//other.example/cover.jpg" }), pic),
            ("absent artwork", new JsonObject(), ""),
            ("null artwork", O(new { pic = (string?)null, img = (string?)null, web_albumpic_short = (string?)null }), ""),
            ("empty artwork", O(new { pic = "", img = "", web_albumpic_short = "" }), ""),
            ("whitespace artwork", O(new { pic = " ", img = " ", web_albumpic_short = " " }), ""),
            ("invalid artwork", O(new { pic = "invalid", img = "file:///cover.jpg" }), ""),
            ("absolute short URL", O(new { web_albumpic_short = "https://other.example/cover.jpg" }), ""),
            ("authority short path", O(new { web_albumpic_short = "//other.example/cover.jpg" }), ""),
            ("rooted short path", O(new { web_albumpic_short = "/120/cover.jpg" }), ""),
            ("parent short path", O(new { web_albumpic_short = "../cover.jpg" }), ""),
            ("nested parent short path", O(new { web_albumpic_short = "120/../../cover.jpg" }), ""),
            ("encoded parent short path", O(new { web_albumpic_short = "%2e%2e/cover.jpg" }), ""),
            ("backslash short path", O(new { web_albumpic_short = @"120\..\cover.jpg" }), ""),
            ("encoded separator short path", O(new { web_albumpic_short = "120/%2f..%2fcover.jpg" }), ""),
            ("query short path", O(new { web_albumpic_short = shortPath + "?token=SYNTHETIC" }), ""),
            ("fragment short path", O(new { web_albumpic_short = shortPath + "#cover" }), ""),
            ("encoded query short path", O(new { web_albumpic_short = shortPath + "%3ftoken=SYNTHETIC" }), ""),
            ("control short path", O(new { web_albumpic_short = "120/\ncover.jpg" }), "")
        ];
        var rows = samples.Select((sample, index) =>
        {
            var raw = Kw(index + 1);
            foreach (var field in sample.Fields) raw[field.Key] = field.Value?.DeepClone();
            return raw;
        }).ToArray();
        using var f = new Fixture(r =>
        {
            Check(r.Method == HttpMethod.Get && r.RequestUri!.GetLeftPart(UriPartial.Path) == "http://search.kuwo.cn/r.s",
                "Artwork mapping issued a non-search request");
            var query = Query(r);
            Check(query["all"] == "m3" && query["pn"] == "1" && query["rn"] == rows.Length.ToString());
            return Json(new { TOTAL = (rows.Length * 3).ToString(), SHOW = "1", abslist = rows });
        });
        var result = await f.Catalog.SearchAsync(new("kw", "m3", Page: 2, Limit: rows.Length));
        Check(N(result, "page") == 2 && N(result, "limit") == rows.Length && N(result, "total") == rows.Length * 3 &&
            result.GetProperty("more").GetBoolean() && N(result.GetProperty("continuation"), "page") == 3);
        var page = MusicSearchPage.Read(result, "kw", MusicSearchKind.Songs, 2);
        Check(page.Items.Length == rows.Length && page.More && page.Next == 3 && page.Total == rows.Length * 3 &&
            page.TotalUnit == "items");
        for (int index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            var song = result.GetProperty("items")[index];
            var native = MusicSongFixture.Info(song);
            var id = (index + 1).ToString();
            Check(S(song, "cover_url") == sample.Cover, sample.Name + ": catalog cover");
            Check(sample.Cover.Length == 0 ? !native.TryGetProperty("img", out _) : S(native, "img") == sample.Cover,
                sample.Name + ": source cover");
            Check(NativeId(song) == id && S(native, "source") == "kw" &&
                S(song.GetProperty("sources")[0], "sourceKey") == $"[\"kw\",\"{id}\",\"\"]",
                sample.Name + ": recording identity");
            Check(native.GetProperty("types").EnumerateArray().Select(t => S(t, "type")).SequenceEqual(["flac", "128k"]) &&
                S(native.GetProperty("_types").GetProperty("flac"), "size") == "12MB",
                sample.Name + ": recording quality");
            var candidate = page.Items[index];
            var row = SongRow.Read(E(candidate.Item));
            Check(candidate.Rank == rows.Length + index + 1 && row.Id == S(song, "songId") &&
                row.Title == "Synthetic track " + id && row.Artist == "A、B" && row.Album == "Synthetic album" &&
                row.Cover == sample.Cover && row.Duration == "3:00", sample.Name + ": search consumer");
        }
        Check(f.Handler.Calls == 1, "Artwork mapping added requests to one search page");
    }
    private static async Task KwQuotedPlaylist()
    {
        using var f = new Fixture(r => r.RequestUri!.Host == "search.kuwo.cn"
            ? Text("{'TOTAL':'1','abslist':[{'playlistid':'42','name':'Artist's 合成','nickname':'Fixture','songnum':'2','intro':'只读'}]}")
            : PlaylistWire("kw", r, 2));
        var card = (await f.Catalog.SearchAsync(new("kw", "x", MusicSearchKind.Playlists, Limit: 1))).GetProperty("items")[0];
        Check(S(card, "name") == "Artist's 合成" && S(card, "source") == "kw" && N(card, "count") == 2);
        var detail = await f.Catalog.PlaylistAsync(new("kw", S(card, "id")));
        Check(N(detail, "total") == 2 && detail.GetProperty("items").GetArrayLength() == 2);
    }
    private static async Task KwDetail()
    {
        using var f = new Fixture(r =>
        {
            Check(r.RequestUri!.Host == "m.kuwo.cn" && Query(r)["musicId"] == "1");
            var raw = Kw(); raw.Remove("N_MINFO");
            return Json(new { status = 200, data = new { songinfo = raw } });
        });
        var song = await f.Catalog.SongAsync(new("kw", "1"));
        Check(NativeId(song) == "1" && MusicSongFixture.Info(song).GetProperty("types").GetArrayLength() == 0);
        Check(!MusicSongFixture.Info(song).GetProperty("qualityKnown").GetBoolean() && f.Handler.Calls == 1);
    }
    private const string SafePage = """<html><script>window.__NUXT__=(function(a,b){return {data:[{songinfo:{rid:a,musicrid:"MUSIC_1",name:"Synthetic",artist:"A",album:"B",duration:180,formats:b}}]}}("1","MP3128"));</script></html>""";
    private static async Task KwPage()
    {
        using var f = new Fixture(r => r.RequestUri!.Host == "m.kuwo.cn" ?
            Json(new { status = 301, msg = "音乐查询失败" }) : Text(SafePage));
        var song = await f.Catalog.SongAsync(new("kw", "1"));
        Check(NativeId(song) == "1" && f.Handler.Calls == 2 &&
            !song.TryGetProperty("detailSource", out _));
        Check(MusicSongFixture.Info(song).GetProperty("types").GetArrayLength() == 1);
    }
    private static async Task KwFailure(string defect)
    {
        using var f = new Fixture(_ =>
        {
            var raw = Kw();
            if (defect == "quality") raw.Remove("N_MINFO");
            if (defect == "cross-id") raw["musicrid"] = "MUSIC_2";
            if (defect == "show") return Json(new { TOTAL = "1", SHOW = "0", abslist = new[] { raw } });
            if (defect == "code") return Json(new { status = 301, msg = "Different failure" });
            if (defect == "id") { raw["id"] = "2"; return Json(new { status = 200, data = new { songinfo = raw } }); }
            return KwResults(raw);
        });
        var expected = defect switch { "code" => "UPSTREAM_REJECTED", "id" or "cross-id" => "IDENTITY_MISMATCH", _ => "INVALID_RESPONSE" };
        await Error(expected, () => defect is "code" or "id" ? f.Catalog.SongAsync(new("kw", "1")) :
            f.Catalog.SearchAsync(new("kw", "x", Limit: 1)));
        Check(f.Handler.Calls == 1);
    }
    private static async Task KwUnsafePage(string defect)
    {
        var page = defect switch
        {
            "call" => SafePage.Replace("duration:180", "duration:run()"),
            "duplicate" => SafePage.Replace("rid:a,", "rid:a,rid:a,"),
            "object-alias" => SafePage.Replace("\"1\",\"MP3128\"", "{\"id\":1},\"MP3128\""),
            "wrong-id" => SafePage.Replace("MUSIC_1", "MUSIC_2"),
            "missing-alias" => SafePage.Replace("rid:a,", "rid:c,"),
            _ => SafePage.Replace("duration:180", "duration:180;run()")
        };
        using var f = new Fixture(r => r.RequestUri!.Host == "m.kuwo.cn" ?
            Json(new { status = 301, msg = "音乐查询失败" }) : Text(page));
        await Error(defect == "wrong-id" ? "IDENTITY_MISMATCH" : "INVALID_RESPONSE", () => f.Catalog.SongAsync(new("kw", "1")));
        Check(f.Handler.Calls == 2);
    }
    private static Task MgSignature()
    {
        var method = typeof(MusicPlatformCatalog).GetMethod("MiguSearchSignature", BindingFlags.NonPublic | BindingFlags.Static)!;
        Check((string)method.Invoke(null, ["合成 keyword", "1792713600000"])! == "d3434441a5aad70c02aeb012010fca95");
        return Task.CompletedTask;
    }
    internal static JsonObject GroupFixture() => O(new
    {
        code = "000000",
        songResultData = new { totalCount = 3, resultList = new[] { new[] { Mg(1), Mg(2) }, new[] { Mg(3), Mg(4, "C1") } } }
    });
    private static async Task MgGroups()
    {
        using var f = new Fixture(r =>
        {
            var q = Query(r);
            var timestamp = r.Headers.GetValues("timestamp").Single();
            var signature = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(q["text"] +
                "6cdc72a439cef99a3418d2a78aa28c73yyapp2d16148780a1dcc7408e06336b98cfd50" +
                "963B7AA0D21511ED807EE5846EC87D20" + timestamp)));
            Check(r.RequestUri!.Scheme == "https" && r.Headers.GetValues("sign").Single() == signature &&
                q["isCorrect"] == "0" && q["pageSize"] == "2");
            return q["pageNo"] == "1" ? Json(GroupFixture()) : MgResults([new[] { Mg(5), Mg(6) }], 3);
        });
        var first = await f.Catalog.SearchAsync(new("mg", "合成 keyword", Limit: 2));
        Check(N(first, "total") == 3 && S(first, "totalUnit") == "groups" && N(first, "groupCount") == 2 && N(first, "rawItemCount") == 4);
        var rows = first.GetProperty("items");
        Check(rows.GetArrayLength() == 3 && N(rows[0], "rank") == 1 &&
            N(rows[1], "rank") == 1 && N(rows[2], "rank") == 2);
        Check(rows.EnumerateArray().All(row => row.EnumerateObject().Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal).SetEquals(["rank", "song"])));
        Check(N(first.GetProperty("continuation"), "page") == 2 && first.GetProperty("more").GetBoolean());
        var second = await f.Catalog.SearchAsync(new("mg", "合成 keyword", Page: 2, Limit: 2));
        Check(N(second, "groupCount") == 1 && N(second.GetProperty("items")[0], "rank") == 3 &&
            !second.GetProperty("more").GetBoolean());
        Check(!first.GetRawText().Contains("DO_NOT_PERSIST", StringComparison.Ordinal));
    }
    private static async Task MgExpandedMetadata()
    {
        var rows = Enumerable.Range(1, 30).Select(i =>
        {
            // Expanded native pages are not a single persisted song or queue command.
            JsonObject Large(int id)
            { var raw = Mg(id); raw["songName"] = new string('x', 4096); raw["album"] = new string('a', 4096); return raw; }
            return new[] { Large(i), Large(i + 1000) };
        }).ToArray();
        using var f = new Fixture(_ => MgResults(rows, 30));
        var result = await f.Catalog.SearchAsync(new("mg", "x", Limit: 30));
        Check(Encoding.UTF8.GetByteCount(result.GetRawText()) > 512 * 1024);
        Check(MusicSearchPage.Read(result, "mg", MusicSearchKind.Songs, 1).Items.Length == 60);
    }
    private static async Task MgExpandedRecordings()
    {
        var groups = Enumerable.Range(0, 30)
            .Select(g => Enumerable.Range(g * 40 + 1, 40).Select(i => Mg(i)).ToArray()).ToArray();
        using var f = new Fixture(_ => MgResults(groups, 30));
        var result = await f.Catalog.SearchAsync(new("mg", "x", Limit: 30));
        var page = MusicSearchPage.Read(result, "mg", MusicSearchKind.Songs, 1);
        Check(page.Items.Length == 1200 && page.RawItemCount == 1200 && page.GroupCount == 30 && !page.More);
    }
    private static async Task ShortSearch(string platform)
    {
        using var f = new Fixture(_ => platform == "kw"
            ? Json(new { TOTAL = "100", SHOW = "1", abslist = new[] { Kw() } })
            : MgResults([new[] { Mg() }], 100));
        var page = await f.Catalog.SearchAsync(new(platform, "x", Limit: 30));
        Check(page.GetProperty("items").GetArrayLength() == 1);
        Check(!page.GetProperty("more").GetBoolean());
        Check(page.GetProperty("continuation").ValueKind == JsonValueKind.Null);
        if (platform == "mg")
            Check(MusicSearchPage.Read(page, "mg", MusicSearchKind.Songs, 1) is { Items.Length: 1, More: false });
        Check(f.Handler.Calls == 1);
    }
    private static async Task EmptySearch(string platform)
    {
        using var f = new Fixture(_ => platform == "kw"
            ? Json(new { TOTAL = "100", SHOW = "1", abslist = Array.Empty<JsonObject>() })
            : MgResults([], 100));
        var page = await f.Catalog.SearchAsync(new(platform, "x", Limit: 30));
        Check(page.GetProperty("items").GetArrayLength() == 0);
        Check(!page.GetProperty("more").GetBoolean());
        Check(page.GetProperty("continuation").ValueKind == JsonValueKind.Null);
        if (platform == "mg")
            Check(MusicSearchPage.Read(page, "mg", MusicSearchKind.Songs, 1) is { Items.Length: 0, More: false });
        Check(f.Handler.Calls == 1);
    }
    private static async Task HighPage(string platform, MusicSearchKind kind)
    {
        bool songs = kind == MusicSearchKind.Songs;
        using var f = new Fixture(request =>
        {
            var query = Query(request);
            Check(query[platform == "kw" ? "pn" : "pageNo"] == (platform == "kw" ? "100" : "101"));
            if (platform == "kw")
                return Json(new { TOTAL = "102", SHOW = "1", abslist = new[] {
                    songs ? Kw() : O(new { playlistid = "42", name = "Synthetic list", songnum = 2 }) } });
            return songs ? MgResults([new[] { Mg() }], 102) :
                Json(new { code = "000000", songListResultData = new { totalCount = 102,
                    result = new[] { new { id = "42", name = "Synthetic list", musicNum = 2 } } } });
        });
        var page = await f.Catalog.SearchAsync(new(platform, "fixture", kind, Page: 101, Limit: 1));
        Check(N(page, "page") == 101 && page.GetProperty("items").GetArrayLength() == 1 &&
            page.GetProperty("more").GetBoolean() && N(page.GetProperty("continuation"), "page") == 102);
    }
    private static async Task MgGroupFailure(string defect)
    {
        const int limit = 1;
        using var f = new Fixture(_ =>
        {
            if (defect == "wrong-shape") return Json(new { code = "000000", songResultData = new { totalCount = 1, resultList = new[] { Mg() } } });
            if (defect == "empty-group") return MgResults([Array.Empty<JsonObject>()], 1);
            var raw = Mg(); raw.Remove("copyrightId"); return MgResults([new[] { raw }], 1);
        });
        string error = defect == "empty-group" ? "INCOMPLETE_PAGE" : "INVALID_RESPONSE";
        await Error(error, () => f.Catalog.SearchAsync(new("mg", "x", Limit: limit)));
        Check(f.Handler.Calls == 1);
    }
    private static async Task MgDetail()
    {
        using var f = new Fixture(async (r, _) =>
        {
            Check(r.Method == HttpMethod.Post && Query(r)["resourceType"] == "2");
            Check(await r.Content!.ReadAsStringAsync() == "resourceId=S1");
            return Json(new { returnCode = "000000", resource = new[] { Mg() } });
        });
        var song = await f.Catalog.SongAsync(new("mg", "S1"));
        var native = MusicSongFixture.Info(song);
        Check(NativeId(song) == "S1" && S(native, "songId") == "S1" &&
            S(native, "copyrightId") == "C1" && !song.TryGetProperty("id", out _));
        Check(S(native, "contentId") == "CONTENT1" && native.GetProperty("audioFormats")[0].GetProperty("asize").GetInt32() == 1048576);
        Check(!song.GetRawText().Contains("DO_NOT_PERSIST", StringComparison.Ordinal));
    }
    private static async Task MgDetailFailure(string defect)
    {
        using var f = new Fixture(_ =>
        {
            var raw = Mg();
            if (defect == "wrong-id") raw["songId"] = "C1";
            if (defect == "wrong-copyright") raw["copyrightId"] = "../credential";
            return Json(new { code = defect == "business" ? "DENIED" : "000000", resource = defect == "two-resources" ? new[] { raw, Mg(2) } : new[] { raw } });
        });
        await Error(defect == "business" ? "UPSTREAM_REJECTED" : defect == "wrong-copyright" || defect == "two-resources" ?
            "INVALID_RESPONSE" : "IDENTITY_MISMATCH", () => f.Catalog.SongAsync(new("mg", "S1")));
        Check(f.Handler.Calls == 1);
    }
    private static async Task MgPlaylistCard()
    {
        using var f = new Fixture(r =>
        {
            if (r.RequestUri!.Host != "jadeite.migu.cn") return PlaylistWire("mg", r, 2);
            Check(Query(r)["isCorrect"] == "1");
            return Json(new { code = "000000", data = new { songListResultData = new { totalCount = 1,
                result = new[] { new { id = "42", name = "Synthetic playlist", musicNum = 2, userName = "Fixture" } } } } });
        });
        var card = (await f.Catalog.SearchAsync(new("mg", "x", MusicSearchKind.Playlists, Limit: 1))).GetProperty("items")[0];
        var detail = await f.Catalog.PlaylistAsync(new("mg", S(card, "id")));
        Check(N(detail, "total") == 2 && detail.GetProperty("items").GetArrayLength() == 2 && f.Handler.Calls == 3);
    }
    private static HttpResponseMessage PlaylistWire(string platform, HttpRequestMessage request, int total,
        bool duplicates = false, bool changeTail = false, bool shortTail = false, string name = "Synthetic playlist", bool wrongPage = false)
    {
        var q = Query(request);
        if (platform == "mg" && request.RequestUri!.Host == "c.musicapp.migu.cn")
            return Json(new { code = "000000", data = new { musicListId = "42", title = name, musicNum = total,
                ownerId = "PUBLIC_FIXTURE", ownerName = "Fixture", imgItem = new { img = "" } } });
        int batchSize = platform == "kw" ? 1000 : 50;
        int page = int.Parse(q[platform == "kw" ? "pn" : "pageNo"]) - (platform == "mg" ? 1 : 0);
        int length = Math.Max(0, Math.Min(batchSize, total - page * batchSize));
        var rows = Enumerable.Range(page * batchSize, length).Select(i =>
        {
            int number = changeTail && i == total - 1 ? 9999 : duplicates && i == 101 ? 101 : i + 1;
            return platform == "kw" ? Kw(number) : Mg(number);
        }).ToList();
        if (shortTail && page * batchSize + length >= total && rows.Count > 0) rows.RemoveAt(rows.Count - 1);
        return platform == "kw"
            ? Json(new { result = "ok", id = "42", title = name, total, uid = "PUBLIC_FIXTURE", uname = "Fixture",
                pn = wrongPage ? page + 1 : page, rn = 1000, musiclist = rows })
            : Json(new { code = "000000", data = new { totalCount = total, publishTime = "synthetic-v1", pageNo = wrongPage ? page + 2 : page + 1,
                pageSize = 50, songList = rows } });
    }
    private static async Task KwWindow()
    {
        var batches = new List<string>();
        using var f = new Fixture(r => { batches.Add(Query(r)["pn"]); return PlaylistWire("kw", r, 1100); });
        var page = await f.Catalog.PlaylistAsync(new("kw", "42", Page: 14, Limit: 73));
        Check(page.GetProperty("items").GetArrayLength() == 73 &&
            NativeId(page.GetProperty("items")[0]) == "950" &&
            NativeId(page.GetProperty("items")[72]) == "1022" &&
            batches.SequenceEqual(["0", "1"]));
    }
    private static async Task MgWindow()
    {
        var batches = new List<string>();
        using var f = new Fixture(r =>
        {
            if (Query(r).TryGetValue("pageNo", out var page)) batches.Add(page);
            return PlaylistWire("mg", r, 235);
        });
        var value = await f.Catalog.PlaylistAsync(new("mg", "42", Page: 2, Limit: 99));
        Check(value.GetProperty("items").GetArrayLength() == 99 &&
            NativeId(value.GetProperty("items")[0]) == "S100" &&
            NativeId(value.GetProperty("items")[98]) == "S198" &&
            batches.SequenceEqual(["2", "3", "4"]) && f.Handler.Calls == 4);
    }
    private static async Task Cache(string platform)
    {
        int number = 1;
        using var f = new Fixture(_ => platform == "kw" ? KwResults(Kw(number)) : MgResults([new[] { Mg(number) }], 1));
        _ = await f.Catalog.SearchAsync(new(platform, "x", Limit: 1));
        // Search results are cached by the search owner only; every catalog call reaches the platform.
        number = 2; var again = await f.Catalog.SearchAsync(new(platform, "x", Limit: 1));
        Check(NativeId(SearchSong(platform, again.GetProperty("items")[0])) == (platform == "kw" ? "2" : "S2") &&
            f.Handler.Calls == 2);
        _ = await f.Catalog.SearchAsync(new(platform, "x", Limit: 1, Refresh: true)); Check(f.Handler.Calls == 3);
        f.Catalog.Invalidate(platform);
        await f.Catalog.SearchAsync(new(platform, "x", Limit: 1)); Check(f.Handler.Calls == 4);
    }
    private static async Task Duplicates(string platform)
    {
        using var f = new Fixture(r => PlaylistWire(platform, r, 102, duplicates: true));
        var page = await f.Catalog.PlaylistAsync(new(platform, "42", Page: 2));
        var rows = page.GetProperty("items");
        Check(rows.GetArrayLength() == 2 && NativeId(rows[0]) == NativeId(rows[1]));
        Check(page.GetProperty("memberIds")[0].GetString() != page.GetProperty("memberIds")[1].GetString());
    }
    private static async Task LargePlaylist(string platform)
    {
        using var f = new Fixture(r => PlaylistWire(platform, r, 10001));
        var page = await f.Catalog.PlaylistAsync(new(platform, "42"));
        Check(page.GetProperty("items").GetArrayLength() == 100 && page.GetProperty("more").GetBoolean());
    }
    private static async Task RepeatedPage(string platform)
    {
        using var f = new Fixture(r => PlaylistWire(platform, r, 235, wrongPage: true));
        await Error("PAGINATION_STALLED", () => f.Catalog.PlaylistAsync(new(platform, "42")));
    }
    private static async Task Snapshot(string platform)
    {
        string name = "Synthetic playlist";
        using var f = new Fixture(r => PlaylistWire(platform, r, 235, name: name));
        var page = await f.Catalog.PlaylistAsync(new(platform, "42"));
        name = "Changed";
        await Error("SNAPSHOT_CHANGED", () => f.Catalog.PlaylistAsync(new(platform, "42", Page: 2, Snapshot: S(page, "snapshot"))));
    }
    private static async Task PublicLifecycle(string platform, string reason)
    {
        var entered = Signal(); var cancelled = Signal(); var release = Signal();
        using var f = new Fixture(async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult(); await release.Task;
            return platform == "kw" ? KwResults(Kw()) : MgResults([new[] { Mg() }], 1);
        });
        using var cancel = new CancellationTokenSource();
        var pending = f.Catalog.SearchAsync(new(platform, "x", Limit: 1), cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (reason == "cancel") cancel.Cancel();
        else if (reason == "close") f.Catalog.Dispose();
        else f.Catalog.Invalidate(platform);
        try
        {
            if (reason == "cancel")
            {
                await Error("CANCELLED", () => pending.WaitAsync(TimeSpan.FromSeconds(3)));
                Check(!cancelled.Task.IsCompleted && !f.Catalog.DrainAsync().IsCompleted);
            }
            else
            {
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3)); Check(!pending.IsCompleted);
            }
        }
        finally { release.TrySetResult(); }
        await Error(reason == "cancel" ? "CANCELLED" : reason == "close" ? "CLOSED" : "STALE_SESSION", () => pending);
        await f.Catalog.DrainAsync();
        Check(f.Handler.Calls == 1);
        using var other = new MusicPlatformCatalog(f.Accounts, f.Http, f.Clients.Owner);
        _ = await other.SearchAsync(new(platform, "x", Limit: 1)); Check(f.Handler.Calls == 2);
    }
    private static async Task Invalid(string platform)
    {
        using var f = new Fixture(_ => throw new InvalidOperationException("No requests"));
        using var c = new CancellationTokenSource(); c.Cancel();
        await Error("CANCELLED", () => f.Catalog.SearchAsync(new(platform, "x"), c.Token));
        await Error("INVALID_REQUEST", () => f.Catalog.SongAsync(new(platform, "https://other.invalid/id")));
        await Error("INVALID_REQUEST", () => f.Catalog.PlaylistAsync(new(platform, "../42")));
        await Error("UNSUPPORTED", () => f.Catalog.PlaylistAsync(new(platform, "42",
            Personal: new(new string('a', 64), MusicPersonalPlaylistKind.QqFavorites, "201"))));
        Check(f.Handler.Calls == 0);
    }
    private sealed class BlockedAccounts : IMusicPlatformSessionSource
    {
        internal readonly TaskCompletionSource Entered = Signal(), Cancelled = Signal(), Release = Signal();
        internal MusicPlatformSession? Result;
        public async ValueTask<MusicPlatformSession?> AcquireAsync(string platform, CancellationToken token)
        {
            using var registration = token.Register(() => Cancelled.TrySetResult());
            Entered.TrySetResult(); await Release.Task; return Result;
        }
        public bool IsCurrent(MusicPlatformSession session) => false;
    }
    private static async Task AcquireDrain(string reason)
    {
        var accounts = new BlockedAccounts();
        if (reason == "generation") accounts.Result = new("wy", "synthetic-scope", 1, "123", "MUSIC_U=SYNTHETIC_ONLY");
        var handler = new Handler((_, _) => throw new InvalidOperationException("Acquire never authorized HTTP"));
        using var http = MusicPlatformHttp.CreateWithHandler(handler);
        await using var clients = new MusicClientContextFixture(http);
        using var catalog = new MusicPlatformCatalog(accounts, http, clients.Owner, reason == "timeout" ? TimeSpan.FromMilliseconds(30) : null);
        using var cancel = new CancellationTokenSource();
        var pending = catalog.SearchAsync(new("wy", "fixture"), cancel.Token);
        await accounts.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (reason == "cancel") cancel.Cancel();
        else if (reason == "invalidate") catalog.Invalidate("wy");
        else if (reason == "close") catalog.Dispose();
        try
        {
            if (reason == "cancel")
            {
                await Error("CANCELLED", () => pending.WaitAsync(TimeSpan.FromSeconds(2)));
                Check(!accounts.Cancelled.Task.IsCompleted && !catalog.DrainAsync().IsCompleted);
                catalog.Invalidate(); // Service retirement, not the departed subscriber, owns cancellation.
            }
            else
            {
                if (reason != "generation") await accounts.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Check(!pending.IsCompleted && handler.Calls == 0);
            }
        }
        finally { accounts.Release.TrySetResult(); }
        await Error(reason switch { "cancel" => "CANCELLED", "close" => "CLOSED", "invalidate" or "generation" => "STALE_SESSION", _ => "TIMEOUT" }, () => pending);
        await catalog.DrainAsync(); Check(handler.Calls == 0);
    }
    private sealed class Store : IDocumentStore
    {
        internal byte[]? Bytes;
        internal int Writes;
        internal bool Disposed;
        public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default)
        {
            Check(ns == MusicLibraryOwner.DocumentNamespace); token.ThrowIfCancellationRequested();
            return Task.FromResult(new DocumentSnapshot(Bytes?.ToArray(), Bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Bytes)));
        }
        public Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> data, DocumentFingerprint expected, CancellationToken token = default)
        {
            Check(ns == MusicLibraryOwner.DocumentNamespace);
            var current = Bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Bytes);
            Check(current == expected); Bytes = data.ToArray(); Writes++;
            return Task.FromResult(DocumentFingerprint.FromBytes(Bytes));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private static async Task OwnerChain(string platform)
    {
        int metadata = 0, wire = 0;
        using var f = new Fixture(r =>
        {
            wire++;
            if (r.RequestUri!.Host == "c.musicapp.migu.cn") metadata++;
            return PlaylistWire(platform, r, 235, duplicates: true);
        });
        var store = new Store();
        await using var library = new MusicLibraryOwner(store);
        await using var owner = new MusicNativeCatalogOwner(f.Catalog, library);
        var first = Ok(await owner.ExecuteAsync("music.catalog.playlist", new { platform, id = "42", page = 2, limit = 100 }));
        string selectedMember = first.GetProperty("memberIds")[1].GetString()!;
        var stage = Ok(await owner.ExecuteAsync("music.catalog.playlist.prepare", new { platform, id = "42", refresh = true }));
        Check(N(stage, "count") == 235 && store.Writes == 0);
        if (platform == "kw") Check(wire == 7); else Check(metadata == 7 && wire == 19);
        var committed = Ok(await owner.ExecuteAsync("music.catalog.playlist.commit", new
        { token = S(stage, "token"), action = "play", memberId = selectedMember, expectedVersion = 0 }));
        Check(N(committed, "version") == 1 && store.Writes == 1);
        var queue = Ok(await library.ExecuteAsync("music.queue.get", new { offset = 100, limit = 100 }));
        var song = queue.GetProperty("current").GetProperty("song");
        Check(N(queue, "total") == 235 && NativeId(song) == (platform == "kw" ? "101" : "S101") &&
            S(queue.GetProperty("source"), "platform") == platform);
        await using (var cold = new MusicLibraryOwner(store))
        {
            var read = Ok(await cold.ExecuteAsync("music.queue.get", new { limit = 1 }));
            Check(N(read, "total") == 235 &&
                NativeId(read.GetProperty("current").GetProperty("song")) == NativeId(song));
        }
        Check(!store.Disposed);
        await ResolveViaScript(library, platform, song);
    }
    private static async Task SearchToLx(string platform)
    {
        using var f = new Fixture(_ => platform == "kw" ? KwResults(Kw()) : MgResults([new[] { Mg() }], 1));
        var search = await f.Catalog.SearchAsync(new(platform, "synthetic", Limit: 1));
        var store = new Store();
        await using var library = new MusicLibraryOwner(store);
        Check(store.Writes == 0);
        await ResolveViaScript(library, platform, SearchSong(platform, search.GetProperty("items")[0]));
    }
    private static async Task ResolveViaScript(MusicLibraryOwner library, string platform, JsonElement song)
    {
        // Real native LX selector/payload builder, injected synthetic script port only.
        var runtime = new Runtime(platform);
        await using var scripts = new MusicScriptOwner(library, runtime);
        var bytes = Encoding.UTF8.GetBytes("// synthetic source: never evaluated");
        var installed = Ok(await scripts.ImportSourceAsync(bytes, "fixture.js"));
        var installationId = S(installed.GetProperty("item"), "installationId");
        Ok(await scripts.ExecuteAsync("music.sources.enable", new { installationId }));
        if (platform == "kw")
        {
            Failed(await scripts.ExecuteAsync("music.online.resolve", new { installationId, platform, song, quality = "320k" }),
                "RECORDING_QUALITY_UNAVAILABLE");
            Check(runtime.Payload is null, "Known unavailable KW quality must not invoke LX");
        }
        string quality = platform == "kw" ? "128k" : "320k";
        var resolution = Ok(await scripts.ExecuteAsync("music.online.resolve", new { installationId, platform, song, quality }));
        Check(S(resolution, "installationId") == installationId && runtime.Payload is { });
        Check(S(resolution, "quality") == quality && S(runtime.Payload!.Value, "quality") == quality);
        var info = runtime.Payload!.Value.GetProperty("musicInfo");
        Check(S(info, "source") == platform && S(info, "songmid") == NativeId(song));
        if (platform == "mg") Check(S(info, "copyrightId") == S(MusicSongFixture.Info(song), "copyrightId") &&
            S(info, "contentId") == S(MusicSongFixture.Info(song), "contentId") &&
            info.GetProperty("audioFormats")[0].GetProperty("asize").GetInt32() == 1048576);
        Check(!info.GetRawText().Contains("DO_NOT_PERSIST", StringComparison.Ordinal));
    }
    private static async Task OwnerFailure(string platform, string defect)
    {
        int pageCalls = 0;
        using var f = new Fixture(r =>
        {
            if (platform == "kw" || r.RequestUri!.Host == "c.musicapp.migu.cn") pageCalls++;
            return PlaylistWire(platform, r, 235, changeTail: defect == "change" && pageCalls > 3, shortTail: defect == "short");
        });
        var store = new Store();
        await using var library = new MusicLibraryOwner(store);
        await using var owner = new MusicNativeCatalogOwner(f.Catalog, library);
        var result = await owner.ExecuteAsync("music.catalog.playlist.prepare", new { platform, id = "42", refresh = true });
        Failed(result, defect == "change" ? "SNAPSHOT_CHANGED" : "INCOMPLETE_PAGE");
        var queue = Ok(await library.ExecuteAsync("music.queue.get", new { limit = 1 }));
        Check(store.Writes == 0 && N(queue, "total") == 0);
    }
    private sealed class Runtime(string platform) : IRuntimeClient
    {
        internal JsonElement? Payload;
        private static JsonElement Reply(object value)
        => E(new { ok = true, contract = "music.script.v3", value });
        private readonly HashSet<(string Id, long Revision)> candidates = [];
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(module == "music" && !source.IsEmpty && Guid.TryParseExact(installationId, "D", out _) && sourceRevision > 0);
            candidates.Add((installationId, sourceRevision));
            return Task.FromResult(Reply(new { installationId, sourceRevision }));
        }
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var p = E(parameters ?? new { });
            JsonElement result;
            switch (method)
            {
                case "music.script.sources.add":
                    Check(candidates.Remove((S(p, "installationId"), p.GetProperty("sourceRevision").GetInt64())));
                    result = Reply(new { item = new { installationId = S(p, "installationId") } }); break;
                case "music.script.sources.enable": result = Reply(new { item = new { installationId = S(p, "installationId"), enabled = true } }); break;
                case "music.script.resolve":
                    Check(S(p, "platform") == platform);
                    Payload = p.Clone(); result = Reply(new { installationId = S(p, "installationId"), platform,
                        quality = S(p, "quality"), generation = 1, contentHash = new string('a', 64),
                        url = "https://synthetic.invalid/audio.mp3", headers = new { } }); break;
                default: throw new InvalidOperationException("Non-script business reached runtime: " + method);
            }
            return Task.FromResult(result);
        }
    }
}
