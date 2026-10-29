using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.PlaybackCache;

/// <summary>
/// The protected document exists but is not the single cache format supported by
/// this development build. Callers must surface the failure and leave it untouched.
/// </summary>
internal sealed class MusicPlaybackCacheDataException(DocumentStoreError error) : Exception(error.ToString())
{
    internal DocumentStoreError Error { get; } = error;

    internal static bool IsStoredFormatFailure(ProtectedDocumentException error) =>
        error.Error is DocumentStoreError.Corrupt or DocumentStoreError.UnsupportedVersion or
            DocumentStoreError.Limit or DocumentStoreError.Protection;

    internal static bool IsOptionalStoreFailure(ProtectedDocumentException error) =>
        error.Error is DocumentStoreError.Unavailable or DocumentStoreError.Conflict or
            DocumentStoreError.Storage or DocumentStoreError.Closed;

    internal static MusicPlaybackCacheDataException FromStore(ProtectedDocumentException error) =>
        new(error.Error);

    internal static MusicPlaybackCacheDataException Corrupt() => new(DocumentStoreError.Corrupt);
    internal static MusicPlaybackCacheDataException UnsupportedVersion() => new(DocumentStoreError.UnsupportedVersion);
    internal static MusicPlaybackCacheDataException Limit() => new(DocumentStoreError.Limit);
}

/// <summary>The store admitted a cache CAS but returned no matching durable receipt.</summary>
internal sealed class MusicPlaybackCacheCommitException : Exception
{
}
