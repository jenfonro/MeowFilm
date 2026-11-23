using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

internal static partial class NativeMusicSong
{
    private static bool KnownIdentityText(string value) => value.Length > 0 &&
        value is not ("未知歌手" or "未知歌曲" or "unknown" or "unknown artist" or "unknown title");

    internal sealed record RecordingIdentity(string Key, string Title, string Artist, double Duration)
    {
        internal string Fingerprint => KnownIdentityText(Title) && Artist.Length > 0
            ? LJ.Stringify(new JsonArray(Title, Artist)) : "";
        internal bool Compatible(RecordingIdentity other) => Key.Length > 0 && Key == other.Key ||
            Fingerprint.Length > 0 && Fingerprint == other.Fingerprint && Duration > 0 && other.Duration > 0 &&
            Math.Abs(Duration - other.Duration) <= 5;
    }
    private static string ArtistKey(JsonObject value)
    {
        var data = value.ContainsKey("musicInfo") ? SourceData(value) : value;
        if (data["artistNames"] is JsonArray names)
        {
            var normalized = names.Select(name => NormalText(LJ.S(name))).ToArray();
            return normalized.Length == 0 || !normalized.All(KnownIdentityText) ? "" :
                LJ.Stringify(LJ.Arr(normalized.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
        }
        string name = NormalText(value.ContainsKey("musicInfo") ? LJ.S(data["singer"]) : LJ.S(data["artist"]));
        return KnownIdentityText(name) ? LJ.Stringify(new JsonArray(name)) : "";
    }
    internal static RecordingIdentity ReadIdentity(JsonObject value)
    {
        var shown = Display(value);
        var baseline = value["baselineDuration"] is null ? shown.Duration : LJ.N(value["baselineDuration"]);
        return new(SourceKey(value), NormalText(shown.Title), ArtistKey(value), baseline > 0 ? baseline : 0);
    }
    internal static string Fingerprint(JsonObject song) => ReadIdentity(song).Fingerprint;

    /// <summary>A repository record. Its first valid duration is fixed; registered sources never change owners.</summary>
    internal sealed class Recordings
    {
        private JsonObject representative;
        private readonly Dictionary<string, JsonObject> sources = new(StringComparer.Ordinal);
        internal string Id { get; set; }
        internal RecordingIdentity Identity => ReadIdentity(representative);
        internal bool IsLocal => sources.Count == 0;
        internal string LocalFileId => LJ.S(representative["localFileId"]);
        internal IEnumerable<JsonObject> Sources => sources.Values;

        internal Recordings(JsonObject song, CancellationToken token = default)
            : this(song, LJ.Array(song["sources"]).OfType<JsonObject>(), token) { }
        internal Recordings(JsonObject song, IEnumerable<JsonObject> listed, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            representative = song.DeepClone().AsObject(); Id = LJ.S(song["songId"]);
            if (!representative.ContainsKey("baselineDuration"))
                representative["baselineDuration"] = ValidDuration(song["duration"]);
            foreach (var source in listed) { token.ThrowIfCancellationRequested(); sources[LJ.S(source["sourceKey"])] = source.DeepClone().AsObject(); }
        }
        internal bool Accepts(RecordingIdentity evidence, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            return sources.ContainsKey(evidence.Key) || Identity.Compatible(evidence);
        }
        internal bool CanMerge(Recordings other, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (Id.Length > 0 && Id == other.Id) return true;
            if (LocalFileId.Length > 0 && LocalFileId == other.LocalFileId) return true;
            if (sources.Keys.Intersect(other.sources.Keys, StringComparer.Ordinal).Any()) return true;
            return !IsLocal && !other.IsLocal && Identity.Fingerprint.Length > 0 &&
                Identity.Fingerprint == other.Identity.Fingerprint && Identity.Duration > 0 && other.Identity.Duration > 0 &&
                Math.Abs(Identity.Duration - other.Identity.Duration) <= 5;
        }
        internal bool TryMerge(Recordings other, bool preferIncoming = false, CancellationToken token = default)
        {
            if (!CanMerge(other, token)) return false;
            bool samePrimary = SourceKey(representative).Length > 0 &&
                SourceKey(representative) == SourceKey(other.representative);
            foreach (var source in other.sources)
            {
                token.ThrowIfCancellationRequested();
                if (preferIncoming) sources[source.Key] = source.Value.DeepClone().AsObject();
                else sources.TryAdd(source.Key, source.Value.DeepClone().AsObject());
            }
            foreach (string field in new[] { "title", "artist", "album" })
                if (preferIncoming && samePrimary ||
                    LJ.S(other.representative[field]).Length > 0 && LJ.S(representative[field]).Length == 0)
                    representative[field] = other.representative[field]?.DeepClone();
            if (preferIncoming && samePrimary ||
                other.representative["artistNames"] is JsonArray && representative["artistNames"] is null)
            {
                if (other.representative["artistNames"] is JsonArray names)
                    representative["artistNames"] = names.DeepClone();
                else representative.Remove("artistNames");
            }
            if (preferIncoming && samePrimary || ValidDuration(representative["duration"]) is null)
                representative["duration"] = ValidDuration(other.representative["duration"]);
            if (ValidDuration(representative["baselineDuration"]) is null)
                representative["baselineDuration"] = ValidDuration(other.representative["baselineDuration"]) ??
                    ValidDuration(other.representative["duration"]);
            return true;
        }
        internal bool TryAddSource(JsonObject source, bool replace = false)
        {
            var canonical = DurableSource(source); var evidence = ReadIdentity(canonical);
            if (!Accepts(evidence)) return false;
            if (replace) sources[LJ.S(canonical["sourceKey"])] = canonical;
            else sources.TryAdd(LJ.S(canonical["sourceKey"]), canonical);
            return true;
        }
        internal void SelectArtwork(string[] platforms, HashSet<string> enabled, bool force = false)
        {
            if (IsLocal) return;
            var eligible = platforms.Where(enabled.Contains).SelectMany(platform =>
                sources.Values.Where(source => LJ.S(source["source"]) == platform &&
                    LJ.S(source["musicInfo"]?["img"]).Length > 0)).ToArray();
            string key = LJ.S(representative["artworkSourceKey"]);
            var selected = force ? null : eligible.FirstOrDefault(source => LJ.S(source["sourceKey"]) == key);
            if (selected is null && !force && key.Length == 0)
                selected = eligible.FirstOrDefault(source => LJ.S(source["musicInfo"]?["img"]) == LJ.S(representative["cover_url"]));
            selected ??= eligible.FirstOrDefault();
            string image = LJ.S(selected?["musicInfo"]?["img"]);
            bool changed = force || LJ.S(selected?["sourceKey"]) != key ||
                image != LJ.S(representative["artworkSourceImage"]);
            representative["artworkSourceKey"] = selected?["sourceKey"]?.DeepClone() ?? JsonValue.Create("");
            representative["artworkSourceImage"] = image;
            if (changed) representative["cover_url"] = image;
        }
        internal JsonObject ToSong(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var result = representative.DeepClone().AsObject();
            result["songId"] = Id;
            result["sources"] = LJ.Arr(sources.Values);
            return result;
        }
    }
}
