using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace MeowFilm.Media;

/// <summary>HTTP resource deadlines and redirect budget for native media input.</summary>
public sealed class NativeHttpMediaOptions
{
    public TimeSpan RequestTimeout { get; }
    public TimeSpan ReadTimeout { get; }
    public int MaximumRedirects { get; }

    public NativeHttpMediaOptions(TimeSpan? requestTimeout = null, TimeSpan? readTimeout = null,
        int maximumRedirects = 5)
    {
        RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        ReadTimeout = readTimeout ?? TimeSpan.FromSeconds(15);
        if (RequestTimeout < TimeSpan.FromMilliseconds(100) || RequestTimeout > TimeSpan.FromMinutes(2) ||
            ReadTimeout < TimeSpan.FromMilliseconds(100) || ReadTimeout > TimeSpan.FromMinutes(2) ||
            maximumRedirects is < 0 or > 10)
            throw new PlaybackException(PlaybackError.InvalidRequest);
        MaximumRedirects = maximumRedirects;
    }

    internal static void ValidateSyntax(Uri uri)
    {
        if (uri is null || !uri.IsAbsoluteUri ||
            uri.Scheme is not ("http" or "https") ||
            uri.Fragment.Length != 0 || uri.IdnHost.Length == 0)
            throw new PlaybackException(PlaybackError.UnsupportedUri);
    }

    internal static void ApplyUserInfo(HttpRequestMessage request)
    {
        if (request.Headers.Contains("Authorization") || request.RequestUri!.UserInfo.Length == 0) return;
        string userInfo = request.RequestUri.UserInfo;
        int separator = userInfo.IndexOf(':');
        string username = Uri.UnescapeDataString(separator < 0 ? userInfo : userInfo[..separator]);
        string password = separator < 0 ? "" : Uri.UnescapeDataString(userInfo[(separator + 1)..]);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password)));
    }

    internal HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Credentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxResponseHeadersLength = 32,
            MaxResponseDrainSize = 0,
            ConnectTimeout = RequestTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
