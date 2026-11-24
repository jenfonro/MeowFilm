using System.Text.Json;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.PlaybackCache;

/// <summary>A cache value cannot supply the requested media metadata; handled as a miss.</summary>
internal sealed class MusicPlaybackCacheDataException(StorageError error) : Exception(error.ToString())
{
    internal StorageError Error { get; } = error;

    internal static bool IsCacheFailure(Exception error) => error is
        MusicPlaybackCacheDataException or StorageException or JsonException or
        Library.MusicLibraryException or InvalidOperationException or FormatException or OverflowException or
        KeyNotFoundException or ArgumentException or NullReferenceException;

    internal static MusicPlaybackCacheDataException Corrupt() => new(StorageError.Corrupt);
    internal static MusicPlaybackCacheDataException Limit() => new(StorageError.Limit);
}

internal static class PlaybackCacheValidation
{
    internal static bool LowerHex(string value, int length) =>
        value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static void RequireHash(string value)
    {
        if (!LowerHex(value, 64)) throw MusicPlaybackCacheDataException.Corrupt();
    }

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
