namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    internal async Task<IReadOnlySet<string>> ReadMatchingSongIdsAsync(IEnumerable<string> ids, CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LJ.Require(!closed && !preparing, "CLOSED");
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = pending ?? recommendationUnknown;
            LJ.Require(attempt is null, attempt?.Code ?? "PENDING_WRITE");
            var songs = new MusicSongSession(store);
            return ids.Distinct(StringComparer.Ordinal).Where(songs.Contains).ToHashSet(StringComparer.Ordinal);
        }
        finally { gate.Release(); }
    }
}
