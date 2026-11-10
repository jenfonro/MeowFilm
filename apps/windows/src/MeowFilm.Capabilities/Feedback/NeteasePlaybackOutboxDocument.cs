using System.Text.Json;
using System.Text.Json.Serialization;
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

internal static class NeteasePlaybackOutboxDocument
{
    internal const string Namespace = "music.playback-outbox.v1";
    // Retention/capacity/byte bounds are local resource and privacy boundaries,
    // not minimum listening time, counting thresholds or a retry allowance.
    internal const int MaximumEvents = 256;
    internal const int MaximumBytes = 4 * 1024 * 1024;
    internal const int MaximumEventBytes = 32 * 1024;
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private sealed record Document(int Version, NeteasePlaybackPending[] Playbacks);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8
    };

    internal static byte[] Encode(IEnumerable<NeteasePlaybackPending> entries) =>
        JsonSerializer.SerializeToUtf8Bytes(new Document(1, entries.Where(e => e.Count > 0).ToArray()), Json);

    internal static NeteasePlaybackPending[] Decode(DocumentSnapshot snapshot)
    {
        if (snapshot.Data is null)
        {
            FeedbackProtocolRules.Require(snapshot.Fingerprint == DocumentFingerprint.Missing, "CORRUPT_STORAGE");
            return [];
        }
        try
        {
            FeedbackProtocolRules.Require(snapshot.Data.Length <= MaximumBytes &&
                snapshot.Fingerprint == DocumentFingerprint.FromBytes(snapshot.Data), "CORRUPT_STORAGE");
            using var syntax = JsonDocument.Parse(snapshot.Data, new() { MaxDepth = 8 });
            UniqueProperties(syntax.RootElement);
            var document = JsonSerializer.Deserialize<Document>(snapshot.Data, Json);
            FeedbackProtocolRules.Require(document is { Version: 1, Playbacks: not null }, "CORRUPT_STORAGE");
            var entries = document.Playbacks;
            FeedbackProtocolRules.Require(entries.Length <= MaximumEvents &&
                entries.Sum(e => e.Count) <= MaximumEvents &&
                entries.Select(e => e.PlaybackId).Distinct().Count() == entries.Length, "CORRUPT_STORAGE");
            foreach (var entry in entries) Validate(entry);
            var eventIds = entries.SelectMany(e => new[] { e.Start?.Id, e.End?.Id })
                .OfType<Guid>().ToArray();
            FeedbackProtocolRules.Require(eventIds.Distinct().Count() == eventIds.Length, "CORRUPT_STORAGE");
            return entries;
        }
        catch (MusicFeedbackException) { throw; }
        catch { throw new MusicFeedbackException("CORRUPT_STORAGE"); }
    }

    private static void Validate(NeteasePlaybackPending entry)
    {
        FeedbackProtocolRules.Require(entry is not null && entry.PlaybackId != Guid.Empty && entry.Account is not null &&
            entry.ConsentRevision >= 0 &&
            FeedbackProtocolRules.Text(entry.Account.Scope, 256) && entry.Account.Generation >= 0 &&
            FeedbackProtocolRules.Id(entry.Account.UserId) && FeedbackProtocolRules.Id(entry.RecordingId) &&
            entry.Count > 0 && (entry.Start is null ? entry.StartConfirmed && entry.End is not null : !entry.StartConfirmed) &&
            (entry.Source is null || FeedbackProtocolRules.Text(entry.Source, MaximumEventBytes)) &&
            (entry.SourceId is null || entry.Source is not null && FeedbackProtocolRules.Text(entry.SourceId, MaximumEventBytes, true)) &&
            (entry.Algorithm is null || FeedbackProtocolRules.Text(entry.Algorithm, MaximumEventBytes)) &&
            (entry.PlayScene is null or "heartbeat"), "CORRUPT_STORAGE");
        if (entry.Start is { } start)
            FeedbackProtocolRules.Require(Valid(start) && start.Time == 0 && start.Realtime == 0 &&
                start.Reason.Length == 0, "CORRUPT_STORAGE");
        if (entry.End is { } end)
            FeedbackProtocolRules.Require(Valid(end) && end.Reason is "playend" or "ui" or "interrupt" or "exception",
                "CORRUPT_STORAGE");
        FeedbackProtocolRules.Require(JsonSerializer.SerializeToUtf8Bytes(entry, Json).Length <= MaximumEventBytes,
            "CORRUPT_STORAGE");
    }

    private static bool Valid(NeteasePlaybackEvent value) => value.Id != Guid.Empty &&
        value.At >= DateTimeOffset.UnixEpoch.ToUnixTimeMilliseconds() &&
        value.At <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() &&
        double.IsFinite(value.Time) && value.Time >= 0 && double.IsFinite(value.Realtime) && value.Realtime >= 0 &&
        double.IsFinite(value.Rate) && value.Rate > 0 &&
        value.Reason is not null;

    private static void UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                FeedbackProtocolRules.Require(names.Add(property.Name), "CORRUPT_STORAGE");
                UniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) UniqueProperties(item);
    }
}
