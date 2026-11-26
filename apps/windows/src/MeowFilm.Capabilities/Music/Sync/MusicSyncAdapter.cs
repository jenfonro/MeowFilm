// SPDX-License-Identifier: GPL-3.0-only
// Native adaptations of iWebPlus b900c259 (Apache-2.0). Protocol notices: LICENSES/NOTICE.md.
using System.Globalization;
using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Sync;

internal sealed record SyncLocator(string? ListId = null, string? CreatorId = null, string? CreatorListId = null)
{
    internal JsonElement ToElement() => MusicNodeSongCodec.BuildObject(w =>
    {
        MusicNodeSongCodec.WriteOptionalString(w, "listId", ListId);
        MusicNodeSongCodec.WriteOptionalString(w, "creatorId", CreatorId);
        MusicNodeSongCodec.WriteOptionalString(w, "creatorListId", CreatorListId);
    });

    internal static SyncLocator FromElement(JsonElement el) =>
        el.ValueKind != JsonValueKind.Object
            ? new SyncLocator()
            : new SyncLocator(
                MusicSyncAdapter.OptionalStr(el, "listId"),
                MusicSyncAdapter.OptionalStr(el, "creatorId"),
                MusicSyncAdapter.OptionalStr(el, "creatorListId"));
}

// Membership can be fully verified even when a detail endpoint omits its recording metadata.
// This flag is round-local evidence, not a persisted song field or a fabricated metadata revision.
internal sealed record SyncMember(string Id, string Key, MusicSongRecord Song, string? Sort = null, bool HasMetadata = true);

internal sealed record SyncRemote(string Id, string Name, string Kind, bool Writable, string Owner,
    IReadOnlyList<SyncMember> Members, JsonElement Locator)
{
    private string[]? keys, ids;
    private string? signature;
    internal string[] Keys => keys ??= Members.Select(m => m.Key).ToArray();
    internal string[] Ids => ids ??= Members.Select(m => m.Id).ToArray();

    internal string Signature => signature ??= MusicNodeSongCodec.HashJsonObject(w =>
    {
        w.WriteString("id", Id);
        w.WriteString("name", Name);
        w.WriteString("kind", Kind);
        w.WriteBoolean("writable", Writable);
        w.WriteString("owner", Owner);
        w.WritePropertyName("members");
        w.WriteStartArray();
        foreach (var m in Members)
        {
            w.WriteStartObject();
            w.WriteString("id", m.Id);
            w.WriteString("key", m.Key);
            if (m.Sort is not null) w.WriteString("sort", m.Sort); else w.WriteNull("sort");
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WritePropertyName("locator");
        if (Locator.ValueKind != JsonValueKind.Undefined) Locator.WriteTo(w);
        else { w.WriteStartObject(); w.WriteEndObject(); }
    });

    internal JsonElement ToWireElement() => MusicNodeSongCodec.BuildObject(w =>
    {
        w.WriteString("id", Id);
        w.WriteString("name", Name);
        w.WriteString("kind", Kind);
        w.WriteBoolean("writable", Writable);
        w.WriteString("owner", Owner);
        w.WritePropertyName("members");
        w.WriteStartArray();
        foreach (var m in Members)
        {
            w.WriteStartObject();
            w.WriteString("id", m.Id);
            w.WriteString("key", m.Key);
            w.WritePropertyName("song");
            MusicNodeSongCodec.WriteSong(w, m.Song);
            if (m.Sort is not null) w.WriteString("sort", m.Sort); else w.WriteNull("sort");
            w.WriteBoolean("hasMetadata", m.HasMetadata);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WritePropertyName("locator");
        if (Locator.ValueKind != JsonValueKind.Undefined) Locator.WriteTo(w);
        else { w.WriteStartObject(); w.WriteEndObject(); }
    });
}

internal sealed record SyncSubscription(string Id, string Name, string Cover, long Count, SyncLocator Locator);
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

    private async Task<JsonElement> Read(string operation, object parameters)
    {
        await checkpointGuard().ConfigureAwait(false); accountGuard(); token.ThrowIfCancellationRequested();
        var value = await client.RequestAsync(Platform, operation, parameters, session, token, accountGuard).ConfigureAwait(false);
        await checkpointGuard().ConfigureAwait(false); accountGuard();
        return value;
    }

    internal static string? OptionalStr(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string Str(JsonElement obj, string key) => OptionalStr(obj, key) ?? "";

    private static bool Bool(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) &&
        v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();

    private static long RequireIntProp(JsonElement obj, string key)
    {
        JsonElement v = default;
        MusicRules.Require(obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out v) && v.ValueKind == JsonValueKind.Number, "INVALID_REQUEST");
        decimal parsed = 0;
        MusicRules.Require(decimal.TryParse(v.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) &&
            decimal.Truncate(parsed) == parsed && parsed >= 0 && parsed <= MusicRules.MaxInteger, "INVALID_REQUEST");
        return (long)parsed;
    }

    private static SyncSubscription ParseSubscription(JsonElement el) => new(
        Str(el, "id"),
        Str(el, "name"),
        Str(el, "cover"),
        el.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt64() : 0,
        el.TryGetProperty("locator", out var loc) ? SyncLocator.FromElement(loc) : new SyncLocator());

    private static SyncMember ParseMember(JsonElement el) => new(
        Str(el, "id"),
        Str(el, "key"),
        MusicNodeSongCodec.ReadSong(el.GetProperty("song")),
        OptionalStr(el, "sort"),
        !el.TryGetProperty("hasMetadata", out var hm) || hm.ValueKind != JsonValueKind.False);

    private static SyncRemote ParseRemote(JsonElement el)
    {
        var members = new List<SyncMember>();
        if (el.TryGetProperty("members", out var mEl) && mEl.ValueKind == JsonValueKind.Array)
            foreach (var m in mEl.EnumerateArray()) members.Add(ParseMember(m));
        var locator = el.TryGetProperty("locator", out var loc) ? loc.Clone() : MusicNodeSongCodec.BuildObject(_ => { });
        return new SyncRemote(
            Str(el, "id"),
            Str(el, "name"),
            Str(el, "kind"),
            Bool(el, "writable"),
            Str(el, "owner"),
            members.ToArray(),
            locator);
    }

    private static SyncDirectory ParseDirectory(JsonElement el)
    {
        var collections = new List<SyncRemote>();
        if (el.TryGetProperty("collections", out var cEl) && cEl.ValueKind == JsonValueKind.Array)
            foreach (var c in cEl.EnumerateArray()) collections.Add(ParseRemote(c));

        var subscriptions = new List<SyncSubscription>();
        if (el.TryGetProperty("subscriptions", out var sEl) && sEl.ValueKind == JsonValueKind.Array)
            foreach (var s in sEl.EnumerateArray()) subscriptions.Add(ParseSubscription(s));

        var directory = new List<SyncDirectoryEntry>();
        if (el.TryGetProperty("directory", out var dEl) && dEl.ValueKind == JsonValueKind.Array)
            foreach (var d in dEl.EnumerateArray())
                directory.Add(new SyncDirectoryEntry(Str(d, "id"), Bool(d, "owned")));

        return new SyncDirectory(Str(el, "accountId"), collections.ToArray(), subscriptions.ToArray(), directory.ToArray());
    }

    internal async Task<SyncDirectory> ReadAsync(string[] kinds) =>
        ParseDirectory(await Read("sync.read", new { kinds }).ConfigureAwait(false));

    internal async Task<SyncRemote?> ReadCollectionAsync(string id)
    {
        var directory = ParseDirectory(await Read("sync.read", new { collectionId = id }).ConfigureAwait(false));
        return directory.Collections.FirstOrDefault();
    }

    internal async Task<SyncDirectory> ReadSubscriptionsAsync() =>
        ParseDirectory(await Read("sync.read", new { subscriptionsOnly = true }).ConfigureAwait(false));

    internal async Task<SyncSubscription> SubscriptionTargetAsync(string id, string name, string cover, long count) =>
        ParseSubscription(await Read("sync.target", new { id, name, cover, count }).ConfigureAwait(false));

    internal async Task<Func<Task>> PrepareSubscriptionAsync(SyncSubscription target, bool subscribe)
    {
        if (subscribe && Platform == "kg")
            MusicRules.Require((target.Locator.CreatorId ?? "") != AccountId, "READ_ONLY");
        string action = subscribe ? "subscribe" : "unsubscribe";
        string? preparationId = Platform == "wy"
            ? (await Read("sync.prepare", new { action, id = target.Id }).ConfigureAwait(false)).GetProperty("preparationId").GetString()
            : null;
        return () => Write(new
        {
            action, id = target.Id, name = target.Name,
            remote = new { id = target.Id, name = target.Name, locator = target.Locator.ToElement() },
            preparationId
        });
    }

    internal void CheckWriteCapability(string operation)
    { if (Platform == "tx" && operation == "order") throw new MusicLibraryException("REMOTE_ORDER_UNSUPPORTED"); }

    internal const int MaxKugouPages = 50;

    internal Task CreateAsync(string name) => Write(new { action = "create", name });
    internal Task DeleteAsync(SyncRemote remote) => Write(new { action = "delete", remote = remote.ToWireElement() });
    internal Task RenameAsync(SyncRemote remote, string name) => Write(new { action = "rename", remote = remote.ToWireElement(), name });

    internal Task ChangeAsync(SyncRemote remote, bool add, IReadOnlyList<string> ids, IReadOnlyDictionary<string, MusicSongSourceRecord> songs)
    {
        var songsEl = MusicNodeSongCodec.BuildObject(w =>
        {
            foreach (var (k, v) in songs)
            {
                w.WritePropertyName(k);
                MusicNodeSongCodec.WriteSource(w, v);
            }
        });
        return Write(new
        {
            action = add ? "add" : "remove",
            remote = remote.ToWireElement(),
            ids,
            songs = songsEl,
            members = remote.Members.Where(member => !add && ids.Contains(member.Id))
                .Select(member => new { id = member.Id, key = member.Key }).ToArray()
        });
    }

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
        return Write(new { action = "order", remote = remote.ToWireElement(), ids, members });
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

    internal string? NativeId(MusicSongSourceRecord source) =>
        source.InfoSource == Platform ? NativeMusicSong.InfoId(source) : null;

    internal string? NativeId(MusicSongRecord song) =>
        song.Sources.Select(NativeId).FirstOrDefault(id => id is not null);

    internal async Task<MusicSongRecord?> ResolveAsync(MusicSongRecord song,
        Func<string, bool>? isExcluded = null)
    {
        if (NativeMusicSong.Fingerprint(song).Length == 0 || (song.Duration ?? 0) <= 0) return null;
        var recording = new NativeMusicSong.Recordings(song, token);
        var matches = new Dictionary<string, MusicSongRecord>(StringComparer.Ordinal);
        var seenPages = new HashSet<int>();
        for (int page = 1; page <= MaxKugouPages && seenPages.Add(page);)
        {
            var value = await Read("search", new
            {
                keyword = MusicSongMatcher.Query(song), page, limit = 30,
                kind = "songs", refresh = false
            }).ConfigureAwait(false);
            int itemCount = 0;
            foreach (var raw in value.GetProperty("items").EnumerateArray())
            {
                itemCount++;
                var candidate = NativeMusicSong.Normalize(MusicNodeSongCodec.ReadSong(raw));
                if (!recording.CanMerge(new(candidate), token) || NativeId(candidate) is not { } id) continue;
                string sourceKey = NativeMusicSong.SourceKey(candidate);
                if (isExcluded is not null && isExcluded(sourceKey)) continue;
                matches[id] = candidate;
            }
            if (Platform != "kg" || itemCount == 0 || !Bool(value, "more")) break;
            if (!value.TryGetProperty("continuation", out var continuation) || continuation.ValueKind != JsonValueKind.Object) break;
            int next = checked((int)RequireIntProp(continuation, "page"));
            if (next <= page) break;
            page = next;
        }
        return matches.Count == 1 ? matches.Values.Single() : null;
    }
}
