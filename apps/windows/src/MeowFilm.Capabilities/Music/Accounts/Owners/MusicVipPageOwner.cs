// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Accounts;

internal sealed class MusicVipPageOwner : IAsyncDisposable
{
    private const string Platform = "wy";
    private readonly MusicAccountOwner accounts;
    private readonly MusicPlatformClient client;
    private readonly object gate = new();
    private Operation? active;
    private Task<MusicVipPageSnapshot>? read;
    private Task? preparation, disposal;
    private long epoch, currentGeneration, signedGeneration;
    private MusicVipSignCardData? currentSign;
    private bool stopping, closed;

    internal MusicVipPageOwner(MusicAccountOwner accounts, MusicPlatformClient client)
    {
        this.accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        accounts.SessionChanged += AccountChanged;
    }

    public event Action? VipPageInvalidated;

    public async Task<MusicVipPageSnapshot> ReadVipPageAsync(CancellationToken token = default)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task<MusicVipPageSnapshot>? work = null;
            Task? waitPrevious = null;
            lock (gate)
            {
                if (closed || stopping) throw new MusicOperationException("CLOSED");
                if (read is { IsCompleted: false } && active?.Epoch == epoch) work = read;
                else if (active is { } prior && prior.Epoch != epoch) waitPrevious = prior.Completion;
                else
                {
                    var operation = Start(CancellationToken.None);
                    work = read = ReadCoreAsync(operation);
                    _ = work.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
            if (waitPrevious is not null)
            {
                await waitPrevious.WaitAsync(token).ConfigureAwait(false);
                continue;
            }
            return await work!.WaitAsync(token).ConfigureAwait(false);
        }
    }

    public Task<MusicVipSignReceipt> SignVipPageAsync(long expectedGeneration,
        CancellationToken token = default)
    {
        Operation operation;
        lock (gate)
        {
            if (closed || stopping) return Task.FromException<MusicVipSignReceipt>(
                new MusicOperationException("CLOSED"));
            if (active is not null) return Task.FromException<MusicVipSignReceipt>(
                new MusicOperationException("BUSY"));
            if (expectedGeneration <= 0 || expectedGeneration != currentGeneration)
                return Task.FromException<MusicVipSignReceipt>(
                    new MusicOperationException("STALE_SESSION"));
            if (signedGeneration == expectedGeneration || currentSign?.SignedToday == true)
                return Task.FromException<MusicVipSignReceipt>(
                    new MusicOperationException("ALREADY_SIGNED"));
            operation = active = new(++epoch, token);
        }
        return SignCoreAsync(operation, expectedGeneration);
    }

    private Operation Start(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (closed || stopping) throw new MusicOperationException("CLOSED");
            if (active is not null) throw new MusicOperationException("BUSY");
            return active = new(++epoch, token);
        }
    }

    private async Task<MusicVipPageSnapshot> ReadCoreAsync(Operation operation)
    {
        await Task.Yield();
        try
        {
            var accountTask = ReadAccountAsync(operation.Token);
            var networkTask = ReadNetworkAsync(operation.Token);
            await Task.WhenAll(accountTask, networkTask).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            var account = await accountTask.ConfigureAwait(false);
            var network = await networkTask.ConfigureAwait(false);
            var generation = network.Session?.Generation ?? account.Value?.Generation ?? 0;
            if (network.Session is not null && account.Value is not null &&
                account.Value.Generation != network.Session.Generation)
                throw new MusicOperationException("STALE_SESSION");
            Guard(operation, network.Session);
            bool signConfirmed;
            lock (gate)
            {
                GuardLocked(operation);
                currentGeneration = generation;
                currentSign = network.Sign.Value;
                if (generation > 0)
                {
                    if (network.Sign.Value?.SignedToday == true)
                        signedGeneration = generation;
                    else if (signedGeneration != generation)
                        signedGeneration = 0;
                }
                signConfirmed = generation > 0 && signedGeneration == generation;
            }
            return new(generation, account.Value, network.Sign.Value, network.Banner.Value,
                account.Error, network.Sign.Error, network.Banner.Error,
                SignConfirmed: signConfirmed);
        }
        finally { Finish(operation); }
    }

    private async Task<MusicVipSignReceipt> SignCoreAsync(Operation operation, long expectedGeneration)
    {
        MusicPlatformSession? session = null;
        try
        {
            session = await accounts.AcquireSettledAsync(Platform, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            if (session is null || session.Generation != expectedGeneration)
                throw new MusicOperationException("STALE_SESSION");
            Guard(operation, session);
            var receipt = await client.RequestAsync(Platform, "vip.checkIn", new { }, session, CancellationToken.None, () => GuardAccount(session)).ConfigureAwait(false);
            var status = receipt.GetProperty("status").GetString();
            if (status != "confirmed")
            {
                var code = receipt.TryGetProperty("errorCode", out var err) && err.ValueKind == JsonValueKind.String
                    ? err.GetString() ?? "UPSTREAM_ERROR" : "UPSTREAM_ERROR";
                if (code is "AUTH_REQUIRED" or "AUTH_EXPIRED" or "INVALID_CREDENTIALS" or "IDENTITY_MISMATCH")
                    accounts.Revoke(session);
                throw new MusicOperationException(code);
            }
            if (!ConfirmSign(operation, session, expectedGeneration))
                return new(expectedGeneration, null);

            MusicVipSignCardData? sign;
            if (operation.Token.IsCancellationRequested)
                sign = null;
            else try { sign = await ReadSignCardAsync(session, operation.Token).ConfigureAwait(false); }
            catch (Exception error)
            {
                if (Code(error) is "AUTH_REQUIRED" or "AUTH_EXPIRED" or "INVALID_CREDENTIALS" or "IDENTITY_MISMATCH")
                    accounts.Revoke(session);
                sign = null;
            }
            PublishRefreshedSign(operation, session, sign);
            return new(expectedGeneration, sign);
        }
        catch (MusicPlatformException error) when (error.Code is
            "AUTH_REQUIRED" or "AUTH_EXPIRED" or "INVALID_CREDENTIALS" or "IDENTITY_MISMATCH")
        {
            if (session is not null && session.Generation == expectedGeneration)
                accounts.Revoke(session);
            throw new MusicOperationException(error.Code);
        }
        finally { Finish(operation); }
    }

    private bool ConfirmSign(Operation operation, MusicPlatformSession session,
        long expectedGeneration)
    {
        lock (gate)
        {
            if (!ReferenceEquals(active, operation) ||
                currentGeneration != expectedGeneration || !accounts.IsCurrent(session))
                return false;
            signedGeneration = expectedGeneration;
            return operation.Epoch == epoch;
        }
    }

    private void PublishRefreshedSign(Operation operation, MusicPlatformSession session,
        MusicVipSignCardData? sign)
    {
        if (sign is null) return;
        lock (gate)
        {
            if (ReferenceEquals(active, operation) && operation.Epoch == epoch &&
                currentGeneration == session.Generation && accounts.IsCurrent(session))
                currentSign = sign;
        }
    }

    private async Task<Branch<MusicVipAccountSummary>> ReadAccountAsync(CancellationToken token)
    {
        try
        {
            var details = await accounts.ReadDetailsAsync(Platform, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new(Account(details), "");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { return new(null, Code(error)); }
    }

    private async Task<NetworkBranches> ReadNetworkAsync(CancellationToken token)
    {
        MusicPlatformSession? session;
        try { session = await accounts.AcquireSettledAsync(Platform, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            var code = Code(error);
            return new(null, new(null, code), new(null, code));
        }
        token.ThrowIfCancellationRequested();
        if (session is null) return new(null, new(null, ""), new(null, ""));
        var sign = ReadBranchAsync(() => ReadSignCardAsync(session, token), token);
        var banner = ReadBranchAsync(() => ReadBannerCardAsync(session, token), token);
        await Task.WhenAll(sign, banner).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!accounts.IsCurrent(session)) throw new MusicOperationException("STALE_SESSION");
        var signResult = await sign.ConfigureAwait(false);
        var bannerResult = await banner.ConfigureAwait(false);
        if (signResult.Error is "AUTH_REQUIRED" or "AUTH_EXPIRED" or "INVALID_CREDENTIALS" or "IDENTITY_MISMATCH" ||
            bannerResult.Error is "AUTH_REQUIRED" or "AUTH_EXPIRED" or "INVALID_CREDENTIALS" or "IDENTITY_MISMATCH")
        {
            accounts.Revoke(session);
            throw new MusicOperationException("STALE_SESSION");
        }
        return new(session, signResult, bannerResult);
    }

    private async Task<MusicVipSignCardData?> ReadSignCardAsync(MusicPlatformSession session, CancellationToken token)
    {
        var value = await client.RequestAsync(Platform, "vip.signCard", new { }, session, token, () => GuardAccount(session)).ConfigureAwait(false);
        if (value.ValueKind == JsonValueKind.Null) return null;
        var days = value.GetProperty("days").EnumerateArray().Select(day => new MusicVipSignDay(
            day.GetProperty("text").GetString()!, day.GetProperty("signed").GetBoolean(), day.GetProperty("cover").GetString()!,
            day.GetProperty("signedAt").GetInt64(), day.GetProperty("today").GetBoolean())).ToArray();
        return new(value.GetProperty("text").GetString()!, value.GetProperty("subText").GetString()!,
            value.GetProperty("redPrize").GetBoolean(), value.GetProperty("redDay").GetBoolean(), value.GetProperty("buttonText").GetString()!,
            value.GetProperty("background").GetString()!, days);
    }

    private async Task<MusicVipBannerData?> ReadBannerCardAsync(MusicPlatformSession session, CancellationToken token)
    {
        var value = await client.RequestAsync(Platform, "vip.banner", new { }, session, token, () => GuardAccount(session)).ConfigureAwait(false);
        return value.ValueKind == JsonValueKind.Null ? null : new(value.GetProperty("image").GetString()!,
            value.GetProperty("resourceType").GetString()!, value.GetProperty("resourceId").GetString()!);
    }

    private static async Task<Branch<T>> ReadBranchAsync<T>(
        Func<Task<T?>> read, CancellationToken token) where T : class
    {
        try
        {
            var value = await read().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new(value, "");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { return new(null, Code(error)); }
    }

    private static MusicVipAccountSummary? Account(MusicAccountDetails details)
    {
        if (details.State is not ("saved" or "verified") ||
            details.Profile is not { } profile)
            return null;
        var avatar = ImageAddress(profile.Avatar);
        var membership = "";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (details.MembershipFreshness == MusicAccountDetailsFreshness.Fresh &&
            details.MembershipRecheckAt > now && details.Membership.Vip == true &&
            details.Membership.ExpiresAt is { } expiry && expiry > now)
        {
            var tier = details.Membership.Tier is "svip" or "huge_vip" or "concept:svip"
                ? "SVIP" : "VIP";
            try
            {
                membership = tier + " · " + DateTimeOffset.FromUnixTimeMilliseconds(expiry)
                    .ToLocalTime().ToString("yyyy.M.d");
            }
            catch (ArgumentOutOfRangeException) { }
        }
        return new(details.Generation, profile.DisplayName, avatar, membership);
    }

    private static string ImageAddress(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0)
            return "";
        return uri.AbsoluteUri;
    }

    private void Guard(Operation operation, MusicPlatformSession? session = null)
    {
        lock (gate) GuardLocked(operation);
        if (session is not null && !accounts.IsCurrent(session))
            throw new MusicOperationException("STALE_SESSION");
    }
    private void GuardAccount(MusicPlatformSession session)
    {
        if (!accounts.IsCurrent(session)) throw new MusicPlatformException("STALE_SESSION");
    }

    private void GuardLocked(Operation operation)
    {
        if (closed || stopping) throw new MusicOperationException("CLOSED");
        if (!ReferenceEquals(active, operation) || operation.Epoch != epoch)
            throw new MusicOperationException("STALE_SESSION");
        operation.Token.ThrowIfCancellationRequested();
    }

    private void Finish(Operation operation)
    {
        operation.Dispose();
        lock (gate)
        {
            if (ReferenceEquals(active, operation)) active = null;
            operation.Complete();
        }
    }

    private void AccountChanged(string platform)
    {
        if (platform != Platform) return;
        Operation? operation;
        lock (gate)
        {
            epoch++;
            currentGeneration = signedGeneration = 0;
            currentSign = null;
            operation = active;
        }
        operation?.Cancel();
        foreach (var observer in VipPageInvalidated?.GetInvocationList() ?? [])
            try { ((Action)observer)(); }
            catch { }
    }

    internal Task PrepareShutdownAsync()
    {
        lock (gate)
        {
            if (preparation is not null) return preparation;
            stopping = true;
            epoch++;
            active?.Cancel();
            return preparation = active?.Completion ?? Task.CompletedTask;
        }
    }


    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            closed = true;
            stopping = true;
            epoch++;
            currentGeneration = signedGeneration = 0;
            currentSign = null;
            active?.Cancel();
            accounts.SessionChanged -= AccountChanged;
            return new(disposal = DisposeCoreAsync(active?.Completion ?? Task.CompletedTask));
        }
    }

    private static async Task DisposeCoreAsync(Task pending)
    {
        try { await pending.ConfigureAwait(false); } catch { }
    }

    private static string Code(Exception error) => error switch
    {
        MusicAccountException value => value.Code,
        MusicPlatformException value => value.Code,
        MusicOperationException value => value.Code,
        _ => "INVALID_RESPONSE"
    };

    private sealed class Operation(long epoch, CancellationToken token) : IDisposable
    {
        private readonly CancellationTokenSource cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(token);
        private readonly TaskCompletionSource settlement =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal long Epoch { get; } = epoch;
        internal CancellationToken Token => cancellation.Token;
        internal Task Completion => settlement.Task;
        internal void Cancel()
        {
            try { cancellation.Cancel(); }
            catch (Exception error) when (error is ObjectDisposedException or AggregateException) { }
        }
        internal void Complete() => settlement.TrySetResult();
        public void Dispose() => cancellation.Dispose();
    }

    private sealed record Branch<T>(T? Value, string Error) where T : class;
    private sealed record NetworkBranches(
        MusicPlatformSession? Session,
        Branch<MusicVipSignCardData> Sign,
        Branch<MusicVipBannerData> Banner);
}
