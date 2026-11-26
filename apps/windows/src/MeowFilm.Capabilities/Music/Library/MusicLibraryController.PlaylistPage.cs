namespace MeowFilm.Capabilities;

public sealed partial class MusicLibraryController
{
    public Task LoadMoreCollectionAsync()
    {
        var selected = collection;
        var origin = ctx.LoadGeneration;
        var offset = LibrarySongsSource.Count;
        var expectedTotal = total;
        if (!CanLoadMoreCollection || selected is null || offset >= expectedTotal)
            return Task.CompletedTask;

        return RunAsync(async () =>
        {
            if (stopping || origin != ctx.LoadGeneration || collection?.Id != selected.Id ||
                collection.Revision != selected.Revision || LibrarySongsSource.Count != offset ||
                total != expectedTotal || offset >= expectedTotal)
                return;

            using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            collectionPageCancellation = request;
            ctx.RefreshBindings();
            try
            {
                var reply = await application.Library.GetCollectionDetailAsync(selected.Id, offset, 100, request.Token);
                var currentCollection = PlaylistRow.FromSummary(reply.Collection);
                var currentTotal = reply.Total;
                if (currentCollection.Id != selected.Id || currentCollection.Revision != selected.Revision ||
                    currentTotal != expectedTotal)
                    throw new MusicOperationException("REVISION_CONFLICT");
                var rows = reply.Songs.Select(item => SongRow.FromRecord(item)).ToArray();
                request.Token.ThrowIfCancellationRequested();
                if (stopping || origin != ctx.LoadGeneration || collection?.Id != selected.Id ||
                    collection.Revision != selected.Revision || LibrarySongsSource.Count != offset)
                    return;
                foreach (var row in rows) LibrarySongsSource.Add(row);
            }
            finally
            {
                if (ReferenceEquals(collectionPageCancellation, request))
                    collectionPageCancellation = null;
                ctx.RefreshBindings();
            }
        });
    }

    public Task ReorderPlaylistSongAsync(SongRow source, SongRow target, bool after)
    {
        ctx.InvalidateCatalogAction();
        var selected = collection;
        var origin = ctx.LoadGeneration;
        var sourceIndex = LibrarySongsSource.IndexOf(source);
        var targetIndex = LibrarySongsSource.IndexOf(target);
        if (!CanReorderPlaylist || selected is null || ReferenceEquals(source, target) ||
            sourceIndex < 0 || targetIndex < 0)
            return Task.CompletedTask;

        return RunAsync(async () =>
        {
            var reply = await application.Library.ReorderSongInCollectionAsync(
                selected.Id, selected.Revision, source.Id, target.Id, after, lifetime.Token);
            var updated = PlaylistRow.FromSummary(reply);
            if (stopping || origin != ctx.LoadGeneration || collection?.Id != selected.Id ||
                !LibrarySongsSource.Contains(source) || !LibrarySongsSource.Contains(target))
                return;

            var currentSource = LibrarySongsSource.IndexOf(source);
            LibrarySongsSource.RemoveAt(currentSource);
            var currentTarget = LibrarySongsSource.IndexOf(target);
            LibrarySongsSource.Insert(currentTarget + (after ? 1 : 0), source);
            AcceptCollectionMutation(updated);
            SelectedSong = source;
        });
    }

    private void AcceptCollectionMutation(PlaylistRow updated)
    {
        libraryRead++;
        collection = updated;
        var index = Playlists.ToList().FindIndex(row => row.Id == updated.Id);
        if (index >= 0) Playlists[index] = updated;
        RebuildNavigation();
        ctx.RefreshBindings();
    }
}
