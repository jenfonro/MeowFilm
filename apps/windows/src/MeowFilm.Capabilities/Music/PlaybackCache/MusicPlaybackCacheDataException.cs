using System.Text.Json;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.PlaybackCache;

/// <summary>A cache value cannot supply the requested media metadata; handled as a miss.</summary>
internal sealed class MusicPlaybackCacheDataException(DocumentStoreError error) : Exception(error.ToString())
{
    internal static bool IsCacheFailure(Exception error) => error is
        MusicPlaybackCacheDataException or DocumentStoreException or JsonException or
        Library.MusicLibraryException or InvalidOperationException or FormatException or OverflowException or
        KeyNotFoundException or ArgumentException or NullReferenceException;

    internal static MusicPlaybackCacheDataException Corrupt() => new(DocumentStoreError.Corrupt);
    internal static MusicPlaybackCacheDataException Limit() => new(DocumentStoreError.Limit);
}

internal static class PlaybackCacheValidation
{
    internal static bool LowerHex(string value, int length) =>
        value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static void RequireHash(string value)
    {
        if (!LowerHex(value, 64)) throw MusicPlaybackCacheDataException.Corrupt();
    }

    internal static long CanonicalLong(JsonElement value, bool allowZero = false)
    {
        if (!value.TryGetInt64(out long result) || (allowZero ? result < 0 : result <= 0))
            throw MusicPlaybackCacheDataException.Corrupt();
        return result;
    }

    internal static long? OptionalLong(JsonElement value, bool allowZero = false) =>
        value.ValueKind == JsonValueKind.Null ? null : CanonicalLong(value, allowZero);

    internal static double CanonicalDouble(JsonElement value, bool allowZero = false)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double result) ||
            !double.IsFinite(result) || (allowZero ? result < 0 : result <= 0))
            throw MusicPlaybackCacheDataException.Corrupt();
        return result;
    }

    internal static bool Boolean(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw MusicPlaybackCacheDataException.Corrupt()
    };

    internal static string Text(JsonElement value, int maximum, bool empty = false)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } result)
            throw MusicPlaybackCacheDataException.Corrupt();
        RequireText(result, maximum, empty);
        return result;
    }

    internal static string? OptionalText(JsonElement value, int maximum, bool empty = false) =>
        value.ValueKind == JsonValueKind.Null ? null : Text(value, maximum, empty);

    internal static void RequireText(string text, int maximum, bool empty = false)
    {
        if (text.Length > maximum || !empty && text.Length == 0)
            throw MusicPlaybackCacheDataException.Corrupt();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c < 32 || c == 127 || char.IsLowSurrogate(c))
                throw MusicPlaybackCacheDataException.Corrupt();
            if (char.IsHighSurrogate(c) &&
                (++i >= text.Length || !char.IsLowSurrogate(text[i])))
                throw MusicPlaybackCacheDataException.Corrupt();
        }
    }

    internal static void ValidateIdentity(string scope, string song, string? signature = null)
    {
        RequireText(scope, 128);
        RequireText(song, 2048);
        if (signature is not null) RequireHash(signature);
    }
}
