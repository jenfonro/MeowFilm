using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal static class NovelSourceStore
{
    private const long MaximumRevision = 9007199254740991;

    internal static (NovelSourceConfigurationSnapshot Snapshot, StorageRevision StorageRevision) Load(DatabaseSession db, string owner)
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
                return new NovelSourceDefinition(id, sourceRev, name, url, group, order, enabled, enabledExplore, protocol,
                    definitionNode.ToJsonString());
            }).ToArray();

        if (storageRevision == StorageRevision.Empty)
        {
            if (sources.Length > 0) throw new DocumentStoreException(DocumentStoreError.Corrupt);
            return (new NovelSourceConfigurationSnapshot(1, 1, []), storageRevision);
        }
        long revision = checked(storageRevision.Revision + 1);
        if (revision is < 1 or > MaximumRevision)
            throw new DocumentStoreException(DocumentStoreError.Corrupt);
        return (new NovelSourceConfigurationSnapshot(1, revision, sources), storageRevision);
    }

    internal static StorageRevision Save(DatabaseSession db, string owner, StorageRevision expected,
        long nextRevision, IReadOnlyList<NovelSourceDefinition> sources)
    {
        if (db.Revision(owner) != expected)
            throw new DocumentStoreException(DocumentStoreError.Conflict);
        if (nextRevision != expected.Revision + 2)
            throw new DocumentStoreException(DocumentStoreError.Conflict);

        var (existingSnapshot, _) = Load(db, owner);
        var prevMap = existingSnapshot.Sources.Select((s, i) => (s.InstallationId, Pos: i, Source: s))
            .ToDictionary(x => x.InstallationId, x => (x.Pos, x.Source), StringComparer.Ordinal);
        var desiredIds = sources.Select(s => s.InstallationId).ToHashSet(StringComparer.Ordinal);
        foreach (var id in prevMap.Keys)
            if (!desiredIds.Contains(id))
                db.Execute("DELETE FROM windows_novel_sources WHERE installation_id=$id", ("$id", id));

        for (int i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            if (prevMap.TryGetValue(source.InstallationId, out var old) && old.Pos == i && old.Source == source)
                continue;
            var definitionObj = JsonNode.Parse(source.Definition) as JsonObject
                ?? throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
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
            if (old.Source is null || old.Source.Definition != source.Definition)
                TypedValueNodes.Replace(db, "windows_novel_source_definition_nodes",
                    [("installation_id", source.InstallationId)], definitionObj, omitEmptyObject: false);
        }

        return db.CommitRevision(owner, expected, Guid.NewGuid().ToString("D"));
    }

    private static Dictionary<string, JsonObject> LoadAllDefinitions(DatabaseSession db)
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
        var result = new Dictionary<string, JsonObject>(grouped.Count, StringComparer.Ordinal);
        foreach (var (id, rows) in grouped)
            result[id] = TypedValueNodes.Decode(rows) as JsonObject
                ?? throw new DocumentStoreException(DocumentStoreError.Corrupt);
        return result;
    }
}
