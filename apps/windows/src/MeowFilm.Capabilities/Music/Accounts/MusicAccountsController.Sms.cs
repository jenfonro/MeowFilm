using MeowFilm.Capabilities.Accounts;

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
    public bool CanSendAccountSms => CanManageAccount && ShowAccountSms && AccountSmsRetrySeconds == 0;
    public bool CanLoginAccountSms => CanManageAccount && ShowAccountSms;

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
        return login
            ? ChangeAccountAsync("wy", "sms.login",
                token => application.Accounts.LoginSmsAsync("wy", phone, countryCode, captcha!, token),
                status => status)
            : ChangeAccountAsync("wy", "sms.send",
                token => application.Accounts.SendSmsAsync("wy", phone, countryCode, token),
                sent =>
                {
                    if (sent.RetryAfterSeconds > 0) SmsCooldown(sent.RetryAfterSeconds);
                    accountStatus = "验证码已发送";
                    AccountBindings();
                    return null;
                });
    }

    private async Task<TResult> CallAccountSmsAsync<TResult>(string operation, Func<CancellationToken, Task<TResult>> invoke, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var work = invoke(token);
        // Sending owns a real dispatched/unknown result and cooldown. A page
        // cancellation cannot replace its receipt with a guessed local reserve.
        TResult reply;
        try
        {
            reply = operation == "sms.send" ? await work : await work.WaitAsync(token);
        }
        catch (MusicAccountException error) when (error.RetryAfterSeconds is >= 1 and <= 600)
        {
            SmsCooldown(error.RetryAfterSeconds);
            throw;
        }
        catch (MusicOperationException error) when (error.RetryAfterSeconds is >= 1 and <= 600)
        {
            SmsCooldown(error.RetryAfterSeconds);
            throw;
        }
        if (operation != "sms.send") token.ThrowIfCancellationRequested();
        return reply;
    }

    public Task LeaveAccountSmsAsync() => HasAccountSms || accountSmsOperation ? CancelAccountAsync() : Task.CompletedTask;
}
