using System.Security.Cryptography;

namespace MeowFilm.Media;

public enum NativeVideoFormat { Auto, Progressive, Hls, Dash }

/// <summary>Legal ClearKey material only; no license URL, HTTP headers or CDM protocol.</summary>
public sealed class NativeClearKeySet : IDisposable
{
    private readonly object _gate = new();
    private byte[]? _pairs;
    private byte[]? _defaultKey;
    private bool _consumed;

    private NativeClearKeySet(byte[] pairs, byte[]? defaultKey)
    { _pairs = pairs; _defaultKey = defaultKey; }

    /// <summary>KID/key are 128-bit hex. KIDs may also use canonical UUID hyphens.</summary>
    public static NativeClearKeySet FromHex(IReadOnlyDictionary<string, string> keysByKid,
        string? defaultKey = null)
    {
        if (keysByKid is null || keysByKid.Count == 0 && defaultKey is null)
            throw new PlaybackException(PlaybackError.InvalidRequest);
        var pairs = new byte[checked(keysByKid.Count * 32)];
        byte[]? fallback = null;
        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int offset = 0;
            foreach (var (kid, key) in keysByKid)
            {
                var id = Decode(kid, uuid: true);
                byte[]? value = null;
                try
                {
                    value = Decode(key, uuid: false);
                    if (!seen.Add(Convert.ToHexString(id)))
                        throw new PlaybackException(PlaybackError.InvalidRequest);
                    id.CopyTo(pairs, offset); value.CopyTo(pairs, offset + 16);
                    offset += 32;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(id);
                    if (value is not null) CryptographicOperations.ZeroMemory(value);
                }
            }
            if (defaultKey is not null) fallback = Decode(defaultKey, uuid: false);
            return new(pairs, fallback);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(pairs);
            if (fallback is not null) CryptographicOperations.ZeroMemory(fallback);
            throw;
        }
    }

    private static byte[] Decode(string text, bool uuid)
    {
        if (text is null) throw new PlaybackException(PlaybackError.InvalidRequest);
        if (uuid && text.Length == 36 && text[8] == '-' && text[13] == '-' &&
            text[18] == '-' && text[23] == '-') text = text.Replace("-", "");
        if (text.Length != 32 || text.Any(c => !char.IsAsciiHexDigit(c)))
            throw new PlaybackException(PlaybackError.InvalidRequest);
        return Convert.FromHexString(text);
    }

    internal (byte[] Pairs, byte[]? Default) Take()
    {
        lock (_gate)
        {
            if (_consumed) throw new PlaybackException(PlaybackError.InvalidRequest);
            _consumed = true;
            var result = (_pairs!, _defaultKey);
            _pairs = null; _defaultKey = null;
            return result;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _consumed = true;
            if (_pairs is not null) CryptographicOperations.ZeroMemory(_pairs);
            if (_defaultKey is not null) CryptographicOperations.ZeroMemory(_defaultKey);
            _pairs = null; _defaultKey = null;
        }
    }
    public override string ToString() => nameof(NativeClearKeySet);
}

/// <summary>Single-use typed native input. Prepare takes ownership immediately, even on rejection.</summary>
public sealed class NativeVideoInput : IDisposable
{
    private readonly object _gate = new();
    private NativeClearKeySet? _keys;
    private bool _consumed;
    public NativeVideoFormat Format { get; }

    public NativeVideoInput(NativeVideoFormat format = NativeVideoFormat.Auto,
        NativeClearKeySet? clearKeys = null)
    {
        if (!Enum.IsDefined(format)) throw new PlaybackException(PlaybackError.InvalidRequest);
        Format = format; _keys = clearKeys;
    }

    internal (byte[] Pairs, byte[]? Default) Take()
    {
        lock (_gate)
        {
            if (_consumed) throw new PlaybackException(PlaybackError.InvalidRequest);
            _consumed = true;
            try { return _keys?.Take() ?? ([], null); }
            finally { _keys?.Dispose(); _keys = null; }
        }
    }

    public void Dispose()
    {
        lock (_gate) { _consumed = true; _keys?.Dispose(); _keys = null; }
    }
    public override string ToString() => $"{nameof(NativeVideoInput)}({Format})";
}
