using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Feedback;

internal sealed record NeteasePlaybackAccount(string Scope, long Generation, string UserId)
{
    internal static NeteasePlaybackAccount From(MusicPlatformSession account) =>
        new(account.Scope, account.Generation, account.UserId);
    internal bool Matches(MusicPlatformSession account) =>
        account.Platform == "wy" && Scope == account.Scope && Generation == account.Generation && UserId == account.UserId;
}

internal sealed record NeteasePlaybackEvent(Guid Id, long At, bool Inflight,
    double Time, double Realtime, string Reason, double Rate = 1);

// A confirmed start with no pending event is kept only while its live session
// can still supply an end. Such an empty pairing marker is never persisted.
internal sealed record NeteasePlaybackPending(Guid PlaybackId, NeteasePlaybackAccount Account, long ConsentRevision,
    string RecordingId, string? Source, string? SourceId, string? Algorithm,
    bool StartConfirmed, NeteasePlaybackEvent? Start, NeteasePlaybackEvent? End, string? PlayScene = null)
{
    internal int Count => (Start is null ? 0 : 1) + (End is null ? 0 : 1);
    internal long Oldest => Math.Min(Start?.At ?? long.MaxValue, End?.At ?? long.MaxValue);
}

internal static class NeteasePlaybackOutboxStore
{
    internal const string Namespace = "music.playback-outbox.v2";
    // Retention is a privacy boundary, not minimum listening time,
    // a counting threshold or a retry allowance.
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    internal static Task<(NeteasePlaybackPending[] Entries, StorageRevision Revision)> ReadAsync(
        IStorageDatabase database, CancellationToken token = default) =>
        database.ReadSnapshotAsync(db =>
        {
            var revision = db.Revision(Namespace);
            var rows = db.Query("""
                SELECT playback_id,consent_revision,recording_id,source,source_id,algorithm,
                       start_confirmed,play_scene,
                       account_scope,account_generation,account_user_id,
                       start_event_id,start_at,start_inflight,start_time,start_realtime,start_reason,start_rate,
                       end_event_id,end_at,end_inflight,end_time,end_realtime,end_reason,end_rate
                FROM windows_music_feedback_outbox
                ORDER BY position
                """,
                row =>
                {
                    FeedbackProtocolRules.Require(Guid.TryParse(row.GetString(0), out var playbackId), "CORRUPT_STORAGE");
                    var account = new NeteasePlaybackAccount(row.GetString(8), row.GetInt64(9), row.GetString(10));
                    var start = ReadEvent(row, 11);
                    var end = ReadEvent(row, 18);
                    return new NeteasePlaybackPending(
                        playbackId,
                        account,
                        row.GetInt64(1),
                        row.GetString(2),
                        row.GetNullableString(3),
                        row.GetNullableString(4),
                        row.GetNullableString(5),
                        row.GetBool(6),
                        start,
                        end,
                        row.GetNullableString(7));
                }).ToArray();

            if (revision == StorageRevision.Empty && rows.Length > 0)
                throw new MusicFeedbackException("CORRUPT_STORAGE");

            FeedbackProtocolRules.Require(rows.Select(e => e.PlaybackId).Distinct().Count() == rows.Length, "CORRUPT_STORAGE");
            foreach (var entry in rows) Validate(entry);
            var eventIds = rows.SelectMany(e => new[] { e.Start?.Id, e.End?.Id }).OfType<Guid>().ToArray();
            FeedbackProtocolRules.Require(eventIds.Distinct().Count() == eventIds.Length, "CORRUPT_STORAGE");
            return (rows, revision);
        }, token);

    internal static Task<StorageRevision> SaveAsync(
        IStorageDatabase database,
        IEnumerable<NeteasePlaybackPending> entries,
        StorageRevision expected,
        CancellationToken token = default)
    {
        var list = entries.Where(e => e.Count > 0).ToArray();
        return database.WriteAsync(db =>
        {
            if (db.Revision(Namespace) != expected)
                throw new DocumentStoreException(DocumentStoreError.Conflict);

            var existingIds = db.Query("SELECT playback_id FROM windows_music_feedback_outbox", row => row.GetString(0));
            var desiredIds = list.Select(e => e.PlaybackId.ToString("D")).ToHashSet(StringComparer.Ordinal);
            foreach (var id in existingIds)
                if (!desiredIds.Contains(id))
                    db.Execute("DELETE FROM windows_music_feedback_outbox WHERE playback_id=$pid", ("$pid", id));

            for (int i = 0; i < list.Length; i++)
            {
                var e = list[i];
                db.Execute("""
                    INSERT INTO windows_music_feedback_outbox(
                        playback_id,position,consent_revision,recording_id,source,source_id,algorithm,
                        start_confirmed,play_scene,
                        account_scope,account_generation,account_user_id,
                        start_event_id,start_at,start_inflight,start_time,start_realtime,start_reason,start_rate,
                        end_event_id,end_at,end_inflight,end_time,end_realtime,end_reason,end_rate)
                    VALUES(
                        $pid,$pos,$crev,$rid,$src,$srcId,$alg,
                        $sconf,$scene,
                        $ascope,$agen,$auid,
                        $seid,$sat,$sinf,$stime,$sreal,$sres,$srate,
                        $eeid,$eat,$einf,$etime,$ereal,$eres,$erate)
                    ON CONFLICT(playback_id) DO UPDATE SET
                        position=excluded.position,
                        consent_revision=excluded.consent_revision,
                        recording_id=excluded.recording_id,
                        source=excluded.source,
                        source_id=excluded.source_id,
                        algorithm=excluded.algorithm,
                        start_confirmed=excluded.start_confirmed,
                        play_scene=excluded.play_scene,
                        account_scope=excluded.account_scope,
                        account_generation=excluded.account_generation,
                        account_user_id=excluded.account_user_id,
                        start_event_id=excluded.start_event_id,
                        start_at=excluded.start_at,
                        start_inflight=excluded.start_inflight,
                        start_time=excluded.start_time,
                        start_realtime=excluded.start_realtime,
                        start_reason=excluded.start_reason,
                        start_rate=excluded.start_rate,
                        end_event_id=excluded.end_event_id,
                        end_at=excluded.end_at,
                        end_inflight=excluded.end_inflight,
                        end_time=excluded.end_time,
                        end_realtime=excluded.end_realtime,
                        end_reason=excluded.end_reason,
                        end_rate=excluded.end_rate
                    """,
                    ("$pid", e.PlaybackId.ToString("D")),
                    ("$pos", i),
                    ("$crev", e.ConsentRevision),
                    ("$rid", e.RecordingId),
                    ("$src", e.Source),
                    ("$srcId", e.SourceId),
                    ("$alg", e.Algorithm),
                    ("$sconf", e.StartConfirmed),
                    ("$scene", e.PlayScene),
                    ("$ascope", e.Account.Scope),
                    ("$agen", e.Account.Generation),
                    ("$auid", e.Account.UserId),
                    ("$seid", e.Start?.Id.ToString("D")),
                    ("$sat", e.Start?.At),
                    ("$sinf", e.Start?.Inflight),
                    ("$stime", e.Start?.Time),
                    ("$sreal", e.Start?.Realtime),
                    ("$sres", e.Start?.Reason),
                    ("$srate", e.Start?.Rate),
                    ("$eeid", e.End?.Id.ToString("D")),
                    ("$eat", e.End?.At),
                    ("$einf", e.End?.Inflight),
                    ("$etime", e.End?.Time),
                    ("$ereal", e.End?.Realtime),
                    ("$eres", e.End?.Reason),
                    ("$erate", e.End?.Rate));
            }

            return db.CommitRevision(Namespace, expected, Guid.NewGuid().ToString("D"));
        }, token);
    }

    private static NeteasePlaybackEvent? ReadEvent(Microsoft.Data.Sqlite.SqliteDataReader row, int offset)
    {
        string? rawId = row.GetNullableString(offset);
        long? at = row.GetNullableInt64(offset + 1);
        bool? inflight = row.GetNullableBool(offset + 2);
        double? time = row.GetNullableDouble(offset + 3);
        double? realtime = row.GetNullableDouble(offset + 4);
        string? reason = row.GetNullableString(offset + 5);
        double? rate = row.GetNullableDouble(offset + 6);

        if (rawId is null && at is null && inflight is null && time is null && realtime is null && reason is null && rate is null)
            return null;

        Guid id = Guid.Empty;
        FeedbackProtocolRules.Require(
            rawId is not null && Guid.TryParse(rawId, out id) &&
            at is not null && inflight is not null && time is not null &&
            realtime is not null && reason is not null && rate is not null,
            "CORRUPT_STORAGE");

        return new NeteasePlaybackEvent(id, at!.Value, inflight!.Value, time!.Value, realtime!.Value, reason!, rate!.Value);
    }

    private static void Validate(NeteasePlaybackPending entry)
    {
        FeedbackProtocolRules.Require(entry is not null && entry.PlaybackId != Guid.Empty && entry.Account is not null &&
            entry.ConsentRevision >= 0 &&
            FeedbackProtocolRules.Text(entry.Account.Scope) && entry.Account.Generation >= 0 &&
            FeedbackProtocolRules.Id(entry.Account.UserId) && FeedbackProtocolRules.Id(entry.RecordingId) &&
            entry.Count > 0 && (entry.Start is null ? entry.StartConfirmed && entry.End is not null : !entry.StartConfirmed) &&
            (entry.Source is null || FeedbackProtocolRules.Text(entry.Source)) &&
            (entry.SourceId is null || entry.Source is not null && FeedbackProtocolRules.Text(entry.SourceId, true)) &&
            (entry.Algorithm is null || FeedbackProtocolRules.Text(entry.Algorithm)) &&
            (entry.PlayScene is null or "heartbeat"), "CORRUPT_STORAGE");
        if (entry.Start is { } start)
            FeedbackProtocolRules.Require(Valid(start) && start.Time == 0 && start.Realtime == 0 &&
                start.Reason.Length == 0, "CORRUPT_STORAGE");
        if (entry.End is { } end)
            FeedbackProtocolRules.Require(Valid(end) && end.Reason is "playend" or "ui" or "interrupt" or "exception",
                "CORRUPT_STORAGE");
    }

    private static bool Valid(NeteasePlaybackEvent value) => value.Id != Guid.Empty &&
        value.At >= DateTimeOffset.UnixEpoch.ToUnixTimeMilliseconds() &&
        value.At <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() &&
        double.IsFinite(value.Time) && value.Time >= 0 && double.IsFinite(value.Realtime) && value.Realtime >= 0 &&
        double.IsFinite(value.Rate) && value.Rate > 0 &&
        value.Reason is not null;
}
