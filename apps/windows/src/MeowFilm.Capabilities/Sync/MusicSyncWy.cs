// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus b900c259 src/netease.ts:988–1130; api-enhanced 19c7dad6 module playlist_* / like / song_order_update (MIT).
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
namespace MeowFilm.Capabilities.Sync;

internal sealed class MusicSyncWy(MusicPlatformSession session, MusicPlatformHttp http,
    MusicPlatformClientContextOwner clients, MusicPlatformClientContext? client,
    Func<Task> guard, Action accountGuard, CancellationToken token)
    : MusicSyncAdapter(session, http, clients, client, guard, accountGuard, token)
{
    private async Task<List<JsonObject>> Directory()
    {
        var all = new List<JsonObject>(); var seen = new HashSet<string>();
        for (long offset = 0; ; offset = checked(offset + 100))
        {
            var data = await Wy("/api/user/playlist", O(("uid", AccountId), ("limit", 100), ("offset", offset), ("includeVideo", true)));
            var rows = Rows(data["playlist"], 100); Require(LJ.IsBool(data["more"])); bool more = LJ.B(data["more"]);
            foreach (var row in rows) { Require(seen.Add(Id(row["id"])), "PAGINATION_STALLED"); _ = Id(row["creator"]?["userId"]); _ = Name(row["name"]); all.Add(row); }
            Require(!more || rows.Count > 0, "PAGINATION_STALLED");
            if (!more) { Require(all.Count(r => Favorite(r)) == 1, "FAVORITES_UNCONFIRMED"); return all; }
        }
    }
    private bool Favorite(JsonObject entry) => Id(entry["creator"]?["userId"]) == AccountId && PJ.Scalar(entry["specialType"]) == "5";
    private static string Signature(IEnumerable<JsonObject> rows) => LJ.Digest(LJ.Arr(rows.Select(r => O(("id", Id(r["id"])),
        ("owner", Id(r["creator"]?["userId"])), ("name", r["name"]), ("specialType", r["specialType"])))));
    private static string[] TrackIds(JsonObject data, string id, string owner)
    {
        Require(Id(data["id"]) == id && Id(data["creator"]?["userId"]) == owner, "IDENTITY_MISMATCH");
        var ids = Rows(data["trackIds"]).Select(t => Id(t["id"])).ToArray();
        long count = Count(data["trackCount"]), cloud = data["cloudTrackCount"] is null ? 0 : Count(data["cloudTrackCount"]);
        Require(ids.Length == count || ids.Length == count + cloud, "INCOMPLETE_PLAYLIST"); return ids;
    }
    private async Task<SyncRemote> ReadEntry(JsonObject entry)
    {
        string id = Id(entry["id"]), owner = Id(entry["creator"]?["userId"]);
        async Task<JsonObject> Detail() => LJ.Object((await Wy("/api/v6/playlist/detail", O(("id", id), ("n", 100000), ("s", 8)), false))["playlist"], "INVALID_RESPONSE");
        var first = await Detail(); string[] ids = TrackIds(first, id, owner);
        var songs = new Dictionary<string, JsonObject>();
        foreach (var raw in Rows(first["tracks"] ?? new JsonArray()))
        {
            string sid = Id(raw["id"]);
            // Repeated trackIds retain their occurrences below. An identical repeated detail is not
            // an incomplete roster, but conflicting metadata for that same native ID is not guessed.
            Require(!songs.TryGetValue(sid, out var previous) || JsonNode.DeepEquals(previous, raw), "IDENTITY_MISMATCH");
            songs[sid] = raw;
        }
        var missing = ids.Where(i => !songs.ContainsKey(i)).Distinct().ToArray();
        foreach (var chunk in missing.Chunk(100))
        {
            try
            {
                var data = await Wy("/api/v3/song/detail", O(("c", "[" + string.Join(",", chunk.Select(i => "{\"id\":" + i + "}")) + "]")));
                foreach (var raw in Rows(data["songs"], 100)) { string sid = Id(raw["id"]); Require(chunk.Contains(sid), "IDENTITY_MISMATCH"); songs[sid] = raw; }
            }
            catch (MusicPlatformException e) when (e.Code is "UPSTREAM_ERROR" or "NETWORK_ERROR" or "TIMEOUT") { await Guard(); }
        }
        var second = await Detail(); Same(ids, TrackIds(second, id, owner));
        Require(PJ.Scalar(first["specialType"]) == PJ.Scalar(second["specialType"]), "SNAPSHOT_CHANGED");
        var occurrences = new Dictionary<string, int>();
        var members = ids.Select(sid =>
        {
            int n = occurrences.GetValueOrDefault(sid); occurrences[sid] = n + 1;
            bool hasMetadata = songs.TryGetValue(sid, out var raw);
            JsonObject song = hasMetadata ? PlatformSongs.Wy(raw!) :
                NativeMusicSong.CreateOnline("wy", O(("songmid", sid)), "未知歌曲");
            return new SyncMember(sid, n == 0 ? sid : sid + "#" + n, song, HasMetadata: hasMetadata);
        }).ToArray();
        bool owned = owner == AccountId; string special = PJ.Scalar(second["specialType"] ?? entry["specialType"] ?? JsonValue.Create(0));
        Require(!owned || special == PJ.Scalar(entry["specialType"] ?? JsonValue.Create(0)), "SNAPSHOT_CHANGED");
        return new(id, Name(entry["name"]), Favorite(entry) ? "favorites" : "playlist", owned && special is "0" or "5", owner, members, O(("specialType", special)));
    }
    internal override async Task<SyncDirectory> ReadAsync(string[] kinds)
    {
        var before = await Directory(); var all = new List<SyncRemote>();
        var selected = before.Where(entry => kinds.Contains(Favorite(entry) ? "favorites" : "playlist")).ToArray();
        foreach (var entry in selected) all.Add(await ReadEntry(entry));
        Require(Signature(before) == Signature(await Directory()), "SNAPSHOT_CHANGED"); return new(AccountId, all);
    }
    internal override async Task<SyncRemote?> ReadCollectionAsync(string id)
    {
        var before = await Directory(); var entry = before.FirstOrDefault(r => Id(r["id"]) == id);
        var result = entry is null ? null : await ReadEntry(entry);
        Require(Signature(before) == Signature(await Directory()), "SNAPSHOT_CHANGED"); return result;
    }
    internal override async Task CreateAsync(string name) => _ = await Wy("/api/playlist/create", O(("name", name), ("privacy", 10), ("type", "NORMAL")));
    internal override async Task DeleteAsync(SyncRemote remote)
    { Require(remote.Writable && remote.Kind != "favorites", "READ_ONLY"); _ = await Wy("/api/playlist/remove", O(("ids", "[" + remote.Id + "]"))); }
    internal override async Task ChangeAsync(SyncRemote remote, bool add, IReadOnlyList<string> ids, IReadOnlyDictionary<string, JsonObject> songs)
    {
        Require(remote.Writable, "READ_ONLY"); Require(ids.Count is > 0 and <= 100, "LIMIT"); foreach (string id in ids) _ = Id(JsonValue.Create(id));
        if (remote.Kind == "favorites")
        {
            // Engine batches favorites one at a time, each followed by a full readback.
            Require(ids.Count == 1, "LIMIT");
            _ = await Wy("/api/radio/like", O(("trackId", ids[0]), ("like", add), ("alg", "itembased"), ("time", "3")));
        }
        else _ = await Wy("/api/playlist/manipulate/tracks", O(("pid", remote.Id), ("op", add ? "add" : "del"), ("trackIds", A(ids).ToJsonString()), ("imme", "true")), false);
    }
    internal override async Task OrderAsync(SyncRemote remote, IReadOnlyList<string> ids)
    {
        Require(remote.Writable, "READ_ONLY"); Same(remote.Ids.Order(), ids.Order());
        _ = await Wy("/api/playlist/manipulate/tracks", O(("pid", remote.Id), ("op", "update"), ("trackIds", A(ids).ToJsonString())), false);
    }
}
