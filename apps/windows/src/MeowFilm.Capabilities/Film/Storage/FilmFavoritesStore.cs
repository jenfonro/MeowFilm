using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal sealed record FilmFavoriteRecord(
    string FavoriteId,
    string Keyword,
    string Title,
    string Poster,
    string Score,
    string TextBadge,
    string Subtitle,
    string? ContentKind,
    int? Year,
    string? TmdbKind,
    int? TmdbId,
    string? DoubanKind,
    string? DoubanId,
    long AddedAtMs);

internal static class FilmFavoritesStore
{
    internal const string Owner = "film.library.v1";

    internal static IReadOnlyList<FilmFavoriteRecord> ReadAll(DatabaseSession db) =>
        db.Query("""
            SELECT favorite_id,keyword,title,poster,score,text_badge,subtitle,
                   content_kind,year,tmdb_kind,tmdb_id,douban_kind,douban_id,added_at_ms
            FROM film_favorites
            ORDER BY position, favorite_id
            """,
            ReadRow).ToArray();

    internal static bool SetFavorite(DatabaseSession db, FilmFavoriteRecord record, bool favorite)
    {
        string favoriteId = record.FavoriteId;
        if (!favorite)
        {
            if (db.Execute("DELETE FROM film_favorites WHERE favorite_id=$id", ("$id", favoriteId)) == 0)
                return false;
            db.CommitRevision(Owner, db.Revision(Owner));
            return true;
        }

        bool exists = db.TryQuerySingle(
            "SELECT position FROM film_favorites WHERE favorite_id=$id",
            row => row.GetInt32(0),
            out int existingPosition,
            ("$id", favoriteId));

        if (exists)
        {
            var current = ReadSingle(db, favoriteId);
            if (current is not null && current == record)
                return false;
        }

        int position = exists
            ? existingPosition
            : checked((int)db.Scalar<long>("SELECT coalesce(max(position),-1)+1 FROM film_favorites"));

        db.Execute("""
            INSERT INTO film_favorites(
                favorite_id,position,keyword,title,poster,score,text_badge,subtitle,
                content_kind,year,tmdb_kind,tmdb_id,douban_kind,douban_id,added_at_ms)
            VALUES(
                $fid,$pos,$kw,$title,$poster,$score,$badge,$sub,
                $kind,$year,$tkind,$tid,$dkind,$did,$addedAt)
            ON CONFLICT(favorite_id) DO UPDATE SET
                position=excluded.position,
                keyword=excluded.keyword,
                title=excluded.title,
                poster=excluded.poster,
                score=excluded.score,
                text_badge=excluded.text_badge,
                subtitle=excluded.subtitle,
                content_kind=excluded.content_kind,
                year=excluded.year,
                tmdb_kind=excluded.tmdb_kind,
                tmdb_id=excluded.tmdb_id,
                douban_kind=excluded.douban_kind,
                douban_id=excluded.douban_id,
                added_at_ms=excluded.added_at_ms
            """,
            ("$fid", favoriteId),
            ("$pos", position),
            ("$kw", record.Keyword),
            ("$title", record.Title),
            ("$poster", record.Poster),
            ("$score", record.Score),
            ("$badge", record.TextBadge),
            ("$sub", record.Subtitle),
            ("$kind", record.ContentKind),
            ("$year", record.Year),
            ("$tkind", record.TmdbKind),
            ("$tid", record.TmdbId),
            ("$dkind", record.DoubanKind),
            ("$did", record.DoubanId),
            ("$addedAt", record.AddedAtMs));

        db.CommitRevision(Owner, db.Revision(Owner));
        return true;
    }

    private static FilmFavoriteRecord? ReadSingle(DatabaseSession db, string favoriteId) =>
        db.TryQuerySingle("""
            SELECT favorite_id,keyword,title,poster,score,text_badge,subtitle,
                   content_kind,year,tmdb_kind,tmdb_id,douban_kind,douban_id,added_at_ms
            FROM film_favorites
            WHERE favorite_id=$fid
            """,
            ReadRow,
            out var item,
            ("$fid", favoriteId))
            ? item
            : null;

    private static FilmFavoriteRecord ReadRow(Microsoft.Data.Sqlite.SqliteDataReader row)
    {
        string favoriteId = row.GetString(0);
        string keyword = row.GetString(1);
        string title = row.GetString(2);
        string poster = row.GetString(3);
        string score = row.GetString(4);
        string textBadge = row.GetString(5);
        string subtitle = row.GetString(6);
        string? contentKind = row.GetNullableString(7);
        int? year = row.GetNullableInt32(8);
        string? tmdbKind = row.GetNullableString(9);
        int? tmdbId = row.GetNullableInt32(10);
        string? doubanKind = row.GetNullableString(11);
        string? doubanId = row.GetNullableString(12);
        long addedAtMs = row.GetInt64(13);

        if (string.IsNullOrWhiteSpace(favoriteId) || string.IsNullOrWhiteSpace(keyword) || string.IsNullOrWhiteSpace(title) ||
            contentKind is not (null or "movie" or "tv") ||
            (tmdbKind is null) != (tmdbId is null) ||
            (doubanKind is null) != (doubanId is null))
            throw new StorageException(StorageError.Corrupt);

        return new FilmFavoriteRecord(
            favoriteId, keyword, title, poster, score, textBadge, subtitle,
            contentKind, year, tmdbKind, tmdbId, doubanKind, doubanId, addedAtMs);
    }
}
