namespace MeowFilm.Media;

/// <summary>Stable, non-sensitive contract v1 codes. Native messages are never exposed.</summary>
public static class PlaybackError
{
    public const string InvalidRequest = "MEDIA_INVALID_REQUEST";
    public const string UnsupportedUri = "MEDIA_UNSUPPORTED_URI";
    public const string UriCredentials = "MEDIA_URI_CREDENTIALS";
    public const string UnsupportedHeaders = "MEDIA_UNSUPPORTED_HEADERS";
    public const string InvalidHeaders = "MEDIA_INVALID_HEADERS";
    public const string UnsupportedAdaptive = "MEDIA_UNSUPPORTED_ADAPTIVE_HEADERS";
    public const string AdaptiveInvalid = "MEDIA_ADAPTIVE_INVALID";
    public const string AdaptiveUnsupported = "MEDIA_ADAPTIVE_VARIANT_UNSUPPORTED";
    public const string AdaptiveDrmUnsupported = "MEDIA_ADAPTIVE_DRM_UNSUPPORTED";
    public const string AdaptiveEncryptionUnsupported = "MEDIA_ADAPTIVE_ENCRYPTION_UNSUPPORTED";
    public const string AdaptiveBudget = "MEDIA_ADAPTIVE_BUDGET";
    public const string HttpAddressBlocked = "MEDIA_HTTP_ADDRESS_BLOCKED";
    public const string HttpRedirectBlocked = "MEDIA_HTTP_REDIRECT_BLOCKED";
    public const string HttpRedirectFailed = "MEDIA_HTTP_REDIRECT_FAILED";
    public const string HttpInvalidRange = "MEDIA_HTTP_INVALID_RANGE";
    public const string HttpRangeRejected = "MEDIA_HTTP_RANGE_REJECTED";
    public const string HttpRepresentationChanged = "MEDIA_HTTP_REPRESENTATION_CHANGED";
    public const string HttpEncodingUnsupported = "MEDIA_HTTP_ENCODING_UNSUPPORTED";
    public const string HttpFormatUnsupported = "MEDIA_HTTP_FORMAT_UNSUPPORTED";
    public const string HttpDeadline = "MEDIA_HTTP_DEADLINE";
    public const string HttpUnauthorized = "MEDIA_HTTP_UNAUTHORIZED";
    public const string HttpNotFound = "MEDIA_HTTP_NOT_FOUND";
    public const string HttpExpired = "MEDIA_HTTP_EXPIRED";
    public const string HttpThrottled = "MEDIA_HTTP_THROTTLED";
    public const string HttpServerFailed = "MEDIA_HTTP_SERVER_FAILED";
    public const string HttpStatusFailed = "MEDIA_HTTP_STATUS_FAILED";
    public const string HttpTlsFailed = "MEDIA_HTTP_TLS_FAILED";
    public const string HttpNetworkFailed = "MEDIA_HTTP_NETWORK_FAILED";
    public const string HttpTruncated = "MEDIA_HTTP_TRUNCATED";
    public const string InvalidReadLease = "MEDIA_INVALID_READ_LEASE";
    public const string InvalidResourceLease = "MEDIA_INVALID_RESOURCE_LEASE";
    public const string ResourceRevoked = "MEDIA_RESOURCE_REVOKED";
    public const string ReadFormatUnsupported = "MEDIA_READ_FORMAT_UNSUPPORTED";
    public const string ReadRevoked = "MEDIA_READ_REVOKED";
    public const string ReadFailed = "MEDIA_READ_FAILED";
    public const string ReadTruncated = "MEDIA_READ_TRUNCATED";
    public const string ReadChanged = "MEDIA_READ_CHANGED";
    public const string ReadSeekFailed = "MEDIA_READ_SEEK_FAILED";
    public const string InvalidSeek = "MEDIA_INVALID_SEEK";
    public const string NotSeekable = "MEDIA_NOT_SEEKABLE";
    public const string NotPausable = "MEDIA_NOT_PAUSABLE";
    public const string InvalidTrack = "MEDIA_INVALID_TRACK";
    public const string TrackMetadataInvalid = "MEDIA_TRACK_METADATA_INVALID";
    public const string TrackSelectionFailed = "MEDIA_TRACK_SELECTION_FAILED";
    public const string InvalidRate = "MEDIA_INVALID_RATE";
    public const string RateUnsupported = "MEDIA_RATE_UNSUPPORTED";
    public const string InvalidSubtitleDelay = "MEDIA_INVALID_SUBTITLE_DELAY";
    public const string SubtitleUnavailable = "MEDIA_SUBTITLE_UNAVAILABLE";
    public const string NativeUnavailable = "MEDIA_NATIVE_UNAVAILABLE";
    public const string OpenFailed = "MEDIA_OPEN_FAILED";
    public const string PlaybackFailed = "MEDIA_PLAYBACK_FAILED";
    public const string CommandFailed = "MEDIA_COMMAND_FAILED";
    public const string CallbackFailed = "MEDIA_CALLBACK_FAILED";
    public const string ReleaseFailed = "MEDIA_RELEASE_FAILED";
    public const string DispatcherUnavailable = "MEDIA_DISPATCHER_UNAVAILABLE";
    public const string WrongThread = "MEDIA_WRONG_THREAD";
    public const string Disposed = "MEDIA_DISPOSED";
    public const string Superseded = "MEDIA_SUPERSEDED";
}

public sealed class PlaybackException : Exception
{
    public string Code { get; }

    internal PlaybackException(string code) : base(code) => Code = code;
}

/// <summary>Dummy is solely for native code fixtures, not audible-output verification.</summary>
public enum NativeAudioOutput
{
    SystemDefault,
    Dummy
}
