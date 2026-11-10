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
        if (address.Length == 0 || address.Any(c => char.IsControl(c)) ||
            !Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.Fragment.Length != 0 ||
            uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
            throw new ArtworkException("ARTWORK_ADDRESS");
        return uri;
    }
    private static SocketsHttpHandler CreateHandler() => new()
    {
        UseCookies = false, UseProxy = false, AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10), MaxResponseHeadersLength = 32,
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
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (int hop = 0; hop <= 5; hop++)
        {
            token.ThrowIfCancellationRequested(); _ = Parse(uri.AbsoluteUri);
            if (!visited.Add(uri.AbsoluteUri)) throw new ArtworkException("ARTWORK_REDIRECT");
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
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                if (hop == 5 || response.Headers.Location is not { } location) throw new ArtworkException("ARTWORK_REDIRECT");
                Uri next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                _ = Parse(next.AbsoluteUri);
                uri = next; continue;
            }
            if (!response.IsSuccessStatusCode) throw new ArtworkException(status is 401 or 403 ? "ARTWORK_DENIED" : "ARTWORK_NETWORK");
            if (response.Content.Headers.ContentEncoding.Count != 0) throw new ArtworkException("ARTWORK_FORMAT");
            long? expected = response.Content.Headers.ContentLength;
            if (expected is > ArtworkService.MaximumBytes or 0) throw new ArtworkException("ARTWORK_LIMIT");
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            byte[] buffer = new byte[32768];
            try
            {
                for (;;)
                {
                    int count = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (count == 0) break;
                    if (bytes.Length + count > ArtworkService.MaximumBytes) throw new ArtworkException("ARTWORK_LIMIT");
                    bytes.Write(buffer, 0, count);
                }
                token.ThrowIfCancellationRequested();
                if (bytes.Length == 0 || expected is { } length && length != bytes.Length) throw new ArtworkException("ARTWORK_FORMAT");
                return bytes.ToArray();
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
                if (bytes.TryGetBuffer(out var view)) System.Security.Cryptography.CryptographicOperations.ZeroMemory(view);
            }
        }
        throw new ArtworkException("ARTWORK_REDIRECT");
    }
    public void Dispose() => client.Dispose();
}
