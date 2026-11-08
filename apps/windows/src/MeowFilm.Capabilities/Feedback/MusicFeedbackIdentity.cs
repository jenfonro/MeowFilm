using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    internal static string PlaybackMode(MusicFeedbackSelection selection, string fallback) =>
        selection.Recommendation is { } proof && proof.Value.EntryId == selection.EntryId && proof.IsCurrent()
            ? proof.Value.Kind switch { "fm" => "playFm", "heartbeat" => "ai", _ => fallback } : fallback;
    internal static MusicFeedbackRecording? Resolve(JsonElement song, string platform,
        MusicFeedbackRecording? pinned = null)
    {
        try
        {
            if (song.ValueKind != JsonValueKind.Object || song.GetRawText().Length > 262144) return null;
            var root = NativeMusicSong.Normalize(JsonNode.Parse(song.GetRawText()));
            var rows = root["sources"]!.AsArray().OfType<JsonObject>().ToArray();
            var candidates = new HashSet<MusicFeedbackRecording>();
            foreach (var row in rows.Take(256))
            {
                if (!NativeMusicSong.Compatible(root, row)) continue;
                if (Recording(row, platform) is { } mapped) candidates.Add(mapped);
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
            if (Recording(root, platform) is { } primary && candidates.Contains(primary)) return primary;
            if (candidates.Count == 1) return candidates.Single();
            if (platform == "kg" && candidates.Count > 0 &&
                candidates.All(c => c.MixSongId is not null) && candidates.Select(c => c.MixSongId).Distinct().Count() == 1)
                return candidates.OrderBy(c => c.Id, StringComparer.Ordinal).First();
            return null; // Ambiguous provenance is never resolved by candidate order.
        }
        catch { return null; }
    }

    private static MusicFeedbackRecording? Recording(JsonObject row, string platform)
    {
        var data = NativeMusicSong.SourceData(row);
        if (Text(data["source"]) != platform || Text(row["source"]) is { Length: > 0 } p && p != platform) return null;
        var id = Text(data["songmid"]);
        if (platform == "wy") return Positive(id) ? new(platform, id) : null;
        if (platform == "tx") return id.Length is >= 1 and <= 64 && id.All(char.IsAsciiLetterOrDigit) ? new(platform, id) : null;
        var hash = Text(data["hash"]);
        if (platform != "kg" || hash.Length != 32 || !hash.All(char.IsAsciiHexDigit) || hash.All(c => c == '0')) return null;
        var ids = new[] { data["albumAudioId"], data["album_audio_id"], data["mixsongid"] }
            .Select(Text).Where(s => s.Length > 0 && s != "0").ToArray();
        if (ids.Any(s => !Positive(s)) || ids.Distinct(StringComparer.Ordinal).Count() > 1) return null;
        if (id.Length == 32 && id.All(char.IsAsciiHexDigit) &&
            !string.Equals(hash, id, StringComparison.OrdinalIgnoreCase)) return null;
        return new(platform, hash.ToUpperInvariant(), ids.FirstOrDefault());
    }

    internal static MusicFeedbackStart Start(MusicFeedbackAttempt instance, MusicFeedbackSelection selection,
        MusicFeedbackRecording recording, double duration, double position, double rate, string mode)
    {
        string source = "unknown", sourceId = "", sourceType = "", algorithm = "";
        JsonObject? selectedSong = null;
        try { selectedSong = JsonNode.Parse(selection.Song.GetRawText()) as JsonObject; }
        catch { }
        bool primaryRecording = selectedSong is not null && Recording(selectedSong, recording.Platform) == recording;
        if (recording.Platform == "wy" && selection.Recommendation is { } proof && proof.IsCurrent() &&
            proof.Value.EntryId == selection.EntryId && proof.Value.Platform == "wy" &&
            primaryRecording)
        {
            var origin = proof.Value;
            if (origin.Algorithm.Length <= 2000 && !origin.Algorithm.Any(char.IsControl))
                algorithm = origin.Algorithm;
            if (origin.Kind == "fm")
            {
                // Rule FM has no verified private-FM reporting attribution.
                if (origin.Provider != "rule") source = "userfm";
                mode = "playFm";
            }
            else if (origin.Provider == "heartbeat" && Positive(origin.SourceId))
            { source = "aiMode"; sourceId = origin.SourceId; sourceType = "track"; mode = "ai"; }
            else if (origin.Provider == "queue-recommendations")
            {
                source = origin.FeedbackSource.Length > 0 ? origin.FeedbackSource : "moreRcmd";
                sourceType = "track";
                if (origin.Kind == "heartbeat") mode = "ai";
            }
            else if (origin.Kind == "similar") sourceType = "track";
            return new(instance, recording, duration, position, rate, selection.Automatic,
                source, sourceId, sourceType, algorithm, mode)
                { IsQueueRecommendation = origin.Provider == "queue-recommendations" };
        }
        // Only preserve provenance for the selected exact recording. FM and
        // recommendation modes require their own live controller; old persisted
        // context alone is not proof of a current automatic recommendation.
        if (recording.Platform == "wy" && selection.Context is { ValueKind: JsonValueKind.Object } context &&
            Value(context, "sourcePlatform") == "wy")
        {
            string kind = Value(context, "sourceType"), id = Value(context, "sourceId");
            bool native = primaryRecording;
            if (kind is "playlist" or "album" && native && Positive(id))
            { source = kind; sourceId = id; sourceType = kind == "playlist" ? "list" : "album"; }
            else if (kind == "daily" && native && Value(context, "dailyBranch") is "" or "default")
            { source = "dailySongRecommend"; sourceType = "dailyRecommend"; }
            else if (kind is "search" or "search-song")
            {
                string keyword = Value(context, "searchKeyword");
                if (keyword.Length <= 512 && !keyword.Any(c => char.IsControl(c)))
                { source = "search"; sourceId = keyword; sourceType = "track"; }
            }
            else if (kind == "song") { source = "song"; sourceType = "track"; }
        }
        return new(instance, recording, duration, position, rate, selection.Automatic,
            source, sourceId, sourceType, algorithm, mode);
    }

    private static string Value(JsonElement value, string key) =>
        value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()! : "";
    private static string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var text) ? text :
        node is JsonValue n && n.TryGetValue<long>(out var number) ? number.ToString(CultureInfo.InvariantCulture) : "";
    private static bool Positive(string value) => value.Length is >= 1 and <= 16 && value[0] is >= '1' and <= '9' &&
        value.All(char.IsAsciiDigit) && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
        id <= 9007199254740991;
}
