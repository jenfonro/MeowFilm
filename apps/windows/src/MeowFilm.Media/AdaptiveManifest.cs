using System.Globalization;
using System.Text;

namespace MeowFilm.Media;

/// <summary>A finite VOD graph, rebuilt rather than forwarding untrusted playlist text.</summary>
internal sealed partial class AdaptiveManifest(AdaptiveHttpTransport transport, Uri localOrigin,
    bool deterministic = false, bool allowLive = false, bool singleMedia = false)
{
    internal const int MaximumBytes = 1024 * 1024;
    private readonly Dictionary<string, AdaptiveResource> _resources = new(StringComparer.Ordinal);
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AdaptiveKey> _keys = new(StringComparer.Ordinal);
    private int _manifestBytes;
    private int _generatedBytes;
    internal IReadOnlyDictionary<string, AdaptiveResource> Resources => _resources;
    internal void Clear()
    {
        foreach (var resource in _resources.Values) resource.Release();
        foreach (var key in _keys.Values) key.Clear();
        _keys.Clear();
        _resources.Clear(); _active.Clear();
    }
    internal static bool IsManifest(ReadOnlySpan<byte> prefix)
    {
        string text = Encoding.UTF8.GetString(prefix).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        return text.StartsWith("#EXTM3U", StringComparison.Ordinal) ||
            text.StartsWith("<?xml", StringComparison.Ordinal) ||
            text.StartsWith("<MPD", StringComparison.Ordinal) ||
            text.StartsWith("<SmoothStreamingMedia", StringComparison.Ordinal);
    }
    internal static bool HasManifestPath(Uri uri) => !uri.IsFile &&
        new[] { ".m3u8", ".m3u", ".mpd", ".ism", ".isml" }.Any(e =>
            Uri.UnescapeDataString(uri.AbsolutePath).EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private static PlaybackException Invalid() => new(PlaybackError.AdaptiveInvalid);
    private static PlaybackException Unsupported() => new(PlaybackError.AdaptiveUnsupported);
    private string Add(AdaptiveResource resource)
    {
        if (_resources.Count >= 4096) throw new PlaybackException(PlaybackError.AdaptiveBudget);
        string path = "/r/" + (deterministic ? _resources.Count.ToString("x32", CultureInfo.InvariantCulture) : Guid.NewGuid().ToString("N"));
        resource.Encryption?.Key.Retain();
        _resources.Add(path, resource);
        return new Uri(localOrigin, path).AbsoluteUri;
    }
    private string Resource(Uri parent, bool credentials, string reference, AdaptiveResourceKind kind,
        (long Offset, long Count)? range = null, AdaptiveEncryption? encryption = null)
    {
        var target = transport.Resolve(parent, credentials, reference);
        return Add(new(target.Uri, target.Credentials, kind, range?.Offset, range?.Count) { Encryption = encryption });
    }
    internal async Task<Uri> BuildAsync(byte[] bytes, Uri parent, bool credentials, CancellationToken token,
        int depth = 0, bool hlsChild = false)
    {
        token.ThrowIfCancellationRequested();
        if (depth >= 8 || bytes.Length > MaximumBytes ||
            (_manifestBytes = checked(_manifestBytes + bytes.Length)) > 4 * MaximumBytes)
            throw new PlaybackException(PlaybackError.AdaptiveBudget);
        string key = parent.AbsoluteUri;
        if (!_active.Add(key)) throw Invalid();
        try
        {
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
            catch (DecoderFallbackException) { throw Invalid(); }
            string sanitized;
            HlsWindow? window = null;
            if (text.StartsWith("#EXTM3U", StringComparison.Ordinal))
                (sanitized, window) = await HlsAsync(text, parent, credentials, depth, token).ConfigureAwait(false);
            else if (!hlsChild) sanitized = Dash(text, parent, credentials);
            else throw Invalid();
            var encoded = Encoding.UTF8.GetBytes(sanitized);
            if (encoded.Length > MaximumBytes ||
                (_generatedBytes = checked(_generatedBytes + encoded.Length)) > 4 * MaximumBytes)
                throw new PlaybackException(PlaybackError.AdaptiveBudget);
            return new Uri(Add(new(parent, credentials, AdaptiveResourceKind.Manifest) { Manifest = encoded, Window = window }));
        }
        finally { _active.Remove(key); }
    }
    private async Task<string> ChildAsync(Uri parent, bool credentials, string reference, int depth,
        CancellationToken token)
    {
        if (singleMedia) throw Invalid();
        var target = transport.Resolve(parent, credentials, reference);
        var resource = new AdaptiveResource(target.Uri, target.Credentials, AdaptiveResourceKind.Manifest);
        byte[] bytes = await transport.ManifestAsync(resource, token).ConfigureAwait(false);
        return (await BuildAsync(bytes, resource.Uri, resource.Credentials, token, depth + 1, true).ConfigureAwait(false)).AbsoluteUri;
    }
    private async Task<(string Text, HlsWindow? Window)> HlsAsync(string text, Uri parent, bool credentials, int depth, CancellationToken token)
    {
        var result = new StringBuilder("#EXTM3U\n");
        bool master = false, media = false, ended = false, target = false, map = false;
        int segments = 0, variants = 0;
        string? stream = null, duration = null;
        (long Offset, long Count)? range = null;
        AdaptiveKey? currentKey = null;
        byte[]? explicitIV = null;
        ulong sequence = 0;
        bool sequenceSeen = false;
        ulong firstSequence = 0, discontinuity = 0, firstDiscontinuity = 0;
        bool discontinuitySeen = false, discontinuityTag = false;
        string? playlistType = null, mapPath = null;
        long targetDuration = 0;
        decimal totalDuration = 0;
        var items = new List<HlsSegment>();
        var ownedPaths = new HashSet<string>(StringComparer.Ordinal);
        var sessionKeys = new HashSet<string>(StringComparer.Ordinal);
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines[0] != "#EXTM3U" || lines.Length > 16384) throw Invalid();
        foreach (string raw in lines.Skip(1))
        {
            token.ThrowIfCancellationRequested();
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.Length > 16384 || line.Any(c => c < 0x20)) throw Invalid();
            if (!line.StartsWith('#'))
            {
                if (stream is not null)
                {
                    result.AppendLine(stream);
                    result.AppendLine(await ChildAsync(parent, credentials, line, depth, token).ConfigureAwait(false));
                    stream = null; variants++;
                }
                else
                {
                    if (duration is null || ended) throw Invalid();
                    media = true; segments++;
                    result.AppendLine("#EXTINF:" + duration + ",");
                    string address = Resource(parent, credentials, line,
                        map ? AdaptiveResourceKind.Mp4Segment : AdaptiveResourceKind.TransportStream, range,
                        currentKey is null ? null : new(currentKey, explicitIV?.ToArray() ?? SequenceIV(sequence)));
                    result.AppendLine(address);
                    string path = new Uri(address).AbsolutePath;
                    decimal seconds = decimal.Parse(duration, CultureInfo.InvariantCulture);
                    totalDuration += seconds;
                    items.Add(new(sequence, discontinuity, seconds, path, mapPath));
                    ownedPaths.Add(path);
                    if (sequence == ulong.MaxValue) throw Invalid();
                    sequence++;
                    duration = null; range = null;
                }
                continue;
            }
            int colon = line.IndexOf(':');
            string tag = colon < 0 ? line : line[..colon];
            string value = colon < 0 ? "" : line[(colon + 1)..];
            switch (tag)
            {
                case "#EXT-X-VERSION":
                    if (UInt(value, 9) == 0) throw Invalid();
                    result.AppendLine(line); break;
                case "#EXT-X-INDEPENDENT-SEGMENTS":
                    if (value.Length != 0) throw Invalid();
                    result.AppendLine(tag); break;
                case "#EXT-X-DISCONTINUITY":
                    if (value.Length != 0 || discontinuity == ulong.MaxValue) throw Invalid();
                    discontinuity++; discontinuityTag = media = true;
                    result.AppendLine(tag); break;
                case "#EXT-X-TARGETDURATION":
                    if (target || (targetDuration = UInt(value, 86400)) == 0) throw Invalid();
                    target = media = true; result.AppendLine(line); break;
                case "#EXT-X-MEDIA-SEQUENCE":
                    if (sequenceSeen || segments != 0 ||
                        !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out sequence)) throw Invalid();
                    firstSequence = sequence;
                    sequenceSeen = media = true; result.AppendLine(line); break;
                case "#EXT-X-DISCONTINUITY-SEQUENCE":
                    if (discontinuitySeen || discontinuityTag || segments != 0 ||
                        !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out discontinuity)) throw Invalid();
                    firstDiscontinuity = discontinuity; discontinuitySeen = media = true; result.AppendLine(line); break;
                case "#EXT-X-PLAYLIST-TYPE":
                    if (playlistType is not null) throw Invalid();
                    if (value != "VOD" && !(allowLive && value == "EVENT")) throw Unsupported();
                    playlistType = value;
                    media = true; result.AppendLine(line); break;
                case "#EXT-X-ENDLIST":
                    if (value.Length != 0 || ended) throw Invalid();
                    ended = true; break;
                case "#EXTINF":
                    if (duration is not null) throw Invalid();
                    duration = value.Split(',')[0];
                    if (!decimal.TryParse(duration, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) ||
                        d <= 0 || d > 86400) throw Invalid();
                    media = true; break;
                case "#EXT-X-BYTERANGE":
                    if (range is not null) throw Invalid();
                    range = HlsRange(value); break;
                case "#EXT-X-MAP":
                {
                    var a = Attributes(value); Only(a, "URI", "BYTERANGE");
                    if (currentKey is not null && explicitIV is null) throw Invalid();
                    string uri = Resource(parent, credentials, Required(a, "URI"), AdaptiveResourceKind.Mp4Init,
                        a.TryGetValue("BYTERANGE", out var r) ? HlsRange(r) : null,
                        currentKey is null ? null : new(currentKey, explicitIV!.ToArray()));
                    result.AppendLine("#EXT-X-MAP:URI=\"" + uri + "\"");
                    mapPath = new Uri(uri).AbsolutePath; ownedPaths.Add(mapPath);
                    map = media = true; break;
                }
                case "#EXT-X-PROGRAM-DATE-TIME":
                    if (!allowLive) throw Unsupported();
                    if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
                        !value.Contains('T') || value.Length > 64) throw Invalid();
                    result.AppendLine(line); media = true; break;
                case "#EXT-X-KEY":
                case "#EXT-X-SESSION-KEY":
                {
                    var a = Attributes(value);
                    Only(a, "METHOD", "URI", "IV", "KEYFORMAT", "KEYFORMATVERSIONS");
                    // Resolve even unsupported keys. Never let native code fetch a key/URI itself.
                    if (a.TryGetValue("URI", out var key)) transport.Resolve(parent, credentials, key);
                    if (a.GetValueOrDefault("KEYFORMAT", "identity") != "identity" ||
                        Required(a, "METHOD") is "SAMPLE-AES" or "SAMPLE-AES-CTR")
                        throw new PlaybackException(PlaybackError.AdaptiveDrmUnsupported);
                    if (a["METHOD"] == "NONE")
                    {
                        if (a.Count != 1 || tag != "#EXT-X-KEY") throw Invalid();
                        currentKey = null; explicitIV = null; media = true; break;
                    }
                    if (a["METHOD"] != "AES-128" || a.GetValueOrDefault("KEYFORMATVERSIONS", "1") != "1")
                        throw new PlaybackException(PlaybackError.AdaptiveEncryptionUnsupported);
                    if (transport.IsOffline) throw Invalid(); // Offline archives contain no keys or ciphertext.
                    var resolved = transport.Resolve(parent, credentials, Required(a, "URI"));
                    string identity = (resolved.Credentials ? "1:" : "0:") + resolved.Uri.AbsoluteUri;
                    if (!_keys.TryGetValue(identity, out var ownedKey))
                    {
                        if (_keys.Count >= 256) throw new PlaybackException(PlaybackError.AdaptiveBudget);
                        _keys.Add(identity, ownedKey = new(new(resolved.Uri, resolved.Credentials, AdaptiveResourceKind.IdentityKey)));
                    }
                    byte[]? iv = a.TryGetValue("IV", out var ivText) ? ParseIV(ivText) : null;
                    if (tag == "#EXT-X-SESSION-KEY")
                    {
                        // Preload hints do not implicitly encrypt children; each child declares EXT-X-KEY.
                        if (!sessionKeys.Add(identity + ":" + (iv is null ? "" : Convert.ToHexString(iv)))) throw Invalid();
                        master = true;
                    }
                    else { currentKey = ownedKey; explicitIV = iv; media = true; }
                    break;
                }
                case "#EXT-X-STREAM-INF":
                {
                    if (stream is not null) throw Invalid();
                    var a = Attributes(value);
                    Only(a, "BANDWIDTH", "AVERAGE-BANDWIDTH", "CODECS", "RESOLUTION", "FRAME-RATE", "AUDIO", "CLOSED-CAPTIONS");
                    if (UInt(Required(a, "BANDWIDTH"), 1_000_000_000) == 0) throw Invalid();
                    if (a.TryGetValue("CLOSED-CAPTIONS", out var captions) && captions != "NONE") throw Unsupported();
                    stream = tag + ":" + RenderAttributes(a); master = true; break;
                }
                case "#EXT-X-MEDIA":
                {
                    var a = Attributes(value);
                    Only(a, "TYPE", "URI", "GROUP-ID", "NAME", "LANGUAGE", "DEFAULT", "AUTOSELECT", "CHANNELS");
                    if (Required(a, "TYPE") != "AUDIO") throw Unsupported();
                    Required(a, "GROUP-ID"); Required(a, "NAME");
                    a["URI"] = await ChildAsync(parent, credentials, Required(a, "URI"), depth, token).ConfigureAwait(false);
                    result.AppendLine(tag + ":" + RenderAttributes(a)); master = true; break;
                }
                default:
                    // Unknown EXT/VLC directives are never copied, even if VLC would tolerate them.
                    if (line.StartsWith("#EXT", StringComparison.OrdinalIgnoreCase)) throw Unsupported();
                    break;
            }
        }
        if (stream is not null || duration is not null || range.HasValue || master && media ||
            master && variants == 0 || !master && (!target || segments == 0 ||
                !ended && (!allowLive || playlistType == "VOD"))) throw Unsupported();
        if (!master && !ended)
        {
            if (targetDuration > 60 || segments > 1024) throw new PlaybackException(PlaybackError.AdaptiveBudget);
            if (!sequenceSeen || totalDuration < 3 * targetDuration ||
                items.Any(s => decimal.Round(s.Duration, 0, MidpointRounding.AwayFromZero) > targetDuration))
                throw Invalid();
        }
        if (!master && ended) result.AppendLine("#EXT-X-ENDLIST");
        return (result.ToString(), master ? null : new(firstSequence, firstDiscontinuity,
            targetDuration, playlistType, ended, items, ownedPaths));
    }
    private static byte[] SequenceIV(ulong sequence)
    {
        var iv = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(iv.AsSpan(8), sequence);
        return iv;
    }
    private static byte[] ParseIV(string text)
    {
        if (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || text.Length is < 3 or > 34 ||
            text.AsSpan(2).IndexOfAnyExcept("0123456789abcdefABCDEF") >= 0) throw Invalid();
        return Convert.FromHexString(text[2..].PadLeft(32, '0'));
    }
    private static (long Offset, long Count) HlsRange(string value)
    {
        var pieces = value.Split('@');
        if (pieces.Length != 2) throw Unsupported(); // Implicit ranges are not guessed across resources.
        long count = UInt(pieces[0], 64 * 1024 * 1024), offset = UInt(pieces[1], 1L << 40);
        if (count == 0 || offset > (1L << 40) - count) throw Invalid();
        return (offset, count);
    }
    private static long UInt(string value, long maximum = long.MaxValue)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 0 || n > maximum)
            throw Invalid();
        return n;
    }
    private static Dictionary<string, string> Attributes(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        int position = 0;
        while (position < value.Length)
        {
            int equals = value.IndexOf('=', position);
            if (equals < position + 1) throw Invalid();
            string key = value[position..equals];
            if (key.Any(c => !char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c) && c != '-')) throw Invalid();
            position = equals + 1;
            string item;
            if (position < value.Length && value[position] == '"')
            {
                int close = value.IndexOf('"', ++position);
                if (close < 0) throw Invalid();
                item = value[position..close]; position = close + 1;
            }
            else
            {
                int comma = value.IndexOf(',', position);
                if (comma < 0) comma = value.Length;
                item = value[position..comma]; position = comma;
            }
            if (item.Length == 0 || !result.TryAdd(key, item)) throw Invalid();
            if (position < value.Length && value[position++] != ',') throw Invalid();
            if (position == value.Length && value.EndsWith(',')) throw Invalid();
        }
        return result;
    }
    private static void Only(Dictionary<string, string> attributes, params string[] allowed)
    {
        if (attributes.Keys.Any(k => !allowed.Contains(k, StringComparer.Ordinal))) throw Unsupported();
    }
    private static string Required(Dictionary<string, string> attributes, string key) =>
        attributes.TryGetValue(key, out var value) ? value : throw Invalid();
    private static string RenderAttributes(Dictionary<string, string> attributes) =>
        string.Join(',', attributes.Select(a =>
        {
            if (a.Key != "URI" && (a.Value.Length > 256 ||
                a.Value.Any(c => !char.IsAsciiLetterOrDigit(c) && !" .,_-/".Contains(c)))) throw Invalid();
            bool quoted = a.Key is "URI" or "CODECS" or "AUDIO" or "GROUP-ID" or "NAME" or "LANGUAGE" or "CHANNELS";
            return a.Key + "=" + (quoted ? "\"" + a.Value + "\"" : a.Value);
        }));
}
