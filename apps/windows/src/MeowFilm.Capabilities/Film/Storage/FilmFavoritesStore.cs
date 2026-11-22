using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal static class FilmFavoritesStore
{
    internal const string Owner = "film.library.v1";

    internal static IReadOnlyList<FilmCardItem> ReadAll(DatabaseSession db)
    {
        var memberRows = db.Query("""
            SELECT favorite_id,source_key,detail_kind,detail_text
            FROM film_favorite_members
            ORDER BY favorite_id, position
            """,
            row => (
                FavoriteId: row.GetString(0),
                SourceKey: row.GetString(1),
                DetailKind: row.GetString(2),
                DetailText: row.GetString(3)));

        var membersByFav = new Dictionary<string, List<(string SourceKey, string DetailKind, string DetailText)>>(StringComparer.Ordinal);
        foreach (var m in memberRows)
        {
            if (!membersByFav.TryGetValue(m.FavoriteId, out var list))
            {
                list = [];
                membersByFav[m.FavoriteId] = list;
            }
            list.Add((m.SourceKey, m.DetailKind, m.DetailText));
        }

        return db.Query("""
            SELECT favorite_id,item_id,title,poster,score,text_badge,subtitle,
                   target_id,target_kind,target_title,metadata_provider,metadata_id_text,
                   target_year,season,episode,global_episode,
                   source_key,source_engine,source_installation_id,
                   source_site_kind,source_site_text,
                   source_detail_kind,source_detail_text,
                   source_line_kind,source_line_text
            FROM film_favorites
            ORDER BY position
            """,
            row => ReadFavoriteRow(row, membersByFav)).ToArray();
    }

    internal static bool SetFavorite(DatabaseSession db, FilmCardItem item, bool favorite)
    {
        var target = item.PlaybackContext!.Target;
        string favoriteId = target.Id;

        bool exists = db.TryQuerySingle(
            "SELECT position FROM film_favorites WHERE favorite_id=$id",
            row => row.GetInt32(0),
            out int existingPosition,
            ("$id", favoriteId));

        if (!favorite)
        {
            if (!exists) return false;
            db.Execute("DELETE FROM film_favorites WHERE favorite_id=$id", ("$id", favoriteId));
            db.CommitRevision(Owner, db.Revision(Owner), Guid.NewGuid().ToString("D"));
            return true;
        }

        var normalized = NormalizeFavorite(item);
        if (exists)
        {
            var current = ReadSingle(db, favoriteId);
            if (current is not null && SameFavorite(current, normalized))
                return false;
        }

        int position = exists
            ? existingPosition
            : checked((int)db.Scalar<long>("SELECT coalesce(max(position),-1)+1 FROM film_favorites"));

        var normTarget = normalized.PlaybackContext!.Target;
        string? sourceKey = null, sourceEngine = null, sourceInstallationId = null;
        (string? Kind, string? Value) siteId = default, detailId = default, lineId = default;
        if (normTarget.Source is { ValueKind: JsonValueKind.Object } src)
        {
            sourceKey = OptionalText(src, "key");
            sourceEngine = OptionalText(src, "engine");
            sourceInstallationId = OptionalText(src, "installationId");
            siteId = EncodeIdentity(FilmJson.Member(src, "siteId"));
            detailId = EncodeIdentity(FilmJson.Member(src, "detailId"));
            lineId = EncodeIdentity(FilmJson.Member(src, "lineId"));
        }

        string? metaIdKind = normTarget.MetadataId is not null ? "text" : null;

        db.Execute("""
            INSERT INTO film_favorites(
                favorite_id,position,item_id,title,poster,score,text_badge,subtitle,
                target_id,target_kind,target_title,metadata_provider,metadata_id_kind,metadata_id_text,
                target_year,season,episode,global_episode,
                source_key,source_engine,source_installation_id,
                source_site_kind,source_site_text,
                source_detail_kind,source_detail_text,
                source_line_kind,source_line_text,row_revision)
            VALUES(
                $fid,$pos,$iid,$title,$poster,$score,$badge,$sub,
                $tid,$tkind,$ttitle,$tprov,$tmidk,$tmid,
                $tyear,$tseason,$tep,$tgep,
                $skey,$seng,$sinst,
                $sskind,$ssval,
                $sdkind,$sdval,
                $slkind,$slval,1)
            ON CONFLICT(favorite_id) DO UPDATE SET
                position=excluded.position,
                item_id=excluded.item_id,
                title=excluded.title,
                poster=excluded.poster,
                score=excluded.score,
                text_badge=excluded.text_badge,
                subtitle=excluded.subtitle,
                target_id=excluded.target_id,
                target_kind=excluded.target_kind,
                target_title=excluded.target_title,
                metadata_provider=excluded.metadata_provider,
                metadata_id_kind=excluded.metadata_id_kind,
                metadata_id_text=excluded.metadata_id_text,
                target_year=excluded.target_year,
                season=excluded.season,
                episode=excluded.episode,
                global_episode=excluded.global_episode,
                source_key=excluded.source_key,
                source_engine=excluded.source_engine,
                source_installation_id=excluded.source_installation_id,
                source_site_kind=excluded.source_site_kind,
                source_site_text=excluded.source_site_text,
                source_detail_kind=excluded.source_detail_kind,
                source_detail_text=excluded.source_detail_text,
                source_line_kind=excluded.source_line_kind,
                source_line_text=excluded.source_line_text,
                row_revision=film_favorites.row_revision+1
            """,
            ("$fid", favoriteId),
            ("$pos", position),
            ("$iid", normalized.Id),
            ("$title", normalized.Title),
            ("$poster", normalized.Poster),
            ("$score", normalized.Score),
            ("$badge", normalized.TextBadge),
            ("$sub", normalized.Subtitle),
            ("$tid", normTarget.Id),
            ("$tkind", normTarget.Kind),
            ("$ttitle", normTarget.Title),
            ("$tprov", normTarget.MetadataProvider),
            ("$tmidk", metaIdKind),
            ("$tmid", normTarget.MetadataId),
            ("$tyear", normTarget.Year),
            ("$tseason", normTarget.Season),
            ("$tep", normTarget.Episode),
            ("$tgep", normTarget.GlobalEpisode),
            ("$skey", sourceKey),
            ("$seng", sourceEngine),
            ("$sinst", sourceInstallationId),
            ("$sskind", siteId.Kind),
            ("$ssval", siteId.Value),
            ("$sdkind", detailId.Kind),
            ("$sdval", detailId.Value),
            ("$slkind", lineId.Kind),
            ("$slval", lineId.Value));

        db.Execute("DELETE FROM film_favorite_members WHERE favorite_id=$fid", ("$fid", favoriteId));
        if (normTarget.Members is { ValueKind: JsonValueKind.Array } members)
        {
            int idx = 0;
            foreach (var member in members.EnumerateArray())
            {
                var mDetail = EncodeIdentity(FilmJson.Member(member, "detailId"));
                if (mDetail.Kind is null || mDetail.Value is null)
                    throw new FilmPlaybackException("INVALID_REQUEST");
                db.Execute("""
                    INSERT INTO film_favorite_members(favorite_id,position,source_key,detail_kind,detail_text)
                    VALUES($fid,$pos,$skey,$dkind,$dval)
                    """,
                    ("$fid", favoriteId),
                    ("$pos", idx++),
                    ("$skey", FilmJson.Text(member, "sourceKey")),
                    ("$dkind", mDetail.Kind),
                    ("$dval", mDetail.Value));
            }
        }

        db.CommitRevision(Owner, db.Revision(Owner), Guid.NewGuid().ToString("D"));
        return true;
    }

    private static bool SameFavorite(FilmCardItem left, FilmCardItem right)
    {
        if (left.Id != right.Id || left.Title != right.Title || left.Poster != right.Poster ||
            left.Score != right.Score || left.TextBadge != right.TextBadge ||
            left.Subtitle != right.Subtitle || left.SearchContext != right.SearchContext)
            return false;
        var lt = left.PlaybackContext?.Target;
        var rt = right.PlaybackContext?.Target;
        if (lt is null || rt is null) return lt is null && rt is null;
        return lt.Id == rt.Id && lt.Kind == rt.Kind && lt.Title == rt.Title &&
            lt.MetadataProvider == rt.MetadataProvider && lt.MetadataId == rt.MetadataId &&
            lt.Year == rt.Year && lt.Season == rt.Season && lt.Episode == rt.Episode &&
            lt.GlobalEpisode == rt.GlobalEpisode &&
            SameJson(lt.Source, rt.Source) && SameJson(lt.Members, rt.Members) &&
            left.PlaybackContext!.EntrySurface == right.PlaybackContext!.EntrySurface &&
            left.PlaybackContext.DataOwner == right.PlaybackContext.DataOwner;

        static bool SameJson(JsonElement? a, JsonElement? b) =>
            a is { } leftVal && b is { } rightVal
                ? JsonElement.DeepEquals(leftVal, rightVal)
                : a is null && b is null;
    }

    private static FilmCardItem? ReadSingle(DatabaseSession db, string favoriteId)
    {
        var members = db.Query("""
            SELECT source_key,detail_kind,detail_text
            FROM film_favorite_members
            WHERE favorite_id=$fid
            ORDER BY position
            """,
            row => (row.GetString(0), row.GetString(1), row.GetString(2)),
            ("$fid", favoriteId));
        var map = new Dictionary<string, List<(string, string, string)>>(StringComparer.Ordinal);
        if (members.Count > 0) map[favoriteId] = members.ToList();

        return db.TryQuerySingle("""
            SELECT favorite_id,item_id,title,poster,score,text_badge,subtitle,
                   target_id,target_kind,target_title,metadata_provider,metadata_id_text,
                   target_year,season,episode,global_episode,
                   source_key,source_engine,source_installation_id,
                   source_site_kind,source_site_text,
                   source_detail_kind,source_detail_text,
                   source_line_kind,source_line_text
            FROM film_favorites
            WHERE favorite_id=$fid
            """,
            row => ReadFavoriteRow(row, map),
            out var item,
            ("$fid", favoriteId))
            ? item
            : null;
    }

    private static FilmCardItem ReadFavoriteRow(
        Microsoft.Data.Sqlite.SqliteDataReader row,
        Dictionary<string, List<(string SourceKey, string DetailKind, string DetailText)>> membersByFav)
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
            throw new DocumentStoreException(DocumentStoreError.Corrupt);

        JsonElement? source = null;
        string? sKey = row.GetNullableString(16);
        string? sEngine = row.GetNullableString(17);
        string? sInst = row.GetNullableString(18);
        string? sSiteKind = row.GetNullableString(19);
        string? sSiteVal = row.GetNullableString(20);
        string? sDetailKind = row.GetNullableString(21);
        string? sDetailVal = row.GetNullableString(22);
        string? sLineKind = row.GetNullableString(23);
        string? sLineVal = row.GetNullableString(24);
        if (sKey is not null || sEngine is not null || sInst is not null ||
            sSiteKind is not null || sDetailKind is not null || sLineKind is not null)
        {
            var obj = new JsonObject();
            if (sKey is not null) obj["key"] = sKey;
            if (sEngine is not null) obj["engine"] = sEngine;
            if (sInst is not null) obj["installationId"] = sInst;
            if (sSiteKind is not null) obj["siteId"] = DecodeIdentity(sSiteKind, sSiteVal!);
            if (sDetailKind is not null) obj["detailId"] = DecodeIdentity(sDetailKind, sDetailVal!);
            if (sLineKind is not null) obj["lineId"] = DecodeIdentity(sLineKind, sLineVal!);
            source = JsonSerializer.SerializeToElement(obj);
        }

        JsonElement? members = null;
        if (targetKind == "group" || membersByFav.ContainsKey(favoriteId))
        {
            var arr = new JsonArray();
            if (membersByFav.TryGetValue(favoriteId, out var list))
            {
                foreach (var m in list)
                    arr.Add(new JsonObject
                    {
                        ["sourceKey"] = m.SourceKey,
                        ["detailId"] = DecodeIdentity(m.DetailKind, m.DetailText)
                    });
            }
            members = JsonSerializer.SerializeToElement(arr);
        }

        var target = new FilmPlaybackTarget(
            targetId,
            targetKind,
            targetTitle,
            row.GetNullableString(10),
            row.GetNullableString(11),
            row.GetNullableInt32(12),
            row.GetNullableInt32(13),
            row.GetNullableInt32(14),
            row.GetNullableInt32(15),
            source,
            members);
        try { target.Validate(); }
        catch (FilmPlaybackException) { throw new DocumentStoreException(DocumentStoreError.Corrupt); }

        return new FilmCardItem(itemId, title, poster, score)
        {
            TextBadge = textBadge,
            Subtitle = subtitle,
            PlaybackContext = new FilmPlaybackRequest(target)
        };
    }

    private static FilmCardItem NormalizeFavorite(FilmCardItem item)
    {
        var target = item.PlaybackContext!.Target;
        JsonElement? normalizedSource = null;
        if (target.Kind is "source" or "group" && target.Source is { ValueKind: JsonValueKind.Object } src)
        {
            var obj = new JsonObject();
            foreach (string name in new[] { "key", "engine", "installationId", "siteId", "detailId", "lineId" })
            {
                if (!src.TryGetProperty(name, out var field) || field.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    continue;
                if (name is "siteId" or "detailId" or "lineId")
                {
                    var (kind, val) = EncodeIdentity(field);
                    if (kind is not null && val is not null) obj[name] = DecodeIdentity(kind, val);
                }
                else if (field.ValueKind == JsonValueKind.String)
                {
                    obj[name] = field.GetString();
                }
            }
            if (obj.Count > 0) normalizedSource = JsonSerializer.SerializeToElement(obj);
        }

        JsonElement? normalizedMembers = null;
        if (target.Kind == "group" && target.Members is { ValueKind: JsonValueKind.Array } rawMembers)
        {
            var arr = new JsonArray();
            foreach (var member in rawMembers.EnumerateArray())
            {
                var (kind, val) = EncodeIdentity(FilmJson.Member(member, "detailId"));
                if (kind is null || val is null) throw new FilmPlaybackException("INVALID_REQUEST");
                arr.Add(new JsonObject
                {
                    ["sourceKey"] = FilmJson.Text(member, "sourceKey"),
                    ["detailId"] = DecodeIdentity(kind, val)
                });
            }
            normalizedMembers = JsonSerializer.SerializeToElement(arr);
        }

        return new FilmCardItem(item.Id, item.Title, item.Poster, item.Score)
        {
            TextBadge = item.TextBadge,
            Subtitle = item.Subtitle,
            PlaybackContext = new FilmPlaybackRequest(target with
            {
                Source = normalizedSource,
                Members = normalizedMembers
            })
        };
    }

    private static string? OptionalText(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString()
            : null;

    private static (string? Kind, string? Value) EncodeIdentity(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => (null, null),
        JsonValueKind.String => ("text", value.GetString()!),
        JsonValueKind.Number when value.TryGetInt64(out long number) => ("integer", number.ToString(CultureInfo.InvariantCulture)),
        _ => throw new FilmPlaybackException("INVALID_REQUEST")
    };

    private static JsonNode DecodeIdentity(string kind, string value) => kind switch
    {
        "text" => JsonValue.Create(value)!,
        "integer" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) => JsonValue.Create(n)!,
        _ => throw new DocumentStoreException(DocumentStoreError.Corrupt)
    };
}
