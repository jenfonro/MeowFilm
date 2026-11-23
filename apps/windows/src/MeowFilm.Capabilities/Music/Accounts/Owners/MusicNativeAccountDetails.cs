using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities;

public sealed partial class MusicNativeApplication
{
    public event Action<string>? AccountDetailsInvalidated;
    private void AccountDetailsChanged(string platform)
    {
        foreach (var observer in AccountDetailsInvalidated?.GetInvocationList() ?? [])
        {
            try { ((Action<string>)observer)(platform); }
            catch { /* Display listeners cannot change account revocation or its physical drain. */ }
        }
    }
    public Task<MusicAccountDetails> ReadDetailsAsync(string platform, CancellationToken token = default) =>
        ExecuteAccountDetailsAsync(platform, null, token);
    public Task<MusicAccountDetails> RefreshDetailsAsync(string platform, long expectedGeneration,
        CancellationToken token = default) =>
        ExecuteAccountDetailsAsync(platform, expectedGeneration, token);

    private async Task<MusicAccountDetails> ExecuteAccountDetailsAsync(string platform, long? expectedGeneration,
        CancellationToken cancellationToken)
    {
        if (platform is not ("wy" or "tx" or "kg"))
            throw new MusicOperationException("INVALID_REQUEST");
        if (expectedGeneration is { } expected && expected is < 1 or > 9_007_199_254_740_991L)
            throw new MusicOperationException("INVALID_REQUEST");
        CancellationToken generation;
        lock (gate)
        {
            if (stopping || closed) throw new MusicOperationException("CLOSED");
            if (accountReceiptUnknown) throw new MusicOperationException("COMMIT_UNKNOWN");
            if (pending++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            generation = lifetime.Token;
        }
        try
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generation);
            request.Token.ThrowIfCancellationRequested();
            var details = expectedGeneration is { } version
                ? await accounts.RefreshDetailsAsync(platform, version, request.Token).ConfigureAwait(false)
                : await accounts.ReadDetailsAsync(platform, request.Token).ConfigureAwait(false);
            lock (gate) if (accountReceiptUnknown) throw new MusicOperationException("COMMIT_UNKNOWN");
            return details;
        }
        catch (MusicAccountException error) { throw new MusicOperationException(error.Code); }
        catch (MusicPlatformException error) { throw new MusicOperationException(error.Code); }
        finally { lock (gate) if (--pending == 0) drained.TrySetResult(); }
    }
}
