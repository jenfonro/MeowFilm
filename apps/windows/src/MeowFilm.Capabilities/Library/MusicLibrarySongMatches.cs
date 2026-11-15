using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    internal async Task<IReadOnlyList<JsonObject>> ReadMatchingSongsAsync(CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LJ.Require(!closed && !preparing, "CLOSED");
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = pending ?? recommendationUnknown;
            LJ.Require(attempt is null, attempt?.Code ?? "PENDING_WRITE");
            var library = LJ.Object(LibraryState(document!)["library"]);
            var songs = LJ.Object(library["songs"]);
            return songs.Select(pair => LJ.Object(pair.Value).DeepClone().AsObject()).ToArray();
        }
        finally { gate.Release(); }
    }
}
