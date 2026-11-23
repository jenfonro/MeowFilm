using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MeowFilm.Core.Debug;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Platform.Debug;

/// <summary>
/// Zero-dependency HTTP/1.1 local debug control server implementing docs/common/debug-mode.md §6.
/// Uses TcpListener directly to avoid Windows HttpListener URLACL restrictions and support port 0 cleanly.
/// </summary>
public sealed class DebugControlHttpServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        MaxDepth = 128
    };

    private readonly DebugSession session;
    private readonly DebugDiagnostics diagnostics;
    private readonly Func<IDebugShellHost?> getShell;
    private readonly Func<IDebugRuntimeHost?> getRuntime;
    private readonly Func<string, IDebugStorageQueryHost?> getStorage;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<Task, byte> activeConnections = new();
    private readonly Task acceptLoop;
    private bool disposed;

    public DebugControlHttpServer(
        DebugSession session,
        DebugDiagnostics diagnostics,
        Func<IDebugShellHost?> getShell,
        Func<IDebugRuntimeHost?> getRuntime,
        Func<string, IDebugStorageQueryHost?> getStorage)
    {
        this.session = session;
        this.diagnostics = diagnostics;
        this.getShell = getShell;
        this.getRuntime = getRuntime;
        this.getStorage = getStorage;

        var ip = IPAddress.Parse(session.Options.Bind);
        listener = new TcpListener(ip, session.Options.Port);
        listener.Start();
        var bound = (IPEndPoint)listener.LocalEndpoint;
        BoundPort = bound.Port;
        BindAddress = session.Options.Bind;
        session.SetControlEndpoint(BindAddress, BoundPort);
        acceptLoop = Task.Run(() => AcceptLoopAsync(lifetime.Token));
    }

    public string BindAddress { get; }
    public int BoundPort { get; }
    public string ControlUrl => session.ControlUrl;

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (token.IsCancellationRequested) break; continue; }
            Task? connTask = null;
            connTask = Task.Run(async () =>
            {
                try { await HandleConnectionAsync(client, token).ConfigureAwait(false); }
                finally { if (connTask is not null) activeConnections.TryRemove(connTask, out _); }
            });
            activeConnections.TryAdd(connTask, 0);
            if (connTask.IsCompleted) activeConnections.TryRemove(connTask, out _);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            HttpRequestData? request;
            try
            {
                request = await ReadRequestAsync(stream, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (IOException) { return; }
            catch (SocketException) { return; }
            catch (Exception ex)
            {
                await TryWriteJsonEnvelopeAsync(stream, 400, false, DebugLog.NextTraceId(), null,
                    new { code = DebugErrorCodes.InvalidArgument, message = ex.Message }, token).ConfigureAwait(false);
                return;
            }
            if (request is null) return;

            var traceId = DebugLog.NextTraceId();
            var opId = DebugLog.NextOpId();
            using var scope = DebugLog.PushCorrelation(opId, traceId);
            try
            {
                await DispatchAsync(request, stream, traceId, token).ConfigureAwait(false);
            }
            catch (DebugApiException apiEx)
            {
                await TryWriteJsonEnvelopeAsync(stream, apiEx.HttpStatus, false, traceId, null,
                    new { code = apiEx.Code, message = apiEx.Message, details = apiEx.Details }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await TryWriteJsonEnvelopeAsync(stream, 504, false, traceId, null,
                    new { code = DebugErrorCodes.Timeout, message = "Operation timed out or cancelled." }, token).ConfigureAwait(false);
            }
            catch (IOException ex) when (ex.InnerException is SocketException) { /* Client disconnected mid-response. */ }
            catch (SocketException) { /* Client disconnected mid-response. */ }
            catch (Exception ex)
            {
                diagnostics.RecordException("handled_api", ex, "DebugControlHttpServer");
                await TryWriteJsonEnvelopeAsync(stream, 500, false, traceId, null,
                    new
                    {
                        code = DebugErrorCodes.InternalError,
                        message = ex.Message,
                        details = new { type = ex.GetType().FullName, stack = ex.StackTrace }
                    }, token).ConfigureAwait(false);
            }
        }
    }

    private async Task DispatchAsync(HttpRequestData req, NetworkStream stream, string traceId, CancellationToken token)
    {
        var method = req.Method;
        var path = req.Path;

        if (method == "GET" && path == "/debug/v1/health")
        {
            var shell = getShell();
            var runtime = getRuntime();
            var data = new
            {
                status = "ok",
                platform = "windows",
                pid = Environment.ProcessId,
                appVersion = session.AppVersion,
                sessionId = session.SessionId,
                sessionDir = session.SessionDir,
                controlUrl = session.ControlUrl,
                uptimeMs = Math.Round(session.UptimeMs, 3),
                runtimeConnected = runtime?.IsConnected ?? false,
                runtimeBaseUrl = runtime?.BaseUrl ?? session.RuntimeBaseUrl,
                runtimePid = runtime?.RuntimePid ?? session.RuntimePid,
                windowReady = shell?.IsWindowReady ?? false,
                dataRoot = session.DataRoot
            };
            await WriteOkAsync(stream, traceId, data, token).ConfigureAwait(false);
            return;
        }

        if (method == "GET" && path == "/debug/v1/capabilities")
        {
            var shell = getShell();
            var data = new
            {
                platform = "windows",
                schemaVersion = 1,
                targets = shell?.SupportedTargets ?? [],
                actions = shell?.SupportedActions ?? [],
                diagnostics = new[] { "summary", "threads", "gc", "dump", "node/heapsnapshot", "node/cpu-profile", "exceptions" },
                logCategories = DebugCategories.All,
                uiEndpoints = new[] { "ui/state", "ui/content", "ui/tree", "ui/images", "ui/screenshot" },
                waitConditions = new[] { "idle", "route_changed", "content_non_empty", "playback_state", "images_settled", "log_event" },
                platformFeatures = new
                {
                    minidump = true,
                    forwardNavigation = true,
                    mediaControlsSettings = true,
                    screenshot = true,
                    uiImages = true,
                    imagesSettled = true,
                    readOnlySql = true,
                    runtimeInvoke = true,
                    sessionFiles = true
                }
            };
            await WriteOkAsync(stream, traceId, data, token).ConfigureAwait(false);
            return;
        }

        if (method == "POST" && path == "/debug/v1/config")
        {
            var body = ParseJsonBody(req.Body);
            if (body.ValueKind != JsonValueKind.Object)
                throw DebugApiException.InvalidArgument("Request body must be a JSON object.");
            IReadOnlyList<string>? modules = null;
            int? inlineLimit = null, eventBytesLimit = null;
            bool? firstChance = null;
                if (body.TryGetProperty("modules", out var mProp))
                {
                    if (mProp.ValueKind != JsonValueKind.Array)
                        throw DebugApiException.InvalidArgument("modules must be an array of strings.");
                    var list = new List<string>();
                    foreach (var elem in mProp.EnumerateArray())
                    {
                        if (elem.ValueKind != JsonValueKind.String)
                            throw DebugApiException.InvalidArgument("modules must be an array of strings.");
                        list.Add(elem.GetString()!);
                    }
                    modules = list;
                }
                if (body.TryGetProperty("inlineLimit", out var ilProp))
                    inlineLimit = RequireInt(ilProp, "inlineLimit");
                if (body.TryGetProperty("eventBytesLimit", out var elProp))
                    eventBytesLimit = RequireInt(elProp, "eventBytesLimit");
                if (body.TryGetProperty("firstChanceExceptions", out var fcProp))
                {
                    if (fcProp.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw DebugApiException.InvalidArgument("firstChanceExceptions must be a boolean.");
                    firstChance = fcProp.GetBoolean();
                }
            var updated = session.UpdateConfig(modules, inlineLimit, eventBytesLimit, firstChance);
            object? nodeSync = null;
            var runtime = getRuntime();
            if (runtime is { IsConnected: true })
            {
                var handshake = session.CreateRuntimeHandshake();
                var forward = await runtime.SendNodeDebugRequestAsync(HttpMethod.Post, "/v1/debug/config",
                    new { modules = handshake.Modules, inlineLimit = handshake.InlineLimit, eventBytesLimit = handshake.EventBytesLimit },
                    TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                nodeSync = forward.ToDiagnosticSection();
            }
            await WriteOkAsync(stream, traceId, new { config = updated, node = nodeSync }, token).ConfigureAwait(false);
            return;
        }

        // Navigation
        if (method == "POST" && path == "/debug/v1/nav/open")
        {
            var shell = RequireShell();
            var body = ParseJsonBody(req.Body);
            var target = RequireString(body, "target");
            JsonElement? parameters = body.TryGetProperty("params", out var p) ? p : null;
            bool waitIdle = body.TryGetProperty("waitIdle", out var w) && w.ValueKind == JsonValueKind.True;
            int timeoutMs = body.TryGetProperty("timeoutMs", out var t) ? Math.Clamp(RequireInt(t, "timeoutMs"), 500, 120_000) : 15_000;
            var result = await shell.OpenTargetAsync(target, parameters, waitIdle, timeoutMs, token).ConfigureAwait(false);
            await WriteOkAsync(stream, traceId, result, token).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && path == "/debug/v1/nav/back")
        {
            await WriteOkAsync(stream, traceId, await RequireShell().NavigateBackAsync(token).ConfigureAwait(false), token).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && path == "/debug/v1/nav/forward")
        {
            await WriteOkAsync(stream, traceId, await RequireShell().NavigateForwardAsync(token).ConfigureAwait(false), token).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && path == "/debug/v1/nav/refresh")
        {
            await WriteOkAsync(stream, traceId, await RequireShell().RefreshAsync(token).ConfigureAwait(false), token).ConfigureAwait(false);
            return;
        }

        // UI Inspection & Screenshot
        if (method == "GET" && path == "/debug/v1/ui/state")
        {
            await WriteOkAsync(stream, traceId, await RequireShell().GetUiStateAsync(token).ConfigureAwait(false), token).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && path == "/debug/v1/ui/content")
        {
            int maxItems = ParseQueryInt(req.Query, "maxItems", 200, 1, 2000);
            bool includeRaw = ParseQueryBool(req.Query, "includeRaw", false);
            await WriteOkAsync(stream, traceId, await RequireShell().GetUiContentAsync(maxItems, includeRaw, token).ConfigureAwait(false), token).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && path == "/debug/v1/ui/tree")
        {
            int maxDepth = ParseQueryInt(req.Query, "maxDepth", 8, 1, 32);
            bool visibleOnly = ParseQueryBool(req.Query, "visibleOnly", true);
            await WriteOkAsync(stream, traceId, await RequireShell().GetUiTreeAsync(maxDepth, visibleOnly, token).ConfigureAwait(false), token).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && path == "/debug/v1/ui/images")
        {
            await WriteOkAsync(stream, traceId, await RequireShell().GetUiImagesAsync(token).ConfigureAwait(false), token).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && path == "/debug/v1/ui/screenshot")
        {
            bool save = ParseQueryBool(req.Query, "save", true);
            var format = (req.Query.GetValueOrDefault("format") ?? "png").Trim().ToLowerInvariant();
            if (format != "png")
                throw DebugApiException.InvalidArgument($"Unsupported screenshot format: '{format}' (only 'png' is supported).");
            var rawWindow = req.Query.GetValueOrDefault("window")?.Trim();
            var window = string.IsNullOrEmpty(rawWindow) ? null : rawWindow;
            var png = await RequireShell().CaptureScreenshotPngAsync(window, token).ConfigureAwait(false);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (save)
            {
                var saved = session.SaveScreenshot(png);
                headers["X-Meow-Screenshot-Path"] = saved.RelativePath;
            }
            await WriteBinaryResponseAsync(stream, 200, "image/png", png, headers, token).ConfigureAwait(false);
            return;
        }

        // UI Actions & Wait
        if (method == "POST" && path == "/debug/v1/ui/action")
        {
            var shell = RequireShell();
            var body = ParseJsonBody(req.Body);
            var action = RequireString(body, "action");
            JsonElement? parameters = body.TryGetProperty("params", out var p) ? p : null;
            bool waitIdle = body.TryGetProperty("waitIdle", out var w) && w.ValueKind == JsonValueKind.True;
            int timeoutMs = body.TryGetProperty("timeoutMs", out var t) ? Math.Clamp(RequireInt(t, "timeoutMs"), 500, 120_000) : 15_000;
            var result = await shell.PerformActionAsync(action, parameters, waitIdle, timeoutMs, token).ConfigureAwait(false);
            await WriteOkAsync(stream, traceId, result, token).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && path == "/debug/v1/ui/wait")
        {
            var body = ParseJsonBody(req.Body);
            var condition = RequireString(body, "condition");
            JsonElement? parameters = body.TryGetProperty("params", out var p) ? p : null;
            int timeoutMs = body.TryGetProperty("timeoutMs", out var t) ? Math.Clamp(RequireInt(t, "timeoutMs"), 100, 120_000) : 15_000;
            if (string.Equals(condition, "log_event", StringComparison.Ordinal))
            {
                if (parameters is not { ValueKind: JsonValueKind.Object } lp)
                    throw DebugApiException.InvalidArgument("log_event requires params object with 'cat'.");
                var cat = RequireString(lp, "cat").Trim().ToLowerInvariant();
                if (cat != "all" && !DebugCategories.IsValid(cat))
                    throw DebugApiException.InvalidArgument($"Unknown category: '{cat}'.");
                string? sub = lp.TryGetProperty("sub", out var sp) && sp.ValueKind == JsonValueKind.String ? sp.GetString() : null;
                string? ev = lp.TryGetProperty("event", out var ep) && ep.ValueKind == JsonValueKind.String ? ep.GetString() : null;
                long? sinceSeq = null;
                if (lp.TryGetProperty("sinceSeq", out var sq) && sq.ValueKind != JsonValueKind.Null)
                {
                    if (sq.ValueKind != JsonValueKind.Number || !sq.TryGetInt64(out var sqv))
                        throw DebugApiException.InvalidArgument("Field 'sinceSeq' must be an integer.");
                    sinceSeq = sqv;
                }
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                waitCts.CancelAfter(timeoutMs);
                try
                {
                    var matched = await session.WaitForEventAsync(cat, sub, ev, sinceSeq, waitCts.Token).ConfigureAwait(false);
                    await WriteOkAsync(stream, traceId, new { matched = true, condition, @event = matched }, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw DebugApiException.Timeout($"Timed out waiting for log_event '{cat}' after {timeoutMs}ms.");
                }
                return;
            }
            var waitResult = await RequireShell().WaitAsync(condition, parameters, timeoutMs, token).ConfigureAwait(false);
            await WriteOkAsync(stream, traceId, waitResult, token).ConfigureAwait(false);
            return;
        }

        // Playback & Novel
        if (method == "GET" && path == "/debug/v1/playback/state")
        {
            await WriteOkAsync(stream, traceId, await RequireShell().GetPlaybackStateAsync(token).ConfigureAwait(false), token).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && path == "/debug/v1/playback/command")
        {
            var body = ParseJsonBody(req.Body);
            var domain = RequireString(body, "domain");
            var command = RequireString(body, "command");
            JsonElement? parameters = body.TryGetProperty("params", out var p) ? p : null;
            var result = await RequireShell().PlaybackCommandAsync(domain, command, parameters, token).ConfigureAwait(false);
            await WriteOkAsync(stream, traceId, result, token).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && path == "/debug/v1/novel/command")
        {
            var body = ParseJsonBody(req.Body);
            var command = RequireString(body, "command");
            JsonElement? parameters = body.TryGetProperty("params", out var p) ? p : null;
            var result = await RequireShell().NovelCommandAsync(command, parameters, token).ConfigureAwait(false);
            await WriteOkAsync(stream, traceId, result, token).ConfigureAwait(false);
            return;
        }

        // Sessions, Files, Logs & Blobs
        if (method == "GET" && path == "/debug/v1/sessions")
        {
            await WriteOkAsync(stream, traceId, session.ListSessions(), token).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && path.StartsWith("/debug/v1/files/", StringComparison.Ordinal))
        {
            var rel = SafeUnescapeDataString(path["/debug/v1/files/".Length..]);
            var sessionId = req.Query.GetValueOrDefault("sessionId");
            var fullPath = session.ResolveSessionFile(sessionId, rel);
            var contentType = DebugBlobStore.ContentTypeForExtension(Path.GetExtension(fullPath));
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
            await WriteStreamResponseAsync(stream, 200, contentType, fs, fs.Length, null, token).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && path == "/debug/v1/logs/categories")
        {
            await WriteOkAsync(stream, traceId, session.GetCategoriesSummary(req.Query.GetValueOrDefault("sessionId")), token).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && path == "/debug/v1/logs/tail")
        {
            var cat = req.Query.GetValueOrDefault("cat");
            if (string.IsNullOrWhiteSpace(cat)) cat = "all";
            int limit = ParseQueryInt(req.Query, "limit", 100, 1, 2000);
            long? sinceSeq = null;
            if (req.Query.TryGetValue("sinceSeq", out var s) && !string.IsNullOrWhiteSpace(s))
            {
                if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sv))
                    throw DebugApiException.InvalidArgument("Query parameter 'sinceSeq' must be an integer.");
                sinceSeq = sv;
            }
            var (resolvedSessionId, events) = session.TailLogs(
                cat: cat,
                limit: limit,
                sinceSeq: sinceSeq,
                level: req.Query.GetValueOrDefault("level"),
                sub: req.Query.GetValueOrDefault("sub"),
                eventName: req.Query.GetValueOrDefault("event"),
                opId: req.Query.GetValueOrDefault("opId"),
                traceId: req.Query.GetValueOrDefault("traceId"),
                query: req.Query.GetValueOrDefault("q"),
                sessionId: req.Query.GetValueOrDefault("sessionId"));
            await WriteOkAsync(stream, traceId, new { sessionId = resolvedSessionId, cat, count = events.Count, events }, token).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && path.StartsWith("/debug/v1/blobs/", StringComparison.Ordinal))
        {
            var sha = SafeUnescapeDataString(path["/debug/v1/blobs/".Length..]).Trim().ToLowerInvariant();
            if (!DebugBlobStore.IsValidSha256(sha))
                throw DebugApiException.InvalidArgument("Invalid sha256 blob identifier.");
            var targetSession = session.ResolveSessionDirectory(req.Query.GetValueOrDefault("sessionId"));
            if (!DebugBlobStore.TryReadBlob(targetSession, sha, out var blobBytes, out var contentType, out _))
                throw DebugApiException.NotFound($"Blob not found: '{sha}'.");
            await WriteBinaryResponseAsync(stream, 200, contentType, blobBytes, null, token).ConfigureAwait(false);
            return;
        }

        // Runtime Invoke & Storage Query
        if (method == "POST" && path == "/debug/v1/runtime/invoke")
        {
            var body = ParseJsonBody(req.Body);
            string? rpcMethod = TryOptionalString(body, "method");
            string? explicitModule = TryOptionalString(body, "module");
            string? explicitOperation = TryOptionalString(body, "operation");
            var (targetModule, targetOperation, targetPath) = ResolveDebugInvokeTarget(rpcMethod, explicitModule, explicitOperation);
            JsonElement? input = body.TryGetProperty("input", out var inp) ? inp : null;
            int timeoutMs = body.TryGetProperty("timeoutMs", out var t) ? Math.Clamp(RequireInt(t, "timeoutMs"), 500, 120_000) : 30_000;
            var runtime = getRuntime();
            if (runtime is null || !runtime.IsConnected)
                throw DebugApiException.RuntimeUnavailable("Node runtime is not connected.");
            var invokeResult = await runtime.InvokeDebugAsync(targetPath, input, TimeSpan.FromMilliseconds(timeoutMs), token).ConfigureAwait(false);
            object? responseValue = invokeResult.JsonBody is { } json ? json : invokeResult.RawText;
            await WriteOkAsync(stream, traceId, new
            {
                module = targetModule,
                operation = targetOperation,
                path = targetPath,
                httpStatus = invokeResult.StatusCode,
                durationMs = invokeResult.DurationMs,
                response = responseValue
            }, token).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && path == "/debug/v1/storage/query")
        {
            var body = ParseJsonBody(req.Body);
            string dbName = TryOptionalString(body, "db") ?? "data";
            if (dbName is not ("data" or "cache"))
                throw DebugApiException.InvalidArgument($"Field 'db' must be 'data' or 'cache', got '{dbName}'.");
            var storage = getStorage(dbName);
            if (storage is null)
                throw DebugApiException.InvalidState($"Storage database '{dbName}' is not initialized.");
            var sql = RequireString(body, "sql");
            JsonElement? parameters = body.TryGetProperty("params", out var p) ? p : null;
            int maxRows = body.TryGetProperty("maxRows", out var m) ? Math.Clamp(RequireInt(m, "maxRows"), 1, 5000) : 200;
            var result = await storage.ExecuteReadOnlyQueryAsync(sql, parameters, maxRows, token).ConfigureAwait(false);
            await WriteOkAsync(stream, traceId, result, token).ConfigureAwait(false);
            return;
        }

        // Diagnostics
        if (method == "GET" && path == "/debug/v1/diag/summary")
        {
            var result = await diagnostics.GetSummaryAsync(getRuntime(), token).ConfigureAwait(false);
            await WriteOkAsync(stream, traceId, result, token).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && path == "/debug/v1/diag/threads")
        {
            var result = await diagnostics.GetThreadsAsync(getShell(), getRuntime(), token).ConfigureAwait(false);
            await WriteOkAsync(stream, traceId, result, token).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && path == "/debug/v1/diag/gc")
        {
            var result = await diagnostics.RunGcAsync(getRuntime(), token).ConfigureAwait(false);
            await WriteOkAsync(stream, traceId, result, token).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && path == "/debug/v1/diag/dump")
        {
            var body = ParseJsonBody(req.Body, allowEmpty: true);
            string mode = "mini";
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("mode", out var mp) && mp.ValueKind != JsonValueKind.Null)
            {
                if (mp.ValueKind != JsonValueKind.String)
                    throw DebugApiException.InvalidArgument("Field 'mode' must be a string ('mini' or 'heap').");
                mode = mp.GetString()!;
            }
            var result = await diagnostics.WriteMinidumpAsync(mode, token).ConfigureAwait(false);
            await WriteOkAsync(stream, traceId, result, token).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && path == "/debug/v1/diag/node/heapsnapshot")
        {
            var data = await ForwardNodeDebugCommandAsync("/v1/debug/heapsnapshot", new { },
                TimeSpan.FromSeconds(120), token).ConfigureAwait(false);
            await WriteOkAsync(stream, traceId, data, token).ConfigureAwait(false);
            return;
        }
        if (method == "POST" && path == "/debug/v1/diag/node/cpu-profile")
        {
            var body = ParseJsonBody(req.Body, allowEmpty: true);
            int durationMs = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("durationMs", out var dp)
                ? Math.Clamp(RequireInt(dp, "durationMs"), 500, 60_000) : 5000;
            var data = await ForwardNodeDebugCommandAsync("/v1/debug/cpu-profile", new { durationMs },
                TimeSpan.FromMilliseconds(durationMs + 30_000), token).ConfigureAwait(false);
            await WriteOkAsync(stream, traceId, data, token).ConfigureAwait(false);
            return;
        }
        if (method == "GET" && path == "/debug/v1/diag/exceptions")
        {
            int limit = ParseQueryInt(req.Query, "limit", 100, 1, 512);
            var kind = req.Query.GetValueOrDefault("kind") ?? "all";
            await WriteOkAsync(stream, traceId, diagnostics.GetExceptions(limit, kind), token).ConfigureAwait(false);
            return;
        }

        throw DebugApiException.NotFound($"Unknown debug endpoint: {method} {path}");
    }

    private IDebugShellHost RequireShell() =>
        getShell() ?? throw DebugApiException.InvalidState("Main shell window is not ready yet.");

    private static JsonElement ParseJsonBody(byte[] body, bool allowEmpty = false)
    {
        if (body.Length == 0)
        {
            if (allowEmpty) return default;
            using var emptyDoc = JsonDocument.Parse("{}");
            return emptyDoc.RootElement.Clone();
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw DebugApiException.InvalidArgument("Malformed JSON request body: " + ex.Message);
        }
    }

    private static string RequireString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var prop) ||
            prop.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(prop.GetString()))
            throw DebugApiException.InvalidArgument($"Missing or invalid string field '{name}'.");
        return prop.GetString()!;
    }

    private static string? TryOptionalString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var prop) ||
            prop.ValueKind == JsonValueKind.Null)
            return null;
        if (prop.ValueKind != JsonValueKind.String)
            throw DebugApiException.InvalidArgument($"Field '{name}' must be a string.");
        var value = prop.GetString()?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static (string Module, string Operation, string Path) ResolveDebugInvokeTarget(
        string? method, string? explicitModule, string? explicitOperation)
    {
        string module, operation;
        if (explicitModule is not null || explicitOperation is not null)
        {
            if (explicitModule is null || explicitOperation is null)
                throw DebugApiException.InvalidArgument("Both 'module' and 'operation' must be provided together.");
            module = explicitModule;
            operation = explicitOperation;
        }
        else if (method is not null)
        {
            if (string.Equals(method, "runtime.settings.changed", StringComparison.Ordinal))
            {
                module = "settings";
                operation = "changed";
            }
            else if (method.IndexOf('/') is int slash and >= 0)
            {
                module = method[..slash];
                operation = method[(slash + 1)..];
                if (module.Length == 0 || operation.Length == 0)
                    throw DebugApiException.InvalidArgument($"Invalid method format: '{method}'.");
            }
            else
            {
                string? matchedModule = null;
                string? matchedOperation = null;
                foreach (var (id, contract) in RuntimeHttpProtocol.Modules)
                {
                    if (!method.StartsWith(contract.Prefix, StringComparison.Ordinal)) continue;
                    var op = method[contract.Prefix.Length..];
                    if (op.Length == 0)
                        throw DebugApiException.InvalidArgument($"Empty operation in method: '{method}'.");
                    matchedModule = id;
                    matchedOperation = op;
                    break;
                }
                if (matchedModule is null && method.IndexOf('.') is int dot and > 0 && dot < method.Length - 1)
                {
                    matchedModule = method[..dot];
                    matchedOperation = method[(dot + 1)..];
                }
                if (matchedModule is null || matchedOperation is null)
                    throw DebugApiException.InvalidArgument($"Invalid method format: '{method}'.");
                module = matchedModule;
                operation = matchedOperation;
            }
        }
        else
        {
            throw DebugApiException.InvalidArgument("Missing 'method' or ('module' and 'operation').");
        }

        if (module.Length == 0 || operation.Length == 0 || module.Contains('/') || operation.Contains('/'))
            throw DebugApiException.InvalidArgument("Invalid module or operation path segment.");
        return (module, operation, $"/v1/{module}/{operation}");
    }

    private static int RequireInt(JsonElement prop, string name)
    {
        if (prop.ValueKind != JsonValueKind.Number || !prop.TryGetInt32(out var value))
            throw DebugApiException.InvalidArgument($"Field '{name}' must be an integer.");
        return value;
    }

    private static int ParseQueryInt(Dictionary<string, string> query, string name, int defaultValue, int min, int max)
    {
        if (!query.TryGetValue(name, out var raw) || string.IsNullOrWhiteSpace(raw)) return defaultValue;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            throw DebugApiException.InvalidArgument($"Query parameter '{name}' must be an integer.");
        return Math.Clamp(parsed, min, max);
    }

    private static bool ParseQueryBool(Dictionary<string, string> query, string name, bool defaultValue)
    {
        if (!query.TryGetValue(name, out var raw) || string.IsNullOrWhiteSpace(raw)) return defaultValue;
        return raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1";
    }

    private static Task WriteOkAsync(NetworkStream stream, string traceId, object? data, CancellationToken token) =>
        WriteJsonEnvelopeAsync(stream, 200, true, traceId, data, null, token);

    private async Task<JsonElement?> ForwardNodeDebugCommandAsync(string nodePath, object payload,
        TimeSpan timeout, CancellationToken token)
    {
        var runtime = getRuntime();
        if (runtime is null || !runtime.IsConnected)
            throw DebugApiException.RuntimeUnavailable("Node runtime is not connected.");
        var forward = await runtime.SendNodeDebugRequestAsync(
            HttpMethod.Post, nodePath, payload, timeout, token).ConfigureAwait(false);
        if (!forward.Available)
            throw DebugApiException.RuntimeUnavailable($"Node debug endpoint {nodePath} is unavailable.",
                new { reason = forward.Reason, status = forward.StatusCode });
        if (forward.StatusCode >= 400)
            throw MapNodeForwardError(nodePath, forward);
        if (forward.Body is { ValueKind: JsonValueKind.Object } obj &&
            obj.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.False)
        {
            var code = obj.TryGetProperty("code", out var cp) && cp.ValueKind == JsonValueKind.String
                ? cp.GetString() : null;
            throw code switch
            {
                "INVALID_REQUEST" or "INVALID_ARGUMENT" => DebugApiException.InvalidArgument($"Node {nodePath} rejected request ({code}).", forward.Body),
                "TIMEOUT" => DebugApiException.Timeout($"Node {nodePath} timed out.", forward.Body),
                "CLOSED" or "METHOD_UNAVAILABLE" => DebugApiException.RuntimeUnavailable($"Node {nodePath} unavailable ({code}).", forward.Body),
                _ => DebugApiException.Internal($"Node {nodePath} failed ({code ?? "UNKNOWN"}).", forward.Body)
            };
        }
        return DebugNodeForwardResult.UnwrapValue(forward.Body);
    }

    private static DebugApiException MapNodeForwardError(string path, DebugNodeForwardResult forward) => forward.StatusCode switch
    {
        400 => DebugApiException.InvalidArgument($"Node {path} returned HTTP 400.", forward.Body),
        503 => DebugApiException.RuntimeUnavailable($"Node {path} returned HTTP 503.", forward.Body),
        504 => DebugApiException.Timeout($"Node {path} timed out (HTTP 504).", forward.Body),
        _ => DebugApiException.Internal($"Node {path} returned HTTP {forward.StatusCode}.", forward.Body)
    };

    private static async Task TryWriteJsonEnvelopeAsync(NetworkStream stream, int statusCode, bool ok,
        string traceId, object? data, object? error, CancellationToken token)
    {
        try { await WriteJsonEnvelopeAsync(stream, statusCode, ok, traceId, data, error, token).ConfigureAwait(false); }
        catch (IOException) { }
        catch (SocketException) { }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task WriteJsonEnvelopeAsync(NetworkStream stream, int statusCode, bool ok,
        string traceId, object? data, object? error, CancellationToken token)
    {
        var ts = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        object payload = ok
            ? new { ok = true, ts, traceId, data }
            : new { ok = false, ts, traceId, error };
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await WriteBinaryResponseAsync(stream, statusCode, "application/json; charset=utf-8", body, null, token).ConfigureAwait(false);
    }

    private static byte[] BuildResponseHeaders(int statusCode, string contentType, long contentLength,
        Dictionary<string, string>? extraHeaders)
    {
        var reason = statusCode switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            409 => "Conflict",
            500 => "Internal Server Error",
            501 => "Not Implemented",
            503 => "Service Unavailable",
            504 => "Gateway Timeout",
            _ => "OK"
        };
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {statusCode} {reason}\r\n");
        sb.Append($"Content-Type: {contentType}\r\n");
        sb.Append($"Content-Length: {contentLength}\r\n");
        sb.Append("Connection: close\r\n");
        sb.Append("Cache-Control: no-store\r\n");
        if (extraHeaders is not null)
        {
            foreach (var (k, v) in extraHeaders)
                sb.Append($"{k}: {v}\r\n");
        }
        sb.Append("\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static async Task WriteBinaryResponseAsync(NetworkStream stream, int statusCode, string contentType,
        byte[] body, Dictionary<string, string>? extraHeaders, CancellationToken token)
    {
        var headerBytes = BuildResponseHeaders(statusCode, contentType, body.Length, extraHeaders);
        await stream.WriteAsync(headerBytes, token).ConfigureAwait(false);
        if (body.Length > 0) await stream.WriteAsync(body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task WriteStreamResponseAsync(NetworkStream stream, int statusCode, string contentType,
        Stream source, long length, Dictionary<string, string>? extraHeaders, CancellationToken token)
    {
        var headerBytes = BuildResponseHeaders(statusCode, contentType, length, extraHeaders);
        await stream.WriteAsync(headerBytes, token).ConfigureAwait(false);
        if (length > 0)
        {
            var buffer = new byte[64 * 1024];
            long remaining = length;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = await source.ReadAsync(buffer.AsMemory(0, toRead), token).ConfigureAwait(false);
                if (read == 0) break;
                await stream.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                remaining -= read;
            }
        }
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<HttpRequestData?> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var headerBuffer = new MemoryStream();
        var chunk = new byte[4096];
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int read = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (read == 0) return null;
            headerBuffer.Write(chunk, 0, read);
            headerEnd = FindHeaderEnd(headerBuffer.GetBuffer(), (int)headerBuffer.Length);
        }

        var allBytes = headerBuffer.GetBuffer();
        var totalBuffered = (int)headerBuffer.Length;
        var headerText = Encoding.ASCII.GetString(allBytes, 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0) throw new InvalidOperationException("Empty HTTP request line.");
        var requestParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestParts.Length < 2) throw new InvalidOperationException("Malformed HTTP request line.");
        var method = requestParts[0].ToUpperInvariant();
        var rawTarget = requestParts[1];

        int qIndex = rawTarget.IndexOf('?');
        var path = qIndex >= 0 ? rawTarget[..qIndex] : rawTarget;
        var query = ParseQueryString(qIndex >= 0 ? rawTarget[(qIndex + 1)..] : "");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        int bodyStart = headerEnd + 4;
        int alreadyReadBody = totalBuffered - bodyStart;
        if (headers.TryGetValue("Expect", out var expect) &&
            expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase) &&
            alreadyReadBody == 0)
        {
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        byte[] body;
        if (headers.TryGetValue("Transfer-Encoding", out var te) &&
            te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            body = await ReadChunkedBodyAsync(stream, allBytes, bodyStart, alreadyReadBody, token).ConfigureAwait(false);
        }
        else if (headers.TryGetValue("Content-Length", out var clRaw))
        {
            if (!int.TryParse(clRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var contentLength) || contentLength < 0)
                throw new InvalidOperationException("Invalid Content-Length header.");
            if (contentLength == 0)
            {
                body = [];
            }
            else
            {
                body = new byte[contentLength];
                int copied = Math.Min(alreadyReadBody, contentLength);
                if (copied > 0) Buffer.BlockCopy(allBytes, bodyStart, body, 0, copied);
                int offset = copied;
                while (offset < contentLength)
                {
                    int read = await stream.ReadAsync(body.AsMemory(offset, contentLength - offset), token).ConfigureAwait(false);
                    if (read == 0) throw new InvalidOperationException("Incomplete HTTP request body.");
                    offset += read;
                }
            }
        }
        else
        {
            body = [];
        }

        return new HttpRequestData(method, path, query, body);
    }

    private static async Task<byte[]> ReadChunkedBodyAsync(NetworkStream stream, byte[] initial, int offset, int count, CancellationToken token)
    {
        var raw = new MemoryStream();
        if (count > 0) raw.Write(initial, offset, count);
        var buf = new byte[4096];
        var decoded = new MemoryStream();
        int pos = 0;
        while (true)
        {
            while (FindCrlf(raw.GetBuffer(), pos, (int)raw.Length) < 0)
            {
                int read = await stream.ReadAsync(buf, token).ConfigureAwait(false);
                if (read == 0) throw new InvalidOperationException("Incomplete chunked HTTP request body.");
                raw.Write(buf, 0, read);
            }
            int lineEnd = FindCrlf(raw.GetBuffer(), pos, (int)raw.Length);
            var sizeText = Encoding.ASCII.GetString(raw.GetBuffer(), pos, lineEnd - pos).Split(';')[0].Trim();
            if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var chunkSize) || chunkSize < 0)
                throw new InvalidOperationException("Invalid chunked size.");
            pos = lineEnd + 2;
            if (chunkSize == 0) break;
            while ((int)raw.Length - pos < chunkSize + 2)
            {
                int read = await stream.ReadAsync(buf, token).ConfigureAwait(false);
                if (read == 0) throw new InvalidOperationException("Incomplete chunked HTTP request body.");
                raw.Write(buf, 0, read);
            }
            decoded.Write(raw.GetBuffer(), pos, chunkSize);
            pos += chunkSize + 2;
        }
        return decoded.ToArray();
    }

    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (int i = 0; i <= length - 4; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    private static int FindCrlf(byte[] buffer, int start, int length)
    {
        for (int i = start; i <= length - 2; i++)
            if (buffer[i] == '\r' && buffer[i + 1] == '\n') return i;
        return -1;
    }

    private static Dictionary<string, string> ParseQueryString(string raw)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(raw)) return dict;
        foreach (var pair in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq < 0) dict[SafeUnescapeDataString(pair.Replace('+', ' '))] = "";
            else dict[SafeUnescapeDataString(pair[..eq].Replace('+', ' '))] =
                SafeUnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }
        return dict;
    }

    private static string SafeUnescapeDataString(string value)
    {
        try { return Uri.UnescapeDataString(value); }
        catch (UriFormatException ex) { throw DebugApiException.InvalidArgument("Malformed percent-encoding in URI: " + ex.Message); }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try { await lifetime.CancelAsync().ConfigureAwait(false); } catch { }
        try { listener.Stop(); } catch { }
        try { await acceptLoop.ConfigureAwait(false); } catch { }
        try { await Task.WhenAll(activeConnections.Keys).ConfigureAwait(false); } catch { }
        lifetime.Dispose();
    }

    private sealed record HttpRequestData(
        string Method,
        string Path,
        Dictionary<string, string> Query,
        byte[] Body);
}
