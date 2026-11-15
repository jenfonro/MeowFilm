using System.Text.Json;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

/// <summary>The sole native film-favorite owner. Node owns history/manual/blocks/preferences, not this document.</summary>
public sealed class FilmLibrary(IDocumentStore store) : IAsyncDisposable
{
    public const string DocumentNamespace = "film.library.v1";
    private static readonly JsonSerializerOptions StoredJson = new(FilmJson.Options) { PropertyNameCaseInsensitive = false };
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
            var matching = before.Records.Where(row => row.Candidates.Contains(context.Target.Id, StringComparer.Ordinal)).ToArray();
            var previous = before.Items.FirstOrDefault(value => value.PlaybackContext!.Target.Id == context.Target.Id);
            if (matching.Length > 1 || matching.Length == 1 && previous is null)
                throw new FilmPlaybackException("CORRUPT_STORAGE");
            var records = before.Records.Where(row => !row.Candidates.Contains(context.Target.Id, StringComparer.Ordinal))
                .Select(row => row.Value).ToList();
            if (favorite)
            {
                // Favorites retain the content identity, not search pages, credentials or a short-lived play address.
                var saved = FilmJson.Encode(ForStorage(item));
                records.Add(previous is null ? saved : FilmDocument.Merge(matching[0].Value, FilmJson.Encode(ForStorage(previous)), saved));
            }
            var root = FilmDocument.Set(before.Root, "favorites", FilmJson.Encode(records));
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(root);
            token.ThrowIfCancellationRequested();
            // Accepted CAS writes settle even if the initiating view leaves. Publish only a real receipt.
            var receipt = await store.WriteAsync(DocumentNamespace, bytes, before.Fingerprint, CancellationToken.None).ConfigureAwait(false);
            if (receipt != DocumentFingerprint.FromBytes(bytes)) throw new FilmPlaybackException("COMMIT_UNKNOWN");
        }
        finally { gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed record StoredFavorite(JsonElement Value, JsonElement Target, string? Id, string[] Candidates);
    private sealed record LibraryDocument(JsonElement Root, StoredFavorite[] Records,
        IReadOnlyList<FilmCardItem> Items, DocumentFingerprint Fingerprint);

    private static FilmCardItem ForStorage(FilmCardItem item)
    {
        var target = item.PlaybackContext!.Target;
        if (target.Kind is "source" or "group" && target.Source is { ValueKind: JsonValueKind.Object } source)
        {
            var identity = new Dictionary<string, JsonElement>();
            foreach (string name in new[] { "key", "detailId", "lineId" })
                if (source.TryGetProperty(name, out var field)) identity.Add(name, field);
            target = target with { Source = FilmJson.Encode(identity) };
        }
        else target = target with { Source = null };
        if (target.Kind == "group" && target.Members is { ValueKind: JsonValueKind.Array } members)
            target = target with { Members = FilmJson.Encode(members.EnumerateArray().Select(member => new
            {
                sourceKey = FilmJson.Member(member, "sourceKey"), detailId = FilmJson.Member(member, "detailId")
            }).ToArray()) };
        else target = target with { Members = null };
        return item with { PlaybackContext = new(target), SearchContext = null };
    }

    private static JsonElement[] IdentityFields(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        ? value.EnumerateObject().Where(property => property.NameEquals(name)).Select(property => property.Value).ToArray() : [];

    private static StoredFavorite ReadRecord(JsonElement row)
    {
        var contexts = IdentityFields(row, "playbackContext");
        var targets = contexts.SelectMany(context => IdentityFields(context, "target")).ToArray();
        var ids = targets.SelectMany(target => IdentityFields(target, "id")).ToArray();
        string? id = contexts.Length == 1 && targets.Length == 1 && ids.Length == 1 &&
            ids[0].ValueKind == JsonValueKind.String ? ids[0].GetString() : null;
        // Every recognizable identity is fenced, including earlier duplicate members.
        return new(row, targets.Length == 1 ? targets[0] : default, id,
            ids.Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!)
                .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray());
    }

    private async Task<LibraryDocument> ReadCoreAsync(CancellationToken token)
    {
        if (closed) throw new FilmPlaybackException("CLOSED");
        var snapshot = await store.ReadAsync(DocumentNamespace, token).ConfigureAwait(false);
        if (snapshot.Data is null)
        {
            if (snapshot.Fingerprint != DocumentFingerprint.Missing) throw new FilmPlaybackException("CORRUPT_STORAGE");
            return new(FilmJson.Encode(new { version = 1, favorites = System.Array.Empty<object>() }),
                [], [], snapshot.Fingerprint);
        }
        try
        {
            using var document = JsonDocument.Parse(snapshot.Data);
            var root = document.RootElement.Clone();
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Count(property => property.NameEquals("version")) != 1 ||
                root.EnumerateObject().Count(property => property.NameEquals("favorites")) != 1 ||
                FilmJson.Integer(root, "version") != 1 ||
                FilmJson.Member(root, "favorites").ValueKind != JsonValueKind.Array)
                throw new FilmPlaybackException("CORRUPT_STORAGE");
            var records = root.GetProperty("favorites").EnumerateArray().Select(ReadRecord).ToArray();
            var counts = records.SelectMany(row => row.Candidates).GroupBy(id => id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var items = new List<FilmCardItem>();
            foreach (var row in records)
            {
                if (row.Id is not { Length: > 0 } id || counts[id] != 1) continue;
                try
                {
                    var target = JsonSerializer.Deserialize<FilmPlaybackTarget>(row.Target, StoredJson);
                    if (target is null) continue;
                    target.Validate();
                    var item = new FilmCardItem(FilmJson.RequiredText(row.Value, "id"), FilmJson.RequiredText(row.Value, "title"),
                        FilmJson.Text(row.Value, "poster"), FilmJson.Text(row.Value, "score"))
                    {
                        TextBadge = FilmJson.Text(row.Value, "textBadge"), Subtitle = FilmJson.Text(row.Value, "subtitle"),
                        PlaybackContext = new(target)
                    };
                    if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Title)) continue;
                    items.Add(item);
                }
                catch (Exception error) when (error is JsonException or ArgumentException or FilmPlaybackException) { }
            }
            return new(root, records, items, snapshot.Fingerprint);
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
