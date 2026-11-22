using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal static class FilmPreferencesStore
{
    internal const string Owner = "film.preferences.v1";

    internal static FilmPreferences? Read(DatabaseSession db)
    {
        bool hasRoot = db.TryQuerySingle(
            "SELECT ios_auto_prepare,metadata_view_provider FROM film_preferences WHERE singleton=1",
            row => (IosAutoPrepare: row.GetBool(0), Provider: row.GetString(1)),
            out var root);

        var items = db.Query(
            "SELECT list_kind,item_value FROM film_preference_items ORDER BY list_kind, position",
            row => (Kind: row.GetString(0), Value: row.GetString(1)));

        if (!hasRoot)
        {
            if (items.Count > 0) throw new DocumentStoreException(DocumentStoreError.Corrupt);
            return null;
        }

        if (root.Provider is not ("tmdb" or "douban"))
            throw new DocumentStoreException(DocumentStoreError.Corrupt);

        var ruleOrder = new List<string>();
        var panOrder = new List<string>();
        var keywords = new List<string>();
        var siteCleanKeywords = new List<string>();

        foreach (var (kind, value) in items)
        {
            switch (kind)
            {
                case "ruleOrder": ruleOrder.Add(value); break;
                case "panOrder": panOrder.Add(value); break;
                case "keywords": keywords.Add(value); break;
                case "siteCleanKeywords": siteCleanKeywords.Add(value); break;
                default: throw new DocumentStoreException(DocumentStoreError.Corrupt);
            }
        }

        return new FilmPreferences(
            ruleOrder.ToArray(),
            panOrder.ToArray(),
            keywords.ToArray(),
            siteCleanKeywords.ToArray(),
            root.IosAutoPrepare,
            root.Provider);
    }

    internal static Task<FilmPreferences> ReadAsync(IStorageDatabase database, CancellationToken token) =>
        database.ReadSnapshotAsync(db => Read(db) ?? FilmPreferences.Defaults, token);

    internal static Task<(FilmPreferences? Preferences, long Revision)> ReadSnapshotAsync(
        IStorageDatabase database, CancellationToken token) =>
        database.ReadSnapshotAsync(db => (Read(db), db.Revision(Owner).Revision), token);

    internal static Task<FilmPreferences> UpdateAsync(
        IStorageDatabase database,
        Func<FilmPreferences, FilmPreferences> update,
        CancellationToken token) =>
        database.WriteAsync(db =>
        {
            var expected = db.Revision(Owner);
            var current = Read(db);
            var baseline = current ?? FilmPreferences.Defaults;
            var next = update(baseline);

            db.Execute("""
                INSERT INTO film_preferences(singleton,ios_auto_prepare,metadata_view_provider,row_revision)
                VALUES(1,$ios,$provider,1)
                ON CONFLICT(singleton) DO UPDATE SET
                    ios_auto_prepare=excluded.ios_auto_prepare,
                    metadata_view_provider=excluded.metadata_view_provider,
                    row_revision=film_preferences.row_revision+1
                """,
                ("$ios", next.IosAutoPrepare),
                ("$provider", next.MetadataViewProvider));

            WriteList(db, "ruleOrder", current?.RuleOrder, next.RuleOrder);
            WriteList(db, "panOrder", current?.PanOrder, next.PanOrder);
            WriteList(db, "keywords", current?.Keywords, next.Keywords);
            WriteList(db, "siteCleanKeywords", current?.SiteCleanKeywords, next.SiteCleanKeywords);

            db.CommitRevision(Owner, expected);
            return next;
        }, token);

    private static void WriteList(DatabaseSession db, string kind, string[]? previous, string[] next)
    {
        if (previous is not null && previous.AsSpan().SequenceEqual(next)) return;
        int prevLen = previous?.Length ?? 0;
        if (prevLen > next.Length)
            db.Execute("DELETE FROM film_preference_items WHERE list_kind=$kind AND position>=$len",
                ("$kind", kind), ("$len", next.Length));
        for (int i = 0; i < next.Length; i++)
        {
            if (previous is not null && i < previous.Length && string.Equals(previous[i], next[i], StringComparison.Ordinal))
                continue;
            db.Execute("""
                INSERT INTO film_preference_items(list_kind,position,item_value) VALUES($kind,$pos,$val)
                ON CONFLICT(list_kind,position) DO UPDATE SET item_value=excluded.item_value
                """, ("$kind", kind), ("$pos", i), ("$val", next[i]));
        }
    }
}
