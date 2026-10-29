// SPDX-License-Identifier: GPL-3.0-only
// Fixed iWebPlus exact-recording behavior; see LICENSES/NOTICE.md. Native only, no cache.
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Lyrics;

public sealed partial class NativeMusicLyrics : INativeMusicLyrics
{
    public const int ContractVersion = 1;
    public static readonly TimeSpan CallDeadline = TimeSpan.FromSeconds(25);
    private readonly IMusicPlatformSessionSource sessions;
    private readonly MusicPlatformHttp http;
    private readonly object gate = new();
    private readonly HashSet<Call> active = [];

    // Borrowed dependencies: no constructor IO, credential store, subscription or Dispose.
    public NativeMusicLyrics(IMusicPlatformSessionSource sessions, MusicPlatformHttp http)
    {
        ArgumentNullException.ThrowIfNull(sessions); ArgumentNullException.ThrowIfNull(http);
        this.sessions = sessions; this.http = http;
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
        internal int Requests;
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
        internal async Task<JsonObject> Read(HttpRequestMessage request)
        {
            var dispatched = false;
            try
            {
                await Check().ConfigureAwait(false);
                L.Require(++Requests <= 2, "LIMIT");
                var remaining = CallDeadline - Stopwatch.GetElapsedTime(started);
                L.Require(remaining.TotalMilliseconds >= 1, "TIMEOUT");
                dispatched = true;
                var bytes = await Owner.http.SendLyricBytesAsync(Request.Platform, request, Token, remaining).ConfigureAwait(false);
                Guard(); await Check().ConfigureAwait(false);
                return L.Parse(bytes, Token);
            }
            finally { if (!dispatched) request.Dispose(); }
        }
        public void Dispose() { linked?.Dispose(); deadline.Dispose(); revoked.Dispose(); }
    }

    public async Task<NativeLyricPayload?> ReadAsync(NativeLyricRequest request, CancellationToken ct)
    {
        L.Validate(request);
        var call = new Call(this, request, ct);
        lock (gate)
        {
            if (active.Count >= 8) { call.Dispose(); throw new MusicPlatformException("RATE_LIMITED", request.Platform, "lyrics"); }
            active.Add(call);
        }
        // Physical work owns its slot/CTS until settled, even if a trusted session
        // source ignores cancellation. A timed-out caller cannot create unbounded work.
        var token = call.Token;
        async Task<NativeLyricPayload?> Physical()
        {
            try
            {
                call.Session = await call.Acquire().ConfigureAwait(false);
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
