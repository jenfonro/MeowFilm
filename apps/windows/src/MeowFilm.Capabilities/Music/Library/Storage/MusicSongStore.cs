using MeowFilm.Platform.Storage;
using static MeowFilm.Capabilities.Library.MusicStorageModelHelpers;

namespace MeowFilm.Capabilities.Library;

internal static class MusicSongStore
{
    private const int BatchChunkSize = 200;

    internal static MusicSongRecord? ReadRecord(DatabaseSession db, string songId) =>
        LoadSongsBatch(db, [songId]).GetValueOrDefault(songId);

    internal static Dictionary<string, MusicSongRecord> LoadSongsBatch(DatabaseSession db, IReadOnlyList<string> songIds)
    {
        var result = new Dictionary<string, MusicSongRecord>(StringComparer.Ordinal);
        if (songIds.Count == 0) return result;
        var unique = new List<string>(songIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in songIds)
            if (!string.IsNullOrEmpty(id) && seen.Add(id))
                unique.Add(id);
        if (unique.Count == 0) return result;

        for (int offset = 0; offset < unique.Count; offset += BatchChunkSize)
        {
            int count = Math.Min(BatchChunkSize, unique.Count - offset);
            var paramNames = new string[count];
            var parameters = new (string Name, object? Value)[count];
            for (int i = 0; i < count; i++)
            {
                string name = "$id" + i;
                paramNames[i] = name;
                parameters[i] = (name, unique[offset + i]);
            }
            string inClause = string.Join(',', paramNames);

            var headers = new Dictionary<string, (
                string Title, string Artist, string Album, double? Duration, double? BaselineDuration,
                string CoverUrl, string? LocalFileId, string? ArtworkSourceKey, string? ArtworkSourceImage)>(count, StringComparer.Ordinal);

            foreach (var row in db.Query($"""
                SELECT s.song_id,s.title,s.artist,s.album,s.duration,s.baseline_duration,s.cover_url,
                       lf.local_file_id,s.artwork_source_key,s.artwork_source_image
                FROM music_songs s
                LEFT JOIN windows_music_song_local_files lf ON lf.song_id=s.song_id
                WHERE s.song_id IN ({inClause})
                """,
                row => (
                    SongId: row.GetString(0),
                    Title: row.GetString(1),
                    Artist: row.GetString(2),
                    Album: row.GetString(3),
                    Duration: row.GetNullableDouble(4),
                    BaselineDuration: row.GetNullableDouble(5),
                    CoverUrl: row.GetString(6),
                    LocalFileId: row.GetNullableString(7),
                    ArtworkSourceKey: row.GetNullableString(8),
                    ArtworkSourceImage: row.GetNullableString(9)),
                parameters))
            {
                headers[row.SongId] = (row.Title, row.Artist, row.Album, row.Duration, row.BaselineDuration,
                    row.CoverUrl, row.LocalFileId, row.ArtworkSourceKey, row.ArtworkSourceImage);
            }
            if (headers.Count == 0) continue;

            var songArtists = new Dictionary<string, List<string>>(headers.Count, StringComparer.Ordinal);
            foreach (var item in db.Query($"""
                SELECT song_id,name FROM music_song_artists
                WHERE song_id IN ({inClause})
                ORDER BY song_id, position
                """, r => (SongId: r.GetString(0), Name: r.GetString(1)), parameters))
            {
                if (!songArtists.TryGetValue(item.SongId, out var list))
                    songArtists[item.SongId] = list = [];
                list.Add(item.Name);
            }

            var srcArtists = new Dictionary<(string SongId, string SourceKey), List<string>>();
            foreach (var item in db.Query($"""
                SELECT song_id,source_key,name FROM music_song_source_artists
                WHERE song_id IN ({inClause})
                ORDER BY song_id, source_key, position
                """, r => (SongId: r.GetString(0), Key: r.GetString(1), Name: r.GetString(2)), parameters))
            {
                var key = (item.SongId, item.Key);
                if (!srcArtists.TryGetValue(key, out var list))
                    srcArtists[key] = list = [];
                list.Add(item.Name);
            }

            var srcExtras = ReadSourceExtrasBatch(db, inClause, parameters);

            var sourcesBySong = new Dictionary<string, List<MusicSongSourceRecord>>(headers.Count, StringComparer.Ordinal);
            foreach (var item in db.Query($"""
                SELECT song_id,source_key,platform,info_source,
                       songmid,mid,native_id,hash,
                       name,singer,album_name,duration,img,quality_known
                FROM music_song_sources
                WHERE song_id IN ({inClause})
                ORDER BY song_id, position
                """,
                r =>
                {
                    string sid = r.GetString(0);
                    string sKey = r.GetString(1);
                    var key = (sid, sKey);
                    return (SongId: sid, Source: new MusicSongSourceRecord(
                        sKey,
                        r.GetString(2),
                        r.GetString(3),
                        r.GetNullableString(4),
                        r.GetNullableString(5),
                        r.GetNullableString(6),
                        r.GetNullableString(7),
                        r.GetNullableString(8),
                        r.GetNullableString(9),
                        r.GetNullableString(10),
                        r.GetNullableDouble(11),
                        r.GetNullableString(12),
                        r.GetNullableBool(13),
                        srcArtists.TryGetValue(key, out var al) ? al.ToArray() : [],
                        srcExtras.TryGetValue(key, out var ex) ? ex : null));
                }, parameters))
            {
                if (!sourcesBySong.TryGetValue(item.SongId, out var list))
                    sourcesBySong[item.SongId] = list = [];
                list.Add(item.Source);
            }

            foreach (var (sid, song) in headers)
            {
                result[sid] = new MusicSongRecord(
                    sid,
                    song.Title,
                    song.Artist,
                    song.Album,
                    song.Duration,
                    song.BaselineDuration,
                    song.CoverUrl,
                    song.LocalFileId,
                    song.ArtworkSourceKey,
                    song.ArtworkSourceImage,
                    songArtists.TryGetValue(sid, out var artists) ? artists.ToArray() : [],
                    sourcesBySong.TryGetValue(sid, out var sources) ? sources.ToArray() : []);
            }
        }
        return result;
    }

    internal static void SaveIncremental(DatabaseSession db, MusicSongRecord next, MusicSongRecord? previous,
        string matchKey, string searchQuery)
    {
        bool artistsChanged = previous is null || !next.ArtistNames.SequenceEqual(previous.ArtistNames, StringComparer.Ordinal);
        if (previous is null || !next.ScalarEquals(previous) || artistsChanged)
        {
            db.Execute("""
                INSERT INTO music_songs(
                    song_id,title,artist,album,duration,baseline_duration,cover_url,
                    artwork_source_key,artwork_source_image,
                    match_key,search_query)
                VALUES($id,$title,$artist,$album,$dur,$bdur,$cover,$ask,$asi,$mk,$sq)
                ON CONFLICT(song_id) DO UPDATE SET
                    title=excluded.title,
                    artist=excluded.artist,
                    album=excluded.album,
                    duration=excluded.duration,
                    baseline_duration=excluded.baseline_duration,
                    cover_url=excluded.cover_url,
                    artwork_source_key=excluded.artwork_source_key,
                    artwork_source_image=excluded.artwork_source_image,
                    match_key=excluded.match_key,
                    search_query=excluded.search_query
                """,
                ("$id", next.SongId),
                ("$title", next.Title),
                ("$artist", next.Artist),
                ("$album", next.Album),
                ("$dur", next.Duration),
                ("$bdur", next.BaselineDuration),
                ("$cover", next.CoverUrl),
                ("$ask", next.ArtworkSourceKey),
                ("$asi", next.ArtworkSourceImage),
                ("$mk", string.IsNullOrEmpty(matchKey) ? null : matchKey),
                ("$sq", string.IsNullOrEmpty(searchQuery) ? null : searchQuery));
        }

        if (previous is null || previous.LocalFileId != next.LocalFileId)
        {
            if (string.IsNullOrEmpty(next.LocalFileId))
            {
                db.Execute("DELETE FROM windows_music_song_local_files WHERE song_id=$id", ("$id", next.SongId));
            }
            else
            {
                db.Execute("""
                    INSERT INTO windows_music_song_local_files(song_id,local_file_id)
                    VALUES($id,$local)
                    ON CONFLICT(song_id) DO UPDATE SET local_file_id=excluded.local_file_id
                    """, ("$id", next.SongId), ("$local", next.LocalFileId));
            }
        }

        if (artistsChanged)
        {
            int prefix = previous is null ? 0 : CommonPrefixLength(previous.ArtistNames, next.ArtistNames, StringComparer.Ordinal);
            if (previous is null || prefix < previous.ArtistNames.Length)
                db.Execute("DELETE FROM music_song_artists WHERE song_id=$id AND position>=$pos",
                    ("$id", next.SongId), ("$pos", prefix));
            for (int pos = prefix; pos < next.ArtistNames.Length; pos++)
                db.Execute("INSERT INTO music_song_artists(song_id,position,name) VALUES($id,$pos,$name)",
                    ("$id", next.SongId), ("$pos", pos), ("$name", next.ArtistNames[pos]));
        }

        var prevByKey = new Dictionary<string, (int Position, MusicSongSourceRecord Source)>(StringComparer.Ordinal);
        if (previous is not null)
        {
            for (int i = 0; i < previous.Sources.Length; i++)
                prevByKey[previous.Sources[i].SourceKey] = (i, previous.Sources[i]);
        }

        for (int i = 0; i < next.Sources.Length; i++)
        {
            var src = next.Sources[i];
            bool existed = prevByKey.TryGetValue(src.SourceKey, out var oldEntry);
            if (existed && oldEntry.Position == i && src.ContentEquals(oldEntry.Source))
                continue;

            if (existed && src.ScalarEquals(oldEntry.Source))
            {
                if (oldEntry.Position != i)
                    db.Execute("UPDATE music_song_sources SET position=$pos WHERE song_id=$id AND source_key=$skey",
                        ("$pos", i), ("$id", next.SongId), ("$skey", src.SourceKey));
            }
            else
            {
                db.Execute("""
                    INSERT INTO music_song_sources(
                        song_id,source_key,position,platform,info_source,
                        songmid,mid,native_id,hash,
                        name,singer,album_name,duration,img,quality_known)
                    VALUES(
                        $id,$skey,$pos,$plat,$isrc,
                        $sm,$mid,$nid,$hash,
                        $name,$singer,$albname,$dur,$img,$qk)
                    ON CONFLICT(song_id,source_key) DO UPDATE SET
                        position=excluded.position,
                        platform=excluded.platform,
                        info_source=excluded.info_source,
                        songmid=excluded.songmid,
                        mid=excluded.mid,
                        native_id=excluded.native_id,
                        hash=excluded.hash,
                        name=excluded.name,
                        singer=excluded.singer,
                        album_name=excluded.album_name,
                        duration=excluded.duration,
                        img=excluded.img,
                        quality_known=excluded.quality_known
                    """,
                    ("$id", next.SongId), ("$skey", src.SourceKey), ("$pos", i),
                    ("$plat", src.Platform), ("$isrc", src.InfoSource),
                    ("$sm", src.SongMid), ("$mid", src.Mid), ("$nid", src.NativeId), ("$hash", src.Hash),
                    ("$name", src.Name), ("$singer", src.Singer), ("$albname", src.AlbumName),
                    ("$dur", src.Duration), ("$img", src.Img), ("$qk", src.QualityKnown));
            }

            if (!existed || !src.ArtistNames.SequenceEqual(oldEntry.Source.ArtistNames, StringComparer.Ordinal))
            {
                int prefix = existed ? CommonPrefixLength(oldEntry.Source.ArtistNames, src.ArtistNames, StringComparer.Ordinal) : 0;
                if (!existed || prefix < oldEntry.Source.ArtistNames.Length)
                    db.Execute("DELETE FROM music_song_source_artists WHERE song_id=$id AND source_key=$skey AND position>=$pos",
                        ("$id", next.SongId), ("$skey", src.SourceKey), ("$pos", prefix));
                for (int apos = prefix; apos < src.ArtistNames.Length; apos++)
                    db.Execute("INSERT INTO music_song_source_artists(song_id,source_key,position,name) VALUES($id,$skey,$pos,$name)",
                        ("$id", next.SongId), ("$skey", src.SourceKey), ("$pos", apos), ("$name", src.ArtistNames[apos]));
            }

            if (!existed || !StorageValue.StructuralEquals(src.WireExtras, oldEntry.Source.WireExtras))
            {
                TypedValueNodes.Replace(db, "music_song_source_extras",
                    [("song_id", next.SongId), ("source_key", src.SourceKey)], src.WireExtras);
            }
        }
    }

    private static Dictionary<(string SongId, string SourceKey), StorageValue> ReadSourceExtrasBatch(
        DatabaseSession db, string inClause, (string Name, object? Value)[] parameters)
    {
        var grouped = new Dictionary<(string SongId, string SourceKey), List<TypedValueNodes.NodeRow>>();
        foreach (var item in db.Query($"""
            SELECT song_id,source_key,node_id,parent_id,prop_key,item_index,kind,bool_value,int_value,real_value,text_value
            FROM music_song_source_extras
            WHERE song_id IN ({inClause})
            ORDER BY song_id, source_key, node_id
            """, r => (SongId: r.GetString(0), SourceKey: r.GetString(1), Node: TypedValueNodes.ReadRow(r, 2)), parameters))
        {
            var key = (item.SongId, item.SourceKey);
            if (!grouped.TryGetValue(key, out var list))
                grouped[key] = list = [];
            list.Add(item.Node);
        }
        var result = new Dictionary<(string SongId, string SourceKey), StorageValue>(grouped.Count);
        foreach (var (key, rows) in grouped)
            if (TypedValueNodes.Decode(rows) is { Kind: StorageValueKind.Object } decoded)
                result[key] = decoded;
        return result;
    }
}
