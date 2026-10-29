// SPDX-License-Identifier: GPL-3.0-only
using System.Net.Http;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Feedback;

/// <summary>One native registration; device context is additional to, never a substitute for, login.</summary>
internal sealed class NativeMusicFeedbackQq : IAsyncDisposable
{
    private readonly IMusicQqFeedbackContextSource source;
    private readonly MusicPlatformSession account;
    private readonly MusicFeedbackStart input;
    private readonly MusicPlatformHttp http;
    private readonly TimeProvider clock;
    private readonly Action<CancellationToken> accountGuard;
    private readonly IMusicQqFeedbackContextLease lease;
    private readonly CancellationTokenSource stop;
    private FeedbackProtocolQqTrack? track;
    private Task? disposal;

    private NativeMusicFeedbackQq(IMusicQqFeedbackContextSource source, MusicPlatformSession account,
        MusicFeedbackStart input, MusicPlatformHttp http, TimeProvider clock, Action<CancellationToken> guard,
        IMusicQqFeedbackContextLease lease, CancellationToken token)
    {
        this.source = source; this.account = account; this.input = input; this.http = http; this.clock = clock;
        accountGuard = guard; this.lease = lease;
        stop = CancellationTokenSource.CreateLinkedTokenSource(token, lease.Revoked);
    }

    internal static async Task<NativeMusicFeedbackQq> Start(IMusicPlatformSessionSource accounts,
        MusicPlatformSession account, MusicFeedbackStart input, MusicPlatformHttp http, TimeProvider clock,
        Action<CancellationToken> guard, CancellationToken token)
    {
        FeedbackProtocolRules.Require(accounts is IMusicQqFeedbackContextSource, "DEVICE_CONTEXT_REQUIRED");
        var source = (IMusicQqFeedbackContextSource)accounts;
        guard(token);
        IMusicQqFeedbackContextLease lease;
        try { lease = await source.AcquireForAccountAsync(account, token).ConfigureAwait(false); }
        catch { guard(token); throw; }
        // Own even a wrong/stale returned lease, so failure still releases and drains it.
        var own = new NativeMusicFeedbackQq(source, account, input, http, clock, guard, lease, token);
        try
        {
            own.Guard(own.stop.Token);
            own.track = FeedbackProtocolQqRequests.VerifyTrack(await own.Send(
                await FeedbackProtocolQqRequests.Track(account, lease.Device, input.Recording, own.Now()).ConfigureAwait(false),
                own.stop.Token).ConfigureAwait(false), input);
            own.Guard(own.stop.Token);
            return own; // Registration only: no fake QQ start report or accepted final receipt.
        }
        catch
        {
            await own.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal void Guard(CancellationToken token)
    {
        accountGuard(token);
        FeedbackProtocolRules.Require(disposal is null, "CLOSED");
        CheckLease(lease, token);
        stop.Token.ThrowIfCancellationRequested();
    }

    private void CheckLease(IMusicQqFeedbackContextLease current, CancellationToken token)
    {
        FeedbackProtocolRules.Require(ReferenceEquals(current.Account, account), "SESSION_CHANGED");
        FeedbackProtocolRules.Require(current.IsCurrent, "DEVICE_CONTEXT_EXPIRED");
        current.Revoked.ThrowIfCancellationRequested();
        token.ThrowIfCancellationRequested();
        FeedbackProtocolQqRequests.Device(current.Device, clock);
    }

    private long Now() => clock.GetUtcNow().ToUnixTimeSeconds();

    private async Task<JsonObject> Send(HttpRequestMessage request, CancellationToken token,
        IMusicQqFeedbackContextLease? fresh = null)
    {
        void Check() { Guard(token); if (fresh is not null) CheckLease(fresh, token); }
        try { Check(); } catch { request.Dispose(); throw; }
        try
        {
            var result = await http.SendQqFeedbackJsonAsync(request, token).ConfigureAwait(false);
            Check(); return result.Body;
        }
        catch { Check(); throw; }
    }

    internal async Task Finish(MusicFeedbackEnd end, CancellationToken token)
    {
        Guard(token);
        long listened = (long)Math.Floor(end.Listened);
        FeedbackProtocolRules.Require(listened >= 1, "INSUFFICIENT_LISTEN_TIME");
        using var acquiring = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
        IMusicQqFeedbackContextLease fresh;
        try { fresh = await source.AcquireForAccountAsync(account, acquiring.Token).ConfigureAwait(false); }
        catch { Guard(token); throw; }
        // Every Acquire live-verifies the same account. Do not renew an expired old registration
        // or adopt another installation/session even if its account ID happens to match.
        await using (fresh.ConfigureAwait(false))
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(acquiring.Token, fresh.Revoked);
            var ct = operation.Token;
            void Check()
            {
                Guard(ct); CheckLease(fresh, ct);
                FeedbackProtocolRules.Require(FeedbackProtocolQqRequests.SameDevice(lease.Device, fresh.Device), "SESSION_CHANGED");
            }
            Check();
            var currentTrack = FeedbackProtocolQqRequests.VerifyTrack(await Send(
                await FeedbackProtocolQqRequests.Track(account, fresh.Device, input.Recording, Now()).ConfigureAwait(false),
                ct, fresh).ConfigureAwait(false), input);
            FeedbackProtocolRules.Require(track is not null && currentTrack.Id == track.Id &&
                currentTrack.Interval == track.Interval, "IDENTITY_MISMATCH");
            var key = FeedbackProtocolQqRequests.VerifyVkey(await Send(
                await FeedbackProtocolQqRequests.Vkey(account, fresh.Device, input.Recording, Now()).ConfigureAwait(false),
                ct, fresh).ConfigureAwait(false), input.Recording);
            var report = await FeedbackProtocolQqRequests.Report(account, fresh.Device, currentTrack, key, listened, Now()).ConfigureAwait(false);
            try { Check(); } catch { report.Dispose(); throw; }
            byte[] receipt;
            try
            {
                receipt = await http.SendQqFeedbackReportAsync(report, ct).ConfigureAwait(false);
                Check();
            }
            catch { Check(); throw; }
            FeedbackProtocolQqReceipt.Verify(receipt, ct);
            Check();
        }
        Guard(token);
    }

    public ValueTask DisposeAsync()
    {
        // The owning protocol calls only after its physical Start/Finish work has settled.
        if (disposal is not null) return new(disposal);
        disposal = DisposeCore();
        return new(disposal);
    }
    private async Task DisposeCore()
    {
        try { await lease.DisposeAsync().ConfigureAwait(false); }
        finally { stop.Dispose(); }
    }
    public override string ToString() => "NativeMusicFeedbackQq (redacted)";
}
