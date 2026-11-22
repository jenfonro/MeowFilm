using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal sealed record StoredNovelSourceRow(
    string InstallationId,
    long Revision,
    string Name,
    string SourceUrl,
    string Group,
    long Order,
    bool Enabled,
    bool EnabledExplore,
    string Protocol,
    StorageValue Definition);

internal static class NovelSourceStore
{
    private const long MaximumRevision = 9007199254740991;

    internal static (StoredNovelSourceRow[] Sources, long Revision, StorageRevision StorageRevision) Load(DatabaseSession db, string owner)
    {
        var storageRevision = db.Revision(owner);
        var definitionsById = LoadAllDefinitions(db);
        var sources = db.Query("""
            SELECT installation_id,revision,name,source_url,source_group,sort_order,enabled,enabled_explore,protocol
            FROM windows_novel_sources
            ORDER BY position, installation_id
            """, row =>
            {
                string id = row.GetString(0);
                long sourceRev = row.GetInt64(1);
                string name = row.GetString(2);
                string url = row.GetString(3);
                string group = row.GetString(4);
                long order = row.GetInt64(5);
                bool enabled = row.GetBool(6);
                bool enabledExplore = row.GetBool(7);
                string protocol = row.GetString(8);
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url) ||
                    sourceRev is < 1 or > MaximumRevision || protocol is not ("legado" or "qysg") ||
                    !definitionsById.TryGetValue(id, out var definitionNode))
                    throw new DocumentStoreException(DocumentStoreError.Corrupt);
                return new StoredNovelSourceRow(id, sourceRev, name, url, group, order, enabled, enabledExplore, protocol, definitionNode);
            }).ToArray();

        if (storageRevision == StorageRevision.Empty)
        {
            if (sources.Length > 0) throw new DocumentStoreException(DocumentStoreError.Corrupt);
            return ([], 1, storageRevision);
        }
        long revision = checked(storageRevision.Revision + 1);
        if (revision is < 1 or > MaximumRevision)
            throw new DocumentStoreException(DocumentStoreError.Corrupt);
        return (sources, revision, storageRevision);
    }

    internal static StorageRevision Save(
        DatabaseSession db,
        string owner,
        StorageRevision expected,
        long nextRevision,
        IReadOnlyList<StoredNovelSourceRow> previous,
        IReadOnlyList<StoredNovelSourceRow> sources)
    {
        if (db.Revision(owner) != expected)
            throw new DocumentStoreException(DocumentStoreError.Conflict);
        if (nextRevision != expected.Revision + 2)
            throw new DocumentStoreException(DocumentStoreError.Conflict);

        var prevMap = previous.Select((s, i) => (s.InstallationId, Pos: i, Source: s))
            .ToDictionary(x => x.InstallationId, x => (x.Pos, x.Source), StringComparer.Ordinal);
        var desiredIds = sources.Select(s => s.InstallationId).ToHashSet(StringComparer.Ordinal);
        foreach (var id in prevMap.Keys)
            if (!desiredIds.Contains(id))
                db.Execute("DELETE FROM windows_novel_sources WHERE installation_id=$id", ("$id", id));

        for (int i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            if (source.Definition.Kind != StorageValueKind.Object)
                throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
            bool hadOld = prevMap.TryGetValue(source.InstallationId, out var old);
            if (hadOld && old.Pos == i && RowEquals(old.Source, source))
                continue;

            db.Execute("""
                INSERT INTO windows_novel_sources(
                    installation_id,position,revision,name,source_url,source_group,sort_order,enabled,enabled_explore,protocol)
                VALUES($id,$pos,$rev,$name,$url,$grp,$ord,$enabled,$explore,$proto)
                ON CONFLICT(installation_id) DO UPDATE SET
                    position=excluded.position,
                    revision=excluded.revision,
                    name=excluded.name,
                    source_url=excluded.source_url,
                    source_group=excluded.source_group,
                    sort_order=excluded.sort_order,
                    enabled=excluded.enabled,
                    enabled_explore=excluded.enabled_explore,
                    protocol=excluded.protocol
                """,
                ("$id", source.InstallationId),
                ("$pos", i),
                ("$rev", source.Revision),
                ("$name", source.Name),
                ("$url", source.SourceUrl),
                ("$grp", source.Group),
                ("$ord", source.Order),
                ("$enabled", source.Enabled),
                ("$explore", source.EnabledExplore),
                ("$proto", source.Protocol));

            if (!hadOld || !StorageValue.StructuralEquals(old.Source.Definition, source.Definition))
                TypedValueNodes.Replace(db, "windows_novel_source_definition_nodes",
                    [("installation_id", source.InstallationId)], source.Definition, omitEmptyObject: false);
        }

        return db.CommitRevision(owner, expected);
    }

    private static bool RowEquals(StoredNovelSourceRow a, StoredNovelSourceRow b) =>
        a.InstallationId == b.InstallationId && a.Revision == b.Revision && a.Name == b.Name &&
        a.SourceUrl == b.SourceUrl && a.Group == b.Group && a.Order == b.Order &&
        a.Enabled == b.Enabled && a.EnabledExplore == b.EnabledExplore && a.Protocol == b.Protocol &&
        StorageValue.StructuralEquals(a.Definition, b.Definition);

    private static Dictionary<string, StorageValue> LoadAllDefinitions(DatabaseSession db)
    {
        var grouped = new Dictionary<string, List<TypedValueNodes.NodeRow>>(StringComparer.Ordinal);
        foreach (var item in db.Query("""
            SELECT installation_id,node_id,parent_id,prop_key,item_index,kind,bool_value,int_value,real_value,text_value
            FROM windows_novel_source_definition_nodes
            ORDER BY installation_id, node_id
            """, row => (Id: row.GetString(0), Node: TypedValueNodes.ReadRow(row, 1))))
        {
            if (!grouped.TryGetValue(item.Id, out var list))
            {
                list = [];
                grouped[item.Id] = list;
            }
            list.Add(item.Node);
        }
        var result = new Dictionary<string, StorageValue>(grouped.Count, StringComparer.Ordinal);
        foreach (var (id, rows) in grouped)
        {
            var decoded = TypedValueNodes.Decode(rows);
            if (decoded is null || decoded.Kind != StorageValueKind.Object)
                throw new DocumentStoreException(DocumentStoreError.Corrupt);
            result[id] = decoded;
        }
        return result;
    }
}
