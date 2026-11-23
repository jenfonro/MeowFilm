using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal sealed record CatProviderMapping(string Provider, IReadOnlyList<string> Aliases);

internal sealed record CatSettingsState(
    int Port,
    IReadOnlyList<(string Provider, bool Enabled)> Providers,
    IReadOnlyList<CatProviderMapping> Mappings,
    IReadOnlyList<CatSourceConfiguration> Scripts);

internal static class CatSourceSettingsStore
{
    internal const string Owner = "film.cat-sources.v1";

    internal static CatSettingsState? Read(DatabaseSession db)
    {
        bool hasRoot = db.TryQuerySingle(
            "SELECT port FROM cat_settings WHERE singleton=1",
            row => row.GetInt32(0),
            out int port);

        var providers = db.Query(
            "SELECT provider,enabled FROM cat_providers ORDER BY position",
            row => (Provider: row.GetString(0), Enabled: row.GetBool(1)));

        var mappingRows = db.Query(
            "SELECT position,provider FROM cat_mappings ORDER BY position",
            row => (Pos: row.GetInt32(0), Provider: row.GetString(1)));

        var aliasRows = db.Query(
            "SELECT mapping_position,alias FROM cat_mapping_aliases ORDER BY mapping_position, position",
            row => (MappingPos: row.GetInt32(0), Alias: row.GetString(1)));

        var scripts = db.Query("""
            SELECT installation_id,name,source_type,source_url,local_path,local_hash,enabled,input_revision
            FROM cat_scripts
            ORDER BY position
            """,
            row => new CatSourceConfiguration(
                row.GetString(0),
                row.GetString(1),
                row.GetString(2),
                row.GetNullableString(3),
                row.GetNullableString(4),
                row.GetNullableString(5),
                row.GetBool(6),
                row.GetInt64(7)));

        if (!hasRoot)
        {
            if (providers.Count > 0 || mappingRows.Count > 0 || aliasRows.Count > 0 || scripts.Count > 0)
                throw new StorageException(StorageError.Corrupt);
            return null;
        }

        var aliasesByPos = new Dictionary<int, List<string>>();
        foreach (var (mPos, alias) in aliasRows)
        {
            if (!aliasesByPos.TryGetValue(mPos, out var list))
            {
                list = [];
                aliasesByPos[mPos] = list;
            }
            list.Add(alias);
        }

        var mappings = mappingRows.Select(m => new CatProviderMapping(
            m.Provider,
            aliasesByPos.TryGetValue(m.Pos, out var list) ? list : [])).ToArray();

        return new CatSettingsState(port, providers, mappings, scripts);
    }

    internal static Task<CatSettingsState> ReadOrEmptyAsync(IStorageDatabase database, CancellationToken token) =>
        database.ReadSnapshotAsync(db => Read(db) ?? new CatSettingsState(0, [], [], []), token);

    internal static Task<(CatSettingsState? State, long Revision)> ReadSnapshotAsync(
        IStorageDatabase database, CancellationToken token) =>
        database.ReadSnapshotAsync(db => (Read(db), db.Revision(Owner).Revision), token);

    internal static Task<CatSettingsState> UpdateAsync(
        IStorageDatabase database,
        Func<CatSettingsState, CatSettingsState> update,
        CancellationToken token) =>
        database.WriteAsync(db =>
        {
            var expected = db.Revision(Owner);
            var existing = Read(db);
            var current = existing ?? new CatSettingsState(0, [], [], []);
            var next = update(current);

            db.Execute("""
                INSERT INTO cat_settings(singleton,port) VALUES(1,$port)
                ON CONFLICT(singleton) DO UPDATE SET port=excluded.port
                """, ("$port", next.Port));

            WriteProviders(db, existing?.Providers, next.Providers);
            WriteMappings(db, existing?.Mappings, next.Mappings);
            WriteScripts(db, existing?.Scripts, next.Scripts);

            db.CommitRevision(Owner, expected);
            return next;
        }, token);

    private static void WriteProviders(
        DatabaseSession db,
        IReadOnlyList<(string Provider, bool Enabled)>? previous,
        IReadOnlyList<(string Provider, bool Enabled)> next)
    {
        var prevMap = previous is null
            ? new Dictionary<string, (int Pos, bool Enabled)>(StringComparer.Ordinal)
            : previous.Select((p, i) => (p.Provider, Pos: i, p.Enabled))
                .ToDictionary(x => x.Provider, x => (x.Pos, x.Enabled), StringComparer.Ordinal);
        var nextKeys = next.Select(p => p.Provider).ToHashSet(StringComparer.Ordinal);
        foreach (var removed in prevMap.Keys)
            if (!nextKeys.Contains(removed))
                db.Execute("DELETE FROM cat_providers WHERE provider=$p", ("$p", removed));

        for (int i = 0; i < next.Count; i++)
        {
            var (provider, enabled) = next[i];
            if (prevMap.TryGetValue(provider, out var old) && old.Pos == i && old.Enabled == enabled) continue;
            db.Execute("""
                INSERT INTO cat_providers(provider,position,enabled) VALUES($p,$pos,$en)
                ON CONFLICT(provider) DO UPDATE SET position=excluded.position,enabled=excluded.enabled
                """, ("$p", provider), ("$pos", i), ("$en", enabled));
        }
    }

    private static void WriteMappings(
        DatabaseSession db,
        IReadOnlyList<CatProviderMapping>? previous,
        IReadOnlyList<CatProviderMapping> next)
    {
        if (previous is not null && previous.Count == next.Count &&
            previous.Zip(next).All(pair => pair.First.Provider == pair.Second.Provider &&
                pair.First.Aliases.SequenceEqual(pair.Second.Aliases, StringComparer.Ordinal)))
            return;

        int prevCount = previous?.Count ?? 0;
        if (prevCount > next.Count)
            db.Execute("DELETE FROM cat_mappings WHERE position>=$len", ("$len", next.Count));

        for (int i = 0; i < next.Count; i++)
        {
            var m = next[i];
            var old = previous is not null && i < previous.Count ? previous[i] : null;
            if (old is not null && old.Provider == m.Provider && old.Aliases.SequenceEqual(m.Aliases, StringComparer.Ordinal))
                continue;
            db.Execute("""
                INSERT INTO cat_mappings(position,provider) VALUES($pos,$p)
                ON CONFLICT(position) DO UPDATE SET provider=excluded.provider
                """, ("$pos", i), ("$p", m.Provider));
            int oldAliasCount = old?.Aliases.Count ?? 0;
            if (oldAliasCount > m.Aliases.Count)
                db.Execute("DELETE FROM cat_mapping_aliases WHERE mapping_position=$mp AND position>=$len",
                    ("$mp", i), ("$len", m.Aliases.Count));
            for (int j = 0; j < m.Aliases.Count; j++)
            {
                if (old is not null && j < old.Aliases.Count && old.Aliases[j] == m.Aliases[j]) continue;
                db.Execute("""
                    INSERT INTO cat_mapping_aliases(mapping_position,position,alias) VALUES($mp,$pos,$al)
                    ON CONFLICT(mapping_position,position) DO UPDATE SET alias=excluded.alias
                    """, ("$mp", i), ("$pos", j), ("$al", m.Aliases[j]));
            }
        }
    }

    private static void WriteScripts(
        DatabaseSession db,
        IReadOnlyList<CatSourceConfiguration>? previous,
        IReadOnlyList<CatSourceConfiguration> next)
    {
        var prevMap = previous is null
            ? new Dictionary<string, (int Pos, CatSourceConfiguration Item)>(StringComparer.Ordinal)
            : previous.Select((s, i) => (s.InstallationId, Pos: i, Item: s))
                .ToDictionary(x => x.InstallationId, x => (x.Pos, x.Item), StringComparer.Ordinal);
        var nextIds = next.Select(s => s.InstallationId).ToHashSet(StringComparer.Ordinal);
        foreach (var removed in prevMap.Keys)
            if (!nextIds.Contains(removed))
                db.Execute("DELETE FROM cat_scripts WHERE installation_id=$id", ("$id", removed));

        for (int i = 0; i < next.Count; i++)
        {
            var s = next[i];
            if (prevMap.TryGetValue(s.InstallationId, out var old) && old.Pos == i && old.Item == s) continue;
            db.Execute("""
                INSERT INTO cat_scripts(
                    installation_id,position,name,source_type,source_url,local_path,local_hash,enabled,input_revision)
                VALUES($id,$pos,$name,$type,$url,$path,$hash,$en,$rev)
                ON CONFLICT(installation_id) DO UPDATE SET
                    position=excluded.position,
                    name=excluded.name,
                    source_type=excluded.source_type,
                    source_url=excluded.source_url,
                    local_path=excluded.local_path,
                    local_hash=excluded.local_hash,
                    enabled=excluded.enabled,
                    input_revision=excluded.input_revision
                """,
                ("$id", s.InstallationId),
                ("$pos", i),
                ("$name", s.Name),
                ("$type", s.SourceType),
                ("$url", s.SourceUrl),
                ("$path", s.LocalPath),
                ("$hash", s.LocalHash),
                ("$en", s.Enabled),
                ("$rev", s.InputRevision));
        }
    }
}
