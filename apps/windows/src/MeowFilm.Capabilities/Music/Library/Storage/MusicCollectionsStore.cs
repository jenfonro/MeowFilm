using MeowFilm.Platform.Storage;
using static MeowFilm.Capabilities.Library.MusicStorageModelHelpers;

namespace MeowFilm.Capabilities.Library;

internal static class MusicCollectionsStore
{
    internal static MusicLibraryAreaRecord? ReadRecord(DatabaseSession db, string scope)
    {
        if (!db.TryQuerySingle("""
            SELECT revision,owner_id,library_revision,next_id
            FROM music_library_state
            WHERE scope=$scope
            """,
            r => (Revision: r.GetInt64(0), OwnerId: r.GetString(1), LibraryRevision: r.GetInt64(2), NextId: r.GetInt64(3)),
            out var state,
            ("$scope", scope)))
            return null;

        var accounts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (plat, accId) in db.Query(
            "SELECT platform,account_id FROM music_library_accounts WHERE scope=$scope ORDER BY platform",
            r => (r.GetString(0), r.GetString(1)), ("$scope", scope)))
            accounts[plat] = accId;

        var songsByCol = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (cid, sid) in db.Query(
            "SELECT collection_id,song_id FROM music_collection_songs WHERE scope=$scope ORDER BY collection_id, position",
            r => (r.GetString(0), r.GetString(1)), ("$scope", scope)))
        {
            if (!songsByCol.TryGetValue(cid, out var list)) songsByCol[cid] = list = [];
            list.Add(sid);
        }

        var baselineByBinding = new Dictionary<(string ColId, int Idx), List<string>>();
        foreach (var (cid, idx, nid) in db.Query("""
            SELECT collection_id,binding_index,native_id
            FROM music_collection_binding_baseline
            WHERE scope=$scope
            ORDER BY collection_id, binding_index, position
            """, r => (r.GetString(0), r.GetInt32(1), r.GetString(2)), ("$scope", scope)))
        {
            var k = (cid, idx);
            if (!baselineByBinding.TryGetValue(k, out var list)) baselineByBinding[k] = list = [];
            list.Add(nid);
        }

        var mapByBinding = new Dictionary<(string ColId, int Idx), Dictionary<string, string>>();
        foreach (var (cid, idx, sid, reid) in db.Query("""
            SELECT collection_id,binding_index,song_id,remote_entry_id
            FROM music_collection_binding_map
            WHERE scope=$scope
            ORDER BY collection_id, binding_index, remote_entry_id
            """, r => (r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetString(3)), ("$scope", scope)))
        {
            var k = (cid, idx);
            if (!mapByBinding.TryGetValue(k, out var dict))
                mapByBinding[k] = dict = new Dictionary<string, string>(StringComparer.Ordinal);
            dict[reid] = sid;
        }

        var bindingsByCol = new Dictionary<string, List<MusicCollectionBindingRecord>>(StringComparer.Ordinal);
        foreach (var (cid, binding) in db.Query("""
            SELECT collection_id,binding_index,platform,account_id,remote_id,writable,name
            FROM music_collection_bindings
            WHERE scope=$scope
            ORDER BY collection_id, binding_index
            """, r =>
            {
                string colId = r.GetString(0);
                int idx = r.GetInt32(1);
                var k = (colId, idx);
                return (colId, new MusicCollectionBindingRecord(
                    r.GetString(2),
                    r.GetString(3),
                    r.GetString(4),
                    r.GetBool(5),
                    r.GetNullableString(6) ?? "",
                    baselineByBinding.TryGetValue(k, out var bl) ? bl.ToArray() : [],
                    mapByBinding.TryGetValue(k, out var mp)
                        ? mp
                        : new Dictionary<string, string>(StringComparer.Ordinal)));
            }, ("$scope", scope)))
        {
            if (!bindingsByCol.TryGetValue(cid, out var list)) bindingsByCol[cid] = list = [];
            list.Add(binding);
        }

        var acksByIntent = new Dictionary<(string ColId, string Key), List<string>>();
        foreach (var (cid, ikey, ack) in db.Query("""
            SELECT collection_id,intent_key,ack_value
            FROM music_collection_intent_acks
            WHERE scope=$scope
            ORDER BY collection_id, intent_key, position
            """, r => (r.GetString(0), r.GetString(1), r.GetString(2)), ("$scope", scope)))
        {
            var k = (cid, ikey);
            if (!acksByIntent.TryGetValue(k, out var list)) acksByIntent[k] = list = [];
            list.Add(ack);
        }

        var intentsByCol = new Dictionary<string, Dictionary<string, MusicCollectionIntentRecord>>(StringComparer.Ordinal);
        foreach (var (cid, ikey, intent) in db.Query("""
            SELECT collection_id,intent_key,present,pending,origin
            FROM music_collection_intents
            WHERE scope=$scope
            ORDER BY collection_id, intent_key
            """, r =>
            {
                string colId = r.GetString(0);
                string key = r.GetString(1);
                return (colId, key, new MusicCollectionIntentRecord(
                    r.GetBool(2),
                    r.GetBool(3),
                    r.GetNullableString(4) ?? "local",
                    acksByIntent.TryGetValue((colId, key), out var acks) ? acks.ToArray() : null));
            }, ("$scope", scope)))
        {
            if (!intentsByCol.TryGetValue(cid, out var dict))
                intentsByCol[cid] = dict = new Dictionary<string, MusicCollectionIntentRecord>(StringComparer.Ordinal);
            dict[ikey] = intent;
        }

        var collections = db.Query("""
            SELECT collection_id,name,kind,revision,manual_order,deleted,locally_created
            FROM music_collections
            WHERE scope=$scope
            ORDER BY position
            """, r =>
            {
                string cid = r.GetString(0);
                return new MusicCollectionRecord(
                    cid,
                    r.GetString(1),
                    r.GetString(2),
                    r.GetInt64(3),
                    r.GetBool(4),
                    r.GetBool(5),
                    r.GetBool(6),
                    songsByCol.TryGetValue(cid, out var sl) ? sl.ToArray() : [],
                    bindingsByCol.TryGetValue(cid, out var bl) ? bl.ToArray() : [],
                    intentsByCol.TryGetValue(cid, out var it)
                        ? it
                        : new Dictionary<string, MusicCollectionIntentRecord>(StringComparer.Ordinal));
            }, ("$scope", scope)).ToArray();

        var networkPlaylists = db.Query("""
            SELECT playlist_id,platform,remote_id,name,cover,song_count,revision,present,
                   sub_account_id,sub_present,sub_list_id,sub_creator_id,sub_creator_list_id,
                   intent_account_id,intent_present
            FROM windows_music_network_playlists
            WHERE scope=$scope
            ORDER BY position
            """, r => new MusicNetworkPlaylistRecord(
                r.GetString(0),
                r.GetString(1),
                r.GetString(2),
                r.GetString(3),
                r.GetString(4),
                r.GetInt64(5),
                r.GetInt64(6),
                r.GetBool(7),
                r.GetNullableString(8),
                r.GetNullableBool(9),
                r.GetNullableString(10),
                r.GetNullableString(11),
                r.GetNullableString(12),
                r.GetNullableString(13),
                r.GetNullableBool(14)), ("$scope", scope)).ToArray();

        return new MusicLibraryAreaRecord(
            state.Revision, state.OwnerId, state.LibraryRevision, state.NextId,
            accounts, collections, networkPlaylists);
    }

    internal static void Save(DatabaseSession db, string scope, MusicLibraryAreaRecord? previous, MusicLibraryAreaRecord next)
    {
        if (previous is null ||
            previous.StateRevision != next.StateRevision ||
            previous.OwnerId != next.OwnerId ||
            previous.LibraryRevision != next.LibraryRevision ||
            previous.NextId != next.NextId)
        {
            db.Execute("""
                INSERT INTO music_library_state(scope,revision,owner_id,library_revision,next_id)
                VALUES($scope,$rev,$owner,$lrev,$next)
                ON CONFLICT(scope) DO UPDATE SET
                    revision=excluded.revision,
                    owner_id=excluded.owner_id,
                    library_revision=excluded.library_revision,
                    next_id=excluded.next_id
                """,
                ("$scope", scope), ("$rev", next.StateRevision), ("$owner", next.OwnerId),
                ("$lrev", next.LibraryRevision), ("$next", next.NextId));
        }

        DiffAccounts(db, scope, previous?.Accounts, next.Accounts);
        DiffCollections(db, scope, previous?.Collections, next.Collections);
        DiffNetworkPlaylists(db, scope, previous?.NetworkPlaylists, next.NetworkPlaylists);
    }

    private static void DiffAccounts(DatabaseSession db, string scope,
        IReadOnlyDictionary<string, string>? previous, IReadOnlyDictionary<string, string> next)
    {
        if (previous is null)
            db.Execute("DELETE FROM music_library_accounts WHERE scope=$scope", ("$scope", scope));
        else
        {
            foreach (var plat in previous.Keys)
                if (!next.ContainsKey(plat))
                    db.Execute("DELETE FROM music_library_accounts WHERE scope=$scope AND platform=$plat",
                        ("$scope", scope), ("$plat", plat));
        }
        foreach (var (plat, accId) in next)
        {
            if (previous is not null && previous.TryGetValue(plat, out var oldAcc) && oldAcc == accId)
                continue;
            db.Execute("""
                INSERT INTO music_library_accounts(scope,platform,account_id) VALUES($scope,$plat,$acc)
                ON CONFLICT(scope,platform) DO UPDATE SET account_id=excluded.account_id
                """, ("$scope", scope), ("$plat", plat), ("$acc", accId));
        }
    }

    private static void DiffCollections(DatabaseSession db, string scope,
        MusicCollectionRecord[]? previous, MusicCollectionRecord[] next)
    {
        var prevById = new Dictionary<string, (int Position, MusicCollectionRecord Col)>(StringComparer.Ordinal);
        if (previous is null)
        {
            db.Execute("DELETE FROM music_collections WHERE scope=$scope", ("$scope", scope));
        }
        else
        {
            for (int i = 0; i < previous.Length; i++)
                prevById[previous[i].CollectionId] = (i, previous[i]);
            var nextIds = new HashSet<string>(next.Select(c => c.CollectionId), StringComparer.Ordinal);
            foreach (var col in previous)
                if (!nextIds.Contains(col.CollectionId))
                    db.Execute("DELETE FROM music_collections WHERE scope=$scope AND collection_id=$cid",
                        ("$scope", scope), ("$cid", col.CollectionId));
        }

        for (int cIdx = 0; cIdx < next.Length; cIdx++)
        {
            var col = next[cIdx];
            bool existed = prevById.TryGetValue(col.CollectionId, out var oldEntry);

            if (!existed || oldEntry.Position != cIdx || !col.HeaderEquals(oldEntry.Col))
            {
                db.Execute("""
                    INSERT INTO music_collections(scope,collection_id,position,name,kind,revision,manual_order,deleted,locally_created)
                    VALUES($scope,$cid,$pos,$name,$kind,$rev,$manual,$del,$local)
                    ON CONFLICT(scope,collection_id) DO UPDATE SET
                        position=excluded.position,
                        name=excluded.name,
                        kind=excluded.kind,
                        revision=excluded.revision,
                        manual_order=excluded.manual_order,
                        deleted=excluded.deleted,
                        locally_created=excluded.locally_created
                    """,
                    ("$scope", scope), ("$cid", col.CollectionId), ("$pos", cIdx),
                    ("$name", col.Name), ("$kind", col.Kind), ("$rev", col.Revision),
                    ("$manual", col.ManualOrder), ("$del", col.Deleted), ("$local", col.LocallyCreated));
            }

            if (!existed || !col.Order.SequenceEqual(oldEntry.Col.Order, StringComparer.Ordinal))
            {
                int prefix = existed ? CommonPrefixLength(oldEntry.Col.Order, col.Order, StringComparer.Ordinal) : 0;
                if (existed && prefix < oldEntry.Col.Order.Length)
                    db.Execute("DELETE FROM music_collection_songs WHERE scope=$scope AND collection_id=$cid AND position>=$pos",
                        ("$scope", scope), ("$cid", col.CollectionId), ("$pos", prefix));
                for (int sIdx = prefix; sIdx < col.Order.Length; sIdx++)
                    db.Execute("INSERT INTO music_collection_songs(scope,collection_id,position,song_id) VALUES($scope,$cid,$pos,$sid)",
                        ("$scope", scope), ("$cid", col.CollectionId), ("$pos", sIdx), ("$sid", col.Order[sIdx]));
            }

            if (!existed || !col.BindingsEqual(oldEntry.Col))
            {
                if (existed)
                    db.Execute("DELETE FROM music_collection_bindings WHERE scope=$scope AND collection_id=$cid",
                        ("$scope", scope), ("$cid", col.CollectionId));
                for (int bIdx = 0; bIdx < col.Bindings.Length; bIdx++)
                {
                    var b = col.Bindings[bIdx];
                    db.Execute("""
                        INSERT INTO music_collection_bindings(scope,collection_id,binding_index,platform,account_id,remote_id,writable,name)
                        VALUES($scope,$cid,$bidx,$plat,$acc,$rid,$wr,$name)
                        """,
                        ("$scope", scope), ("$cid", col.CollectionId), ("$bidx", bIdx),
                        ("$plat", b.Platform), ("$acc", b.AccountId), ("$rid", b.RemoteId),
                        ("$wr", b.Writable), ("$name", b.Name));
                    for (int i = 0; i < b.Baseline.Length; i++)
                        db.Execute("""
                            INSERT INTO music_collection_binding_baseline(scope,collection_id,binding_index,position,native_id)
                            VALUES($scope,$cid,$bidx,$pos,$nid)
                            """, ("$scope", scope), ("$cid", col.CollectionId), ("$bidx", bIdx), ("$pos", i), ("$nid", b.Baseline[i]));
                    foreach (var (reid, sid) in b.Map)
                        db.Execute("""
                            INSERT INTO music_collection_binding_map(scope,collection_id,binding_index,song_id,remote_entry_id)
                            VALUES($scope,$cid,$bidx,$sid,$reid)
                            """, ("$scope", scope), ("$cid", col.CollectionId), ("$bidx", bIdx), ("$sid", sid), ("$reid", reid));
                }
            }

            if (!existed || !col.IntentsEqual(oldEntry.Col))
            {
                var prevIntents = existed ? oldEntry.Col.Intents : null;
                if (prevIntents is not null)
                {
                    foreach (var ikey in prevIntents.Keys)
                        if (!col.Intents.ContainsKey(ikey))
                            db.Execute("DELETE FROM music_collection_intents WHERE scope=$scope AND collection_id=$cid AND intent_key=$ikey",
                                ("$scope", scope), ("$cid", col.CollectionId), ("$ikey", ikey));
                }
                foreach (var (ikey, intent) in col.Intents)
                {
                    MusicCollectionIntentRecord? oldIntent = null;
                    if (prevIntents is not null && prevIntents.TryGetValue(ikey, out oldIntent) && intent.ContentEquals(oldIntent))
                        continue;
                    db.Execute("""
                        INSERT INTO music_collection_intents(scope,collection_id,intent_key,present,pending,origin)
                        VALUES($scope,$cid,$ikey,$pres,$pend,$orig)
                        ON CONFLICT(scope,collection_id,intent_key) DO UPDATE SET
                            present=excluded.present,pending=excluded.pending,origin=excluded.origin
                        """,
                        ("$scope", scope), ("$cid", col.CollectionId), ("$ikey", ikey),
                        ("$pres", intent.Present), ("$pend", intent.Pending), ("$orig", intent.Origin));
                    bool acksSame = oldIntent is not null && (intent.Acknowledged is null
                        ? oldIntent.Acknowledged is null
                        : oldIntent.Acknowledged is not null && intent.Acknowledged.SequenceEqual(oldIntent.Acknowledged, StringComparer.Ordinal));
                    if (!acksSame)
                    {
                        db.Execute("DELETE FROM music_collection_intent_acks WHERE scope=$scope AND collection_id=$cid AND intent_key=$ikey",
                            ("$scope", scope), ("$cid", col.CollectionId), ("$ikey", ikey));
                        if (intent.Acknowledged is { Length: > 0 } acks)
                            for (int i = 0; i < acks.Length; i++)
                                db.Execute("""
                                    INSERT INTO music_collection_intent_acks(scope,collection_id,intent_key,position,ack_value)
                                    VALUES($scope,$cid,$ikey,$pos,$val)
                                    """, ("$scope", scope), ("$cid", col.CollectionId), ("$ikey", ikey), ("$pos", i), ("$val", acks[i]));
                    }
                }
            }
        }
    }

    private static void DiffNetworkPlaylists(DatabaseSession db, string scope,
        MusicNetworkPlaylistRecord[]? previous, MusicNetworkPlaylistRecord[] next)
    {
        var prevById = new Dictionary<string, (int Position, MusicNetworkPlaylistRecord Playlist)>(StringComparer.Ordinal);
        if (previous is null)
        {
            db.Execute("DELETE FROM windows_music_network_playlists WHERE scope=$scope", ("$scope", scope));
        }
        else
        {
            for (int i = 0; i < previous.Length; i++)
                prevById[previous[i].PlaylistId] = (i, previous[i]);
            var nextIds = new HashSet<string>(next.Select(p => p.PlaylistId), StringComparer.Ordinal);
            foreach (var np in previous)
                if (!nextIds.Contains(np.PlaylistId))
                    db.Execute("DELETE FROM windows_music_network_playlists WHERE scope=$scope AND playlist_id=$pid",
                        ("$scope", scope), ("$pid", np.PlaylistId));
        }

        for (int i = 0; i < next.Length; i++)
        {
            var np = next[i];
            if (prevById.TryGetValue(np.PlaylistId, out var oldEntry) && oldEntry.Position == i && oldEntry.Playlist == np)
                continue;
            db.Execute("""
                INSERT INTO windows_music_network_playlists(
                    scope,playlist_id,position,platform,remote_id,name,cover,song_count,revision,present,
                    sub_account_id,sub_present,sub_list_id,sub_creator_id,sub_creator_list_id,
                    intent_account_id,intent_present)
                VALUES($scope,$pid,$pos,$plat,$rid,$name,$cov,$cnt,$rev,$pres,$sa,$sp,$sl,$sc,$scl,$ia,$ip)
                ON CONFLICT(scope,playlist_id) DO UPDATE SET
                    position=excluded.position,platform=excluded.platform,remote_id=excluded.remote_id,
                    name=excluded.name,cover=excluded.cover,song_count=excluded.song_count,
                    revision=excluded.revision,present=excluded.present,
                    sub_account_id=excluded.sub_account_id,sub_present=excluded.sub_present,
                    sub_list_id=excluded.sub_list_id,sub_creator_id=excluded.sub_creator_id,
                    sub_creator_list_id=excluded.sub_creator_list_id,
                    intent_account_id=excluded.intent_account_id,intent_present=excluded.intent_present
                """,
                ("$scope", scope), ("$pid", np.PlaylistId), ("$pos", i),
                ("$plat", np.Platform), ("$rid", np.RemoteId),
                ("$name", np.Name), ("$cov", np.Cover),
                ("$cnt", np.SongCount), ("$rev", np.Revision), ("$pres", np.Present),
                ("$sa", np.SubAccountId), ("$sp", np.SubPresent),
                ("$sl", np.SubListId), ("$sc", np.SubCreatorId), ("$scl", np.SubCreatorListId),
                ("$ia", np.IntentAccountId), ("$ip", np.IntentPresent));
        }
    }
}
