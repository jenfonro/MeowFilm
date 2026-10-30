// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Accounts;

public sealed partial class MusicAccountOwner
{
    private static MusicAccountSmsInput SmsInput(JsonElement parameters, bool login) => new MusicAccountSmsInput(
        AccountRules.String(parameters, "platform", 8), AccountRules.String(parameters, "phone", 32),
        parameters.TryGetProperty("countryCode", out _) ? AccountRules.String(parameters, "countryCode", 16) : "86",
        login ? AccountRules.String(parameters, "captcha", 16) : null).Normalize(login);

    private async Task<JsonObject> Sms(MusicAccountSmsInput input, bool login, CancellationToken token)
    {
        using var op = Start(input.Platform, token);
        bool knownConflict = false;
        try
        {
            // Fail before any SMS side effect if protected storage cannot be read.
            var before = await Load(input.Platform, op.Token).WaitAsync(op.Token).ConfigureAwait(false);
            Guard(op);
            if (!login)
            {
                var sent = await protocol.SendSmsAsync(input, op.Token).ConfigureAwait(false);
                Guard(op);
                AccountRules.Require(sent.Platform == input.Platform && sent.Sent && sent.RetryAfterSeconds is >= 1 and <= 600,
                    "INVALID_RESPONSE");
                return new() { ["platform"] = input.Platform, ["sent"] = true, ["retryAfter"] = sent.RetryAfterSeconds };
            }
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
            return new() { ["account"] = Status(input.Platform, committed!) };
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
