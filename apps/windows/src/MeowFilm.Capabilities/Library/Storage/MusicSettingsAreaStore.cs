using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;
using static MeowFilm.Capabilities.Library.LibraryStorageHelpers;

namespace MeowFilm.Capabilities.Library;

internal static class MusicSettingsAreaStore
{
    private const string SourceExclusionKind = "source";

    internal static readonly HashSet<string> SupportedAreas = new(StringComparer.Ordinal)
    {
        "playback-settings",
        "online-settings",
        "native-device-settings",
        "native-appearance",
        "native-catalog-source",
        "native-desktop-lyrics",
        "native-quality-policy",
        "native-official-sources",
        "native-metadata-policy",
        "native-search-policy",
        "native-search-history",
        "native-source-exclusions",
        "native-local-lyrics",
        "library-sync"
    };

    internal static JsonObject? ReadArea(DatabaseSession db, string scope, string area, string[]? members) =>
        SelectMembers(area switch
        {
            "playback-settings" => ReadPlaybackSettings(db, scope),
            "online-settings" => ReadOnlineSettings(db, scope),
            "native-device-settings" => ReadDeviceSettings(db, scope),
            "native-appearance" => ReadAppearance(db, scope),
            "native-catalog-source" => ReadCatalogSource(db, scope),
            "native-desktop-lyrics" => ReadDesktopLyrics(db, scope),
            "native-quality-policy" => ReadQualityPolicy(db, scope),
            "native-official-sources" => ReadOfficialSources(db, scope),
            "native-metadata-policy" => ReadMetadataPolicy(db, scope),
            "native-search-policy" => ReadSearchPolicy(db, scope),
            "native-search-history" => ReadSearchHistory(db, scope),
            "native-source-exclusions" => ReadSourceExclusions(db, scope, members),
            "native-local-lyrics" => ReadLocalLyricsArea(db, scope, members),
            "library-sync" => ReadLibrarySync(db, scope),
            _ => throw new DocumentStoreException(DocumentStoreError.InvalidRequest)
        }, members);

    internal static void WriteArea(DatabaseSession db, string scope, string area, JsonObject value, bool partial)
    {
        switch (area)
        {
            case "playback-settings": WritePlaybackSettings(db, scope, value); break;
            case "online-settings": WriteOnlineSettings(db, scope, value); break;
            case "native-device-settings": WriteDeviceSettings(db, scope, value); break;
            case "native-appearance": WriteAppearance(db, scope, value); break;
            case "native-catalog-source": WriteCatalogSource(db, scope, value); break;
            case "native-desktop-lyrics": WriteDesktopLyrics(db, scope, value); break;
            case "native-quality-policy": WriteQualityPolicy(db, scope, value); break;
            case "native-official-sources": WriteOfficialSources(db, scope, value); break;
            case "native-metadata-policy": WriteMetadataPolicy(db, scope, value); break;
            case "native-search-policy": WriteSearchPolicy(db, scope, value); break;
            case "native-search-history": WriteSearchHistory(db, scope, value); break;
            case "native-source-exclusions": WriteSourceExclusions(db, scope, value, partial); break;
            case "native-local-lyrics": WriteLocalLyricsArea(db, scope, value, partial); break;
            case "library-sync": WriteLibrarySync(db, scope, value); break;
            default: throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        }
    }

    internal static bool HasRelation(DatabaseSession db, string area, string[] relations, object?[] key, string relation)
    {
        string scope = (string)key[0]!;
        return (area, relations, relation) switch
        {
            ("native-source-exclusions", [], "items") =>
                db.Scalar<long>("SELECT count(*) FROM music_playback_settings WHERE scope=$scope AND exclusions_revision IS NOT NULL", ("$scope", scope)) > 0,
            ("native-local-lyrics", [], "items") =>
                db.Scalar<long>("SELECT count(*) FROM windows_music_local_lyrics WHERE scope=$scope", ("$scope", scope)) > 0,
            _ => false
        };
    }

    internal static IReadOnlyList<MusicLibrarySelectedRow> ReadRows(
        DatabaseSession db, string area, string[] relations, object?[] prefix, string[] members)
    {
        if (area == "native-source-exclusions" && relations is ["items"])
        {
            string scope = (string)prefix[0]!;
            bool single = prefix.Length == 2;
            string sql = single
                ? """
                  SELECT exclusion_id,position,song_id,provider,platform,provider_name,title,artist,excluded_at
                  FROM music_source_exclusions
                  WHERE scope=$scope AND exclusion_kind='source' AND exclusion_id=$key
                  ORDER BY position
                  """
                : """
                  SELECT exclusion_id,position,song_id,provider,platform,provider_name,title,artist,excluded_at
                  FROM music_source_exclusions
                  WHERE scope=$scope AND exclusion_kind='source'
                  ORDER BY position
                  """;
            var parameters = single
                ? new (string, object?)[] { ("$scope", scope), ("$key", prefix[1]) }
                : new (string, object?)[] { ("$scope", scope) };
            return db.Query(sql, r =>
            {
                var obj = new JsonObject
                {
                    ["songId"] = r.GetString(2),
                    ["provider"] = r.GetString(3),
                    ["platform"] = r.GetString(4),
                    ["providerName"] = r.GetString(5),
                    ["title"] = r.GetString(6),
                    ["artist"] = r.GetString(7),
                    ["at"] = r.GetInt64(8)
                };
                return new MusicLibrarySelectedRow([scope, r.GetString(0)], r.GetInt32(1), SelectMembers(obj, members)!);
            }, parameters);
        }

        if (area == "native-local-lyrics" && relations is ["items"])
        {
            string scope = (string)prefix[0]!;
            bool single = prefix.Length == 2;
            string sql = single
                ? "SELECT song_id,position,content_hash FROM windows_music_local_lyrics WHERE scope=$scope AND song_id=$sid ORDER BY position"
                : "SELECT song_id,position,content_hash FROM windows_music_local_lyrics WHERE scope=$scope ORDER BY position";
            var parameters = single
                ? new (string, object?)[] { ("$scope", scope), ("$sid", prefix[1]) }
                : new (string, object?)[] { ("$scope", scope) };
            return db.Query(sql, r =>
            {
                string sid = r.GetString(0);
                var obj = new JsonObject { ["songId"] = sid, ["contentHash"] = r.GetString(2) };
                return new MusicLibrarySelectedRow([scope, sid], r.GetInt32(1), SelectMembers(obj, members)!);
            }, parameters);
        }

        throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
    }

    internal static JsonObject? ReadRow(DatabaseSession db, string area, string[] relations, object?[] key)
    {
        if (area == "native-source-exclusions" && relations is ["items"])
        {
            return db.TryQuerySingle("""
                SELECT song_id,provider,platform,provider_name,title,artist,excluded_at
                FROM music_source_exclusions
                WHERE scope=$scope AND exclusion_kind='source' AND exclusion_id=$key
                """,
                r => new JsonObject
                {
                    ["songId"] = r.GetString(0),
                    ["provider"] = r.GetString(1),
                    ["platform"] = r.GetString(2),
                    ["providerName"] = r.GetString(3),
                    ["title"] = r.GetString(4),
                    ["artist"] = r.GetString(5),
                    ["at"] = r.GetInt64(6)
                },
                out var obj,
                ("$scope", key[0]), ("$key", key[1]))
                ? obj
                : null;
        }

        if (area == "native-local-lyrics" && relations is ["items"])
            return ReadLocalLyricItem(db, (string)key[0]!, (string)key[1]!);

        throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
    }

    internal static void ApplyRowChange(DatabaseSession db, MusicLibraryRowChange row)
    {
        string scope = (string)row.Key[0]!;
        if (row.Area == "native-source-exclusions" && row.Relations is ["items"])
        {
            string itemKey = (string)row.Key[1]!;
            if (row.Value is null)
            {
                db.Execute("""
                    DELETE FROM music_source_exclusions
                    WHERE scope=$scope AND exclusion_kind='source' AND exclusion_id=$key
                    """, ("$scope", scope), ("$key", itemKey));
            }
            else
            {
                EnsurePlaybackSettingsRow(db, scope);
                db.Execute("""
                    INSERT INTO music_source_exclusions(
                        scope,exclusion_kind,exclusion_id,position,song_id,provider,platform,provider_name,title,artist,excluded_at)
                    VALUES($scope,$kind,$key,$pos,$sid,$prov,$plat,$pname,$title,$artist,$at)
                    ON CONFLICT(scope,exclusion_kind,exclusion_id) DO UPDATE SET
                        position=excluded.position,
                        song_id=excluded.song_id,
                        provider=excluded.provider,
                        platform=excluded.platform,
                        provider_name=excluded.provider_name,
                        title=excluded.title,
                        artist=excluded.artist,
                        excluded_at=excluded.excluded_at
                    """,
                    ("$scope", scope), ("$kind", SourceExclusionKind), ("$key", itemKey), ("$pos", row.Ordinal),
                    ("$sid", Str(row.Value, "songId")!),
                    ("$prov", Str(row.Value, "provider")!),
                    ("$plat", Str(row.Value, "platform")!),
                    ("$pname", Str(row.Value, "providerName") ?? ""),
                    ("$title", Str(row.Value, "title") ?? ""),
                    ("$artist", Str(row.Value, "artist") ?? ""),
                    ("$at", Int64(row.Value, "at") ?? 0));
            }
            return;
        }

        if (row.Area == "native-local-lyrics" && row.Relations is ["items"])
        {
            if (row.Value is null)
            {
                db.Execute("DELETE FROM windows_music_local_lyrics WHERE scope=$scope AND song_id=$sid",
                    ("$scope", scope), ("$sid", row.Key[1]));
            }
            else
            {
                string songId = Str(row.Value, "songId")!;
                SaveLocalLyricItem(db, scope, songId, row.Ordinal, Str(row.Value, "contentHash")!, row.Value["lines"] as JsonArray ?? []);
            }
            return;
        }

        throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
    }

    private static void EnsurePlaybackSettingsRow(DatabaseSession db, string scope) =>
        db.Execute("INSERT OR IGNORE INTO music_playback_settings(scope) VALUES($scope)", ("$scope", scope));

    private static JsonObject? ReadPlaybackSettings(DatabaseSession db, string scope) =>
        db.TryQuerySingle("SELECT mode FROM music_playback_settings WHERE scope=$scope AND has_mode=1",
            r => new JsonObject { ["schema"] = 1, ["mode"] = r.GetInt64(0) }, out var obj, ("$scope", scope))
            ? obj : null;

    private static void WritePlaybackSettings(DatabaseSession db, string scope, JsonObject value) =>
        db.Execute("""
            INSERT INTO music_playback_settings(scope,mode,has_mode) VALUES($scope,$mode,1)
            ON CONFLICT(scope) DO UPDATE SET mode=excluded.mode,has_mode=1
            """, ("$scope", scope), ("$mode", Int64(value, "mode") ?? 1));

    private static JsonObject? ReadOnlineSettings(DatabaseSession db, string scope) =>
        db.TryQuerySingle("""
            SELECT online_revision,lyrics_enabled
            FROM music_playback_settings
            WHERE scope=$scope AND online_revision IS NOT NULL
            """,
            r => new JsonObject { ["schema"] = 1, ["revision"] = r.GetInt64(0), ["lyrics"] = r.GetBool(1) },
            out var obj, ("$scope", scope)) ? obj : null;

    private static void WriteOnlineSettings(DatabaseSession db, string scope, JsonObject value) =>
        db.Execute("""
            INSERT INTO music_playback_settings(scope,online_revision,lyrics_enabled) VALUES($scope,$rev,$lyr)
            ON CONFLICT(scope) DO UPDATE SET online_revision=excluded.online_revision,lyrics_enabled=excluded.lyrics_enabled
            """, ("$scope", scope), ("$rev", Int64(value, "revision") ?? 0), ("$lyr", Bool(value, "lyrics") ?? true));

    private static JsonObject? ReadDeviceSettings(DatabaseSession db, string scope) =>
        db.TryQuerySingle("SELECT revision,volume FROM windows_music_device_settings WHERE scope=$scope",
            r => new JsonObject { ["schema"] = 1, ["revision"] = r.GetInt64(0), ["volume"] = r.GetInt64(1) },
            out var obj, ("$scope", scope)) ? obj : null;

    private static void WriteDeviceSettings(DatabaseSession db, string scope, JsonObject value) =>
        db.Execute("""
            INSERT INTO windows_music_device_settings(scope,revision,volume) VALUES($scope,$rev,$vol)
            ON CONFLICT(scope) DO UPDATE SET revision=excluded.revision,volume=excluded.volume
            """, ("$scope", scope), ("$rev", Int64(value, "revision") ?? 0), ("$vol", Int64(value, "volume") ?? 100));

    private static JsonObject? ReadAppearance(DatabaseSession db, string scope) =>
        db.TryQuerySingle("""
            SELECT revision,playlist_view,player_theme,color_mode,color_palette
            FROM music_appearance WHERE scope=$scope
            """,
            r => new JsonObject
            {
                ["schema"] = 1,
                ["revision"] = r.GetInt64(0),
                ["playlistView"] = r.GetString(1),
                ["playerTheme"] = r.GetString(2),
                ["colorMode"] = r.GetString(3),
                ["colorPalette"] = r.GetString(4)
            },
            out var obj, ("$scope", scope)) ? obj : null;

    private static void WriteAppearance(DatabaseSession db, string scope, JsonObject value) =>
        db.Execute("""
            INSERT INTO music_appearance(scope,revision,playlist_view,player_theme,color_mode,color_palette)
            VALUES($scope,$rev,$pv,$pt,$cm,$cp)
            ON CONFLICT(scope) DO UPDATE SET
                revision=excluded.revision,playlist_view=excluded.playlist_view,
                player_theme=excluded.player_theme,color_mode=excluded.color_mode,color_palette=excluded.color_palette
            """,
            ("$scope", scope), ("$rev", Int64(value, "revision") ?? 0),
            ("$pv", Str(value, "playlistView")!), ("$pt", Str(value, "playerTheme")!),
            ("$cm", Str(value, "colorMode")!), ("$cp", Str(value, "colorPalette")!));

    private static JsonObject? ReadCatalogSource(DatabaseSession db, string scope) =>
        db.TryQuerySingle("SELECT revision,platform FROM music_catalog_source WHERE scope=$scope",
            r => new JsonObject { ["schema"] = 1, ["revision"] = r.GetInt64(0), ["platform"] = r.GetString(1) },
            out var obj, ("$scope", scope)) ? obj : null;

    private static void WriteCatalogSource(DatabaseSession db, string scope, JsonObject value) =>
        db.Execute("""
            INSERT INTO music_catalog_source(scope,revision,platform) VALUES($scope,$rev,$plat)
            ON CONFLICT(scope) DO UPDATE SET revision=excluded.revision,platform=excluded.platform
            """, ("$scope", scope), ("$rev", Int64(value, "revision") ?? 0), ("$plat", Str(value, "platform")!));

    private static JsonObject? ReadDesktopLyrics(DatabaseSession db, string scope) =>
        db.TryQuerySingle("""
            SELECT revision,layout,normal_color,highlight_color,geom_monitor,geom_x,geom_y,geom_width,geom_height
            FROM windows_music_desktop_lyrics WHERE scope=$scope
            """,
            r =>
            {
                var obj = new JsonObject
                {
                    ["schema"] = 1,
                    ["revision"] = r.GetInt64(0),
                    ["layout"] = r.GetString(1),
                    ["normalColor"] = r.GetString(2),
                    ["highlightColor"] = r.GetString(3)
                };
                if (r.GetNullableString(4) is { } mon)
                {
                    obj["geometry"] = new JsonObject
                    {
                        ["monitor"] = mon,
                        ["x"] = r.GetNullableDouble(5) ?? 0,
                        ["y"] = r.GetNullableDouble(6) ?? 0,
                        ["width"] = r.GetNullableDouble(7) ?? 0,
                        ["height"] = r.GetNullableDouble(8) ?? 0
                    };
                }
                return obj;
            },
            out var result, ("$scope", scope)) ? result : null;

    private static void WriteDesktopLyrics(DatabaseSession db, string scope, JsonObject value)
    {
        var geom = value["geometry"] as JsonObject;
        db.Execute("""
            INSERT INTO windows_music_desktop_lyrics(
                scope,revision,layout,normal_color,highlight_color,geom_monitor,geom_x,geom_y,geom_width,geom_height)
            VALUES($scope,$rev,$lay,$nc,$hc,$gm,$gx,$gy,$gw,$gh)
            ON CONFLICT(scope) DO UPDATE SET
                revision=excluded.revision,layout=excluded.layout,
                normal_color=excluded.normal_color,highlight_color=excluded.highlight_color,
                geom_monitor=excluded.geom_monitor,geom_x=excluded.geom_x,geom_y=excluded.geom_y,
                geom_width=excluded.geom_width,geom_height=excluded.geom_height
            """,
            ("$scope", scope), ("$rev", Int64(value, "revision") ?? 0),
            ("$lay", Str(value, "layout")!), ("$nc", Str(value, "normalColor")!), ("$hc", Str(value, "highlightColor")!),
            ("$gm", Str(geom, "monitor")), ("$gx", Dbl(geom, "x")), ("$gy", Dbl(geom, "y")),
            ("$gw", Dbl(geom, "width")), ("$gh", Dbl(geom, "height")));
    }

    private static JsonObject? ReadQualityPolicy(DatabaseSession db, string scope)
    {
        if (!db.TryQuerySingle("""
            SELECT quality_revision,quality_request
            FROM music_playback_settings
            WHERE scope=$scope AND quality_revision IS NOT NULL
            """,
            r => (Rev: r.GetInt64(0), Req: r.GetNullableString(1) ?? ""), out var row, ("$scope", scope)))
            return null;

        var lists = new Dictionary<string, JsonArray>(StringComparer.Ordinal)
        {
            ["qualityOrder"] = [], ["qualityEnabled"] = [], ["spatialOrder"] = [], ["spatialEnabled"] = []
        };
        foreach (var (lk, val) in db.Query("""
            SELECT list_kind,item_value FROM music_playback_lists
            WHERE scope=$scope AND list_kind IN ('qualityOrder','qualityEnabled','spatialOrder','spatialEnabled')
            ORDER BY list_kind, position
            """, r => (r.GetString(0), r.GetString(1)), ("$scope", scope)))
            lists[lk].Add(val);

        return new JsonObject
        {
            ["schema"] = 1,
            ["revision"] = row.Rev,
            ["request"] = row.Req,
            ["qualityOrder"] = lists["qualityOrder"],
            ["qualityEnabled"] = lists["qualityEnabled"],
            ["spatialOrder"] = lists["spatialOrder"],
            ["spatialEnabled"] = lists["spatialEnabled"]
        };
    }

    private static void WriteQualityPolicy(DatabaseSession db, string scope, JsonObject value)
    {
        db.Execute("""
            INSERT INTO music_playback_settings(scope,quality_revision,quality_request) VALUES($scope,$rev,$req)
            ON CONFLICT(scope) DO UPDATE SET quality_revision=excluded.quality_revision,quality_request=excluded.quality_request
            """, ("$scope", scope), ("$rev", Int64(value, "revision") ?? 0), ("$req", Str(value, "request")!));
        foreach (string lk in new[] { "qualityOrder", "qualityEnabled", "spatialOrder", "spatialEnabled" })
            DiffPlaybackList(db, scope, lk, ReadStringArray(value[lk] as JsonArray));
    }

    private static JsonObject? ReadOfficialSources(DatabaseSession db, string scope)
    {
        if (!db.TryQuerySingle("""
            SELECT official_revision FROM music_playback_settings
            WHERE scope=$scope AND official_revision IS NOT NULL
            """,
            r => r.GetInt64(0), out long rev, ("$scope", scope)))
            return null;

        var enabled = new JsonArray();
        var platforms = new JsonArray();
        foreach (var (lk, val) in db.Query("""
            SELECT list_kind,item_value FROM music_playback_lists
            WHERE scope=$scope AND list_kind IN ('officialPlatforms','officialEnabled')
            ORDER BY list_kind, position
            """, r => (r.GetString(0), r.GetString(1)), ("$scope", scope)))
        {
            if (lk == "officialEnabled") enabled.Add(val);
            else platforms.Add(val);
        }
        var obj = new JsonObject { ["schema"] = 1, ["revision"] = rev, ["enabled"] = enabled };
        if (platforms.Count > 0) obj["platforms"] = platforms;
        return obj;
    }

    private static void WriteOfficialSources(DatabaseSession db, string scope, JsonObject value)
    {
        db.Execute("""
            INSERT INTO music_playback_settings(scope,official_revision) VALUES($scope,$rev)
            ON CONFLICT(scope) DO UPDATE SET official_revision=excluded.official_revision
            """, ("$scope", scope), ("$rev", Int64(value, "revision") ?? 0));
        DiffPlaybackList(db, scope, "officialPlatforms", ReadStringArray(value["platforms"] as JsonArray));
        DiffPlaybackList(db, scope, "officialEnabled", ReadStringArray(value["enabled"] as JsonArray));
    }

    private static JsonObject? ReadMetadataPolicy(DatabaseSession db, string scope)
    {
        if (!db.TryQuerySingle("""
            SELECT metadata_revision FROM music_playback_settings
            WHERE scope=$scope AND metadata_revision IS NOT NULL
            """,
            r => r.GetInt64(0), out long rev, ("$scope", scope)))
            return null;

        var platforms = new JsonArray();
        var enabled = new JsonArray();
        foreach (var (lk, val) in db.Query("""
            SELECT list_kind,item_value FROM music_playback_lists
            WHERE scope=$scope AND list_kind IN ('metadataPlatforms','metadataEnabled')
            ORDER BY list_kind, position
            """, r => (r.GetString(0), r.GetString(1)), ("$scope", scope)))
        {
            if (lk == "metadataPlatforms") platforms.Add(val);
            else enabled.Add(val);
        }
        return new JsonObject { ["schema"] = 1, ["revision"] = rev, ["platforms"] = platforms, ["enabled"] = enabled };
    }

    private static void WriteMetadataPolicy(DatabaseSession db, string scope, JsonObject value)
    {
        db.Execute("""
            INSERT INTO music_playback_settings(scope,metadata_revision) VALUES($scope,$rev)
            ON CONFLICT(scope) DO UPDATE SET metadata_revision=excluded.metadata_revision
            """, ("$scope", scope), ("$rev", Int64(value, "revision") ?? 0));
        DiffPlaybackList(db, scope, "metadataPlatforms", ReadStringArray(value["platforms"] as JsonArray));
        DiffPlaybackList(db, scope, "metadataEnabled", ReadStringArray(value["enabled"] as JsonArray));
    }

    private static void DiffPlaybackList(DatabaseSession db, string scope, string listKind, string[] next)
    {
        var prev = db.Query("""
            SELECT item_value FROM music_playback_lists
            WHERE scope=$scope AND list_kind=$lk ORDER BY position
            """, r => r.GetString(0), ("$scope", scope), ("$lk", listKind));
        if (prev.Count == next.Length && prev.SequenceEqual(next, StringComparer.Ordinal)) return;
        int prefix = CommonPrefixLength(prev, next, StringComparer.Ordinal);
        if (prefix < prev.Count)
            db.Execute("DELETE FROM music_playback_lists WHERE scope=$scope AND list_kind=$lk AND position>=$pos",
                ("$scope", scope), ("$lk", listKind), ("$pos", prefix));
        for (int i = prefix; i < next.Length; i++)
            db.Execute("INSERT INTO music_playback_lists(scope,list_kind,position,item_value) VALUES($scope,$lk,$pos,$val)",
                ("$scope", scope), ("$lk", listKind), ("$pos", i), ("$val", next[i]));
    }

    private static JsonObject? ReadSearchPolicy(DatabaseSession db, string scope)
    {
        if (!db.TryQuerySingle("""
            SELECT revision,songs_pages,songs_concurrency,songs_cache_minutes,
                   playlists_pages,playlists_concurrency,playlists_cache_minutes
            FROM windows_music_search_policy WHERE scope=$scope
            """,
            r => (Rev: r.GetInt64(0), SP: r.GetInt64(1), SC: r.GetInt64(2), SM: r.GetInt64(3),
                PP: r.GetInt64(4), PC: r.GetInt64(5), PM: r.GetInt64(6)),
            out var row, ("$scope", scope)))
            return null;

        JsonArray sOrd = [], sEn = [], pOrd = [], pEn = [];
        foreach (var (tk, lk, val) in db.Query("""
            SELECT target_kind,list_kind,item_value FROM windows_music_search_policy_list
            WHERE scope=$scope ORDER BY target_kind, list_kind, position
            """, r => (r.GetString(0), r.GetString(1), r.GetString(2)), ("$scope", scope)))
        {
            if (tk == "songs") (lk == "order" ? sOrd : sEn).Add(val);
            else (lk == "order" ? pOrd : pEn).Add(val);
        }

        return new JsonObject
        {
            ["schema"] = 1,
            ["revision"] = row.Rev,
            ["songs"] = new JsonObject
            {
                ["order"] = sOrd, ["enabled"] = sEn,
                ["pages"] = row.SP, ["concurrency"] = row.SC, ["cacheMinutes"] = row.SM
            },
            ["playlists"] = new JsonObject
            {
                ["order"] = pOrd, ["enabled"] = pEn,
                ["pages"] = row.PP, ["concurrency"] = row.PC, ["cacheMinutes"] = row.PM
            }
        };
    }

    private static void WriteSearchPolicy(DatabaseSession db, string scope, JsonObject value)
    {
        var songs = value["songs"] as JsonObject ?? new JsonObject();
        var playlists = value["playlists"] as JsonObject ?? new JsonObject();
        db.Execute("""
            INSERT INTO windows_music_search_policy(
                scope,revision,songs_pages,songs_concurrency,songs_cache_minutes,
                playlists_pages,playlists_concurrency,playlists_cache_minutes)
            VALUES($scope,$rev,$sp,$sc,$sm,$pp,$pc,$pm)
            ON CONFLICT(scope) DO UPDATE SET
                revision=excluded.revision,
                songs_pages=excluded.songs_pages,songs_concurrency=excluded.songs_concurrency,songs_cache_minutes=excluded.songs_cache_minutes,
                playlists_pages=excluded.playlists_pages,playlists_concurrency=excluded.playlists_concurrency,playlists_cache_minutes=excluded.playlists_cache_minutes
            """,
            ("$scope", scope), ("$rev", Int64(value, "revision") ?? 0),
            ("$sp", Int64(songs, "pages") ?? 1), ("$sc", Int64(songs, "concurrency") ?? 1), ("$sm", Int64(songs, "cacheMinutes") ?? 60),
            ("$pp", Int64(playlists, "pages") ?? 1), ("$pc", Int64(playlists, "concurrency") ?? 1), ("$pm", Int64(playlists, "cacheMinutes") ?? 60));

        foreach (var (tk, obj) in new[] { ("songs", songs), ("playlists", playlists) })
            foreach (string lk in new[] { "order", "enabled" })
            {
                var next = ReadStringArray(obj[lk] as JsonArray);
                var prev = db.Query("""
                    SELECT item_value FROM windows_music_search_policy_list
                    WHERE scope=$scope AND target_kind=$tk AND list_kind=$lk ORDER BY position
                    """, r => r.GetString(0), ("$scope", scope), ("$tk", tk), ("$lk", lk));
                if (prev.Count == next.Length && prev.SequenceEqual(next, StringComparer.Ordinal)) continue;
                int prefix = CommonPrefixLength(prev, next, StringComparer.Ordinal);
                if (prefix < prev.Count)
                    db.Execute("""
                        DELETE FROM windows_music_search_policy_list
                        WHERE scope=$scope AND target_kind=$tk AND list_kind=$lk AND position>=$pos
                        """, ("$scope", scope), ("$tk", tk), ("$lk", lk), ("$pos", prefix));
                for (int i = prefix; i < next.Length; i++)
                    db.Execute("""
                        INSERT INTO windows_music_search_policy_list(scope,target_kind,list_kind,position,item_value)
                        VALUES($scope,$tk,$lk,$pos,$val)
                        """, ("$scope", scope), ("$tk", tk), ("$lk", lk), ("$pos", i), ("$val", next[i]));
            }
    }

    private static JsonObject? ReadSearchHistory(DatabaseSession db, string scope)
    {
        if (!db.TryQuerySingle("SELECT revision FROM windows_music_search_history_state WHERE scope=$scope",
            r => r.GetInt64(0), out long rev, ("$scope", scope)))
            return null;

        var items = new JsonArray();
        foreach (var item in db.Query("""
            SELECT query_text,kind FROM windows_music_search_history WHERE scope=$scope ORDER BY position
            """, r => new JsonObject { ["text"] = r.GetString(0), ["kind"] = r.GetString(1) }, ("$scope", scope)))
            items.Add(item);

        return new JsonObject { ["schema"] = 1, ["revision"] = rev, ["items"] = items };
    }

    private static void WriteSearchHistory(DatabaseSession db, string scope, JsonObject value)
    {
        db.Execute("""
            INSERT INTO windows_music_search_history_state(scope,revision) VALUES($scope,$rev)
            ON CONFLICT(scope) DO UPDATE SET revision=excluded.revision
            """, ("$scope", scope), ("$rev", Int64(value, "revision") ?? 0));

        var prev = db.Query("""
            SELECT query_text,kind FROM windows_music_search_history WHERE scope=$scope ORDER BY position
            """, r => (Text: r.GetString(0), Kind: r.GetString(1)), ("$scope", scope));
        var next = new List<(string Text, string Kind)>();
        if (value["items"] is JsonArray items)
            foreach (var item in items)
                if (item is JsonObject obj)
                    next.Add((Str(obj, "text")!, Str(obj, "kind")!));

        if (prev.Count == next.Count && prev.SequenceEqual(next)) return;
        int prefix = CommonPrefixLength(prev, next);
        if (prefix < prev.Count)
            db.Execute("DELETE FROM windows_music_search_history WHERE scope=$scope AND position>=$pos",
                ("$scope", scope), ("$pos", prefix));
        for (int i = prefix; i < next.Count; i++)
            db.Execute("INSERT INTO windows_music_search_history(scope,position,query_text,kind) VALUES($scope,$pos,$txt,$kind)",
                ("$scope", scope), ("$pos", i), ("$txt", next[i].Text), ("$kind", next[i].Kind));
    }

    private static JsonObject? ReadSourceExclusions(DatabaseSession db, string scope, string[]? members)
    {
        if (!db.TryQuerySingle("""
            SELECT exclusions_revision FROM music_playback_settings
            WHERE scope=$scope AND exclusions_revision IS NOT NULL
            """,
            r => r.GetInt64(0), out long rev, ("$scope", scope)))
            return null;

        var obj = new JsonObject { ["schema"] = 1, ["revision"] = rev };
        if (members is null || members.Contains("items"))
        {
            var items = new JsonArray();
            foreach (var item in db.Query("""
                SELECT song_id,provider,platform,provider_name,title,artist,excluded_at
                FROM music_source_exclusions
                WHERE scope=$scope AND exclusion_kind='source'
                ORDER BY position
                """, r => new JsonObject
                {
                    ["songId"] = r.GetString(0),
                    ["provider"] = r.GetString(1),
                    ["platform"] = r.GetString(2),
                    ["providerName"] = r.GetString(3),
                    ["title"] = r.GetString(4),
                    ["artist"] = r.GetString(5),
                    ["at"] = r.GetInt64(6)
                }, ("$scope", scope)))
                items.Add(item);
            obj["items"] = items;
        }
        return obj;
    }

    private static void WriteSourceExclusions(DatabaseSession db, string scope, JsonObject value, bool partial)
    {
        db.Execute("""
            INSERT INTO music_playback_settings(scope,exclusions_revision) VALUES($scope,$rev)
            ON CONFLICT(scope) DO UPDATE SET exclusions_revision=excluded.exclusions_revision
            """, ("$scope", scope), ("$rev", Int64(value, "revision") ?? 0));
        if (partial && !value.ContainsKey("items")) return;

        db.Execute("DELETE FROM music_source_exclusions WHERE scope=$scope AND exclusion_kind='source'", ("$scope", scope));
        if (value["items"] is JsonArray items)
            for (int i = 0; i < items.Count; i++)
                if (items[i] is JsonObject item)
                    db.Execute("""
                        INSERT INTO music_source_exclusions(
                            scope,exclusion_kind,exclusion_id,position,song_id,provider,platform,provider_name,title,artist,excluded_at)
                        VALUES($scope,$kind,$key,$pos,$sid,$prov,$plat,$pname,$title,$artist,$at)
                        """,
                        ("$scope", scope), ("$kind", SourceExclusionKind), ("$key", Guid.NewGuid().ToString("D")), ("$pos", i),
                        ("$sid", Str(item, "songId")!), ("$prov", Str(item, "provider")!),
                        ("$plat", Str(item, "platform")!), ("$pname", Str(item, "providerName") ?? ""),
                        ("$title", Str(item, "title") ?? ""), ("$artist", Str(item, "artist") ?? ""),
                        ("$at", Int64(item, "at") ?? 0));
    }

    private static JsonObject? ReadLocalLyricsArea(DatabaseSession db, string scope, string[]? members)
    {
        var ids = db.Query("SELECT song_id FROM windows_music_local_lyrics WHERE scope=$scope ORDER BY position",
            r => r.GetString(0), ("$scope", scope));
        if (ids.Count == 0) return null;
        var obj = new JsonObject { ["schema"] = 1 };
        if (members is null || members.Contains("items"))
        {
            var items = new JsonArray();
            foreach (var sid in ids)
                if (ReadLocalLyricItem(db, scope, sid) is { } item) items.Add(item);
            obj["items"] = items;
        }
        return obj;
    }

    private static void WriteLocalLyricsArea(DatabaseSession db, string scope, JsonObject value, bool partial)
    {
        if (partial && !value.ContainsKey("items")) return;
        var nextIds = new HashSet<string>(StringComparer.Ordinal);
        var items = value["items"] as JsonArray;
        if (items is not null)
        {
            foreach (var item in items)
                if (item is JsonObject obj && Str(obj, "songId") is { } sid)
                    nextIds.Add(sid);
        }
        foreach (var oldSid in db.Query("SELECT song_id FROM windows_music_local_lyrics WHERE scope=$scope",
            r => r.GetString(0), ("$scope", scope)))
        {
            if (!nextIds.Contains(oldSid))
                db.Execute("DELETE FROM windows_music_local_lyrics WHERE scope=$scope AND song_id=$sid",
                    ("$scope", scope), ("$sid", oldSid));
        }
        if (items is not null)
            for (int i = 0; i < items.Count; i++)
                if (items[i] is JsonObject item)
                    SaveLocalLyricItem(db, scope, Str(item, "songId")!, i, Str(item, "contentHash")!, item["lines"] as JsonArray ?? []);
    }

    private static JsonObject? ReadLocalLyricItem(DatabaseSession db, string scope, string songId)
    {
        if (!db.TryQuerySingle("""
            SELECT content_hash FROM windows_music_local_lyrics WHERE scope=$scope AND song_id=$sid
            """, r => r.GetString(0), out string hash, ("$scope", scope), ("$sid", songId)))
            return null;

        var wordsByLine = new Dictionary<int, List<JsonObject>>();
        foreach (var item in db.Query("""
            SELECT line_index,time_sec,text,duration_sec,offset_sec
            FROM windows_music_local_lyric_words
            WHERE scope=$scope AND song_id=$sid
            ORDER BY line_index, word_index
            """, r =>
            {
                var w = new JsonObject
                {
                    ["time"] = r.GetDouble(1),
                    ["text"] = r.GetString(2),
                    ["duration"] = r.GetDouble(3)
                };
                if (r.GetNullableDouble(4) is { } off) w["offset"] = off;
                return (LineIdx: r.GetInt32(0), Word: w);
            }, ("$scope", scope), ("$sid", songId)))
        {
            if (!wordsByLine.TryGetValue(item.LineIdx, out var list)) wordsByLine[item.LineIdx] = list = [];
            list.Add(item.Word);
        }

        var lines = new JsonArray();
        foreach (var line in db.Query("""
            SELECT line_index,time_sec,text,translation,romanization,end_sec
            FROM windows_music_local_lyric_lines
            WHERE scope=$scope AND song_id=$sid
            ORDER BY line_index
            """, r =>
            {
                int idx = r.GetInt32(0);
                var l = new JsonObject { ["time"] = r.GetDouble(1), ["text"] = r.GetString(2) };
                SetNonNull(l, "translation", r.GetNullableString(3));
                SetNonNull(l, "romanization", r.GetNullableString(4));
                SetNonNull(l, "end", r.GetNullableDouble(5));
                l["words"] = wordsByLine.TryGetValue(idx, out var ws)
                    ? new JsonArray(ws.Cast<JsonNode?>().ToArray())
                    : new JsonArray();
                return l;
            }, ("$scope", scope), ("$sid", songId)))
            lines.Add(line);

        return new JsonObject { ["songId"] = songId, ["contentHash"] = hash, ["lines"] = lines };
    }

    private static void SaveLocalLyricItem(DatabaseSession db, string scope, string songId, int position, string hash, JsonArray lines)
    {
        if (db.TryQuerySingle("""
            SELECT position,content_hash FROM windows_music_local_lyrics WHERE scope=$scope AND song_id=$sid
            """, r => (Pos: r.GetInt32(0), Hash: r.GetString(1)), out var existing, ("$scope", scope), ("$sid", songId)))
        {
            if (existing.Hash == hash)
            {
                if (existing.Pos != position)
                    db.Execute("UPDATE windows_music_local_lyrics SET position=$pos WHERE scope=$scope AND song_id=$sid",
                        ("$pos", position), ("$scope", scope), ("$sid", songId));
                return;
            }
            db.Execute("""
                UPDATE windows_music_local_lyrics SET position=$pos,content_hash=$hash
                WHERE scope=$scope AND song_id=$sid
                """, ("$pos", position), ("$hash", hash), ("$scope", scope), ("$sid", songId));
            db.Execute("DELETE FROM windows_music_local_lyric_lines WHERE scope=$scope AND song_id=$sid",
                ("$scope", scope), ("$sid", songId));
        }
        else
        {
            db.Execute("""
                INSERT INTO windows_music_local_lyrics(scope,song_id,position,content_hash)
                VALUES($scope,$sid,$pos,$hash)
                """, ("$scope", scope), ("$sid", songId), ("$pos", position), ("$hash", hash));
        }

        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i] is not JsonObject line) continue;
            db.Execute("""
                INSERT INTO windows_music_local_lyric_lines(scope,song_id,line_index,time_sec,text,translation,romanization,end_sec)
                VALUES($scope,$sid,$idx,$time,$text,$tr,$roma,$end)
                """,
                ("$scope", scope), ("$sid", songId), ("$idx", i),
                ("$time", Dbl(line, "time") ?? 0d), ("$text", Str(line, "text") ?? ""),
                ("$tr", Str(line, "translation")), ("$roma", Str(line, "romanization")), ("$end", Dbl(line, "end")));
            if (line["words"] is JsonArray words)
            {
                for (int w = 0; w < words.Count; w++)
                {
                    if (words[w] is not JsonObject word) continue;
                    db.Execute("""
                        INSERT INTO windows_music_local_lyric_words(scope,song_id,line_index,word_index,time_sec,text,duration_sec,offset_sec)
                        VALUES($scope,$sid,$lidx,$widx,$time,$text,$dur,$off)
                        """,
                        ("$scope", scope), ("$sid", songId), ("$lidx", i), ("$widx", w),
                        ("$time", Dbl(word, "time") ?? 0d), ("$text", Str(word, "text") ?? ""),
                        ("$dur", Dbl(word, "duration") ?? 0d), ("$off", Dbl(word, "offset")));
                }
            }
        }
    }

    private static JsonObject? ReadLibrarySync(DatabaseSession db, string scope)
    {
        if (!db.TryQuerySingle("""
            SELECT revision,
                   pending_platform,pending_account_id,pending_collection_id,pending_remote_id,pending_operation,
                   pending_before_present,pending_desired_present,pending_name,
                   pending_locator_list_id,pending_locator_creator_id,pending_locator_creator_list_id,
                   confirmation_token,confirmation_code,confirmation_count,
                   approved_token,approved_code,approved_count,
                   status_phase,status_at,status_code
            FROM windows_music_sync_state
            WHERE scope=$scope
            """,
            r => (
                Rev: r.GetInt64(0),
                PPlat: r.GetNullableString(1), PAcc: r.GetNullableString(2), PCol: r.GetNullableString(3),
                PRem: r.GetNullableString(4), POp: r.GetNullableString(5),
                PBP: r.GetNullableBool(6), PDP: r.GetNullableBool(7), PName: r.GetNullableString(8),
                PLL: r.GetNullableString(9), PLC: r.GetNullableString(10), PLCL: r.GetNullableString(11),
                CTok: r.GetNullableString(12), CCode: r.GetNullableString(13), CCnt: r.GetNullableInt64(14),
                ATok: r.GetNullableString(15), ACode: r.GetNullableString(16), ACnt: r.GetNullableInt64(17),
                SPhase: r.GetNullableString(18), SAt: r.GetNullableDouble(19), SCode: r.GetNullableString(20)),
            out var st, ("$scope", scope)))
            return null;

        var platforms = new JsonObject();
        foreach (var (plat, fav, pls, acc) in db.Query("""
            SELECT platform,favorites,playlists,account_id FROM windows_music_sync_platforms WHERE scope=$scope ORDER BY platform
            """, r => (r.GetString(0), r.GetBool(1), r.GetBool(2), r.GetNullableString(3) ?? ""), ("$scope", scope)))
        {
            platforms[plat] = new JsonObject { ["favorites"] = fav, ["playlists"] = pls, ["accountId"] = acc };
        }

        JsonObject? pending = null;
        if (st.POp is not null)
        {
            if (st.POp is "subscribe" or "unsubscribe")
            {
                var locator = new JsonObject();
                SetNonNull(locator, "listId", st.PLL);
                SetNonNull(locator, "creatorId", st.PLC);
                SetNonNull(locator, "creatorListId", st.PLCL);
                pending = new JsonObject
                {
                    ["platform"] = st.PPlat ?? "",
                    ["accountId"] = st.PAcc ?? "",
                    ["collectionId"] = st.PCol ?? "",
                    ["remoteId"] = st.PRem ?? "",
                    ["operation"] = st.POp,
                    ["beforePresent"] = st.PBP ?? false,
                    ["desiredPresent"] = st.PDP ?? false,
                    ["locator"] = locator,
                    ["name"] = st.PName ?? ""
                };
            }
            else
            {
                var lists = new Dictionary<string, JsonArray>(StringComparer.Ordinal)
                {
                    ["before"] = [], ["desired"] = [], ["ids"] = [], ["directory"] = []
                };
                foreach (var (lk, val) in db.Query("""
                    SELECT list_kind,item_value FROM windows_music_sync_pending_items WHERE scope=$scope ORDER BY list_kind, position
                    """, r => (r.GetString(0), r.GetString(1)), ("$scope", scope)))
                    lists[lk].Add(val);
                pending = new JsonObject
                {
                    ["platform"] = st.PPlat ?? "",
                    ["accountId"] = st.PAcc ?? "",
                    ["collectionId"] = st.PCol ?? "",
                    ["remoteId"] = st.PRem ?? "",
                    ["operation"] = st.POp,
                    ["before"] = lists["before"],
                    ["desired"] = lists["desired"],
                    ["ids"] = lists["ids"],
                    ["name"] = st.PName ?? "",
                    ["directory"] = lists["directory"]
                };
            }
        }

        JsonObject? confirmation = st.CTok is not null
            ? new JsonObject { ["token"] = st.CTok, ["code"] = st.CCode ?? "", ["count"] = st.CCnt ?? 0 }
            : null;
        JsonObject? approved = st.ATok is not null
            ? new JsonObject { ["token"] = st.ATok, ["code"] = st.ACode ?? "", ["count"] = st.ACnt ?? 0 }
            : null;

        var issues = new JsonArray();
        foreach (var iss in db.Query("""
            SELECT platform,collection_id,code FROM windows_music_sync_issues WHERE scope=$scope ORDER BY position
            """, r => new JsonObject
            {
                ["platform"] = r.GetNullableString(0) ?? "",
                ["collectionId"] = r.GetNullableString(1) ?? "",
                ["code"] = r.GetString(2)
            }, ("$scope", scope)))
            issues.Add(iss);

        return new JsonObject
        {
            ["schema"] = 1,
            ["revision"] = st.Rev,
            ["platforms"] = platforms,
            ["pending"] = pending,
            ["confirmation"] = confirmation,
            ["approved"] = approved,
            ["status"] = new JsonObject
            {
                ["phase"] = st.SPhase ?? "idle",
                ["code"] = st.SCode ?? "",
                ["at"] = WholeOrDouble(st.SAt ?? 0),
                ["issues"] = issues
            }
        };
    }

    private static void WriteLibrarySync(DatabaseSession db, string scope, JsonObject value)
    {
        var pending = value["pending"] as JsonObject;
        var loc = pending?["locator"] as JsonObject;
        var conf = value["confirmation"] as JsonObject;
        var appr = value["approved"] as JsonObject;
        var status = value["status"] as JsonObject;

        db.Execute("""
            INSERT INTO windows_music_sync_state(
                scope,revision,
                pending_platform,pending_account_id,pending_collection_id,pending_remote_id,pending_operation,
                pending_before_present,pending_desired_present,pending_name,
                pending_locator_list_id,pending_locator_creator_id,pending_locator_creator_list_id,
                confirmation_token,confirmation_code,confirmation_count,
                approved_token,approved_code,approved_count,
                status_phase,status_at,status_code)
            VALUES(
                $scope,$rev,
                $pp,$pa,$pc,$pr,$po,
                $pbp,$pdp,$pn,
                $pll,$plc,$plcl,
                $ct,$cc,$ccnt,
                $at,$ac,$acnt,
                $sph,$sat,$scode)
            ON CONFLICT(scope) DO UPDATE SET
                revision=excluded.revision,
                pending_platform=excluded.pending_platform,
                pending_account_id=excluded.pending_account_id,
                pending_collection_id=excluded.pending_collection_id,
                pending_remote_id=excluded.pending_remote_id,
                pending_operation=excluded.pending_operation,
                pending_before_present=excluded.pending_before_present,
                pending_desired_present=excluded.pending_desired_present,
                pending_name=excluded.pending_name,
                pending_locator_list_id=excluded.pending_locator_list_id,
                pending_locator_creator_id=excluded.pending_locator_creator_id,
                pending_locator_creator_list_id=excluded.pending_locator_creator_list_id,
                confirmation_token=excluded.confirmation_token,
                confirmation_code=excluded.confirmation_code,
                confirmation_count=excluded.confirmation_count,
                approved_token=excluded.approved_token,
                approved_code=excluded.approved_code,
                approved_count=excluded.approved_count,
                status_phase=excluded.status_phase,
                status_at=excluded.status_at,
                status_code=excluded.status_code
            """,
            ("$scope", scope), ("$rev", Int64(value, "revision") ?? 0),
            ("$pp", Str(pending, "platform")), ("$pa", Str(pending, "accountId")),
            ("$pc", Str(pending, "collectionId")), ("$pr", Str(pending, "remoteId")),
            ("$po", Str(pending, "operation")),
            ("$pbp", Bool(pending, "beforePresent")), ("$pdp", Bool(pending, "desiredPresent")),
            ("$pn", Str(pending, "name")),
            ("$pll", Str(loc, "listId")), ("$plc", Str(loc, "creatorId")), ("$plcl", Str(loc, "creatorListId")),
            ("$ct", Str(conf, "token")), ("$cc", Str(conf, "code")), ("$ccnt", Int64(conf, "count")),
            ("$at", Str(appr, "token")), ("$ac", Str(appr, "code")), ("$acnt", Int64(appr, "count")),
            ("$sph", Str(status, "phase")), ("$sat", Dbl(status, "at")), ("$scode", Str(status, "code")));

        var prevPlats = db.Query("""
            SELECT platform,favorites,playlists,account_id FROM windows_music_sync_platforms WHERE scope=$scope
            """, r => (Plat: r.GetString(0), Fav: r.GetBool(1), Pls: r.GetBool(2), Acc: r.GetNullableString(3)), ("$scope", scope))
            .ToDictionary(x => x.Plat, x => (x.Fav, x.Pls, x.Acc), StringComparer.Ordinal);
        var nextPlats = new Dictionary<string, (bool Fav, bool Pls, string? Acc)>(StringComparer.Ordinal);
        if (value["platforms"] is JsonObject platforms)
            foreach (var pair in platforms)
                if (pair.Value is JsonObject p)
                    nextPlats[pair.Key] = (Bool(p, "favorites") ?? false, Bool(p, "playlists") ?? false, Str(p, "accountId"));
        foreach (var oldPlat in prevPlats.Keys)
            if (!nextPlats.ContainsKey(oldPlat))
                db.Execute("DELETE FROM windows_music_sync_platforms WHERE scope=$scope AND platform=$plat",
                    ("$scope", scope), ("$plat", oldPlat));
        foreach (var (plat, entry) in nextPlats)
        {
            if (prevPlats.TryGetValue(plat, out var prev) && prev == entry) continue;
            db.Execute("""
                INSERT INTO windows_music_sync_platforms(scope,platform,favorites,playlists,account_id)
                VALUES($scope,$plat,$fav,$pls,$acc)
                ON CONFLICT(scope,platform) DO UPDATE SET
                    favorites=excluded.favorites,playlists=excluded.playlists,account_id=excluded.account_id
                """, ("$scope", scope), ("$plat", plat), ("$fav", entry.Fav), ("$pls", entry.Pls), ("$acc", entry.Acc));
        }

        foreach (string lk in new[] { "before", "desired", "ids", "directory" })
        {
            var nextList = pending is not null ? ReadStringArray(pending[lk] as JsonArray) : [];
            var prevList = db.Query("""
                SELECT item_value FROM windows_music_sync_pending_items
                WHERE scope=$scope AND list_kind=$lk ORDER BY position
                """, r => r.GetString(0), ("$scope", scope), ("$lk", lk));
            if (prevList.Count == nextList.Length && prevList.SequenceEqual(nextList, StringComparer.Ordinal)) continue;
            int prefix = CommonPrefixLength(prevList, nextList, StringComparer.Ordinal);
            if (prefix < prevList.Count)
                db.Execute("DELETE FROM windows_music_sync_pending_items WHERE scope=$scope AND list_kind=$lk AND position>=$pos",
                    ("$scope", scope), ("$lk", lk), ("$pos", prefix));
            for (int i = prefix; i < nextList.Length; i++)
                db.Execute("""
                    INSERT INTO windows_music_sync_pending_items(scope,list_kind,position,item_value)
                    VALUES($scope,$lk,$pos,$val)
                    """, ("$scope", scope), ("$lk", lk), ("$pos", i), ("$val", nextList[i]));
        }

        var prevIssues = db.Query("""
            SELECT platform,collection_id,code FROM windows_music_sync_issues WHERE scope=$scope ORDER BY position
            """, r => (Plat: r.GetNullableString(0), ColId: r.GetNullableString(1), Code: r.GetString(2)), ("$scope", scope));
        var nextIssues = new List<(string? Plat, string? ColId, string Code)>();
        if (status?["issues"] is JsonArray issues)
            foreach (var item in issues)
                if (item is JsonObject iss)
                    nextIssues.Add((Str(iss, "platform"), Str(iss, "collectionId") ?? Str(iss, "collectionID"), Str(iss, "code") ?? ""));
        if (prevIssues.Count != nextIssues.Count || !prevIssues.SequenceEqual(nextIssues))
        {
            int prefix = CommonPrefixLength(prevIssues, nextIssues);
            if (prefix < prevIssues.Count)
                db.Execute("DELETE FROM windows_music_sync_issues WHERE scope=$scope AND position>=$pos",
                    ("$scope", scope), ("$pos", prefix));
            for (int i = prefix; i < nextIssues.Count; i++)
                db.Execute("""
                    INSERT INTO windows_music_sync_issues(scope,position,platform,collection_id,code)
                    VALUES($scope,$pos,$plat,$cid,$code)
                    """, ("$scope", scope), ("$pos", i),
                    ("$plat", nextIssues[i].Plat), ("$cid", nextIssues[i].ColId), ("$code", nextIssues[i].Code));
        }
    }

    private static string[] ReadStringArray(JsonArray? arr)
    {
        if (arr is null || arr.Count == 0) return [];
        var list = new List<string>(arr.Count);
        foreach (var item in arr)
            if (item is JsonValue v && v.TryGetValue<string>(out var text))
                list.Add(text);
        return list.ToArray();
    }
}
