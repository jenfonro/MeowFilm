// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus b900c259 src/kugou.ts; KuGouMusicApi 514f870d (MIT), see Platforms licenses.
using System.Net.Http;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
namespace MeowFilm.Capabilities.Sync;

internal sealed class MusicSyncKg(MusicPlatformSession session, MusicPlatformHttp http, Func<Task> guard, CancellationToken token,
    Func<HttpRequestMessage, CancellationToken, Task<byte[]>>? cloud, MusicSyncRequestBudget budget)
    : MusicSyncAdapter(session, http, budget, guard, token)
{
    private sealed record Entry(string Id, string ListId, int Type, string Name, string Owner, long Count,
        string? Version, string GlobalId, bool Favorite, bool Special, bool Writable, long Source, long? Sort);
    private sealed record Page(List<JsonObject> Rows, long? Count, string? Version);
    private JsonObject Credentials() => O(("userid", AccountId), ("token", PJ.Cookie(Cookies, "token")));
    private static Page ParsePage(JsonObject response, bool tracks)
    {
        var data = response["data"] as JsonObject ?? response; var info = data["list_info"];
        JsonNode? countNode = data["count"] ?? data["total"] ?? info?["count"] ?? response["count"] ?? response["total"];
        long? count = countNode is null ? null : Count(countNode);
        if (!tracks && count is null && new[] { "list_count", "collect_count", "album_count" }.Any(k => data[k] is not null))
            count = new[] { "list_count", "collect_count", "album_count" }.Sum(k => Count(data[k]));
        var rows = Rows(data.ContainsKey("info") ? data["info"] ?? (count == 0 ? new JsonArray() : null) : data["songs"] ?? response["info"]);
        var version = tracks ? info?["list_ver"] ?? data["list_ver"] ?? response["list_ver"] : data["total_ver"] ?? response["total_ver"];
        return new(rows, count, version is null ? null : Id(version));
    }
    private async Task<Page> Paginate(Func<int, Task<JsonObject>> read, int maximumPages,
        bool tracks, bool owned, Action<JsonObject>? inspect = null)
    {
        var all = new List<JsonObject>(); var seen = new HashSet<string>(); long? count = null; string? version = null;
        for (int page = 1; page <= maximumPages; page++)
        {
            var response = await read(page); inspect?.Invoke(response); var next = ParsePage(response, tracks);
            Require(count is null || next.Count is null || count == next.Count, "SNAPSHOT_CHANGED");
            Require(version is null || next.Version == version, "SNAPSHOT_CHANGED"); count = next.Count ?? count; version = next.Version ?? version;
            foreach (var row in next.Rows)
            {
                string id = tracks ? owned ? Id(row["fileid"]) : PlatformSongs.KgHash(row["hash"] ?? row["audio_info"]?["hash"]) : PJ.Scalar(row["type"]) + ":" + Id(row["listid"]);
                Require(seen.Add(id), "PAGINATION_STALLED"); all.Add(row);
            }
            Require(all.Count <= 10000 && (count is null || all.Count <= count), "PAGINATION_LIMIT");
            if (count is not null && all.Count == count || next.Rows.Count == 0)
            { Require(count is null || all.Count == count); return new(all, count, version); }
        }
        throw new MusicLibraryException("PAGINATION_LIMIT");
    }
    private async Task<(List<Entry> Entries, string? Version)> Directory()
    {
        Require(PJ.Cookie(Cookies, "userid") == AccountId, "INVALID_CREDENTIALS");
        var page = await Paginate(p => Kg("/v7/get_all_list", O(("plat", 1)), body: LJ.Merge(Credentials(), O(("total_ver", 979), ("type", 2), ("page", p), ("pagesize", 30)))),
            MusicSyncRequestBudget.MaximumDirectoryPages, false, false);
        var entries = page.Rows.Select(r =>
        {
            string list = Id(r["listid"]), name = Name(r["name"]), owner = Id(r["list_create_userid"]); long type = Count(r["type"]), source = Count(r["source"]);
            Require(type is 0 or 1, "IDENTITY_MISMATCH"); bool special = PJ.Scalar(r["is_def"]) is "1" or "2" || PJ.Scalar(r["is_default"]) == "1";
            bool favorite = type == 0 && owner == AccountId && special && name is "我喜欢" or "我喜欢的音乐";
            bool writable = type == 0 && owner == AccountId && source != 2 && (favorite || !special && name is not ("我喜欢" or "我喜欢的音乐" or "默认收藏"));
            return new Entry(type + ":" + list, list, (int)type, name, owner, Count(r["count"]), r["list_ver"] is null ? null : Id(r["list_ver"]),
                PJ.Scalar(r["global_collection_id"] ?? r["list_create_gid"]), favorite, special, writable, source, r["sort"] is null ? null : Count(r["sort"]));
        }).OrderBy(e => e.Type).ThenBy(e => e.Sort ?? long.MaxValue).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
        Require(entries.Count(e => e.Favorite) <= 1, "FAVORITES_UNCONFIRMED"); return (entries, page.Version);
    }
    private static string DirectorySignature((List<Entry> Entries, string? Version) d) => System.Text.Json.JsonSerializer.Serialize(new { d.Entries, d.Version });
    private async Task<SyncRemote> Scan(Entry entry)
    {
        void Identity(JsonObject response)
        {
            var data = response["data"] as JsonObject ?? response;
            if (data["list_info"] is null) return;
            Require(data["list_info"] is JsonObject, "INVALID_RESPONSE");
            var info = (JsonObject)data["list_info"]!;
            foreach (string key in new[] { "listid", "list_id" })
                if (info[key] is not null) Require(Id(info[key]) == entry.ListId, "IDENTITY_MISMATCH");
            if (info["list_create_userid"] is not null) Require(Id(info["list_create_userid"]) == entry.Owner, "IDENTITY_MISMATCH");
            if (info["type"] is not null) Require(Count(info["type"]) == entry.Type, "IDENTITY_MISMATCH");
            if (info["global_collection_id"] is not null && entry.GlobalId.Length > 0)
                Require(PJ.Scalar(info["global_collection_id"]) == entry.GlobalId, "IDENTITY_MISMATCH");
        }
        Page page;
        if (entry.Type == 0)
            page = await Paginate(p => Kg("/v4/get_list_all_file_v3", new(), body: LJ.Merge(Credentials(), O(("listid", entry.ListId), ("type", 0),
                ("page", p), ("pagesize", 30), ("area_code", 1), ("allplatform", 1), ("show_cover", 1)))),
                MusicSyncRequestBudget.MaximumCollectionPages, true, true, Identity);
        else
        {
            Require(System.Text.RegularExpressions.Regex.IsMatch(entry.GlobalId, "^[A-Za-z0-9_-]{1,200}$") && !entry.GlobalId.StartsWith("id_", StringComparison.Ordinal), "IDENTITY_MISMATCH");
            page = await Paginate(p => Kg("/pubsongs/v2/get_other_list_file_nofilt", O(("area_code", 1), ("begin_idx", (p - 1) * 30), ("plat", 1),
                ("type", 1), ("mode", 1), ("personal_switch", 1), ("extend_fields", "abtags,hot_cmt,popularization"), ("pagesize", 30), ("global_collection_id", entry.GlobalId)), null),
                MusicSyncRequestBudget.MaximumCollectionPages, true, false, Identity);
        }
        Require(page.Rows.Count == entry.Count); Require(entry.Version is null || page.Version is null || entry.Version == page.Version, "SNAPSHOT_CHANGED");
        var members = page.Rows.OrderBy(r => r["sort"] is null ? long.MaxValue : Count(r["sort"])).Select(r =>
        {
            var song = PlatformSongs.Kg(r); string hash = NativeMusicSong.NativeId(song);
            return new SyncMember(hash, entry.Type == 0 ? Id(r["fileid"]) : hash, song, r["sort"] is null ? null : PJ.Scalar(r["sort"]));
        }).ToArray();
        return new(entry.Id, entry.Name, entry.Favorite ? "favorites" : "playlist", entry.Writable, entry.Owner, members,
            O(("listId", entry.ListId), ("type", entry.Type), ("version", page.Version ?? entry.Version), ("special", entry.Special), ("globalId", entry.GlobalId)));
    }
    private async Task<SyncRemote> ReadEntry(Entry entry)
    { var before = await Scan(entry); var after = await Scan(entry); Require(before.Signature == after.Signature, "SNAPSHOT_CHANGED"); return after; }
    internal override async Task<SyncDirectory> ReadAsync(string[] kinds)
    {
        var before = await Directory(); if (kinds.Contains("favorites")) Require(before.Entries.Count(e => e.Favorite) == 1, "FAVORITES_UNCONFIRMED");
        var result = new List<SyncRemote>();
        var selected = before.Entries.Where(e => e.Source != 2 &&
            kinds.Contains(e.Favorite ? "favorites" : "playlist")).ToArray();
        Require(selected.Length <= MusicSyncRequestBudget.MaximumCollectionsPerPlatform, "SYNC_REQUEST_LIMIT");
        foreach (var entry in selected) result.Add(await ReadEntry(entry));
        Require(DirectorySignature(before) == DirectorySignature(await Directory()), "SNAPSHOT_CHANGED"); return new(AccountId, result);
    }
    internal override async Task<SyncRemote?> ReadCollectionAsync(string id)
    {
        var before = await Directory(); var entry = before.Entries.FirstOrDefault(e => e.Id == id);
        var result = entry is null ? null : await ReadEntry(entry);
        Require(DirectorySignature(before) == DirectorySignature(await Directory()), "SNAPSHOT_CHANGED"); return result;
    }
    internal override void CheckWriteCapability(string operation)
    { if (operation is "delete" or "order") Require(cloud is not null, "SYNC_CLOUD_TRANSPORT_REQUIRED"); }
    internal override async Task CreateAsync(string name)
    {
        var body = LJ.Merge(Credentials(), O(("total_ver", 0), ("name", name), ("type", 0), ("source", 1), ("is_pri", 1), ("list_create_gid", ""), ("from_shupinmv", 0)));
        _ = await Kg("/cloudlist.service/v5/add_list", O(("last_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("last_area", "gztx")), null, body);
    }
    internal override async Task DeleteAsync(SyncRemote remote)
    {
        CheckWriteCapability("delete"); Require(remote.Writable && remote.Kind != "favorites" && !LJ.B(remote.Locator["special"]), "READ_ONLY");
        await AdmitRequest(); await MusicSyncKgCloud.SendAsync(Session, "/v2/delete_list", O(("listid", JsonNode.Parse(Id(remote.Locator["listId"]))), ("total_ver", 0), ("type", 1)), cloud!, Token); await Guard();
    }
    internal override async Task ChangeAsync(SyncRemote remote, bool add, IReadOnlyList<string> ids, IReadOnlyDictionary<string, JsonObject> songs)
    {
        Require(remote.Writable && ids.Count is > 0 and <= 100, "READ_ONLY"); var body = LJ.Merge(Credentials(), O(("listid", Id(remote.Locator["listId"])), ("list_ver", 0), ("type", 0)));
        var rows = new JsonArray();
        if (add)
        {
            foreach (string id in ids)
            {
                Require(songs.ContainsKey(id), "UNMAPPED_SONG"); var source = NativeMusicSong.SourceData(songs[id]);
                if (PJ.Scalar(source["albumId"] ?? source["album_id"]) is "" or "0" || PJ.Scalar(source["albumAudioId"]) is "" or "0")
                {
                    // Native catalog detail is read-only and account-bound, never a Node fallback.
                    using var catalog = new MusicPlatformCatalog(new FixedSession(Session), Http);
                    await AdmitRequest();
                    var result = await catalog.SongAsync(new("kg", id, true), Token); await Guard(); source = NativeMusicSong.SourceData(JsonNode.Parse(result.GetRawText())!.AsObject());
                }
                string name = LJ.S(source["name"], LJ.S(songs[id]["title"])); Require(LJ.Id(name, 200) && name != "未知歌曲" && !name.Any(c => c is ',' or '|' or '\r' or '\n'), "UNMAPPED_SONG");
                Require(PlatformSongs.KgHash(source["hash"]) == id, "IDENTITY_MISMATCH");
                rows.Add(O(("number", 1), ("name", name), ("hash", id), ("size", 0), ("sort", 0), ("timelen", 0), ("bitrate", 0),
                    ("album_id", JsonNode.Parse(Id(source["albumId"] ?? source["album_id"]))), ("mixsongid", JsonNode.Parse(Id(source["albumAudioId"])))));
            }
            body["slow_upload"] = 1; body["scene"] = "false;null"; body["data"] = rows;
            _ = await Kg("/cloudlist.service/v6/add_song", O(("last_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("last_area", "gztx")), null, body);
        }
        else
        {
            foreach (var member in remote.Members.Where(m => ids.Contains(m.Id))) rows.Add(O(("fileid", JsonNode.Parse(Id(JsonValue.Create(member.Key))))));
            Require(rows.Count > 0, "IDENTITY_MISMATCH"); body["data"] = rows;
            _ = await Kg("/v4/delete_songs", new(), body: body);
        }
    }
    internal override async Task OrderAsync(SyncRemote remote, IReadOnlyList<string> ids)
    {
        CheckWriteCapability("order"); Require(remote.Writable, "READ_ONLY"); Same(remote.Ids.Order(), ids.Order());
        Require(remote.Locator["version"] is not null, "INCOMPLETE_PAGE");
        var byHash = remote.Members.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => new Queue<string>(g.Select(m => m.Key)));
        var data = new JsonArray(ids.Select((id, index) => (JsonNode)O(("fileid", JsonNode.Parse(byHash[id].Dequeue())), ("sort", index))).ToArray());
        await AdmitRequest(); await MusicSyncKgCloud.SendAsync(Session, "/v1/modify_song_sort", O(("listid", JsonNode.Parse(Id(remote.Locator["listId"]))),
            ("list_ver", JsonNode.Parse(Id(remote.Locator["version"]))), ("type", 0), ("data", data)), cloud!, Token); await Guard();
    }
    private sealed class FixedSession(MusicPlatformSession session) : IMusicPlatformSessionSource
    { public ValueTask<MusicPlatformSession?> AcquireAsync(string platform, CancellationToken cancellationToken) => ValueTask.FromResult<MusicPlatformSession?>(platform == "kg" ? session : null);
        public bool IsCurrent(MusicPlatformSession value) => ReferenceEquals(session, value); }
}
