namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    public Task LoadMoreCollectionAsync()
    {
        var selected = collection;
        var origin = loadGeneration;
        var offset = Songs.Count;
        var expectedTotal = total;
        if (!CanLoadMoreCollection || selected is null || offset >= expectedTotal)
            return Task.CompletedTask;

        return RunAsync(async () =>
        {
            if (stopping || origin != loadGeneration || collection?.Id != selected.Id ||
                collection.Revision != selected.Revision || Songs.Count != offset ||
                total != expectedTotal || offset >= expectedTotal)
                return;

            using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            collectionPageCancellation = request;
            RefreshBindings();
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
                if (stopping || origin != loadGeneration || collection?.Id != selected.Id ||
                    collection.Revision != selected.Revision || Songs.Count != offset)
                    return;
                var known = Songs.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
                MusicDto.Require(rows.All(row => known.Add(row.Id)));
                foreach (var row in rows) Songs.Add(row);
                RefreshBindings();
            }
            finally
            {
                if (ReferenceEquals(collectionPageCancellation, request))
                    collectionPageCancellation = null;
                RefreshBindings();
            }
        });
    }

    public Task ReorderPlaylistSongAsync(SongRow source, SongRow target, bool after)
    {
        InvalidateCatalogAction();
        var selected = collection;
        var origin = loadGeneration;
        var sourceIndex = Songs.IndexOf(source);
        var targetIndex = Songs.IndexOf(target);
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
            if (stopping || origin != loadGeneration || collection?.Id != selected.Id ||
                !Songs.Contains(source) || !Songs.Contains(target))
                return;

            var currentSource = Songs.IndexOf(source);
            Songs.RemoveAt(currentSource);
            var currentTarget = Songs.IndexOf(target);
            Songs.Insert(currentTarget + (after ? 1 : 0), source);
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
        RefreshBindings();
    }
}
