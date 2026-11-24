using System.Net.Http;

namespace MeowFilm.Media;

internal static class NativeHttpMediaPolicy
{
    private static readonly HashSet<string> TransportManagedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Expect",
        "Range", "Accept-Encoding", "If-Range", "Content-Length"
    };
    private static readonly string[] AdaptiveExtensions = [".m3u8", ".m3u", ".mpd", ".ism", ".isml"];

    internal static Dictionary<string, string> ValidateRequest(Uri uri,
        IReadOnlyDictionary<string, string> headers, bool allowAdaptive = false)
    {
        NativeHttpMediaOptions.ValidateSyntax(uri);
        if (!allowAdaptive) RejectAdaptivePath(uri);
        if (headers is null) throw new PlaybackException(PlaybackError.InvalidHeaders);
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            if (string.IsNullOrEmpty(name) || value is null || TransportManagedHeaders.Contains(name))
                continue;
            copy[name] = value;
        }
        return copy;
    }

    internal static void RejectAdaptivePath(Uri uri)
    {
        string path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (AdaptiveExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            throw new PlaybackException(PlaybackError.UnsupportedAdaptive);
    }

    internal static string Classify(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is PlaybackException known) return known.Code;
        return error switch
        {
            UriFormatException => PlaybackError.UnsupportedUri,
            OperationCanceledException => PlaybackError.HttpDeadline,
            HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => PlaybackError.HttpTlsFailed,
            HttpRequestException { HttpRequestError: HttpRequestError.ResponseEnded } => PlaybackError.HttpTruncated,
            HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded } => PlaybackError.HttpTruncated,
            _ => PlaybackError.HttpNetworkFailed
        };
    }
}
