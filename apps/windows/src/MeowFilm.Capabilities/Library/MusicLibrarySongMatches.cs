using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    internal Action<IEnumerable<JsonObject>>? SongsObserved { get; set; }
    internal Func<JsonObject, JsonObject>? SongFactsProjection { get; set; }
    internal Func<CancellationToken, Task>? PrepareSongFacts { get; set; }

    private void ObserveStoredSongs(JsonObject value)
    {
        if (SongsObserved is not { } observe) return;
        var songs = LJ.Object(LJ.Object(LibraryState(value)["library"])["songs"]);
        var queued = LJ.Array(Queue(value)["entries"]).OfType<JsonObject>().Select(entry => LJ.Object(entry["song"]));
        observe(songs.Select(pair => LJ.Object(pair.Value)).Concat(queued));
    }

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
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<JsonObject>();
            foreach (var collection in LJ.Array(library["collections"]).OfType<JsonObject>())
            {
                if (LJ.B(collection["deleted"]) || LJ.S(collection["kind"]) is not ("favorites" or "playlist")) continue;
                foreach (string id in Order(collection))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (seen.Add(id)) result.Add(LJ.Object(songs[id]).DeepClone().AsObject());
                }
            }
            return result;
        }
        finally { gate.Release(); }
    }
}
