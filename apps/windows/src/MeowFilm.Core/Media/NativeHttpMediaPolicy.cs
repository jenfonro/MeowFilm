using System.Net.Http;

namespace MeowFilm.Media;

internal static class NativeHttpMediaPolicy
{
    internal static Dictionary<string, string> ValidateRequest(Uri uri,
        IReadOnlyDictionary<string, string> headers, bool allowAdaptive = false)
    {
        NativeHttpMediaOptions.ValidateSyntax(uri);
        if (!allowAdaptive) RejectAdaptivePath(uri);
        if (headers is null || headers.Count > 32) throw new PlaybackException(PlaybackError.InvalidHeaders);
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int size = 0;
        foreach (var (name, value) in headers)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 128 || value is null || value.Length > 8192 ||
                name.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c)) ||
                value.Any(c => c < 0x20 || c > 0x7e) ||
                !copy.TryAdd(name, value) || (size += name.Length + value.Length) > 16384)
                throw new PlaybackException(PlaybackError.InvalidHeaders);
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Range", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("If-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Sec-", StringComparison.OrdinalIgnoreCase) ||
                new[] { "Connection", "Keep-Alive", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Expect" }
                    .Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new PlaybackException(PlaybackError.UnsupportedHeaders);
        }
        return copy;
    }

    internal static void RejectAdaptivePath(Uri uri)
    {
        string path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (new[] { ".m3u8", ".m3u", ".mpd", ".ism", ".isml" }
            .Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            throw new PlaybackException(PlaybackError.UnsupportedAdaptive);
    }

    internal static string Classify(Exception error)
    {
        // Transport exceptions can be wrapped by HttpClient. Only extract our fixed code;
        // no URI/response/header/exception body or inner exception is published.
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is PlaybackException known) return known.Code;
        return error switch
        {
            OperationCanceledException => PlaybackError.HttpDeadline,
            HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => PlaybackError.HttpTlsFailed,
            HttpRequestException { HttpRequestError: HttpRequestError.ResponseEnded } => PlaybackError.HttpTruncated,
            HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded } => PlaybackError.HttpTruncated,
            _ => PlaybackError.HttpNetworkFailed
        };
    }

}
