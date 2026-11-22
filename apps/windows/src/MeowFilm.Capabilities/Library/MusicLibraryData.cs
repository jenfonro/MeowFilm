using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

internal sealed record MusicLibrarySelectedRow(object?[] Key, int Ordinal, JsonObject Value);
internal sealed record PendingRowChange(string Area, string[] Relations, object?[] Key, JsonObject? Value, int Ordinal, bool Insert = false);

/// <summary>One command's copy-on-write control records and isolated song changes.</summary>
internal sealed class MusicLibraryData
{
    private readonly IReadOnlyDictionary<string, JsonNode?> original;
    private readonly Dictionary<string, JsonNode?> fetched = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonNode?> accessed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> assigned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> loadedTypedAreas;
    private readonly Dictionary<string, MusicHistoryPlaylistRecord> loadedHistoryPlaylists = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MusicHistoryDayRecord> loadedHistoryDays = new(StringComparer.Ordinal);
    private readonly HashSet<string> knownSongs;
    private readonly bool writable;
    private readonly string scope;
    private readonly MusicLibraryRevision revision;
    private readonly List<PendingRowChange> rows = [];
    private readonly HashSet<string> partialAreas = new(StringComparer.Ordinal);
    internal MusicSongSession Songs { get; }

    internal MusicLibraryData(IMusicLibraryStore store, IReadOnlyDictionary<string, JsonNode?> areas, bool writable = false,
        HashSet<string>? knownSongs = null, string scope = "local", MusicLibraryRevision? revision = null,
        Dictionary<string, object?>? loadedTypedAreas = null)
    {
        this.scope = scope;
        this.revision = revision ?? MusicLibraryRevision.Empty;
        original = areas;
        this.writable = writable;
        this.knownSongs = knownSongs ?? new(StringComparer.Ordinal);
        this.loadedTypedAreas = loadedTypedAreas is not null
            ? new Dictionary<string, object?>(loadedTypedAreas, StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal);
        Songs = new(store, knownSongs: this.knownSongs);
    }

    internal MusicLibraryData Candidate(bool write) =>
        new(Songs.Store, PublishedAreas(), write, knownSongs, scope, revision, loadedTypedAreas);

    internal MusicLibraryData Publish(MusicLibraryRevision next, MusicLibraryCommit committed)
    {
        knownSongs.UnionWith(Songs.Changes.Keys);
        var nextTyped = new Dictionary<string, object?>(loadedTypedAreas, StringComparer.Ordinal);
        if (committed.Library is { Next: var lib }) nextTyped["library"] = lib;
        if (committed.Queue is { Next: var q }) nextTyped["queue"] = q;
        if (committed.HistoryArea is not null || committed.HistoryPlaylists.Count > 0 || committed.HistoryDays.Count > 0)
            nextTyped.Remove("history");
        if (committed.PlaybackMode is { } mode) nextTyped["playback-settings"] = mode;
        if (committed.OnlineSettings is { } online) nextTyped["online-settings"] = online;
        if (committed.DeviceSettings is { } device) nextTyped["native-device-settings"] = device;
        if (committed.Appearance is { } app) nextTyped["native-appearance"] = app;
        if (committed.CatalogSource is { } cat) nextTyped["native-catalog-source"] = cat;
        if (committed.DesktopLyrics is { } dl) nextTyped["native-desktop-lyrics"] = dl;
        if (committed.QualityPolicy is { } qp) nextTyped["native-quality-policy"] = qp;
        if (committed.OfficialSources is { } os) nextTyped["native-official-sources"] = os;
        if (committed.MetadataPolicy is { } mp) nextTyped["native-metadata-policy"] = mp;
        if (committed.SearchPolicy is { } sp) nextTyped["native-search-policy"] = sp;
        if (committed.SearchHistory is { } sh) nextTyped["native-search-history"] = sh;
        if (committed.SourceExclusionsRevision is not null || committed.ReplaceSourceExclusions is not null || committed.SourceExclusions.Count > 0)
            nextTyped.Remove("native-source-exclusions");
        if (committed.ReplaceLocalLyrics is not null || committed.LocalLyrics.Count > 0)
            nextTyped.Remove("native-local-lyrics");
        if (committed.LibrarySync is { } sync) nextTyped["library-sync"] = sync;

        return new(Songs.Store, PublishedAreas(), knownSongs: knownSongs, scope: scope, revision: next, loadedTypedAreas: nextTyped);
    }

    internal JsonNode? Value(string area)
    {
        if (assigned.TryGetValue(area, out var assignedValue)) return assignedValue;
        if (accessed.TryGetValue(area, out var value)) return value;
        if (!original.TryGetValue(area, out value))
        {
            value = LoadAreaJson(area, members: null);
            fetched[area] = value;
        }
        if (!writable) return accessed[area] = value;
        return accessed[area] = value?.DeepClone();
    }

    internal bool HasValue(string area) => Value(area) is not null;
    internal bool IsLoaded(string area) => assigned.ContainsKey(area) || accessed.ContainsKey(area) || original.ContainsKey(area);

    internal JsonObject? Select(string area, params string[] members)
    {
        if (!IsLoaded(area)) return LoadAreaJson(area, members);
        var value = Value(area) as JsonObject;
        return value is null ? null : new JsonObject(value.Where(pair => members.Contains(pair.Key))
            .Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone())));
    }

    internal IReadOnlyList<MusicLibrarySelectedRow> Rows(string area, string[] relations, object?[] prefix, params string[] members)
    {
        var store = Songs.Store;
        if (area == "history")
        {
            if (relations is ["playlists"])
            {
                string? pid = prefix.Length == 2 ? (string)prefix[1]! : null;
                return store.ReadHistoryPlaylistSummaries(scope, revision, pid)
                    .Select(s => new MusicLibrarySelectedRow(
                        [scope, s.PlaylistId], s.Ordinal,
                        MusicLibraryProjection.SelectMembers(MusicLibraryProjection.ToJson(s), members)!))
                    .ToArray();
            }
            if (relations is ["playlists", "entries"])
            {
                string pid = (string)prefix[1]!;
                string? eid = prefix.Length == 3 ? (string)prefix[2]! : null;
                var entries = store.ReadHistoryPlaylistEntries(scope, revision, pid, eid);
                return entries.Select((e, idx) => new MusicLibrarySelectedRow(
                    [scope, pid, e.EntryId], idx,
                    MusicLibraryProjection.SelectMembers(new JsonObject { ["id"] = e.EntryId, ["songId"] = e.SongId }, members)!))
                    .ToArray();
            }
            if (relations is ["days"])
            {
                string? date = prefix.Length == 2 ? (string)prefix[1]! : null;
                return store.ReadHistoryDaySummaries(scope, revision, date)
                    .Select(s => new MusicLibrarySelectedRow(
                        [scope, s.DayDate], s.Ordinal,
                        MusicLibraryProjection.SelectMembers(MusicLibraryProjection.ToJson(s), members)!))
                    .ToArray();
            }
            if (relations is ["days", "songs"])
            {
                string date = (string)prefix[1]!;
                string? sid = prefix.Length == 3 ? (string)prefix[2]! : null;
                var songs = store.ReadHistoryDaySongs(scope, revision, date, sid);
                return songs.Select((s, idx) => new MusicLibrarySelectedRow(
                    [scope, date, s.SongId], idx,
                    MusicLibraryProjection.SelectMembers(new JsonObject { ["songId"] = s.SongId, ["at"] = s.PlayedAtMs }, members)!))
                    .ToArray();
            }
        }
        if (area == "native-source-exclusions" && relations is ["items"])
        {
            string? key = prefix.Length == 2 ? (string)prefix[1]! : null;
            return store.ReadSourceExclusions(scope, revision, key)
                .Select(item => new MusicLibrarySelectedRow(
                    [scope, item.ExclusionId], item.Ordinal,
                    MusicLibraryProjection.SelectMembers(MusicLibraryProjection.ToJson(item), members)!))
                .ToArray();
        }
        if (area == "native-local-lyrics" && relations is ["items"])
        {
            string? sid = prefix.Length == 2 ? (string)prefix[1]! : null;
            return store.ReadLocalLyricSummaries(scope, revision, sid)
                .Select(item => new MusicLibrarySelectedRow(
                    [scope, item.SongId], item.Ordinal,
                    MusicLibraryProjection.SelectMembers(MusicLibraryProjection.ToJson(item), members)!))
                .ToArray();
        }
        throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
    }

    internal JsonObject Row(string area, string[] relations, object?[] key) =>
        TryReadRow(area, relations, key) ?? throw new MusicLibraryException("CORRUPT_STORAGE");

    private JsonObject? TryReadRow(string area, string[] relations, object?[] key)
    {
        var store = Songs.Store;
        if (area == "history")
        {
            if (relations is ["playlists"])
            {
                string pid = (string)key[1]!;
                var rec = store.ReadHistoryPlaylist(scope, revision, pid);
                if (rec is not null) loadedHistoryPlaylists[pid] = rec;
                return rec is null ? null : MusicLibraryProjection.ToJson(rec);
            }
            if (relations is ["playlists", "entries"])
            {
                var entry = store.ReadHistoryPlaylistEntries(scope, revision, (string)key[1]!, (string)key[2]!).FirstOrDefault();
                return entry is null ? null : MusicLibraryProjection.ToJson(entry);
            }
            if (relations is ["days"])
            {
                string date = (string)key[1]!;
                var rec = store.ReadHistoryDay(scope, revision, date);
                if (rec is not null) loadedHistoryDays[date] = rec;
                return rec is null ? null : MusicLibraryProjection.ToJson(rec);
            }
            if (relations is ["days", "songs"])
            {
                var song = store.ReadHistoryDaySongs(scope, revision, (string)key[1]!, (string)key[2]!).FirstOrDefault();
                return song is null ? null : MusicLibraryProjection.ToJson(song);
            }
        }
        if (area == "native-source-exclusions" && relations is ["items"])
        {
            var item = store.ReadSourceExclusions(scope, revision, (string)key[1]!).FirstOrDefault();
            return item is null ? null : MusicLibraryProjection.ToJson(item);
        }
        if (area == "native-local-lyrics" && relations is ["items"])
        {
            var item = store.ReadLocalLyric(scope, revision, (string)key[1]!);
            return item is null ? null : MusicLibraryProjection.ToJson(item);
        }
        throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
    }

    internal bool HasRelation(string area, string[] relations, object?[] key, string relation)
    {
        var store = Songs.Store;
        if (area == "history")
        {
            return (relations, relation) switch
            {
                ([], "playlists" or "days") => store.HasHistory(scope, revision),
                (["playlists"], "entries") => store.ReadHistoryPlaylistSummaries(scope, revision, (string)key[1]!).Count > 0,
                (["days"], "songs") => store.ReadHistoryDaySummaries(scope, revision, (string)key[1]!).Count > 0,
                _ => false
            };
        }
        if (area == "native-source-exclusions" && relations.Length == 0 && relation == "items")
            return store.ReadSourceExclusionsRevision(scope, revision) is not null;
        if (area == "native-local-lyrics" && relations.Length == 0 && relation == "items")
            return store.ReadLocalLyricSummaries(scope, revision).Count > 0;
        return false;
    }

    internal JsonObject SelectRow(string area, string[] relations, object?[] key, params string[] members) =>
        Rows(area, relations, key, members).SingleOrDefault()?.Value ?? throw new MusicLibraryException("CORRUPT_STORAGE");

    internal bool Matches(string area, string[] relations, object?[] key, JsonObject value)
    {
        var stored = relations.Length == 0
            ? LoadAreaJson(area, members: null)
            : TryReadRow(area, relations, key);
        return MusicLibraryProjection.MatchesSubset(stored, value);
    }

    internal string Scope => scope;

    internal void ChangeRow(string area, string[] relations, object?[] key, JsonObject? value, int ordinal, bool insert = false) =>
        rows.Add(new(area, relations, key, value, ordinal, insert));

    internal void Set(string area, JsonObject value, bool partial = false)
    {
        LJ.Require(writable, "INVALID_REQUEST");
        if (partial) partialAreas.Add(area); else partialAreas.Remove(area);
        assigned[area] = IsLoaded(area) && Value(area) is JsonObject previous
            ? LJ.Merge(previous.DeepClone().AsObject(), value)
            : value.Parent is null ? value : value.DeepClone().AsObject();
    }

    internal IReadOnlyDictionary<string, JsonObject> AreaChanges() => assigned
        .Where(pair => !(original.TryGetValue(pair.Key, out var previous) || fetched.TryGetValue(pair.Key, out previous)) || !JsonNode.DeepEquals(previous, pair.Value))
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    internal MusicLibraryCommit BuildCommit()
    {
        var changedAreas = AreaChanges();
        (MusicLibraryAreaRecord?, MusicLibraryAreaRecord)? libraryChange = null;
        (MusicQueueStateRecord?, MusicQueueStateRecord)? queueChange = null;
        MusicHistoryAreaRecord? historyArea = null;
        MusicPlaybackModeRecord? playbackMode = null;
        MusicOnlineSettingsRecord? onlineSettings = null;
        MusicDeviceSettingsRecord? deviceSettings = null;
        MusicAppearanceRecord? appearance = null;
        MusicCatalogSourceRecord? catalogSource = null;
        MusicDesktopLyricsRecord? desktopLyrics = null;
        MusicQualityPolicyRecord? qualityPolicy = null;
        MusicOfficialSourcesRecord? officialSources = null;
        MusicMetadataPolicyRecord? metadataPolicy = null;
        MusicSearchPolicyRecord? searchPolicy = null;
        MusicSearchHistoryRecord? searchHistory = null;
        long? sourceExclusionsRevision = null;
        MusicSourceExclusionRecord[]? replaceSourceExclusions = null;
        MusicLocalLyricRecord[]? replaceLocalLyrics = null;
        MusicLibrarySyncStateRecord? librarySync = null;

        foreach (var (area, value) in changedAreas)
        {
            bool partial = partialAreas.Contains(area);
            switch (area)
            {
                case "library":
                    libraryChange = (loadedTypedAreas.GetValueOrDefault("library") as MusicLibraryAreaRecord,
                        MusicLibraryProjection.LibraryFromJson(scope, value));
                    break;
                case "queue":
                    queueChange = (loadedTypedAreas.GetValueOrDefault("queue") as MusicQueueStateRecord,
                        MusicLibraryProjection.QueueFromJson(value));
                    break;
                case "history":
                    if (rows.All(r => r.Area != "history"))
                        historyArea = MusicLibraryProjection.HistoryAreaFromJson(value);
                    break;
                case "playback-settings":
                    playbackMode = MusicLibraryProjection.PlaybackModeFromJson(value);
                    break;
                case "online-settings":
                    onlineSettings = MusicLibraryProjection.OnlineSettingsFromJson(value);
                    break;
                case "native-device-settings":
                    deviceSettings = MusicLibraryProjection.DeviceSettingsFromJson(value);
                    break;
                case "native-appearance":
                    appearance = MusicLibraryProjection.AppearanceFromJson(value);
                    break;
                case "native-catalog-source":
                    catalogSource = MusicLibraryProjection.CatalogSourceFromJson(value);
                    break;
                case "native-desktop-lyrics":
                    desktopLyrics = MusicLibraryProjection.DesktopLyricsFromJson(value);
                    break;
                case "native-quality-policy":
                    qualityPolicy = MusicLibraryProjection.QualityPolicyFromJson(value);
                    break;
                case "native-official-sources":
                    officialSources = MusicLibraryProjection.OfficialSourcesFromJson(value);
                    break;
                case "native-metadata-policy":
                    metadataPolicy = MusicLibraryProjection.MetadataPolicyFromJson(value);
                    break;
                case "native-search-policy":
                    searchPolicy = MusicLibraryProjection.SearchPolicyFromJson(value);
                    break;
                case "native-search-history":
                    searchHistory = MusicLibraryProjection.SearchHistoryFromJson(value);
                    break;
                case "native-source-exclusions":
                    sourceExclusionsRevision = MusicLibraryProjection.Int64(value, "revision") ?? 0;
                    if (!partial && value["items"] is JsonArray exclItems && rows.All(r => r.Area != "native-source-exclusions"))
                    {
                        var list = new List<MusicSourceExclusionRecord>();
                        for (int i = 0; i < exclItems.Count; i++)
                            if (exclItems[i] is JsonObject obj)
                                list.Add(MusicLibraryProjection.SourceExclusionFromJson(Guid.NewGuid().ToString("D"), i, obj));
                        replaceSourceExclusions = list.ToArray();
                    }
                    break;
                case "native-local-lyrics":
                    if (!partial && value["items"] is JsonArray lyrItems && rows.All(r => r.Area != "native-local-lyrics"))
                    {
                        var list = new List<MusicLocalLyricRecord>();
                        for (int i = 0; i < lyrItems.Count; i++)
                            if (lyrItems[i] is JsonObject obj)
                                list.Add(MusicLibraryProjection.LocalLyricFromJson(MusicLibraryProjection.Str(obj, "songId") ?? "", i, obj));
                        replaceLocalLyrics = list.ToArray();
                    }
                    break;
                case "library-sync":
                    librarySync = MusicLibraryProjection.LibrarySyncFromJson(value);
                    break;
                default:
                    throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
            }
        }

        var historyPlaylists = new List<MusicHistoryPlaylistMutation>();
        var historyDays = new List<MusicHistoryDayMutation>();
        var sourceExclusions = new List<MusicSourceExclusionMutation>();
        var localLyrics = new List<MusicLocalLyricMutation>();

        foreach (var row in rows)
        {
            if (row.Key[0] as string != scope)
                throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
            if (row.Area == "history" && row.Relations is ["playlists"])
            {
                string pid = row.Value is not null
                    ? MusicLibraryProjection.Str(row.Value, "id") ?? (string)row.Key[1]!
                    : (string)row.Key[1]!;
                loadedHistoryPlaylists.TryGetValue(pid, out var prevPl);
                var nextPl = row.Value is null ? null : MusicLibraryProjection.HistoryPlaylistFromJson(pid, row.Value);
                historyPlaylists.Add(new(pid, prevPl, nextPl, row.Ordinal, row.Insert));
            }
            else if (row.Area == "history" && row.Relations is ["days"])
            {
                string date = row.Value is not null
                    ? MusicLibraryProjection.Str(row.Value, "date") ?? (string)row.Key[1]!
                    : (string)row.Key[1]!;
                loadedHistoryDays.TryGetValue(date, out var prevDay);
                var nextDay = row.Value is null ? null : MusicLibraryProjection.HistoryDayFromJson(date, row.Value);
                historyDays.Add(new(date, prevDay, nextDay, row.Ordinal, row.Insert));
            }
            else if (row.Area == "native-source-exclusions" && row.Relations is ["items"])
            {
                string key = (string)row.Key[1]!;
                var nextExcl = row.Value is null ? null : MusicLibraryProjection.SourceExclusionFromJson(key, row.Ordinal, row.Value);
                sourceExclusions.Add(new(key, nextExcl, row.Ordinal));
            }
            else if (row.Area == "native-local-lyrics" && row.Relations is ["items"])
            {
                string songId = row.Value is not null
                    ? MusicLibraryProjection.Str(row.Value, "songId") ?? (string)row.Key[1]!
                    : (string)row.Key[1]!;
                var nextLyr = row.Value is null ? null : MusicLibraryProjection.LocalLyricFromJson(songId, row.Ordinal, row.Value);
                localLyrics.Add(new(songId, nextLyr, row.Ordinal));
            }
            else
            {
                throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
            }
        }

        return new MusicLibraryCommit
        {
            Songs = Songs.BuildMutations(),
            Library = libraryChange,
            Queue = queueChange,
            HistoryArea = historyArea,
            HistoryPlaylists = historyPlaylists,
            HistoryDays = historyDays,
            PlaybackMode = playbackMode,
            OnlineSettings = onlineSettings,
            DeviceSettings = deviceSettings,
            Appearance = appearance,
            CatalogSource = catalogSource,
            DesktopLyrics = desktopLyrics,
            QualityPolicy = qualityPolicy,
            OfficialSources = officialSources,
            MetadataPolicy = metadataPolicy,
            SearchPolicy = searchPolicy,
            SearchHistory = searchHistory,
            SourceExclusionsRevision = sourceExclusionsRevision,
            ReplaceSourceExclusions = replaceSourceExclusions,
            SourceExclusions = sourceExclusions,
            ReplaceLocalLyrics = replaceLocalLyrics,
            LocalLyrics = localLyrics,
            LibrarySync = librarySync
        };
    }

    internal IReadOnlyDictionary<string, JsonNode?> PublishedAreas()
    {
        var values = new Dictionary<string, JsonNode?>(original, StringComparer.Ordinal);
        foreach (var pair in accessed) values[pair.Key] = pair.Value;
        foreach (var pair in assigned)
            if (partialAreas.Contains(pair.Key)) values.Remove(pair.Key);
            else values[pair.Key] = pair.Value;
        foreach (var row in rows) values.Remove(row.Area);
        return values;
    }

    private JsonObject? LoadAreaJson(string area, string[]? members)
    {
        var store = Songs.Store;
        JsonObject? full = area switch
        {
            "library" => ReadAndTrack("library", store.ReadLibrary(scope, revision), r => MusicLibraryProjection.ToJson(scope, r)),
            "queue" => ReadAndTrack("queue", store.ReadQueue(scope, revision), MusicLibraryProjection.ToJson),
            "history" => ReadHistoryArea(store, members),
            "playback-settings" => ReadAndTrack("playback-settings", store.ReadPlaybackMode(scope, revision), MusicLibraryProjection.ToJson),
            "online-settings" => ReadAndTrack("online-settings", store.ReadOnlineSettings(scope, revision), MusicLibraryProjection.ToJson),
            "native-device-settings" => ReadAndTrack("native-device-settings", store.ReadDeviceSettings(scope, revision), MusicLibraryProjection.ToJson),
            "native-appearance" => ReadAndTrack("native-appearance", store.ReadAppearance(scope, revision), MusicLibraryProjection.ToJson),
            "native-catalog-source" => ReadAndTrack("native-catalog-source", store.ReadCatalogSource(scope, revision), MusicLibraryProjection.ToJson),
            "native-desktop-lyrics" => ReadAndTrack("native-desktop-lyrics", store.ReadDesktopLyrics(scope, revision), MusicLibraryProjection.ToJson),
            "native-quality-policy" => ReadAndTrack("native-quality-policy", store.ReadQualityPolicy(scope, revision), MusicLibraryProjection.ToJson),
            "native-official-sources" => ReadAndTrack("native-official-sources", store.ReadOfficialSources(scope, revision), MusicLibraryProjection.ToJson),
            "native-metadata-policy" => ReadAndTrack("native-metadata-policy", store.ReadMetadataPolicy(scope, revision), MusicLibraryProjection.ToJson),
            "native-search-policy" => ReadAndTrack("native-search-policy", store.ReadSearchPolicy(scope, revision), MusicLibraryProjection.ToJson),
            "native-search-history" => ReadAndTrack("native-search-history", store.ReadSearchHistory(scope, revision), MusicLibraryProjection.ToJson),
            "native-source-exclusions" => ReadSourceExclusionsArea(store, members),
            "native-local-lyrics" => ReadLocalLyricsArea(store, members),
            "library-sync" => ReadAndTrack("library-sync", store.ReadLibrarySync(scope, revision), MusicLibraryProjection.ToJson),
            _ => throw new DocumentStoreException(DocumentStoreError.InvalidRequest)
        };
        return MusicLibraryProjection.SelectMembers(full, members);
    }

    private JsonObject? ReadAndTrack<T>(string area, T? record, Func<T, JsonObject> toJson) where T : class
    {
        loadedTypedAreas[area] = record;
        return record is null ? null : toJson(record);
    }

    private JsonObject? ReadHistoryArea(IMusicLibraryStore store, string[]? members)
    {
        if (!store.HasHistory(scope, revision)) return null;
        if (members is ["schema"]) return new JsonObject { ["schema"] = 1 };
        var area = store.ReadHistoryArea(scope, revision);
        return area is null ? null : MusicLibraryProjection.ToJson(area);
    }

    private JsonObject? ReadSourceExclusionsArea(IMusicLibraryStore store, string[]? members)
    {
        var rev = store.ReadSourceExclusionsRevision(scope, revision);
        if (rev is null) return null;
        var obj = new JsonObject { ["schema"] = 1, ["revision"] = rev.Value };
        if (members is null || members.Contains("items"))
        {
            var items = new JsonArray();
            foreach (var item in store.ReadSourceExclusions(scope, revision))
                items.Add(MusicLibraryProjection.ToJson(item));
            obj["items"] = items;
        }
        return obj;
    }

    private JsonObject? ReadLocalLyricsArea(IMusicLibraryStore store, string[]? members)
    {
        var summaries = store.ReadLocalLyricSummaries(scope, revision);
        if (summaries.Count == 0) return null;
        var obj = new JsonObject { ["schema"] = 1 };
        if (members is null || members.Contains("items"))
        {
            var items = new JsonArray();
            foreach (var sum in summaries)
                if (store.ReadLocalLyric(scope, revision, sum.SongId) is { } full)
                    items.Add(MusicLibraryProjection.ToJson(full));
            obj["items"] = items;
        }
        return obj;
    }
}
