// SPDX-License-Identifier: GPL-3.0-only
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Accounts;

/// <summary>
/// Fixed, source-pinned native account client. No discovery, saved credentials,
/// background polling, automatic retry or verification bypass. KG registration
/// requires the native owner's durably acknowledged device handoff.
/// </summary>
public sealed partial class MusicAccountProtocol : IMusicAccountProtocol
{
    private readonly MusicPlatformHttp transport;
    private readonly MusicPlatformClientContextOwner clients;
    internal MusicPlatformClientProtocol ClientProtocol { get; }
    private readonly bool leaveOpen;
    private readonly TimeSpan timeout;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly Dictionary<string, ProtocolState> states = AccountRules.Platforms.ToDictionary(p => p, _ => new ProtocolState());
    private readonly HashSet<Task> pending = [];
    private bool closed;
    private sealed class ProtocolState
    {
        internal long Generation;
        internal Context? Active;
        internal Qr? Qr;
        internal int Handoffs;
        internal long? LastQrCreation;
    }
    private sealed class Qr(string id, string key, long generation, long started)
    {
        internal string Id { get; } = id;
        internal string Key { get; } = key;
        internal long Generation { get; } = generation;
        internal long Started { get; } = started;
        internal int Polls;
        internal bool Consumed;
        internal MusicKgClientContext? Device;
        public override string ToString() => "MusicAccountQR (transient)";
    }
    private sealed class Context : IDisposable
    {
        private readonly MusicAccountProtocol owner;
        private readonly CancellationTokenSource timeoutCancellation, cancellation;
        private readonly CancellationToken external;
        private readonly long started;
        private readonly TimeSpan duration;
        private readonly bool detailsRead;
        internal string Platform { get; }
        internal long Generation { get; }
        internal CancellationToken Token { get; }
        internal string Reason = "CANCELLED";
        internal bool HandoffStarted;
        internal MusicPlatformClientContext? Client;
        internal MusicWyClientContext WyClient => (MusicWyClientContext)Client!;
        internal MusicKgClientContext KgClient => (MusicKgClientContext)Client!;
        private int requests;
        private readonly int requestLimit;
        internal Context(MusicAccountProtocol owner, string platform, long generation, CancellationToken external, int requestLimit,
            bool detailsRead = false)
        {
            this.owner = owner; Platform = platform; Generation = generation; this.external = external;
            this.requestLimit = requestLimit;
            this.detailsRead = detailsRead;
            duration = detailsRead && owner.timeout > TimeSpan.FromSeconds(25) ? TimeSpan.FromSeconds(25) : owner.timeout;
            started = owner.clock.GetTimestamp();
            timeoutCancellation = new(duration);
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(external, timeoutCancellation.Token);
            Token = cancellation.Token;
        }
        internal void Abort(string reason) { Reason = reason; cancellation.Cancel(); }
        internal void Guard()
        {
            lock (owner.gate)
            {
                AccountRules.Require(!owner.closed, "CLOSED");
                AccountRules.Require(owner.states[Platform].Generation == Generation, "SUPERSEDED");
            }
            AccountRules.Require(!external.IsCancellationRequested, "CANCELLED");
            AccountRules.Require(owner.clock.GetElapsedTime(started) < duration && !timeoutCancellation.IsCancellationRequested, "TIMEOUT");
            AccountRules.Require(!Token.IsCancellationRequested, Reason);
            if (Client is not null)
                AccountRules.Require(owner.clients.IsCurrent(Client),
                    owner.clients.NeedsReconciliation ? "COMMIT_UNKNOWN" : "STORAGE_CONFLICT");
        }
        internal HttpRequestMessage Wy(string path, JsonObject parameters, string? cookie, bool weapi = false) =>
            MusicPlatformRequests.WyWithCookie(path, parameters, cookie, WyClient, weapi);
        internal HttpRequestMessage Kg(string path, JsonObject parameters, string? cookie,
            string? router = null, JsonObject? body = null) =>
            MusicPlatformRequests.KgWithCookie(path, parameters, cookie, KgClient, router, body);
        internal async Task<MusicPlatformHttpResult> Send(HttpRequestMessage request, bool accountDetails = false)
        {
            var result = await SendCore(request, left => detailsRead || accountDetails
                ? owner.transport.SendAccountDetailsAsync(Platform, request, Token, left)
                : owner.transport.SendAsync(Platform, request, Token, 1024 * 1024, left)).ConfigureAwait(false);
            Inspect(result.Body); Guard();
            return result;
        }
        internal async Task AcceptWyCookies(IReadOnlyList<string> cookies, string? accountCookie = null)
        {
            Client = await owner.clients.AcceptWyAsync(WyClient, accountCookie, cookies, Guard, Token).ConfigureAwait(false);
            Guard();
        }
        internal Task<MusicAccountHttpResult> SendAccount(MusicAccountHttpStep step, HttpRequestMessage request) =>
            SendCore(request, left => owner.transport.SendAccountStepAsync(step, request, Token, left));
        private async Task<T> SendCore<T>(HttpRequestMessage request, Func<TimeSpan, Task<T>> send)
        {
            try
            {
                Guard();
                AccountRules.Require(++requests <= requestLimit, "LIMIT");
                int bytes = Encoding.UTF8.GetByteCount(request.RequestUri!.AbsoluteUri) +
                    request.Headers.Sum(h => Encoding.UTF8.GetByteCount(h.Key) + h.Value.Sum(Encoding.UTF8.GetByteCount));
                AccountRules.Require(bytes + (request.Content?.Headers.ContentLength ?? 0) <= 65536, "LIMIT");
            }
            catch { request.Dispose(); throw; }
            var left = duration - owner.clock.GetElapsedTime(started);
            try
            {
                var result = await send(TimeSpan.FromMilliseconds(Math.Clamp(left.TotalMilliseconds, 1, 60000))).ConfigureAwait(false);
                Guard();
                return result;
            }
            catch (Exception error)
            {
                Guard();
                throw new MusicAccountException(error is MusicPlatformException or MusicAccountException
                    ? AccountRules.Error(error) : "NETWORK_ERROR");
            }
        }
        internal async Task Accept(MusicAccountSessionHandoff accept, MusicAccountHandoff handoff)
        {
            Guard();
            lock (owner.gate)
            {
                var state = owner.states[Platform];
                AccountRules.Require(state.Handoffs == 0, "BUSY");
                state.Qr = null; state.Handoffs++; HandoffStarted = true;
            }
            async Task<bool> Physical()
            {
                try { Guard(); return await accept(handoff, Token).ConfigureAwait(false); }
                finally { lock (owner.gate) owner.states[Platform].Handoffs--; }
            }
            var task = Physical();
            owner.Track(task);
            var accepted = await task.WaitAsync(Token).ConfigureAwait(false);
            Guard();
            AccountRules.Require(accepted, "HANDOFF_REJECTED");
        }
        public void Dispose() { cancellation.Dispose(); timeoutCancellation.Dispose(); }
    }
    internal MusicAccountProtocol(MusicPlatformHttp transport, MusicPlatformClientContextOwner clients,
        bool leaveOpen = true, TimeSpan? timeout = null, TimeProvider? clock = null,
        MusicPlatformClientProtocol? clientProtocol = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.clients = clients ?? throw new ArgumentNullException(nameof(clients));
        this.leaveOpen = leaveOpen; this.timeout = timeout ?? TimeSpan.FromSeconds(25);
        AccountRules.Require(this.timeout.TotalMilliseconds is >= 1 and <= 60000);
        this.clock = clock ?? TimeProvider.System;
        ClientProtocol = clientProtocol ?? new(transport, this.clock);
    }
    private void Track(Task task)
    {
        lock (gate) pending.Add(task);
        _ = task.ContinueWith(t =>
        {
            _ = t.Exception;
            lock (gate) pending.Remove(t);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private void Fence(ProtocolState state, string reason)
    {
        state.Generation++; state.Qr = null; state.Active?.Abort(reason); state.Active = null;
    }
    public void Cancel(string platform)
    {
        AccountRules.Platform(platform);
        lock (gate) { AccountRules.Require(!closed, "CLOSED"); Fence(states[platform], "CANCELLED"); }
    }
    private async Task<T> Run<T>(string platform, CancellationToken token, Func<Context, Task<T>> action, bool replace = true, int requestLimit = 3,
        bool detailsRead = false)
    {
        token.ThrowIfCancellationRequested();
        Context ctx;
        lock (gate)
        {
            AccountRules.Require(!closed, "CLOSED");
            AccountRules.Require(pending.Count < 8, "RATE_LIMITED");
            var state = states[platform];
            if (replace) Fence(state, "SUPERSEDED");
            else AccountRules.Require(state.Active is null, "BUSY");
            ctx = new(this, platform, state.Generation, token, requestLimit, detailsRead);
            state.Active = ctx;
        }
        async Task<T> Physical()
        {
            try
            {
                ctx.Guard();
                if (platform is "wy" or "kg")
                    ctx.Client = await clients.AcquireAsync(platform, ctx.Token).ConfigureAwait(false);
                ctx.Guard();
                return await action(ctx).ConfigureAwait(false);
            }
            finally { lock (gate) if (ReferenceEquals(states[platform].Active, ctx)) states[platform].Active = null; }
        }
        var task = Physical();
        Track(task);
        try
        {
            var result = detailsRead ? await task.ConfigureAwait(false) : await task.WaitAsync(ctx.Token).ConfigureAwait(false);
            ctx.Guard(); return result;
        }
        catch (Exception error)
        {
            string code = error switch { MusicAccountException known => known.Code,
                MusicPlatformException known => AccountRules.Error(known), OperationCanceledException => "CANCELLED", _ => "UPSTREAM_ERROR" };
            try { ctx.Guard(); } catch (MusicAccountException e) { code = e.Code; }
            if (ctx.HandoffStarted && code != "HANDOFF_REJECTED") code = "HANDOFF_UNCONFIRMED";
            lock (gate) if (states[platform].Generation == ctx.Generation) states[platform].Qr = null;
            throw new MusicAccountException(code, error is MusicAccountException knownError ? knownError.RetryAfterSeconds : 0);
        }
        finally
        {
            // A trusted callback that ignores cancellation retains its resources until settled.
            if (task.IsCompleted) ctx.Dispose();
            else _ = task.ContinueWith(_ => ctx.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
    public Task<MusicAccountDetailsObservation> ValidateAsync(string platform, string cookie, CancellationToken cancellationToken = default)
    {
        AccountRules.Platform(platform);
        var parts = AccountRules.Cookies(cookie);
        return Run(platform, cancellationToken, async ctx =>
        {
            var profile = platform switch
            {
                "wy" => await ValidateWy(ctx, cookie, parts).ConfigureAwait(false),
                "tx" => await ValidateTx(ctx, cookie, parts).ConfigureAwait(false),
                "kg" => await ValidateKg(ctx, cookie, parts).ConfigureAwait(false),
                _ => throw new MusicAccountException("INVALID_REQUEST")
            };
            return await ObserveAccountDetails(ctx, profile, cookie).ConfigureAwait(false);
        });
    }
    public Task<MusicAccountChallenge> BeginAsync(string platform, CancellationToken cancellationToken = default)
    {
        AccountRules.Platform(platform);
        if (platform == "tx") return BeginTx(cancellationToken);
        if (platform == "kg") return BeginKg(cancellationToken);
        AccountRules.Require(platform == "wy", "UNSUPPORTED");
        return Run(platform, cancellationToken, async ctx =>
        {
            var result = await ctx.Send(ctx.Wy("/api/login/qrcode/unikey",
                new JsonObject { ["type"] = 3 }, null)).ConfigureAwait(false);
            WyStatus(result.Body);
            string key = Text(result.Body["unikey"]);
            AccountRules.Require(AccountRules.Match(key, "^[A-Za-z0-9_-]{1,256}$"), "INVALID_RESPONSE");
            await ctx.AcceptWyCookies(result.SetCookieHeaders).ConfigureAwait(false);
            ctx.Guard();
            var challenge = new MusicAccountChallenge(platform, Guid.NewGuid().ToString("D"),
                "https://music.163.com/login?codekey=" + Uri.EscapeDataString(key),
                clock.GetUtcNow().ToUnixTimeMilliseconds() + 180000);
            lock (gate)
            {
                ctx.Guard();
                states[platform].Qr = new(challenge.ChallengeId, key, ctx.Generation, clock.GetTimestamp());
            }
            return challenge;
        });
    }
    public Task<MusicAccountPoll> PollAsync(string platform, string challengeId, MusicAccountSessionHandoff acceptSession,
        CancellationToken cancellationToken = default)
    {
        AccountRules.Platform(platform);
        AccountRules.Require(Guid.TryParseExact(challengeId, "D", out _));
        AccountRules.Require(acceptSession is not null);
        if (platform == "tx") return PollTx(challengeId, acceptSession!, cancellationToken);
        if (platform == "kg") return PollKg(challengeId, acceptSession!, cancellationToken);
        AccountRules.Require(platform == "wy", "UNSUPPORTED");
        cancellationToken.ThrowIfCancellationRequested();
        Qr? qr;
        lock (gate)
        {
            AccountRules.Require(!closed, "CLOSED");
            qr = states[platform].Qr;
            if (qr is null || qr.Id != challengeId) return Task.FromResult(new MusicAccountPoll(platform, challengeId, "expired"));
            if (clock.GetElapsedTime(qr.Started) >= TimeSpan.FromSeconds(180))
            { Fence(states[platform], "CANCELLED"); return Task.FromResult(new MusicAccountPoll(platform, challengeId, "expired")); }
        }
        return Run(platform, cancellationToken, async ctx =>
        {
            void GuardQr()
            {
                ctx.Guard();
                lock (gate) AccountRules.Require(ReferenceEquals(states[platform].Qr, qr) && qr.Generation == ctx.Generation, "SUPERSEDED");
                AccountRules.Require(clock.GetElapsedTime(qr.Started) < TimeSpan.FromSeconds(180), "QR_EXPIRED");
            }
            GuardQr();
            var result = await ctx.Send(ctx.Wy("/api/login/qrcode/client/login",
                new JsonObject { ["key"] = qr.Key, ["type"] = 3 }, null)).ConfigureAwait(false);
            GuardQr();
            int code = StrictCode(result.Body["code"]);
            AccountRules.Require(code is 800 or 801 or 802 or 803, "UPSTREAM_ERROR");
            if (code != 803)
            {
                await ctx.AcceptWyCookies(result.SetCookieHeaders).ConfigureAwait(false);
                GuardQr();
                if (code == 800) lock (gate) states[platform].Qr = null;
                return new MusicAccountPoll(platform, challengeId, code switch { 800 => "expired", 801 => "waiting", _ => "confirming" });
            }
            string cookie = QrCookie(result.SetCookieHeaders, clock.GetUtcNow());
            await ctx.AcceptWyCookies(result.SetCookieHeaders).ConfigureAwait(false);
            var account = await ValidateWy(ctx, cookie, AccountRules.Cookies(cookie)).ConfigureAwait(false);
            GuardQr();
            var details = await ObserveAccountDetails(ctx, account, cookie).ConfigureAwait(false);
            GuardQr();
            await ctx.Accept(acceptSession!, new(platform, challengeId, cookie, details)).ConfigureAwait(false);
            return new MusicAccountPoll(platform, challengeId, "authenticated");
        }, replace: false);
    }
    public JsonElement Describe() => System.Text.Json.JsonSerializer.SerializeToElement(new
    {
        version = "1.3.0", closed, timeoutMs = (int)timeout.TotalMilliseconds,
        platforms = AccountRules.Platforms.Select(p => new { platform = p, validate = true, qr = true,
            deviceOwnerRequired = p == "kg", sms = p == "wy", localCancel = true, remoteLogout = false,
            credentialRefresh = false, liveVerified = false })
    });
    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (gate)
        {
            if (!closed)
            { closed = true; foreach (var state in states.Values) Fence(state, "CLOSED"); }
            tasks = pending.ToArray();
        }
        if (!leaveOpen) transport.Dispose();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { /* Individual calls already have finite diagnostics. */ }
    }
}
