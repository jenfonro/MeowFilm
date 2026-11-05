// SPDX-License-Identifier: GPL-3.0-only
// Fixed iWebPlus exact-recording behavior; see LICENSES/NOTICE.md. Native only, no cache.
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Lyrics;

public sealed partial class NativeMusicLyrics : INativeMusicLyrics
{
    public const int ContractVersion = 1;
    public static readonly TimeSpan CallDeadline = TimeSpan.FromSeconds(25);
    private readonly IMusicPlatformSessionSource sessions;
    private readonly MusicPlatformHttp http;
    private readonly MusicPlatformClientContextOwner clients;
    private readonly Action<MusicPlatformSession>? authenticationFailed;
    private readonly object gate = new();
    private readonly HashSet<Call> active = [];

    // Borrowed dependencies: no constructor IO, credential store, subscription or Dispose.
    internal NativeMusicLyrics(IMusicPlatformSessionSource sessions, MusicPlatformHttp http, MusicPlatformClientContextOwner clients,
        Action<MusicPlatformSession>? authenticationFailed = null)
    {
        ArgumentNullException.ThrowIfNull(sessions); ArgumentNullException.ThrowIfNull(http);
        this.sessions = sessions; this.http = http;
        this.clients = clients ?? throw new ArgumentNullException(nameof(clients));
        this.authenticationFailed = authenticationFailed;
    }

    private sealed class Call(NativeMusicLyrics owner, NativeLyricRequest request, CancellationToken caller) : IDisposable
    {
        internal readonly NativeMusicLyrics Owner = owner;
        internal readonly NativeLyricRequest Request = request;
        internal readonly CancellationToken Caller = caller;
        private readonly long started = Stopwatch.GetTimestamp();
        private readonly CancellationTokenSource deadline = new(CallDeadline), revoked = new();
        private CancellationTokenSource? linked;
        internal CancellationToken Token => (linked ??= CancellationTokenSource.CreateLinkedTokenSource(Caller, deadline.Token, revoked.Token)).Token;
        internal MusicPlatformSession? Session;
        internal MusicPlatformClientContext? Client;
        internal MusicWyClientContext WyClient => (MusicWyClientContext)Client!;
        internal MusicKgClientContext KgClient => (MusicKgClientContext)Client!;
        private int stale;
        internal void Fence() => Volatile.Write(ref stale, 1);
        internal void Cancel()
        {
            try { revoked.Cancel(); }
            catch (Exception e) when (e is ObjectDisposedException or AggregateException) { }
        }
        internal void Guard()
        {
            L.Require(Volatile.Read(ref stale) == 0, "STALE_SESSION");
            L.Require(!Caller.IsCancellationRequested, "CANCELLED");
            L.Require(!deadline.IsCancellationRequested && Stopwatch.GetElapsedTime(started) < CallDeadline, "TIMEOUT");
            if (Client is not null) L.Require(Owner.clients.IsCurrent(Client),
                Owner.clients.NeedsReconciliation ? "COMMIT_UNKNOWN" : "STORAGE_CONFLICT");
            if (Session is not null)
            {
                bool current;
                try { current = Owner.sessions.IsCurrent(Session); }
                catch { throw new MusicPlatformException("CREDENTIALS_UNAVAILABLE"); }
                L.Require(current, "STALE_SESSION");
            }
        }
        internal async Task<MusicPlatformSession?> Acquire()
        {
            Guard();
            MusicPlatformSession? next;
            try { next = await Owner.sessions.AcquireAsync(Request.Platform, Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { Guard(); throw new MusicPlatformException("CANCELLED"); }
            catch { Guard(); throw new MusicPlatformException("CREDENTIALS_UNAVAILABLE"); }
            Guard();
            L.Require(next is null || next.Platform == Request.Platform, "IDENTITY_MISMATCH");
            return next;
        }
        internal async Task Check()
        {
            var next = await Acquire().ConfigureAwait(false);
            L.Require(Session is null ? next is null : next is not null && Session.Platform == next.Platform &&
                Session.Scope == next.Scope && Session.Generation == next.Generation && Session.UserId == next.UserId &&
                Session.Cookie == next.Cookie, "STALE_SESSION");
            Guard();
        }
        internal async Task<MusicPlatformHttpResult> Read(HttpRequestMessage request)
        {
            var dispatched = false;
            try
            {
                await Check().ConfigureAwait(false);
                var remaining = CallDeadline - Stopwatch.GetElapsedTime(started);
                L.Require(remaining.TotalMilliseconds >= 1, "TIMEOUT");
                dispatched = true;
                var response = await Owner.http.SendLyricAsync(Request.Platform, request, Token, remaining).ConfigureAwait(false);
                try
                {
                    Guard(); await Check().ConfigureAwait(false);
                    return new(L.Parse(response.Bytes, Token), response.Cookies);
                }
                finally { CryptographicOperations.ZeroMemory(response.Bytes); }
            }
            finally { if (!dispatched) request.Dispose(); }
        }
        internal async Task AcceptWy(IReadOnlyList<string> cookies)
        {
            await Check().ConfigureAwait(false);
            Client = await Owner.clients.AcceptWyAsync(WyClient, Session?.Cookie, cookies, Guard, Token).ConfigureAwait(false);
            Guard();
        }
        public void Dispose() { linked?.Dispose(); deadline.Dispose(); revoked.Dispose(); }
    }

    public async Task<NativeLyricPayload?> ReadAsync(NativeLyricRequest request, CancellationToken ct)
    {
        L.Validate(request);
        var call = new Call(this, request, ct);
        lock (gate)
        {
            active.Add(call);
        }
        // Physical work owns its CTS until settled, including cancellation-ignoring dependencies.
        var token = call.Token;
        async Task<NativeLyricPayload?> Physical()
        {
            try
            {
                call.Session = await call.Acquire().ConfigureAwait(false);
                if (request.Platform is "wy" or "kg")
                    call.Client = await clients.AcquireAsync(request.Platform, token).ConfigureAwait(false);
                call.Guard();
                if (call.Session is not null)
                {
                    var cookies = PJ.CookieHeader(call.Session.Cookie);
                    if (request.Platform is "tx" or "kg")
                        L.Require(PJ.Cookie(cookies, request.Platform == "tx" ? "musicid" : "userid") == call.Session.UserId,
                            "INVALID_CREDENTIALS");
                }
                var result = request.Platform switch {
                    "wy" => await Wy(call).ConfigureAwait(false),
                    "tx" => await Tx(call).ConfigureAwait(false),
                    _ => await Kg(call).ConfigureAwait(false)
                };
                await call.Check().ConfigureAwait(false);
                lock (gate) { call.Guard(); return result; }
            }
            finally { lock (gate) active.Remove(call); call.Dispose(); }
        }
        var work = Physical();
        _ = work.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return await work.ConfigureAwait(false); }
        catch (Exception error)
        {
            var code = error is MusicPlatformException known ? known.Code : error is OperationCanceledException ? "CANCELLED" : "INVALID_RESPONSE";
            try { call.Guard(); } catch (MusicPlatformException reason) { code = reason.Code; }
            if (code is "AUTH_REQUIRED" or "AUTH_EXPIRED" && call.Session is { } failed)
                try { authenticationFailed?.Invoke(failed); }
                catch { /* Read settlement must not depend on observer behavior. */ }
            throw new MusicPlatformException(code, request.Platform, "lyrics");
        }
    }
    public void Invalidate(string? platform = null)
    {
        L.Require(platform is null or "wy" or "tx" or "kg", "INVALID_REQUEST");
        Call[] retired;
        lock (gate)
        {
            retired = active.Where(c => platform is null || c.Request.Platform == platform).ToArray();
            foreach (var call in retired) call.Fence();
        }
        foreach (var call in retired) call.Cancel();
    }
}
