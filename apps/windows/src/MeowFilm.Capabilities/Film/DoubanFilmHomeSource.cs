using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace MeowFilm.Capabilities;

internal interface IFilmHomeSource : IDisposable
{
    Task<IReadOnlyList<FilmCardItem>> LoadAsync(
        FilmSectionDefinition section,
        int start,
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FilmCardItem>> LoadAsync(
        FilmSectionDefinition section,
        CancellationToken cancellationToken) =>
        LoadAsync(section, 0, 12, cancellationToken);
}

internal sealed class FilmHomeException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}

internal sealed class DoubanFilmHomeSource : IFilmHomeSource
{
    internal const int MaximumResponseBytes = 4 * 1024 * 1024;
    private static readonly Uri Referer = new("https://m.douban.com/");
    private readonly HttpClient client;
    private readonly TimeSpan timeout;
    private bool disposed;

    internal DoubanFilmHomeSource()
        : this(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate |
                DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 4,
            MaxResponseHeadersLength = 32
        })
    {
    }

    internal DoubanFilmHomeSource(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        this.timeout = timeout ?? TimeSpan.FromSeconds(15);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        client = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<IReadOnlyList<FilmCardItem>> LoadAsync(
        FilmSectionDefinition section,
        int start,
        int limit,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(section);
        if (!FilmDataFilters.IsAllowedRequest(section))
            throw new ArgumentException("Unknown film section.", nameof(section));
        if (start < 0 || start > 10000) throw new ArgumentOutOfRangeException(nameof(start));
        if (limit < 1 || limit > 24) throw new ArgumentOutOfRangeException(nameof(limit));
        cancellationToken.ThrowIfCancellationRequested();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildAddress(section, start, limit));
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");
        request.Headers.Referrer = Referer;
        request.Headers.TryAddWithoutValidation("Origin", "https://m.douban.com");

        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (response.StatusCode != HttpStatusCode.OK)
                throw new FilmHomeException((int)response.StatusCode is >= 300 and < 400
                    ? "REDIRECT_DENIED"
                    : "UPSTREAM_ERROR");
            if (response.Content.Headers.ContentEncoding.Count != 0)
                throw new FilmHomeException("INVALID_RESPONSE");
            if (response.Content.Headers.ContentLength is 0 or > MaximumResponseBytes)
                throw new FilmHomeException("LIMIT");

            byte[] bytes = await ReadBoundedAsync(response.Content, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return Parse(bytes, section, start, limit);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FilmHomeException("TIMEOUT");
        }
        catch (FilmHomeException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new FilmHomeException("INVALID_RESPONSE");
        }
        catch
        {
            throw new FilmHomeException("NETWORK");
        }
    }

    internal static Uri BuildAddress(FilmSectionDefinition section, int start = 0, int limit = 12)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (!FilmDataFilters.IsAllowedRequest(section))
            throw new ArgumentException("Unknown film section.", nameof(section));
        if (start < 0 || start > 10000) throw new ArgumentOutOfRangeException(nameof(start));
        if (limit < 1 || limit > 24) throw new ArgumentOutOfRangeException(nameof(limit));
        string category = Uri.EscapeDataString(section.Category);
        string type = Uri.EscapeDataString(section.Type);
        return new($"https://m.douban.com/rexxar/api/v2/subject/recent_hot/{section.Kind}" +
            $"?start={start.ToString(CultureInfo.InvariantCulture)}" +
            $"&limit={limit.ToString(CultureInfo.InvariantCulture)}&category={category}&type={type}");
    }

    internal static IReadOnlyList<FilmCardItem> Parse(
        ReadOnlyMemory<byte> bytes,
        FilmSectionDefinition section,
        int start = 0,
        int limit = 12)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (!FilmDataFilters.IsAllowedRequest(section))
            throw new ArgumentException("Unknown film section.", nameof(section));
        if (start < 0 || start > 10000) throw new ArgumentOutOfRangeException(nameof(start));
        if (limit < 1 || limit > 24) throw new ArgumentOutOfRangeException(nameof(limit));
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
            throw new FilmHomeException("INVALID_RESPONSE");

        var result = new List<FilmCardItem>(limit);
        var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (result.Count == limit) break;
            int itemIndex = checked(start + index++);
            if (item.ValueKind != JsonValueKind.Object) continue;
            string title = Text(item, "title");
            if (title.Length == 0) continue;
            string id = Scalar(item, "id");
            if (id.Length == 0)
                id = $"{section.Key}:{itemIndex.ToString(CultureInfo.InvariantCulture)}:{title}";
            result.Add(new(id, title, Poster(item), Score(item)));
        }
        return result;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        await using var input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[32768];
        while (true)
        {
            int count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (count == 0) break;
            if (output.Length + count > MaximumResponseBytes)
                throw new FilmHomeException("LIMIT");
            output.Write(buffer, 0, count);
        }
        if (output.Length == 0) throw new FilmHomeException("INVALID_RESPONSE");
        return output.ToArray();
    }

    private static string Poster(JsonElement item)
    {
        string[] candidates =
        [
            NestedText(item, "pic", "normal"),
            NestedText(item, "pic", "large"),
            NestedText(item, "cover", "url"),
            Text(item, "cover_url"),
            Text(item, "poster"),
            Text(item, "image")
        ];
        return FilmPosterAddress.Normalize(candidates.FirstOrDefault(value => value.Length > 0) ?? "");
    }

    private static string Score(JsonElement item)
    {
        if (!item.TryGetProperty("rating", out var rating) ||
            rating.ValueKind != JsonValueKind.Object ||
            !rating.TryGetProperty("value", out var value))
            return "";
        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var number) &&
            double.IsFinite(number) &&
            number > 0)
            return number.ToString("0.#", CultureInfo.InvariantCulture);
        if (value.ValueKind != JsonValueKind.String) return "";
        string text = value.GetString()?.Trim() ?? "";
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            double.IsFinite(parsed) && parsed > 0
            ? parsed.ToString("0.#", CultureInfo.InvariantCulture)
            : "";
    }

    private static string NestedText(JsonElement item, string owner, string property) =>
        item.TryGetProperty(owner, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? Text(nested, property)
            : "";

    private static string Text(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? ""
            : "";

    private static string Scalar(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => ""
        };
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        client.Dispose();
    }
}

internal static class FilmPosterAddress
{
    private const string ProxyHost = "img.doubanio.cmliussss.net";

    internal static string Normalize(string address)
    {
        address = address.Trim();
        if (address.Length == 0 || address.Length > 8192 || address.Any(char.IsControl))
            return "";
        if (address.StartsWith("//", StringComparison.Ordinal)) address = "https:" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 ||
            uri.Fragment.Length != 0)
            return "";
        var builder = new UriBuilder(uri)
        {
            Scheme = Uri.UriSchemeHttps,
            Port = 443
        };
        if (IsDoubanHost(uri.IdnHost)) builder.Host = ProxyHost;
        return builder.Uri.AbsoluteUri;
    }

    private static bool IsDoubanHost(string host)
    {
        host = host.ToLowerInvariant();
        return Indexed(host, "img", ".doubanio.com") ||
            Indexed(host, "img", ".douban.com") ||
            host is "img.doubanio.com" or "img.douban.com" or "img3.doubanio.com" or
                "img.doubanio.cmliussss.net" or "img.doubanio.cmliussss.com";
    }

    private static bool Indexed(string value, string prefix, string suffix)
    {
        if (!value.StartsWith(prefix, StringComparison.Ordinal) ||
            !value.EndsWith(suffix, StringComparison.Ordinal))
            return false;
        var middle = value.AsSpan(prefix.Length, value.Length - prefix.Length - suffix.Length);
        return middle.Length > 0 && middle.IndexOfAnyExceptInRange('0', '9') < 0;
    }
}
