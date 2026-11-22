using System.Text.Json;
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
            return await database.ReadSnapshotAsync(FilmFavoritesStore.ReadAll, token).ConfigureAwait(false);
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
        await gate.WaitAsync(token).ConfigureAwait(false);
        bool changed;
        try
        {
            Check(); token.ThrowIfCancellationRequested();
            changed = await database.WriteAsync(
                db => FilmFavoritesStore.SetFavorite(db, item, favorite),
                CancellationToken.None,
                favorite ? 1024 : 0).ConfigureAwait(false);
        }
        catch (JsonException) { throw Corrupt(); }
        catch (DocumentStoreException error) when (error.Error == DocumentStoreError.Corrupt) { throw Corrupt(); }
        finally { gate.Release(); }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
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
