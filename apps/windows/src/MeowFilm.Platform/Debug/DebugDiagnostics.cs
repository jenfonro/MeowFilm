using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using MeowFilm.Core.Debug;

namespace MeowFilm.Platform.Debug;

public sealed class DebugDiagnostics : IDisposable
{
    private const int ExceptionRingCapacity = 512;
    [ThreadStatic] private static bool inExceptionHandler;

    private readonly DebugSession session;
    private readonly object exceptionGate = new();
    private readonly Queue<CapturedException> exceptionRing = new(ExceptionRingCapacity);
    private readonly SemaphoreSlim dumpGate = new(1, 1);
    private long exceptionCounter;
    private int dumpCounter;
    private volatile bool firstChanceEnabled;
    private bool disposed;

    public DebugDiagnostics(DebugSession session)
    {
        this.session = session;
        firstChanceEnabled = session.FirstChanceExceptions;
        session.FirstChanceChanged += OnFirstChanceChanged;
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    public void RecordException(string kind, Exception exception, string? source = null)
    {
        if (disposed || inExceptionHandler) return;
        inExceptionHandler = true;
        try
        {
            var corr = DebugLog.Correlation;
            var item = new CapturedException(
                Seq: Interlocked.Increment(ref exceptionCounter),
                Ts: DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                Kind: kind,
                Source: source ?? "app",
                ThreadId: Environment.CurrentManagedThreadId,
                Type: exception.GetType().FullName ?? exception.GetType().Name,
                Message: exception.Message,
                Stack: exception.StackTrace ?? Environment.StackTrace,
                OpId: corr?.OpId,
                TraceId: corr?.TraceId);
            lock (exceptionGate)
            {
                if (exceptionRing.Count >= ExceptionRingCapacity) exceptionRing.Dequeue();
                exceptionRing.Enqueue(item);
            }
            var level = kind == "first_chance" ? "debug" : "error";
            session.Write(level, DebugCategories.Diagnostics, "exception." + kind, "exception.captured",
                new { kind, source = item.Source, threadId = item.ThreadId, exceptionSeq = item.Seq },
                opId: item.OpId, traceId: item.TraceId, error: exception);
        }
        catch { }
        finally { inExceptionHandler = false; }
    }

    public object GetExceptions(int limit, string kind)
    {
        limit = Math.Clamp(limit, 1, ExceptionRingCapacity);
        var normalizedKind = string.IsNullOrWhiteSpace(kind) ? "all" : kind.Trim().ToLowerInvariant();
        if (normalizedKind is not ("all" or "first_chance" or "unhandled" or "unobserved" or "handled_api"))
            throw DebugApiException.InvalidArgument($"Invalid exception kind: '{kind}' (expected all|first_chance|unhandled|unobserved).");
        CapturedException[] snapshot;
        lock (exceptionGate) snapshot = exceptionRing.ToArray();
        IEnumerable<CapturedException> filtered = normalizedKind == "all"
            ? snapshot
            : snapshot.Where(e => string.Equals(e.Kind, normalizedKind, StringComparison.OrdinalIgnoreCase));
        var items = filtered.TakeLast(limit).Select(e => new
        {
            seq = e.Seq,
            ts = e.Ts,
            kind = e.Kind,
            source = e.Source,
            threadId = e.ThreadId,
            type = e.Type,
            message = e.Message,
            stack = e.Stack,
            opId = e.OpId,
            traceId = e.TraceId
        }).ToArray();
        return new
        {
            firstChanceEnabled,
            totalCaptured = Interlocked.Read(ref exceptionCounter),
            count = items.Length,
            items
        };
    }

    public async Task<object> GetSummaryAsync(IDebugRuntimeHost? runtime, CancellationToken cancellationToken)
    {
        var nativeSnapshot = CaptureNativeMemorySnapshot();
        var leases = DebugLeaseRegistry.Snapshot();
        object nodeSummary;
        if (runtime is null || !runtime.IsConnected)
        {
            nodeSummary = new { available = false, reason = "RUNTIME_UNAVAILABLE" };
        }
        else
        {
            var forward = await runtime.SendNodeDebugRequestAsync(
                HttpMethod.Get, "/v1/debug/diagnostics", null, TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
            nodeSummary = forward.ToDiagnosticSection();
        }

        var result = new
        {
            platform = "windows",
            pid = Environment.ProcessId,
            uptimeMs = Math.Round(session.UptimeMs, 3),
            native = nativeSnapshot,
            leases,
            exceptionsCaptured = Interlocked.Read(ref exceptionCounter),
            firstChanceEnabled,
            node = nodeSummary
        };
        DebugLog.Info(DebugCategories.Diagnostics, "summary", "diag.summary", nativeSnapshot);
        return result;
    }

    public async Task<object> GetThreadsAsync(IDebugShellHost? shell, IDebugRuntimeHost? runtime, CancellationToken cancellationToken)
    {
        using var proc = Process.GetCurrentProcess();
        proc.Refresh();
        var osThreads = new List<object>();
        foreach (ProcessThread t in proc.Threads)
        {
            using (t)
            {
                try
                {
                string? waitReason = null;
                try { if (t.ThreadState == System.Diagnostics.ThreadState.Wait) waitReason = t.WaitReason.ToString(); } catch { }
                double userMs = 0, totalMs = 0;
                try { userMs = Math.Round(t.UserProcessorTime.TotalMilliseconds, 2); } catch { }
                try { totalMs = Math.Round(t.TotalProcessorTime.TotalMilliseconds, 2); } catch { }
                osThreads.Add(new
                {
                    id = t.Id,
                    state = t.ThreadState.ToString(),
                    waitReason,
                    basePriority = t.BasePriority,
                    currentPriority = t.CurrentPriority,
                    userProcessorTimeMs = userMs,
                    totalProcessorTimeMs = totalMs
                });
                }
                catch { }
            }
        }

        ThreadPool.GetAvailableThreads(out int availWorkers, out int availIo);
        ThreadPool.GetMaxThreads(out int maxWorkers, out int maxIo);
        ThreadPool.GetMinThreads(out int minWorkers, out int minIo);

        object? uiProbe = null;
        if (shell is not null)
        {
            try
            {
                using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                uiProbe = await shell.ProbeUiThreadAsync(probeTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                uiProbe = new { responsive = false, error = ex.GetType().Name, message = ex.Message };
            }
        }

        object nodeInfo = new { available = false, reason = "RUNTIME_UNAVAILABLE" };
        if (runtime is { IsConnected: true })
        {
            var forward = await runtime.SendNodeDebugRequestAsync(
                HttpMethod.Get, "/v1/debug/diagnostics", null, TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
            nodeInfo = forward.ToDiagnosticSection();
        }

        return new
        {
            pid = Environment.ProcessId,
            osThreadCount = osThreads.Count,
            osThreads,
            threadPool = new
            {
                threadCount = ThreadPool.ThreadCount,
                pendingWorkItemCount = ThreadPool.PendingWorkItemCount,
                completedWorkItemCount = ThreadPool.CompletedWorkItemCount,
                activeWorkers = maxWorkers - availWorkers,
                maxWorkers,
                minWorkers,
                activeIo = maxIo - availIo,
                maxIo,
                minIo
            },
            uiThread = uiProbe,
            diagnosticThreadStack = Environment.StackTrace,
            node = nodeInfo
        };
    }

    public async Task<object> RunGcAsync(IDebugRuntimeHost? runtime, CancellationToken cancellationToken)
    {
        var before = CaptureNativeMemorySnapshot();
        var started = Stopwatch.GetTimestamp();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var after = CaptureNativeMemorySnapshot();

        object nodeGc;
        if (runtime is null || !runtime.IsConnected)
        {
            nodeGc = new { available = false, reason = "RUNTIME_UNAVAILABLE" };
        }
        else
        {
            var forward = await runtime.SendNodeDebugRequestAsync(
                HttpMethod.Post, "/v1/debug/gc", new { }, TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(false);
            nodeGc = forward.ToDiagnosticSection();
        }

        var result = new
        {
            native = new
            {
                durationMs = Math.Round(elapsedMs, 3),
                before,
                after
            },
            node = nodeGc
        };
        DebugLog.Info(DebugCategories.Diagnostics, "gc", "diag.gc", result, durationMs: elapsedMs);
        return result;
    }

    public async Task<object> WriteMinidumpAsync(string mode, CancellationToken cancellationToken)
    {
        var normalized = string.IsNullOrWhiteSpace(mode) ? "mini" : mode.Trim().ToLowerInvariant();
        if (normalized is not ("mini" or "heap"))
            throw DebugApiException.InvalidArgument($"Invalid dump mode: '{mode}' (expected 'mini' or 'heap').");

        await dumpGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        Directory.CreateDirectory(session.DumpsDir);
        var seqNum = Interlocked.Increment(ref dumpCounter);
        var fileName = $"{DateTimeOffset.UtcNow:yyyyMMdd'T'HHmmssfff'Z'}-{seqNum:D3}-{normalized}.dmp";
        var fullPath = Path.Combine(session.DumpsDir, fileName);
        var relativePath = "dumps/" + fileName;
        var started = Stopwatch.GetTimestamp();

        // Self-minidump runs on a dedicated OS thread so thread stacks are clean and non-deadlocking.
        var tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dumpThread = new Thread(() =>
        {
            try
            {
                uint flags = normalized == "heap"
                    ? (MiniDumpWithFullMemory | MiniDumpWithHandleData | MiniDumpWithThreadInfo | MiniDumpWithUnloadedModules)
                    : (MiniDumpNormal | MiniDumpWithDataSegs | MiniDumpWithHandleData | MiniDumpWithThreadInfo | MiniDumpWithUnloadedModules);
                using var fs = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var proc = Process.GetCurrentProcess();
                bool ok = MiniDumpWriteDump(proc.Handle, (uint)proc.Id, fs.SafeFileHandle, flags, 0, 0, 0);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException($"MiniDumpWriteDump failed with Win32 error {err}.");
                }
                fs.Flush(true);
                tcs.TrySetResult(fs.Length);
            }
            catch (Exception ex)
            {
                try { if (File.Exists(fullPath)) File.Delete(fullPath); } catch { }
                tcs.TrySetException(ex);
            }
        }) { IsBackground = true, Name = "meowfilm-debug-minidump" };
        dumpThread.Start();

        var bytes = await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var result = new
        {
            mode = normalized,
            file = relativePath,
            fullPath,
            bytes,
            durationMs = Math.Round(elapsedMs, 3)
        };
        DebugLog.Info(DebugCategories.Diagnostics, "dump", "diag.dump", result, durationMs: elapsedMs);
        return result;
        }
        finally
        {
            dumpGate.Release();
        }
    }

    private static object CaptureNativeMemorySnapshot()
    {
        using var proc = Process.GetCurrentProcess();
        proc.Refresh();
        var gcInfo = GC.GetGCMemoryInfo();
        return new
        {
            workingSetBytes = proc.WorkingSet64,
            privateMemoryBytes = proc.PrivateMemorySize64,
            virtualMemoryBytes = proc.VirtualMemorySize64,
            pagedMemoryBytes = proc.PagedMemorySize64,
            managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
            gcHeapSizeBytes = gcInfo.HeapSizeBytes,
            gcFragmentedBytes = gcInfo.FragmentedBytes,
            gcTotalCommittedBytes = gcInfo.TotalCommittedBytes,
            gcGen0Count = GC.CollectionCount(0),
            gcGen1Count = GC.CollectionCount(1),
            gcGen2Count = GC.CollectionCount(2),
            threadCount = proc.Threads.Count,
            handleCount = proc.HandleCount
        };
    }

    private void OnFirstChanceChanged(bool enabled) => firstChanceEnabled = enabled;

    private void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e)
    {
        if (!firstChanceEnabled || e.Exception is null) return;
        RecordException("first_chance", e.Exception, "AppDomain.FirstChance");
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            RecordException("unhandled", ex, e.IsTerminating ? "AppDomain.Unhandled(terminating)" : "AppDomain.Unhandled");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        RecordException("unobserved", e.Exception, "TaskScheduler.Unobserved");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        session.FirstChanceChanged -= OnFirstChanceChanged;
        AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        dumpGate.Dispose();
    }

    private const uint MiniDumpNormal = 0x00000000;
    private const uint MiniDumpWithDataSegs = 0x00000001;
    private const uint MiniDumpWithFullMemory = 0x00000002;
    private const uint MiniDumpWithHandleData = 0x00000004;
    private const uint MiniDumpWithUnloadedModules = 0x00000020;
    private const uint MiniDumpWithThreadInfo = 0x00001000;

    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(
        nint hProcess,
        uint processId,
        SafeHandle hFile,
        uint dumpType,
        nint exceptionParam,
        nint userStreamParam,
        nint callbackParam);

    private sealed record CapturedException(
        long Seq,
        string Ts,
        string Kind,
        string Source,
        int ThreadId,
        string Type,
        string Message,
        string Stack,
        string? OpId,
        string? TraceId);
}
