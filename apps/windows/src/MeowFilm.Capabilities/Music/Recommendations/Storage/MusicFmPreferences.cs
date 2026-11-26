using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Recommendations;

internal readonly record struct MusicFmSelection(string Mode, string Submode, string RuleId, string TabTagId)
{
    internal static MusicFmSelection Default => new("default", "", "", "");
}

/// <summary>Device-only choices read and update one actual account row.</summary>
internal sealed class MusicFmPreferences(IStorageDatabase database)
{
    internal Task<MusicFmSelection> ReadAsync(string accountId, CancellationToken token) => database.ReadAsync(db =>
        db.TryQuerySingle("""
            SELECT mode,submode,rule_id,tab_tag_id
            FROM windows_music_fm_preferences
            WHERE account_id=$id
            """,
            row => new MusicFmSelection(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3)),
            out var value,
            ("$id", accountId))
            ? value : MusicFmSelection.Default, token);

    internal async Task SaveAsync(string accountId, MusicFmSelection selection, Func<bool> current, CancellationToken token)
    {
        RecommendationJson.Require(selection.Mode is "default" or "familiar" or "explore" or "puzzle" or "scene" &&
            selection.Submode is not null && selection.RuleId is not null && selection.TabTagId is not null,
            "CORRUPT_STORAGE");
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
        }, token, commit: commit => { if (!current()) throw new OperationCanceledException(token); commit(); }).ConfigureAwait(false);
    }
}
