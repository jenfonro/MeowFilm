using MeowFilm.Capabilities.Library;
using static MeowFilm.Capabilities.Recommendations.RecommendationJson;

namespace MeowFilm.Capabilities.Recommendations;

/// <summary>
/// One admission lane over the existing library owner, not storage and not another writer.
/// Every external queue mutation (including catalog commits) must use this same instance.
/// </summary>
public sealed class MusicRecommendationQueuePort : IDisposable
{
    private readonly MusicLibraryOwner library;
    private readonly SemaphoreSlim lane = new(1, 1);
    private int disposed;
    private long epoch;
    internal event Action<MusicRecommendationQueueChange>? Changing;
    internal event Action<MusicRecommendationQueueChange>? Changed;
    internal event Action<string>? Selected;
    internal Func<string, MusicQueueSourceRecord?>? SelectionSource
    {
        get => library.QueueSelectionSource;
        set => library.QueueSelectionSource = value;
    }
    internal long Epoch => Interlocked.Read(ref epoch);
    public MusicRecommendationQueuePort(MusicLibraryOwner library)
    { ArgumentNullException.ThrowIfNull(library); this.library = library; }

    public async Task<T> ExecuteExternalAsync<T>(MusicRecommendationQueueChange change,
        Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
        where T : IMusicQueuePosition
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (change is MusicRecommendationQueueChange.Replace or MusicRecommendationQueueChange.Clear or
            MusicRecommendationQueueChange.Restore or MusicRecommendationQueueChange.Reset)
            Interlocked.Increment(ref epoch);
        Changing?.Invoke(change); // fence before admission, never while holding the lane
        try
        {
            return await InLaneAsync(async (_, token) =>
            {
                var result = await operation(token).ConfigureAwait(false);
                // Publish actual selection only after its successful CAS, before
                // releasing the lane or admitting a following ordinary append.
                if (change is MusicRecommendationQueueChange.Selection or MusicRecommendationQueueChange.Remove)
                    Selected?.Invoke(result.CurrentId ?? "");
                return result;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { Changed?.Invoke(change); }
    }

    internal async Task<T> InLaneAsync<T>(Func<MusicLibraryOwner, CancellationToken, Task<T>> operation, CancellationToken token)
    {
        // Owning an available lane must not run hot library snapshots on the UI caller.
        await lane.WaitAsync(token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try { return await operation(library, token).ConfigureAwait(false); }
        finally { lane.Release(); }
    }

    internal async Task InLaneAsync(Func<MusicLibraryOwner, CancellationToken, Task> operation, CancellationToken token)
    {
        await lane.WaitAsync(token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try { await operation(library, token).ConfigureAwait(false); }
        finally { lane.Release(); }
    }

    internal Task<MusicRecommendationSessionRecord?> ReadRecommendationSessionAsync(CancellationToken token) =>
        InLaneAsync((owner, ct) => owner.ReadRecommendationSessionAsync(ct), token);
    internal Task SaveRecommendationSessionAsync(MusicRecommendationSessionRecord? value, CancellationToken token) =>
        InLaneAsync((owner, ct) => owner.SaveRecommendationSessionAsync(value, ct), token);
    internal Task<bool> HasRecommendationCheckpointAsync(string checkpointId, CancellationToken token) =>
        InLaneAsync((owner, ct) => owner.HasRecommendationCheckpointAsync(checkpointId, ct), token);

    internal static async Task<QueueSnapshot> ReadAsync(MusicLibraryOwner owner, CancellationToken token)
    {
        var entries = new List<QueueEntry>();
        long? version = null; int total = 0, mode = 1; string current = ""; bool priority = false;
        for (int offset = 0; offset == 0 || offset < total; offset += 100)
        {
            var value = await owner.GetQueueAsync(offset, 100, token).ConfigureAwait(false);
            Require(value.PersistenceState == "saved", "PENDING_WRITE");
            long actualVersion = value.Version;
            string actualCurrent = value.CurrentId ?? "";
            int actualTotal = value.Total;
            Require(version is null || version == actualVersion && current == actualCurrent && total == actualTotal, "STALE_QUEUE");
            version = actualVersion; current = actualCurrent; total = actualTotal; mode = value.Mode;
            priority = value.HasPriority;
            Require(value.Entries.Length == Math.Min(100, total - offset), "INVALID_RESPONSE");
            foreach (var row in value.Entries)
                entries.Add(new(row.EntryId, row.Song, row.Context));
        }
        Require(entries.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() == entries.Count, "INVALID_RESPONSE");
        return new(version!.Value, current, mode, entries.ToArray(), priority);
    }

    internal sealed record QueueEntry(string Id, MusicSongRecord Song, MusicPlayContextRecord Context);
    internal sealed record QueueSnapshot(long Version, string CurrentId, int Mode, QueueEntry[] Entries, bool HasPriority)
    {
        internal int Index => Array.FindIndex(Entries, e => e.Id == CurrentId);
        internal string Tail => Entries.LastOrDefault()?.Id ?? "";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            lane.Dispose();
    }
}
