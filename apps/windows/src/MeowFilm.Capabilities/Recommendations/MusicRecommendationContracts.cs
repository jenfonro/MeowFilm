using System.Text.Json;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Recommendations;

public enum MusicRecommendationQueueChange { Selection, PlayNext, Append, Remove, Reorder, Replace, Clear, Restore, Mode, Reset }
public enum MusicRecommendationPlaybackAction { Select, Stop }

/// <summary>Only actual local media observations. A queue selection is not a Started event.</summary>
public sealed record MusicRecommendationPlaybackState(string EntryId, bool IsPlaying,
    bool HasStarted, bool MediaReady, bool IsLocal = true, long PlaybackEvent = 0);

/// <summary>Ephemeral provenance of an entry actually admitted by this live coordinator.</summary>
public sealed record MusicRecommendationFeedback(string SessionId, string BatchId, string EntryId,
    string Platform, string Kind, string Provider, string SourceType, string SourceId,
    string Algorithm, string Mode, string Submode, bool Automatic, bool FromHome,
    string FeedbackSource = "");

public sealed class MusicRecommendationSelection
{
    private readonly Func<bool> current;
    internal MusicRecommendationSelection(MusicRecommendationPlaybackAction action, string sessionId,
        string entryId, long queueVersion, bool play, bool automatic, bool restoreOnly,
        MusicRecommendationFeedback? feedback, Func<bool> current)
    {
        Action = action; SessionId = sessionId; EntryId = entryId; QueueVersion = queueVersion;
        Play = play; Automatic = automatic; RestoreOnly = restoreOnly; Feedback = feedback; this.current = current;
    }
    public MusicRecommendationPlaybackAction Action { get; }
    public string SessionId { get; }
    public string EntryId { get; }
    public long QueueVersion { get; }
    public bool Play { get; }
    public bool Automatic { get; }
    public bool RestoreOnly { get; }
    public MusicRecommendationFeedback? Feedback { get; }
    /// <summary>Recheck before playback admission and after asynchronous resolution. Never serialize.</summary>
    public bool IsCurrent() => current();
}

/// <summary>Typed read request; a host can wrap ReadAsync with its existing account lease frame.</summary>
public sealed class MusicRecommendationRequest
{
    internal MusicRecommendationRequest(MusicFmRequest value) { Fm = value; Platform = value.Platform; Method = "fm"; }
    internal MusicRecommendationRequest(MusicSimilarRequest value) { Similar = value; Platform = value.Platform; Method = "similar"; }
    internal MusicRecommendationRequest(MusicHeartbeatRequest value) { Heartbeat = value; Platform = value.Platform; Method = "heartbeat"; }
    internal MusicRecommendationRequest(MusicQueueRecommendationsRequest value)
    { QueueRecommendations = value; Platform = value.Platform; Method = "queue-recommendations"; }
    public string Platform { get; }
    public string Method { get; }
    public MusicFmRequest? Fm { get; }
    public MusicSimilarRequest? Similar { get; }
    public MusicHeartbeatRequest? Heartbeat { get; }
    public MusicQueueRecommendationsRequest? QueueRecommendations { get; }
    public Task<JsonElement> ReadAsync(IMusicPlatformCatalog catalog, CancellationToken cancellationToken = default) =>
        Method switch
        {
            "fm" => catalog.FmAsync(Fm!, cancellationToken),
            "similar" => catalog.SimilarAsync(Similar!, cancellationToken),
            "heartbeat" => catalog.HeartbeatAsync(Heartbeat!, cancellationToken),
            _ => catalog.QueueRecommendationsAsync(QueueRecommendations!, cancellationToken)
        };
}

internal sealed class RecommendationException(string code) : Exception(code)
{ internal string Code { get; } = code; }

internal static class RecommendationJson
{
    internal static JsonElement Json(object? value) => value is JsonElement e ? e.Clone() : JsonSerializer.SerializeToElement(value);
    internal static void Require(bool condition, string code = "INVALID_REQUEST")
    { if (!condition) throw new RecommendationException(code); }
    internal static void Fields(JsonElement p, string allowed, string required = "")
    {
        Require(p.ValueKind == JsonValueKind.Object);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in p.EnumerateObject())
            Require(names.Add(property.Name) && allowed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(property.Name));
        foreach (string field in required.Split(' ', StringSplitOptions.RemoveEmptyEntries)) Require(names.Contains(field));
    }
    internal static string Text(JsonElement p, string key, string fallback = "", int max = 512)
    {
        if (!p.TryGetProperty(key, out var v)) return fallback;
        Require(v.ValueKind == JsonValueKind.String);
        string value = v.GetString()!;
        Require(value.Length <= max && !value.Any(char.IsControl));
        return value;
    }
    internal static bool Bool(JsonElement p, string key, bool fallback = false)
    {
        if (!p.TryGetProperty(key, out var v)) return fallback;
        Require(v.ValueKind is JsonValueKind.True or JsonValueKind.False);
        return v.GetBoolean();
    }
    internal static long Number(JsonElement p, string key)
    {
        Require(p.TryGetProperty(key, out var v) && v.TryGetInt64(out _) && v.GetInt64() >= 0, "INVALID_RESPONSE");
        return v.GetInt64();
    }
    internal static JsonElement Okay(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.Object, "INVALID_RESPONSE");
        Require(value.TryGetProperty("ok", out var ok) &&
            ok.ValueKind is JsonValueKind.True or JsonValueKind.False, "INVALID_RESPONSE");
        if (!ok.GetBoolean()) throw new RecommendationException(Text(value, "code", "INVALID_RESPONSE", 128));
        return value;
    }
    internal static string NativeId(JsonElement song) => Feedback.MusicFeedbackIdentity.Resolve(song, "wy")?.Id ?? "";
    internal static bool ValidId(string id) => id.Length is > 0 and <= 16 && id[0] != '0' &&
        id.All(char.IsAsciiDigit) && long.TryParse(id, out var n) && n is > 0 and <= 9007199254740991;
}
