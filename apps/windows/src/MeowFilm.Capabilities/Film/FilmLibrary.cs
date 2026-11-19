using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

/// <summary>Native favorite rows. The runtime owns history, manual matches and blocks.</summary>
public sealed class FilmLibrary(IStorageDatabase database) : IAsyncDisposable
{
    public const string DocumentNamespace = "film.library.v1";
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool closed;
    public event EventHandler? Changed;

    public async Task<IReadOnlyList<FilmCardItem>> ReadAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Check();
            return await database.ReadAsync(db =>
            {
                CheckRoot(db);
                return (IReadOnlyList<FilmCardItem>)FilmStorageRecords.Favorite
                    .ReadMany(db, ["device"]).Select(Decode).ToArray();
            }, token).ConfigureAwait(false);
        }
        catch (JsonException) { throw Corrupt(); }
        catch (DocumentStoreException error) when (error.Error == DocumentStoreError.Corrupt) { throw Corrupt(); }
        finally { gate.Release(); }
    }

    public async Task SetFavoriteAsync(FilmCardItem item, bool favorite, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.PlaybackContext is not { } context) throw new FilmPlaybackException("INVALID_REQUEST");
        context.Target.Validate();
        var value = favorite ? Row(item) : null;
        await gate.WaitAsync(token).ConfigureAwait(false);
        bool changed;
        try
        {
            Check(); token.ThrowIfCancellationRequested();
            changed = await database.WriteAsync(db =>
            {
                CheckRoot(db);
                var key = new object?[] { "device", context.Target.Id };
                var previous = FilmStorageRecords.Favorite.Read(db, key);
                if (previous is not null) _ = Decode(previous);
                if (JsonNode.DeepEquals(value, previous)) return false;
                if (previous is null && value is null) return false;
                if (db.Scalar<long>("SELECT count(*) FROM windows_film_library WHERE scope_key='device'") == 0)
                    FilmStorageRecords.Favorites.Save(db, new JsonObject { ["version"] = 1, ["favorites"] = new JsonArray() }, null, ["device"]);
                int ordinal = checked((int)(previous is null
                    ? db.Scalar<long>("SELECT coalesce(max(ordinal),-1)+1 FROM windows_film_library_favorites WHERE scope_key='device'")
                    : db.Scalar<long>("SELECT ordinal FROM windows_film_library_favorites WHERE scope_key='device' AND favorites_key=$id", ("$id", context.Target.Id))));
                FilmStorageRecords.Favorite.Save(db, value, previous, key, ordinal, ordinal);
                db.CommitRevision(DocumentNamespace, db.Revision(DocumentNamespace), Guid.NewGuid().ToString("D"));
                return true;
            }, CancellationToken.None, value?.ToJsonString().Length * 2 ?? 0).ConfigureAwait(false);
        }
        catch (JsonException) { throw Corrupt(); }
        catch (DocumentStoreException error) when (error.Error == DocumentStoreError.Corrupt) { throw Corrupt(); }
        finally { gate.Release(); }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void CheckRoot(DatabaseSession db)
    {
        if (db.Query("SELECT version,shape_mask,null_mask FROM windows_film_library WHERE scope_key='device'",
                row => !row.IsDBNull(0) && row.GetInt64(0) == 1 && row.GetInt64(1) == 3 && row.GetInt64(2) == 0)
            .Any(valid => !valid)) throw Corrupt();
    }

    private static FilmCardItem Decode(JsonObject row)
    {
        var item = row.Deserialize<FilmCardItem>(FilmJson.Options) ?? throw Corrupt();
        if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Title) ||
            item.PlaybackContext is not { } request || row["favoriteId"]?.GetValue<string>() != request.Target.Id)
            throw Corrupt();
        try { request.Target.Validate(); }
        catch (FilmPlaybackException) { throw Corrupt(); }
        return item;
    }

    private static JsonObject Row(FilmCardItem item)
    {
        var target = item.PlaybackContext!.Target;
        if (target.Kind is "source" or "group" && target.Source is { ValueKind: JsonValueKind.Object } source)
        {
            var identity = new Dictionary<string, JsonElement>();
            foreach (string name in new[] { "key", "engine", "installationId", "siteId", "detailId", "lineId" })
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
        return JsonSerializer.SerializeToNode(new
        {
            favoriteId = target.Id, id = item.Id, title = item.Title, poster = item.Poster, score = item.Score,
            textBadge = item.TextBadge, subtitle = item.Subtitle, playbackContext = new { target = target.Wire() }
        })!.AsObject();
    }
    private void Check() { if (closed) throw new FilmPlaybackException("CLOSED"); }
    private static FilmPlaybackException Corrupt() => new("CORRUPT_STORAGE");
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { closed = true; }
        finally { gate.Release(); }
    }
}
