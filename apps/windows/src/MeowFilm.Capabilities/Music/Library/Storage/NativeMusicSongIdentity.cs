namespace MeowFilm.Capabilities.Library;

internal static partial class NativeMusicSong
{
    private static bool KnownIdentityText(string value) => value.Length > 0 &&
        value is not ("未知歌手" or "未知歌曲" or "unknown" or "unknown artist" or "unknown title");

    internal sealed record RecordingIdentity(string Key, string Title, string Artist, double Duration)
    {
        internal string Fingerprint => KnownIdentityText(Title) && Artist.Length > 0
            ? MusicRules.CanonicalArray(Title, Artist) : "";
        internal bool Compatible(RecordingIdentity other) => Key.Length > 0 && Key == other.Key ||
            Fingerprint.Length > 0 && Fingerprint == other.Fingerprint && Duration > 0 && other.Duration > 0 &&
            Math.Abs(Duration - other.Duration) <= 5;
    }

    private static string ArtistKey(string[] artistNames, string fallbackArtist)
    {
        if (artistNames.Length > 0)
        {
            var normalized = artistNames.Select(NormalText).ToArray();
            return normalized.Length == 0 || !normalized.All(KnownIdentityText) ? "" :
                MusicRules.CanonicalArray(normalized.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        }
        string name = NormalText(fallbackArtist);
        return KnownIdentityText(name) ? MusicRules.CanonicalArray(name) : "";
    }

    internal static RecordingIdentity ReadIdentity(MusicSongRecord song)
    {
        var baseline = song.BaselineDuration ?? song.Duration ?? 0;
        return new(SourceKey(song), NormalText(song.Title), ArtistKey(song.ArtistNames, song.Artist), baseline > 0 ? baseline : 0);
    }

    internal static RecordingIdentity ReadIdentity(MusicSongSourceRecord source)
    {
        var dur = source.Duration ?? 0;
        return new(source.SourceKey, NormalText(source.Name ?? ""), ArtistKey(source.ArtistNames, source.Singer ?? ""), dur > 0 ? dur : 0);
    }

    internal static string Fingerprint(MusicSongRecord song) => ReadIdentity(song).Fingerprint;

    /// <summary>A repository record. Its first valid duration is fixed; registered sources never change owners.</summary>
    internal sealed class Recordings
    {
        private MusicSongRecord representative;
        private readonly List<string> sourceOrder = [];
        private readonly Dictionary<string, MusicSongSourceRecord> sources = new(StringComparer.Ordinal);

        internal string Id { get; set; }
        internal RecordingIdentity Identity => ReadIdentity(representative);
        internal bool IsLocal => sources.Count == 0;
        internal string LocalFileId => representative.LocalFileId ?? "";
        internal IEnumerable<MusicSongSourceRecord> Sources => sourceOrder.Select(k => sources[k]);

        internal Recordings(MusicSongRecord song, CancellationToken token = default)
            : this(song, song.Sources, token) { }

        internal Recordings(MusicSongRecord song, IEnumerable<MusicSongSourceRecord> listed, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            representative = song with { BaselineDuration = song.BaselineDuration ?? ValidDuration(song.Duration) };
            Id = song.SongId;
            foreach (var source in listed)
            {
                token.ThrowIfCancellationRequested();
                SetSource(source);
            }
        }

        private void SetSource(MusicSongSourceRecord source)
        {
            if (!sources.ContainsKey(source.SourceKey)) sourceOrder.Add(source.SourceKey);
            sources[source.SourceKey] = source;
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
            foreach (var key in other.sourceOrder)
            {
                token.ThrowIfCancellationRequested();
                var src = other.sources[key];
                if (preferIncoming || !sources.ContainsKey(key)) SetSource(src);
            }

            string title = preferIncoming && samePrimary || other.representative.Title.Length > 0 && representative.Title.Length == 0
                ? other.representative.Title : representative.Title;
            string artist = preferIncoming && samePrimary || other.representative.Artist.Length > 0 && representative.Artist.Length == 0
                ? other.representative.Artist : representative.Artist;
            string album = preferIncoming && samePrimary || other.representative.Album.Length > 0 && representative.Album.Length == 0
                ? other.representative.Album : representative.Album;
            string[] artistNames = preferIncoming && samePrimary || other.representative.ArtistNames.Length > 0 && representative.ArtistNames.Length == 0
                ? other.representative.ArtistNames : representative.ArtistNames;
            double? duration = preferIncoming && samePrimary || ValidDuration(representative.Duration) is null
                ? ValidDuration(other.representative.Duration) : representative.Duration;
            double? baseline = ValidDuration(representative.BaselineDuration) ??
                ValidDuration(other.representative.BaselineDuration) ??
                ValidDuration(other.representative.Duration);

            representative = representative with
            {
                Title = title,
                Artist = artist,
                Album = album,
                ArtistNames = artistNames,
                Duration = duration,
                BaselineDuration = baseline
            };
            return true;
        }

        internal bool TryAddSource(MusicSongSourceRecord source, bool replace = false)
        {
            var canonical = DurableSource(source);
            var evidence = ReadIdentity(canonical);
            if (!Accepts(evidence)) return false;
            if (replace || !sources.ContainsKey(canonical.SourceKey)) SetSource(canonical);
            return true;
        }

        internal void SelectArtwork(string[] platforms, HashSet<string> enabled, bool force = false)
        {
            if (IsLocal) return;
            var eligible = platforms.Where(enabled.Contains).SelectMany(platform =>
                Sources.Where(source => source.Platform == platform && !string.IsNullOrEmpty(source.Img))).ToArray();
            string key = representative.ArtworkSourceKey ?? "";
            var selected = force ? null : eligible.FirstOrDefault(source => source.SourceKey == key);
            if (selected is null && !force && key.Length == 0)
                selected = eligible.FirstOrDefault(source => (source.Img ?? "") == representative.CoverUrl);
            selected ??= eligible.FirstOrDefault();
            string image = selected?.Img ?? "";
            string nextKey = selected?.SourceKey ?? "";
            bool changed = force || nextKey != key || image != (representative.ArtworkSourceImage ?? "");
            representative = representative with
            {
                ArtworkSourceKey = nextKey,
                ArtworkSourceImage = image,
                CoverUrl = changed ? image : representative.CoverUrl
            };
        }

        internal MusicSongRecord ToSong(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            return representative with
            {
                SongId = Id,
                Sources = Sources.ToArray()
            };
        }
    }
}
