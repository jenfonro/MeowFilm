using MeowFilm.Capabilities.Accounts;

namespace MeowFilm.Capabilities;

public sealed partial class MusicNativeApplication
{
    public event Action? VipPageInvalidated
    {
        add => vipPage.VipPageInvalidated += value;
        remove => vipPage.VipPageInvalidated -= value;
    }

    public Task<MusicVipPageSnapshot> ReadVipPageAsync(CancellationToken token = default) =>
        ExecuteVipPageAsync(vipPage.ReadVipPageAsync, token);

    public Task<MusicVipSignReceipt> SignVipPageAsync(long expectedGeneration,
        CancellationToken token = default) =>
        ExecuteVipPageAsync(request => vipPage.SignVipPageAsync(expectedGeneration, request), token);

    private async Task<T> ExecuteVipPageAsync<T>(
        Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        CancellationToken generation;
        lock (gate)
        {
            if (stopping || closed) throw new MusicOperationException("CLOSED");
            if (pending >= 32) throw new MusicOperationException("LIMIT");
            if (pending++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            generation = lifetime.Token;
        }
        try
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, generation);
            request.Token.ThrowIfCancellationRequested();
            return await action(request.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
                if (--pending == 0) drained.TrySetResult();
        }
    }
}
