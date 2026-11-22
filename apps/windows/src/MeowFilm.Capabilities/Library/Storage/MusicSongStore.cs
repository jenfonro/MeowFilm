using MeowFilm.Platform.Storage;
using static MeowFilm.Capabilities.Library.MusicStorageModelHelpers;

namespace MeowFilm.Capabilities.Library;

internal static class MusicSongStore
{
    internal static MusicSongRecord? ReadRecord(DatabaseSession db, string songId)
    {
        if (!db.TryQuerySingle("""
            SELECT title,artist,album,duration,baseline_duration,cover_url,
                   local_file_id,artwork_source_key,artwork_source_image
            FROM music_songs
            WHERE song_id=$id
            """,
            row => (
                Title: row.GetString(0),
                Artist: row.GetString(1),
                Album: row.GetString(2),
                Duration: row.GetNullableDouble(3),
                BaselineDuration: row.GetNullableDouble(4),
                CoverUrl: row.GetString(5),
                LocalFileId: row.GetNullableString(6),
                ArtworkSourceKey: row.GetNullableString(7),
                ArtworkSourceImage: row.GetNullableString(8)),
            out var song,
            ("$id", songId)))
            return null;

        var songArtists = db.Query(
            "SELECT name FROM music_song_artists WHERE song_id=$id ORDER BY position",
            r => r.GetString(0), ("$id", songId)).ToArray();

        var srcArtists = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var item in db.Query(
            "SELECT source_key,name FROM music_song_source_artists WHERE song_id=$id ORDER BY source_key, position",
            r => (Key: r.GetString(0), Name: r.GetString(1)), ("$id", songId)))
        {
            if (!srcArtists.TryGetValue(item.Key, out var list))
                srcArtists[item.Key] = list = [];
            list.Add(item.Name);
        }

        var srcExtras = ReadSourceExtras(db, songId);

        var sources = db.Query("""
            SELECT source_key,platform,info_source,
                   songmid,mid,native_id,hash,
                   name,singer,album_name,duration,img,quality_known
            FROM music_song_sources
            WHERE song_id=$id
            ORDER BY position
            """,
            r =>
            {
                string sKey = r.GetString(0);
                return new MusicSongSourceRecord(
                    sKey,
                    r.GetString(1),
                    r.GetString(2),
                    r.GetNullableString(3),
                    r.GetNullableString(4),
                    r.GetNullableString(5),
                    r.GetNullableString(6),
                    r.GetNullableString(7),
                    r.GetNullableString(8),
                    r.GetNullableString(9),
                    r.GetNullableDouble(10),
                    r.GetNullableString(11),
                    r.GetNullableBool(12),
                    srcArtists.TryGetValue(sKey, out var al) ? al.ToArray() : [],
                    srcExtras.TryGetValue(sKey, out var ex) ? ex : null);
            }, ("$id", songId)).ToArray();

        return new MusicSongRecord(
            songId,
            song.Title,
            song.Artist,
            song.Album,
            song.Duration,
            song.BaselineDuration,
            song.CoverUrl,
            song.LocalFileId,
            song.ArtworkSourceKey,
            song.ArtworkSourceImage,
            songArtists,
            sources);
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
                    local_file_id,artwork_source_key,artwork_source_image,
                    match_key,search_query)
                VALUES($id,$title,$artist,$album,$dur,$bdur,$cover,$local,$ask,$asi,$mk,$sq)
                ON CONFLICT(song_id) DO UPDATE SET
                    title=excluded.title,
                    artist=excluded.artist,
                    album=excluded.album,
                    duration=excluded.duration,
                    baseline_duration=excluded.baseline_duration,
                    cover_url=excluded.cover_url,
                    local_file_id=excluded.local_file_id,
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
                ("$local", next.LocalFileId),
                ("$ask", next.ArtworkSourceKey),
                ("$asi", next.ArtworkSourceImage),
                ("$mk", string.IsNullOrEmpty(matchKey) ? null : matchKey),
                ("$sq", string.IsNullOrEmpty(searchQuery) ? null : searchQuery));
        }

        if (artistsChanged)
        {
            if (previous is null)
            {
                db.Execute("DELETE FROM music_song_artists WHERE song_id=$id", ("$id", next.SongId));
                for (int pos = 0; pos < next.ArtistNames.Length; pos++)
                    db.Execute("INSERT INTO music_song_artists(song_id,position,name) VALUES($id,$pos,$name)",
                        ("$id", next.SongId), ("$pos", pos), ("$name", next.ArtistNames[pos]));
            }
            else
            {
                int prefix = CommonPrefixLength(previous.ArtistNames, next.ArtistNames, StringComparer.Ordinal);
                if (prefix < previous.ArtistNames.Length)
                    db.Execute("DELETE FROM music_song_artists WHERE song_id=$id AND position>=$pos",
                        ("$id", next.SongId), ("$pos", prefix));
                for (int pos = prefix; pos < next.ArtistNames.Length; pos++)
                    db.Execute("INSERT INTO music_song_artists(song_id,position,name) VALUES($id,$pos,$name)",
                        ("$id", next.SongId), ("$pos", pos), ("$name", next.ArtistNames[pos]));
            }
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
                if (!existed)
                {
                    db.Execute("DELETE FROM music_song_source_artists WHERE song_id=$id AND source_key=$skey",
                        ("$id", next.SongId), ("$skey", src.SourceKey));
                    for (int apos = 0; apos < src.ArtistNames.Length; apos++)
                        db.Execute("INSERT INTO music_song_source_artists(song_id,source_key,position,name) VALUES($id,$skey,$pos,$name)",
                            ("$id", next.SongId), ("$skey", src.SourceKey), ("$pos", apos), ("$name", src.ArtistNames[apos]));
                }
                else
                {
                    int prefix = CommonPrefixLength(oldEntry.Source.ArtistNames, src.ArtistNames, StringComparer.Ordinal);
                    if (prefix < oldEntry.Source.ArtistNames.Length)
                        db.Execute("DELETE FROM music_song_source_artists WHERE song_id=$id AND source_key=$skey AND position>=$pos",
                            ("$id", next.SongId), ("$skey", src.SourceKey), ("$pos", prefix));
                    for (int apos = prefix; apos < src.ArtistNames.Length; apos++)
                        db.Execute("INSERT INTO music_song_source_artists(song_id,source_key,position,name) VALUES($id,$skey,$pos,$name)",
                            ("$id", next.SongId), ("$skey", src.SourceKey), ("$pos", apos), ("$name", src.ArtistNames[apos]));
                }
            }

            if (!existed || !StorageValue.StructuralEquals(src.WireExtras, oldEntry.Source.WireExtras))
            {
                TypedValueNodes.Replace(db, "music_song_source_extras",
                    [("song_id", next.SongId), ("source_key", src.SourceKey)], src.WireExtras);
            }
        }
    }

    private static Dictionary<string, StorageValue> ReadSourceExtras(DatabaseSession db, string songId)
    {
        var grouped = new Dictionary<string, List<TypedValueNodes.NodeRow>>(StringComparer.Ordinal);
        foreach (var item in db.Query("""
            SELECT source_key,node_id,parent_id,prop_key,item_index,kind,bool_value,int_value,real_value,text_value
            FROM music_song_source_extras
            WHERE song_id=$id
            ORDER BY source_key, node_id
            """, r => (SourceKey: r.GetString(0), Node: TypedValueNodes.ReadRow(r, 1)), ("$id", songId)))
        {
            if (!grouped.TryGetValue(item.SourceKey, out var list))
                grouped[item.SourceKey] = list = [];
            list.Add(item.Node);
        }
        var result = new Dictionary<string, StorageValue>(grouped.Count, StringComparer.Ordinal);
        foreach (var (sKey, rows) in grouped)
        {
            var decoded = TypedValueNodes.Decode(rows);
            if (decoded is null || decoded.Kind != StorageValueKind.Object)
                throw new DocumentStoreException(DocumentStoreError.Corrupt);
            result[sKey] = decoded;
        }
        return result;
    }
}
