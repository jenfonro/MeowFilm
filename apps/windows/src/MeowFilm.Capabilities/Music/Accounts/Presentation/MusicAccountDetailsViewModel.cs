using System.ComponentModel;
using System.Runtime.CompilerServices;
using MeowFilm.Capabilities.Accounts;

namespace MeowFilm.Capabilities;

/// <summary>Display-only account consumer. Activation reads local state; only Refresh contacts a platform.</summary>
public sealed class MusicAccountDetailsViewModel(MusicNativeApplication source) : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly MusicNativeApplication source = source ?? throw new ArgumentNullException(nameof(source));
    private MusicPlatformAccount? account;
    private MusicAccountDetails? snapshot;
    private CancellationTokenSource? visible, request, expiration;
    private Task requests = Task.CompletedTask, expirationTask = Task.CompletedTask;
    private Task? disposal;
    private long epoch;
    private bool busy, disposed;
    private string status = "";

    private static readonly string[] AllProperties =
    [
        nameof(IsBusy), nameof(CanRefresh), nameof(DisplayName), nameof(Avatar), nameof(Status), nameof(MembershipSummary)
    ];

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<MusicAccountDetails>? Observed;
    public bool IsBusy => busy;
    public bool CanRefresh => !disposed && visible is not null && !busy &&
        (snapshot?.State ?? account?.State) is "saved" or "verified";
    public string DisplayName => snapshot?.Profile?.DisplayName ?? "";
    public string Avatar => snapshot?.Profile?.Avatar is { } address &&
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" &&
        uri.UserInfo.Length == 0 ? address : "";
    public string Status => status;
    public string MembershipSummary
    {
        get
        {
            if (snapshot is null) return visible is null ? "" : "权益未确认";
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (snapshot.Membership.Vip == true && snapshot.Membership.ExpiresAt is { } expired && expired <= now)
                return "VIP无效";
            if (snapshot.MembershipFreshness != MusicAccountDetailsFreshness.Fresh ||
                snapshot.MembershipRecheckAt <= now || snapshot.Membership.Vip is null) return "权益未确认";
            if (snapshot.Membership.Vip == false) return "VIP无效";
            string tier = snapshot.Membership.Tier switch
            {
                "svip" or "huge_vip" or "concept:svip" => "SVIP",
                _ => "VIP"
            };
            if (snapshot.Membership.ExpiresAt is not { } expiry) return tier + "有效";
            try
            {
                return tier + "有效 · " + DateTimeOffset.FromUnixTimeMilliseconds(expiry)
                    .ToLocalTime().ToString("yyyy.MM.dd");
            }
            catch (ArgumentOutOfRangeException) { return "权益未确认"; }
        }
    }

    public void SelectAccount(MusicPlatformAccount? value)
    {
        if (disposed || Equals(account, value)) return;
        if (value is not null && snapshot is not null &&
            snapshot.Platform == value.Platform && snapshot.Generation == value.Generation &&
            snapshot.State == value.State && (snapshot.Profile?.UserId ?? "") == value.UserId)
        {
            // Publishing this very observation to the account selector is not
            // a new credential selection or a second metadata request.
            account = value; Notify(); return;
        }
        epoch++; Cancel(request); Cancel(expiration);
        account = value; snapshot = null; busy = false; status = "";
        if (visible is not null && value is not null) _ = ReadAsync();
        else Notify();
    }
    public void Invalidate(string platform)
    {
        if (disposed || account?.Platform != platform) return;
        epoch++; Cancel(request); Cancel(expiration);
        snapshot = null; busy = false; status = "";
        if (visible is not null) _ = ReadAsync();
        else Notify();
    }
    public Task ActivateAsync(CancellationToken token = default)
    {
        if (disposed) return Task.CompletedTask;
        if (visible is not null) return requests;
        visible = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (account is not null) return ReadAsync();
        Notify();
        return Task.CompletedTask;
    }
    public Task ReadAsync() => Start(refresh: false);
    public Task RefreshAsync() => !CanRefresh ? Task.CompletedTask : Start(refresh: true);

    private Task Start(bool refresh)
    {
        if (disposed || visible is null || account is null || busy) return Task.CompletedTask;
        var selected = account; var ticket = ++epoch; var previous = requests;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(visible.Token);
        Cancel(expiration); Cancel(request); request = cancellation;
        busy = true; status = refresh ? "正在刷新" : "正在读取"; Notify();
        return requests = Run();

        async Task Run()
        {
            await Task.Yield();
            try
            {
                // A replaced page or selection never frees an old physical request early.
                await previous;
                cancellation.Token.ThrowIfCancellationRequested();
                var result = refresh
                    ? await source.RefreshDetailsAsync(selected.Platform, selected.Generation, cancellation.Token)
                    : await source.ReadDetailsAsync(selected.Platform, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (!Current()) return;
                if (result.Platform != selected.Platform || result.Generation < selected.Generation ||
                    result.Generation > 9_007_199_254_740_991L ||
                    refresh && result.Generation != selected.Generation ||
                    result.State is not ("none" or "saved" or "verified" or "revoked") ||
                    refresh && selected.State is not ("saved" or "verified"))
                    throw new MusicOperationException("STALE_SESSION");
                snapshot = result;
                status = result.ErrorCode is { Length: > 0 } code ? Error(code) :
                    result.VerificationDeferred ? "等待平台验证" : "";
                foreach (var observer in Observed?.GetInvocationList() ?? [])
                {
                    try { ((Action<MusicAccountDetails>)observer)(result); }
                    catch { /* A display observer cannot alter the authoritative read/CAS outcome. */ }
                }
            }
            catch (OperationCanceledException) { if (Current()) status = "已取消"; }
            catch (Exception error)
            {
                if (!Current()) return;
                string code = error switch
                {
                    MusicAccountException e => e.Code,
                    MusicOperationException e => e.Code,
                    _ => "INVALID_RESPONSE"
                };
                // Never retain another account's display after a generation/credential fence.
                if (code is "STALE_SESSION" or "SESSION_CHANGED" or "IDENTITY_MISMATCH" or
                    "AUTH_REQUIRED" or "AUTH_EXPIRED" or "INVALID_CREDENTIALS" or
                    "COMMIT_UNKNOWN" or "HANDOFF_UNCONFIRMED" or "CLOSED")
                    snapshot = null;
                status = Error(code);
            }
            finally
            {
                if (ReferenceEquals(request, cancellation)) request = null;
                cancellation.Dispose();
                if (Current())
                {
                    busy = false; Notify();
                    ScheduleExpiration(ticket);
                }
            }
            bool Current() => !disposed && visible is not null && ticket == epoch;
        }
    }
    private void ScheduleExpiration(long ticket)
    {
        Cancel(expiration);
        if (visible is null || snapshot is null) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long[] deadlines = [snapshot.ProfileExpiresAt, snapshot.MembershipRecheckAt, snapshot.Membership.ExpiresAt ?? 0];
        long next = deadlines.Where(t => t > now && t - now <= 30L * 24 * 60 * 60 * 1000).DefaultIfEmpty(0).Min();
        if (next == 0) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(visible.Token);
        expiration = cancellation;
        expirationTask = Expire();
        async Task Expire()
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, next - now)), cancellation.Token);
                if (ticket != epoch || visible is null || disposed) return;
                // One deadline-driven local read, not a polling or HTTP refresh loop.
                if (account is not null && !busy) await ReadAsync();
                else Notify();
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(expiration, cancellation)) expiration = null;
                cancellation.Dispose();
            }
        }
    }
    public async Task DeactivateAsync()
    {
        epoch++; var old = visible; visible = null;
        Cancel(request); Cancel(expiration); Cancel(old);
        snapshot = null; busy = false; status = ""; Notify();
        try { await Task.WhenAll(requests, expirationTask); }
        finally { old?.Dispose(); }
    }
    public ValueTask DisposeAsync()
    {
        if (disposal is not null) return new(disposal);
        disposed = true;
        return new(disposal = DisposeCore());
        async Task DisposeCore()
        {
            // Cache one receipt before cancellation or bindings can re-enter.
            await Task.Yield();
            await DeactivateAsync();
        }
    }
    private static void Cancel(CancellationTokenSource? source)
    {
        try { source?.Cancel(); } catch (ObjectDisposedException) { }
    }
    private void Notify()
    {
        foreach (string name in AllProperties) Changed(name);
    }
    private void Changed([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new(name));
    private static string Error(string code) => code switch
    {
        "AUTH_REQUIRED" or "AUTH_EXPIRED" or "INVALID_CREDENTIALS" => "账号会话无效或已过期",
        "STALE_SESSION" or "SESSION_CHANGED" or "IDENTITY_MISMATCH" => "账号会话已变更",
        "COMMIT_UNKNOWN" or "HANDOFF_UNCONFIRMED" => "账号写入待核对",
        "RATE_LIMITED" => "请求过于频繁",
        "VERIFICATION_REQUIRED" or "DEVICE_CONTEXT_REQUIRED" => "等待平台验证",
        "CANCELLED" => "已取消",
        "CLOSED" => "已停止",
        "UNSUPPORTED" => "资料读取不可用",
        _ => "资料或权益读取失败"
    };
}
