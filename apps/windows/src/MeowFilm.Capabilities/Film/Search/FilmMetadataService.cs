using System.Text.Json;
using System.Text.RegularExpressions;
using MeowFilm.Core.Debug;
using MeowFilm.Threading;
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
    private readonly OwnerDispatcher dispatcher = OwnerDispatcher.Current;
    private readonly HashSet<Task> pending = [];
    private CancellationTokenSource reads = new();
    private Task? preparation, disposal;
    private bool stopping, closed;
    public FilmMetadataSettings? Settings { get; private set; }
    public event EventHandler? Saved;

    public Task<FilmMetadataSettings> GetSettingsAsync(CancellationToken token = default)
        => TrackAsync(async ct =>
        {
            var snapshot = await FilmMetadataSettingsStore.ReadTmdbAsync(database, ct);
            Settings = snapshot;
            return snapshot;
        }, token, writing: false);

    public Task<FilmMetadataSettings> SaveSettingsAsync(IReadOnlyDictionary<string, object?> patch,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return TrackAsync(async ct =>
        {
            var (snapshot, changed) = await FilmMetadataSettingsStore.UpdateTmdbAsync(database,
                current => ApplyTmdbPatch(current, patch), ct);
            Settings = snapshot;
            if (changed)
            {
                (runtime as IRuntimeSettingsNotifications)?.SettingsChanged("tmdb");
                Saved?.Invoke(this, EventArgs.Empty);
            }
            return snapshot;
        }, token, writing: true);
    }

    public Task<FilmDoubanSettings> GetDoubanSettingsAsync(CancellationToken token = default) =>
        TrackAsync(ct => FilmMetadataSettingsStore.ReadDoubanAsync(database, ct), token, writing: false);

    public Task<FilmDoubanSettings> SaveDoubanSettingsAsync(IReadOnlyDictionary<string, object?> patch,
        CancellationToken token = default) =>
        TrackAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(patch);
            var (snapshot, changed) = await FilmMetadataSettingsStore.UpdateDoubanAsync(database,
                current => ApplyDoubanPatch(current, patch), ct);
            if (changed)
                (runtime as IRuntimeSettingsNotifications)?.SettingsChanged("douban");
            return snapshot;
        }, token, writing: true);

    private Task<T> TrackAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token, bool writing)
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
            return await operation(request?.Token ?? token);
        }
    }

    private static FilmMetadataSettings ApplyTmdbPatch(FilmMetadataSettings current, IReadOnlyDictionary<string, object?> patch)
    {
        var defaults = FilmMetadataSettingsStore.TmdbDefaults;
        var next = current;
        foreach (var (key, value) in patch)
        {
            next = key switch
            {
                "apiBase" => next with { ApiBase = NormalizeTextField(value, "apiBase", defaults.ApiBase) },
                "imageBase" => next with { ImageBase = NormalizeTextField(value, "imageBase", defaults.ImageBase) },
                "apiKey" => next with { ApiKey = NormalizeTextField(value, "apiKey", defaults.ApiKey) },
                "accessToken" => next with { AccessToken = NormalizeTextField(value, "accessToken", defaults.AccessToken) },
                "language" => next with { Language = NormalizeTextField(value, "language", defaults.Language) },
                "region" => next with { Region = NormalizeTextField(value, "region", defaults.Region) },
                "includeAdult" => next with { IncludeAdult = NormalizeBoolField(value) },
                _ => next
            };
        }
        return next;
    }

    private static FilmDoubanSettings ApplyDoubanPatch(FilmDoubanSettings current, IReadOnlyDictionary<string, object?> patch)
    {
        var defaults = FilmMetadataSettingsStore.DoubanDefaults;
        var next = current;
        foreach (var (key, value) in patch)
        {
            next = key switch
            {
                "apiBase" => next with { ApiBase = NormalizeTextField(value, "apiBase", defaults.ApiBase) },
                "searchBase" => next with { SearchBase = NormalizeTextField(value, "searchBase", defaults.SearchBase) },
                "imageBase" => next with { ImageBase = NormalizeTextField(value, "imageBase", defaults.ImageBase) },
                "apiProxy" => next with { ApiProxy = NormalizeTextField(value, "apiProxy", defaults.ApiProxy) },
                "imageProxy" => next with { ImageProxy = NormalizeTextField(value, "imageProxy", defaults.ImageProxy) },
                "cookie" => next with { Cookie = NormalizeTextField(value, "cookie", defaults.Cookie) },
                _ => next
            };
        }
        return next;
    }

    private static bool NormalizeBoolField(object? value) => value switch
    {
        bool flag => flag,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        _ => throw new BridgeException("INVALID_REQUEST")
    };

    private static string NormalizeTextField(object? raw, string fieldName, string fallback)
    {
        if (raw is null) throw new BridgeException("INVALID_REQUEST");
        string text = raw switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString()!,
            _ => throw new BridgeException("INVALID_REQUEST")
        };
        if (text.Any(unit => unit <= '\u001f' || unit == '\u007f'))
            throw new BridgeException("INVALID_REQUEST");
        bool proxy = fieldName.EndsWith("Proxy", StringComparison.Ordinal);
        if (!fieldName.EndsWith("Base", StringComparison.Ordinal) && !proxy)
            return text.Trim(FilmJson.InputWhitespace);
        if (text.Length == 0 && !proxy) text = fallback;
        if (text.Length == 0 && proxy) return text;
        var slots = Regex.Matches(text, @"\{(?:url|encodedUrl)\}");
        string address = Regex.Replace(text, @"\{(?:url|encodedUrl)\}", "https%3A%2F%2Fexample.invalid%2F");
        if (!Regex.IsMatch(text, @"^https?://", RegexOptions.IgnoreCase) ||
            text != text.Trim(FilmJson.InputWhitespace) || address.Any(c => char.IsWhiteSpace(c) || c == '\uFEFF') || address.Contains('\\') ||
            address.Contains('{') || address.Contains('}') || slots.Count > (proxy ? 1 : 0) ||
            slots.Count > 0 && !Regex.IsMatch(text, @"^https?://[^/?#{}]+[/?]", RegexOptions.IgnoreCase) ||
            !Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") ||
            text.Contains('#') || !proxy && text.Contains('?'))
            throw new BridgeException("INVALID_REQUEST");
        return proxy ? url.Scheme + text[text.IndexOf(':')..] : url.AbsoluteUri.TrimEnd('/');
    }

    public Task ClearCacheAsync(string provider, CancellationToken token = default)
    {
        dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(closed, this);
        if (stopping) throw new BridgeException("CLOSED");
        if (provider is not ("tmdb" or "douban")) throw new BridgeException("INVALID_REQUEST");
        token.ThrowIfCancellationRequested();
        var work = ExchangeAsync();
        pending.Add(work);
        _ = ReleaseAsync(work);
        return work;

        async Task ExchangeAsync()
        {
            var reply = await runtime.CallAsync("film.data.invalidate", new { domain = provider }, token);
            if (!reply.GetProperty("ok").GetBoolean()) throw new BridgeException(reply.GetProperty("code").GetString()!);
        }
    }
    private async Task ReleaseAsync(Task work)
    {
        try { await work; }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.FilmCatalog, "metadata", "operation.settled", error: error);
        }
        pending.Remove(work);
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
    {
        try { await Task.WhenAll(operations); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.FilmCatalog, "metadata", "drain.settled", error: error);
        }
    }
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
        try { await drained; }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.FilmCatalog, "metadata", "close.settled", error: error);
        }
        reads.Dispose();
        Saved = null;
    }
}
