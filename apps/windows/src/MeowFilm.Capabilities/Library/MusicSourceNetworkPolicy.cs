using System.Globalization;
using System.Net;

namespace MeowFilm.Capabilities.Library;

internal static class MusicSourceNetworkPolicy
{
    internal const int MaximumOrigins = 128;

    internal static bool TryFromUrl(string input, out string origin)
    {
        origin = "";
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 ||
            !PublicHost(uri))
            return false;
        try
        {
            var host = new IdnMapping().GetAscii(uri.IdnHost);
            var builder = new UriBuilder(uri.Scheme.ToLowerInvariant(), host)
            {
                Port = uri.IsDefaultPort ? -1 : uri.Port,
                Path = "",
                Query = "",
                Fragment = ""
            };
            origin = builder.Uri.GetLeftPart(UriPartial.Authority);
            return origin.Length <= 2048;
        }
        catch (Exception error) when (error is UriFormatException or ArgumentException)
        {
            return false;
        }
    }

    internal static bool TryCanonical(string input, out string origin) =>
        TryFromUrl(input, out origin) &&
        Uri.TryCreate(input, UriKind.Absolute, out var uri) &&
        uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        StringComparer.Ordinal.Equals(input, origin);

    private static bool PublicHost(Uri uri)
    {
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host is "localhost" or "" || host.EndsWith(".localhost", StringComparison.Ordinal) ||
            host.EndsWith(".local", StringComparison.Ordinal))
            return false;
        if (!IPAddress.TryParse(host, out var address)) return true;
        if (address.IsIPv4MappedToIPv6) return false;
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None) ||
            address.Equals(IPAddress.IPv6None))
            return false;
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
            return bytes[0] != 0 && bytes[0] != 10 && bytes[0] != 127 && bytes[0] < 224 &&
                !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
                !(bytes[0] == 169 && bytes[1] == 254) &&
                !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
                !(bytes[0] == 192 && bytes[1] == 168);
        return bytes[0] != 0xff &&
            (bytes[0] & 0xfe) != 0xfc &&
            !(bytes[0] == 0xfe && (bytes[1] & 0xc0) is 0x80 or 0xc0);
    }
}
