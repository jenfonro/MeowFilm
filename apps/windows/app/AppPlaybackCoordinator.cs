// Output arbitration belongs to App composition, not either media engine.
// Each engine releases its own command gate while awaiting this callback, then
// rechecks its exact input before starting output.
internal sealed class AppPlaybackCoordinator(
    Func<Task> stopMusic,
    Func<Task> stopFilm,
    Func<string, bool> hasOutput)
{
    private readonly SemaphoreSlim acquisitions = new(1, 1);
    private string? activeOwner;

    internal string? ActiveOwner => Volatile.Read(ref activeOwner);
    internal event EventHandler? ActiveOwnerChanged;

    internal Task<IDisposable> AcquireMusicAsync(CancellationToken token) => AcquireAsync("music", stopFilm, token);
    internal Task<IDisposable> AcquireFilmAsync(CancellationToken token) => AcquireAsync("film", stopMusic, token);

    internal void ReleaseOwner(string owner)
    {
        if (Interlocked.CompareExchange(ref activeOwner, null, owner) == owner)
            ActiveOwnerChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<IDisposable> AcquireAsync(string owner, Func<Task> stopOther, CancellationToken token)
    {
        await acquisitions.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            await stopOther();
            token.ThrowIfCancellationRequested();
            var previous = Interlocked.Exchange(ref activeOwner, owner);
            if (!string.Equals(previous, owner, StringComparison.Ordinal))
                ActiveOwnerChanged?.Invoke(this, EventArgs.Empty);
            return new Permit(this, owner);
        }
        catch
        {
            acquisitions.Release();
            throw;
        }
    }

    private bool HasOutput(string owner) => hasOutput(owner);

    private sealed class Permit(AppPlaybackCoordinator coordinator, string owner) : IDisposable
    {
        private AppPlaybackCoordinator? held = coordinator;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref held, null);
            if (current is null) return;
            try
            {
                if (!current.HasOutput(owner))
                    current.ReleaseOwner(owner);
            }
            finally
            {
                current.acquisitions.Release();
            }
        }
    }
}
