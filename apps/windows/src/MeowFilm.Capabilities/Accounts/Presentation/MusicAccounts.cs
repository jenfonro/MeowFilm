using System.Collections.ObjectModel;
using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

public sealed record MusicPlatformAccount(string Platform, string Name, string State,
    string DisplayName = "", string UserId = "", long Generation = 0)
{
    public string StateText => State switch
    {
        "none" => "未登录", "saved" => "会话已保存，待验证", "verified" => "会话验证成功",
        "revoked" => "会话已撤销，退出结果待确认", _ => "未读取"
    };
    public bool HasSession => State is "saved" or "verified" or "revoked";
}

public sealed partial class MusicCapability
{
    private readonly record struct AccountReadResult(long Epoch, long Observation, string? ErrorCode);

    // Selection identity is independent from hydrated account snapshots. Replacing
    // a snapshot must not make WPF clear the selected platform mid-publication.
    public IReadOnlyList<MusicCatalogPlatform> AccountPlatforms { get; } =
        [new("wy", "网易云音乐"), new("tx", "QQ 音乐"), new("kg", "酷狗音乐")];
    public string? SelectedAccountPlatform
    {
        get => selectedAccount?.Platform;
        set
        {
            if (value is not null && Accounts.FirstOrDefault(row => row.Platform == value) is { } account)
                SelectedAccount = account;
        }
    }
    public ObservableCollection<MusicPlatformAccount> Accounts { get; } =
        [new("wy", "网易云音乐", "unknown"), new("tx", "QQ 音乐", "unknown"), new("kg", "酷狗音乐", "unknown")];
    private MusicPlatformAccount? selectedAccount;
    private CancellationTokenSource? accountCancellation;
    private Task accountSettlement = Task.CompletedTask;
    private string? accountOperationPlatform;
    private bool accountQrOperation;
    private bool accountMayWrite, publishingAccounts;
    private long accountEpoch;
    private string accountStatus = "";
    private bool accountSaveUncertain;
    private long accountObservation;
    private long loadedAccountObservation = -1;
    private CancellationTokenSource? accountReadCancellation;
    private Task<AccountReadResult> accountRead = Task.FromResult(new AccountReadResult(-1, -1, null));
    private bool accountsLoaded;
    public MusicPlatformAccount? SelectedAccount
    {
        get => selectedAccount;
        set
        {
            if (stopping || publishingAccounts || value is not null && !Accounts.Any(item => ReferenceEquals(item, value))) return;
            if (selectedAccount?.Platform != value?.Platform)
            {
                // Cancel synchronously fences the UI epoch before changing platform.
                if (HasAccountQr || HasAccountSms || AccountBusy) _ = CancelAccountAsync();
                else { ClearAccountQr(); ClearAccountSms(); }
            }
            selectedAccount = value; AccountBindings();
        }
    }
    public bool AccountBusy => accountCancellation is not null || !accountSettlement.IsCompleted;
    public bool CanManageAccount => !stopping && selectedAccount is not null && !AccountBusy && !accountQrSaving;
    public string AccountStatus => accountStatus;
    private void AccountBindings()
    {
        foreach (string property in new[] { nameof(SelectedAccount), nameof(SelectedAccountPlatform), nameof(AccountBusy),
            nameof(CanManageAccount), nameof(AccountStatus) }) Changed(property);
        AccountQrBindings();
        AccountSmsBindings();
        AccountDetailsBindings();
    }
    private static string AccountError(Exception error) => error switch
    {
        MusicOperationException { Code: "AUTH_REQUIRED" or "AUTH_EXPIRED" } => "会话无效或已过期",
        MusicOperationException { Code: "INVALID_CREDENTIALS" } => "会话格式无效",
        MusicOperationException { Code: "FORBIDDEN" } => "账号访问被拒绝",
        MusicOperationException { Code: "VERIFICATION_REQUIRED" } => "需要平台验证",
        MusicOperationException { Code: "DEVICE_REQUIRED" or "DEVICE_CONTEXT_REQUIRED" or "SIGNATURE_REQUIRED" } => "平台设备认证未通过",
        MusicOperationException { Code: "DEVICE_LIMIT" } => "平台登录设备数已达上限",
        MusicOperationException { Code: "STALE_SESSION" or "SESSION_CHANGED" or "IDENTITY_MISMATCH" or "SUPERSEDED" } => "账号会话已变更",
        MusicOperationException { Code: "COMMIT_UNKNOWN" or "HANDOFF_UNCONFIRMED" } => "保存结果未确认，请读取账号状态",
        MusicOperationException { Code: "QR_EXPIRED" } => "二维码已过期",
        MusicOperationException { Code: "BUSY" } => "登录请求尚未结束",
        MusicOperationException { Code: "INVALID_CODE" } => "验证码无效或已过期",
        MusicOperationException { Code: "RATE_LIMITED" } => "请求过于频繁，请稍后重试",
        MusicOperationException { Code: "SMS_SEND_UNCONFIRMED" } => "短信发送结果未确认",
        MusicOperationException { Code: "UNSUPPORTED" } => "此账号协议尚未支持",
        _ => ErrorText(error)
    };
    private static MusicPlatformAccount AccountRow(JsonElement item)
    {
        string platform = MusicDto.Text(item, "platform"), state = MusicDto.Text(item, "state");
        string name = platform switch { "wy" => "网易云音乐", "tx" => "QQ 音乐", "kg" => "酷狗音乐",
            _ => throw new MusicOperationException("INVALID_RESPONSE") };
        MusicDto.Require(state is "none" or "saved" or "verified" or "revoked");
        long generation = MusicDto.Integer(item, "generation");
        var profile = item.GetProperty("profile");
        if (profile.ValueKind == JsonValueKind.Null) return new(platform, name, state, Generation: generation);
        MusicDto.Require(MusicDto.Text(profile, "platform") == platform);
        string userId = MusicDto.Text(profile, "userId");
        var displayName = profile.GetProperty("displayName");
        MusicDto.Require(displayName.ValueKind == JsonValueKind.String);
        return new(platform, name, state, string.IsNullOrEmpty(displayName.GetString()) ? userId : displayName.GetString()!,
            userId, generation);
    }
    private void UpdateAccount(MusicPlatformAccount value)
    {
        int index = Accounts.ToList().FindIndex(item => item.Platform == value.Platform);
        MusicDto.Require(index >= 0);
        bool selected = selectedAccount?.Platform == value.Platform;
        // ComboBox temporarily emits null when the selected collection row is replaced.
        // That is publication, not a user platform switch/cancellation of the successful CAS.
        publishingAccounts = true;
        try
        {
            if (selected) selectedAccount = value;
            Accounts[index] = value;
        }
        finally { publishingAccounts = false; }
        // Publish after the collection replacement has finished its selection
        // transaction, otherwise ComboBox can clear the newly rebound selection.
        AccountBindings();
        RecommendationBindings();
    }
    private async Task EnsureAccountsAsync()
    {
        _ = await StartAccountRead(force: false);
    }
    private async Task EnsureAccountsForRequestAsync(CancellationToken token = default)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var result = await StartAccountRead(force: false).WaitAsync(token);
            if (stopping) throw new OperationCanceledException(token);
            if (result.Epoch != accountEpoch || result.Observation != Volatile.Read(ref accountObservation)) continue;
            if (result.ErrorCode is { } code) throw new MusicOperationException(code);
            return;
        }
    }
    public async Task ReadAccountsAsync()
    {
        _ = await StartAccountRead(force: true);
    }
    private Task<AccountReadResult> StartAccountRead(bool force, bool allowStopping = false)
    {
        if (!accountRead.IsCompleted) return accountRead;
        if (!force && accountsLoaded && loadedAccountObservation == Volatile.Read(ref accountObservation))
            return Task.FromResult(new AccountReadResult(accountEpoch, loadedAccountObservation, null));
        if (stopping && !allowStopping) return Task.FromResult(new AccountReadResult(accountEpoch, Volatile.Read(ref accountObservation), "CANCELLED"));
        var priorCommands = commandTail;
        return accountRead = Observe();

        async Task<AccountReadResult> Observe()
        {
            await Task.Yield(); // Publish accountRead before callbacks can request another read.
            if (!allowStopping) await priorCommands;
            if (stopping && !allowStopping)
                return new(accountEpoch, Volatile.Read(ref accountObservation), "CANCELLED");
            using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            accountReadCancellation = request;
            long epoch = -1;
            long observation = -1;
            string? errorCode = "CANCELLED";
            async Task Read()
            {
                epoch = accountEpoch;
                observation = Volatile.Read(ref accountObservation);
                try
                {
                    var reply = await Call("music.accounts.list", cancellationToken: request.Token);
                    var items = reply.GetProperty("items");
                    MusicDto.Require(items.ValueKind == JsonValueKind.Array && items.GetArrayLength() == 3);
                    var rows = items.EnumerateArray().Select(AccountRow).ToArray();
                    MusicDto.Require(rows.Select(item => item.Platform).Distinct().Count() == 3);
                    if (epoch != accountEpoch || observation != Volatile.Read(ref accountObservation) || stopping && !allowStopping) return;
                    foreach (var row in rows) UpdateAccount(row);
                    accountsLoaded = true;
                    loadedAccountObservation = observation;
                    accountSaveUncertain = false;
                    accountStatus = "";
                    errorCode = null;
                    AccountBindings();
                }
                catch (Exception error)
                {
                    errorCode = error switch
                    {
                        MusicOperationException known => known.Code,
                        BridgeException known => known.Code,
                        OperationCanceledException => "CANCELLED",
                        _ => "INVALID_RESPONSE"
                    };
                    if ((!stopping || allowStopping) && epoch == accountEpoch &&
                        observation == Volatile.Read(ref accountObservation))
                    {
                        accountStatus = AccountError(error);
                        AccountBindings();
                    }
                }
            }
            // Wait for prior mutations, but do not occupy the foreground command
            // lane. A later mutation fences this read with accountEpoch.
            try { await Read(); }
            finally { if (ReferenceEquals(accountReadCancellation, request)) accountReadCancellation = null; }
            return new(epoch, observation, errorCode);
        }
    }
    public Task ImportAccountAsync(string cookie)
    {
        string? platform = selectedAccount?.Platform;
        if (stopping || platform is null || string.IsNullOrWhiteSpace(cookie)) return Task.CompletedTask;
        if (cookie.Length > 32768 || cookie.Any(character => character < 32 || character == 127))
        { accountStatus = "会话格式无效"; AccountBindings(); return Task.CompletedTask; }
        return ChangeAccountAsync(platform, "import", new { platform, cookie });
    }
    public Task ValidateAccountAsync()
    {
        string? platform = selectedAccount?.Platform;
        return platform is null ? Task.CompletedTask : ChangeAccountAsync(platform, "validate", new { platform });
    }
    private bool IsCurrentAccount(MusicPlatformAccount account) =>
        Accounts.Any(current => current.Platform == account.Platform && current.Generation == account.Generation &&
            current.UserId == account.UserId && current.State is "saved" or "verified");
    internal bool CanLogoutAccount(MusicPlatformAccount account) =>
        !stopping && !disposed && !accountSaveUncertain && !AccountBusy && IsCurrentAccount(account);

    public Task LogoutAccountAsync(MusicPlatformAccount account, bool confirmed)
    {
        if (!confirmed || !CanLogoutAccount(account)) return Task.CompletedTask;
        return ChangeAccountAsync(account.Platform, "logout", new { platform = account.Platform }, account);
    }
    private async Task ChangeAccountAsync(string platform, string operation, object parameters,
        MusicPlatformAccount? expectedAccount = null, CancellationToken token = default)
    {
        bool changedAccount = false;
        var epoch = ++accountEpoch;
        accountCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token);
        // Admission is visible even while the shared command queue has not dispatched it.
        accountCancellation = cancellation; accountOperationPlatform = platform;
        accountQrOperation = operation.StartsWith("qr.", StringComparison.Ordinal);
        accountSmsOperation = operation.StartsWith("sms.", StringComparison.Ordinal);
        accountSmsSending = operation == "sms.send";
        // KG begin durably establishes device identity before its first request.
        accountMayWrite = operation != "sms.send" && (operation != "qr.begin" || platform == "kg");
        if (!accountSmsOperation) ClearAccountSms();
        if (operation != "qr.poll") { ClearAccountQr(); RetireCatalogSession(); }
        accountStatus = ""; AccountBindings();
        try { await RunAsync(async () =>
        {
            if (stopping || epoch != accountEpoch) return;
            if (expectedAccount is not null && !IsCurrentAccount(expectedAccount))
            {
                accountStatus = "账号会话已变更";
                return;
            }
            try
            {
                var reply = accountSmsOperation
                    ? await CallAccountSms(operation, parameters, cancellation.Token)
                    : await Call("music.accounts." + operation, parameters, cancellation.Token);
                if (stopping || epoch != accountEpoch) return;
                if (operation == "sms.send")
                {
                    MusicDto.Require(MusicDto.Text(reply, "platform") == platform &&
                        reply.TryGetProperty("sent", out var sent) && sent.ValueKind == JsonValueKind.True);
                    accountStatus = "验证码已发送"; AccountBindings(); return;
                }
                if (operation == "qr.begin") { ReadAccountQr(reply, platform); return; }
                if (operation == "qr.poll" && !PublishQrPoll(reply, platform)) return;
                var row = AccountRow(reply.GetProperty("account"));
                MusicDto.Require(row.Platform == platform);
                UpdateAccount(row);
                accountsLoaded = false;
                changedAccount = true;
                if (operation == "sms.login") ClearAccountSms();
                accountSaveUncertain = false;
                accountStatus = operation == "logout" ? "已退出" : "会话验证成功";
                // Retire request authority, not the last successful home display.
                RetireCatalogSession();
                RetireLyrics();
            }
            catch (OperationCanceledException)
            {
                if (epoch == accountEpoch)
                {
                    if (operation.StartsWith("qr.", StringComparison.Ordinal)) ClearAccountQr();
                    accountStatus = operation == "sms.send" ? "短信发送结果未确认" : "已取消";
                }
            }
            catch (Exception error)
            {
                if (epoch == accountEpoch && error is MusicOperationException { Code: "COMMIT_UNKNOWN" or "HANDOFF_UNCONFIRMED" })
                    accountSaveUncertain = true;
                if (epoch == accountEpoch && operation.StartsWith("qr.", StringComparison.Ordinal)) ClearAccountQr();
                if (epoch == accountEpoch) accountStatus = AccountError(error);
            }
        }); }
        finally
        {
            if (ReferenceEquals(accountCancellation, cancellation))
            {
                accountCancellation = null; accountOperationPlatform = null; accountQrOperation = false; accountMayWrite = false;
                accountSmsOperation = false; accountSmsSending = false;
            }
            AccountBindings();
            if (changedAccount) BeginHomeAccountRecovery(forceRead: true);
        }
    }
    public Task CancelAccountAsync()
    {
        if (!accountSettlement.IsCompleted) return accountSettlement;
        if (stopping || (accountOperationPlatform ?? accountQr?.Platform ?? (HasAccountSms ? "wy" : null)) is not { } platform)
            return Task.CompletedTask;
        bool sending = accountSmsSending;
        if (accountOperationPlatform is not null && accountMayWrite) accountSaveUncertain = true;
        long epoch = ++accountEpoch;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        accountSettlement = completion.Task;
        accountCancellation?.Cancel(); ClearAccountQr(); ClearAccountSms();
        accountStatus = sending ? "短信发送结果未确认" : "已取消";
        RetireLyrics();
        AccountBindings();
        _ = Settle();
        return completion.Task;

        async Task Settle()
        {
            await Task.Yield(); // Publish the settlement task before cancellation callbacks can re-enter.
            try
            {
                await Call("music.accounts.cancel", new { platform }, lifetime.Token);
                if (epoch != accountEpoch) return;
                AccountReadResult result;
                do
                {
                    result = await StartAccountRead(force: true, allowStopping: true);
                }
                while (epoch == accountEpoch && (result.Epoch != epoch ||
                    result.Observation != Volatile.Read(ref accountObservation)));
                if (epoch != accountEpoch) return;
                if (result.ErrorCode is { } code) throw new MusicOperationException(code);
                accountStatus = accountSaveUncertain ? "取消结果待核对，请读取账号状态" :
                    sending ? "短信发送结果未确认" : "已取消";
            }
            catch
            {
                if (epoch == accountEpoch)
                {
                    if (!sending) accountSaveUncertain = true;
                    accountStatus = sending ? "短信发送结果未确认" : "取消结果待核对，请读取账号状态";
                }
            }
            finally
            {
                completion.TrySetResult();
                AccountBindings();
            }
        }
    }
}
