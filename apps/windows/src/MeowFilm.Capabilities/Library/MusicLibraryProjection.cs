using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

/// <summary>
/// Boundary projection between the command-layer JsonObject representation and the
/// strongly-typed persistence models used by IMusicLibraryStore.
/// </summary>
internal static class MusicLibraryProjection
{
    private static readonly HashSet<string> PureSourceScalarFields = new(StringComparer.Ordinal)
    {
        "source", "hash", "name", "singer", "albumName", "duration", "img", "qualityKnown", "artistNames"
    };

    private static readonly HashSet<string> IndexedSourceIdFields = new(StringComparer.Ordinal)
    {
        "songmid", "mid", "id"
    };

    internal static string? Str(JsonObject? obj, string key) =>
        obj is not null && obj[key] is JsonValue v
            ? v.TryGetValue<string>(out var s) ? s
            : v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.String ? el.GetString()
            : null : null;

    internal static long? Int64(JsonObject? obj, string key) =>
        obj is not null && obj[key] is JsonValue v
            ? v.TryGetValue<long>(out var l) ? l
            : v.TryGetValue<int>(out var i) ? i
            : v.TryGetValue<decimal>(out var m) && decimal.Truncate(m) == m ? (long)m
            : v.TryGetValue<double>(out var d) && double.IsFinite(d) && Math.Truncate(d) == d ? (long)d
            : v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var en) ? en
            : null : null;

    internal static double? Dbl(JsonObject? obj, string key) =>
        obj is not null && obj[key] is JsonValue v
            ? v.TryGetValue<double>(out var d) ? d
            : v.TryGetValue<long>(out var l) ? l
            : v.TryGetValue<int>(out var i) ? i
            : v.TryGetValue<float>(out var f) ? f
            : v.TryGetValue<decimal>(out var m) ? (double)m
            : v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var ed) ? ed
            : null : null;

    internal static bool? Bool(JsonObject? obj, string key) =>
        obj is not null && obj[key] is JsonValue v
            ? v.TryGetValue<bool>(out var b) ? b
            : v.TryGetValue<JsonElement>(out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False ? el.GetBoolean()
            : null : null;

    private static void SetNonNull(JsonObject obj, string key, string? val) { if (val is not null) obj[key] = val; }
    private static void SetNonNull(JsonObject obj, string key, double? val) { if (val is { } v) obj[key] = v; }
    private static void SetNonNull(JsonObject obj, string key, bool? val) { if (val is { } v) obj[key] = v; }

    private static string[] ReadStringArray(JsonArray? arr)
    {
        if (arr is null || arr.Count == 0) return [];
        var list = new List<string>(arr.Count);
        foreach (var item in arr)
            if (item is JsonValue v && v.TryGetValue<string>(out var text))
                list.Add(text);
        return list.ToArray();
    }

    private static JsonArray ToStringArray(IReadOnlyList<string> items) =>
        new(items.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());

    internal static JsonObject? SelectMembers(JsonObject? source, string[]? members)
    {
        if (source is null) return null;
        if (members is null) return source;
        var selected = new JsonObject();
        foreach (string member in members)
            if (source.ContainsKey(member))
                selected[member] = source[member]?.DeepClone();
        return selected;
    }

    internal static bool MatchesSubset(JsonObject? stored, JsonObject expected)
    {
        if (stored is null) return false;
        foreach (var pair in expected)
        {
            if (!stored.TryGetPropertyValue(pair.Key, out var actual)) return false;
            if (!JsonValuesEqual(actual, pair.Value)) return false;
        }
        return true;
    }

    internal static bool JsonValuesEqual(JsonNode? left, JsonNode? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left is JsonObject lo && right is JsonObject ro)
        {
            if (lo.Count != ro.Count) return false;
            foreach (var pair in lo)
                if (!ro.TryGetPropertyValue(pair.Key, out var rightProp) || !JsonValuesEqual(pair.Value, rightProp))
                    return false;
            return true;
        }
        if (left is JsonArray la && right is JsonArray ra)
        {
            if (la.Count != ra.Count) return false;
            for (int i = 0; i < la.Count; i++)
                if (!JsonValuesEqual(la[i], ra[i])) return false;
            return true;
        }
        if (left is JsonValue lv && right is JsonValue rv)
        {
            var lk = lv.GetValueKind();
            if (lk != rv.GetValueKind()) return false;
            return lk switch
            {
                JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False => true,
                JsonValueKind.String => string.Equals(ScalarString(lv), ScalarString(rv), StringComparison.Ordinal),
                JsonValueKind.Number => JsonNumbersEqual(lv, rv),
                _ => JsonNode.DeepEquals(lv, rv)
            };
        }
        return false;
    }

    private static string? ScalarString(JsonValue v) =>
        v.TryGetValue<string>(out var s) ? s
        : v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.String ? el.GetString()
        : null;

    private static bool JsonNumbersEqual(JsonValue left, JsonValue right)
    {
        if (TryGetDecimal(left, out var lm) && TryGetDecimal(right, out var rm))
            return lm == rm;
        if (TryGetDouble(left, out var ld) && TryGetDouble(right, out var rd))
            return ld == rd;
        return JsonNode.DeepEquals(left, right);
    }

    private static bool TryGetDecimal(JsonValue v, out decimal value)
    {
        if (v.TryGetValue<long>(out var l)) { value = l; return true; }
        if (v.TryGetValue<int>(out var i)) { value = i; return true; }
        if (v.TryGetValue<decimal>(out value)) return true;
        if (v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.Number)
            return el.TryGetDecimal(out value) ||
                decimal.TryParse(el.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        if (v.TryGetValue<double>(out var d) && double.IsFinite(d))
            return decimal.TryParse(d.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        return decimal.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetDouble(JsonValue v, out double value)
    {
        if (v.TryGetValue<double>(out value)) return double.IsFinite(value);
        if (v.TryGetValue<long>(out var l)) { value = l; return true; }
        if (v.TryGetValue<int>(out var i)) { value = i; return true; }
        if (v.TryGetValue<float>(out var f) && float.IsFinite(f)) { value = f; return true; }
        if (v.TryGetValue<decimal>(out var m)) { value = (double)m; return true; }
        if (v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out value))
            return double.IsFinite(value);
        value = 0;
        return false;
    }

    // ---- Songs ----

    internal static JsonObject ToJson(MusicSongRecord song)
    {
        var obj = new JsonObject
        {
            ["songId"] = song.SongId,
            ["title"] = song.Title,
            ["artist"] = song.Artist,
            ["album"] = song.Album,
            ["duration"] = song.Duration,
            ["baselineDuration"] = song.BaselineDuration,
            ["cover_url"] = song.CoverUrl
        };
        SetNonNull(obj, "localFileId", song.LocalFileId);
        SetNonNull(obj, "artworkSourceKey", song.ArtworkSourceKey);
        SetNonNull(obj, "artworkSourceImage", song.ArtworkSourceImage);
        if (song.ArtistNames.Length > 0)
            obj["artistNames"] = ToStringArray(song.ArtistNames);
        var sources = new JsonArray();
        foreach (var s in song.Sources) sources.Add(ToJson(s));
        obj["sources"] = sources;
        return obj;
    }

    internal static JsonObject ToJson(MusicSongSourceRecord src)
    {
        var mi = new JsonObject { ["source"] = src.InfoSource };
        SetNonNull(mi, "songmid", src.SongMid);
        SetNonNull(mi, "mid", src.Mid);
        SetNonNull(mi, "id", src.NativeId);
        SetNonNull(mi, "hash", src.Hash);
        SetNonNull(mi, "name", src.Name);
        SetNonNull(mi, "singer", src.Singer);
        SetNonNull(mi, "albumName", src.AlbumName);
        mi["duration"] = src.Duration;
        SetNonNull(mi, "img", src.Img);
        SetNonNull(mi, "qualityKnown", src.QualityKnown);
        if (src.ArtistNames.Length > 0)
            mi["artistNames"] = ToStringArray(src.ArtistNames);
        if (src.WireExtras is { Kind: StorageValueKind.Object, ObjectFields: { } extraFields })
        {
            foreach (var field in extraFields)
                mi[field.Name] = StorageValueJson.ToJsonNode(field.Value);
        }
        return new JsonObject
        {
            ["sourceKey"] = src.SourceKey,
            ["source"] = src.Platform,
            ["musicInfo"] = mi
        };
    }

    internal static MusicSongRecord SongFromJson(string songId, JsonObject value)
    {
        var artists = ReadStringArray(value["artistNames"] as JsonArray);
        var sources = new List<MusicSongSourceRecord>();
        if (value["sources"] is JsonArray srcArr)
        {
            foreach (var item in srcArr)
                if (item is JsonObject src)
                    sources.Add(SongSourceFromJson(src));
        }

        return new MusicSongRecord(
            songId,
            Str(value, "title") ?? "",
            Str(value, "artist") ?? "",
            Str(value, "album") ?? "",
            NativeMusicSong.ValidDuration(value["duration"]),
            NativeMusicSong.ValidDuration(value["baselineDuration"]),
            Str(value, "cover_url") ?? "",
            Str(value, "localFileId"),
            Str(value, "artworkSourceKey"),
            Str(value, "artworkSourceImage"),
            artists,
            sources.ToArray());
    }

    private static MusicSongSourceRecord SongSourceFromJson(JsonObject src)
    {
        string sourceKey = Str(src, "sourceKey")!;
        string platform = Str(src, "source")!;
        var mi = src["musicInfo"] as JsonObject ?? new JsonObject();
        string infoSource = Str(mi, "source") ?? platform;

        string? songMid = ReadIdAsText(mi, "songmid", out bool songMidNeedsExtra);
        string? mid = ReadIdAsText(mi, "mid", out bool midNeedsExtra);
        string? nativeId = ReadIdAsText(mi, "id", out bool nativeIdNeedsExtra);
        var artists = ReadStringArray(mi["artistNames"] as JsonArray);

        List<StorageValueField>? extraEntries = null;
        foreach (var pair in mi)
        {
            if (PureSourceScalarFields.Contains(pair.Key)) continue;
            if (IndexedSourceIdFields.Contains(pair.Key))
            {
                bool keepExtra = pair.Key switch
                {
                    "songmid" => songMidNeedsExtra,
                    "mid" => midNeedsExtra,
                    "id" => nativeIdNeedsExtra,
                    _ => false
                };
                if (!keepExtra) continue;
            }
            extraEntries ??= [];
            extraEntries.Add(new StorageValueField(pair.Key, StorageValueJson.FromJsonNode(pair.Value)));
        }

        return new MusicSongSourceRecord(
            sourceKey,
            platform,
            infoSource,
            songMid,
            mid,
            nativeId,
            Str(mi, "hash"),
            Str(mi, "name"),
            Str(mi, "singer"),
            Str(mi, "albumName"),
            NativeMusicSong.ValidDuration(mi["duration"]),
            Str(mi, "img"),
            Bool(mi, "qualityKnown"),
            artists,
            extraEntries is null ? null : StorageValue.FromObject(extraEntries));
    }

    private static string? ReadIdAsText(JsonObject obj, string key, out bool needsTypedExtra)
    {
        needsTypedExtra = false;
        if (!obj.TryGetPropertyValue(key, out var node) || node is null) return null;
        if (node is not JsonValue v)
        {
            needsTypedExtra = true;
            return null;
        }
        if (v.TryGetValue<string>(out var s)) return s;
        if (v.TryGetValue<JsonElement>(out var el))
        {
            if (el.ValueKind == JsonValueKind.String) return el.GetString();
            if (el.ValueKind == JsonValueKind.Number)
            {
                needsTypedExtra = true;
                return el.GetRawText();
            }
            needsTypedExtra = true;
            return null;
        }
        if (v.GetValueKind() == JsonValueKind.Number)
        {
            needsTypedExtra = true;
            return v.ToJsonString();
        }
        needsTypedExtra = true;
        return null;
    }

    // ---- Queue Source & Play Context ----

    internal static JsonObject? ToJson(MusicQueueSourceRecord? src)
    {
        if (src is null || src.IsEmpty) return null;
        var obj = new JsonObject();
        SetNonNull(obj, "id", src.Id);
        SetNonNull(obj, "title", src.Title);
        SetNonNull(obj, "kind", src.Kind);
        SetNonNull(obj, "platform", src.Platform);
        SetNonNull(obj, "sourceType", src.SourceType);
        SetNonNull(obj, "sourceTitle", src.SourceTitle);
        SetNonNull(obj, "sourceId", src.SourceId);
        SetNonNull(obj, "playlist", src.Playlist);
        SetNonNull(obj, "seedEntryId", src.SeedEntryId);
        SetNonNull(obj, "remoteSession", src.RemoteSession);
        SetNonNull(obj, "dailyBranch", src.DailyBranch);
        SetNonNull(obj, "searchKeyword", src.SearchKeyword);
        return obj;
    }

    internal static MusicQueueSourceRecord? QueueSourceFromJson(JsonObject? src)
    {
        if (src is null) return null;
        var rec = new MusicQueueSourceRecord(
            Str(src, "id"), Str(src, "title"), Str(src, "kind"), Str(src, "platform"),
            Str(src, "sourceType"), Str(src, "sourceTitle"), Str(src, "sourceId"),
            Str(src, "playlist"), Str(src, "seedEntryId"), Str(src, "remoteSession"),
            Str(src, "dailyBranch"), Str(src, "searchKeyword"));
        return rec.IsEmpty ? null : rec;
    }

    internal static JsonObject ToJson(MusicPlayContextRecord ctx)
    {
        var obj = new JsonObject();
        SetNonNull(obj, "collectionId", ctx.CollectionId);
        SetNonNull(obj, "playlistId", ctx.PlaylistId);
        SetNonNull(obj, "sourceTitle", ctx.SourceTitle);
        SetNonNull(obj, "playlist", ctx.Playlist);
        SetNonNull(obj, "sourceType", ctx.SourceType);
        SetNonNull(obj, "sourceId", ctx.SourceId);
        SetNonNull(obj, "sourcePlatform", ctx.SourcePlatform);
        SetNonNull(obj, "platform", ctx.Platform);
        SetNonNull(obj, "sourceLabel", ctx.SourceLabel);
        SetNonNull(obj, "searchKeyword", ctx.SearchKeyword);
        SetNonNull(obj, "dailyBranch", ctx.DailyBranch);
        SetNonNull(obj, "heartbeatPlaylistId", ctx.HeartbeatPlaylistId);
        SetNonNull(obj, "heartbeatAccountId", ctx.HeartbeatAccountId);
        SetNonNull(obj, "provider", ctx.Provider);
        SetNonNull(obj, "remoteSession", ctx.RemoteSession);

        if (ctx.QueueSourceId is not null || ctx.QueueSourceTitle is not null ||
            ctx.QueueSourceKind is not null || ctx.QueueSourcePlatform is not null)
        {
            var qs = new JsonObject();
            SetNonNull(qs, "id", ctx.QueueSourceId);
            SetNonNull(qs, "title", ctx.QueueSourceTitle);
            SetNonNull(qs, "kind", ctx.QueueSourceKind);
            SetNonNull(qs, "platform", ctx.QueueSourcePlatform);
            obj["queueSource"] = qs;
        }
        if (ctx.SimilarBatchId is not null || ctx.SimilarBasedOnSongId is not null || ctx.SimilarProvider is not null)
        {
            var sim = new JsonObject();
            SetNonNull(sim, "batchId", ctx.SimilarBatchId);
            SetNonNull(sim, "basedOnSongId", ctx.SimilarBasedOnSongId);
            SetNonNull(sim, "provider", ctx.SimilarProvider);
            obj["similar"] = sim;
        }
        return obj;
    }

    internal static MusicPlayContextRecord ContextFromJson(JsonObject? ctx)
    {
        if (ctx is null) return MusicPlayContextRecord.Empty;
        var qs = ctx["queueSource"] as JsonObject;
        var sim = ctx["similar"] as JsonObject;
        var rec = new MusicPlayContextRecord(
            Str(ctx, "collectionId"),
            Str(ctx, "playlistId"),
            Str(ctx, "sourceTitle"),
            Str(ctx, "playlist"),
            Str(ctx, "sourceType"),
            Str(ctx, "sourceId"),
            Str(ctx, "sourcePlatform"),
            Str(ctx, "platform"),
            Str(ctx, "sourceLabel"),
            Str(ctx, "searchKeyword"),
            Str(ctx, "dailyBranch"),
            Str(ctx, "heartbeatPlaylistId"),
            Str(ctx, "heartbeatAccountId"),
            Str(ctx, "provider"),
            Str(ctx, "remoteSession"),
            Str(qs, "id"),
            Str(qs, "title"),
            Str(qs, "kind"),
            Str(qs, "platform"),
            Str(sim, "batchId"),
            Str(sim, "basedOnSongId"),
            Str(sim, "provider"));
        return rec.IsEmpty ? MusicPlayContextRecord.Empty : rec;
    }

    // ---- Library ----

    internal static JsonObject ToJson(string scope, MusicLibraryAreaRecord rec)
    {
        var accounts = new JsonObject();
        foreach (var (plat, accId) in rec.Accounts) accounts[plat] = accId;

        var collections = new JsonArray();
        foreach (var col in rec.Collections)
        {
            var bindings = new JsonArray();
            foreach (var b in col.Bindings)
            {
                var mp = new JsonObject();
                foreach (var (reid, sid) in b.Map) mp[reid] = sid;
                bindings.Add(new JsonObject
                {
                    ["platform"] = b.Platform,
                    ["accountId"] = b.AccountId,
                    ["id"] = b.RemoteId,
                    ["writable"] = b.Writable,
                    ["baseline"] = ToStringArray(b.Baseline),
                    ["map"] = mp,
                    ["name"] = b.Name
                });
            }

            var intents = new JsonObject();
            foreach (var (ikey, it) in col.Intents)
            {
                var iObj = new JsonObject
                {
                    ["present"] = it.Present,
                    ["pending"] = it.Pending,
                    ["origin"] = it.Origin
                };
                if (it.Acknowledged is not null)
                    iObj["acknowledged"] = ToStringArray(it.Acknowledged);
                intents[ikey] = iObj;
            }

            var cObj = new JsonObject
            {
                ["id"] = col.CollectionId,
                ["name"] = col.Name,
                ["kind"] = col.Kind,
                ["revision"] = col.Revision,
                ["manualOrder"] = col.ManualOrder,
                ["order"] = ToStringArray(col.Order),
                ["bindings"] = bindings,
                ["intents"] = intents
            };
            if (col.Deleted) cObj["deleted"] = true;
            if (col.LocallyCreated) cObj["locallyCreated"] = true;
            collections.Add(cObj);
        }

        var networkPlaylists = new JsonArray();
        foreach (var np in rec.NetworkPlaylists)
        {
            JsonObject? subscription = null;
            if (np.SubAccountId is not null)
            {
                var locator = new JsonObject();
                SetNonNull(locator, "listId", np.SubListId);
                SetNonNull(locator, "creatorId", np.SubCreatorId);
                SetNonNull(locator, "creatorListId", np.SubCreatorListId);
                subscription = new JsonObject
                {
                    ["accountId"] = np.SubAccountId,
                    ["present"] = np.SubPresent ?? false,
                    ["locator"] = locator
                };
            }
            JsonObject? intent = np.IntentAccountId is not null
                ? new JsonObject { ["accountId"] = np.IntentAccountId, ["present"] = np.IntentPresent ?? false }
                : null;
            networkPlaylists.Add(new JsonObject
            {
                ["id"] = np.PlaylistId,
                ["platform"] = np.Platform,
                ["remoteId"] = np.RemoteId,
                ["name"] = np.Name,
                ["cover"] = np.Cover,
                ["count"] = np.SongCount,
                ["revision"] = np.Revision,
                ["present"] = np.Present,
                ["subscription"] = subscription,
                ["intent"] = intent
            });
        }

        return new JsonObject
        {
            ["schema"] = 1,
            ["scope"] = scope,
            ["revision"] = rec.StateRevision,
            ["library"] = new JsonObject
            {
                ["version"] = 1,
                ["ownerId"] = rec.OwnerId,
                ["revision"] = rec.LibraryRevision,
                ["nextId"] = rec.NextId,
                ["collections"] = collections,
                ["accounts"] = accounts,
                ["networkPlaylists"] = networkPlaylists
            }
        };
    }

    internal static MusicLibraryAreaRecord LibraryFromJson(string scope, JsonObject value)
    {
        var lib = value["library"] as JsonObject ?? new JsonObject();
        long stateRev = Int64(value, "revision") ?? 0;
        string ownerId = Str(lib, "ownerId") ?? scope;
        long libRev = Int64(lib, "revision") ?? stateRev;
        long nextId = Int64(lib, "nextId") ?? 1;

        var accounts = new Dictionary<string, string>(StringComparer.Ordinal);
        if (lib["accounts"] is JsonObject accObj)
            foreach (var pair in accObj)
                if (pair.Value is JsonValue v && v.TryGetValue<string>(out var accId))
                    accounts[pair.Key] = accId;

        var collections = new List<MusicCollectionRecord>();
        if (lib["collections"] is JsonArray colArr)
        {
            foreach (var item in colArr)
            {
                if (item is not JsonObject col) continue;
                var order = ReadStringArray(col["order"] as JsonArray);
                var bindings = new List<MusicCollectionBindingRecord>();
                if (col["bindings"] is JsonArray bArr)
                {
                    foreach (var bItem in bArr)
                    {
                        if (bItem is not JsonObject b) continue;
                        var baseline = ReadStringArray(b["baseline"] as JsonArray);
                        var map = new Dictionary<string, string>(StringComparer.Ordinal);
                        if (b["map"] is JsonObject mapObj)
                            foreach (var pair in mapObj)
                                if (pair.Value is JsonValue mv && mv.TryGetValue<string>(out var sid))
                                    map[pair.Key] = sid;
                        bindings.Add(new MusicCollectionBindingRecord(
                            Str(b, "platform")!,
                            Str(b, "accountId")!,
                            Str(b, "id")!,
                            Bool(b, "writable") ?? false,
                            Str(b, "name") ?? "",
                            baseline,
                            map));
                    }
                }

                var intents = new Dictionary<string, MusicCollectionIntentRecord>(StringComparer.Ordinal);
                if (col["intents"] is JsonObject intObj)
                {
                    foreach (var pair in intObj)
                    {
                        if (pair.Value is not JsonObject it) continue;
                        string[]? acks = it["acknowledged"] is JsonArray ackArr ? ReadStringArray(ackArr) : null;
                        intents[pair.Key] = new MusicCollectionIntentRecord(
                            Bool(it, "present") ?? false,
                            Bool(it, "pending") ?? false,
                            Str(it, "origin") ?? "local",
                            acks);
                    }
                }

                collections.Add(new MusicCollectionRecord(
                    Str(col, "id")!,
                    Str(col, "name") ?? "",
                    Str(col, "kind") ?? "playlist",
                    Int64(col, "revision") ?? 0,
                    Bool(col, "manualOrder") ?? false,
                    Bool(col, "deleted") ?? false,
                    Bool(col, "locallyCreated") ?? false,
                    order,
                    bindings.ToArray(),
                    intents));
            }
        }

        var networkPlaylists = new List<MusicNetworkPlaylistRecord>();
        if (lib["networkPlaylists"] is JsonArray npArr)
        {
            foreach (var item in npArr)
            {
                if (item is not JsonObject np) continue;
                var sub = np["subscription"] as JsonObject;
                var loc = sub?["locator"] as JsonObject;
                var intent = np["intent"] as JsonObject;
                networkPlaylists.Add(new MusicNetworkPlaylistRecord(
                    Str(np, "id")!,
                    Str(np, "platform")!,
                    Str(np, "remoteId")!,
                    Str(np, "name")!,
                    Str(np, "cover") ?? "",
                    Int64(np, "count") ?? 0,
                    Int64(np, "revision") ?? 0,
                    Bool(np, "present") ?? true,
                    Str(sub, "accountId"),
                    Bool(sub, "present"),
                    Str(loc, "listId"),
                    Str(loc, "creatorId"),
                    Str(loc, "creatorListId"),
                    Str(intent, "accountId"),
                    Bool(intent, "present")));
            }
        }

        return new MusicLibraryAreaRecord(
            stateRev, ownerId, libRev, nextId,
            accounts, collections.ToArray(), networkPlaylists.ToArray());
    }

    // ---- Queue ----

    internal static JsonObject ToJson(MusicQueueStateRecord rec)
    {
        var entries = new JsonArray();
        foreach (var e in rec.Entries)
            entries.Add(ToJson(e));
        return new JsonObject
        {
            ["schema"] = 1,
            ["entries"] = entries,
            ["currentId"] = rec.CurrentId,
            ["priority"] = ToStringArray(rec.Priority),
            ["source"] = ToJson(rec.Source)
        };
    }

    internal static JsonObject ToJson(MusicQueueEntryRecord e) => new()
    {
        ["id"] = e.EntryId,
        ["songId"] = e.SongId,
        ["context"] = ToJson(e.Context)
    };

    internal static MusicQueueStateRecord QueueFromJson(JsonObject value)
    {
        var entries = new List<MusicQueueEntryRecord>();
        if (value["entries"] is JsonArray entriesArr)
            foreach (var item in entriesArr)
                if (item is JsonObject e)
                    entries.Add(new MusicQueueEntryRecord(
                        Str(e, "id")!, Str(e, "songId")!, ContextFromJson(e["context"] as JsonObject)));

        return new MusicQueueStateRecord(
            Str(value, "currentId"),
            Int64(value, "serial") ?? 0,
            Int64(value, "revision") ?? 0,
            Str(value, "runId"),
            QueueSourceFromJson(value["source"] as JsonObject),
            entries.ToArray(),
            ReadStringArray(value["priority"] as JsonArray));
    }

    // ---- History ----

    internal static JsonObject ToJson(MusicHistoryAreaRecord area)
    {
        var playlists = new JsonArray();
        foreach (var pl in area.Playlists) playlists.Add(ToJson(pl));
        var days = new JsonArray();
        foreach (var d in area.Days) days.Add(ToJson(d));
        return new JsonObject
        {
            ["schema"] = 1,
            ["playlists"] = playlists,
            ["days"] = days
        };
    }

    internal static JsonObject ToJson(MusicHistoryPlaylistSummaryRecord sum) => new()
    {
        ["id"] = sum.PlaylistId,
        ["at"] = sum.PlayedAtMs,
        ["title"] = sum.Title,
        ["currentId"] = sum.CurrentId,
        ["source"] = ToJson(sum.Source)
    };

    internal static JsonObject ToJson(MusicHistoryPlaylistRecord pl)
    {
        var entries = new JsonArray();
        foreach (var e in pl.Entries) entries.Add(ToJson(e));
        return new JsonObject
        {
            ["id"] = pl.PlaylistId,
            ["title"] = pl.Title,
            ["at"] = pl.PlayedAtMs,
            ["currentId"] = pl.CurrentId,
            ["entries"] = entries,
            ["priority"] = ToStringArray(pl.Priority),
            ["history"] = ToStringArray(pl.History),
            ["source"] = ToJson(pl.Source)
        };
    }

    internal static MusicHistoryPlaylistRecord HistoryPlaylistFromJson(string fallbackId, JsonObject pl)
    {
        string pid = Str(pl, "id") ?? fallbackId;
        var entries = new List<MusicQueueEntryRecord>();
        if (pl["entries"] is JsonArray arr)
            foreach (var item in arr)
                if (item is JsonObject e)
                    entries.Add(new MusicQueueEntryRecord(
                        Str(e, "id")!, Str(e, "songId")!, ContextFromJson(e["context"] as JsonObject)));
        return new MusicHistoryPlaylistRecord(
            pid,
            Int64(pl, "at") ?? 0,
            Str(pl, "title") ?? "",
            Str(pl, "currentId"),
            QueueSourceFromJson(pl["source"] as JsonObject),
            ContextFromJson(pl["context"] as JsonObject),
            entries.ToArray(),
            ReadStringArray(pl["priority"] as JsonArray),
            ReadStringArray(pl["history"] as JsonArray));
    }

    internal static JsonObject ToJson(MusicHistoryDaySummaryRecord sum) => new()
    {
        ["date"] = sum.DayDate
    };

    internal static JsonObject ToJson(MusicHistoryDayRecord day)
    {
        var songs = new JsonArray();
        foreach (var s in day.Songs) songs.Add(ToJson(s));
        return new JsonObject
        {
            ["date"] = day.DayDate,
            ["songs"] = songs
        };
    }

    internal static JsonObject ToJson(MusicHistoryDaySongRecord s) => new()
    {
        ["songId"] = s.SongId,
        ["at"] = s.PlayedAtMs,
        ["context"] = ToJson(s.Context)
    };

    internal static MusicHistoryDayRecord HistoryDayFromJson(string fallbackDate, JsonObject day)
    {
        string date = Str(day, "date") ?? fallbackDate;
        var songs = new List<MusicHistoryDaySongRecord>();
        if (day["songs"] is JsonArray arr)
            foreach (var item in arr)
                if (item is JsonObject s)
                    songs.Add(new MusicHistoryDaySongRecord(
                        Str(s, "songId")!, Int64(s, "at") ?? 0, ContextFromJson(s["context"] as JsonObject)));
        long updatedAt = Int64(day, "at") ?? (songs.Count > 0 ? songs[0].PlayedAtMs : 0);
        return new MusicHistoryDayRecord(date, Str(day, "id") ?? date, updatedAt, songs.ToArray());
    }

    internal static MusicHistoryAreaRecord HistoryAreaFromJson(JsonObject value)
    {
        var playlists = new List<MusicHistoryPlaylistRecord>();
        if (value["playlists"] is JsonArray plArr)
            foreach (var item in plArr)
                if (item is JsonObject pl)
                    playlists.Add(HistoryPlaylistFromJson(Str(pl, "id") ?? "", pl));
        var days = new List<MusicHistoryDayRecord>();
        if (value["days"] is JsonArray dArr)
            foreach (var item in dArr)
                if (item is JsonObject d)
                    days.Add(HistoryDayFromJson(Str(d, "date") ?? "", d));
        return new MusicHistoryAreaRecord(playlists.ToArray(), days.ToArray());
    }

    // ---- Settings & Sync ----

    internal static JsonObject ToJson(MusicPlaybackModeRecord rec) => new() { ["schema"] = 1, ["mode"] = rec.Mode };
    internal static MusicPlaybackModeRecord PlaybackModeFromJson(JsonObject value) => new(Int64(value, "mode") ?? 1);

    internal static JsonObject ToJson(MusicOnlineSettingsRecord rec) =>
        new() { ["schema"] = 1, ["revision"] = rec.Revision, ["lyrics"] = rec.LyricsEnabled };
    internal static MusicOnlineSettingsRecord OnlineSettingsFromJson(JsonObject value) =>
        new(Int64(value, "revision") ?? 0, Bool(value, "lyrics") ?? true);

    internal static JsonObject ToJson(MusicDeviceSettingsRecord rec) =>
        new() { ["schema"] = 1, ["revision"] = rec.Revision, ["volume"] = rec.Volume };
    internal static MusicDeviceSettingsRecord DeviceSettingsFromJson(JsonObject value) =>
        new(Int64(value, "revision") ?? 0, Int64(value, "volume") ?? 100);

    internal static JsonObject ToJson(MusicAppearanceRecord rec) => new()
    {
        ["schema"] = 1,
        ["revision"] = rec.Revision,
        ["playlistView"] = rec.PlaylistView,
        ["playerTheme"] = rec.PlayerTheme,
        ["colorMode"] = rec.ColorMode,
        ["colorPalette"] = rec.ColorPalette
    };
    internal static MusicAppearanceRecord AppearanceFromJson(JsonObject value) => new(
        Int64(value, "revision") ?? 0,
        Str(value, "playlistView")!,
        Str(value, "playerTheme")!,
        Str(value, "colorMode")!,
        Str(value, "colorPalette")!);

    internal static JsonObject ToJson(MusicCatalogSourceRecord rec) =>
        new() { ["schema"] = 1, ["revision"] = rec.Revision, ["platform"] = rec.Platform };
    internal static MusicCatalogSourceRecord CatalogSourceFromJson(JsonObject value) =>
        new(Int64(value, "revision") ?? 0, Str(value, "platform")!);

    internal static JsonObject ToJson(MusicDesktopLyricsRecord rec)
    {
        var obj = new JsonObject
        {
            ["schema"] = 1,
            ["revision"] = rec.Revision,
            ["layout"] = rec.Layout,
            ["normalColor"] = rec.NormalColor,
            ["highlightColor"] = rec.HighlightColor
        };
        if (rec.Geometry is { } g)
        {
            obj["geometry"] = new JsonObject
            {
                ["monitor"] = g.Monitor,
                ["x"] = g.X,
                ["y"] = g.Y,
                ["width"] = g.Width,
                ["height"] = g.Height
            };
        }
        return obj;
    }

    internal static MusicDesktopLyricsRecord DesktopLyricsFromJson(JsonObject value)
    {
        MusicDesktopLyricsGeometryRecord? geom = value["geometry"] is JsonObject g && Str(g, "monitor") is { } mon
            ? new MusicDesktopLyricsGeometryRecord(
                mon, Dbl(g, "x") ?? 0, Dbl(g, "y") ?? 0, Dbl(g, "width") ?? 0, Dbl(g, "height") ?? 0)
            : null;
        return new MusicDesktopLyricsRecord(
            Int64(value, "revision") ?? 0,
            Str(value, "layout")!,
            Str(value, "normalColor")!,
            Str(value, "highlightColor")!,
            geom);
    }

    internal static JsonObject ToJson(MusicQualityPolicyRecord rec) => new()
    {
        ["schema"] = 1,
        ["revision"] = rec.Revision,
        ["request"] = rec.Request,
        ["qualityOrder"] = ToStringArray(rec.QualityOrder),
        ["qualityEnabled"] = ToStringArray(rec.QualityEnabled),
        ["spatialOrder"] = ToStringArray(rec.SpatialOrder),
        ["spatialEnabled"] = ToStringArray(rec.SpatialEnabled)
    };

    internal static MusicQualityPolicyRecord QualityPolicyFromJson(JsonObject value) => new(
        Int64(value, "revision") ?? 0,
        Str(value, "request")!,
        ReadStringArray(value["qualityOrder"] as JsonArray),
        ReadStringArray(value["qualityEnabled"] as JsonArray),
        ReadStringArray(value["spatialOrder"] as JsonArray),
        ReadStringArray(value["spatialEnabled"] as JsonArray));

    internal static JsonObject ToJson(MusicOfficialSourcesRecord rec)
    {
        var obj = new JsonObject
        {
            ["schema"] = 1,
            ["revision"] = rec.Revision,
            ["enabled"] = ToStringArray(rec.Enabled)
        };
        if (rec.Platforms.Length > 0) obj["platforms"] = ToStringArray(rec.Platforms);
        return obj;
    }

    internal static MusicOfficialSourcesRecord OfficialSourcesFromJson(JsonObject value) => new(
        Int64(value, "revision") ?? 0,
        ReadStringArray(value["platforms"] as JsonArray),
        ReadStringArray(value["enabled"] as JsonArray));

    internal static JsonObject ToJson(MusicMetadataPolicyRecord rec) => new()
    {
        ["schema"] = 1,
        ["revision"] = rec.Revision,
        ["platforms"] = ToStringArray(rec.Platforms),
        ["enabled"] = ToStringArray(rec.Enabled)
    };

    internal static MusicMetadataPolicyRecord MetadataPolicyFromJson(JsonObject value) => new(
        Int64(value, "revision") ?? 0,
        ReadStringArray(value["platforms"] as JsonArray),
        ReadStringArray(value["enabled"] as JsonArray));

    internal static JsonObject ToJson(MusicSearchPolicyRecord rec) => new()
    {
        ["schema"] = 1,
        ["revision"] = rec.Revision,
        ["songs"] = new JsonObject
        {
            ["order"] = ToStringArray(rec.Songs.Order),
            ["enabled"] = ToStringArray(rec.Songs.Enabled),
            ["pages"] = rec.Songs.Pages,
            ["concurrency"] = rec.Songs.Concurrency,
            ["cacheMinutes"] = rec.Songs.CacheMinutes
        },
        ["playlists"] = new JsonObject
        {
            ["order"] = ToStringArray(rec.Playlists.Order),
            ["enabled"] = ToStringArray(rec.Playlists.Enabled),
            ["pages"] = rec.Playlists.Pages,
            ["concurrency"] = rec.Playlists.Concurrency,
            ["cacheMinutes"] = rec.Playlists.CacheMinutes
        }
    };

    internal static MusicSearchPolicyRecord SearchPolicyFromJson(JsonObject value)
    {
        static MusicSearchKindRecord Kind(JsonObject? obj) => new(
            ReadStringArray(obj?["order"] as JsonArray),
            ReadStringArray(obj?["enabled"] as JsonArray),
            Int64(obj, "pages") ?? 1,
            Int64(obj, "concurrency") ?? 1,
            Int64(obj, "cacheMinutes") ?? 60);
        return new MusicSearchPolicyRecord(
            Int64(value, "revision") ?? 0,
            Kind(value["songs"] as JsonObject),
            Kind(value["playlists"] as JsonObject));
    }

    internal static JsonObject ToJson(MusicSearchHistoryRecord rec)
    {
        var items = new JsonArray();
        foreach (var item in rec.Items)
            items.Add(new JsonObject { ["text"] = item.Text, ["kind"] = item.Kind });
        return new JsonObject { ["schema"] = 1, ["revision"] = rec.Revision, ["items"] = items };
    }

    internal static MusicSearchHistoryRecord SearchHistoryFromJson(JsonObject value)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var items = new List<MusicSearchHistoryItemRecord>();
        if (value["items"] is JsonArray arr)
            foreach (var item in arr)
                if (item is JsonObject obj)
                    items.Add(new MusicSearchHistoryItemRecord(Str(obj, "text")!, Str(obj, "kind")!, now));
        return new MusicSearchHistoryRecord(Int64(value, "revision") ?? 0, items.ToArray());
    }

    internal static JsonObject ToJson(MusicSourceExclusionRecord rec) => new()
    {
        ["songId"] = rec.SongId,
        ["provider"] = rec.Provider,
        ["platform"] = rec.Platform,
        ["providerName"] = rec.ProviderName,
        ["title"] = rec.Title,
        ["artist"] = rec.Artist,
        ["at"] = rec.ExcludedAtMs
    };

    internal static MusicSourceExclusionRecord SourceExclusionFromJson(string exclusionId, int ordinal, JsonObject obj) => new(
        exclusionId,
        ordinal,
        Str(obj, "songId")!,
        Str(obj, "provider")!,
        Str(obj, "platform")!,
        Str(obj, "providerName") ?? "",
        Str(obj, "title") ?? "",
        Str(obj, "artist") ?? "",
        Int64(obj, "at") ?? 0);

    internal static JsonObject ToJson(MusicLocalLyricSummaryRecord sum) => new()
    {
        ["songId"] = sum.SongId,
        ["contentHash"] = sum.ContentHash
    };

    internal static JsonObject ToJson(MusicLocalLyricRecord rec)
    {
        var lines = new JsonArray();
        foreach (var line in rec.Lines)
        {
            var words = new JsonArray();
            foreach (var word in line.Words)
            {
                var w = new JsonObject
                {
                    ["time"] = word.TimeSec,
                    ["text"] = word.Text,
                    ["duration"] = word.DurationSec
                };
                if (word.OffsetSec is { } off) w["offset"] = off;
                words.Add(w);
            }
            var l = new JsonObject { ["time"] = line.TimeSec, ["text"] = line.Text };
            SetNonNull(l, "translation", line.Translation);
            SetNonNull(l, "romanization", line.Romanization);
            SetNonNull(l, "end", line.EndSec);
            l["words"] = words;
            lines.Add(l);
        }
        return new JsonObject { ["songId"] = rec.SongId, ["contentHash"] = rec.ContentHash, ["lines"] = lines };
    }

    internal static MusicLocalLyricRecord LocalLyricFromJson(string fallbackSongId, int ordinal, JsonObject obj)
    {
        string songId = Str(obj, "songId") ?? fallbackSongId;
        string hash = Str(obj, "contentHash")!;
        var lines = new List<MusicLyricLineRecord>();
        if (obj["lines"] is JsonArray linesArr)
        {
            foreach (var item in linesArr)
            {
                if (item is not JsonObject line) continue;
                var words = new List<MusicLyricWordRecord>();
                if (line["words"] is JsonArray wordsArr)
                    foreach (var wItem in wordsArr)
                        if (wItem is JsonObject w)
                            words.Add(new MusicLyricWordRecord(
                                Dbl(w, "time") ?? 0d,
                                Str(w, "text") ?? "",
                                Dbl(w, "duration") ?? 0d,
                                Dbl(w, "offset")));
                lines.Add(new MusicLyricLineRecord(
                    Dbl(line, "time") ?? 0d,
                    Str(line, "text") ?? "",
                    Str(line, "translation"),
                    Str(line, "romanization"),
                    Dbl(line, "end"),
                    words.ToArray()));
            }
        }
        return new MusicLocalLyricRecord(songId, ordinal, hash, lines.ToArray());
    }

    internal static JsonObject ToJson(MusicLibrarySyncStateRecord rec)
    {
        var platforms = new JsonObject();
        foreach (var (plat, entry) in rec.Platforms)
            platforms[plat] = new JsonObject
            {
                ["favorites"] = entry.Favorites,
                ["playlists"] = entry.Playlists,
                ["accountId"] = entry.AccountId
            };

        JsonObject? pending = null;
        if (rec.Pending is { } p)
        {
            if (p.Operation is "subscribe" or "unsubscribe")
            {
                var locator = new JsonObject();
                SetNonNull(locator, "listId", p.LocatorListId);
                SetNonNull(locator, "creatorId", p.LocatorCreatorId);
                SetNonNull(locator, "creatorListId", p.LocatorCreatorListId);
                pending = new JsonObject
                {
                    ["platform"] = p.Platform,
                    ["accountId"] = p.AccountId,
                    ["collectionId"] = p.CollectionId,
                    ["remoteId"] = p.RemoteId,
                    ["operation"] = p.Operation,
                    ["beforePresent"] = p.BeforePresent ?? false,
                    ["desiredPresent"] = p.DesiredPresent ?? false,
                    ["locator"] = locator,
                    ["name"] = p.Name
                };
            }
            else
            {
                pending = new JsonObject
                {
                    ["platform"] = p.Platform,
                    ["accountId"] = p.AccountId,
                    ["collectionId"] = p.CollectionId,
                    ["remoteId"] = p.RemoteId,
                    ["operation"] = p.Operation,
                    ["before"] = ToStringArray(p.Before),
                    ["desired"] = ToStringArray(p.Desired),
                    ["ids"] = ToStringArray(p.Ids),
                    ["name"] = p.Name,
                    ["directory"] = ToStringArray(p.Directory)
                };
            }
        }

        JsonObject? confirmation = rec.Confirmation is { } c
            ? new JsonObject { ["token"] = c.Token, ["code"] = c.Code, ["count"] = c.Count } : null;
        JsonObject? approved = rec.Approved is { } a
            ? new JsonObject { ["token"] = a.Token, ["code"] = a.Code, ["count"] = a.Count } : null;

        var issues = new JsonArray();
        foreach (var iss in rec.Issues)
            issues.Add(new JsonObject
            {
                ["platform"] = iss.Platform,
                ["collectionId"] = iss.CollectionId,
                ["code"] = iss.Code
            });

        return new JsonObject
        {
            ["schema"] = 1,
            ["revision"] = rec.Revision,
            ["platforms"] = platforms,
            ["pending"] = pending,
            ["confirmation"] = confirmation,
            ["approved"] = approved,
            ["status"] = new JsonObject
            {
                ["phase"] = rec.StatusPhase,
                ["code"] = rec.StatusCode,
                ["at"] = rec.StatusAtMs,
                ["issues"] = issues
            }
        };
    }

    internal static MusicLibrarySyncStateRecord LibrarySyncFromJson(JsonObject value)
    {
        var platforms = new Dictionary<string, MusicSyncPlatformRecord>(StringComparer.Ordinal);
        if (value["platforms"] is JsonObject platsObj)
            foreach (var pair in platsObj)
                if (pair.Value is JsonObject p)
                    platforms[pair.Key] = new MusicSyncPlatformRecord(
                        Bool(p, "favorites") ?? false,
                        Bool(p, "playlists") ?? false,
                        Str(p, "accountId") ?? "");

        MusicSyncPendingRecord? pending = null;
        if (value["pending"] is JsonObject po && Str(po, "operation") is { } op)
        {
            var loc = po["locator"] as JsonObject;
            pending = new MusicSyncPendingRecord(
                Str(po, "platform") ?? "",
                Str(po, "accountId") ?? "",
                Str(po, "collectionId") ?? "",
                Str(po, "remoteId") ?? "",
                op,
                Bool(po, "beforePresent"),
                Bool(po, "desiredPresent"),
                Str(po, "name") ?? "",
                Str(loc, "listId"),
                Str(loc, "creatorId"),
                Str(loc, "creatorListId"),
                ReadStringArray(po["before"] as JsonArray),
                ReadStringArray(po["desired"] as JsonArray),
                ReadStringArray(po["ids"] as JsonArray),
                ReadStringArray(po["directory"] as JsonArray));
        }

        MusicSyncConfirmationRecord? confirmation = value["confirmation"] is JsonObject co && Str(co, "token") is { } ct
            ? new MusicSyncConfirmationRecord(ct, Str(co, "code") ?? "", Int64(co, "count") ?? 0) : null;
        MusicSyncConfirmationRecord? approved = value["approved"] is JsonObject ao && Str(ao, "token") is { } at
            ? new MusicSyncConfirmationRecord(at, Str(ao, "code") ?? "", Int64(ao, "count") ?? 0) : null;

        var status = value["status"] as JsonObject;
        var issues = new List<MusicSyncIssueRecord>();
        if (status?["issues"] is JsonArray issArr)
            foreach (var item in issArr)
                if (item is JsonObject iss)
                    issues.Add(new MusicSyncIssueRecord(
                        Str(iss, "platform") ?? "",
                        Str(iss, "collectionId") ?? Str(iss, "collectionID") ?? "",
                        Str(iss, "code") ?? ""));

        return new MusicLibrarySyncStateRecord(
            Int64(value, "revision") ?? 0,
            platforms,
            pending,
            confirmation,
            approved,
            Str(status, "phase") ?? "idle",
            Int64(status, "at") ?? 0,
            Str(status, "code") ?? "",
            issues.ToArray());
    }
}
