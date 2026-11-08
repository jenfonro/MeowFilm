using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace MeowFilm.Desktop.Artwork;

internal sealed class ArtworkHttp : IDisposable
{
    private readonly HttpClient client;
    internal ArtworkHttp(HttpMessageHandler? handler) =>
        client = new(handler ?? CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };

    internal static Uri Parse(string address)
    {
        if (address.Length is 0 or > 8192 || address.Any(c => char.IsControl(c)) ||
            !Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Port is not (80 or 443) ||
            uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6) ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("local", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(uri.DnsSafeHost.Trim('[', ']'), out var ip) && !PublicAddress(ip))
            throw new ArtworkException("ARTWORK_ADDRESS");
        return uri;
    }
    private static bool PublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] is not (0 or 10 or 127) && b[0] < 224 &&
                !(b[0] == 100 && b[1] is >= 64 and <= 127) &&
                !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31) &&
                !(b[0] == 192 && (b[1] is 0 or 168 || b[1] == 88 && b[2] == 99)) &&
                !(b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100)) &&
                !(b[0] == 203 && b[1] == 0 && b[2] == 113);
        return b.Length == 16 && (b[0] & 0xe0) == 0x20 &&
            !(b[0] == 0x20 && b[1] == 1 && b[2] < 2) &&
            !(b[0] == 0x20 && b[1] == 1 && b[2] == 0x0d && b[3] == 0xb8) &&
            !(b[0] == 0x20 && b[1] == 2);
    }
    private static SocketsHttpHandler CreateHandler() => new()
    {
        UseCookies = false, UseProxy = false, AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10), MaxResponseHeadersLength = 32,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = async (context, token) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
            if (addresses.Length == 0 || addresses.Any(ip => !PublicAddress(ip))) throw new ArtworkException("ARTWORK_ADDRESS");
            foreach (var address in addresses.Take(8))
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (OperationCanceledException) { socket.Dispose(); throw; }
                catch { socket.Dispose(); }
            }
            throw new ArtworkException("ARTWORK_NETWORK");
        }
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
            message.Headers.Accept.ParseAdd("image/png,image/jpeg,image/gif,image/webp");
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                if (hop == 5 || response.Headers.Location is not { } location) throw new ArtworkException("ARTWORK_REDIRECT");
                Uri next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                _ = Parse(next.AbsoluteUri);
                if (uri.Scheme == "https" && next.Scheme != "https") throw new ArtworkException("ARTWORK_REDIRECT");
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
