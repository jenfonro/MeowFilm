// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus b900c259 qq-library.ts; QQMusicApi 27861e51 modules/songlist.py (GPL-3.0-or-later).
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
namespace MeowFilm.Capabilities.Sync;

internal sealed class MusicSyncTx(MusicPlatformSession session, MusicPlatformHttp http,
    MusicPlatformClientContextOwner clients, MusicPlatformClientContext? client,
    Func<Task> guard, Action accountGuard, CancellationToken token)
    : MusicSyncAdapter(session, http, clients, client, guard, accountGuard, token)
{
    private sealed record Entry(string Id, string DirId, string Owner, string Name, bool Writable, string Kind);
    private Entry Favorites => new("favorites", "201", AccountId, "我喜欢", true, "favorites");
    private string Euin
    {
        get { var c = Cookies; string value = PJ.Cookie(c, "encrypt_uin", PJ.Cookie(c, "encryptUin"));
            Require(value.Length > 0 && (!c.TryGetValue("encryptUin", out var alias) || alias == value), "INVALID_CREDENTIALS"); return value; }
    }
    private async Task<List<Entry>> Directory()
    {
        Require(PJ.Cookie(Cookies, "musicid") == AccountId, "INVALID_CREDENTIALS");
        var data = await Tx("music.musicasset.PlaylistBaseRead", "GetPlaylistByUin", O(("uin", AccountId)));
        var rows = Rows(data["v_playlist"]); Require(LJ.B(data["bFinish"]) && Count(data["total"]) == rows.Count);
        var result = new List<Entry>(); var seen = new HashSet<string>(); var subscribed = new HashSet<string>();
        Entry Parse(JsonObject r, bool owned)
        {
            string id = Id(r["tid"] ?? r["dissid"] ?? r["id"]), dir = Id(r["dirid"]), owner = Id(r["uin"]);
            Require(!owned || owner == AccountId && r["invalid"] is not null && !LJ.B(r["invalid"]), "IDENTITY_MISMATCH");
            return new(id, dir, owner, Name(r["dirName"] ?? r["title"] ?? r["dissname"]), owned, "playlist");
        }
        foreach (var row in rows)
        {
            if (PJ.Scalar(row["dirid"]) == "201") { Require(Id(row["uin"]) == AccountId, "IDENTITY_MISMATCH"); continue; }
            var entry = Parse(row, true); Require(seen.Add(entry.Id), "PAGINATION_STALLED"); result.Add(entry);
        }
        long? total = null; int received = 0;
        for (long page = 0; ; page = checked(page + 1))
        {
            var next = await Tx("music.musicasset.PlaylistFavRead", "CgiGetPlaylistFavInfo", O(("uin", Euin), ("offset", page * 100), ("size", 100)));
            Require(next["hide"] is not null && !LJ.B(next["hide"]), "FORBIDDEN"); Require(next["v_failTids"] is JsonArray fail && fail.Count == 0);
            long count = Count(next["total"]), more = Count(next["hasmore"]); Require(more is 0 or 1 && (total is null || total == count), "SNAPSHOT_CHANGED"); total = count;
            var items = Rows(next["v_list"], 100); received += items.Count;
            foreach (var row in items)
            {
                var entry = Parse(row, false); Require(subscribed.Add(entry.Id), "PAGINATION_STALLED");
                if (seen.Add(entry.Id)) result.Add(entry);
                else Require(result.Any(e => e.Id == entry.Id && e.Writable && e.Owner == entry.Owner), "IDENTITY_MISMATCH");
            }
            if (more == 0) { Require(received == total); return result; }
            Require(items.Count == 100 && received < total, "PAGINATION_STALLED");
        }
    }
    private async Task<SyncMember[]> Scan(Entry entry)
    {
        var result = new List<SyncMember>(); var seen = new HashSet<string>(); long? total = null;
        for (long page = 0; ; page = checked(page + 1))
        {
            var args = O(("disstid", entry.Kind == "favorites" ? JsonValue.Create(0) : JsonNode.Parse(entry.Id)), ("dirid", JsonNode.Parse(entry.DirId)),
                ("tag", true), ("song_begin", page * 100), ("song_num", 100), ("userinfo", true), ("orderlist", true), ("onlysonglist", false));
            if (entry.Kind == "favorites") args["enc_host_uin"] = Euin;
            var data = await Tx("music.srfDissInfo.DissInfo", "CgiGetDiss", args); var info = LJ.Object(data["dirinfo"]);
            Require(entry.Kind == "favorites" || Id(info["id"]) == entry.Id, "IDENTITY_MISMATCH");
            Require(Id(info["creator"]?["musicid"]) == entry.Owner && (!entry.Writable || Id(info["dirid"]) == entry.DirId), "IDENTITY_MISMATCH");
            long count = Count(data["total_song_num"]), more = Count(data["hasmore"]); Require(more is 0 or 1 && (total is null || total == count), "SNAPSHOT_CHANGED"); total = count;
            var rows = Rows(data["songlist"] ?? (total == 0 ? new JsonArray() : null), 100); Require(Count(data["songlist_size"]) == rows.Count);
            foreach (var row in rows)
            {
                var song = PlatformSongs.Tx(row); string id = NativeMusicSong.NativeId(song); Require(seen.Add(id), "PAGINATION_STALLED");
                result.Add(new(id, id, song));
            }
            if (more == 0) { Require(result.Count == total); return result.ToArray(); }
            Require(rows.Count == 100 && result.Count < total, "PAGINATION_STALLED");
        }
    }
    private async Task<SyncRemote> ReadEntry(Entry entry)
    {
        var before = await Scan(entry); var after = await Scan(entry); Same(before.Select(m => m.Id), after.Select(m => m.Id));
        for (int i = 0; i < before.Length; i++)
        {
            var a = NativeMusicSong.SourceData(before[i].Song); var b = NativeMusicSong.SourceData(after[i].Song);
            Require(PJ.Scalar(a["qqSongId"]) == PJ.Scalar(b["qqSongId"]) && PJ.Scalar(a["songType"]) == PJ.Scalar(b["songType"]), "IDENTITY_MISMATCH");
        }
        return new(entry.Id, entry.Name, entry.Kind, entry.Writable, entry.Owner, after, O(("dirId", entry.DirId)));
    }
    private static string Signature(List<Entry> entries) => System.Text.Json.JsonSerializer.Serialize(entries.OrderBy(e => e.Id, StringComparer.Ordinal));
    internal override async Task<SyncDirectory> ReadAsync(string[] kinds)
    {
        var before = kinds.Contains("playlist") ? await Directory() : []; var result = new List<SyncRemote>();
        if (kinds.Contains("favorites")) result.Add(await ReadEntry(Favorites));
        foreach (var entry in before) result.Add(await ReadEntry(entry));
        if (kinds.Contains("playlist")) Require(Signature(before) == Signature(await Directory()), "SNAPSHOT_CHANGED");
        return new(AccountId, result);
    }
    internal override async Task<SyncRemote?> ReadCollectionAsync(string id)
    {
        if (id == "favorites") return await ReadEntry(Favorites);
        var before = await Directory(); var entry = before.FirstOrDefault(e => e.Id == id); var result = entry is null ? null : await ReadEntry(entry);
        Require(Signature(before) == Signature(await Directory()), "SNAPSHOT_CHANGED"); return result;
    }
    internal override async Task CreateAsync(string name) => _ = await Tx("music.musicasset.PlaylistBaseWrite", "AddPlaylist", O(("dirName", name)));
    internal override async Task DeleteAsync(SyncRemote remote)
    {
        Require(remote.Writable && remote.Kind != "favorites", "READ_ONLY");
        _ = await Tx("music.musicasset.PlaylistBaseWrite", "DelPlaylist", O(("dirId", JsonNode.Parse(Id(remote.Locator["dirId"])))));
    }
    internal override async Task ChangeAsync(SyncRemote remote, bool add, IReadOnlyList<string> ids, IReadOnlyDictionary<string, JsonObject> songs)
    {
        Require(remote.Writable && ids.Count is > 0 and <= 100, "READ_ONLY"); var values = new JsonArray();
        foreach (string id in ids)
        {
            JsonObject? song = remote.Members.FirstOrDefault(m => m.Id == id)?.Song ?? songs.GetValueOrDefault(id);
            var sd = song is null ? new JsonObject() : NativeMusicSong.SourceData(song);
            if (sd["qqSongId"] is null || sd["songType"] is null)
            {
                var data = await Tx("music.trackInfo.UniformRuleCtrl", "CgiGetTrackInfo", O(("ctx", 0), ("client", 1), ("types", new[] { 0 }), ("modify_stamp", new[] { 0 }), ("mids", new[] { id })));
                var rows = Rows(data["tracks"], 1); Require(rows.Count == 1 && PJ.Scalar(rows[0]["mid"]) == id, "IDENTITY_MISMATCH");
                sd = NativeMusicSong.SourceData(PlatformSongs.Tx(rows[0]));
            }
            values.Add(O(("songId", JsonNode.Parse(Id(sd["qqSongId"]))), ("songType", add ? Count(sd["songType"]) : 0)));
        }
        var result = await Tx("music.musicasset.PlaylistDetailWrite", add ? "AddSonglist" : "DelSonglist",
            O(("dirId", JsonNode.Parse(Id(remote.Locator["dirId"]))), ("tid", remote.Kind == "favorites" ? JsonValue.Create(0) : JsonNode.Parse(remote.Id)), ("bFmtUtf8", true), ("v_songInfo", values)));
        Require(PJ.Scalar(result["retCode"]) == "0", "WRITE_UNCONFIRMED");
    }
    internal override void CheckWriteCapability(string operation) { if (operation == "order") throw new MusicLibraryException("REMOTE_ORDER_UNSUPPORTED"); }
    internal override Task OrderAsync(SyncRemote remote, IReadOnlyList<string> ids) => throw new MusicLibraryException("REMOTE_ORDER_UNSUPPORTED");
}
