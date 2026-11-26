using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

/// <summary>Native favorite rows anchored on canonical work identity. The runtime owns history, manual matches and blocks.</summary>
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
            catch (StorageException error) when (error.Error == StorageError.Corrupt) { throw Corrupt(); }
            finally { gate.Release(); }
        }
        finally { Exit(); }
    }

    public async Task SetFavoriteAsync(FilmCardItem item, bool favorite, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Entry is not { } entry) throw new FilmPlaybackException("INVALID_REQUEST");
        entry.Validate();
        var record = ToRecord(item, entry, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
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
            catch (StorageException error) when (error.Error == StorageError.Corrupt) { throw Corrupt(); }
            finally { gate.Release(); }
        }
        finally { Exit(); }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private static FilmCardItem FromRecord(FilmFavoriteRecord record)
    {
        var tmdb = record.TmdbKind is not null && record.TmdbId is { } tid ? new FilmTmdbRef(record.TmdbKind, tid) : null;
        var douban = record.DoubanKind is not null && record.DoubanId is { } did ? new FilmDoubanRef(record.DoubanKind, did) : null;
        var entry = new FilmEntry(
            record.FavoriteId,
            record.Keyword,
            record.Title,
            record.Subtitle,
            record.Poster,
            "",
            record.Score,
            record.TextBadge,
            "",
            record.ContentKind,
            record.Year,
            Tmdb: tmdb,
            Douban: douban);

        return new FilmCardItem(record.FavoriteId, record.Title, record.Poster, record.Score)
        {
            TextBadge = record.TextBadge,
            Subtitle = record.Subtitle,
            Entry = entry
        };
    }

    private static FilmFavoriteRecord ToRecord(FilmCardItem item, FilmEntry entry, long addedAtMs) =>
        new(
            entry.TargetId,
            entry.Keyword,
            item.Title.Length > 0 ? item.Title : entry.Title,
            item.Poster,
            item.Score,
            item.TextBadge,
            item.Subtitle,
            entry.Kind,
            entry.Year,
            entry.Tmdb?.Kind,
            entry.Tmdb?.Id,
            entry.Douban?.Kind,
            entry.Douban?.Id,
            addedAtMs);

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
