using System.Text.Json;
using MeowFilm.Capabilities.Library;
using static MeowFilm.Capabilities.Recommendations.RecommendationJson;

namespace MeowFilm.Capabilities.Recommendations;

/// <summary>
/// One admission lane over the existing library owner, not storage and not another writer.
/// Every external queue mutation (including catalog commits) must use this same instance.
/// </summary>
public sealed class MusicRecommendationQueuePort
{
    private readonly MusicLibraryOwner library;
    private readonly SemaphoreSlim lane = new(1, 1);
    private long epoch;
    internal event Action<MusicRecommendationQueueChange>? Changing;
    internal event Action<MusicRecommendationQueueChange>? Changed;
    internal long Epoch => Interlocked.Read(ref epoch);
    public MusicRecommendationQueuePort(MusicLibraryOwner library)
    { ArgumentNullException.ThrowIfNull(library); this.library = library; }

    public Task<JsonElement> ExecuteAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        // Recommendation checkpoint commands are native coordinator capabilities,
        // not a second route for UI callers to bypass change notification.
        if (method.StartsWith("music.queue.recommendation.", StringComparison.Ordinal))
            return Task.FromResult(Json(new { ok = false, code = "CAPABILITY_UNAVAILABLE" }));
        var change = Classify(method, parameters);
        return change is { } kind
            ? ExecuteExternalAsync(kind, token => library.ExecuteAsync(method, parameters, token), cancellationToken)
            : InLaneAsync((owner, token) => owner.ExecuteAsync(method, parameters, token), cancellationToken);
    }
    public async Task<JsonElement> ExecuteExternalAsync(MusicRecommendationQueueChange change,
        Func<CancellationToken, Task<JsonElement>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref epoch);
        Changing?.Invoke(change); // fence before admission, never while holding the lane
        try { return await InLaneAsync((_, token) => operation(token), cancellationToken).ConfigureAwait(false); }
        finally { Changed?.Invoke(change); }
    }
    private static MusicRecommendationQueueChange? Classify(string method, object? parameters) => method switch
    {
        "music.queue.select" or "music.queue.next" or "music.queue.previous" or "music.queue.step" => MusicRecommendationQueueChange.Selection,
        "music.queue.append" => MusicRecommendationQueueChange.Append,
        "music.queue.remove" => MusicRecommendationQueueChange.Remove,
        "music.queue.reorder" => MusicRecommendationQueueChange.Reorder,
        "music.queue.replace" or "music.queue.playCollection" => MusicRecommendationQueueChange.Replace,
        "music.queue.clear" => MusicRecommendationQueueChange.Clear,
        "music.history.restorePlaylist" => MusicRecommendationQueueChange.Restore,
        "music.queue.mode" when parameters is not null && Json(parameters).TryGetProperty("mode", out _) => MusicRecommendationQueueChange.Mode,
        _ => null
    };
    internal async Task<T> InLaneAsync<T>(Func<MusicLibraryOwner, CancellationToken, Task<T>> operation, CancellationToken token)
    {
        await lane.WaitAsync(token).ConfigureAwait(false);
        try { return await operation(library, token).ConfigureAwait(false); }
        finally { lane.Release(); }
    }
    internal static async Task<QueueSnapshot> ReadAsync(MusicLibraryOwner owner, CancellationToken token)
    {
        var entries = new List<QueueEntry>();
        long? version = null; int total = 0, mode = 0; string current = "";
        for (int offset = 0; offset == 0 || offset < total; offset += 100)
        {
            var value = Okay(await owner.ExecuteAsync("music.queue.get", new { offset, limit = 100 }, token).ConfigureAwait(false));
            Require(Text(value.GetProperty("persistence"), "state") == "saved", "PENDING_WRITE");
            long actualVersion = Number(value, "version");
            string actualCurrent = value.GetProperty("currentId").ValueKind == JsonValueKind.Null ? "" : Text(value, "currentId");
            int actualTotal = checked((int)Number(value, "total"));
            Require(version is null || version == actualVersion && current == actualCurrent && total == actualTotal, "STALE_QUEUE");
            version = actualVersion; current = actualCurrent; total = actualTotal; mode = checked((int)Number(value, "mode"));
            var rows = value.GetProperty("entries");
            Require(rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() == Math.Min(100, total - offset), "INVALID_RESPONSE");
            foreach (var row in rows.EnumerateArray())
                entries.Add(new(Text(row, "id"), row.GetProperty("song").Clone(), row.GetProperty("context").Clone()));
        }
        Require(entries.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() == entries.Count, "INVALID_RESPONSE");
        return new(version!.Value, current, mode, entries.ToArray());
    }
    internal sealed record QueueEntry(string Id, JsonElement Song, JsonElement Context);
    internal sealed record QueueSnapshot(long Version, string CurrentId, int Mode, QueueEntry[] Entries)
    {
        internal int Index => Array.FindIndex(Entries, e => e.Id == CurrentId);
        internal string Tail => Entries.LastOrDefault()?.Id ?? "";
    }
}
