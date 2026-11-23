using MeowFilm.Capabilities.Film.Rules;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal static class FilmRulesStore
{
    internal const string Owner = "film.rules.v1";

    internal static (FilmRuleSet? Rules, StorageRevision Revision) Read(DatabaseSession db)
    {
        var revision = db.Revision(Owner);
        var rows = db.Query("""
            SELECT group_name,pattern,replacement,flags
            FROM film_rules
            ORDER BY group_name, position
            """,
            row => (
                Group: row.GetString(0),
                Pattern: row.GetString(1),
                Replacement: row.GetNullableString(2),
                Flags: row.GetNullableString(3)));

        if (revision == StorageRevision.Empty)
        {
            if (rows.Count > 0) throw new StorageException(StorageError.Corrupt);
            return (null, revision);
        }

        var episodeClean = new List<string>();
        var episodeRules = new List<FilmMatchRule>();
        var movieRules = new List<FilmMatchRule>();
        var aggregate = new List<string>();

        foreach (var (group, pattern, replacement, flags) in rows)
        {
            switch (group)
            {
                case "episodeCleanRegexRules":
                    episodeClean.Add(pattern);
                    break;
                case "episodeRules":
                    episodeRules.Add(new FilmMatchRule(pattern, replacement ?? "", flags ?? ""));
                    break;
                case "movieRules":
                    movieRules.Add(new FilmMatchRule(pattern, replacement ?? "", flags ?? ""));
                    break;
                case "aggregateRegexRules":
                    aggregate.Add(pattern);
                    break;
                default:
                    throw new StorageException(StorageError.Corrupt);
            }
        }

        return (new FilmRuleSet
        {
            EpisodeCleanRegexRules = episodeClean,
            EpisodeRules = episodeRules,
            MovieRules = movieRules,
            AggregateRegexRules = aggregate
        }, revision);
    }

    internal static Task<(FilmRuleSet? Rules, StorageRevision Revision)> ReadAsync(
        IStorageDatabase database, CancellationToken token) =>
        database.ReadSnapshotAsync(Read, token);

    internal static Task<StorageRevision> SaveAsync(
        IStorageDatabase database,
        FilmRuleSet rules,
        StorageRevision expected,
        CancellationToken token) =>
        database.WriteAsync(db =>
        {
            if (db.Revision(Owner) != expected)
                throw new StorageException(StorageError.Conflict);

            static void UpsertRule(DatabaseSession session, string group, int pos, string pat, string? rep, string? flags)
            {
                session.Execute("""
                    INSERT INTO film_rules(group_name,position,pattern,replacement,flags)
                    VALUES($g,$pos,$pat,$rep,$flags)
                    ON CONFLICT(group_name,position) DO UPDATE SET
                        pattern=excluded.pattern,
                        replacement=excluded.replacement,
                        flags=excluded.flags
                    """, ("$g", group), ("$pos", pos), ("$pat", pat), ("$rep", rep), ("$flags", flags));
            }

            static void TrimGroup(DatabaseSession session, string group, int count) =>
                session.Execute("DELETE FROM film_rules WHERE group_name=$g AND position>=$len",
                    ("$g", group), ("$len", count));

            for (int i = 0; i < rules.EpisodeCleanRegexRules.Count; i++)
                UpsertRule(db, "episodeCleanRegexRules", i, rules.EpisodeCleanRegexRules[i], null, null);
            TrimGroup(db, "episodeCleanRegexRules", rules.EpisodeCleanRegexRules.Count);

            for (int i = 0; i < rules.EpisodeRules.Count; i++)
            {
                var r = rules.EpisodeRules[i];
                UpsertRule(db, "episodeRules", i, r.Pattern, r.Replace ?? "", r.Flags ?? "");
            }
            TrimGroup(db, "episodeRules", rules.EpisodeRules.Count);

            for (int i = 0; i < rules.MovieRules.Count; i++)
            {
                var r = rules.MovieRules[i];
                UpsertRule(db, "movieRules", i, r.Pattern, r.Replace ?? "", r.Flags ?? "");
            }
            TrimGroup(db, "movieRules", rules.MovieRules.Count);

            for (int i = 0; i < rules.AggregateRegexRules.Count; i++)
                UpsertRule(db, "aggregateRegexRules", i, rules.AggregateRegexRules[i], null, null);
            TrimGroup(db, "aggregateRegexRules", rules.AggregateRegexRules.Count);

            db.Execute("UPDATE film_rule_groups SET overridden=1,row_revision=row_revision+1");

            return db.CommitRevision(Owner, expected);
        }, token);
}
