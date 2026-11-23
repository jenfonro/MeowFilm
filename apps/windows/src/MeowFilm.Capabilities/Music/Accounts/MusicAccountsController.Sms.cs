using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed partial class MusicAccountsController
{
    private bool accountSmsPending, accountSmsOperation, accountSmsSending;
    private long accountSmsRetryUntil, accountSmsInputEpoch;

    public bool HasAccountSms => accountSmsPending;
    public bool ShowAccountSms => selectedAccount?.Platform == "wy";
    public long AccountSmsInputEpoch => accountSmsInputEpoch;
    public int AccountSmsRetrySeconds => (int)Math.Clamp(
        Math.Ceiling((accountSmsRetryUntil - Environment.TickCount64) / 1000d), 0, 600);
    public string AccountSmsSendText => AccountSmsRetrySeconds > 0 ? $"{AccountSmsRetrySeconds}秒后重发" : "发送验证码";
    public bool CanSendAccountSms => CanManageAccount && !accountSaveUncertain && ShowAccountSms && AccountSmsRetrySeconds == 0;
    public bool CanLoginAccountSms => CanManageAccount && !accountSaveUncertain && ShowAccountSms;

    private void AccountSmsBindings()
    {
        Changed(nameof(HasAccountSms));
        Changed(nameof(ShowAccountSms));
        Changed(nameof(AccountSmsInputEpoch));
        Changed(nameof(AccountSmsRetrySeconds));
        Changed(nameof(AccountSmsSendText));
        Changed(nameof(CanSendAccountSms));
        Changed(nameof(CanLoginAccountSms));
    }

    // Called by the visible view's local-only countdown; never performs I/O.
    public void RefreshAccountSmsCountdown() { if (!stopping) AccountSmsBindings(); }

    private void SmsCooldown(int seconds) =>
        accountSmsRetryUntil = Math.Max(accountSmsRetryUntil, Environment.TickCount64 + Math.Clamp(seconds, 0, 600) * 1000L);

    private void ClearAccountSmsQuiet()
    {
        accountSmsPending = false; accountSmsInputEpoch++;
    }

    private void ClearAccountSms()
    {
        ClearAccountSmsQuiet();
        // Retain the cooldown across cancel, platform/method changes and page reentry.
        AccountSmsBindings();
    }

    public Task SendAccountSmsAsync(string phone, string countryCode = "86") => SubmitSms(phone, countryCode, null);
    public Task LoginAccountSmsAsync(string phone, string countryCode, string captcha) => SubmitSms(phone, countryCode, captcha);

    private Task SubmitSms(string phone, string countryCode, string? captcha)
    {
        bool login = captcha is not null;
        if (!(login ? CanLoginAccountSms : CanSendAccountSms)) return Task.CompletedTask;
        accountSmsPending = true;
        return ChangeAccountAsync("wy", login ? "sms.login" : "sms.send", login
            ? new { platform = "wy", phone, countryCode, captcha }
            : (object)new { platform = "wy", phone, countryCode });
    }

    private async Task<JsonElement> CallAccountSms(string operation, object parameters, CancellationToken token)
    {
        string method = "music.accounts." + operation;
        token.ThrowIfCancellationRequested();
        var work = application.ExecuteAsync(method, parameters, token);
        // Sending owns a real dispatched/unknown result and cooldown. A page
        // cancellation cannot replace its receipt with a guessed local reserve.
        var reply = operation == "sms.send" ? await work : await work.WaitAsync(token);
        if (operation != "sms.send") token.ThrowIfCancellationRequested();
        MusicDto.Require(reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("ok", out _));
        if (reply.TryGetProperty("retryAfter", out var retry))
        {
            MusicDto.Require(retry.ValueKind == JsonValueKind.Number && retry.TryGetInt32(out int seconds) && seconds is >= 1 and <= 600);
            SmsCooldown(retry.GetInt32());
        }
        if (reply.GetProperty("ok").ValueKind == JsonValueKind.False)
            throw new MusicOperationException(MusicDto.Text(reply, "code"));
        MusicDto.Require(reply.GetProperty("ok").ValueKind == JsonValueKind.True);
        return reply;
    }

    public Task LeaveAccountSmsAsync() => HasAccountSms || accountSmsOperation ? CancelAccountAsync() : Task.CompletedTask;
}
