using System.Text.Json;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

/// <summary>The sole native film-favorite owner. Node owns history/manual/blocks/preferences, not this document.</summary>
public sealed class FilmLibrary(IDocumentStore store) : IAsyncDisposable
{
    public const string DocumentNamespace = "film.library.v1";
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool closed;
    public event EventHandler? Changed;

    public async Task<IReadOnlyList<FilmCardItem>> ReadAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { return (await ReadCoreAsync(token).ConfigureAwait(false)).Items; }
        finally { gate.Release(); }
    }

    public async Task SetFavoriteAsync(FilmCardItem item, bool favorite, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.PlaybackContext is not { } context) throw new FilmPlaybackException("INVALID_REQUEST");
        context.Target.Validate();
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var before = await ReadCoreAsync(token).ConfigureAwait(false);
            var items = before.Items.Where(value => value.PlaybackContext!.Target.Id != context.Target.Id).ToList();
            if (favorite)
            {
                var target = context.Target;
                if (target.Source is { } source)
                {
                    var identity = new Dictionary<string, JsonElement>();
                    foreach (string name in new[] { "key", "detailId", "lineId" })
                        if (source.TryGetProperty(name, out var field)) identity.Add(name, field);
                    target = target with { Source = FilmJson.Encode(identity) };
                }
                // Favorites retain the content identity, not search pages, credentials or a short-lived play address.
                items.Add(item with { PlaybackContext = new(target) });
            }
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, favorites = items }, FilmJson.Options);
            token.ThrowIfCancellationRequested();
            // Accepted CAS writes settle even if the initiating view leaves. Publish only a real receipt.
            var receipt = await store.WriteAsync(DocumentNamespace, bytes, before.Fingerprint, CancellationToken.None).ConfigureAwait(false);
            if (receipt != DocumentFingerprint.FromBytes(bytes)) throw new FilmPlaybackException("COMMIT_UNKNOWN");
        }
        finally { gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<(IReadOnlyList<FilmCardItem> Items, DocumentFingerprint Fingerprint)> ReadCoreAsync(CancellationToken token)
    {
        if (closed) throw new FilmPlaybackException("CLOSED");
        var snapshot = await store.ReadAsync(DocumentNamespace, token).ConfigureAwait(false);
        if (snapshot.Data is null)
        {
            if (snapshot.Fingerprint != DocumentFingerprint.Missing) throw new FilmPlaybackException("CORRUPT_STORAGE");
            return ([], snapshot.Fingerprint);
        }
        try
        {
            using var document = JsonDocument.Parse(snapshot.Data);
            var root = document.RootElement;
            if (FilmJson.Integer(root, "version") != 1 ||
                FilmJson.Member(root, "favorites").ValueKind != JsonValueKind.Array)
                throw new FilmPlaybackException("CORRUPT_STORAGE");
            var items = JsonSerializer.Deserialize<FilmCardItem[]>(root.GetProperty("favorites"), FilmJson.Options)!;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Title) ||
                    item.Poster is null || item.Score is null ||
                    item.PlaybackContext is not { Target: { } } context || !ids.Add(context.Target.Id))
                    throw new FilmPlaybackException("CORRUPT_STORAGE");
                context.Target.Validate();
            }
            return (items, snapshot.Fingerprint);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or FilmPlaybackException)
        { throw new FilmPlaybackException("CORRUPT_STORAGE"); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { closed = true; }
        finally { gate.Release(); }
    }
}
