namespace MeowFilm.Capabilities;

public sealed partial class MusicLibraryController
{
    public Task LoadMoreCollectionAsync()
    {
        var selected = collection;
        var origin = host.LoadGeneration;
        var offset = host.ActiveSongs.Count;
        var expectedTotal = total;
        if (!CanLoadMoreCollection || selected is null || offset >= expectedTotal)
            return Task.CompletedTask;

        return RunAsync(async () =>
        {
            if (stopping || origin != host.LoadGeneration || collection?.Id != selected.Id ||
                collection.Revision != selected.Revision || host.ActiveSongs.Count != offset ||
                total != expectedTotal || offset >= expectedTotal)
                return;

            using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            collectionPageCancellation = request;
            host.RefreshBindings();
            try
            {
                var reply = await Call("music.library.detail", new
                {
                    id = selected.Id,
                    offset,
                    limit = 100
                }, request.Token);
                var currentCollection = PlaylistRow.Read(reply.GetProperty("collection"));
                var currentTotal = MusicDto.Count(reply, "total");
                if (currentCollection.Id != selected.Id || currentCollection.Revision != selected.Revision ||
                    currentTotal != expectedTotal)
                    throw new MusicOperationException("REVISION_CONFLICT");
                var rows = MusicDto.Page(reply, "songs", offset).Select(item => SongRow.Read(item)).ToArray();
                request.Token.ThrowIfCancellationRequested();
                if (stopping || origin != host.LoadGeneration || collection?.Id != selected.Id ||
                    collection.Revision != selected.Revision || host.ActiveSongs.Count != offset)
                    return;
                var known = host.ActiveSongs.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
                MusicDto.Require(rows.All(row => known.Add(row.Id)));
                foreach (var row in rows) host.ActiveSongs.Add(row);
                host.RefreshBindings();
            }
            finally
            {
                if (ReferenceEquals(collectionPageCancellation, request))
                    collectionPageCancellation = null;
                host.RefreshBindings();
            }
        });
    }

    public Task ReorderPlaylistSongAsync(SongRow source, SongRow target, bool after)
    {
        host.Catalog.InvalidateCatalogAction();
        var selected = collection;
        var origin = host.LoadGeneration;
        var sourceIndex = host.ActiveSongs.IndexOf(source);
        var targetIndex = host.ActiveSongs.IndexOf(target);
        if (!CanReorderPlaylist || selected is null || ReferenceEquals(source, target) ||
            sourceIndex < 0 || targetIndex < 0)
            return Task.CompletedTask;

        return RunAsync(async () =>
        {
            var reply = await Call("music.library.reorderSong", new
            {
                id = selected.Id,
                ifRevision = selected.Revision,
                songId = source.Id,
                targetId = target.Id,
                after
            }, lifetime.Token);
            var updated = PlaylistRow.Read(reply.GetProperty("collection"));
            MusicDto.Require(updated.Id == selected.Id && updated.Count == selected.Count &&
                updated.Revision == selected.Revision + 1);
            if (stopping || origin != host.LoadGeneration || collection?.Id != selected.Id ||
                !host.ActiveSongs.Contains(source) || !host.ActiveSongs.Contains(target))
                return;

            var currentSource = host.ActiveSongs.IndexOf(source);
            host.ActiveSongs.RemoveAt(currentSource);
            var currentTarget = host.ActiveSongs.IndexOf(target);
            host.ActiveSongs.Insert(currentTarget + (after ? 1 : 0), source);
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
        host.RefreshBindings();
    }
}
