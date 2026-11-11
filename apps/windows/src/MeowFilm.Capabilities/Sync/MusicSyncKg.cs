// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus b900c259 src/kugou.ts; KuGouMusicApi 514f870d (MIT), see Platforms licenses.
using System.Net.Http;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
namespace MeowFilm.Capabilities.Sync;

internal sealed class MusicSyncKg(MusicPlatformSession session, MusicPlatformHttp http,
    MusicPlatformClientContextOwner clients, MusicPlatformClientContext? client, Func<Task> guard, Action accountGuard, CancellationToken token,
    Func<HttpRequestMessage, CancellationToken, Task<byte[]>>? cloud)
    : MusicSyncAdapter(session, http, clients, client, guard, accountGuard, token)
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
    private async Task<Page> Paginate(Func<int, Task<JsonObject>> read,
        bool tracks, Action<JsonObject>? inspect = null)
    {
        var all = new List<JsonObject>(); var seen = new HashSet<string>(); long? count = null; string? version = null;
        for (int page = 1; ; page = checked(page + 1))
        {
            var response = await read(page); inspect?.Invoke(response); var next = ParsePage(response, tracks);
            Require(count is null || next.Count is null || count == next.Count, "SNAPSHOT_CHANGED");
            Require(version is null || next.Version == version, "SNAPSHOT_CHANGED"); count = next.Count ?? count; version = next.Version ?? version;
            foreach (var row in next.Rows)
            {
                string id = tracks ? Id(row["fileid"]) : PJ.Scalar(row["type"]) + ":" + Id(row["listid"]);
                Require(seen.Add(id), "PAGINATION_STALLED"); all.Add(row);
            }
            Require(count is null || all.Count <= count, "INCOMPLETE_PAGE");
            if (count is not null && all.Count == count || next.Rows.Count == 0)
            { Require(count is null || all.Count == count); return new(all, count, version); }
        }
    }
    private async Task<(List<Entry> Entries, string? Version)> Directory()
    {
        Require(PJ.Cookie(Cookies, "userid") == AccountId, "INVALID_CREDENTIALS");
        var page = await Paginate(p => Kg("/v7/get_all_list", O(("plat", 1)), body: LJ.Merge(Credentials(), O(("total_ver", 979), ("type", 2), ("page", p), ("pagesize", 30)))),
            false);
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
    private static List<SyncSubscription> Subscriptions(IEnumerable<Entry> entries) => entries.Where(entry => entry.Type == 1 && entry.Source != 2)
        .Select(entry =>
        {
            _ = MusicLibraryOwner.NetworkPlaylistId("kg", entry.GlobalId);
            return new SyncSubscription(entry.GlobalId, entry.Name, "", entry.Count, O(("listId", entry.ListId)));
        }).ToList();
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
        var page = await Paginate(p => Kg("/v4/get_list_all_file_v3", new(), body: LJ.Merge(Credentials(), O(("listid", entry.ListId), ("type", 0),
                ("page", p), ("pagesize", 30), ("area_code", 1), ("allplatform", 1), ("show_cover", 1)))),
                true, Identity);
        Require(page.Rows.Count == entry.Count); Require(entry.Version is null || page.Version is null || entry.Version == page.Version, "SNAPSHOT_CHANGED");
        var members = page.Rows.OrderBy(r => r["sort"] is null ? long.MaxValue : Count(r["sort"])).Select(r =>
        {
            var song = PlatformSongs.Kg(r); string hash = NativeMusicSong.NativeId(song);
            return new SyncMember(hash, Id(r["fileid"]), song, r["sort"] is null ? null : PJ.Scalar(r["sort"]));
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
        var selected = before.Entries.Where(e => e.Writable && e.Owner == AccountId &&
            kinds.Contains(e.Favorite ? "favorites" : "playlist")).ToArray();
        foreach (var entry in selected) result.Add(await ReadEntry(entry));
        Require(DirectorySignature(before) == DirectorySignature(await Directory()), "SNAPSHOT_CHANGED");
        return new(AccountId, result, kinds.Contains("playlist") ? Subscriptions(before.Entries) : []);
    }
    internal override async Task<SyncRemote?> ReadCollectionAsync(string id)
    {
        var before = await Directory(); var entry = before.Entries.FirstOrDefault(e => e.Id == id && e.Writable && e.Owner == AccountId);
        var result = entry is null ? null : await ReadEntry(entry);
        Require(DirectorySignature(before) == DirectorySignature(await Directory()), "SNAPSHOT_CHANGED"); return result;
    }
    internal override async Task<IReadOnlyList<SyncSubscription>> ReadSubscriptionsAsync()
    {
        var before = await Directory(); var subscriptions = Subscriptions(before.Entries);
        Require(DirectorySignature(before) == DirectorySignature(await Directory()), "SNAPSHOT_CHANGED"); return subscriptions;
    }
    internal override async Task<SyncSubscription> SubscriptionTargetAsync(string id, string name, string cover, long count)
    {
        _ = MusicLibraryOwner.NetworkPlaylistId("kg", id);
        var response = await Kg("/v3/get_list_info", new(), "pubsongs.kugou.com",
            LJ.Merge(Credentials(), O(("data", new JsonArray(O(("global_collection_id", id)))))));
        var rows = Rows(response["data"]);
        Require(rows.Count == 1 && PJ.Scalar(rows[0]["global_collection_id"]) == id &&
            PJ.Scalar(rows[0]["status"]) == "1", "SUBSCRIPTION_ID_UNCONFIRMED");
        string creator = Id(rows[0]["list_create_userid"]), list = Id(rows[0]["list_create_listid"]);
        Require(creator != AccountId, "READ_ONLY");
        return new(id, Name(rows[0]["name"]), NativeMusicSong.PublicUrl(rows[0]["pic"]), Count(rows[0]["count"]),
            O(("creatorId", creator), ("creatorListId", list)));
    }
    internal override async Task SubscribeAsync(SyncSubscription target)
    {
        _ = MusicLibraryOwner.NetworkPlaylistId("kg", target.Id);
        string creator = Id(target.Locator["creatorId"]), list = Id(target.Locator["creatorListId"]);
        Require(creator != AccountId, "READ_ONLY");
        var body = LJ.Merge(Credentials(), O(("total_ver", 0), ("name", target.Name), ("type", 1), ("source", 1), ("is_pri", 0),
            ("list_create_userid", creator), ("list_create_listid", list), ("list_create_gid", target.Id), ("from_shupinmv", 0)));
        _ = await Kg("/cloudlist.service/v5/add_list", new(), null, body);
    }
    internal override async Task UnsubscribeAsync(SyncSubscription subscription)
    {
        CheckWriteCapability("unsubscribe");
        _ = MusicLibraryOwner.NetworkPlaylistId("kg", subscription.Id);
        // This is the current account's type=1 subscription list, never the creator's source list.
        string list = Id(subscription.Locator["listId"]);
        await Guard();
        await MusicSyncKgCloud.SendAsync(Session, KgClient, "/v2/delete_list",
            O(("listid", JsonNode.Parse(list)), ("total_ver", 0), ("type", 1)), cloud!, Token);
        await Guard();
    }
    internal override void CheckWriteCapability(string operation)
    { if (operation is "delete" or "order" or "unsubscribe") Require(cloud is not null, "SYNC_CLOUD_TRANSPORT_REQUIRED"); }
    internal override async Task CreateAsync(string name)
    {
        var body = LJ.Merge(Credentials(), O(("total_ver", 0), ("name", name), ("type", 0), ("source", 1), ("is_pri", 1), ("list_create_gid", ""), ("from_shupinmv", 0)));
        _ = await Kg("/cloudlist.service/v5/add_list", O(("last_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("last_area", "gztx")), null, body);
    }
    internal override async Task DeleteAsync(SyncRemote remote)
    {
        CheckWriteCapability("delete"); Require(remote.Writable && remote.Kind != "favorites" && !LJ.B(remote.Locator["special"]), "READ_ONLY");
        await Guard(); await MusicSyncKgCloud.SendAsync(Session, KgClient, "/v2/delete_list", O(("listid", JsonNode.Parse(Id(remote.Locator["listId"]))), ("total_ver", 0), ("type", 1)), cloud!, Token); await Guard();
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
                    using var catalog = new MusicPlatformCatalog(new FixedSession(Session), Http, Clients);
                    await Guard();
                    var result = await catalog.SongAsync(new("kg", id, true), Token); await AcceptCatalogReadAsync(); source = NativeMusicSong.SourceData(JsonNode.Parse(result.GetRawText())!.AsObject());
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
        await Guard(); await MusicSyncKgCloud.SendAsync(Session, KgClient, "/v1/modify_song_sort", O(("listid", JsonNode.Parse(Id(remote.Locator["listId"]))),
            ("list_ver", JsonNode.Parse(Id(remote.Locator["version"]))), ("type", 0), ("data", data)), cloud!, Token); await Guard();
    }
    private sealed class FixedSession(MusicPlatformSession session) : IMusicPlatformSessionSource
    { public ValueTask<MusicPlatformSession?> AcquireAsync(string platform, CancellationToken cancellationToken) => ValueTask.FromResult<MusicPlatformSession?>(platform == "kg" ? session : null);
        public bool IsCurrent(MusicPlatformSession value) => ReferenceEquals(session, value); }
}
