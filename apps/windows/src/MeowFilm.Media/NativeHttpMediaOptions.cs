using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace MeowFilm.Media;

/// <summary>
/// Trusted native-host configuration, never script supplied. Production accepts public HTTP(S)
/// endpoints only. Explicit revocable resource leases permit one registered loopback origin.
/// Tests must separately opt in to individual literal-loopback origins.
/// </summary>
public sealed class NativeHttpMediaOptions
{
    public TimeSpan RequestTimeout { get; }
    public TimeSpan ReadTimeout { get; }
    public int MaximumRedirects { get; }
    private readonly HashSet<string>? _testOrigins;
    private readonly MediaResourceSession? _resource;

    public NativeHttpMediaOptions(TimeSpan? requestTimeout = null, TimeSpan? readTimeout = null,
        int maximumRedirects = 5) : this(requestTimeout, readTimeout, maximumRedirects, null) { }

    private NativeHttpMediaOptions(TimeSpan? requestTimeout, TimeSpan? readTimeout,
        int maximumRedirects, HashSet<string>? testOrigins, MediaResourceSession? resource = null)
    {
        RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        ReadTimeout = readTimeout ?? TimeSpan.FromSeconds(15);
        if (RequestTimeout < TimeSpan.FromMilliseconds(100) || RequestTimeout > TimeSpan.FromMinutes(2) ||
            ReadTimeout < TimeSpan.FromMilliseconds(100) || ReadTimeout > TimeSpan.FromMinutes(2) ||
            maximumRedirects is < 0 or > 10)
            throw new PlaybackException(PlaybackError.InvalidRequest);
        MaximumRedirects = maximumRedirects;
        _testOrigins = testOrigins;
        _resource = resource;
    }

    // Not a public address-policy escape hatch. Only OpenResourceAsync can construct
    // this policy from a validated, owned lease; test origins are never inherited.
    internal static NativeHttpMediaOptions ForResource(MediaResourceSession resource,
        NativeHttpMediaOptions? defaults = null) =>
        new(defaults?.RequestTimeout, defaults?.ReadTimeout, defaults?.MaximumRedirects ?? 5, null, resource);

    /// <summary>
    /// Restricted test transport: ONLY these literal-loopback HTTP(S) origins, no public hosts.
    /// Ports are part of the allowlist. This is not a production LAN/proxy exception.
    /// </summary>
    public static NativeHttpMediaOptions ForLoopbackTests(IEnumerable<Uri> origins,
        TimeSpan? requestTimeout = null, TimeSpan? readTimeout = null, int maximumRedirects = 5)
    {
        if (origins is null) throw new PlaybackException(PlaybackError.InvalidRequest);
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var uri in origins)
        {
            ValidateSyntax(uri);
            if (!IPAddress.TryParse(uri.IdnHost, out var address) || !IPAddress.IsLoopback(address) ||
                uri.AbsolutePath != "/" || uri.Query.Length != 0 || allowed.Count >= 16)
                throw new PlaybackException(PlaybackError.InvalidRequest);
            allowed.Add(Origin(uri));
        }
        if (allowed.Count == 0) throw new PlaybackException(PlaybackError.InvalidRequest);
        return new(requestTimeout, readTimeout, maximumRedirects, allowed);
    }

    internal void Validate(Uri uri)
    {
        ValidateSyntax(uri);
        if (_resource is not null)
        {
            _resource.Authorize(uri);
            if (ResourceOrigin(uri)) return;
            // Public children still pass the production DNS/address rules below.
            // A capability cannot admit another local service, even if its adapter errs.
        }
        if (_testOrigins is not null)
        {
            if (!_testOrigins.Contains(Origin(uri)))
                throw new PlaybackException(PlaybackError.HttpAddressBlocked);
        }
        else if (uri.IsLoopback || !uri.IdnHost.Contains('.') && !uri.IdnHost.Contains(':') ||
                 uri.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
                 IPAddress.TryParse(uri.IdnHost, out var address) && !IsPublicAddress(address))
            throw new PlaybackException(PlaybackError.HttpAddressBlocked);
    }

    private bool ResourceOrigin(Uri uri) => _resource is not null &&
        uri.Scheme == "http" && uri.IdnHost == "127.0.0.1" &&
        uri.Port == _resource.Address.Port;

    internal static void ValidateSyntax(Uri uri)
    {
        if (uri is null || !uri.IsAbsoluteUri ||
            uri.Scheme is not ("http" or "https") || uri.AbsoluteUri.Length > 16384 ||
            uri.Fragment.Length != 0 || uri.IdnHost.Length == 0)
            throw new PlaybackException(PlaybackError.UnsupportedUri);
        if (uri.UserInfo.Length != 0) throw new PlaybackException(PlaybackError.UriCredentials);
    }

    internal static string Origin(Uri uri) => $"{uri.Scheme}://{uri.IdnHost.ToLowerInvariant()}:{uri.Port}";

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
            MaxConnectionsPerServer = 1,
            MaxResponseHeadersLength = 32,
            MaxResponseDrainSize = 0,
            ConnectTimeout = RequestTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectAsync
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private async ValueTask<System.IO.Stream> ConnectAsync(SocketsHttpConnectionContext context,
        CancellationToken token)
    {
        var uri = context.InitialRequestMessage.RequestUri!;
        Validate(uri);
        var addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
        // Validate ALL DNS answers, then connect to that exact address, not the name a second time.
        // Mixed public/private answers are rejected; no fallback to an unchecked resolver/proxy.
        if (addresses.Length == 0 || addresses.Length > 32 ||
            addresses.Any(address => ResourceOrigin(uri)
                ? !address.Equals(IPAddress.Loopback)
                : _testOrigins is null ? !IsPublicAddress(address) : !IPAddress.IsLoopback(address)))
            throw new PlaybackException(PlaybackError.HttpAddressBlocked);
        foreach (var address in addresses)
        {
            token.ThrowIfCancellationRequested();
            Validate(uri); // Recheck capability after DNS/admission and before opening a socket.
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new PlaybackException(PlaybackError.HttpNetworkFailed);
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224 ||
                b[0] == 100 && b[1] is >= 64 and <= 127 ||
                b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31 ||
                b[0] == 192 && (b[1] is 0 or 168 || b[1] == 88 && b[2] == 99) ||
                b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100) ||
                b[0] == 203 && b[1] == 0 && b[2] == 113);
        }
        // Fail closed outside global unicast; also reject transition/tunnel, documentation,
        // benchmarking and protocol-assignment space. No NAT64/6to4 escape to a private IPv4.
        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId == 0 &&
            (b[0] & 0xe0) == 0x20 &&
            !(b[0] == 0x20 && b[1] == 0x02) &&
            !(b[0] == 0x20 && b[1] == 0x01 && (b[2] < 2 || b[2] == 0x0d && b[3] == 0xb8)) &&
            !(b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xf0) == 0);
    }
}
