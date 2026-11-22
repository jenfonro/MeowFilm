using System.Text.Json;
using MeowFilm.Capabilities.Film.Rules;
using MeowFilm.Platform.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal static class FilmRulesStore
{
    internal const string Owner = "film.rules.v1";

    private readonly record struct RuleRow(string Pattern, string? Replacement, string? Flags);

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
            if (rows.Count > 0) throw new DocumentStoreException(DocumentStoreError.Corrupt);
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
                    throw new DocumentStoreException(DocumentStoreError.Corrupt);
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
                throw new DocumentStoreException(DocumentStoreError.Conflict);

            var currentByGroup = db.Query("""
                SELECT group_name,pattern,replacement,flags
                FROM film_rules
                ORDER BY group_name, position
                """, r => (Group: r.GetString(0), Row: new RuleRow(r.GetString(1), r.GetNullableString(2), r.GetNullableString(3))))
                .GroupBy(x => x.Group, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Row).ToList(), StringComparer.Ordinal);

            ReconcileGroup(db, "episodeCleanRegexRules",
                currentByGroup.GetValueOrDefault("episodeCleanRegexRules"),
                rules.EpisodeCleanRegexRules.Select(p => new RuleRow(p, null, null)).ToList());
            ReconcileGroup(db, "episodeRules",
                currentByGroup.GetValueOrDefault("episodeRules"),
                rules.EpisodeRules.Select(r => new RuleRow(r.Pattern, r.Replace ?? "", r.Flags ?? "")).ToList());
            ReconcileGroup(db, "movieRules",
                currentByGroup.GetValueOrDefault("movieRules"),
                rules.MovieRules.Select(r => new RuleRow(r.Pattern, r.Replace ?? "", r.Flags ?? "")).ToList());
            ReconcileGroup(db, "aggregateRegexRules",
                currentByGroup.GetValueOrDefault("aggregateRegexRules"),
                rules.AggregateRegexRules.Select(p => new RuleRow(p, null, null)).ToList());

            return db.CommitRevision(Owner, expected, Guid.NewGuid().ToString("D"));
        }, token);

    private static void ReconcileGroup(DatabaseSession db, string groupName, List<RuleRow>? previous, List<RuleRow> next)
    {
        int prevCount = previous?.Count ?? 0;
        bool same = prevCount == next.Count;
        if (same && previous is not null)
        {
            for (int i = 0; i < next.Count; i++)
                if (previous[i] != next[i]) { same = false; break; }
        }
        if (same)
        {
            db.Execute("UPDATE film_rule_groups SET overridden=1 WHERE group_name=$grp AND overridden=0", ("$grp", groupName));
            return;
        }

        if (prevCount > next.Count)
            db.Execute("DELETE FROM film_rules WHERE group_name=$grp AND position>=$len",
                ("$grp", groupName), ("$len", next.Count));

        for (int i = 0; i < next.Count; i++)
        {
            if (previous is not null && i < previous.Count && previous[i] == next[i]) continue;
            var r = next[i];
            db.Execute("""
                INSERT INTO film_rules(group_name,position,pattern,replacement,flags)
                VALUES($grp,$pos,$pat,$rep,$flags)
                ON CONFLICT(group_name,position) DO UPDATE SET
                    pattern=excluded.pattern,
                    replacement=excluded.replacement,
                    flags=excluded.flags
                """, ("$grp", groupName), ("$pos", i), ("$pat", r.Pattern), ("$rep", r.Replacement), ("$flags", r.Flags));
        }

        db.Execute("UPDATE film_rule_groups SET overridden=1,row_revision=row_revision+1 WHERE group_name=$grp",
            ("$grp", groupName));
    }

    internal static Task<RuntimeSettingsSnapshot> ReadRuntimeSnapshotAsync(
        IStorageDatabase database, CancellationToken token) =>
        database.ReadSnapshotAsync(db =>
        {
            var (rules, revision) = Read(db);
            if (rules is null) return new RuntimeSettingsSnapshot(null, revision.Revision);
            var doc = JsonSerializer.SerializeToElement(new { version = 1, rules }, FilmRuleService.Json);
            return new RuntimeSettingsSnapshot(doc, revision.Revision);
        }, token);
}
