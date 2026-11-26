namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    internal async Task<IReadOnlySet<string>> ReadMatchingSongIdsAsync(IEnumerable<string> ids, CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            MusicRules.Require(!closed && !preparing, "CLOSED");
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var songs = new MusicSongSession(store);
            return ids.Distinct(StringComparer.Ordinal).Where(songs.Contains).ToHashSet(StringComparer.Ordinal);
        }
        finally { gate.Release(); }
    }
}
