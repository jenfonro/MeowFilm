using MeowFilm.Platform.Storage;
using static MeowFilm.Capabilities.Library.MusicStorageModelHelpers;

namespace MeowFilm.Capabilities.Library;

internal static class MusicSettingsStore
{
    private static string SearchHistoryRevisionOwner(string scope) => "music.search-history:" + scope;

    private static void EnsurePlaybackSettingsRow(DatabaseSession db, string scope) =>
        db.Execute("INSERT OR IGNORE INTO music_playback_settings(scope) VALUES($scope)", ("$scope", scope));

    private static void UpsertPolicyRevision(DatabaseSession db, string scope, string policyArea, long revision)
    {
        EnsurePlaybackSettingsRow(db, scope);
        db.Execute("""
            INSERT INTO windows_music_policy_revisions(scope,policy_area,revision)
            VALUES($scope,$area,$rev)
            ON CONFLICT(scope,policy_area) DO UPDATE SET revision=excluded.revision
            """, ("$scope", scope), ("$area", policyArea), ("$rev", revision));
    }

    internal static MusicPlaybackModeRecord? ReadPlaybackMode(DatabaseSession db, string scope) =>
        db.TryQuerySingle("SELECT mode FROM music_playback_settings WHERE scope=$scope",
            r => new MusicPlaybackModeRecord(r.GetInt64(0)), out var rec, ("$scope", scope))
            ? rec : null;

    internal static void WritePlaybackMode(DatabaseSession db, string scope, MusicPlaybackModeRecord value) =>
        db.Execute("""
            INSERT INTO music_playback_settings(scope,mode) VALUES($scope,$mode)
            ON CONFLICT(scope) DO UPDATE SET mode=excluded.mode
            """, ("$scope", scope), ("$mode", value.Mode));

    internal static MusicOnlineSettingsRecord? ReadOnlineSettings(DatabaseSession db, string scope) =>
        db.TryQuerySingle("""
            SELECT r.revision,s.lyrics_enabled
            FROM windows_music_policy_revisions r
            JOIN music_playback_settings s ON s.scope=r.scope
            WHERE r.scope=$scope AND r.policy_area='online'
            """,
            r => new MusicOnlineSettingsRecord(r.GetInt64(0), r.GetBool(1)),
            out var rec, ("$scope", scope)) ? rec : null;

    internal static void WriteOnlineSettings(DatabaseSession db, string scope, MusicOnlineSettingsRecord value)
    {
        db.Execute("""
            INSERT INTO music_playback_settings(scope,lyrics_enabled) VALUES($scope,$lyr)
            ON CONFLICT(scope) DO UPDATE SET lyrics_enabled=excluded.lyrics_enabled
            """, ("$scope", scope), ("$lyr", value.LyricsEnabled));
        UpsertPolicyRevision(db, scope, "online", value.Revision);
    }

    internal static MusicDeviceSettingsRecord? ReadDeviceSettings(DatabaseSession db, string scope) =>
        db.TryQuerySingle("SELECT revision,volume FROM playback_volume WHERE owner=$owner",
            r => new MusicDeviceSettingsRecord(
                r.GetInt64(0),
                Math.Clamp((long)Math.Round(r.GetDouble(1) * 100.0, MidpointRounding.AwayFromZero), 0, 100)),
            out var rec, ("$owner", scope)) ? rec : null;

    internal static void WriteDeviceSettings(DatabaseSession db, string scope, MusicDeviceSettingsRecord value) =>
        db.Execute("""
            INSERT INTO playback_volume(owner,revision,volume) VALUES($owner,$rev,$vol)
            ON CONFLICT(owner) DO UPDATE SET revision=excluded.revision,volume=excluded.volume
            """,
            ("$owner", scope),
            ("$rev", value.Revision),
            ("$vol", Math.Clamp(value.Volume, 0, 100) / 100.0));

    internal static MusicAppearanceRecord? ReadAppearance(DatabaseSession db, string scope) =>
        db.TryQuerySingle("""
            SELECT revision,playlist_view,player_theme,color_mode,color_palette
            FROM music_appearance WHERE scope=$scope
            """,
            r => new MusicAppearanceRecord(
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)),
            out var rec, ("$scope", scope)) ? rec : null;

    internal static void WriteAppearance(DatabaseSession db, string scope, MusicAppearanceRecord value) =>
        db.Execute("""
            INSERT INTO music_appearance(scope,revision,playlist_view,player_theme,color_mode,color_palette)
            VALUES($scope,$rev,$pv,$pt,$cm,$cp)
            ON CONFLICT(scope) DO UPDATE SET
                revision=excluded.revision,playlist_view=excluded.playlist_view,
                player_theme=excluded.player_theme,color_mode=excluded.color_mode,color_palette=excluded.color_palette
            """,
            ("$scope", scope), ("$rev", value.Revision),
            ("$pv", value.PlaylistView), ("$pt", value.PlayerTheme),
            ("$cm", value.ColorMode), ("$cp", value.ColorPalette));

    internal static MusicCatalogSourceRecord? ReadCatalogSource(DatabaseSession db, string scope) =>
        db.TryQuerySingle("SELECT revision,platform FROM music_catalog_source WHERE scope=$scope",
            r => new MusicCatalogSourceRecord(r.GetInt64(0), r.GetString(1)),
            out var rec, ("$scope", scope)) ? rec : null;

    internal static void WriteCatalogSource(DatabaseSession db, string scope, MusicCatalogSourceRecord value) =>
        db.Execute("""
            INSERT INTO music_catalog_source(scope,revision,platform) VALUES($scope,$rev,$plat)
            ON CONFLICT(scope) DO UPDATE SET revision=excluded.revision,platform=excluded.platform
            """, ("$scope", scope), ("$rev", value.Revision), ("$plat", value.Platform));

    internal static MusicDesktopLyricsRecord? ReadDesktopLyrics(DatabaseSession db, string scope) =>
        db.TryQuerySingle("""
            SELECT revision,layout,normal_color,highlight_color,geom_monitor,geom_x,geom_y,geom_width,geom_height
            FROM windows_music_desktop_lyrics WHERE scope=$scope
            """,
            r =>
            {
                MusicDesktopLyricsGeometryRecord? geom = r.GetNullableString(4) is { } mon
                    ? new MusicDesktopLyricsGeometryRecord(
                        mon,
                        r.GetNullableDouble(5) ?? 0,
                        r.GetNullableDouble(6) ?? 0,
                        r.GetNullableDouble(7) ?? 0,
                        r.GetNullableDouble(8) ?? 0)
                    : null;
                return new MusicDesktopLyricsRecord(
                    r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), geom);
            },
            out var result, ("$scope", scope)) ? result : null;

    internal static void WriteDesktopLyrics(DatabaseSession db, string scope, MusicDesktopLyricsRecord value)
    {
        var geom = value.Geometry;
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
            ("$scope", scope), ("$rev", value.Revision),
            ("$lay", value.Layout), ("$nc", value.NormalColor), ("$hc", value.HighlightColor),
            ("$gm", geom?.Monitor), ("$gx", geom?.X), ("$gy", geom?.Y),
            ("$gw", geom?.Width), ("$gh", geom?.Height));
    }

    internal static MusicQualityPolicyRecord? ReadQualityPolicy(DatabaseSession db, string scope)
    {
        if (!db.TryQuerySingle("""
            SELECT r.revision,s.quality_request
            FROM windows_music_policy_revisions r
            JOIN music_playback_settings s ON s.scope=r.scope
            WHERE r.scope=$scope AND r.policy_area='quality'
            """,
            r => (Rev: r.GetInt64(0), Req: r.GetNullableString(1) ?? ""), out var row, ("$scope", scope)))
            return null;

        List<string> qOrd = [], qEn = [], sOrd = [], sEn = [];
        foreach (var (lk, val) in db.Query("""
            SELECT list_kind,item_value FROM music_playback_lists
            WHERE scope=$scope AND list_kind IN ('qualityOrder','qualityEnabled','spatialOrder','spatialEnabled')
            ORDER BY list_kind, position
            """, r => (r.GetString(0), r.GetString(1)), ("$scope", scope)))
        {
            switch (lk)
            {
                case "qualityOrder": qOrd.Add(val); break;
                case "qualityEnabled": qEn.Add(val); break;
                case "spatialOrder": sOrd.Add(val); break;
                case "spatialEnabled": sEn.Add(val); break;
            }
        }
        return new MusicQualityPolicyRecord(row.Rev, row.Req, qOrd.ToArray(), qEn.ToArray(), sOrd.ToArray(), sEn.ToArray());
    }

    internal static void WriteQualityPolicy(DatabaseSession db, string scope, MusicQualityPolicyRecord? previous, MusicQualityPolicyRecord value)
    {
        db.Execute("""
            INSERT INTO music_playback_settings(scope,quality_request) VALUES($scope,$req)
            ON CONFLICT(scope) DO UPDATE SET quality_request=excluded.quality_request
            """, ("$scope", scope), ("$req", value.Request));
        UpsertPolicyRevision(db, scope, "quality", value.Revision);
        SyncPlaybackList(db, scope, "qualityOrder", previous?.QualityOrder, value.QualityOrder);
        SyncPlaybackList(db, scope, "qualityEnabled", previous?.QualityEnabled, value.QualityEnabled);
        SyncPlaybackList(db, scope, "spatialOrder", previous?.SpatialOrder, value.SpatialOrder);
        SyncPlaybackList(db, scope, "spatialEnabled", previous?.SpatialEnabled, value.SpatialEnabled);
    }

    internal static MusicOfficialSourcesRecord? ReadOfficialSources(DatabaseSession db, string scope)
    {
        if (!db.TryQuerySingle("""
            SELECT revision FROM windows_music_policy_revisions
            WHERE scope=$scope AND policy_area='official'
            """,
            r => r.GetInt64(0), out long rev, ("$scope", scope)))
            return null;

        List<string> platforms = [], enabled = [];
        foreach (var (lk, val) in db.Query("""
            SELECT list_kind,item_value FROM music_playback_lists
            WHERE scope=$scope AND list_kind IN ('officialPlatforms','officialEnabled')
            ORDER BY list_kind, position
            """, r => (r.GetString(0), r.GetString(1)), ("$scope", scope)))
        {
            if (lk == "officialEnabled") enabled.Add(val);
            else platforms.Add(val);
        }
        return new MusicOfficialSourcesRecord(rev, platforms.ToArray(), enabled.ToArray());
    }

    internal static void WriteOfficialSources(DatabaseSession db, string scope, MusicOfficialSourcesRecord? previous, MusicOfficialSourcesRecord value)
    {
        UpsertPolicyRevision(db, scope, "official", value.Revision);
        SyncPlaybackList(db, scope, "officialPlatforms", previous?.Platforms, value.Platforms);
        SyncPlaybackList(db, scope, "officialEnabled", previous?.Enabled, value.Enabled);
    }

    internal static MusicMetadataPolicyRecord? ReadMetadataPolicy(DatabaseSession db, string scope)
    {
        if (!db.TryQuerySingle("""
            SELECT revision FROM windows_music_policy_revisions
            WHERE scope=$scope AND policy_area='metadata'
            """,
            r => r.GetInt64(0), out long rev, ("$scope", scope)))
            return null;

        List<string> platforms = [], enabled = [];
        foreach (var (lk, val) in db.Query("""
            SELECT list_kind,item_value FROM music_playback_lists
            WHERE scope=$scope AND list_kind IN ('metadataPlatforms','metadataEnabled')
            ORDER BY list_kind, position
            """, r => (r.GetString(0), r.GetString(1)), ("$scope", scope)))
        {
            if (lk == "metadataPlatforms") platforms.Add(val);
            else enabled.Add(val);
        }
        return new MusicMetadataPolicyRecord(rev, platforms.ToArray(), enabled.ToArray());
    }

    internal static void WriteMetadataPolicy(DatabaseSession db, string scope, MusicMetadataPolicyRecord? previous, MusicMetadataPolicyRecord value)
    {
        UpsertPolicyRevision(db, scope, "metadata", value.Revision);
        SyncPlaybackList(db, scope, "metadataPlatforms", previous?.Platforms, value.Platforms);
        SyncPlaybackList(db, scope, "metadataEnabled", previous?.Enabled, value.Enabled);
    }

    private static void SyncPlaybackList(DatabaseSession db, string scope, string listKind, string[]? previous, string[] next)
    {
        if (previous is not null && previous.SequenceEqual(next, StringComparer.Ordinal)) return;
        int start = previous is null ? 0 : CommonPrefixLength(previous, next, StringComparer.Ordinal);
        if (previous is null || start < previous.Length)
            db.Execute("DELETE FROM music_playback_lists WHERE scope=$scope AND list_kind=$lk AND position>=$pos",
                ("$scope", scope), ("$lk", listKind), ("$pos", start));
        for (int i = start; i < next.Length; i++)
            db.Execute("INSERT INTO music_playback_lists(scope,list_kind,position,item_value) VALUES($scope,$lk,$pos,$val)",
                ("$scope", scope), ("$lk", listKind), ("$pos", i), ("$val", next[i]));
    }

    internal static MusicSearchPolicyRecord? ReadSearchPolicy(DatabaseSession db, string scope)
    {
        if (!db.TryQuerySingle("""
            SELECT revision,songs_pages,songs_concurrency,songs_cache_minutes,
                   playlists_pages,playlists_concurrency,playlists_cache_minutes
            FROM music_search_policy WHERE scope=$scope
            """,
            r => (Rev: r.GetInt64(0), SP: r.GetInt64(1), SC: r.GetInt64(2), SM: r.GetInt64(3),
                PP: r.GetInt64(4), PC: r.GetInt64(5), PM: r.GetInt64(6)),
            out var row, ("$scope", scope)))
            return null;

        List<string> sOrd = [], sEn = [], pOrd = [], pEn = [];
        foreach (var (tk, lk, val) in db.Query("""
            SELECT target_kind,list_kind,platform FROM music_search_policy_platforms
            WHERE scope=$scope ORDER BY target_kind, list_kind, position
            """, r => (r.GetString(0), r.GetString(1), r.GetString(2)), ("$scope", scope)))
        {
            if (tk == "songs") (lk == "order" ? sOrd : sEn).Add(val);
            else (lk == "order" ? pOrd : pEn).Add(val);
        }

        return new MusicSearchPolicyRecord(
            row.Rev,
            new MusicSearchKindRecord(sOrd.ToArray(), sEn.ToArray(), row.SP, row.SC, row.SM),
            new MusicSearchKindRecord(pOrd.ToArray(), pEn.ToArray(), row.PP, row.PC, row.PM));
    }

    internal static void WriteSearchPolicy(DatabaseSession db, string scope, MusicSearchPolicyRecord? previous, MusicSearchPolicyRecord value)
    {
        db.Execute("""
            INSERT INTO music_search_policy(
                scope,revision,songs_pages,songs_concurrency,songs_cache_minutes,
                playlists_pages,playlists_concurrency,playlists_cache_minutes)
            VALUES($scope,$rev,$sp,$sc,$sm,$pp,$pc,$pm)
            ON CONFLICT(scope) DO UPDATE SET
                revision=excluded.revision,
                songs_pages=excluded.songs_pages,songs_concurrency=excluded.songs_concurrency,songs_cache_minutes=excluded.songs_cache_minutes,
                playlists_pages=excluded.playlists_pages,playlists_concurrency=excluded.playlists_concurrency,playlists_cache_minutes=excluded.playlists_cache_minutes
            """,
            ("$scope", scope), ("$rev", value.Revision),
            ("$sp", value.Songs.Pages), ("$sc", value.Songs.Concurrency), ("$sm", value.Songs.CacheMinutes),
            ("$pp", value.Playlists.Pages), ("$pc", value.Playlists.Concurrency), ("$pm", value.Playlists.CacheMinutes));

        SyncSearchPolicyList(db, scope, "songs", "order", previous?.Songs.Order, value.Songs.Order);
        SyncSearchPolicyList(db, scope, "songs", "enabled", previous?.Songs.Enabled, value.Songs.Enabled);
        SyncSearchPolicyList(db, scope, "playlists", "order", previous?.Playlists.Order, value.Playlists.Order);
        SyncSearchPolicyList(db, scope, "playlists", "enabled", previous?.Playlists.Enabled, value.Playlists.Enabled);
    }

    private static void SyncSearchPolicyList(DatabaseSession db, string scope, string targetKind, string listKind, string[]? previous, string[] next)
    {
        if (previous is not null && previous.SequenceEqual(next, StringComparer.Ordinal)) return;
        int start = previous is null ? 0 : CommonPrefixLength(previous, next, StringComparer.Ordinal);
        if (previous is null || start < previous.Length)
            db.Execute("""
                DELETE FROM music_search_policy_platforms
                WHERE scope=$scope AND target_kind=$tk AND list_kind=$lk AND position>=$pos
                """, ("$scope", scope), ("$tk", targetKind), ("$lk", listKind), ("$pos", start));
        for (int i = start; i < next.Length; i++)
            db.Execute("""
                INSERT INTO music_search_policy_platforms(scope,target_kind,list_kind,position,platform)
                VALUES($scope,$tk,$lk,$pos,$plat)
                """, ("$scope", scope), ("$tk", targetKind), ("$lk", listKind), ("$pos", i), ("$plat", next[i]));
    }

    internal static MusicSearchHistoryRecord? ReadSearchHistory(DatabaseSession db, string scope)
    {
        var rev = db.Revision(SearchHistoryRevisionOwner(scope));
        var items = db.Query("""
            SELECT query_text,kind,used_at_ms FROM windows_music_search_history
            WHERE scope=$scope ORDER BY position
            """, r => new MusicSearchHistoryItemRecord(r.GetString(0), r.GetString(1), r.GetInt64(2)), ("$scope", scope)).ToArray();

        if (rev.Revision == 0 && items.Length == 0) return null;
        return new MusicSearchHistoryRecord(rev.Revision, items);
    }

    internal static void WriteSearchHistory(DatabaseSession db, string scope, MusicSearchHistoryRecord? previous, MusicSearchHistoryRecord value)
    {
        string owner = SearchHistoryRevisionOwner(scope);
        if (value.Revision >= 1)
        {
            db.Execute("""
                INSERT INTO owner_revisions(owner,revision) VALUES($owner,$rev)
                ON CONFLICT(owner) DO UPDATE SET revision=excluded.revision
                """, ("$owner", owner), ("$rev", value.Revision));
        }
        else
        {
            db.Execute("DELETE FROM owner_revisions WHERE owner=$owner", ("$owner", owner));
        }

        if (value.Items.Length == 0)
        {
            db.Execute("DELETE FROM windows_music_search_history WHERE scope=$scope", ("$scope", scope));
            return;
        }
        if (previous is null)
        {
            db.Execute("DELETE FROM windows_music_search_history WHERE scope=$scope", ("$scope", scope));
            for (int i = 0; i < value.Items.Length; i++)
            {
                var item = value.Items[i];
                db.Execute("""
                    INSERT INTO windows_music_search_history(scope,kind,query_text,position,used_at_ms)
                    VALUES($scope,$kind,$txt,$pos,$at)
                    """, ("$scope", scope), ("$kind", item.Kind), ("$txt", item.Text), ("$pos", i), ("$at", item.UsedAtMs));
            }
            return;
        }
        var existing = previous.Items.Select((item, idx) => (Key: (item.Kind, item.Text), Pos: idx, UsedAt: item.UsedAtMs))
            .ToDictionary(x => x.Key, x => (x.Pos, x.UsedAt));
        var nextKeys = value.Items.Select(item => (item.Kind, item.Text)).ToHashSet();
        foreach (var oldKey in existing.Keys)
            if (!nextKeys.Contains(oldKey))
                db.Execute("DELETE FROM windows_music_search_history WHERE scope=$scope AND kind=$kind AND query_text=$txt",
                    ("$scope", scope), ("$kind", oldKey.Kind), ("$txt", oldKey.Text));
        for (int i = 0; i < value.Items.Length; i++)
        {
            var item = value.Items[i];
            if (existing.TryGetValue((item.Kind, item.Text), out var old) && old.Pos == i && old.UsedAt == item.UsedAtMs)
                continue;
            db.Execute("""
                INSERT INTO windows_music_search_history(scope,kind,query_text,position,used_at_ms)
                VALUES($scope,$kind,$txt,$pos,$at)
                ON CONFLICT(scope,kind,query_text) DO UPDATE SET
                    position=excluded.position,
                    used_at_ms=excluded.used_at_ms
                """,
                ("$scope", scope), ("$kind", item.Kind), ("$txt", item.Text), ("$pos", i), ("$at", item.UsedAtMs));
        }
    }

    internal static long? ReadSourceExclusionsRevision(DatabaseSession db, string scope) =>
        db.TryQuerySingle("""
            SELECT revision FROM windows_music_policy_revisions
            WHERE scope=$scope AND policy_area='exclusions'
            """, r => r.GetInt64(0), out long rev, ("$scope", scope))
            ? rev : null;

    internal static void WriteSourceExclusionsRevision(DatabaseSession db, string scope, long revision) =>
        UpsertPolicyRevision(db, scope, "exclusions", revision);

    internal static IReadOnlyList<MusicSourceExclusionRecord> ReadSourceExclusions(
        DatabaseSession db, string scope, string? exclusionId = null)
    {
        string sql = exclusionId is not null
            ? """
              SELECT exclusion_id,position,song_id,provider,platform,provider_name,title,artist,excluded_at_ms
              FROM music_source_exclusions
              WHERE scope=$scope AND exclusion_id=$key
              ORDER BY position
              """
            : """
              SELECT exclusion_id,position,song_id,provider,platform,provider_name,title,artist,excluded_at_ms
              FROM music_source_exclusions
              WHERE scope=$scope
              ORDER BY position
              """;
        var parameters = exclusionId is not null
            ? new (string, object?)[] { ("$scope", scope), ("$key", exclusionId) }
            : new (string, object?)[] { ("$scope", scope) };
        return db.Query(sql, r => new MusicSourceExclusionRecord(
            r.GetString(0),
            r.GetInt32(1),
            r.GetString(2),
            r.GetString(3),
            r.GetString(4),
            r.GetString(5),
            r.GetString(6),
            r.GetString(7),
            r.GetInt64(8)), parameters);
    }

    internal static void ApplySourceExclusionMutation(DatabaseSession db, string scope, MusicSourceExclusionMutation mutation)
    {
        if (mutation.Next is null)
        {
            db.Execute("""
                DELETE FROM music_source_exclusions
                WHERE scope=$scope AND exclusion_id=$key
                """, ("$scope", scope), ("$key", mutation.ExclusionId));
            return;
        }
        EnsurePlaybackSettingsRow(db, scope);
        var item = mutation.Next;
        db.Execute("""
            INSERT INTO music_source_exclusions(
                scope,exclusion_id,position,song_id,provider,platform,provider_name,title,artist,excluded_at_ms)
            VALUES($scope,$key,$pos,$sid,$prov,$plat,$pname,$title,$artist,$at)
            ON CONFLICT(scope,exclusion_id) DO UPDATE SET
                position=excluded.position,
                song_id=excluded.song_id,
                provider=excluded.provider,
                platform=excluded.platform,
                provider_name=excluded.provider_name,
                title=excluded.title,
                artist=excluded.artist,
                excluded_at_ms=excluded.excluded_at_ms
            """,
            ("$scope", scope), ("$key", mutation.ExclusionId), ("$pos", mutation.Ordinal),
            ("$sid", item.SongId), ("$prov", item.Provider), ("$plat", item.Platform),
            ("$pname", item.ProviderName), ("$title", item.Title), ("$artist", item.Artist),
            ("$at", item.ExcludedAtMs));
    }

    internal static IReadOnlyList<MusicLocalLyricSummaryRecord> ReadLocalLyricSummaries(
        DatabaseSession db, string scope, string? songId = null)
    {
        string sql = songId is not null
            ? "SELECT song_id,position,content_hash FROM windows_music_local_lyrics WHERE scope=$scope AND song_id=$sid ORDER BY position"
            : "SELECT song_id,position,content_hash FROM windows_music_local_lyrics WHERE scope=$scope ORDER BY position";
        var parameters = songId is not null
            ? new (string, object?)[] { ("$scope", scope), ("$sid", songId) }
            : new (string, object?)[] { ("$scope", scope) };
        return db.Query(sql, r => new MusicLocalLyricSummaryRecord(r.GetString(0), r.GetInt32(1), r.GetString(2)), parameters);
    }

    internal static MusicLocalLyricRecord? ReadLocalLyric(DatabaseSession db, string scope, string songId)
    {
        if (!db.TryQuerySingle("""
            SELECT position,content_hash FROM windows_music_local_lyrics WHERE scope=$scope AND song_id=$sid
            """, r => (Pos: r.GetInt32(0), Hash: r.GetString(1)), out var head, ("$scope", scope), ("$sid", songId)))
            return null;

        var wordsByLine = new Dictionary<int, List<MusicLyricWordRecord>>();
        foreach (var item in db.Query("""
            SELECT line_index,time_sec,text,duration_sec,offset_sec
            FROM windows_music_local_lyric_words
            WHERE scope=$scope AND song_id=$sid
            ORDER BY line_index, word_index
            """, r => (LineIdx: r.GetInt32(0), Word: new MusicLyricWordRecord(
                r.GetDouble(1), r.GetString(2), r.GetDouble(3), r.GetNullableDouble(4))),
            ("$scope", scope), ("$sid", songId)))
        {
            if (!wordsByLine.TryGetValue(item.LineIdx, out var list)) wordsByLine[item.LineIdx] = list = [];
            list.Add(item.Word);
        }

        var lines = db.Query("""
            SELECT line_index,time_sec,text,translation,romanization,end_sec
            FROM windows_music_local_lyric_lines
            WHERE scope=$scope AND song_id=$sid
            ORDER BY line_index
            """, r =>
        {
            int idx = r.GetInt32(0);
            return new MusicLyricLineRecord(
                r.GetDouble(1),
                r.GetString(2),
                r.GetNullableString(3),
                r.GetNullableString(4),
                r.GetNullableDouble(5),
                wordsByLine.TryGetValue(idx, out var ws) ? ws.ToArray() : []);
        }, ("$scope", scope), ("$sid", songId)).ToArray();

        return new MusicLocalLyricRecord(songId, head.Pos, head.Hash, lines);
    }

    internal static void ApplyLocalLyricMutation(DatabaseSession db, string scope, MusicLocalLyricMutation mutation)
    {
        if (mutation.Next is null)
        {
            db.Execute("DELETE FROM windows_music_local_lyrics WHERE scope=$scope AND song_id=$sid",
                ("$scope", scope), ("$sid", mutation.SongId));
            return;
        }
        WriteLocalLyricItem(db, scope, mutation.Next.SongId, mutation.Ordinal, mutation.Next.ContentHash, mutation.Next.Lines);
    }

    private static void WriteLocalLyricItem(DatabaseSession db, string scope, string songId, int position,
        string contentHash, MusicLyricLineRecord[] lines)
    {
        db.Execute("""
            INSERT INTO windows_music_local_lyrics(scope,song_id,position,content_hash)
            VALUES($scope,$sid,$pos,$hash)
            ON CONFLICT(scope,song_id) DO UPDATE SET position=excluded.position,content_hash=excluded.content_hash
            """, ("$scope", scope), ("$sid", songId), ("$pos", position), ("$hash", contentHash));
        db.Execute("DELETE FROM windows_music_local_lyric_lines WHERE scope=$scope AND song_id=$sid",
            ("$scope", scope), ("$sid", songId));

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            db.Execute("""
                INSERT INTO windows_music_local_lyric_lines(scope,song_id,line_index,time_sec,text,translation,romanization,end_sec)
                VALUES($scope,$sid,$idx,$time,$text,$tr,$roma,$end)
                """,
                ("$scope", scope), ("$sid", songId), ("$idx", i),
                ("$time", line.TimeSec), ("$text", line.Text),
                ("$tr", line.Translation), ("$roma", line.Romanization), ("$end", line.EndSec));
            for (int w = 0; w < line.Words.Length; w++)
            {
                var word = line.Words[w];
                db.Execute("""
                    INSERT INTO windows_music_local_lyric_words(scope,song_id,line_index,word_index,time_sec,text,duration_sec,offset_sec)
                    VALUES($scope,$sid,$lidx,$widx,$time,$text,$dur,$off)
                    """,
                    ("$scope", scope), ("$sid", songId), ("$lidx", i), ("$widx", w),
                    ("$time", word.TimeSec), ("$text", word.Text), ("$dur", word.DurationSec), ("$off", word.OffsetSec));
            }
        }
    }

    internal static MusicLibrarySyncStateRecord? ReadLibrarySync(DatabaseSession db, string scope)
    {
        if (!db.TryQuerySingle("""
            SELECT revision,
                   pending_platform,pending_account_id,pending_collection_id,pending_remote_id,pending_operation,
                   pending_before_present,pending_desired_present,pending_name,
                   pending_locator_list_id,pending_locator_creator_id,pending_locator_creator_list_id,
                   confirmation_token,confirmation_code,confirmation_count,
                   approved_token,approved_code,approved_count,
                   status_phase,status_at_ms,status_code
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
                SPhase: r.GetNullableString(18), SAtMs: r.GetNullableInt64(19), SCode: r.GetNullableString(20)),
            out var st, ("$scope", scope)))
            return null;

        var platforms = new Dictionary<string, MusicSyncPlatformRecord>(StringComparer.Ordinal);
        foreach (var (plat, fav, pls, acc) in db.Query("""
            SELECT platform,favorites,playlists,account_id FROM windows_music_sync_platforms WHERE scope=$scope ORDER BY platform
            """, r => (r.GetString(0), r.GetBool(1), r.GetBool(2), r.GetNullableString(3) ?? ""), ("$scope", scope)))
            platforms[plat] = new MusicSyncPlatformRecord(fav, pls, acc);

        MusicSyncPendingRecord? pending = null;
        if (st.POp is not null)
        {
            List<string> before = [], desired = [], ids = [], directory = [];
            if (st.POp is not ("subscribe" or "unsubscribe"))
            {
                foreach (var (lk, val) in db.Query("""
                    SELECT list_kind,item_value FROM windows_music_sync_pending_items WHERE scope=$scope ORDER BY list_kind, position
                    """, r => (r.GetString(0), r.GetString(1)), ("$scope", scope)))
                {
                    switch (lk)
                    {
                        case "before": before.Add(val); break;
                        case "desired": desired.Add(val); break;
                        case "ids": ids.Add(val); break;
                        case "directory": directory.Add(val); break;
                    }
                }
            }
            pending = new MusicSyncPendingRecord(
                st.PPlat ?? "",
                st.PAcc ?? "",
                st.PCol ?? "",
                st.PRem ?? "",
                st.POp,
                st.PBP,
                st.PDP,
                st.PName ?? "",
                st.PLL,
                st.PLC,
                st.PLCL,
                before.ToArray(),
                desired.ToArray(),
                ids.ToArray(),
                directory.ToArray());
        }

        MusicSyncConfirmationRecord? confirmation = st.CTok is not null
            ? new MusicSyncConfirmationRecord(st.CTok, st.CCode ?? "", st.CCnt ?? 0) : null;
        MusicSyncConfirmationRecord? approved = st.ATok is not null
            ? new MusicSyncConfirmationRecord(st.ATok, st.ACode ?? "", st.ACnt ?? 0) : null;

        var issues = db.Query("""
            SELECT platform,collection_id,code FROM windows_music_sync_issues WHERE scope=$scope ORDER BY position
            """, r => new MusicSyncIssueRecord(
                r.GetNullableString(0) ?? "",
                r.GetNullableString(1) ?? "",
                r.GetString(2)), ("$scope", scope)).ToArray();

        return new MusicLibrarySyncStateRecord(
            st.Rev,
            platforms,
            pending,
            confirmation,
            approved,
            st.SPhase ?? "idle",
            st.SAtMs ?? 0,
            st.SCode ?? "",
            issues);
    }

    internal static void WriteLibrarySync(DatabaseSession db, string scope, MusicLibrarySyncStateRecord? previous, MusicLibrarySyncStateRecord value)
    {
        var pending = value.Pending;
        var conf = value.Confirmation;
        var appr = value.Approved;

        db.Execute("""
            INSERT INTO windows_music_sync_state(
                scope,revision,
                pending_platform,pending_account_id,pending_collection_id,pending_remote_id,pending_operation,
                pending_before_present,pending_desired_present,pending_name,
                pending_locator_list_id,pending_locator_creator_id,pending_locator_creator_list_id,
                confirmation_token,confirmation_code,confirmation_count,
                approved_token,approved_code,approved_count,
                status_phase,status_at_ms,status_code)
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
                status_at_ms=excluded.status_at_ms,
                status_code=excluded.status_code
            """,
            ("$scope", scope), ("$rev", value.Revision),
            ("$pp", pending?.Platform), ("$pa", pending?.AccountId),
            ("$pc", pending?.CollectionId), ("$pr", pending?.RemoteId),
            ("$po", pending?.Operation),
            ("$pbp", pending?.BeforePresent), ("$pdp", pending?.DesiredPresent),
            ("$pn", pending?.Name),
            ("$pll", pending?.LocatorListId), ("$plc", pending?.LocatorCreatorId), ("$plcl", pending?.LocatorCreatorListId),
            ("$ct", conf?.Token), ("$cc", conf?.Code), ("$ccnt", conf?.Count),
            ("$at", appr?.Token), ("$ac", appr?.Code), ("$acnt", appr?.Count),
            ("$sph", value.StatusPhase), ("$sat", value.StatusAtMs), ("$scode", value.StatusCode));

        if (previous is null)
        {
            db.Execute("DELETE FROM windows_music_sync_platforms WHERE scope=$scope", ("$scope", scope));
            foreach (var (plat, entry) in value.Platforms)
                db.Execute("""
                    INSERT INTO windows_music_sync_platforms(scope,platform,favorites,playlists,account_id)
                    VALUES($scope,$plat,$fav,$pls,$acc)
                    """, ("$scope", scope), ("$plat", plat), ("$fav", entry.Favorites), ("$pls", entry.Playlists), ("$acc", entry.AccountId));
        }
        else
        {
            foreach (var oldPlat in previous.Platforms.Keys)
                if (!value.Platforms.ContainsKey(oldPlat))
                    db.Execute("DELETE FROM windows_music_sync_platforms WHERE scope=$scope AND platform=$plat",
                        ("$scope", scope), ("$plat", oldPlat));
            foreach (var (plat, entry) in value.Platforms)
            {
                if (previous.Platforms.TryGetValue(plat, out var prevEntry) && prevEntry == entry) continue;
                db.Execute("""
                    INSERT INTO windows_music_sync_platforms(scope,platform,favorites,playlists,account_id)
                    VALUES($scope,$plat,$fav,$pls,$acc)
                    ON CONFLICT(scope,platform) DO UPDATE SET
                        favorites=excluded.favorites,
                        playlists=excluded.playlists,
                        account_id=excluded.account_id
                    """, ("$scope", scope), ("$plat", plat), ("$fav", entry.Favorites), ("$pls", entry.Playlists), ("$acc", entry.AccountId));
            }
        }

        var prevPending = previous?.Pending is { } pp0 && pp0.Operation is not ("subscribe" or "unsubscribe") ? pp0 : null;
        var nextPending = pending is not null && pending.Operation is not ("subscribe" or "unsubscribe") ? pending : null;
        if (nextPending is null)
        {
            if (previous is null || prevPending is not null)
                db.Execute("DELETE FROM windows_music_sync_pending_items WHERE scope=$scope", ("$scope", scope));
        }
        else
        {
            SyncPendingList(db, scope, "before", previous is null ? null : (prevPending?.Before ?? []), nextPending.Before);
            SyncPendingList(db, scope, "desired", previous is null ? null : (prevPending?.Desired ?? []), nextPending.Desired);
            SyncPendingList(db, scope, "ids", previous is null ? null : (prevPending?.Ids ?? []), nextPending.Ids);
            SyncPendingList(db, scope, "directory", previous is null ? null : (prevPending?.Directory ?? []), nextPending.Directory);
        }

        if (previous is null || !previous.Issues.SequenceEqual(value.Issues))
        {
            int start = previous is null ? 0 : CommonPrefixLength(previous.Issues, value.Issues);
            if (previous is null || start < previous.Issues.Length)
                db.Execute("DELETE FROM windows_music_sync_issues WHERE scope=$scope AND position>=$pos",
                    ("$scope", scope), ("$pos", start));
            for (int i = start; i < value.Issues.Length; i++)
            {
                var iss = value.Issues[i];
                db.Execute("""
                    INSERT INTO windows_music_sync_issues(scope,position,platform,collection_id,code)
                    VALUES($scope,$pos,$plat,$cid,$code)
                    """, ("$scope", scope), ("$pos", i), ("$plat", iss.Platform), ("$cid", iss.CollectionId), ("$code", iss.Code));
            }
        }
    }

    private static void SyncPendingList(DatabaseSession db, string scope, string listKind, string[]? previous, string[] next)
    {
        if (previous is not null && previous.SequenceEqual(next, StringComparer.Ordinal)) return;
        int start = previous is null ? 0 : CommonPrefixLength(previous, next, StringComparer.Ordinal);
        if (previous is null || start < previous.Length)
            db.Execute("""
                DELETE FROM windows_music_sync_pending_items
                WHERE scope=$scope AND list_kind=$lk AND position>=$pos
                """, ("$scope", scope), ("$lk", listKind), ("$pos", start));
        for (int i = start; i < next.Length; i++)
            db.Execute("""
                INSERT INTO windows_music_sync_pending_items(scope,list_kind,position,item_value)
                VALUES($scope,$lk,$pos,$val)
                """, ("$scope", scope), ("$lk", listKind), ("$pos", i), ("$val", next[i]));
    }
}
