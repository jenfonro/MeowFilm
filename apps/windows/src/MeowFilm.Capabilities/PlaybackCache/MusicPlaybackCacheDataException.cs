using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.PlaybackCache;

/// <summary>A cache value cannot supply the requested media metadata; handled as a miss.</summary>
internal sealed class MusicPlaybackCacheDataException(DocumentStoreError error) : Exception(error.ToString())
{
    internal DocumentStoreError Error { get; } = error;

    internal static bool IsCacheFailure(Exception error) => error is
        MusicPlaybackCacheDataException or DocumentStoreException or System.Text.Json.JsonException or
        Library.MusicLibraryException or InvalidOperationException or FormatException or OverflowException or
        KeyNotFoundException or ArgumentException or NullReferenceException;

    internal static MusicPlaybackCacheDataException Corrupt() => new(DocumentStoreError.Corrupt);
    internal static MusicPlaybackCacheDataException Limit() => new(DocumentStoreError.Limit);
}
