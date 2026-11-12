// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Platforms;

public sealed partial class MusicPlatformCatalog : IMusicPlatformCatalog, IMusicDailyStylePreferences
{
    private const int MaxResponse = 4 * 1024 * 1024;
    private readonly IMusicPlatformSessionSource sessions;
    private readonly MusicPlatformClientContextOwner clients;
    private readonly MusicPlatformHttp http;
    private readonly MusicPlatformClientProtocol clientProtocol;
    private readonly bool ownsHttp;
    private readonly TimeSpan timeout;
    private readonly object gate = new();
    private readonly HashSet<Call> active = [];
    private readonly Dictionary<string, Flight> flights = [];
    private readonly Dictionary<string, long> dailyStyleEpochs = [];
    private readonly Action<MusicPlatformSession>? authenticationFailed;
    private sealed record Flight(Call Call, TaskCompletionSource<JsonElement> Completion);
    private readonly Dictionary<string, CacheEntry> cache = [];
    private int cacheBytes;
    private bool closed;
    private sealed record CacheEntry(JsonElement Value, DateTimeOffset Expires, int Bytes, string Platform, string Operation, string Scope);

    internal MusicPlatformCatalog(IMusicPlatformSessionSource sessions, MusicPlatformClientContextOwner clients,
        TimeSpan? timeout = null)
        : this(sessions, new MusicPlatformHttp(), clients, timeout, leaveOpen: false) { }

    internal MusicPlatformCatalog(IMusicPlatformSessionSource sessions, MusicPlatformHttp transport,
        MusicPlatformClientContextOwner clients, TimeSpan? timeout = null, bool leaveOpen = true,
        MusicPlatformClientProtocol? clientProtocol = null, Action<MusicPlatformSession>? authenticationFailed = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(transport);
        this.sessions = sessions;
        this.authenticationFailed = authenticationFailed;
        this.clients = clients ?? throw new ArgumentNullException(nameof(clients));
        this.timeout = timeout ?? TimeSpan.FromSeconds(25);
        PJ.Require(this.timeout > TimeSpan.Zero);
        http = transport; ownsHttp = !leaveOpen;
        this.clientProtocol = clientProtocol ?? new(transport);
    }
    /// <summary>Trusted composition/test seam only. Origin, redirect/status and streaming budgets remain enforced.</summary>
    internal static MusicPlatformCatalog CreateWithHandler(IMusicPlatformSessionSource sessions,
        HttpMessageHandler handler, MusicPlatformClientContextOwner clients, TimeSpan? timeout = null) =>
        new(sessions, MusicPlatformHttp.CreateWithHandler(handler), clients, timeout, leaveOpen: false);

    private sealed class Call : IDisposable
    {
        internal readonly MusicPlatformCatalog Owner;
        internal readonly string Platform, Operation;
        internal Task Work = Task.CompletedTask;
        internal volatile MusicPlatformException? Failure;
        internal volatile Call? Shared;
        internal readonly TimeSpan Duration;
        internal readonly CancellationTokenSource Deadline, Cancelled, Linked;
        internal readonly long Started = Stopwatch.GetTimestamp();
        internal MusicPlatformSession? Session;
        internal MusicPlatformClientContext? Client;
        internal MusicWyClientContext WyClient => (MusicWyClientContext)Client!;
        internal MusicKgClientContext KgClient => (MusicKgClientContext)Client!;
        internal string Scope = "", Reason = "";
        internal readonly CancellationToken Token;
        internal bool PublicCatalog => Platform is "kw" or "mg";
        internal Call(MusicPlatformCatalog owner, string platform, string operation)
        {
            Owner = owner; Platform = platform; Operation = operation;
            Duration = owner.timeout;
            Deadline = new(Duration); Cancelled = new();
            Linked = CancellationTokenSource.CreateLinkedTokenSource(Deadline.Token, Cancelled.Token);
            Token = Linked.Token;
        }
        internal void Fence(string reason) => Volatile.Write(ref Reason, reason);
        internal void Cancel()
        {
            try { Cancelled.Cancel(); }
            catch (ObjectDisposedException) { /* The operation already drained after being fenced. */ }
            catch (AggregateException) { /* A trusted port's callback cannot undo the fence. */ }
        }
        internal void Guard()
        {
            var reason = Volatile.Read(ref Reason);
            if (reason.Length > 0) throw new MusicPlatformException(reason);
            if (Deadline.IsCancellationRequested || Stopwatch.GetElapsedTime(Started) >= Duration)
                throw new MusicPlatformException("TIMEOUT");
            if (Client is not null)
                PJ.Require(Owner.clients.IsCurrent(Client),
                    Owner.clients.NeedsReconciliation ? "COMMIT_UNKNOWN" : "STORAGE_CONFLICT");
            if (Session is not null)
            {
                bool current;
                try { current = Owner.sessions.IsCurrent(Session); }
                catch { throw new MusicPlatformException("CREDENTIALS_UNAVAILABLE"); }
                PJ.Require(current, "STALE_SESSION");
            }
        }
        internal async Task CheckSessionAsync()
        {
            Guard();
            if (PublicCatalog) return; // These providers have no account capability, even when another platform is signed in.
            var next = await Owner.AcquireAsync(this);
            Guard();
            PJ.Require(Same(Session, next), "STALE_SESSION");
        }
        internal void Auth()
        {
            PJ.Require(Session is not null, "AUTH_REQUIRED");
            var cookies = PJ.Cookies(Session);
            if (Platform == "wy") PJ.Require(PJ.Cookie(cookies, "MUSIC_U").Length > 0, "AUTH_REQUIRED");
        }
        internal Task<T> Wy<T>(string path, JsonObject parameters, Func<JsonObject, T> decode, bool weapi = false) =>
            Read(PlatformProtocols.Wy(path, parameters, Session, WyClient, weapi), d => decode(PlatformProtocols.WySuccess(d)));
        internal Task<JsonObject> Tx(string module, string method, JsonObject parameters) =>
            Read(PlatformProtocols.Tx(module, method, parameters, Session, Client as MusicQqClientContext,
                Client is MusicQqClientContext ? Owner.clientProtocol.Clock.GetUtcNow() : null), PlatformProtocols.TxSuccess);
        internal Task<JsonObject> Kg(string path, JsonObject parameters, string? router = null, JsonObject? body = null) =>
            Read(PlatformProtocols.Kg(path, parameters, Session, KgClient, router, body), d => PlatformProtocols.KgSuccess(d, path));
        internal Task<JsonObject> KgRank(JsonObject body)
        {
            const string path = "/openapi/kmr/v2/rank/audio";
            var request = PlatformProtocols.Kg(path, new(), Session, KgClient, body: body);
            request.Headers.TryAddWithoutValidation("kg-tid", "369");
            return Read(request, d => PlatformProtocols.KgSuccess(d, path));
        }
        internal async Task<T> Xeapi<T>(string path, JsonObject parameters, Func<JsonObject, T> decode, Action? dispatched = null, Action? rejected = null)
        {
            Auth();
            var bootstrap = PlatformXeapi.KeyRequest(WyClient, Session!);
            var key = await Read(bootstrap.Request,
                body => PlatformXeapi.ReadKey(PlatformProtocols.WySuccess(body), bootstrap.Nonce));
            return await Read(PlatformXeapi.Request(path, parameters, Session!, WyClient, key),
                body =>
                {
                    if (rejected is not null)
                        PJ.Require(int.TryParse(PJ.Scalar(body["code"]), out _), "INVALID_RESPONSE");
                    try { PlatformProtocols.WySuccess(body); }
                    catch (MusicPlatformException) { rejected?.Invoke(); throw; }
                    return decode(body);
                }, xeapi: true, dispatched: dispatched);
        }
        private async Task<T> Read<T>(HttpRequestMessage request, Func<JsonObject, T> business, bool xeapi = false, Action? dispatched = null)
        {
            var sent = false;
            try
            {
                await CheckSessionAsync();
                dispatched?.Invoke();
                sent = true;
                var result = xeapi ? await Owner.http.SendXeapiAsync(request, Token, MaxResponse, Owner.timeout)
                    : await Owner.http.SendAsync(Platform, request, Token, MaxResponse, Owner.timeout);
                Guard();
                await CheckSessionAsync();
                var body = business(result.Body);
                if (Platform == "wy")
                    Client = await Owner.clients.AcceptWyAsync(WyClient, Session?.Cookie, result.SetCookieHeaders,
                        Guard, Token).ConfigureAwait(false);
                Guard(); return body;
            }
            finally { if (!sent) request.Dispose(); }
        }
        internal async Task<byte[]> PublicRead(HttpRequestMessage request)
        {
            var dispatched = false;
            try
            {
                Guard();
                PJ.Require(PublicCatalog && Session is null, "IDENTITY_MISMATCH");
                var remaining = Duration - Stopwatch.GetElapsedTime(Started);
                PJ.Require(remaining.TotalMilliseconds >= 1, "TIMEOUT");
                dispatched = true;
                // The dedicated transport awaits physical settle, including non-cooperative handlers.
                var bytes = await Owner.http.SendPublicCatalogReadAsync(Platform, request, Token, MaxResponse, remaining);
                Guard();
                return bytes;
            }
            finally { if (!dispatched) request.Dispose(); }
        }
        public void Dispose() { Linked.Dispose(); Deadline.Dispose(); Cancelled.Dispose(); }
    }
    private static bool Same(MusicPlatformSession? a, MusicPlatformSession? b) =>
        a is null ? b is null : b is not null && a.Platform == b.Platform && a.Scope == b.Scope &&
            a.Generation == b.Generation && a.UserId == b.UserId && a.Cookie == b.Cookie;
    private async Task<MusicPlatformSession?> AcquireAsync(Call call)
    {
        call.Guard();
        if (call.PublicCatalog) return null;
        try
        {
            var value = await sessions.AcquireAsync(call.Platform, call.Token);
            call.Guard();
            PJ.Require(value is null || value.Platform == call.Platform, "IDENTITY_MISMATCH");
            return value;
        }
        catch (MusicPlatformException) { throw; }
        catch (OperationCanceledException) { call.Guard(); throw new MusicPlatformException("CANCELLED"); }
        catch { throw new MusicPlatformException("CREDENTIALS_UNAVAILABLE"); }
    }
    private async Task<JsonElement> RunAsync(string platform, string operation, object input, bool refresh,
        Func<Call, Task<JsonObject>> action, CancellationToken token, bool cacheable = true, bool privateCache = false)
    {
        PJ.Require(platform is "wy" or "tx" or "kg" or "kw" or "mg");
        if (platform is "kw" or "mg")
            PJ.Require(operation is "search" or "song" or "playlist" &&
                input is not MusicPlaylistRequest { Personal: not null }, "UNSUPPORTED");
        PJ.Require(!token.IsCancellationRequested, "CANCELLED");
        Task<JsonElement> work;
        Call call;
        lock (gate)
        {
            PJ.Require(!closed, "CLOSED");
            call = new Call(this, platform, operation);
            active.Add(call);
            call.Work = work = RunOwnedAsync(call, input, refresh, action, cacheable, privateCache);
        }
        _ = work.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return await work.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Revoking a failed lease can cancel the subscriber synchronously.
            // Do not replace the already determined service error with cancellation.
            throw call.Failure ?? call.Shared?.Failure ?? new MusicPlatformException("CANCELLED", platform, operation);
        }
    }
    private async Task<JsonElement> RunOwnedAsync(Call call, object input, bool refresh,
        Func<Call, Task<JsonObject>> action, bool cacheable, bool privateCache)
    {
        await Task.Yield(); // Publish ownership before any synchronous dependency can finish.
        var platform = call.Platform; var operation = call.Operation;
        Flight? owned = null;
        string? flightKey = null;
        try
        {
            call.Session = await AcquireAsync(call);
            if (platform is "wy" or "kg")
                call.Client = await clients.AcquireAsync(platform, call.Token).ConfigureAwait(false);
            else if (platform == "tx" && operation == "search")
                call.Client = await clients.EnsureQqAsync(clientProtocol, call.Token).ConfigureAwait(false);
            call.Guard();
            if (call.Session is not null) _ = PJ.Cookies(call.Session);
            call.Scope = PJ.Hash(PJ.Node(new object?[] { platform, call.Session?.Scope ?? "anonymous", call.Session?.Generation ?? 0, call.Session?.UserId }));
            var keyInput = JsonSerializer.SerializeToNode(input, PJ.Options)!.AsObject();
            keyInput.Remove("Refresh"); // A successful explicit refresh replaces the ordinary read's cache.
            if (operation == "daily-style")
                lock (gate) keyInput["StyleEpoch"] = dailyStyleEpochs.GetValueOrDefault(call.Scope);
            string CacheKey() => PJ.Hash(PJ.Node(new object[] { call.Scope, operation, keyInput,
                call.Client switch
                {
                    MusicWyClientContext w => w.DeviceId, MusicKgClientContext k => k.Seed, _ => ""
                }, call.Client?.Generation ?? 0 }));
            var key = CacheKey();
            JsonElement? cached = null;
            lock (gate)
            {
                foreach (var expired in cache.Where(p => p.Value.Expires <= DateTimeOffset.UtcNow).Select(p => p.Key).ToArray()) Erase(expired);
                if (cacheable && (call.Session is null || privateCache) && !refresh && cache.TryGetValue(key, out var entry)) cached = entry.Value.Clone();
            }
            Task<JsonElement>? shared = null;
            if (cached is null)
                lock (gate)
                {
                    call.Guard();
                    if (flights.TryGetValue(key, out var existing))
                    { call.Shared = existing.Call; shared = existing.Completion.Task; }
                    else
                    {
                        owned = new(call, new(TaskCreationOptions.RunContinuationsAsynchronously));
                        flights.Add(key, owned); flightKey = key;
                        _ = owned.Completion.Task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                }
            if (shared is not null)
            {
                var value = await shared.ConfigureAwait(false);
                call.Guard(); return value.Clone();
            }
            var result = cached ?? PJ.Element(await (platform switch
            {
                "kw" => input switch
                {
                    MusicSearchRequest p => KwSearch(call, p),
                    MusicSongRequest p => KwSong(call, p),
                    MusicPlaylistRequest p => KwPlaylist(call, p),
                    _ => throw new MusicPlatformException("UNSUPPORTED")
                },
                "mg" => input switch
                {
                    MusicSearchRequest p => MgSearch(call, p),
                    MusicSongRequest p => MgSong(call, p),
                    MusicPlaylistRequest p => MgPlaylist(call, p),
                    _ => throw new MusicPlatformException("UNSUPPORTED")
                },
                _ => action(call)
            }));
            call.Guard();
            await call.CheckSessionAsync();
            call.Guard();
            if (cacheable && (call.Session is null || privateCache) && cached is null)
            {
                var bytes = Encoding.UTF8.GetByteCount(result.GetRawText());
                var ttl = operation is "song" or "playlist" ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(10);
                if (operation is "daily" or "daily-style" && result.TryGetProperty("expiresAt", out var expiry))
                    ttl = DateTimeOffset.FromUnixTimeMilliseconds(expiry.GetInt64()) - DateTimeOffset.UtcNow;
                lock (gate)
                {
                    call.Guard();
                    key = CacheKey(); // A trusted response may have advanced this call's client context.
                    Erase(key);
                    if (bytes <= 2 * 1024 * 1024)
                    {
                        while (cache.Count >= 64 || cacheBytes + bytes > 2 * 1024 * 1024) Erase(cache.Keys.First());
                        cache[key] = new(result.Clone(), DateTimeOffset.UtcNow + ttl, bytes, platform, operation, call.Scope); cacheBytes += bytes;
                    }
                }
            }
            owned?.Completion.TrySetResult(result);
            return result;
        }
        catch (Exception error)
        {
            var code = error is MusicPlatformException known ? known.Code : error is OperationCanceledException ? "CANCELLED" : "INVALID_RESPONSE";
            try { call.Guard(); } catch (MusicPlatformException e) { code = e.Code; }
            var failure = call.Shared?.Failure ?? new MusicPlatformException(code, platform, operation);
            call.Failure = failure;
            code = failure.Code;
            if (code is "AUTH_REQUIRED" or "AUTH_EXPIRED" or "FORBIDDEN" or "STALE_SESSION")
                lock (gate) foreach (var k in cache.Where(e => e.Value.Platform == platform).Select(e => e.Key).ToArray()) Erase(k);
            if (code is "AUTH_REQUIRED" or "AUTH_EXPIRED" && call.Session is { } failed)
                try { authenticationFailed?.Invoke(failed); }
                catch { /* An observer cannot prevent settlement of shared work. */ }
            owned?.Completion.TrySetException(failure);
            throw failure;
        }
        finally
        {
            call.Dispose();
            lock (gate)
            {
                if (flightKey is not null && flights.GetValueOrDefault(flightKey) == owned) flights.Remove(flightKey);
                active.Remove(call);
            }
        }
    }
    private void Erase(string key) { if (cache.Remove(key, out var entry)) cacheBytes -= entry.Bytes; }
    public void Invalidate(string? platform = null)
    {
        if (platform is not null) PJ.Require(platform is "wy" or "tx" or "kg" or "kw" or "mg");
        Call[] retired;
        lock (gate)
        {
            foreach (var key in cache.Where(e => platform is null || e.Value.Platform == platform).Select(e => e.Key).ToArray()) Erase(key);
            if (platform is null or "wy") dailyStyleEpochs.Clear();
            retired = active.Where(c => platform is null || c.Platform == platform).ToArray();
            foreach (var call in retired) call.Fence("STALE_SESSION");
            foreach (var key in flights.Where(p => platform is null || p.Value.Call.Platform == platform).Select(p => p.Key).ToArray())
                flights.Remove(key);
        }
        foreach (var call in retired) call.Cancel();
    }
    internal async Task DrainAsync()
    {
        Task[] pending;
        lock (gate) pending = active.Select(c => c.Work).ToArray();
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch { /* Each subscriber observes its own outcome; this is physical settlement. */ }
    }
    public void Dispose()
    {
        Call[] retired;
        lock (gate)
        {
            if (closed) return;
            closed = true; cache.Clear(); flights.Clear(); dailyStyleEpochs.Clear(); cacheBytes = 0;
            retired = active.ToArray();
            foreach (var call in retired) call.Fence("CLOSED");
        }
        foreach (var call in retired) call.Cancel();
        if (ownsHttp) http.Dispose();
    }
    public JsonElement Describe() => PJ.Element(PJ.Obj(("version", "1.0.0"), ("execution", "native"),
        ("platforms", PJ.Obj(
            ("wy", PJ.Obj(("search", new[] { "songs", "playlists" }), ("song", true), ("playlist", true), ("home", true), ("channels", true),
                ("channelKinds", new[] { "featured", "playlists", "rankings", "new-songs", "cube", "artists", "vip" }),
                ("personalPlaylists", true), ("daily", true), ("dailyStyle", true), ("dailyRequiresDeviceContext", true),
                ("xeapiV1", true), ("fm", true), ("similar", true), ("artist", true))),
            ("tx", PJ.Obj(("search", new[] { "songs", "playlists" }), ("song", true), ("playlist", true), ("home", false), ("channels", false), ("personalPlaylists", true))),
            ("kg", PJ.Obj(("search", new[] { "songs", "playlists" }), ("song", true), ("playlist", true), ("home", true), ("channels", true),
                ("personalPlaylists", true), ("songTLSRouteVerifiedLive", false))),
            ("kw", PJ.Obj(("search", new[] { "songs", "playlists" }), ("song", true), ("playlist", true),
                ("home", false), ("channels", false), ("personalPlaylists", false), ("accounts", false), ("anonymousMetadataHttp", true))),
            ("mg", PJ.Obj(("search", new[] { "songs", "playlists" }), ("song", true), ("playlist", true),
                ("home", false), ("channels", false), ("personalPlaylists", false), ("accounts", false),
                ("songSearchTotalUnit", "groups"))))),
        ("limits", PJ.Obj(("maxPageSize", 100), ("maxResponseBytes", MaxResponse))),
        ("liveVerified", false), ("cloudWrites", false), ("timeoutMs", timeout.TotalMilliseconds)));
}
