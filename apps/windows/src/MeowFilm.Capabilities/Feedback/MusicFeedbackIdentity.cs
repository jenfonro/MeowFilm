using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Recommendations;

namespace MeowFilm.Capabilities.Feedback;

public sealed record MusicFeedbackSelection(string EntryId, JsonElement Song, JsonElement? Context = null,
    bool Automatic = false)
{
    // Native-only proof. Neither queue JSON nor an import can manufacture it.
    internal MusicFeedbackRecommendation? Recommendation { get; init; }
}

internal sealed record MusicFeedbackRecommendation(MusicRecommendationFeedback Value, Func<bool> IsCurrent);

internal static class MusicFeedbackIdentity
{
    internal static MusicFeedbackRecording? Resolve(JsonElement song, string platform,
        MusicFeedbackRecording? pinned = null)
    {
        try
        {
            if (song.ValueKind != JsonValueKind.Object) return null;
            var root = NativeMusicSong.Normalize(MusicWireCodec.ReadSong(song));
            var candidates = new HashSet<MusicFeedbackRecording>();
            foreach (var src in root.Sources)
            {
                if (RecordingFromSource(src, platform) is { } mapped) candidates.Add(mapped);
            }
            if (platform == "kg")
                foreach (var known in candidates.Where(c => c.MixSongId is not null).ToArray())
                    candidates.Remove(new("kg", known.Id));
            if (pinned is not null && candidates.Contains(pinned)) return pinned;
            if (pinned is { Platform: "kg" })
            {
                var hash = candidates.Where(c => c.Id == pinned.Id).ToArray();
                if (hash.Length == 1 && (pinned.MixSongId is null || hash[0].MixSongId is null ||
                    hash[0].MixSongId == pinned.MixSongId)) return pinned;
            }
            if (RecordingFromElement(song, platform) is { } primary && candidates.Contains(primary)) return primary;
            if (candidates.Count == 1) return candidates.Single();
            return null; // Ambiguous provenance is never resolved by candidate order.
        }
        catch { return null; }
    }

    private static MusicFeedbackRecording? RecordingFromSource(MusicSongSourceRecord src, string platform)
    {
        if (src.InfoSource != platform) return null;
        if (platform is "wy" or "tx")
        {
            string id = NativeMusicSong.InfoId(src);
            return id.Length > 0 ? new(platform, id) : null;
        }
        if (platform != "kg") return null;
        string hash = src.Hash ?? "";
        string albumAudioId = StorageValueJson.ScalarText(StorageValueJson.Field(src.WireExtras, "albumAudioId")) ?? "";
        return hash.Length > 0 ? new(platform, hash, albumAudioId.Length > 0 ? albumAudioId : null) : null;
    }

    private static MusicFeedbackRecording? RecordingFromElement(JsonElement row, string platform)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;
        var data = MusicWireCodec.TryGet(row, "musicInfo", out var mi) && mi.ValueKind == JsonValueKind.Object ? mi : row;
        if (MusicWireCodec.Str(data, "source") != platform) return null;
        if (platform is "wy" or "tx")
        {
            string id = FirstNonEmpty(MusicWireCodec.ScalarProp(data, "songmid"),
                MusicWireCodec.ScalarProp(data, "mid"),
                MusicWireCodec.ScalarProp(data, "id"),
                MusicWireCodec.ScalarProp(data, "hash"));
            return id.Length > 0 ? new(platform, id) : null;
        }
        if (platform != "kg") return null;
        string hash = MusicWireCodec.Str(data, "hash");
        string albumAudioId = MusicWireCodec.ScalarProp(data, "albumAudioId");
        return hash.Length > 0 ? new(platform, hash, albumAudioId.Length > 0 ? albumAudioId : null) : null;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string v in values)
            if (v.Length > 0) return v;
        return "";
    }

    internal static MusicFeedbackStart Start(MusicFeedbackAttempt instance, MusicFeedbackSelection selection,
        MusicFeedbackRecording recording, double duration, double position, double rate, string mode)
    {
        string source = "unknown", sourceId = "", algorithm = "";
        var recommendation = LiveRecommendation(selection);
        // A second trusted recording on the same platform does not inherit
        // the per-song recommendation metadata of the selected native ID.
        if (recommendation is { } originForPlatform && originForPlatform.Platform == recording.Platform &&
            RecommendedRecording(originForPlatform, selection.Song) != recording.Id)
            recommendation = null;
        mode = recommendation?.Kind switch { "fm" => "playFm", "heartbeat" => "ai", _ => mode };
        JsonElement? context = selection.Context is { ValueKind: JsonValueKind.Object } queued &&
            Value(queued, "sourceType") is "playlist" or "album" or "daily" or "search" or "search-song" or "song"
                ? queued.Clone() : null;
        MusicFeedbackStart Facts(MusicFeedbackStart value) => value with
        { Context = context, Recommendation = recommendation };
        if (recording.Platform == "wy" && recommendation is { Platform: "wy" } origin &&
            RecommendedRecording(origin, selection.Song) == recording.Id)
        {
            algorithm = origin.Algorithm;
            if (origin.Kind == "fm")
            {
                // Rule FM has no verified private-FM reporting attribution.
                if (origin.Provider != "rule") source = "userfm";
                mode = "playFm";
            }
            else if (origin.Provider == "heartbeat")
            { source = "aiMode"; sourceId = origin.SourceId; mode = "ai"; }
            else if (origin.Provider == "queue-recommendations")
            {
                if (origin.FeedbackSource.Length > 0) source = origin.FeedbackSource;
                if (origin.Kind == "heartbeat") mode = "ai";
            }
            return Facts(new(instance, recording, duration, position, rate, selection.Automatic,
                source, sourceId, algorithm, mode)
                { IsQueueRecommendation = origin.Provider == "queue-recommendations" && origin.FeedbackSource.Length > 0 });
        }
        // Only preserve provenance for the selected exact recording. FM and
        // recommendation modes require their own live controller; old persisted
        // context alone is not proof of a current automatic recommendation.
        if (recording.Platform == "wy" && context is { } ordinary &&
            (Value(ordinary, "sourcePlatform") == "wy" ||
                Value(ordinary, "sourcePlatform") == "" && Value(ordinary, "sourceType") is "search" or "search-song"))
        {
            string kind = Value(ordinary, "sourceType"), id = Value(ordinary, "sourceId");
            if (kind is "playlist" or "album" && id.Length > 0)
            { source = kind == "playlist" ? "list" : "album"; sourceId = id; }
            else if (kind == "daily" && Value(ordinary, "dailyBranch") is "" or "default")
            { source = "dailySongRecommend"; }
            else if (kind is "search" or "search-song")
            { source = "search"; sourceId = Value(ordinary, "searchKeyword"); }
            else if (kind == "song") { source = "song"; }
        }
        return Facts(new(instance, recording, duration, position, rate, selection.Automatic,
            source, sourceId, algorithm, mode));
    }

    private static MusicRecommendationFeedback? LiveRecommendation(MusicFeedbackSelection selection)
    {
        if (selection.Recommendation is not { } proof || proof.Value.EntryId != selection.EntryId || !proof.IsCurrent()) return null;
        var origin = proof.Value;
        string id = RecommendedRecording(origin, selection.Song);
        return id.Length > 0 && Resolve(selection.Song, origin.Platform, new(origin.Platform, id))?.Id == id ? origin : null;
    }

    private static string RecommendedRecording(MusicRecommendationFeedback origin, JsonElement song) =>
        origin.Metadata is { ValueKind: JsonValueKind.Object } metadata ? Value(metadata, "id") :
            RecordingFromElement(song, origin.Platform)?.Id ?? "";

    private static string Value(JsonElement value, string key) =>
        value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()! : "";
}
