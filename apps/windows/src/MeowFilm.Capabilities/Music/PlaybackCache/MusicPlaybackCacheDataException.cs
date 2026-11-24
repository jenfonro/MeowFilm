using System.Text.Json;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.PlaybackCache;

/// <summary>A cache value cannot supply the requested media metadata; handled as a miss.</summary>
internal sealed class MusicPlaybackCacheDataException(StorageError error) : Exception(error.ToString())
{
    internal StorageError Error { get; } = error;

    internal static bool IsCacheFailure(Exception error) => error is
        MusicPlaybackCacheDataException or StorageException or JsonException or
        Library.MusicLibraryException or FormatException or OverflowException or IOException;

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

    internal static void RequireNonEmpty(string? text)
    {
        if (string.IsNullOrEmpty(text))
            throw MusicPlaybackCacheDataException.Corrupt();
    }

    internal static void ValidateIdentity(string scope, string song, string? signature = null)
    {
        RequireNonEmpty(scope);
        RequireNonEmpty(song);
        if (signature is not null) RequireHash(signature);
    }
}
