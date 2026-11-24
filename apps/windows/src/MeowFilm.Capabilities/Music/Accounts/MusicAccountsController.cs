using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using MeowFilm.Capabilities.Accounts;
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

/// <summary>Standalone Layer-2 owner for music platform accounts, QR/SMS login, details, sync, and import/export.</summary>
public sealed partial class MusicAccountsController : MusicController
{
    private readonly record struct AccountReadResult(long Epoch, long Observation, string? ErrorCode);
    private readonly MusicSettingsController settings;
    private readonly ConcurrentDictionary<string, long> platformObservations = new();

    internal MusicAccountsController(MusicRuntimeContext ctx, MusicSettingsController settings) : base(ctx)
    {
        this.settings = settings;
        LibrarySync = new(
            application.LibrarySync,
            Receipts,
            ConfirmResetSyncAccountAsync,
            () => stopping || disposed);
        application.LibrarySyncChanged += LibrarySyncChanged;
        InitializePlatformSettings();
    }

    public MusicLibrarySyncViewModel LibrarySync { get; }

    // Explicit cross-domain events wired at MusicCapability composition root.
    internal event Action<string>? AccountUpdated;
    internal event Action? AccountsReloaded;
    internal event Action? AccountSessionChanging;
    internal event Action? AccountSessionChanged;
    internal event Action<bool>? AccountChangeSettled;
    internal event Action? AccountCancelStarted;
    internal event Action? AccountCancelSettled;
    internal event Func<long, CancellationToken, Task>? LibrarySynced;

    internal long RecordPlatformObservation(string platform)
    {
        Interlocked.Increment(ref accountObservation);
        return platformObservations.AddOrUpdate(platform, 1, (_, value) => value + 1);
    }

    internal long PlatformObservation(string platform) => platformObservations.GetValueOrDefault(platform);

    public ObservableCollection<MusicPlatformAccount> Accounts { get; } =
        [
            new("wy", MusicPlatformInfo.DisplayName("wy"), "unknown"),
            new("tx", MusicPlatformInfo.DisplayName("tx"), "unknown"),
            new("kg", MusicPlatformInfo.DisplayName("kg"), "unknown")
        ];

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

    internal long AccountEpoch => accountEpoch;
    internal bool AccountSaveUncertain => accountSaveUncertain;
    internal Task AccountReadTask => accountRead;
    internal Task AccountSettlementTask => accountSettlement;
    internal bool CanEditFeedback => settings.CanEditFeedback;

    internal void MarkAccountObserved() => Interlocked.Increment(ref accountObservation);
    internal void MarkAccountsStale() => accountsLoaded = false;
    internal bool HasSettledCurrentAccount(string platform) => accountsLoaded &&
        loadedAccountObservation == Volatile.Read(ref accountObservation) &&
        accountOperationPlatform != platform &&
        Accounts.Single(account => account.Platform == platform).State is "saved" or "verified";

    internal void CancelReadForShutdown()
    {
        // Cancellation settlement owns its confirming read. Cancelling that
        // read here would turn an ordinary page exit into an unknown receipt.
        if (accountSettlement.IsCompleted) accountReadCancellation?.Cancel();
    }

    internal override void RefreshAvailability()
    {
        base.RefreshAvailability();
        AccountBindings();
    }

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
                else { ClearAccountQrQuiet(); ClearAccountSmsQuiet(); }
            }
            selectedAccount = value; AccountBindings();
        }
    }
    public bool AccountBusy => accountCancellation is not null || !accountSettlement.IsCompleted;
    public bool CanManageAccount => !stopping && !accountTransferBusy && selectedAccount is not null && !AccountBusy && !accountQrSaving;
    public string AccountStatus => accountStatus;
    public string AccountNotice => accountStatus is "等待扫码" or "等待确认" ? "" : accountStatus;

    internal void AccountBindings()
    {
        Changed(nameof(SelectedAccount));
        Changed(nameof(AccountBusy));
        Changed(nameof(CanManageAccount));
        Changed(nameof(CanTransferAccounts));
        Changed(nameof(AccountStatus));
        Changed(nameof(AccountNotice));
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
        MusicOperationException { Code: "DEVICE_CONTEXT_REQUIRED" or "SIGNATURE_REQUIRED" } => "平台设备认证未通过",
        MusicOperationException { Code: "DEVICE_LIMIT" } => "平台登录设备数已达上限",
        MusicOperationException { Code: "STALE_SESSION" or "SESSION_CHANGED" or "IDENTITY_MISMATCH" or "SUPERSEDED" } => "账号会话已变更",
        MusicOperationException { Code: "COMMIT_UNKNOWN" or "HANDOFF_UNCONFIRMED" } => "保存结果未确认，请读取账号状态",
        MusicOperationException { Code: "QR_EXPIRED" } => "二维码已过期",
        MusicOperationException { Code: "BUSY" } => "登录请求尚未结束",
        MusicOperationException { Code: "INVALID_CODE" } => "验证码无效或已过期",
        MusicOperationException { Code: "INVALID_PHONE" } => "验证码发送失败，手机号无效",
        MusicOperationException { Code: "ACCOUNT_SELECTION_REQUIRED" } => "该手机号关联多个账号，请使用二维码登录",
        MusicOperationException { Code: "RATE_LIMITED" } => "请求过于频繁，请稍后重试",
        MusicOperationException { Code: "SMS_SEND_UNCONFIRMED" } => "短信发送结果未确认",
        MusicOperationException { Code: "UNSUPPORTED" } => "此账号协议尚未支持",
        _ => ErrorText(error)
    };

    private static MusicPlatformAccount AccountRow(MusicAccountStatusRecord item)
    {
        MusicDto.Require(item.Platform is "wy" or "tx" or "kg");
        string name = MusicPlatformInfo.DisplayName(item.Platform);
        MusicDto.Require(item.State is "none" or "saved" or "verified" or "revoked");
        if (item.Profile is not { } profile)
            return new(item.Platform, name, item.State, Generation: item.Generation);
        MusicDto.Require(profile.Platform == item.Platform && !string.IsNullOrEmpty(profile.UserId));
        return new(item.Platform, name, item.State,
            string.IsNullOrEmpty(profile.DisplayName) ? profile.UserId : profile.DisplayName,
            profile.UserId, item.Generation);
    }

    private void UpdateAccount(MusicPlatformAccount value, bool notify = true)
    {
        int index = -1;
        for (int i = 0; i < Accounts.Count; i++)
            if (Accounts[i].Platform == value.Platform) { index = i; break; }
        MusicDto.Require(index >= 0);
        if (!Equals(Accounts[index], value))
        {
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
        }
        if (!notify) return;
        // Publish after the collection replacement has finished its selection
        // transaction, otherwise ComboBox can clear the newly rebound selection.
        AccountBindings();
        AccountUpdated?.Invoke(value.Platform);
    }

    internal async Task EnsureAccountsAsync()
    {
        _ = await StartAccountRead(force: false);
    }

    internal async Task EnsureAccountsForRequestAsync(CancellationToken token = default)
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
        var result = await StartAccountRead(force: true);
        if (!stopping && result.ErrorCode is null && result.Epoch == accountEpoch &&
            result.Observation == Volatile.Read(ref accountObservation))
        {
            // An explicit read consumes the notice even when it joined a passive read.
            accountStatus = "";
            AccountBindings();
        }
    }

    private Task<AccountReadResult> StartAccountRead(bool force, bool allowStopping = false)
    {
        if (!accountRead.IsCompleted) return accountRead;
        if (!force && accountsLoaded && loadedAccountObservation == Volatile.Read(ref accountObservation))
            return Task.FromResult(new AccountReadResult(accountEpoch, loadedAccountObservation, null));
        if (stopping && !allowStopping) return Task.FromResult(new AccountReadResult(accountEpoch, Volatile.Read(ref accountObservation), "CANCELLED"));
        var priorCommands = ctx.CommandTail;
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
                    var reply = await application.ListAccountsAsync(request.Token);
                    MusicDto.Require(reply.Items.Count == 3);
                    var rows = reply.Items.Select(AccountRow).ToArray();
                    MusicDto.Require(rows.Select(item => item.Platform).Distinct().Count() == 3);
                    if (epoch != accountEpoch || observation != Volatile.Read(ref accountObservation) || stopping && !allowStopping) return;
                    foreach (var row in rows) UpdateAccount(row, notify: false);
                    accountsLoaded = true;
                    loadedAccountObservation = observation;
                    accountSaveUncertain = false;
                    // Passive reconciliation must not consume an operation's result notice.
                    errorCode = null;
                    AccountBindings();
                    AccountsReloaded?.Invoke();
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
                        observation == Volatile.Read(ref accountObservation) && (force || accountStatus.Length == 0))
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

    private bool IsCurrentAccount(MusicPlatformAccount account) =>
        Accounts.Any(current => current.Platform == account.Platform && current.Generation == account.Generation &&
            current.UserId == account.UserId && current.State is "saved" or "verified");

    internal bool CanLogoutAccount(MusicPlatformAccount account) =>
        !stopping && !disposed && !accountTransferBusy && !accountSaveUncertain && !AccountBusy && IsCurrentAccount(account);

    public Task LogoutAccountAsync(MusicPlatformAccount account, bool confirmed)
    {
        if (!confirmed || !CanLogoutAccount(account)) return Task.CompletedTask;
        return ChangeAccountAsync(account.Platform, "logout",
            token => application.LogoutAccountAsync(account.Platform, token),
            status => status, account);
    }

    private async Task ChangeAccountAsync<TResult>(string platform, string operation,
        Func<CancellationToken, Task<TResult>> invoke,
        Func<TResult, MusicAccountStatusRecord?> handle,
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
        if (!accountSmsOperation) ClearAccountSmsQuiet();
        if (operation != "qr.poll") { ClearAccountQrQuiet(); AccountSessionChanging?.Invoke(); }
        if (operation != "qr.poll") accountStatus = "";
        AccountBindings();
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
                    ? await CallAccountSmsAsync(operation, invoke, cancellation.Token)
                    : await invoke(cancellation.Token);
                if (stopping || epoch != accountEpoch) return;
                if (handle(reply) is not { } account) return;
                var row = AccountRow(account);
                MusicDto.Require(row.Platform == platform);
                UpdateAccount(row);
                accountsLoaded = false;
                changedAccount = true;
                if (operation == "sms.login") ClearAccountSms();
                accountSaveUncertain = false;
                accountStatus = operation == "logout" ? "已退出" : "会话验证成功";
                // Retire request authority, not the last successful home display.
                AccountSessionChanged?.Invoke();
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
                bool retryablePoll = operation == "qr.poll" &&
                    error is MusicOperationException { Code: "RATE_LIMITED" or "BUSY" };
                if (epoch == accountEpoch && operation.StartsWith("qr.", StringComparison.Ordinal) && !retryablePoll) ClearAccountQr();
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
            if (changedAccount) AccountChangeSettled?.Invoke(epoch == accountEpoch);
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
        accountCancellation?.Cancel(); ClearAccountQrQuiet(); ClearAccountSmsQuiet();
        accountStatus = sending ? "短信发送结果未确认" : "已取消";
        AccountCancelStarted?.Invoke();
        AccountBindings();
        _ = Settle();
        return completion.Task;

        async Task Settle()
        {
            bool settled = false;
            await Task.Yield(); // Publish the settlement task before cancellation callbacks can re-enter.
            try
            {
                await application.CancelAccountAsync(platform, lifetime.Token);
                if (epoch != accountEpoch) return;
                AccountReadResult result;
                bool joinedRead;
                do
                {
                    joinedRead = !accountRead.IsCompleted;
                    result = await StartAccountRead(force: false, allowStopping: true);
                }
                while (epoch == accountEpoch && (result.Epoch != epoch ||
                    result.Observation != Volatile.Read(ref accountObservation) ||
                    // An ordinary observation joined above can be retired by
                    // shutdown. It is not the settlement's confirming receipt.
                    joinedRead && result.ErrorCode == "CANCELLED" && !lifetime.IsCancellationRequested));
                if (epoch != accountEpoch) return;
                if (result.ErrorCode is { } code) throw new MusicOperationException(code);
                accountStatus = accountSaveUncertain ? "取消结果待核对，请读取账号状态" :
                    sending ? "短信发送结果未确认" : "已取消";
                settled = true;
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
                if (settled && epoch == accountEpoch) AccountCancelSettled?.Invoke();
            }
        }
    }
}
