using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using MeowFilm.Core.Debug;

namespace MeowFilm.Platform.Debug;

public sealed class DebugSession : IDebugLogger, IDisposable
{
    // FILE_APPEND_DATA | SYNCHRONIZE (deliberately omitting FILE_WRITE_DATA) forces
    // the Windows kernel to atomically position every WriteFile call at the true EOF,
    // preventing cross-process overwrites with Node's O_APPEND writes to the same JSONL file.
    private const uint FileAppendData = 0x00000004u;
    private const uint Synchronize = 0x00100000u;
    private const uint FileShareRead = 0x00000001u;
    private const uint FileShareWrite = 0x00000002u;
    private const uint FileShareDelete = 0x00000004u;
    private const uint OpenAlways = 4u;
    private const uint FileAttributeNormal = 0x00000080u;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        nint lpOverlapped);

    private readonly object fileGate = new();
    private readonly object configGate = new();
    private readonly object waiterGate = new();
    private readonly object sessionFileGate = new();
    private readonly Dictionary<string, SafeFileHandle> logHandles = new(StringComparer.Ordinal);
    private readonly List<LogWaiter> waiters = [];
    private readonly long startedTicks = Stopwatch.GetTimestamp();
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    private readonly int pid = Environment.ProcessId;
    private readonly string appVersion;
    private readonly DebugBlobStore blobs;

    private HashSet<string> enabledModules;
    private IReadOnlyList<string> moduleList;
    private int inlineLimit;
    private int eventBytesLimit;
    private bool firstChanceExceptions;
    private long seq;
    private int screenshotCounter;
    private string controlUrl = "";
    private string bindAddress;
    private int port;
    private string? runtimeBaseUrl;
    private int? runtimePid;
    private bool controlReady;
    private bool exiting;
    private bool disposed;

    public DebugSession(DebugLaunchOptions options, string debugRoot, string dataRoot)
    {
        if (!options.Enabled) throw new ArgumentException("Debug options must be enabled.", nameof(options));
        Options = options;
        DebugRoot = Path.GetFullPath(debugRoot);
        DataRoot = Path.GetFullPath(dataRoot);
        bindAddress = options.Bind;
        port = options.Port;
        inlineLimit = options.InlineLimit;
        eventBytesLimit = options.EventBytesLimit;
        firstChanceExceptions = options.FirstChanceExceptions;
        moduleList = options.Modules;
        enabledModules = BuildModuleSet(options.Modules);
        appVersion = ResolveAppVersion();

        SessionId = $"{startedAt:yyyyMMdd'T'HHmmss'Z'}-{pid}";
        SessionsRoot = Path.Combine(DebugRoot, "sessions");
        SessionDir = Path.Combine(SessionsRoot, SessionId);
        LogsDir = Path.Combine(SessionDir, "logs");
        BlobsDir = Path.Combine(SessionDir, "blobs");
        DumpsDir = Path.Combine(SessionDir, "dumps");
        ScreenshotsDir = Path.Combine(SessionDir, "screenshots");

        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(BlobsDir);
        Directory.CreateDirectory(DumpsDir);
        Directory.CreateDirectory(ScreenshotsDir);

        blobs = new DebugBlobStore(BlobsDir);
        PruneOldSessions(options.KeepSessions);
        WriteSessionFiles("starting", endedAt: null, exitSummary: null, includeActiveFile: false);
    }

    public DebugLaunchOptions Options { get; }
    public string DebugRoot { get; }
    public string SessionsRoot { get; }
    public string DataRoot { get; }
    public string SessionId { get; }
    public string SessionDir { get; }
    public string LogsDir { get; }
    public string BlobsDir { get; }
    public string DumpsDir { get; }
    public string ScreenshotsDir { get; }
    public string AppVersion => appVersion;
    public DateTimeOffset StartedAt => startedAt;
    public double UptimeMs => Stopwatch.GetElapsedTime(startedTicks).TotalMilliseconds;
    public bool IsEnabled => !disposed;
    public long CurrentSeq => Interlocked.Read(ref seq);
    public string ControlUrl { get { lock (configGate) return controlUrl; } }
    public string BindAddress { get { lock (configGate) return bindAddress; } }
    public int Port { get { lock (configGate) return port; } }
    public string? RuntimeBaseUrl { get { lock (configGate) return runtimeBaseUrl; } }
    public int? RuntimePid { get { lock (configGate) return runtimePid; } }
    public bool FirstChanceExceptions { get { lock (configGate) return firstChanceExceptions; } }

    public event Action<bool>? FirstChanceChanged;

    public bool IsCategoryEnabled(string category)
    {
        if (disposed || !DebugCategories.IsValid(category)) return false;
        lock (configGate) return enabledModules.Contains(category);
    }

    public DebugRuntimeHandshake CreateRuntimeHandshake()
    {
        lock (configGate)
        {
            return new DebugRuntimeHandshake(
                Enabled: true,
                SessionId: SessionId,
                SessionDir: SessionDir,
                InlineLimit: inlineLimit,
                EventBytesLimit: eventBytesLimit,
                Modules: moduleList);
        }
    }

    public void SetControlEndpoint(string bind, int boundPort)
    {
        lock (configGate)
        {
            bindAddress = bind;
            port = boundPort;
            var rawHost = bind is "0.0.0.0" or "::" ? "127.0.0.1" : bind;
            var host = rawHost.Contains(':') && !rawHost.StartsWith('[') ? $"[{rawHost}]" : rawHost;
            controlUrl = $"http://{host}:{boundPort}";
            controlReady = true;
        }
        WriteSessionFiles("running", null, null, includeActiveFile: true);
    }

    public void UpdateRuntimeEndpoint(string? baseUrl, int? childPid)
    {
        bool shouldWriteActive;
        lock (configGate)
        {
            runtimeBaseUrl = baseUrl;
            runtimePid = childPid;
            if (exiting) return;
            shouldWriteActive = controlReady;
        }
        WriteSessionFiles(shouldWriteActive ? "running" : "starting", null, null, includeActiveFile: shouldWriteActive);
    }

    public object GetCurrentConfigSnapshot()
    {
        lock (configGate)
        {
            return new
            {
                modules = moduleList,
                inlineLimit,
                eventBytesLimit,
                keepSessions = Options.KeepSessions,
                firstChanceExceptions
            };
        }
    }

    public object UpdateConfig(IReadOnlyList<string>? modules, int? nextInlineLimit, int? nextEventBytesLimit, bool? nextFirstChance)
    {
        bool? notifyFirstChance = null;
        lock (configGate)
        {
            if (modules is not null)
            {
                if (modules.Count == 0)
                    throw DebugApiException.InvalidArgument("modules must be a non-empty array.");
                var normalized = new List<string>(modules.Count);
                bool hasAll = false;
                foreach (var item in modules)
                {
                    var name = item?.Trim().ToLowerInvariant() ?? "";
                    if (name == "all") { hasAll = true; continue; }
                    if (!DebugCategories.IsValid(name))
                        throw DebugApiException.InvalidArgument($"Unknown log category: '{item}'.");
                    if (!normalized.Contains(name, StringComparer.Ordinal)) normalized.Add(name);
                }
                moduleList = hasAll ? ["all"] : normalized;
                enabledModules = BuildModuleSet(moduleList);
            }
            if (nextInlineLimit is { } il)
            {
                if (il < 0) throw DebugApiException.InvalidArgument("inlineLimit must be >= 0.");
                inlineLimit = il;
            }
            if (nextEventBytesLimit is { } el)
            {
                if (el < 0) throw DebugApiException.InvalidArgument("eventBytesLimit must be >= 0.");
                eventBytesLimit = el;
            }
            if (nextFirstChance is { } fc && fc != firstChanceExceptions)
            {
                firstChanceExceptions = fc;
                notifyFirstChance = fc;
            }
        }
        if (notifyFirstChance is { } changed) FirstChanceChanged?.Invoke(changed);
        bool writeActive;
        lock (configGate)
        {
            if (exiting) return GetCurrentConfigSnapshot();
            writeActive = controlReady;
        }
        WriteSessionFiles(writeActive ? "running" : "starting", null, null, includeActiveFile: writeActive);
        return GetCurrentConfigSnapshot();
    }

    public long Write(string level, string category, string sub, string @event,
        object? data = null, string? opId = null, string? traceId = null,
        double? durationMs = null, Exception? error = null, object? errorDetails = null)
    {
        if (disposed || !DebugCategories.IsValid(category)) return 0;
        int il, el;
        lock (configGate)
        {
            if (!enabledModules.Contains(category)) return 0;
            il = inlineLimit;
            el = eventBytesLimit;
        }

        long eventSeq = Interlocked.Increment(ref seq);
        double mono = Math.Round(Stopwatch.GetElapsedTime(startedTicks).TotalMilliseconds, 3);
        string ts = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        var record = new JsonObject
        {
            ["seq"] = eventSeq,
            ["ts"] = ts,
            ["monoMs"] = mono,
            ["proc"] = "app",
            ["level"] = level,
            ["cat"] = category,
            ["sub"] = sub,
            ["event"] = @event
        };
        if (!string.IsNullOrEmpty(opId)) record["opId"] = opId;
        if (!string.IsNullOrEmpty(traceId)) record["traceId"] = traceId;
        if (durationMs is { } d) record["durationMs"] = Math.Round(d, 3);
        if (data is not null) record["data"] = blobs.ExternalizeData(data, il, el);
        if (error is not null || errorDetails is not null)
            record["error"] = SerializeError(error, errorDetails, il);

        string line = record.ToJsonString(DebugBlobStore.SerializerOptions);
        byte[] utf8 = Encoding.UTF8.GetBytes(line + "\n");

        lock (fileGate)
        {
            if (disposed) return 0;
            try
            {
                if (!logHandles.TryGetValue(category, out var handle) || handle.IsInvalid || handle.IsClosed)
                {
                    var path = Path.Combine(LogsDir, category + ".jsonl");
                    handle = CreateFileW(
                        path,
                        FileAppendData | Synchronize,
                        FileShareRead | FileShareWrite | FileShareDelete,
                        0,
                        OpenAlways,
                        FileAttributeNormal,
                        0);
                    if (handle.IsInvalid)
                    {
                        handle.Dispose();
                        handle = null;
                    }
                    else
                    {
                        logHandles[category] = handle;
                    }
                }
                if (handle is not null && !WriteFile(handle, utf8, (uint)utf8.Length, out _, 0))
                {
                    handle.Dispose();
                    logHandles.Remove(category);
                }
            }
            catch
            {
                // Logging must never crash business execution.
            }
        }

        NotifyWaiters(category, sub, @event, eventSeq, line);
        return eventSeq;
    }

    public async Task<JsonElement?> WaitForEventAsync(string category, string? sub, string? @event,
        long? sinceSeq, CancellationToken cancellationToken)
    {
        var normalizedCat = category.Trim().ToLowerInvariant();
        long floor = sinceSeq ?? 0;
        // Register in-process waiter BEFORE scanning disk so no app event is lost in between.
        var waiter = new LogWaiter(normalizedCat, sub, @event, floor);
        lock (waiterGate) waiters.Add(waiter);
        using var reg = cancellationToken.Register(() => waiter.Completion.TrySetCanceled(cancellationToken));
        try
        {
            if (sinceSeq is not null)
            {
                var existing = FindFirstMatchingEventOnDisk(normalizedCat, floor, sub, @event);
                if (existing is { } hit) return hit;
            }

            // Poll disk periodically as well so events appended directly by the Node child process are also observed.
            long diskFloor = sinceSeq ?? CurrentSeq;
            var baselineTs = sinceSeq is null ? DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) : null;
            while (true)
            {
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var delay = Task.Delay(150, delayCts.Token);
                var finished = await Task.WhenAny(waiter.Completion.Task, delay).ConfigureAwait(false);
                if (finished == waiter.Completion.Task)
                {
                    delayCts.Cancel();
                    return await waiter.Completion.Task.ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                var fromDisk = FindFirstMatchingEventOnDisk(normalizedCat, sinceSeq is not null ? floor : 0, sub, @event, baselineTs,
                    excludeAppSeqAtOrBelow: sinceSeq is null ? diskFloor : null);
                if (fromDisk is { } diskHit) return diskHit;
            }
        }
        finally
        {
            lock (waiterGate) waiters.Remove(waiter);
        }
    }

    private JsonElement? FindFirstMatchingEventOnDisk(string cat, long sinceSeq, string? sub, string? eventName,
        string? minTimestamp = null, long? excludeAppSeqAtOrBelow = null)
    {
        var files = new List<string>();
        if (cat == "all")
        {
            if (Directory.Exists(LogsDir))
                files.AddRange(Directory.EnumerateFiles(LogsDir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal));
        }
        else
        {
            var path = Path.Combine(LogsDir, cat + ".jsonl");
            if (File.Exists(path)) files.Add(path);
        }

        foreach (var file in files)
        {
            try
            {
                var parsed = ReadParsedLines(file);
                int startIndex = sinceSeq > 0 ? ComputeSinceSeqStartIndex(parsed, sinceSeq) : 0;
                for (int i = startIndex; i < parsed.Count; i++)
                {
                    var entry = parsed[i];
                    var root = entry.Element;
                    if (sinceSeq > 0 && entry.IsApp && entry.Seq <= sinceSeq) continue;
                    if (excludeAppSeqAtOrBelow is { } appFloor && entry.IsApp && entry.Seq <= appFloor) continue;
                    if (minTimestamp is not null && (string.IsNullOrEmpty(entry.Ts) || string.CompareOrdinal(entry.Ts, minTimestamp) < 0))
                        continue;
                    if (!string.IsNullOrEmpty(sub) && !MatchesField(root, "sub", sub)) continue;
                    if (!string.IsNullOrEmpty(eventName) && !MatchesField(root, "event", eventName)) continue;
                    return root.Clone();
                }
            }
            catch { }
        }
        return null;
    }

    private sealed record ParsedLogLine(string RawLine, string Ts, long Seq, bool IsApp, JsonElement Element);

    private static List<ParsedLogLine> ReadParsedLines(string file)
    {
        var entries = new List<ParsedLogLine>();
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                long itemSeq = root.TryGetProperty("seq", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var sv) ? sv : 0;
                string ts = root.TryGetProperty("ts", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
                bool isApp = !root.TryGetProperty("proc", out var p) ||
                    (p.ValueKind == JsonValueKind.String && string.Equals(p.GetString(), "app", StringComparison.OrdinalIgnoreCase));
                entries.Add(new(line, ts, itemSeq, isApp, root.Clone()));
            }
            catch { }
        }
        return entries;
    }

    // C# ("app") and Node ("runtime") maintain independent per-process seq counters.
    // Never compare an app seq floor directly against runtime seq values; instead advance
    // past the last line at or below the floor in append order.
    private static int ComputeSinceSeqStartIndex(List<ParsedLogLine> entries, long floor)
    {
        bool hasApp = false;
        int lastCutoff = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].IsApp)
            {
                hasApp = true;
                if (entries[i].Seq <= floor) lastCutoff = i;
            }
        }
        if (hasApp) return lastCutoff + 1;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Seq <= floor) lastCutoff = i;
        }
        return lastCutoff + 1;
    }

    public (string FullPath, string RelativePath) SaveScreenshot(byte[] pngBytes)
    {
        var count = Interlocked.Increment(ref screenshotCounter);
        var name = $"{DateTimeOffset.UtcNow:yyyyMMdd'T'HHmmssfff'Z'}-{count:D4}.png";
        var full = Path.Combine(ScreenshotsDir, name);
        File.WriteAllBytes(full, pngBytes);
        return (full, "screenshots/" + name);
    }

    public object GetCategoriesSummary(string? sessionId = null)
    {
        var targetSessionDir = ResolveSessionDirectory(sessionId);
        var resolvedSessionId = Path.GetFileName(targetSessionDir);
        var targetLogsDir = Path.Combine(targetSessionDir, "logs");
        var items = new List<object>();
        if (!Directory.Exists(targetLogsDir)) return new { sessionId = resolvedSessionId, categories = items };
        foreach (var cat in DebugCategories.All)
        {
            var path = Path.Combine(targetLogsDir, cat + ".jsonl");
            if (!File.Exists(path)) continue;
            var info = new FileInfo(path);
            long lines = 0, latestSeq = 0;
            var levels = new Dictionary<string, long>(StringComparer.Ordinal);
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                while (reader.ReadLine() is { } line)
                {
                    if (line.Length == 0) continue;
                    lines++;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("seq", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var sv) && sv > latestSeq)
                            latestSeq = sv;
                        if (root.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.String)
                        {
                            var key = lv.GetString()!;
                            levels[key] = levels.TryGetValue(key, out var c) ? c + 1 : 1;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            items.Add(new
            {
                category = cat,
                file = $"logs/{cat}.jsonl",
                bytes = info.Length,
                lines,
                latestSeq,
                levels
            });
        }
        foreach (var extra in new[] { "runtime-node.stdout.log", "runtime-node.stderr.log" })
        {
            var path = Path.Combine(targetLogsDir, extra);
            if (!File.Exists(path)) continue;
            items.Add(new
            {
                category = extra,
                file = $"logs/{extra}",
                bytes = new FileInfo(path).Length,
                rawText = true
            });
        }
        return new { sessionId = resolvedSessionId, categories = items };
    }

    public (string SessionId, List<JsonElement> Events) TailLogs(string cat, int limit, long? sinceSeq, string? level,
        string? sub, string? eventName, string? opId, string? traceId, string? query, string? sessionId = null)
    {
        var targetSessionDir = ResolveSessionDirectory(sessionId);
        var resolvedSessionId = Path.GetFileName(targetSessionDir);
        var targetLogsDir = Path.Combine(targetSessionDir, "logs");
        limit = Math.Clamp(limit, 1, 2000);
        var files = new List<string>();
        if (string.Equals(cat, "all", StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(targetLogsDir))
                files.AddRange(Directory.EnumerateFiles(targetLogsDir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal));
        }
        else
        {
            if (!DebugCategories.IsValid(cat))
                throw DebugApiException.InvalidArgument($"Unknown log category: '{cat}'.");
            var path = Path.Combine(targetLogsDir, cat + ".jsonl");
            if (File.Exists(path)) files.Add(path);
        }

        var perFile = new List<List<ParsedLogLine>>(files.Count);
        bool anyAppAcrossFiles = false;
        string? appCutoffTs = null;
        foreach (var file in files)
        {
            try
            {
                var parsed = ReadParsedLines(file);
                perFile.Add(parsed);
                if (sinceSeq is { } floor)
                {
                    foreach (var entry in parsed)
                    {
                        if (!entry.IsApp) continue;
                        anyAppAcrossFiles = true;
                        if (entry.Seq <= floor && !string.IsNullOrEmpty(entry.Ts) &&
                            (appCutoffTs is null || string.CompareOrdinal(entry.Ts, appCutoffTs) > 0))
                        {
                            appCutoffTs = entry.Ts;
                        }
                    }
                }
            }
            catch { }
        }

        var matches = new List<(string Ts, int Order, JsonElement Element)>();
        int order = 0;
        foreach (var parsed in perFile)
        {
            bool fileHasApp = parsed.Any(e => e.IsApp);
            int startIndex = sinceSeq is { } floor && (fileHasApp || !anyAppAcrossFiles)
                ? ComputeSinceSeqStartIndex(parsed, floor)
                : 0;
            for (int i = startIndex; i < parsed.Count; i++)
            {
                var entry = parsed[i];
                if (sinceSeq is { } seqFloor)
                {
                    if (entry.IsApp && entry.Seq <= seqFloor) continue;
                    if (!fileHasApp && anyAppAcrossFiles && appCutoffTs is not null &&
                        !string.IsNullOrEmpty(entry.Ts) && string.CompareOrdinal(entry.Ts, appCutoffTs) < 0)
                        continue;
                }
                if (!string.IsNullOrEmpty(query) &&
                    !entry.RawLine.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;
                var root = entry.Element;
                if (!string.IsNullOrEmpty(level) && !MatchesField(root, "level", level)) continue;
                if (!string.IsNullOrEmpty(sub) && !MatchesField(root, "sub", sub)) continue;
                if (!string.IsNullOrEmpty(eventName) && !MatchesField(root, "event", eventName)) continue;
                if (!string.IsNullOrEmpty(opId) && !MatchesField(root, "opId", opId)) continue;
                if (!string.IsNullOrEmpty(traceId) && !MatchesField(root, "traceId", traceId)) continue;
                matches.Add((entry.Ts, order++, root));
            }
        }

        if (files.Count > 1)
            matches.Sort((a, b) =>
            {
                int cmp = string.CompareOrdinal(a.Ts, b.Ts);
                return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
            });

        if (matches.Count > limit)
            matches.RemoveRange(0, matches.Count - limit);
        return (resolvedSessionId, matches.Select(m => m.Element).ToList());
    }

    public object ListSessions()
    {
        var list = new List<object>();
        if (!Directory.Exists(SessionsRoot)) return new { currentSessionId = SessionId, debugRoot = DebugRoot, sessions = list };
        foreach (var dir in Directory.EnumerateDirectories(SessionsRoot).OrderByDescending(d => d, StringComparer.Ordinal))
        {
            var id = Path.GetFileName(dir);
            var metaPath = Path.Combine(dir, "session.json");
            string? started = null, ended = null, status = null;
            int? sessionPid = null;
            if (File.Exists(metaPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(metaPath, Encoding.UTF8));
                    var r = doc.RootElement;
                    if (r.TryGetProperty("startedAt", out var s) && s.ValueKind == JsonValueKind.String) started = s.GetString();
                    if (r.TryGetProperty("endedAt", out var e) && e.ValueKind == JsonValueKind.String) ended = e.GetString();
                    if (r.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String) status = st.GetString();
                    if (r.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var pv)) sessionPid = pv;
                }
                catch { }
            }
            long totalBytes = 0;
            int fileCount = 0;
            long logLines = 0;
            long errorCount = 0;
            var downloadableFiles = new List<string>();
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    fileCount++;
                    totalBytes += new FileInfo(f).Length;
                }
                foreach (var subdir in new[] { "logs", "dumps", "screenshots" })
                {
                    var subPath = Path.Combine(dir, subdir);
                    if (!Directory.Exists(subPath)) continue;
                    foreach (var f in Directory.EnumerateFiles(subPath, "*", SearchOption.AllDirectories)
                        .OrderBy(p => p, StringComparer.Ordinal))
                    {
                        var name = Path.GetFileName(f);
                        if (name.StartsWith('.')) continue;
                        downloadableFiles.Add(Path.GetRelativePath(dir, f).Replace('\\', '/'));
                    }
                }
                var logsPath = Path.Combine(dir, "logs");
                if (Directory.Exists(logsPath))
                {
                    foreach (var logFile in Directory.EnumerateFiles(logsPath, "*.jsonl"))
                    {
                        using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var reader = new StreamReader(fs, Encoding.UTF8);
                        while (reader.ReadLine() is { } line)
                        {
                            if (line.Length == 0) continue;
                            logLines++;
                            if (line.Contains("\"level\":\"error\"", StringComparison.Ordinal) ||
                                line.Contains("\"level\":\"fatal\"", StringComparison.Ordinal))
                                errorCount++;
                        }
                    }
                }
            }
            catch { }
            list.Add(new
            {
                sessionId = id,
                active = string.Equals(id, SessionId, StringComparison.Ordinal),
                status = status ?? (string.Equals(id, SessionId, StringComparison.Ordinal) ? "running" : "unknown"),
                startedAt = started,
                endedAt = ended,
                pid = sessionPid,
                sizeBytes = totalBytes,
                fileCount,
                logLines,
                errorCount,
                files = downloadableFiles
            });
        }
        return new { currentSessionId = SessionId, debugRoot = DebugRoot, sessions = list };
    }

    public string ResolveSessionDirectory(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.Equals(sessionId, SessionId, StringComparison.Ordinal))
            return SessionDir;
        if (sessionId != sessionId.Trim() || sessionId is "." or ".." ||
            sessionId.Contains('/') || sessionId.Contains('\\') || sessionId.Contains("..") ||
            sessionId.Contains(':') || sessionId.EndsWith('.') ||
            sessionId.Any(char.IsControl) || sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw DebugApiException.InvalidArgument("Invalid sessionId.");
        var candidate = Path.GetFullPath(Path.Combine(SessionsRoot, sessionId));
        var rootPrefix = Path.GetFullPath(SessionsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(candidate))
            throw DebugApiException.NotFound($"Session not found: '{sessionId}'.");
        return candidate;
    }

    public string ResolveSessionFile(string? sessionId, string relativePath)
    {
        var targetSession = ResolveSessionDirectory(sessionId);
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains('\0') || relativePath.Contains(':'))
            throw DebugApiException.InvalidArgument("Invalid file path.");
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or ".."))
            throw DebugApiException.InvalidArgument("Path traversal is not allowed.");
        bool allowed = (parts.Length == 1 && parts[0] == "session.json") ||
            (parts.Length >= 2 && parts[0] is "logs" or "blobs" or "dumps" or "screenshots");
        if (!allowed)
            throw DebugApiException.InvalidArgument("Only session.json, logs/*, blobs/*, dumps/*, screenshots/* are accessible.");
        var full = Path.GetFullPath(Path.Combine(targetSession, Path.Combine(parts)));
        var sessionPrefix = Path.GetFullPath(targetSession).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(sessionPrefix, StringComparison.OrdinalIgnoreCase))
            throw DebugApiException.InvalidArgument("Resolved path escaped session directory.");
        if (!File.Exists(full))
            throw DebugApiException.NotFound($"File not found: '{normalized}'.");
        return full;
    }

    public void MarkExiting(int exitCode, bool cleanDrain, string? lastError = null)
    {
        bool writeActive;
        lock (configGate)
        {
            exiting = true;
            writeActive = controlReady;
        }
        WriteSessionFiles("exited", DateTimeOffset.UtcNow, new
        {
            exitCode,
            cleanDrain,
            lastError
        }, includeActiveFile: writeActive);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lock (waiterGate)
        {
            foreach (var w in waiters) w.Completion.TrySetCanceled();
            waiters.Clear();
        }
        lock (fileGate)
        {
            foreach (var handle in logHandles.Values)
            {
                try { handle.Dispose(); } catch { }
            }
            logHandles.Clear();
        }
    }

    private void NotifyWaiters(string category, string sub, string @event, long eventSeq, string rawLine)
    {
        List<LogWaiter>? matched = null;
        lock (waiterGate)
        {
            if (waiters.Count == 0) return;
            foreach (var w in waiters)
            {
                if (eventSeq > w.SinceSeq &&
                    (string.Equals(w.Category, "all", StringComparison.OrdinalIgnoreCase) || string.Equals(w.Category, category, StringComparison.OrdinalIgnoreCase)) &&
                    (w.Sub is null || string.Equals(w.Sub, sub, StringComparison.OrdinalIgnoreCase)) &&
                    (w.Event is null || string.Equals(w.Event, @event, StringComparison.OrdinalIgnoreCase)))
                {
                    matched ??= [];
                    matched.Add(w);
                }
            }
        }
        if (matched is null) return;
        try
        {
            using var doc = JsonDocument.Parse(rawLine);
            var clone = doc.RootElement.Clone();
            foreach (var w in matched) w.Completion.TrySetResult(clone);
        }
        catch { }
    }

    private void WriteSessionFiles(string status, DateTimeOffset? endedAt, object? exitSummary, bool includeActiveFile)
    {
        string ctrlUrl, bind;
        int boundPort;
        string? rtUrl;
        int? rtPid;
        object optionsSnapshot;
        lock (configGate)
        {
            ctrlUrl = controlUrl;
            bind = bindAddress;
            boundPort = port;
            rtUrl = runtimeBaseUrl;
            rtPid = runtimePid;
            optionsSnapshot = new
            {
                modules = moduleList,
                inlineLimit,
                eventBytesLimit,
                keepSessions = Options.KeepSessions,
                firstChanceExceptions
            };
        }

        var activePayload = new
        {
            schemaVersion = 1,
            status,
            platform = "windows",
            appVersion,
            pid,
            sessionId = SessionId,
            sessionDir = SessionDir,
            controlUrl = ctrlUrl,
            bind,
            port = boundPort,
            runtimeBaseUrl = rtUrl,
            runtimePid = rtPid,
            dataRoot = DataRoot,
            startedAt = startedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            endedAt = endedAt?.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            options = optionsSnapshot
        };

        var sessionPayload = new
        {
            schemaVersion = 1,
            status,
            platform = "windows",
            appVersion,
            pid,
            sessionId = SessionId,
            sessionDir = SessionDir,
            controlUrl = ctrlUrl,
            bind,
            port = boundPort,
            runtimeBaseUrl = rtUrl,
            runtimePid = rtPid,
            dataRoot = DataRoot,
            startedAt = startedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            endedAt = endedAt?.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            options = optionsSnapshot,
            environment = new
            {
                osDescription = RuntimeInformation.OSDescription,
                osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                frameworkDescription = RuntimeInformation.FrameworkDescription,
                machineName = Environment.MachineName,
                processorCount = Environment.ProcessorCount,
                baseDirectory = AppContext.BaseDirectory
            },
            exitSummary
        };

        lock (sessionFileGate)
        {
            if (includeActiveFile)
                WriteAtomicJson(Path.Combine(DebugRoot, "active-session.json"), activePayload);
            WriteAtomicJson(Path.Combine(SessionDir, "session.json"), sessionPayload);
        }
    }

    private static void WriteAtomicJson(string path, object payload)
    {
        string? temp = null;
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = true
            });
            File.WriteAllText(temp, json + "\n", new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        catch { }
        finally
        {
            if (temp is not null && File.Exists(temp))
            {
                try { File.Delete(temp); } catch { }
            }
        }
    }

    private void PruneOldSessions(int keepSessions)
    {
        try
        {
            if (!Directory.Exists(SessionsRoot)) return;
            var otherDirs = Directory.EnumerateDirectories(SessionsRoot)
                .Where(d => !string.Equals(Path.GetFileName(d), SessionId, StringComparison.Ordinal))
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                .ToList();
            int allowOthers = Math.Max(0, keepSessions - 1);
            for (int i = allowOthers; i < otherDirs.Count; i++)
            {
                try { Directory.Delete(otherDirs[i], recursive: true); } catch { }
            }
        }
        catch { }
    }

    private JsonNode SerializeError(Exception? error, object? details, int currentInlineLimit)
    {
        var obj = new JsonObject();
        if (error is not null)
        {
            obj["type"] = error.GetType().FullName ?? error.GetType().Name;
            obj["message"] = error.Message;
            if (error is MeowFilm.Core.Runtime.BridgeException bridge)
                obj["code"] = bridge.Code;
            if (!string.IsNullOrEmpty(error.StackTrace))
                obj["stack"] = error.StackTrace;
            if (error.InnerException is { } inner)
            {
                obj["inner"] = new JsonObject
                {
                    ["type"] = inner.GetType().FullName ?? inner.GetType().Name,
                    ["message"] = inner.Message,
                    ["stack"] = inner.StackTrace
                };
            }
        }
        if (details is not null)
            obj["details"] = blobs.ExternalizeData(details, currentInlineLimit, 0);
        return blobs.ExternalizeData(obj, currentInlineLimit, 0) ?? obj;
    }

    private static HashSet<string> BuildModuleSet(IReadOnlyList<string> modules)
    {
        if (modules.Any(m => string.Equals(m, "all", StringComparison.OrdinalIgnoreCase)))
            return new HashSet<string>(DebugCategories.All, StringComparer.Ordinal);
        return new HashSet<string>(modules, StringComparer.Ordinal);
    }

    private static bool MatchesField(JsonElement root, string property, string expected) =>
        root.TryGetProperty(property, out var prop) &&
        prop.ValueKind == JsonValueKind.String &&
        string.Equals(prop.GetString(), expected, StringComparison.OrdinalIgnoreCase);

    private static string ResolveAppVersion()
    {
        var entry = Assembly.GetEntryAssembly();
        var info = entry?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info)) return info;
        return entry?.GetName().Version?.ToString() ?? "0.1.0";
    }

    private sealed class LogWaiter(string category, string? sub, string? @event, long sinceSeq)
    {
        public string Category { get; } = category;
        public string? Sub { get; } = sub;
        public string? Event { get; } = @event;
        public long SinceSeq { get; } = sinceSeq;
        public TaskCompletionSource<JsonElement?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
