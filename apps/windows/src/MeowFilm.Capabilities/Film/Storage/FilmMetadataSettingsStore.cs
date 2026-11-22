using System.Text.Json;
using MeowFilm.Platform.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal static class FilmMetadataSettingsStore
{
    internal const string TmdbOwner = "film.tmdb.v1";
    internal const string DoubanOwner = "film.douban.v1";

    internal static readonly FilmMetadataSettings TmdbDefaults = new(
        "https://api.themoviedb.org/3",
        "https://image.tmdb.org",
        "",
        "",
        "zh-CN",
        "CN",
        false);

    internal static readonly FilmDoubanSettings DoubanDefaults = new(
        "https://m.douban.com",
        "https://search.douban.com",
        "https://img3.doubanio.com",
        "",
        "",
        "");

    internal static FilmMetadataSettings? ReadTmdb(DatabaseSession db) =>
        db.TryQuerySingle("""
            SELECT api_base,image_base,api_key,access_token,language,region,include_adult
            FROM tmdb_settings WHERE singleton=1
            """,
            row => new FilmMetadataSettings(
                row.GetString(0),
                row.GetString(1),
                row.GetString(2),
                row.GetString(3),
                row.GetString(4),
                row.GetString(5),
                row.GetBool(6)),
            out var settings)
            ? settings
            : null;

    internal static Task<FilmMetadataSettings> ReadTmdbAsync(IStorageDatabase database, CancellationToken token) =>
        database.ReadAsync(db => ReadTmdb(db) ?? TmdbDefaults, token);

    internal static Task<FilmMetadataSettings> UpdateTmdbAsync(
        IStorageDatabase database,
        Func<FilmMetadataSettings, FilmMetadataSettings> update,
        CancellationToken token) =>
        database.WriteAsync(db =>
        {
            var expected = db.Revision(TmdbOwner);
            var next = update(ReadTmdb(db) ?? TmdbDefaults);
            db.Execute("""
                INSERT INTO tmdb_settings(
                    singleton,api_base,image_base,api_key,access_token,language,region,include_adult,row_revision)
                VALUES(1,$apiBase,$imageBase,$apiKey,$accessToken,$language,$region,$includeAdult,1)
                ON CONFLICT(singleton) DO UPDATE SET
                    api_base=excluded.api_base,
                    image_base=excluded.image_base,
                    api_key=excluded.api_key,
                    access_token=excluded.access_token,
                    language=excluded.language,
                    region=excluded.region,
                    include_adult=excluded.include_adult,
                    row_revision=tmdb_settings.row_revision+1
                """,
                ("$apiBase", next.ApiBase),
                ("$imageBase", next.ImageBase),
                ("$apiKey", next.ApiKey),
                ("$accessToken", next.AccessToken),
                ("$language", next.Language),
                ("$region", next.Region),
                ("$includeAdult", next.IncludeAdult));
            db.CommitRevision(TmdbOwner, expected, Guid.NewGuid().ToString("D"));
            return next;
        }, token);

    internal static Task<RuntimeSettingsSnapshot> ReadTmdbRuntimeSnapshotAsync(
        IStorageDatabase database, CancellationToken token) =>
        database.ReadSnapshotAsync(db =>
        {
            var rev = db.Revision(TmdbOwner).Revision;
            var settings = ReadTmdb(db);
            if (settings is null) return new RuntimeSettingsSnapshot(null, rev);
            var doc = JsonSerializer.SerializeToElement(new
            {
                version = 1,
                settings = new
                {
                    apiBase = settings.ApiBase,
                    imageBase = settings.ImageBase,
                    apiKey = settings.ApiKey,
                    accessToken = settings.AccessToken,
                    language = settings.Language,
                    region = settings.Region,
                    includeAdult = settings.IncludeAdult
                }
            });
            return new RuntimeSettingsSnapshot(doc, rev);
        }, token);

    internal static FilmDoubanSettings? ReadDouban(DatabaseSession db) =>
        db.TryQuerySingle("""
            SELECT api_base,search_base,image_base,api_proxy,image_proxy,cookie
            FROM douban_settings WHERE singleton=1
            """,
            row => new FilmDoubanSettings(
                row.GetString(0),
                row.GetString(1),
                row.GetString(2),
                row.GetString(3),
                row.GetString(4),
                row.GetString(5)),
            out var settings)
            ? settings
            : null;

    internal static Task<FilmDoubanSettings> ReadDoubanAsync(IStorageDatabase database, CancellationToken token) =>
        database.ReadAsync(db => ReadDouban(db) ?? DoubanDefaults, token);

    internal static Task<FilmDoubanSettings> UpdateDoubanAsync(
        IStorageDatabase database,
        Func<FilmDoubanSettings, FilmDoubanSettings> update,
        CancellationToken token) =>
        database.WriteAsync(db =>
        {
            var expected = db.Revision(DoubanOwner);
            var next = update(ReadDouban(db) ?? DoubanDefaults);
            db.Execute("""
                INSERT INTO douban_settings(
                    singleton,api_base,search_base,image_base,api_proxy,image_proxy,cookie,row_revision)
                VALUES(1,$apiBase,$searchBase,$imageBase,$apiProxy,$imageProxy,$cookie,1)
                ON CONFLICT(singleton) DO UPDATE SET
                    api_base=excluded.api_base,
                    search_base=excluded.search_base,
                    image_base=excluded.image_base,
                    api_proxy=excluded.api_proxy,
                    image_proxy=excluded.image_proxy,
                    cookie=excluded.cookie,
                    row_revision=douban_settings.row_revision+1
                """,
                ("$apiBase", next.ApiBase),
                ("$searchBase", next.SearchBase),
                ("$imageBase", next.ImageBase),
                ("$apiProxy", next.ApiProxy),
                ("$imageProxy", next.ImageProxy),
                ("$cookie", next.Cookie));
            db.CommitRevision(DoubanOwner, expected, Guid.NewGuid().ToString("D"));
            return next;
        }, token);

    internal static Task<RuntimeSettingsSnapshot> ReadDoubanRuntimeSnapshotAsync(
        IStorageDatabase database, CancellationToken token) =>
        database.ReadSnapshotAsync(db =>
        {
            var rev = db.Revision(DoubanOwner).Revision;
            var settings = ReadDouban(db);
            if (settings is null) return new RuntimeSettingsSnapshot(null, rev);
            var doc = JsonSerializer.SerializeToElement(new
            {
                version = 1,
                settings = new
                {
                    apiBase = settings.ApiBase,
                    searchBase = settings.SearchBase,
                    imageBase = settings.ImageBase,
                    apiProxy = settings.ApiProxy,
                    imageProxy = settings.ImageProxy,
                    cookie = settings.Cookie
                }
            });
            return new RuntimeSettingsSnapshot(doc, rev);
        }, token);
}
