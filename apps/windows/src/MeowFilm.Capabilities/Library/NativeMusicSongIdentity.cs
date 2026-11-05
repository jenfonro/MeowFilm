using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MeowFilm.Capabilities.Library;

internal static partial class NativeMusicSong
{
    internal sealed record RecordingIdentity(string Key, string Title, string Artist, string Version, double Duration)
    {
        private static bool Known(string value) => value.Length > 0 &&
            !value.StartsWith("unknown", StringComparison.Ordinal) && !value.StartsWith("未知", StringComparison.Ordinal);
        internal string Fingerprint { get; } = Known(Title) && Known(Artist)
            ? LJ.Stringify(new JsonArray(Title, Artist, Version)) : "";
        internal JsonObject Descriptor() =>
            LJ.Obj(("title", Title), ("artist", Artist), ("version", Version), ("duration", Duration));
        internal bool Compatible(RecordingIdentity other)
        {
            bool durations = !(Duration > 0 && other.Duration > 0) ||
                Math.Abs(Duration - other.Duration) <= Math.Max(3, Math.Min(Duration, other.Duration) * .02);
            if (Key.Length > 0 && Key == other.Key)
                return (!Known(Artist) || !Known(other.Artist) || Artist == other.Artist) &&
                    (!Known(Title) || !Known(other.Title) || Version == other.Version) && durations;
            return Fingerprint.Length > 0 && Fingerprint == other.Fingerprint &&
                Duration > 0 && other.Duration > 0 && durations;
        }
    }

    internal static RecordingIdentity ReadIdentity(JsonObject value)
    {
        var shown = Display(value);
        string name = NormalText(shown.Title);
        string artist = string.Join(" & ", Regex.Split(NormalText(shown.Artist),
            Pattern(@"\s*(?:、|;|\s/\s|\s&\s|\sfeat\.?\s|\sfeaturing\s)\s*"), RegexOptions.CultureInvariant, RegexTimeout)
            .Where(s => s.Length > 0).Order(StringComparer.Ordinal));
        string[] patterns = ["和声伴奏|和聲伴奏", "伴奏|instrumental|karaoke", @"(?<![a-z0-9_])live(?![a-z0-9_])|现场|現場",
            @"remix|(?<![a-z0-9_])dj(?![a-z0-9_])", "remaster|重制|重製", "acoustic|不插电|不插電", @"翻唱|(?<![a-z0-9_])cover(?![a-z0-9_])"];
        string[] tags = ["harmony", "instrumental", "live", "remix", "remaster", "acoustic", "cover"];
        var versions = tags.Where((_, i) => Match(name, patterns[i]));
        string title = Regex.Replace(name, Pattern(@"\s*[\(（]([^()（）]+)[\)）]\s*$"), m =>
            NormalText(m.Groups[1].Value) == Regex.Split(name, "[（(]", RegexOptions.None, RegexTimeout)[0].Trim(' ') ? "" : m.Value,
            RegexOptions.CultureInvariant, RegexTimeout);
        title = Replace(Replace(title, @"\s*\(\s*", "("), @"\s*\)\s*", ")").Trim(' ');
        return new(SourceKey(value), title, artist, string.Join(',', versions), shown.Duration);
    }
    internal static JsonObject Descriptor(JsonObject value) => ReadIdentity(value).Descriptor();
    internal static string Fingerprint(JsonObject song) => ReadIdentity(song).Fingerprint;
    internal static bool Compatible(JsonObject a, JsonObject b) => ReadIdentity(a).Compatible(ReadIdentity(b));

    /// <summary>
    /// One song's canonical recording set. Inputs are normalized songs (or the validated sources
    /// in Normalize). Identity evidence is parsed once, not re-normalized for every pair.
    /// The preferred song supplies display metadata and the primary recording; other sources
    /// can join only when all recording pairs remain compatible.
    /// </summary>
    internal sealed class Recordings
    {
        private sealed record Recording(JsonObject Source, RecordingIdentity Identity);
        private JsonObject representative;
        private readonly Dictionary<string, Recording> sources = new(StringComparer.Ordinal);
        internal string Id { get; set; }
        internal RecordingIdentity Identity { get; private set; }
        internal bool IsLocal => sources.Count == 0;
        internal IEnumerable<JsonObject> Sources => sources.Values.Select(row => row.Source);
        internal IEnumerable<RecordingIdentity> Evidence => sources.Values.Select(row => row.Identity).Prepend(Identity);

        internal Recordings(JsonObject song, CancellationToken token = default)
            : this(song, LJ.Array(song["sources"], "INVALID_SOURCE").Select(source => LJ.Object(source, "INVALID_SOURCE")), token) { }
        internal Recordings(JsonObject song, IEnumerable<JsonObject> listed, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            representative = song; Id = LJ.S(song["songId"]); Identity = ReadIdentity(song);
            foreach (var source in listed)
            {
                token.ThrowIfCancellationRequested();
                string key = LJ.S(source["sourceKey"]);
                LJ.Require(!sources.ContainsKey(key), "INVALID_SOURCE");
                var evidence = ReadIdentity(source);
                LJ.Require(Accepts(evidence, token), "RECORDING_CONFLICT");
                sources.Add(key, new(source, evidence));
            }
        }
        internal bool Accepts(RecordingIdentity evidence, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (!Identity.Compatible(evidence)) return false;
            foreach (var row in sources.Values)
            {
                token.ThrowIfCancellationRequested();
                if (!row.Identity.Compatible(evidence)) return false;
            }
            return true;
        }
        internal bool CanMerge(Recordings other, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            // Local identity belongs to the local song id, not guessed title/duration metadata.
            if (sources.Count == 0 && other.sources.Count == 0) return Id == other.Id;
            foreach (var evidence in other.Evidence)
            {
                token.ThrowIfCancellationRequested();
                if (!Accepts(evidence, token)) return false;
            }
            return true;
        }
        internal bool TryMerge(Recordings other, bool preferIncoming = false, CancellationToken token = default)
        {
            if (!CanMerge(other, token)) return false;
            foreach (var pair in other.sources)
            {
                token.ThrowIfCancellationRequested();
                if (preferIncoming) sources[pair.Key] = pair.Value;
                else sources.TryAdd(pair.Key, pair.Value);
            }
            var preferred = preferIncoming ? other.Identity : Identity;
            string primary = preferred.Key.Length > 0 ? preferred.Key : preferIncoming ? Identity.Key : other.Identity.Key;
            if (preferIncoming) representative = other.representative;
            Identity = preferred.Key == primary ? preferred : preferred with { Key = primary };
            return true;
        }
        internal bool TryAddSource(JsonObject source, bool replace = false)
        {
            var canonical = DurableSource(source);
            var evidence = ReadIdentity(canonical);
            if (!Accepts(evidence)) return false;
            if (replace) sources[LJ.S(canonical["sourceKey"])] = new(canonical, evidence);
            else sources.TryAdd(LJ.S(canonical["sourceKey"]), new(canonical, evidence));
            if (Identity.Key.Length == 0) Identity = Identity with { Key = evidence.Key };
            return true;
        }
        internal string QualifiedId() => Id + ":" + Hash(LJ.Stringify(new JsonArray(Identity.Key, Identity.Descriptor(),
            LJ.Arr(sources.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
                new JsonArray(pair.Key, pair.Value.Identity.Descriptor()))))));
        internal JsonObject ToSong(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var result = (JsonObject)representative.DeepClone();
            result["songId"] = Id;
            result["sources"] = new JsonArray(sources.OrderBy(pair => pair.Key == Identity.Key ? 0 : 1)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
                {
                    token.ThrowIfCancellationRequested();
                    return pair.Value.Source.DeepClone();
                }).ToArray());
            return result;
        }
    }
}
