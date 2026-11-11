// SPDX-License-Identifier: GPL-3.0-only
using MeowFilm.Capabilities.Accounts;

namespace MeowFilm.Capabilities;

public sealed partial class MusicNativeApplication : IMusicAccountTransferSource
{
    public Task<MusicAccountTransferPlan> PrepareAccountTransferAsync(MusicAccountTransferFile file, CancellationToken token = default) =>
        AccountTransfer(token, cancellation => accounts.PrepareAccountTransferAsync(file, cancellation));
    public Task<MusicAccountTransferFile?> ExportAccountTransferAsync(CancellationToken token = default) =>
        AccountTransfer(token, accounts.ExportAccountTransferAsync);
    public Task<MusicAccountTransferSummary> ImportAccountTransferAsync(MusicAccountTransferPlan plan,
        MusicAccountTransferPolicy policy, CancellationToken token = default) => AccountTransfer(token, async cancellation =>
    {
        var result = await accounts.ImportAccountTransferAsync(plan, policy, cancellation).ConfigureAwait(false);
        if (result.Items.Any(item => item.Code == "COMMIT_UNKNOWN"))
            lock (gate) { accountReceiptUnknown = true; accountReceiptEpoch++; }
        return result;
    });
    private async Task<T> AccountTransfer<T>(CancellationToken token, Func<CancellationToken, Task<T>> action)
    {
        CancellationToken generation;
        lock (gate)
        {
            if (stopping) throw new MusicAccountException("CLOSED");
            if (pending++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            generation = lifetime.Token;
        }
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, generation);
            return await action(cancellation.Token).ConfigureAwait(false);
        }
        finally { lock (gate) if (--pending == 0) drained.TrySetResult(); }
    }
}
