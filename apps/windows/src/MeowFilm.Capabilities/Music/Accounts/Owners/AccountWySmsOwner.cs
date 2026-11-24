// SPDX-License-Identifier: GPL-3.0-only
namespace MeowFilm.Capabilities.Accounts;

public sealed partial class MusicAccountOwner
{
    private async Task<MusicAccountSmsSendResult> SendSmsCoreAsync(MusicAccountSmsInput input, CancellationToken token)
    {
        using var op = Start(input.Platform, token);
        try
        {
            // Fail before any SMS side effect if protected storage cannot be read.
            _ = await Load(input.Platform, op.Token).WaitAsync(op.Token).ConfigureAwait(false);
            Guard(op);
            var sent = await protocol.SendSmsAsync(input, op.Token).ConfigureAwait(false);
            AccountRules.Require(sent.Sent, "INVALID_RESPONSE");
            return new MusicAccountSmsSendResult(input.Platform, true, sent.RetryAfterSeconds);
        }
        finally { Finish(op); }
    }

    private async Task<MusicAccountStatusRecord> LoginSmsCoreAsync(MusicAccountSmsInput input, CancellationToken token)
    {
        using var op = Start(input.Platform, token);
        bool knownConflict = false;
        try
        {
            // Fail before any SMS side effect if protected storage cannot be read.
            var before = await Load(input.Platform, op.Token).WaitAsync(op.Token).ConfigureAwait(false);
            Guard(op);
            string attemptId = Guid.NewGuid().ToString("D");
            bool consumed = false;
            StoredAccount? committed = null;
            await protocol.LoginSmsAsync(input, attemptId, async (handoff, authority) =>
            {
                authority.ThrowIfCancellationRequested();
                lock (gate)
                {
                    Guard(op);
                    AccountRules.Require(!consumed && handoff.Platform == input.Platform && handoff.ChallengeId == attemptId,
                        "HANDOFF_REJECTED");
                    consumed = true;
                }
                try { committed = await Commit(op, before, handoff.Cookie, handoff.Details, true, authority).ConfigureAwait(false); }
                catch (MusicAccountException error) when (error.Code == "STORAGE_CONFLICT")
                { knownConflict = true; throw; }
                return true;
            }, op.Token).ConfigureAwait(false);
            Guard(op);
            AccountRules.Require(consumed && committed is not null, "HANDOFF_REJECTED");
            return Status(input.Platform, committed!);
        }
        catch
        {
            if (knownConflict) throw new MusicAccountException("STORAGE_CONFLICT");
            if (op.Admitted) throw new MusicAccountException("HANDOFF_UNCONFIRMED");
            throw;
        }
        finally { Finish(op); }
    }
}
