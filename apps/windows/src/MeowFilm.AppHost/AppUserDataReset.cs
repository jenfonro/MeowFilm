namespace MeowFilm.AppHost;

/// <summary>
/// One confirmed reset of one App generation. A failed stage remains retryable;
/// neither a timeout nor a failed deletion admits a replacement generation.
/// </summary>
internal sealed class AppUserDataReset(
    Action request, Func<Task> release, Func<Task> clear, Func<Task> reopen)
{
    private readonly object gate = new();
    private Task? attempt;
    private bool requested, released, cleared;

    public Task ResetAsync()
    {
        lock (gate)
        {
            if (attempt is null || attempt.IsFaulted || attempt.IsCanceled)
                attempt = RunAsync();
            return attempt;
        }
    }

    private async Task RunAsync()
    {
        // Publish the accepted attempt before callbacks can re-enter this owner.
        await Task.Yield();
        if (!requested) { request(); requested = true; }
        if (!released) { await release(); released = true; }
        if (!cleared) { await clear(); cleared = true; }
        await reopen();
    }
}
