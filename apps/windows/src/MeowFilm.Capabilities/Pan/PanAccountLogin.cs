using System.Windows.Media.Imaging;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

public sealed partial class PanAccounts
{
    private sealed class Login(PanAccountRow row, bool qr, long epoch, Task before)
    {
        internal readonly PanAccountRow Row = row;
        internal readonly bool Qr = qr;
        internal readonly long Epoch = epoch, Generation = row.Generation, Revision = row.Revision;
        internal readonly Task Before = before;
        internal readonly CancellationTokenSource Cancellation = new();
        internal Task Start = Task.CompletedTask, Poll = Task.CompletedTask, Save = Task.CompletedTask;
        internal string? SessionId;
        internal long ExpiresAt;
        internal BitmapSource? Image;
        internal string Status = "";
        internal bool Busy, Confirmed, PollingStopped;
    }
    private Login? login;
    private Task cleanup = Task.CompletedTask;
    public bool LoginOpen => login is not null;
    public PanAccountRow? LoginRow => login?.Row;
    public string LoginTitle => login?.Row.Name ?? "";
    public bool QrMode => login?.Qr == true;
    public bool LoginBusy => login?.Busy == true;
    public bool CanSave => active && login is { Qr: false, Busy: false, Row.IsBusy: false };
    public bool CanChangeMethod => login is { Qr: true } or { Busy: false };
    public bool CanRetryQr => active && login is { Qr: true, Busy: false };
    public BitmapSource? QrImage => login?.Image;
    public string LoginStatus => login?.Status ?? "";
    public string QrAccessibleName => LoginTitle + "登录二维码";

    private bool Current(Login session) => active && !stopping && ReferenceEquals(login, session) &&
        session.Epoch == epoch && session.Generation == session.Row.Generation && !session.Cancellation.IsCancellationRequested;
    private void LoginChanged()
    {
        foreach (string name in new[] { nameof(LoginOpen), nameof(LoginRow), nameof(LoginTitle), nameof(QrMode),
            nameof(LoginBusy), nameof(CanSave), nameof(CanChangeMethod), nameof(CanRetryQr),
            nameof(QrImage), nameof(LoginStatus), nameof(QrAccessibleName) }) Changed(name);
    }
    public Task OpenLoginAsync(PanAccountRow row)
    {
        if (!active || stopping || !Rows.Contains(row) || !row.CanLogin) return Task.CompletedTask;
        return SetLogin(row, row.SupportsQr);
    }
    public Task SwitchMethodAsync(bool qr)
    {
        if (!active || login is not { } current || !CanChangeMethod || qr && !current.Row.SupportsQr)
            return Task.CompletedTask;
        if (current.Qr == qr) return Task.CompletedTask;
        return SetLogin(current.Row, qr);
    }
    public Task RetryQrAsync() => CanRetryQr && login is { } current
        ? SetLogin(current.Row, qr: true) : Task.CompletedTask;

    private Task SetLogin(PanAccountRow row, bool qr)
    {
        RetireLogin();
        var session = new Login(row, qr, epoch, cleanup);
        login = session;
        row.Editing = true; row.Changed();
        Notify("");
        session.Busy = qr;
        LoginChanged();
        return qr ? session.Start = Track(Start()) : Task.CompletedTask;

        async Task Start()
        {
            await Task.Yield(); // Publish the task before close/method changes can retire it.
            try
            {
                await session.Before;
                if (!Current(session)) return;
                // Keep start's response observable after UI cancellation, so a late challenge
                // is always cancelled by Cleanup instead of becoming an orphan on the runtime.
                var reply = await Call("login.start", new { provider = row.Provider });
                session.SessionId = reply.GetProperty("sessionId").GetString()!;
                if (!Current(session)) return;
                if (reply.GetProperty("provider").GetString() != row.Provider ||
                    reply.GetProperty("status").GetString() != "pending")
                    throw new BridgeException("INVALID_RESPONSE");
                session.ExpiresAt = reply.GetProperty("expiresAt").GetInt64();
                var challenge = reply.GetProperty("challenge");
                session.Image = PanQrImage.Create(challenge.GetProperty("type").GetString()!,
                    challenge.GetProperty("url").GetString()!);
                session.Status = "等待扫码";
            }
            catch (Exception error)
            {
                if (Current(session)) Notify(Error(error));
            }
            finally
            {
                session.Busy = false;
                if (Current(session)) LoginChanged();
            }
        }
    }

    public Task PollLoginAsync()
    {
        if (login is not { Qr: true, Busy: false, Image: not null, SessionId: not null, PollingStopped: false } session ||
            !Current(session) || !session.Poll.IsCompleted) return Task.CompletedTask;
        if (session.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            session.Image = null; session.Status = "二维码已过期"; LoginChanged();
            return Task.CompletedTask;
        }
        return session.Poll = Track(Poll());

        async Task Poll()
        {
            await Task.Yield();
            try
            {
                var reply = await Call("login.poll", new { sessionId = session.SessionId }, session.Cancellation.Token,
                    session.Row.Provider == "baidu" ? TimeSpan.FromSeconds(45) : null);
                if (!Current(session)) return;
                if (reply.GetProperty("sessionId").GetString() != session.SessionId ||
                    reply.TryGetProperty("provider", out var provider) && provider.GetString() != session.Row.Provider)
                    throw new BridgeException("INVALID_RESPONSE");
                switch (reply.GetProperty("status").GetString())
                {
                    case "pending": session.Status = "等待扫码"; break;
                    case "scanned": session.Status = "等待确认"; break;
                    case "confirmed":
                        var account = PanAccountSnapshot.Read(reply.GetProperty("account"));
                        if (account.Revision <= session.Revision || account.Status != "signedIn" || !account.Configured)
                            throw new BridgeException("INVALID_RESPONSE");
                        session.Row.Publish(account);
                        session.Confirmed = true;
                        Notify("网盘登录成功");
                        _ = CloseLoginAsync();
                        return;
                    case "expired": session.Image = null; session.Status = "二维码已过期"; break;
                    case "cancelled": session.Image = null; session.Status = "登录已取消"; break;
                    default: throw new BridgeException("INVALID_RESPONSE");
                }
                LoginChanged();
            }
            catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (!Current(session)) return;
                session.PollingStopped = true;
                if (error is PanAccountException { Code: "LOGIN_EXPIRED" })
                { session.Image = null; session.Status = "二维码已过期"; }
                Notify(Error(error)); LoginChanged();
            }
        }
    }

    public Task SaveAsync(IReadOnlyDictionary<string, string> draft)
    {
        if (!CanSave || login is not { } session) return Task.CompletedTask;
        var credentials = new Dictionary<string, object>();
        try
        {
            foreach (var field in session.Row.CredentialFields)
                if (draft.TryGetValue(field.Id, out string? value) && value.Length > 0)
                    credentials.Add(field.Id, field.Parse(value));
        }
        catch (Exception error)
        {
            credentials.Clear(); Notify(Error(error)); return Task.CompletedTask;
        }
        session.Busy = true; LoginChanged();
        return session.Save = RunRow(session.Row, async (request, current) =>
        {
            await Task.Yield();
            try
            {
                await session.Before;
                if (!current() || !ReferenceEquals(login, session)) return;
                // Once dispatched, observe the real write receipt before admitting a successor.
                // Cancelling HTTP observation cannot revoke an already admitted runtime replace.
                var account = PanAccountSnapshot.Read(await Call("settings.set",
                    new { provider = session.Row.Provider, credentials }));
                credentials.Clear();
                if (account.Revision <= session.Revision) throw new BridgeException("INVALID_RESPONSE");
                if (!current() || !ReferenceEquals(login, session)) return;
                session.Row.Publish(account);
                var refreshed = await Call("refresh", new { provider = session.Row.Provider }, request.Token);
                if (!refreshed.GetProperty("refreshed").GetBoolean()) throw new BridgeException("INVALID_RESPONSE");
                if (!current() || !ReferenceEquals(login, session)) return;
                var verified = PanAccountSnapshot.Read(refreshed.GetProperty("account"));
                if (!verified.Configured || verified.Status != "signedIn" || verified.Revision < account.Revision)
                    throw new BridgeException("INVALID_RESPONSE");
                session.Row.Publish(verified);
                Notify("网盘凭据已保存");
                _ = CloseLoginAsync();
            }
            finally
            {
                credentials.Clear(); session.Busy = false;
                if (ReferenceEquals(login, session)) LoginChanged();
            }
        });
    }

    public Task CloseLoginAsync()
    {
        RetireLogin();
        LoginChanged();
        return cleanup;
    }
    private void RetireLogin()
    {
        var retired = login;
        if (retired is null) return;
        var before = cleanup;
        login = null;
        retired.Cancellation.Cancel();
        retired.Row.Generation++;
        var request = retired.Row.Request;
        retired.Row.Request = null;
        request?.Cancel();
        retired.Row.Editing = false; retired.Row.Changed();
        cleanup = Track(Cleanup());
        async Task Cleanup()
        {
            try
            {
                await before;
                await retired.Start;
                if (retired.SessionId is { } sessionId && !retired.Confirmed)
                    await Call("login.cancel", new { sessionId });
            }
            catch (PanAccountException error) when (error.Code is "LOGIN_EXPIRED" or "CANCELLED" or "CLOSED") { }
            catch (BridgeException error) when (error.Code is "CANCELLED" or "CLOSED") { }
            catch (Exception error) { if (active && !stopping) Notify(Error(error)); }
            finally
            {
                await retired.Poll;
                await retired.Save;
                retired.Image = null;
                retired.Cancellation.Dispose();
            }
        }
    }
}
