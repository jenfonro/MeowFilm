// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus b900c259 static/netease-client.js discovery; api-enhanced MIT 19c7dad6
// module/{search_hot_detail,search_default,toplist,playlist_track_all}.js. Existing notices/licenses retained.
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Search;

namespace MeowFilm.Capabilities.Platforms;

internal sealed class MusicPlatformSearchDiscovery(MusicPlatformHttp http)
{
    internal sealed record Data(MusicSearchDiscoveryHot[] Hot, string[] Suggestions,
        MusicSearchDiscoveryChart[] Charts, MusicSearchDiscoveryFailure[] Failures);
    private sealed record Branch<T>(T? Value, MusicSearchDiscoveryFailure? Failure);
    internal async Task<Data> ReadAsync(MusicPlatformSession? session, Action guard, CancellationToken token)
    {
        using var slots = new SemaphoreSlim(2); // Preserve the shared native WY physical two-slot limit.
        var requests = 0;
        async Task<JsonObject> Read(string path, JsonObject parameters, bool weapi = false)
        {
            await slots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                guard(); token.ThrowIfCancellationRequested();
                PJ.Require(Interlocked.Increment(ref requests) <= 9, "LIMIT");
                var request = MusicPlatformRequests.WyWithCookie(path, parameters, session?.Cookie, weapi);
                var response = await http.SendCatalogAsync("wy", request, token, 2 * 1024 * 1024, TimeSpan.FromSeconds(25)).ConfigureAwait(false);
                guard(); token.ThrowIfCancellationRequested();
                return PlatformProtocols.WySuccess(response.Body);
            }
            finally { slots.Release(); }
        }
        async Task<Branch<T>> Capture<T>(string stage, string? id, Func<Task<T>> action)
        {
            try { return new(await action().ConfigureAwait(false), null); }
            catch (MusicPlatformException error) when (error.Code is not ("CANCELLED" or "TIMEOUT" or "STALE_SESSION" or "CLOSED" or "CREDENTIALS_UNAVAILABLE"))
            { guard(); return new(default, new(stage, id, SafeCode(error.Code))); }
            catch (OperationCanceledException) { throw; }
            catch (MusicPlatformException) { throw; }
            catch { guard(); return new(default, new(stage, id, "INVALID_RESPONSE")); }
        }
        var hot = Capture("hot", null, async () =>
        {
            var raw = await Read("/api/hotsearchlist/get", new(), true);
            return PJ.Rows(raw["data"], 10000).Take(20).Select(row => new MusicSearchDiscoveryHot(Keyword(row["searchWord"]),
                row["score"] is null ? null : PJ.Number(row["score"]))).ToArray();
        });
        var top = Capture("toplist", null, async () =>
        {
            var raw = await Read("/api/toplist", new());
            var charts = PJ.Rows(raw["list"], 10000).Take(3).Select(row => PlatformSongs.Playlist("wy", row)).ToArray();
            PJ.Require(charts.Select(c => PJ.S(c, "id")).Distinct(StringComparer.Ordinal).Count() == charts.Length, "IDENTITY_MISMATCH");
            return charts;
        });
        var suggested = Capture("default", null, async () =>
        {
            var raw = await Read("/api/search/defaultkeyword/get", new());
            PJ.Require(raw["data"] is JsonObject, "INVALID_RESPONSE");
            return Keyword(PJ.At(raw, "data", "realkeyword"), allowEmpty: true);
        });
        await Task.WhenAll(hot, top, suggested).ConfigureAwait(false); guard();
        var h = await hot; var t = await top; var s = await suggested;
        // Fixed original behavior: a default keyword alone cannot mask loss of both primary sections.
        if (h.Failure is not null && t.Failure is not null) throw new MusicPlatformException(h.Failure.Code, "wy", "discovery");
        var chartTasks = (t.Value ?? []).Select(chart => Capture("chart", PJ.S(chart, "id"), async () =>
        {
            var id = PJ.Id(chart["id"], "^[1-9][0-9]{0,19}$");
            var raw = await Read("/api/v6/playlist/detail", new() { ["id"] = id, ["n"] = 100000, ["s"] = 8 });
            var playlist = raw["playlist"] as JsonObject ?? throw new MusicPlatformException("INVALID_RESPONSE");
            PJ.Require(PJ.Id(playlist["id"]) == id, "IDENTITY_MISMATCH");
            // /playlist/track/all slices the first twenty IDs locally, then requests exact song detail.
            var ids = PJ.Rows(playlist["trackIds"], 10000).Take(20).Select(row => PJ.Id(row["id"], "^[1-9][0-9]{0,19}$")).ToArray();
            PJ.Require(ids.Distinct(StringComparer.Ordinal).Count() == ids.Length, "IDENTITY_MISMATCH");
            var songs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (ids.Length > 0)
            {
                var detail = await Read("/api/v3/song/detail", new() { ["c"] = "[" + string.Join(",", ids.Select(i => "{\"id\":" + i + "}")) + "]" });
                foreach (var row in PJ.Rows(detail["songs"], 20))
                {
                    var song = PlatformSongs.Wy(row);
                    PJ.Require(songs.TryAdd(NativeMusicSong.NativeId(song), PJ.Element(song)), "IDENTITY_MISMATCH");
                }
                PJ.Require(songs.Count == ids.Length && ids.All(songs.ContainsKey), "IDENTITY_MISMATCH");
            }
            var cover = PJ.S(chart, "cover");
            if (cover.Length > 0 && Uri.TryCreate(cover, UriKind.Absolute, out var uri) && (uri.Query.Length > 0 || uri.Fragment.Length > 0)) cover = "";
            return new MusicSearchDiscoveryChart(id, PJ.S(chart, "name"), cover, PJ.OptionalCount(chart["count"]),
                PJ.S(chart, "creator"), ids.Select(i => songs[i]).ToArray());
        })).ToArray();
        var charts = await Task.WhenAll(chartTasks).ConfigureAwait(false); guard(); token.ThrowIfCancellationRequested();
        var hotItems = h.Value ?? [];
        var keywords = new[] { s.Value ?? "" }.Concat(hotItems.Select(i => i.Keyword)).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var failures = new[] { h.Failure, t.Failure, s.Failure }.Concat(charts.Select(c => c.Failure)).OfType<MusicSearchDiscoveryFailure>().ToArray();
        return new(hotItems, keywords, charts.Where(c => c.Failure is null).Select(c => c.Value!).ToArray(), failures);
    }
    private static string Keyword(JsonNode? node, bool allowEmpty = false)
    {
        if (node is null && allowEmpty) return "";
        PJ.Require(node is JsonValue v && v.TryGetValue<string>(out _), "INVALID_RESPONSE");
        var value = PJ.Text(node);
        PJ.Require((allowEmpty || value.Length > 0) && value.Length <= 200 && !value.Any(char.IsControl), "INVALID_RESPONSE"); return value;
    }
    internal static string SafeCode(string code) => code switch
    {
        "CANCELLED" or "TIMEOUT" or "STALE_SESSION" or "CLOSED" or "CREDENTIALS_UNAVAILABLE" => code,
        "AUTH_REQUIRED" or "FORBIDDEN" or "RATE_LIMITED" or "VERIFICATION_REQUIRED" or "INVALID_RESPONSE" or
        "IDENTITY_MISMATCH" or "LIMIT" or "REDIRECT_DENIED" or "ORIGIN_DENIED" or "NETWORK_ERROR" => code,
        _ => "UPSTREAM_ERROR"
    };
}
