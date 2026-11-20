using System.Text.Json;
using System.Text.RegularExpressions;
using MeowFilm.Threading;
using Microsoft.UI.Dispatching;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

public sealed record FilmMetadataSettings(string ApiBase, string ImageBase,
    string ApiKey, string AccessToken, string Language, string Region, bool IncludeAdult)
{
    public string ApiToken => AccessToken.Length > 0 ? AccessToken : ApiKey;
}

public sealed record FilmDoubanSettings(string ApiBase, string SearchBase, string ImageBase,
    string ApiProxy, string ImageProxy, string Cookie);

/// <summary>Native settings owner; upstream metadata requests remain in the runtime.</summary>
public sealed class FilmMetadataService(IRuntimeClient runtime, IStorageDatabase database) : IAsyncDisposable
{
    private readonly SettingsDocument tmdb = new(new(database, "film.tmdb.v1", FilmStorageRecords.TMDB)), douban = new(new(database, "film.douban.v1", FilmStorageRecords.Douban));
    private static readonly JsonElement TmdbDefaults = FilmJson.Encode(new
    {
        apiBase = "https://api.themoviedb.org/3", imageBase = "https://image.tmdb.org",
        apiKey = "", accessToken = "", language = "zh-CN", region = "CN", includeAdult = false
    });
    private static readonly JsonElement DoubanDefaults = FilmJson.Encode(new
    {
        apiBase = "https://m.douban.com", searchBase = "https://search.douban.com",
        imageBase = "https://img3.doubanio.com", apiProxy = "", imageProxy = "", cookie = ""
    });
    private static readonly char[] InputWhitespace =
        "\u0009\u000a\u000b\u000c\u000d\u0020\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000\ufeff".ToCharArray();
    private readonly OwnerDispatcher dispatcher = OwnerDispatcher.Current;
    private readonly HashSet<Task> pending = [];
    private CancellationTokenSource reads = new();
    private Task? preparation, disposal;
    private bool stopping, closed;
    public FilmMetadataSettings? Settings { get; private set; }
    public event EventHandler? Saved;

    public string ResolveImage(string path) => path;

    public Task<FilmMetadataSettings> GetSettingsAsync(CancellationToken token = default)
        => SettingsAsync(tmdb, TmdbDefaults, null, value =>
        {
            var snapshot = ParseSettings(value);
            Settings = snapshot;
            return snapshot;
        }, token);

    public Task<FilmMetadataSettings> SaveSettingsAsync(IReadOnlyDictionary<string, object?> patch,
        CancellationToken token = default)
    {
        return SettingsAsync(tmdb, TmdbDefaults, patch, value =>
        {
            var snapshot = ParseSettings(value);
            Settings = snapshot;
            Saved?.Invoke(this, EventArgs.Empty);
            return snapshot;
        }, token, "tmdb");
    }

    public Task<FilmDoubanSettings> GetDoubanSettingsAsync(CancellationToken token = default) =>
        SettingsAsync(douban, DoubanDefaults, null, ParseDoubanSettings, token);

    public Task<FilmDoubanSettings> SaveDoubanSettingsAsync(IReadOnlyDictionary<string, object?> patch,
        CancellationToken token = default) =>
        SettingsAsync(douban, DoubanDefaults, patch, ParseDoubanSettings, token, "douban");

    private Task<T> SettingsAsync<T>(SettingsDocument document, JsonElement defaults,
        IReadOnlyDictionary<string, object?>? patch, Func<JsonElement, T> receive, CancellationToken token, string? block = null)
    {
        dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(closed, this);
        if (stopping) throw new BridgeException("CLOSED");
        token.ThrowIfCancellationRequested();
        var work = ExchangeAsync();
        pending.Add(work);
        _ = ReleaseAsync(work);
        return work;
        async Task<T> ExchangeAsync()
        {
            using var request = patch is null ? CancellationTokenSource.CreateLinkedTokenSource(token, reads.Token) : null;
            JsonElement settings;
            if (patch is null)
                settings = ReadSettings(await document.ReadAsync(request!.Token), defaults);
            else
            {
                var saved = await document.UpdateAsync(current =>
                {
                    var next = ReadSettings(current, defaults);
                    foreach (var (key, value) in patch)
                    {
                        if (!defaults.TryGetProperty(key, out var fallback)) continue;
                        var normalized = NormalizeSettings(FilmJson.Encode(new Dictionary<string, object?> { [key] = value }),
                            FilmJson.Encode(new Dictionary<string, JsonElement> { [key] = fallback }),
                            block == "tmdb");
                        next = FilmDocument.Set(next, key, normalized.GetProperty(key));
                    }
                    return FilmDocument.Set(current ?? FilmJson.Encode(new { version = 1 }), "settings", next);
                }, token);
                settings = saved.GetProperty("settings");
                (runtime as IRuntimeSettingsNotifications)?.SettingsChanged(block!);
            }
            return receive(settings);
        }
    }

    private static JsonElement ReadSettings(JsonElement? current, JsonElement defaults)
    {
        if (current is null) return defaults;
        var root = current.Value;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != 1 ||
            !root.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
            throw new DocumentStoreException(DocumentStoreError.Corrupt);
        foreach (var field in defaults.EnumerateObject())
            if (!settings.TryGetProperty(field.Name, out var value) ||
                (field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    : value.ValueKind != field.Value.ValueKind))
                throw new DocumentStoreException(DocumentStoreError.Corrupt);
        return settings;
    }

    private static JsonElement NormalizeSettings(JsonElement value, JsonElement defaults, bool tmdb)
    {
        var result = new Dictionary<string, object?>();
        foreach (var property in defaults.EnumerateObject())
        {
            var item = value.GetProperty(property.Name);
            if (property.Name == "includeAdult")
            {
                if (item.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new BridgeException("INVALID_REQUEST");
                result[property.Name] = item.GetBoolean();
                continue;
            }
            if (item.ValueKind != JsonValueKind.String || item.GetString()!.Any(unit => unit <= '\u001f' || unit == '\u007f'))
                throw new BridgeException("INVALID_REQUEST");
            string text = item.GetString()!;
            bool proxy = property.Name.EndsWith("Proxy", StringComparison.Ordinal);
            if (!property.Name.EndsWith("Base", StringComparison.Ordinal) && !proxy)
            {
                result[property.Name] = text.Trim(InputWhitespace);
                continue;
            }
            if (text.Length == 0 && tmdb) text = property.Value.GetString()!;
            if (text.Length == 0 && proxy) { result[property.Name] = text; continue; }
            var slots = Regex.Matches(text, @"\{(?:url|encodedUrl)\}");
            string address = Regex.Replace(text, @"\{(?:url|encodedUrl)\}", "https%3A%2F%2Fexample.invalid%2F");
            if (!Regex.IsMatch(text, @"^https?://", RegexOptions.IgnoreCase) ||
                text != text.Trim() || address.Any(char.IsWhiteSpace) || address.Contains('\\') ||
                address.Contains('{') || address.Contains('}') || slots.Count > (proxy ? 1 : 0) ||
                slots.Count > 0 && !Regex.IsMatch(text, @"^https?://[^/?#{}]+[/?]", RegexOptions.IgnoreCase) ||
                !Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") ||
                text.Contains('#') || !proxy && text.Contains('?'))
                throw new BridgeException("INVALID_REQUEST");
            result[property.Name] = proxy ? url.Scheme + text[text.IndexOf(':')..] : url.AbsoluteUri.TrimEnd('/');
        }
        return FilmJson.Encode(result);
    }

    public Task ClearCacheAsync(string provider, CancellationToken token = default) =>
        RunAsync("film.data.invalidate", new { domain = provider }, _ => true, token, writing: true);

    private Task<T> RunAsync<T>(string method, object input, Func<JsonElement, T> receive,
        CancellationToken token, bool writing = false)
    {
        dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(closed, this);
        if (stopping) throw new BridgeException("CLOSED");
        token.ThrowIfCancellationRequested();
        var work = ExchangeAsync();
        pending.Add(work);
        _ = ReleaseAsync(work);
        return work;

        async Task<T> ExchangeAsync()
        {
            using var request = writing ? null : CancellationTokenSource.CreateLinkedTokenSource(token, reads.Token);
            var reply = await runtime.CallAsync(method, input, request?.Token ?? CancellationToken.None);
            if (!writing) request!.Token.ThrowIfCancellationRequested();
            if (!reply.GetProperty("ok").GetBoolean()) throw new BridgeException(reply.GetProperty("code").GetString()!);
            return receive(reply.GetProperty("value").Clone());
        }
    }
    private async Task ReleaseAsync(Task work)
    {
        try { await work; } catch { /* The caller owns the outcome. */ }
        pending.Remove(work);
    }
    private static FilmMetadataSettings ParseSettings(JsonElement value)
    {
        try
        {
            string Text(string key) => value.GetProperty(key).GetString() ?? throw new BridgeException("INVALID_RESPONSE");
            return new(Text("apiBase"), Text("imageBase"), Text("apiKey"), Text("accessToken"),
                Text("language"), Text("region"), value.GetProperty("includeAdult").GetBoolean());
        }
        catch { throw new BridgeException("INVALID_RESPONSE"); }
    }
    private static FilmDoubanSettings ParseDoubanSettings(JsonElement value)
    {
        try
        {
            string Text(string key) => value.GetProperty(key).GetString() ?? throw new BridgeException("INVALID_RESPONSE");
            return new(Text("apiBase"), Text("searchBase"), Text("imageBase"),
                Text("apiProxy"), Text("imageProxy"), Text("cookie"));
        }
        catch { throw new BridgeException("INVALID_RESPONSE"); }
    }
    public Task PrepareShutdownAsync()
    {
        dispatcher.VerifyAccess();
        if (preparation is not null) return preparation;
        stopping = true;
        reads.Cancel();
        return preparation = DrainAsync(pending.ToArray());
    }
    private static async Task DrainAsync(Task[] operations)
    { try { await Task.WhenAll(operations); } catch { /* Each caller receives its own failure. */ } }
    public ValueTask DisposeAsync()
    {
        dispatcher.VerifyAccess();
        if (disposal is not null) return new(disposal);
        var drained = PrepareShutdownAsync();
        closed = true;
        return new(disposal = CloseAsync(drained));
    }
    private async Task CloseAsync(Task drained)
    {
        try { await drained; } catch { /* Request failures were already delivered to their callers. */ }
        reads.Dispose();
        Saved = null;
    }
}
