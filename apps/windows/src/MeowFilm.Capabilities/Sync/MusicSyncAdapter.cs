// SPDX-License-Identifier: GPL-3.0-only
// Native adaptations of iWebPlus b900c259 (Apache-2.0). Protocol notices: LICENSES/NOTICE.md.
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Sync;

// Membership can be fully verified even when a detail endpoint omits its recording metadata.
// This flag is round-local evidence, not a persisted song field or a fabricated metadata revision.
internal sealed record SyncMember(string Id, string Key, JsonObject Song, string? Sort = null, bool HasMetadata = true);
internal sealed record SyncRemote(string Id, string Name, string Kind, bool Writable, string Owner,
    IReadOnlyList<SyncMember> Members, JsonObject Locator)
{
    internal string[] Keys => Members.Select(m => m.Key).ToArray();
    internal string[] Ids => Members.Select(m => m.Id).ToArray();
    internal string Signature => LJ.Digest(LJ.Obj(("id", Id), ("name", Name), ("kind", Kind), ("writable", Writable),
        ("owner", Owner), ("members", Members.Select(m => new { m.Id, m.Key, m.Sort })), ("locator", Locator)));
}
internal sealed record SyncSubscription(string Id, string Name, string Cover, long Count, JsonObject Locator);
internal sealed record SyncDirectoryEntry(string Id, bool Owned);
internal sealed record SyncDirectory(string AccountId, IReadOnlyList<SyncRemote> Collections,
    IReadOnlyList<SyncSubscription> Subscriptions, IReadOnlyList<SyncDirectoryEntry> Directory);

internal sealed class MusicSyncWriteException(string status, string code) : Exception(code)
{
    internal string Status { get; } = status;
    internal string Code { get; } = code;
}

/// <summary>Node platform adapter. The native sync engine owns its journal, preflight and readback.</summary>
internal sealed class MusicSyncAdapter(MusicPlatformSession session, MusicPlatformClient client,
    Func<Task> checkpointGuard, Action accountGuard, CancellationToken token)
{
    internal string Platform => session.Platform;
    internal string AccountId => session.UserId;
    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private async Task<System.Text.Json.JsonElement> Read(string operation, object parameters)
    {
        await checkpointGuard().ConfigureAwait(false); accountGuard(); token.ThrowIfCancellationRequested();
        var value = await client.RequestAsync(Platform, operation, parameters, session, token, accountGuard).ConfigureAwait(false);
        await checkpointGuard().ConfigureAwait(false); accountGuard();
        return value;
    }
    internal async Task<SyncDirectory> ReadAsync(string[] kinds) =>
        (await Read("sync.read", new { kinds }).ConfigureAwait(false)).Deserialize<SyncDirectory>(Json)!;
    internal async Task<SyncRemote?> ReadCollectionAsync(string id)
    {
        var directory = (await Read("sync.read", new { collectionId = id }).ConfigureAwait(false)).Deserialize<SyncDirectory>(Json)!;
        return directory.Collections.FirstOrDefault();
    }
    internal async Task<SyncDirectory> ReadSubscriptionsAsync() =>
        (await Read("sync.read", new { subscriptionsOnly = true }).ConfigureAwait(false)).Deserialize<SyncDirectory>(Json)!;
    internal async Task<SyncSubscription> SubscriptionTargetAsync(string id, string name, string cover, long count) =>
        (await Read("sync.target", new { id, name, cover, count }).ConfigureAwait(false)).Deserialize<SyncSubscription>(Json)!;
    internal async Task<Func<Task>> PrepareSubscriptionAsync(SyncSubscription target, bool subscribe)
    {
        if (subscribe && Platform == "kg")
            LJ.Require(LJ.S(target.Locator["creatorId"]) != AccountId, "READ_ONLY");
        string action = subscribe ? "subscribe" : "unsubscribe";
        string? preparationId = Platform == "wy" ? (await Read("sync.prepare", new { action, id = target.Id }).ConfigureAwait(false))
            .GetProperty("preparationId").GetString() : null;
        return () => Write(new { action, id = target.Id, name = target.Name,
            remote = new { id = target.Id, name = target.Name, locator = target.Locator }, preparationId });
    }
    internal void CheckWriteCapability(string operation)
    { if (Platform == "tx" && operation == "order") throw new MusicLibraryException("REMOTE_ORDER_UNSUPPORTED"); }
    internal Task CreateAsync(string name) => Write(new { action = "create", name });
    internal Task DeleteAsync(SyncRemote remote) => Write(new { action = "delete", remote });
    internal Task ChangeAsync(SyncRemote remote, bool add, IReadOnlyList<string> ids, IReadOnlyDictionary<string, JsonObject> songs) =>
        Write(new { action = add ? "add" : "remove", remote, ids, songs,
            members = remote.Members.Where(member => !add && ids.Contains(member.Id))
                .Select(member => new { id = member.Id, key = member.Key }).ToArray() });
    internal Task OrderAsync(SyncRemote remote, IReadOnlyList<string> ids)
    {
        var remaining = remote.Members.GroupBy(member => member.Id).ToDictionary(
            group => group.Key, group => new Queue<SyncMember>(group), StringComparer.Ordinal);
        var members = new List<object>();
        foreach (var id in ids)
        {
            if (!remaining.TryGetValue(id, out var values) || !values.TryDequeue(out var member))
                throw new MusicSyncWriteException("notSent", "REMOTE_CHANGED");
            members.Add(new { id = member.Id, key = member.Key });
        }
        return Write(new { action = "order", remote, ids, members });
    }
    private async Task Write(object parameters)
    {
        try { await checkpointGuard().ConfigureAwait(false); accountGuard(); token.ThrowIfCancellationRequested(); }
        catch (Exception error) { throw new MusicSyncWriteException("notSent", MusicLibrarySyncEngine.Error(error)); }
        var value = await client.RequestAsync(Platform, "sync.write", parameters, session, token, accountGuard).ConfigureAwait(false);
        string status = value.TryGetProperty("status", out var receipt) && receipt.ValueKind == JsonValueKind.String
            ? receipt.GetString()! : "unknown";
        if (status != "confirmed") throw new MusicSyncWriteException(
            status is "notSent" or "rejected" ? status : "unknown",
            value.TryGetProperty("errorCode", out var code) && code.ValueKind == JsonValueKind.String
                ? code.GetString()! : "WRITE_UNCONFIRMED");
    }
    internal string? NativeId(JsonObject song)
    {
        var info = NativeMusicSong.SourceData(song);
        return LJ.S(info["source"]) == Platform ? NativeMusicSong.InfoId(info) : null;
    }
    internal async Task<JsonObject?> ResolveAsync(JsonObject song)
    {
        if (NativeMusicSong.Fingerprint(song).Length == 0 || LJ.N(song["duration"]) <= 0) return null;
        var recording = new NativeMusicSong.Recordings(song, token);
        var matches = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        for (int page = 1;;)
        {
            var value = JsonNode.Parse((await Read("search", new { keyword = MusicSongMatcher.Query(song), page, limit = 30,
                kind = "songs", refresh = true }).ConfigureAwait(false)).GetRawText())!.AsObject();
            foreach (var candidate in value["items"]!.AsArray().OfType<JsonObject>())
                if (recording.CanMerge(new(candidate), token) && NativeId(candidate) is { } id) matches[id] = candidate;
            if (Platform != "kg" || !LJ.B(value["more"])) break;
            int next = checked((int)LJ.I(value["continuation"]!["page"]));
            LJ.Require(next != page, "PAGINATION_STALLED"); page = next;
        }
        return matches.Count == 1 ? matches.Values.Single() : null;
    }
}
