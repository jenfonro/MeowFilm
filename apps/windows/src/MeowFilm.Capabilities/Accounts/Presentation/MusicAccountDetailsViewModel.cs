using System.ComponentModel;
using System.Runtime.CompilerServices;
using MeowFilm.Capabilities.Accounts;

namespace MeowFilm.Capabilities;

/// <summary>Display-only account consumer. Activation reads local state; only Refresh contacts a platform.</summary>
public sealed class MusicAccountDetailsViewModel(IMusicAccountDetailsSource source) : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IMusicAccountDetailsSource source = source ?? throw new ArgumentNullException(nameof(source));
    private MusicPlatformAccount? account;
    private MusicAccountDetails? snapshot;
    private CancellationTokenSource? visible, request, expiration;
    private Task requests = Task.CompletedTask, expirationTask = Task.CompletedTask;
    private Task? disposal;
    private long epoch;
    private bool busy, disposed;
    private string status = "";

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<MusicAccountDetails>? Observed;
    public bool HasAccount => account is not null;
    public bool HasSession => (snapshot?.State ?? account?.State) is "saved" or "verified" or "revoked";
    public bool HasProfile => snapshot?.Profile is not null;
    public bool IsBusy => busy;
    public bool CanRefresh => !disposed && visible is not null && !busy &&
        (snapshot?.State ?? account?.State) is "saved" or "verified";
    public bool CanRead => !disposed && visible is not null && !busy && account is not null;
    public string DisplayName => snapshot?.Profile?.DisplayName ?? "";
    public string UserId => snapshot?.Profile?.UserId ?? "";
    public string Avatar => snapshot?.Profile?.Avatar is { } address &&
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 ? address : "";
    public string Status => status;
    public string ProfileState => snapshot?.ProfileFreshness switch
    {
        MusicAccountDetailsFreshness.Fresh => "资料已更新",
        MusicAccountDetailsFreshness.Stale => "资料已过期",
        MusicAccountDetailsFreshness.Deferred => "资料待验证",
        _ => ""
    };
    public string MembershipText
    {
        get
        {
            if (snapshot is null || snapshot.MembershipFreshness != MusicAccountDetailsFreshness.Fresh ||
                snapshot.MembershipRecheckAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) return "权益未确认";
            if (snapshot.Membership.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                return "会员已过期";
            return snapshot.Membership.Vip switch
            {
                true => (snapshot.Membership.Tier is "" or "none" or "unknown" ? "VIP" : snapshot.Membership.Tier) + " · 会员有效",
                false => "非会员", _ => "权益未确认"
            };
        }
    }
    public string MembershipSummary
    {
        get
        {
            if (snapshot is null || snapshot.MembershipFreshness != MusicAccountDetailsFreshness.Fresh ||
                snapshot.MembershipRecheckAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() ||
                snapshot.Membership.Vip != true) return "";
            string tier = snapshot.Membership.Tier switch
            {
                "svip" or "huge_vip" or "concept:svip" => "SVIP",
                _ => "VIP"
            };
            if (snapshot.Membership.ExpiresAt is not { } expiry ||
                expiry <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) return "";
            try
            {
                return tier + " · " + DateTimeOffset.FromUnixTimeMilliseconds(expiry)
                    .ToLocalTime().ToString("yyyy.M.d");
            }
            catch (ArgumentOutOfRangeException) { return ""; }
        }
    }
    public string MembershipExpires => FormatTime(snapshot?.Membership.ExpiresAt);
    public string ProfileUpdated => FormatTime(snapshot?.ProfileFetchedAt);
    public string MembershipChecked => FormatTime(snapshot?.MembershipCheckedAt);
    public string AudioLevels => snapshot?.MembershipFreshness == MusicAccountDetailsFreshness.Fresh &&
        snapshot.MembershipRecheckAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() &&
        snapshot.AudioLevels is { Count: > 0 } levels ? string.Join(" / ", levels) : "";

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
        Notify();
        if (visible is not null && value is not null) _ = ReadAsync();
    }
    public void Invalidate(string platform)
    {
        if (disposed || account?.Platform != platform) return;
        epoch++; Cancel(request); Cancel(expiration);
        snapshot = null; busy = false; status = ""; Notify();
        if (visible is not null) _ = ReadAsync();
    }
    public Task ActivateAsync(CancellationToken token = default)
    {
        if (disposed) return Task.CompletedTask;
        if (visible is not null) return requests;
        visible = CancellationTokenSource.CreateLinkedTokenSource(token);
        Notify();
        return ReadAsync();
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
                    refresh && selected.State is not ("saved" or "verified") ||
                    result.Profile is { } profile &&
                    (profile.Platform != selected.Platform ||
                     result.Generation == selected.Generation && selected.UserId.Length > 0 &&
                     profile.UserId != selected.UserId))
                    throw new MusicOperationException("STALE_SESSION");
                if (result.Membership is null || result.Membership.Tier is null || result.Membership.Tier.Length > 64 ||
                    result.Membership.Tier.Any(char.IsControl) || result.AudioLevels is null || result.AudioLevels.Count > 32 ||
                    !Enum.IsDefined(result.ProfileFreshness) || !Enum.IsDefined(result.MembershipFreshness) ||
                    result.State is "none" or "revoked" && (result.Profile is not null ||
                        result.MembershipFreshness == MusicAccountDetailsFreshness.Fresh) ||
                    result.AudioLevels.Any(s => s is null || s.Length > 64 || s.Any(char.IsControl)))
                    throw new MusicOperationException("INVALID_RESPONSE");
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
                    "AUTH_REQUIRED" or "AUTH_EXPIRED" or "FORBIDDEN" or "VERIFICATION_REQUIRED" or
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
                Notify();
                await ReadAsync();
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
        foreach (string name in new[] { nameof(HasAccount), nameof(HasSession), nameof(HasProfile), nameof(IsBusy), nameof(CanRefresh),
            nameof(CanRead), nameof(DisplayName), nameof(UserId), nameof(Avatar), nameof(Status), nameof(ProfileState),
            nameof(MembershipText), nameof(MembershipSummary), nameof(MembershipExpires),
            nameof(ProfileUpdated), nameof(MembershipChecked), nameof(AudioLevels) })
            Changed(name);
    }
    private void Changed([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new(name));
    private static string FormatTime(long? timestamp)
    {
        if (timestamp is not > 0) return "";
        try { return DateTimeOffset.FromUnixTimeMilliseconds(timestamp.Value).ToLocalTime().ToString("yyyy-MM-dd HH:mm"); }
        catch (ArgumentOutOfRangeException) { return ""; }
    }
    private static string Error(string code) => code switch
    {
        "AUTH_REQUIRED" or "AUTH_EXPIRED" => "账号会话无效或已过期",
        "STALE_SESSION" or "SESSION_CHANGED" or "IDENTITY_MISMATCH" => "账号会话已变更",
        "COMMIT_UNKNOWN" or "HANDOFF_UNCONFIRMED" => "账号写入待核对",
        "RATE_LIMITED" => "请求过于频繁",
        "VERIFICATION_REQUIRED" or "DEVICE_REQUIRED" => "等待平台验证",
        "CANCELLED" => "已取消",
        "CLOSED" => "已停止",
        "UNSUPPORTED" => "资料读取不可用",
        _ => "资料或权益读取失败"
    };
}
