using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal readonly record struct FilmFavoriteIdentity(string Kind, string Value);

internal sealed record FilmFavoriteSourceRecord(
    string? Key,
    string? Engine,
    string? InstallationId,
    string? SiteId,
    FilmFavoriteIdentity? DetailId,
    FilmFavoriteIdentity? LineId);

internal sealed record FilmFavoriteMemberRecord(
    string SourceKey,
    FilmFavoriteIdentity DetailId);

internal sealed record FilmFavoriteRecord(
    string FavoriteId,
    string ItemId,
    string Title,
    string Poster,
    string Score,
    string TextBadge,
    string Subtitle,
    string TargetId,
    string TargetKind,
    string TargetTitle,
    string? MetadataProvider,
    string? MetadataId,
    int? TargetYear,
    int? Season,
    int? Episode,
    int? GlobalEpisode,
    FilmFavoriteSourceRecord? Source,
    IReadOnlyList<FilmFavoriteMemberRecord>? Members,
    long AddedAtMs);

internal static class FilmFavoritesStore
{
    internal const string Owner = "film.library.v1";

    internal static IReadOnlyList<FilmFavoriteRecord> ReadAll(DatabaseSession db)
    {
        var memberRows = db.Query("""
            SELECT favorite_id,source_key,detail_kind,detail_text
            FROM film_favorite_members
            ORDER BY favorite_id, position
            """,
            row => (
                FavoriteId: row.GetString(0),
                Member: new FilmFavoriteMemberRecord(
                    row.GetString(1),
                    new FilmFavoriteIdentity(row.GetString(2), row.GetString(3)))));

        var membersByFav = new Dictionary<string, List<FilmFavoriteMemberRecord>>(StringComparer.Ordinal);
        foreach (var m in memberRows)
        {
            if (!membersByFav.TryGetValue(m.FavoriteId, out var list))
            {
                list = [];
                membersByFav[m.FavoriteId] = list;
            }
            list.Add(m.Member);
        }

        return db.Query("""
            SELECT f.favorite_id,f.item_id,d.title,d.poster,d.score,d.text_badge,d.subtitle,
                   f.target_id,f.target_kind,f.target_title,f.metadata_provider,f.metadata_id,
                   f.target_year,f.season,f.episode,f.global_episode,
                   f.source_key,f.source_engine,f.source_installation_id,f.source_site_id,
                   f.source_detail_kind,f.source_detail_text,
                   f.source_line_kind,f.source_line_text,f.added_at_ms
            FROM film_favorites f
            INNER JOIN windows_film_favorite_display d ON d.favorite_id=f.favorite_id
            ORDER BY f.position
            """,
            row => ReadRow(row, membersByFav)).ToArray();
    }

    internal static bool SetFavorite(DatabaseSession db, FilmFavoriteRecord record, bool favorite)
    {
        string favoriteId = record.FavoriteId;
        bool exists = db.TryQuerySingle(
            "SELECT position FROM film_favorites WHERE favorite_id=$id",
            row => row.GetInt32(0),
            out int existingPosition,
            ("$id", favoriteId));

        if (!favorite)
        {
            if (!exists) return false;
            db.Execute("DELETE FROM film_favorites WHERE favorite_id=$id", ("$id", favoriteId));
            db.CommitRevision(Owner, db.Revision(Owner));
            return true;
        }

        if (exists)
        {
            var current = ReadSingle(db, favoriteId);
            if (current is not null && ContentEquals(current, record))
                return false;
        }

        int position = exists
            ? existingPosition
            : checked((int)db.Scalar<long>("SELECT coalesce(max(position),-1)+1 FROM film_favorites"));

        var src = record.Source;
        db.Execute("""
            INSERT INTO film_favorites(
                favorite_id,position,item_id,
                target_id,target_kind,target_title,metadata_provider,metadata_id,
                target_year,season,episode,global_episode,
                source_key,source_engine,source_installation_id,source_site_id,
                source_detail_kind,source_detail_text,
                source_line_kind,source_line_text,added_at_ms,row_revision)
            VALUES(
                $fid,$pos,$iid,
                $tid,$tkind,$ttitle,$tprov,$tmid,
                $tyear,$tseason,$tep,$tgep,
                $skey,$seng,$sinst,$ssite,
                $sdkind,$sdval,
                $slkind,$slval,$addedAt,1)
            ON CONFLICT(favorite_id) DO UPDATE SET
                position=excluded.position,
                item_id=excluded.item_id,
                target_id=excluded.target_id,
                target_kind=excluded.target_kind,
                target_title=excluded.target_title,
                metadata_provider=excluded.metadata_provider,
                metadata_id=excluded.metadata_id,
                target_year=excluded.target_year,
                season=excluded.season,
                episode=excluded.episode,
                global_episode=excluded.global_episode,
                source_key=excluded.source_key,
                source_engine=excluded.source_engine,
                source_installation_id=excluded.source_installation_id,
                source_site_id=excluded.source_site_id,
                source_detail_kind=excluded.source_detail_kind,
                source_detail_text=excluded.source_detail_text,
                source_line_kind=excluded.source_line_kind,
                source_line_text=excluded.source_line_text,
                added_at_ms=excluded.added_at_ms,
                row_revision=film_favorites.row_revision+1
            """,
            ("$fid", favoriteId),
            ("$pos", position),
            ("$iid", record.ItemId),
            ("$tid", record.TargetId),
            ("$tkind", record.TargetKind),
            ("$ttitle", record.TargetTitle),
            ("$tprov", record.MetadataProvider),
            ("$tmid", record.MetadataId),
            ("$tyear", record.TargetYear),
            ("$tseason", record.Season),
            ("$tep", record.Episode),
            ("$tgep", record.GlobalEpisode),
            ("$skey", src?.Key),
            ("$seng", src?.Engine),
            ("$sinst", src?.InstallationId),
            ("$ssite", src?.SiteId),
            ("$sdkind", src?.DetailId?.Kind),
            ("$sdval", src?.DetailId?.Value),
            ("$slkind", src?.LineId?.Kind),
            ("$slval", src?.LineId?.Value),
            ("$addedAt", record.AddedAtMs));

        db.Execute("""
            INSERT INTO windows_film_favorite_display(favorite_id,title,poster,score,text_badge,subtitle)
            VALUES($fid,$title,$poster,$score,$badge,$sub)
            ON CONFLICT(favorite_id) DO UPDATE SET
                title=excluded.title,
                poster=excluded.poster,
                score=excluded.score,
                text_badge=excluded.text_badge,
                subtitle=excluded.subtitle
            """,
            ("$fid", favoriteId),
            ("$title", record.Title),
            ("$poster", record.Poster),
            ("$score", record.Score),
            ("$badge", record.TextBadge),
            ("$sub", record.Subtitle));

        db.Execute("DELETE FROM film_favorite_members WHERE favorite_id=$fid", ("$fid", favoriteId));
        if (record.Members is { Count: > 0 } members)
        {
            for (int i = 0; i < members.Count; i++)
            {
                var m = members[i];
                db.Execute("""
                    INSERT INTO film_favorite_members(favorite_id,position,source_key,detail_kind,detail_text)
                    VALUES($fid,$pos,$skey,$dkind,$dval)
                    """,
                    ("$fid", favoriteId),
                    ("$pos", i),
                    ("$skey", m.SourceKey),
                    ("$dkind", m.DetailId.Kind),
                    ("$dval", m.DetailId.Value));
            }
        }

        db.CommitRevision(Owner, db.Revision(Owner));
        return true;
    }

    private static FilmFavoriteRecord? ReadSingle(DatabaseSession db, string favoriteId)
    {
        var members = db.Query("""
            SELECT source_key,detail_kind,detail_text
            FROM film_favorite_members
            WHERE favorite_id=$fid
            ORDER BY position
            """,
            row => new FilmFavoriteMemberRecord(row.GetString(0), new FilmFavoriteIdentity(row.GetString(1), row.GetString(2))),
            ("$fid", favoriteId));
        var map = new Dictionary<string, List<FilmFavoriteMemberRecord>>(StringComparer.Ordinal);
        if (members.Count > 0) map[favoriteId] = members.ToList();

        return db.TryQuerySingle("""
            SELECT f.favorite_id,f.item_id,d.title,d.poster,d.score,d.text_badge,d.subtitle,
                   f.target_id,f.target_kind,f.target_title,f.metadata_provider,f.metadata_id,
                   f.target_year,f.season,f.episode,f.global_episode,
                   f.source_key,f.source_engine,f.source_installation_id,f.source_site_id,
                   f.source_detail_kind,f.source_detail_text,
                   f.source_line_kind,f.source_line_text,f.added_at_ms
            FROM film_favorites f
            INNER JOIN windows_film_favorite_display d ON d.favorite_id=f.favorite_id
            WHERE f.favorite_id=$fid
            """,
            row => ReadRow(row, map),
            out var item,
            ("$fid", favoriteId))
            ? item
            : null;
    }

    private static FilmFavoriteRecord ReadRow(
        Microsoft.Data.Sqlite.SqliteDataReader row,
        Dictionary<string, List<FilmFavoriteMemberRecord>> membersByFav)
    {
        string favoriteId = row.GetString(0);
        string itemId = row.GetString(1);
        string title = row.GetString(2);
        string poster = row.GetString(3);
        string score = row.GetString(4);
        string textBadge = row.GetString(5);
        string subtitle = row.GetString(6);

        string? targetId = row.GetNullableString(7);
        string? targetKind = row.GetNullableString(8);
        string? targetTitle = row.GetNullableString(9);
        if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(title) ||
            string.IsNullOrWhiteSpace(targetId) || string.IsNullOrWhiteSpace(targetKind) ||
            string.IsNullOrWhiteSpace(targetTitle) || favoriteId != targetId)
            throw new StorageException(StorageError.Corrupt);

        string? sKey = row.GetNullableString(16);
        string? sEngine = row.GetNullableString(17);
        string? sInst = row.GetNullableString(18);
        string? sSite = row.GetNullableString(19);
        string? sDetailKind = row.GetNullableString(20);
        string? sDetailVal = row.GetNullableString(21);
        string? sLineKind = row.GetNullableString(22);
        string? sLineVal = row.GetNullableString(23);
        FilmFavoriteSourceRecord? source = null;
        if (sKey is not null || sEngine is not null || sInst is not null || sSite is not null ||
            sDetailKind is not null || sLineKind is not null)
        {
            source = new FilmFavoriteSourceRecord(
                sKey, sEngine, sInst, sSite,
                sDetailKind is not null ? new FilmFavoriteIdentity(sDetailKind, sDetailVal!) : null,
                sLineKind is not null ? new FilmFavoriteIdentity(sLineKind, sLineVal!) : null);
        }

        IReadOnlyList<FilmFavoriteMemberRecord>? members = null;
        if (targetKind == "group" || membersByFav.ContainsKey(favoriteId))
            members = membersByFav.TryGetValue(favoriteId, out var list) ? list : [];

        return new FilmFavoriteRecord(
            favoriteId, itemId, title, poster, score, textBadge, subtitle,
            targetId, targetKind, targetTitle,
            row.GetNullableString(10), row.GetNullableString(11),
            row.GetNullableInt32(12), row.GetNullableInt32(13), row.GetNullableInt32(14), row.GetNullableInt32(15),
            source, members, row.GetInt64(24));
    }

    private static bool ContentEquals(FilmFavoriteRecord a, FilmFavoriteRecord b) =>
        a.FavoriteId == b.FavoriteId && a.ItemId == b.ItemId && a.Title == b.Title &&
        a.Poster == b.Poster && a.Score == b.Score && a.TextBadge == b.TextBadge &&
        a.Subtitle == b.Subtitle && a.TargetId == b.TargetId && a.TargetKind == b.TargetKind &&
        a.TargetTitle == b.TargetTitle && a.MetadataProvider == b.MetadataProvider &&
        a.MetadataId == b.MetadataId && a.TargetYear == b.TargetYear && a.Season == b.Season &&
        a.Episode == b.Episode && a.GlobalEpisode == b.GlobalEpisode &&
        a.Source == b.Source &&
        (a.Members is null ? b.Members is null : b.Members is not null && a.Members.SequenceEqual(b.Members));
}
