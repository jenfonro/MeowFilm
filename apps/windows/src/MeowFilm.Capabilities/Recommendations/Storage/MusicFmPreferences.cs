using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Recommendations;

internal readonly record struct MusicFmSelection(string Mode, string Submode, string RuleId, string TabTagId)
{
    internal static MusicFmSelection Default => new("default", "", "", "");
}

/// <summary>Device-only choices read and update one actual account row.</summary>
internal sealed class MusicFmPreferences(IStorageDatabase database)
{
    internal const string DocumentNamespace = "music.fm-selection.v1";

    internal Task<MusicFmSelection> ReadAsync(string accountId, CancellationToken token) => database.ReadAsync(db =>
    {
        if (!db.TryQuerySingle("""
            SELECT mode,submode,rule_id,tab_tag_id
            FROM windows_music_fm_preferences
            WHERE account_id=$id
            """,
            row => (
                Mode: row.GetNullableString(0),
                Submode: row.GetNullableString(1),
                RuleId: row.GetNullableString(2),
                TabTagId: row.GetNullableString(3)),
            out var value,
            ("$id", accountId)))
        {
            return MusicFmSelection.Default;
        }
        Validate(value.Mode, value.Submode, value.RuleId, value.TabTagId);
        return new MusicFmSelection(value.Mode!, value.Submode!, value.RuleId!, value.TabTagId!);
    }, token);

    internal async Task SaveAsync(string accountId, MusicFmSelection selection, Func<bool> current, CancellationToken token)
    {
        Validate(selection.Mode, selection.Submode, selection.RuleId, selection.TabTagId);
        if (!current()) return;
        await database.WriteAsync(db =>
        {
            if (!current()) return false;
            db.Execute("""
                INSERT INTO windows_music_fm_preferences(account_id,mode,submode,rule_id,tab_tag_id)
                VALUES($id,$mode,$submode,$ruleId,$tabTagId)
                ON CONFLICT(account_id) DO UPDATE SET
                    mode=excluded.mode,
                    submode=excluded.submode,
                    rule_id=excluded.rule_id,
                    tab_tag_id=excluded.tab_tag_id
                """,
                ("$id", accountId),
                ("$mode", selection.Mode),
                ("$submode", selection.Submode),
                ("$ruleId", selection.RuleId),
                ("$tabTagId", selection.TabTagId));
            return true;
        }, token, 256, commit: commit => { if (!current()) throw new OperationCanceledException(token); commit(); }).ConfigureAwait(false);
    }

    private static void Validate(string? mode, string? submode, string? ruleId, string? tabTagId)
    {
        RecommendationJson.Require(mode is "default" or "familiar" or "explore" or "puzzle" or "scene",
            "CORRUPT_STORAGE");
        RecommendationJson.Require(submode is not null && ruleId is not null && tabTagId is not null,
            "CORRUPT_STORAGE");
    }
}
