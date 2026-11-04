using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using MeowFilm.Platform.Files;

namespace MeowFilm.Capabilities;

internal sealed class MusicSourceNetworkDownload : IDisposable
{
    private readonly HttpClient client;

    internal MusicSourceNetworkDownload(HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler { AllowAutoRedirect = false };
        client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal async Task<(byte[] Bytes, string Basename)> ReadAsync(string address, CancellationToken token)
    {
        if (!TryAddress(address, out var uri, out var basename))
            throw new MusicOperationException("INVALID_SOURCE_URL");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            if ((int)response.StatusCode is < 200 or >= 300 ||
                response.RequestMessage?.RequestUri is { } finalUri && finalUri != uri)
                throw new MusicOperationException("SOURCE_DOWNLOAD_FAILED");
            if (response.Content.Headers.ContentLength is > SelectedScriptSnapshot.MaximumBytes)
                throw new MusicOperationException("LIMIT");

            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var output = new MemoryStream(response.Content.Headers.ContentLength is { } length
                ? checked((int)length) : 0);
            byte[] buffer = new byte[16 * 1024];
            try
            {
                while (true)
                {
                    int read = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    if (output.Length + read > SelectedScriptSnapshot.MaximumBytes)
                        throw new MusicOperationException("LIMIT");
                    output.Write(buffer, 0, read);
                }
                deadline.Token.ThrowIfCancellationRequested();
                if (output.Length == 0 || response.Content.Headers.ContentLength is { } expected && output.Length != expected)
                    throw new MusicOperationException("SOURCE_DOWNLOAD_FAILED");
                return (output.ToArray(), basename);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
                if (output.TryGetBuffer(out var stored)) CryptographicOperations.ZeroMemory(stored);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new MusicOperationException("TIMEOUT"); }
        catch (Exception error) when (error is HttpRequestException or IOException)
        { throw new MusicOperationException("SOURCE_DOWNLOAD_FAILED"); }
    }

    internal static bool TryAddress(string address, out Uri uri, out string basename)
    {
        uri = null!;
        basename = "";
        address = address.Trim();
        if (address.Length is 0 or > 2048 || address.Any(char.IsControl) ||
            !Uri.TryCreate(address, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps || parsed.Host.Length == 0 || parsed.UserInfo.Length != 0 ||
            parsed.Fragment.Length != 0 ||
            !parsed.AbsolutePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            return false;
        basename = Uri.UnescapeDataString(Path.GetFileName(parsed.AbsolutePath));
        if (basename.Length is 0 or > 255 ||
            !basename.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            basename.Any(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c)))
            return false;
        uri = parsed;
        return true;
    }

    public void Dispose() => client.Dispose();
}
