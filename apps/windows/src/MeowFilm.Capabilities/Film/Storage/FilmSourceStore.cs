using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal sealed record StoredFilmCat(
    string InstallationId,
    string Name,
    string SourceType,
    string? SourceUrl,
    bool Enabled,
    long? ImportedAt,
    long? UpdatedAt,
    string UpdateState,
    string? UpdateError,
    string? SourceHash,
    IReadOnlyList<FilmSourceSite> Sites,
    long InputRevision = 0)
{
    internal static StoredFilmCat From(FilmCatSource cat) => new(
        cat.InstallationId,
        cat.Name,
        cat.SourceType,
        cat.SourceUrl,
        cat.Enabled,
        cat.ImportedAt,
        cat.UpdatedAt,
        cat.UpdateState,
        cat.UpdateError,
        cat.SourceHash,
        cat.Sites,
        cat.InputRevision);

    internal FilmCatSource ToSnapshot() => new(
        InstallationId,
        Name,
        SourceType,
        SourceUrl,
        Enabled,
        ImportedAt,
        UpdatedAt,
        UpdateState,
        UpdateError,
        0,
        Enabled ? "stopped" : "disabled",
        SourceHash,
        null,
        Sites)
    { InputRevision = InputRevision };
}

internal sealed record StoredFilmSourceState(
    IReadOnlyList<StoredFilmCat> Cats,
    IReadOnlyList<FilmVideoSource> Videos,
    IReadOnlyList<FilmSourceKey> Deleted);

internal static class FilmSourceStore
{
    internal const string Owner = "film.video-sources.v1";

    internal static (StoredFilmSourceState State, StorageRevision Revision) Read(DatabaseSession db)
    {
        var revision = db.Revision(Owner);

        var catActionRows = db.Query("""
            SELECT installation_id,site_key,action
            FROM windows_film_source_cat_site_actions
            ORDER BY installation_id, site_key, position
            """,
            row => (Key: new FilmSourceKey(row.GetString(0), row.GetString(1)), Action: row.GetString(2)));
        var catActions = GroupLists(catActionRows, item => item.Key, item => item.Action);

        var catMethodRows = db.Query("""
            SELECT installation_id,site_key,method_key,method_name
            FROM windows_film_source_cat_site_methods
            ORDER BY installation_id, site_key, method_key
            """,
            row => (Key: new FilmSourceKey(row.GetString(0), row.GetString(1)), MethodKey: row.GetString(2), MethodName: row.GetString(3)));
        var catMethods = GroupDicts(catMethodRows, item => item.Key, item => item.MethodKey, item => item.MethodName);

        var siteRows = db.Query("""
            SELECT installation_id,site_key,name,api
            FROM windows_film_source_cat_sites
            ORDER BY installation_id, position
            """,
            row =>
            {
                var key = new FilmSourceKey(row.GetString(0), row.GetString(1));
                return (
                    InstallationId: key.InstallationId,
                    Site: new FilmSourceSite(
                        key.SiteKey,
                        row.GetString(2),
                        row.GetString(3),
                        catActions.TryGetValue(key, out var actions) ? actions.ToArray() : Array.Empty<string>(),
                        catMethods.TryGetValue(key, out var methods) ? methods : new Dictionary<string, string>(StringComparer.Ordinal)));
            });
        var sitesByCat = GroupLists(siteRows, item => item.InstallationId, item => item.Site, StringComparer.Ordinal);

        var cats = db.Query("""
            SELECT installation_id,name,source_type,source_url,enabled,imported_at,updated_at,
                   update_state,update_error,source_hash,input_revision
            FROM windows_film_source_cats
            ORDER BY position
            """,
            row =>
            {
                string id = row.GetString(0);
                long? importedAt = row.GetNullableInt64(5);
                long? updatedAt = row.GetNullableInt64(6);
                return new StoredFilmCat(
                    id,
                    row.GetString(1),
                    row.GetString(2),
                    row.GetNullableString(3),
                    row.GetBool(4),
                    importedAt is >= 0 and <= 253402300799999 ? importedAt : null,
                    updatedAt is >= 0 and <= 253402300799999 ? updatedAt : null,
                    row.GetString(7),
                    row.GetNullableString(8),
                    row.GetNullableString(9),
                    sitesByCat.TryGetValue(id, out var list) ? list.ToArray() : Array.Empty<FilmSourceSite>(),
                    row.GetInt64(10));
            }).ToArray();

        var videoActionRows = db.Query("""
            SELECT installation_id,site_key,action
            FROM windows_film_source_video_actions
            ORDER BY installation_id, site_key, position
            """,
            row => (Key: new FilmSourceKey(row.GetString(0), row.GetString(1)), Action: row.GetString(2)));
        var videoActions = GroupLists(videoActionRows, item => item.Key, item => item.Action);

        var videoMethodRows = db.Query("""
            SELECT installation_id,site_key,method_key,method_name
            FROM windows_film_source_video_methods
            ORDER BY installation_id, site_key, method_key
            """,
            row => (Key: new FilmSourceKey(row.GetString(0), row.GetString(1)), MethodKey: row.GetString(2), MethodName: row.GetString(3)));
        var videoMethods = GroupDicts(videoMethodRows, item => item.Key, item => item.MethodKey, item => item.MethodName);

        var videoStepRows = db.Query("""
            SELECT installation_id,site_key,operation,state,error
            FROM windows_film_source_video_steps
            ORDER BY installation_id, site_key, position
            """,
            row => (Key: new FilmSourceKey(row.GetString(0), row.GetString(1)),
                Step: new FilmSourceProbeStep(row.GetString(2), row.GetString(3), row.GetNullableString(4))));
        var videoSteps = GroupLists(videoStepRows, item => item.Key, item => item.Step);

        var videos = db.Query("""
            SELECT installation_id,site_key,sort_order,name,api,enabled,home_visible,search_enabled,aggregate_images,
                   detection_result,detection_checked_at,detection_playback_resolved
            FROM windows_film_source_videos
            ORDER BY position
            """,
            row =>
            {
                var key = new FilmSourceKey(row.GetString(0), row.GetString(1));
                string? detResult = row.GetNullableString(9);
                long? detCheckedAt = row.GetNullableInt64(10);
                bool? detResolved = row.GetNullableBool(11);
                FilmSourceDetection? detection = detResult is not null && detCheckedAt is not null && detResolved is not null
                    ? new FilmSourceDetection(
                        detResult,
                        detCheckedAt.Value,
                        detResolved.Value,
                        videoSteps.TryGetValue(key, out var steps) ? steps.ToArray() : Array.Empty<FilmSourceProbeStep>())
                    : null;
                return new FilmVideoSource(
                    key,
                    row.GetString(3),
                    row.GetString(4),
                    videoActions.TryGetValue(key, out var actions) ? actions.ToArray() : Array.Empty<string>(),
                    videoMethods.TryGetValue(key, out var methods) ? methods : new Dictionary<string, string>(StringComparer.Ordinal),
                    new FilmVideoOptions(row.GetBool(5), row.GetBool(6), row.GetBool(7), row.GetBool(8)),
                    row.GetInt32(2),
                    detection);
            }).ToArray();

        var deleted = db.Query("""
            SELECT installation_id,site_key
            FROM film_source_deletions
            ORDER BY position
            """,
            row => new FilmSourceKey(row.GetString(0), row.GetString(1))).ToArray();

        return (new StoredFilmSourceState(cats, videos, deleted), revision);
    }

    internal static Task<(StoredFilmSourceState State, StorageRevision Revision)> ReadAsync(
        IStorageDatabase database, CancellationToken token) =>
        database.ReadSnapshotAsync(Read, token);

    internal static Task<StorageRevision> SaveAsync(
        IStorageDatabase database,
        StoredFilmSourceState state,
        StorageRevision expected,
        CancellationToken token) =>
        database.WriteAsync(db =>
        {
            if (db.Revision(Owner) != expected)
                throw new DocumentStoreException(DocumentStoreError.Conflict);

            var (previous, _) = Read(db);
            ReconcileCats(db, previous.Cats, state.Cats);
            ReconcileVideos(db, previous.Videos, state.Videos);
            ReconcileDeletions(db, previous.Deleted, state.Deleted);

            return db.CommitRevision(Owner, expected, Guid.NewGuid().ToString("D"));
        }, token);

    private static void ReconcileCats(DatabaseSession db, IReadOnlyList<StoredFilmCat> previous, IReadOnlyList<StoredFilmCat> next)
    {
        var prevMap = previous.Select((c, i) => (c.InstallationId, Pos: i, Cat: c))
            .ToDictionary(x => x.InstallationId, x => (x.Pos, x.Cat), StringComparer.Ordinal);
        var nextIds = next.Select(c => c.InstallationId).ToHashSet(StringComparer.Ordinal);
        foreach (var removed in prevMap.Keys)
            if (!nextIds.Contains(removed))
                db.Execute("DELETE FROM windows_film_source_cats WHERE installation_id=$id", ("$id", removed));

        for (int i = 0; i < next.Count; i++)
        {
            var cat = next[i];
            if (prevMap.TryGetValue(cat.InstallationId, out var old) && old.Pos == i && CatEquals(old.Cat, cat))
                continue;
            db.Execute("""
                INSERT INTO windows_film_source_cats(
                    installation_id,position,name,source_type,source_url,enabled,imported_at,updated_at,
                    update_state,update_error,source_hash,input_revision)
                VALUES($id,$pos,$name,$type,$url,$en,$imp,$upd,$ustate,$uerr,$hash,$rev)
                ON CONFLICT(installation_id) DO UPDATE SET
                    position=excluded.position,
                    name=excluded.name,
                    source_type=excluded.source_type,
                    source_url=excluded.source_url,
                    enabled=excluded.enabled,
                    imported_at=excluded.imported_at,
                    updated_at=excluded.updated_at,
                    update_state=excluded.update_state,
                    update_error=excluded.update_error,
                    source_hash=excluded.source_hash,
                    input_revision=excluded.input_revision
                """,
                ("$id", cat.InstallationId), ("$pos", i), ("$name", cat.Name), ("$type", cat.SourceType),
                ("$url", cat.SourceUrl), ("$en", cat.Enabled), ("$imp", cat.ImportedAt), ("$upd", cat.UpdatedAt),
                ("$ustate", cat.UpdateState), ("$uerr", cat.UpdateError), ("$hash", cat.SourceHash), ("$rev", cat.InputRevision));

            if (old.Cat is not null && SitesEqual(old.Cat.Sites, cat.Sites)) continue;
            db.Execute("DELETE FROM windows_film_source_cat_sites WHERE installation_id=$id", ("$id", cat.InstallationId));
            for (int s = 0; s < cat.Sites.Count; s++)
            {
                var site = cat.Sites[s];
                db.Execute("""
                    INSERT INTO windows_film_source_cat_sites(installation_id,site_key,position,name,api)
                    VALUES($id,$skey,$pos,$name,$api)
                    """, ("$id", cat.InstallationId), ("$skey", site.Key), ("$pos", s), ("$name", site.Name), ("$api", site.Api));

                for (int a = 0; a < site.Actions.Count; a++)
                    db.Execute("""
                        INSERT INTO windows_film_source_cat_site_actions(installation_id,site_key,position,action)
                        VALUES($id,$skey,$pos,$act)
                        """, ("$id", cat.InstallationId), ("$skey", site.Key), ("$pos", a), ("$act", site.Actions[a]));

                foreach (var (mKey, mName) in site.Methods.OrderBy(p => p.Key, StringComparer.Ordinal))
                    db.Execute("""
                        INSERT INTO windows_film_source_cat_site_methods(installation_id,site_key,method_key,method_name)
                        VALUES($id,$skey,$mkey,$mname)
                        """, ("$id", cat.InstallationId), ("$skey", site.Key), ("$mkey", mKey), ("$mname", mName));
            }
        }
    }

    private static void ReconcileVideos(DatabaseSession db, IReadOnlyList<FilmVideoSource> previous, IReadOnlyList<FilmVideoSource> next)
    {
        var prevMap = previous.Select((v, i) => (v.Key, Pos: i, Video: v))
            .ToDictionary(x => x.Key, x => (x.Pos, x.Video));
        var nextKeys = next.Select(v => v.Key).ToHashSet();
        foreach (var removed in prevMap.Keys)
            if (!nextKeys.Contains(removed))
                db.Execute("DELETE FROM windows_film_source_videos WHERE installation_id=$id AND site_key=$skey",
                    ("$id", removed.InstallationId), ("$skey", removed.SiteKey));

        for (int i = 0; i < next.Count; i++)
        {
            var video = next[i];
            bool hadOld = prevMap.TryGetValue(video.Key, out var old);
            if (hadOld && old.Pos == i && VideoEquals(old.Video, video)) continue;
            db.Execute("""
                INSERT INTO windows_film_source_videos(
                    installation_id,site_key,position,sort_order,name,api,enabled,home_visible,search_enabled,aggregate_images,
                    detection_result,detection_checked_at,detection_playback_resolved)
                VALUES($id,$skey,$pos,$ord,$name,$api,$en,$home,$search,$agg,$dres,$dchk,$dplay)
                ON CONFLICT(installation_id,site_key) DO UPDATE SET
                    position=excluded.position,
                    sort_order=excluded.sort_order,
                    name=excluded.name,
                    api=excluded.api,
                    enabled=excluded.enabled,
                    home_visible=excluded.home_visible,
                    search_enabled=excluded.search_enabled,
                    aggregate_images=excluded.aggregate_images,
                    detection_result=excluded.detection_result,
                    detection_checked_at=excluded.detection_checked_at,
                    detection_playback_resolved=excluded.detection_playback_resolved
                """,
                ("$id", video.Key.InstallationId), ("$skey", video.Key.SiteKey), ("$pos", i),
                ("$ord", video.Order), ("$name", video.Name), ("$api", video.Api),
                ("$en", video.Options.Enabled), ("$home", video.Options.HomeVisible),
                ("$search", video.Options.SearchEnabled), ("$agg", video.Options.AggregateImages),
                ("$dres", video.Detection?.Result), ("$dchk", video.Detection?.CheckedAt),
                ("$dplay", video.Detection?.PlaybackResolved));

            if (!hadOld || !old.Video.Actions.SequenceEqual(video.Actions, StringComparer.Ordinal))
            {
                db.Execute("DELETE FROM windows_film_source_video_actions WHERE installation_id=$id AND site_key=$skey",
                    ("$id", video.Key.InstallationId), ("$skey", video.Key.SiteKey));
                for (int a = 0; a < video.Actions.Count; a++)
                    db.Execute("""
                        INSERT INTO windows_film_source_video_actions(installation_id,site_key,position,action)
                        VALUES($id,$skey,$pos,$act)
                        """, ("$id", video.Key.InstallationId), ("$skey", video.Key.SiteKey), ("$pos", a), ("$act", video.Actions[a]));
            }

            if (!hadOld || !DictEquals(old.Video.Methods, video.Methods))
            {
                db.Execute("DELETE FROM windows_film_source_video_methods WHERE installation_id=$id AND site_key=$skey",
                    ("$id", video.Key.InstallationId), ("$skey", video.Key.SiteKey));
                foreach (var (mKey, mName) in video.Methods.OrderBy(p => p.Key, StringComparer.Ordinal))
                    db.Execute("""
                        INSERT INTO windows_film_source_video_methods(installation_id,site_key,method_key,method_name)
                        VALUES($id,$skey,$mkey,$mname)
                        """, ("$id", video.Key.InstallationId), ("$skey", video.Key.SiteKey), ("$mkey", mKey), ("$mname", mName));
            }

            if (!hadOld || !DetectionEquals(old.Video.Detection, video.Detection))
            {
                db.Execute("DELETE FROM windows_film_source_video_steps WHERE installation_id=$id AND site_key=$skey",
                    ("$id", video.Key.InstallationId), ("$skey", video.Key.SiteKey));
                if (video.Detection is { Steps: var steps })
                {
                    for (int st = 0; st < steps.Count; st++)
                    {
                        var step = steps[st];
                        db.Execute("""
                            INSERT INTO windows_film_source_video_steps(installation_id,site_key,position,operation,state,error)
                            VALUES($id,$skey,$pos,$op,$st,$err)
                            """,
                            ("$id", video.Key.InstallationId), ("$skey", video.Key.SiteKey), ("$pos", st),
                            ("$op", step.Operation), ("$st", step.State), ("$err", step.Error));
                    }
                }
            }
        }
    }

    private static void ReconcileDeletions(DatabaseSession db, IReadOnlyList<FilmSourceKey> previous, IReadOnlyList<FilmSourceKey> next)
    {
        var prevMap = previous.Select((k, i) => (Key: k, Pos: i)).ToDictionary(x => x.Key, x => x.Pos);
        var nextSet = next.ToHashSet();
        foreach (var removed in prevMap.Keys)
            if (!nextSet.Contains(removed))
                db.Execute("DELETE FROM film_source_deletions WHERE installation_id=$id AND site_key=$skey",
                    ("$id", removed.InstallationId), ("$skey", removed.SiteKey));

        for (int i = 0; i < next.Count; i++)
        {
            var key = next[i];
            if (prevMap.TryGetValue(key, out int oldPos) && oldPos == i) continue;
            db.Execute("""
                INSERT INTO film_source_deletions(installation_id,site_key,position)
                VALUES($id,$skey,$pos)
                ON CONFLICT(installation_id,site_key) DO UPDATE SET position=excluded.position
                """, ("$id", key.InstallationId), ("$skey", key.SiteKey), ("$pos", i));
        }
    }

    internal static bool StateEquals(StoredFilmSourceState left, StoredFilmSourceState right)
    {
        if (left.Cats.Count != right.Cats.Count ||
            left.Videos.Count != right.Videos.Count ||
            left.Deleted.Count != right.Deleted.Count)
            return false;

        for (int i = 0; i < left.Deleted.Count; i++)
            if (left.Deleted[i] != right.Deleted[i]) return false;

        for (int i = 0; i < left.Cats.Count; i++)
            if (!CatEquals(left.Cats[i], right.Cats[i])) return false;

        for (int i = 0; i < left.Videos.Count; i++)
            if (!VideoEquals(left.Videos[i], right.Videos[i])) return false;

        return true;
    }

    private static bool SitesEqual(IReadOnlyList<FilmSourceSite> a, IReadOnlyList<FilmSourceSite> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!SiteEquals(a[i], b[i])) return false;
        return true;
    }

    private static bool CatEquals(StoredFilmCat a, StoredFilmCat b) =>
        a.InstallationId == b.InstallationId && a.Name == b.Name && a.SourceType == b.SourceType &&
        a.SourceUrl == b.SourceUrl && a.Enabled == b.Enabled && a.ImportedAt == b.ImportedAt &&
        a.UpdatedAt == b.UpdatedAt && a.UpdateState == b.UpdateState && a.UpdateError == b.UpdateError &&
        a.SourceHash == b.SourceHash && a.InputRevision == b.InputRevision &&
        SitesEqual(a.Sites, b.Sites);

    private static bool SiteEquals(FilmSourceSite a, FilmSourceSite b) =>
        a.Key == b.Key && a.Name == b.Name && a.Api == b.Api &&
        a.Actions.SequenceEqual(b.Actions, StringComparer.Ordinal) &&
        DictEquals(a.Methods, b.Methods);

    private static bool VideoEquals(FilmVideoSource a, FilmVideoSource b) =>
        a.Key == b.Key && a.Name == b.Name && a.Api == b.Api && a.Order == b.Order &&
        a.Options == b.Options &&
        a.Actions.SequenceEqual(b.Actions, StringComparer.Ordinal) &&
        DictEquals(a.Methods, b.Methods) &&
        DetectionEquals(a.Detection, b.Detection);

    private static bool DetectionEquals(FilmSourceDetection? a, FilmSourceDetection? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.Result == b.Result && a.CheckedAt == b.CheckedAt &&
            a.PlaybackResolved == b.PlaybackResolved && a.Steps.SequenceEqual(b.Steps);
    }

    private static bool DictEquals(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in a)
            if (!b.TryGetValue(k, out var bv) || !string.Equals(v, bv, StringComparison.Ordinal))
                return false;
        return true;
    }

    private static Dictionary<TKey, List<TVal>> GroupLists<TRow, TKey, TVal>(
        IReadOnlyList<TRow> rows, Func<TRow, TKey> keySelector, Func<TRow, TVal> valSelector,
        IEqualityComparer<TKey>? comparer = null) where TKey : notnull
    {
        var map = new Dictionary<TKey, List<TVal>>(comparer);
        foreach (var row in rows)
        {
            var key = keySelector(row);
            if (!map.TryGetValue(key, out var list))
            {
                list = [];
                map[key] = list;
            }
            list.Add(valSelector(row));
        }
        return map;
    }

    private static Dictionary<TKey, Dictionary<string, string>> GroupDicts<TRow, TKey>(
        IReadOnlyList<TRow> rows, Func<TRow, TKey> keySelector,
        Func<TRow, string> nameSelector, Func<TRow, string> valSelector) where TKey : notnull
    {
        var map = new Dictionary<TKey, Dictionary<string, string>>();
        foreach (var row in rows)
        {
            var key = keySelector(row);
            if (!map.TryGetValue(key, out var dict))
            {
                dict = new Dictionary<string, string>(StringComparer.Ordinal);
                map[key] = dict;
            }
            dict[nameSelector(row)] = valSelector(row);
        }
        return map;
    }
}
