using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace MeowFilm.Desktop.Artwork;

internal sealed class ArtworkHttp : IDisposable
{
    private readonly HttpClient client;
    internal ArtworkHttp(HttpMessageHandler? handler) =>
        client = new(handler ?? CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };

    internal static Uri Parse(string address)
    {
        if (string.IsNullOrWhiteSpace(address) ||
            !Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            throw new ArtworkException("ARTWORK_ADDRESS");
        return uri;
    }
    private static SocketsHttpHandler CreateHandler() => new()
    {
        UseCookies = false, AllowAutoRedirect = true, MaxAutomaticRedirections = 10,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    };

    internal async Task<byte[]> ReadAsync(Uri uri, CancellationToken token)
    {
        // Metadata keeps the original public image identity; only the transport
        // requests a bounded rendition from NetEase's static image CDN.
        if (uri.Query.Length == 0 && uri.IsDefaultPort &&
            Regex.IsMatch(uri.Host, @"^p[1-4]\.music\.(?:126|163)\.net$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase) &&
            Regex.IsMatch(uri.AbsolutePath, @"^/[A-Za-z0-9_=-]+/[0-9]+\.(?:jpg|jpeg|png|gif)$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
            uri = new UriBuilder(uri) { Query = "param=512y512" }.Uri;
        token.ThrowIfCancellationRequested();
        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        if (uri.UserInfo.Length != 0)
        {
            string userInfo = uri.UserInfo;
            int separator = userInfo.IndexOf(':');
            string username = Uri.UnescapeDataString(separator < 0 ? userInfo : userInfo[..separator]);
            string password = separator < 0 ? "" : Uri.UnescapeDataString(userInfo[(separator + 1)..]);
            message.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password)));
        }
        message.Headers.Accept.ParseAdd("image/png,image/jpeg,image/gif,image/webp");
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        int status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode) throw new ArtworkException(status is 401 or 403 ? "ARTWORK_DENIED" : "ARTWORK_NETWORK");
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        if (bytes.Length == 0) throw new ArtworkException("ARTWORK_FORMAT");
        return bytes;
    }
    public void Dispose() => client.Dispose();
}
