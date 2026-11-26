using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

/// <summary>Native favorite rows. The runtime owns history, manual matches and blocks.</summary>
public sealed class FilmLibrary(IStorageDatabase database) : IAsyncDisposable
{
    public const string StorageOwner = "film.library.v1";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object lifecycle = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? disposal;
    private int active;
    private volatile bool closed;
    public event EventHandler? Changed;

    public async Task<IReadOnlyList<FilmCardItem>> ReadAsync(CancellationToken token = default)
    {
        Enter();
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Check();
                var records = await database.ReadSnapshotAsync(FilmFavoritesStore.ReadAll, token).ConfigureAwait(false);
                return records.Select(FromRecord).ToArray();
            }
            catch (JsonException) { throw Corrupt(); }
            catch (StorageException error) when (error.Error == StorageError.Corrupt) { throw Corrupt(); }
            finally { gate.Release(); }
        }
        finally { Exit(); }
    }

    public async Task SetFavoriteAsync(FilmCardItem item, bool favorite, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.PlaybackContext is not { } context) throw new FilmPlaybackException("INVALID_REQUEST");
        context.Target.Validate();
        var record = ToRecord(item, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Enter();
        bool changed;
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Check(); token.ThrowIfCancellationRequested();
                changed = await database.WriteAsync(
                    db => FilmFavoritesStore.SetFavorite(db, record, favorite),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (JsonException) { throw Corrupt(); }
            catch (StorageException error) when (error.Error == StorageError.Corrupt) { throw Corrupt(); }
            finally { gate.Release(); }
        }
        finally { Exit(); }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private static FilmCardItem FromRecord(FilmFavoriteRecord record)
    {
        JsonElement? source = null;
        if (record.Source is { } src)
        {
            var obj = new JsonObject();
            if (src.Key is not null) obj["key"] = src.Key;
            if (src.Engine is not null) obj["engine"] = src.Engine;
            if (src.InstallationId is not null) obj["installationId"] = src.InstallationId;
            if (src.SiteId is not null) obj["siteId"] = src.SiteId;
            if (src.DetailId is { } detailId) obj["detailId"] = DecodeIdentity(detailId);
            if (src.LineId is { } lineId) obj["lineId"] = DecodeIdentity(lineId);
            if (obj.Count > 0) source = JsonSerializer.SerializeToElement(obj);
        }

        JsonElement? members = null;
        if (record.Members is { } list)
        {
            var arr = new JsonArray();
            foreach (var m in list)
                arr.Add(new JsonObject
                {
                    ["sourceKey"] = m.SourceKey,
                    ["detailId"] = DecodeIdentity(m.DetailId)
                });
            members = JsonSerializer.SerializeToElement(arr);
        }

        var target = new FilmPlaybackTarget(
            record.FavoriteId,
            record.TargetKind,
            record.TargetTitle,
            record.MetadataProvider,
            record.MetadataId,
            record.TargetYear,
            record.Season,
            record.Episode,
            record.GlobalEpisode,
            source,
            members);

        return new FilmCardItem(record.ItemId, record.Title, record.Poster, record.Score)
        {
            TextBadge = record.TextBadge,
            Subtitle = record.Subtitle,
            PlaybackContext = new FilmPlaybackRequest(target)
        };
    }

    private static FilmFavoriteRecord ToRecord(FilmCardItem item, long addedAtMs)
    {
        var target = item.PlaybackContext!.Target;
        FilmFavoriteSourceRecord? source = null;
        if (target.Kind is "source" or "group" && target.Source is { ValueKind: JsonValueKind.Object } src)
        {
            string? key = OptionalText(src, "key");
            string? engine = OptionalText(src, "engine");
            string? installationId = OptionalText(src, "installationId");
            string? siteId = OptionalText(src, "siteId") ??
                (FilmJson.Member(src, "siteId") is { ValueKind: JsonValueKind.Number } numSite ? numSite.GetRawText() : null);
            var detailId = EncodeIdentity(FilmJson.Member(src, "detailId"));
            var lineId = EncodeIdentity(FilmJson.Member(src, "lineId"));
            if (key is not null || engine is not null || installationId is not null || siteId is not null ||
                detailId is not null || lineId is not null)
                source = new FilmFavoriteSourceRecord(key, engine, installationId, siteId, detailId, lineId);
        }

        List<FilmFavoriteMemberRecord>? members = null;
        if (target.Kind == "group" && target.Members is { ValueKind: JsonValueKind.Array } rawMembers)
        {
            members = [];
            foreach (var member in rawMembers.EnumerateArray())
            {
                var detailId = EncodeIdentity(FilmJson.Member(member, "detailId"))
                    ?? throw new FilmPlaybackException("INVALID_REQUEST");
                members.Add(new FilmFavoriteMemberRecord(FilmJson.Text(member, "sourceKey"), detailId));
            }
        }

        return new FilmFavoriteRecord(
            target.Id,
            item.Id,
            item.Title,
            item.Poster,
            item.Score,
            item.TextBadge,
            item.Subtitle,
            target.Kind,
            target.Title,
            target.MetadataProvider,
            target.MetadataId,
            target.Year,
            target.Season,
            target.Episode,
            target.GlobalEpisode,
            source,
            members,
            addedAtMs);
    }

    private static string? OptionalText(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString()
            : null;

    private static FilmFavoriteIdentity? EncodeIdentity(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => null,
        JsonValueKind.String => new FilmFavoriteIdentity("text", value.GetString()!),
        JsonValueKind.Number when value.TryGetInt64(out long number) =>
            new FilmFavoriteIdentity("integer", number.ToString(CultureInfo.InvariantCulture)),
        _ => throw new FilmPlaybackException("INVALID_REQUEST")
    };

    private static JsonNode DecodeIdentity(FilmFavoriteIdentity identity) => identity.Kind switch
    {
        "text" => JsonValue.Create(identity.Value)!,
        "integer" when long.TryParse(identity.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) => JsonValue.Create(n)!,
        _ => throw new StorageException(StorageError.Corrupt)
    };

    private void Enter()
    {
        lock (lifecycle)
        {
            if (closed) throw new FilmPlaybackException("CLOSED");
            active++;
        }
    }

    private void Exit()
    {
        lock (lifecycle)
        {
            if (--active == 0 && closed) drained.TrySetResult();
        }
    }

    private void Check() { if (closed) throw new FilmPlaybackException("CLOSED"); }
    private static FilmPlaybackException Corrupt() => new("CORRUPT_STORAGE");
    public ValueTask DisposeAsync()
    {
        lock (lifecycle)
        {
            if (disposal is not null) return new(disposal);
            closed = true;
            if (active == 0) drained.TrySetResult();
            disposal = CloseAsync();
            return new(disposal);
        }

        async Task CloseAsync()
        {
            await drained.Task.ConfigureAwait(false);
            gate.Dispose();
            Changed = null;
        }
    }
}
