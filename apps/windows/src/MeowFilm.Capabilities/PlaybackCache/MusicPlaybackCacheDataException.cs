using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.PlaybackCache;

/// <summary>A cache value cannot supply the requested media metadata; handled as a miss.</summary>
internal sealed class MusicPlaybackCacheDataException(StorageError error) : Exception(error.ToString())
{
    internal StorageError Error { get; } = error;

    internal static bool IsCacheFailure(Exception error) => error is
        MusicPlaybackCacheDataException or StorageException or System.Text.Json.JsonException or
        Library.MusicLibraryException or InvalidOperationException or FormatException or OverflowException or
        KeyNotFoundException or ArgumentException or NullReferenceException;

    internal static MusicPlaybackCacheDataException Corrupt() => new(StorageError.Corrupt);
    internal static MusicPlaybackCacheDataException Limit() => new(StorageError.Limit);
}
