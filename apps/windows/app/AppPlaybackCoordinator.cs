// Output arbitration belongs to App composition, not either media engine.
// Each engine releases its own command gate while awaiting this callback, then
// rechecks its exact input before starting output.
internal sealed class AppPlaybackCoordinator(Func<Task> stopMusic, Func<Task> stopFilm)
{
    private readonly SemaphoreSlim acquisitions = new(1, 1);
    internal string? ActiveOwner { get; private set; }

    internal Task<IDisposable> AcquireMusicAsync(CancellationToken token) => AcquireAsync("music", stopFilm, token);
    internal Task<IDisposable> AcquireFilmAsync(CancellationToken token) => AcquireAsync("film", stopMusic, token);

    private async Task<IDisposable> AcquireAsync(string owner, Func<Task> stopOther, CancellationToken token)
    {
        await acquisitions.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            await stopOther();
            token.ThrowIfCancellationRequested();
            ActiveOwner = owner;
            return new Permit(acquisitions);
        }
        catch { acquisitions.Release(); throw; }
    }

    private sealed class Permit(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? held = gate;
        public void Dispose() => Interlocked.Exchange(ref held, null)?.Release();
    }
}
