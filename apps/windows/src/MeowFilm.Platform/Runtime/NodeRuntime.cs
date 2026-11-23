using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MeowFilm.Core.Debug;
using MeowFilm.Core.Runtime;
using Microsoft.Win32.SafeHandles;

namespace MeowFilm.Platform.Runtime;

/// <summary>Owns one child, its Job, private lifetime pipe and HTTP client.</summary>
public sealed partial class NodeRuntime : IRuntimeSession, IRuntimeConnectionSource
{
    private const int PipeBufferBytes = 64 * 1024;
    private readonly Process process;
    private readonly int processId;
    private readonly NamedPipeServerStream pipe;
    private readonly RuntimeHttpClient client;
    private readonly Task outputDrain, errorDrain, processExit;
    private readonly Task<string> pipeEnd;
    private readonly OwnedProcessJob job;
    private readonly object gate = new();
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Uri businessOrigin;
    private readonly CancellationTokenSource capability;
    private readonly CancellationToken revoked;
    private readonly IDisposable? leaseRegistration;
    private Task? disposal;

    private NodeRuntime(Process process, NamedPipeServerStream pipe, RuntimeHttpClient client,
        Task output, Task error, Task processExit, OwnedProcessJob job, Uri businessOrigin, CancellationToken ownerRevoked,
        RuntimeSettingsPipe settingsPipe)
    {
        this.process = process; processId = process.Id; this.pipe = pipe; this.client = client;
        outputDrain = output; errorDrain = error; this.processExit = processExit; this.job = job;
        this.businessOrigin = businessOrigin;
        capability = CancellationTokenSource.CreateLinkedTokenSource(ownerRevoked);
        revoked = capability.Token;
        pipeEnd = settingsPipe.RunAsync();
        if (DebugLog.IsEnabled)
        {
            leaseRegistration = DebugLeaseRegistry.Register("runtime.node", () => new
            {
                connected = IsConnected,
                pid = processId,
                baseUrl = client.EndpointUrl,
                activeHttpRequests = client.ActiveRequestCount
            });
        }
        _ = MonitorAsync();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    public int ProcessId => processId;
    public string BaseUrl => client.EndpointUrl;
    public bool IsConnected { get { lock (gate) return disposal is null && !revoked.IsCancellationRequested &&
        !processExit.IsCompleted && !pipeEnd.IsCompleted; } }

    public Task<DebugNodeForwardResult> SendNodeDebugRequestAsync(HttpMethod method, string path,
        object? body, TimeSpan timeout, CancellationToken cancellationToken) =>
        IsConnected
            ? client.SendNodeDebugRequestAsync(method, path, body, timeout, cancellationToken)
            : Task.FromResult(new DebugNodeForwardResult(false, 503, null, "RUNTIME_CLOSED"));

    public Task<DebugNodeInvokeResult> InvokeDebugRawAsync(string path, JsonElement? input,
        TimeSpan timeout, CancellationToken cancellationToken) =>
        IsConnected
            ? client.InvokeDebugRawAsync(path, input, timeout, cancellationToken)
            : Task.FromException<DebugNodeInvokeResult>(DebugApiException.RuntimeUnavailable("Node runtime connection closed."));
    /// <summary>Proves release after the actual child, pipe and output drains stop;
    /// it is not a graceful-close acknowledgement.</summary>
    public Task Completion => completion.Task;

    public Task<RuntimeConnection> CaptureConnectionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!IsConnected) throw new BridgeException("CLOSED");
            return Task.FromResult(new RuntimeConnection(this, businessOrigin, revoked));
        }
    }

    public static async Task<NodeRuntime> StartAsync(string nodeExecutable, RuntimeBundle bundle,
        IReadOnlyList<RuntimeModule> expectedModules, string scriptDataRoot,
        CancellationToken cancellationToken = default, CancellationToken ownerRevoked = default,
        IRuntimeSettingsStore? settingsStore = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string node, dataRoot;
        try
        {
            node = Path.GetFullPath(nodeExecutable);
            if (!File.Exists(node) || !Path.IsPathFullyQualified(nodeExecutable)
                || !Path.IsPathFullyQualified(scriptDataRoot)
                || scriptDataRoot.Any(char.IsControl)) throw new BridgeException("INVALID_MESSAGE");
            dataRoot = Path.GetFullPath(scriptDataRoot);
        }
        catch { throw new BridgeException("INVALID_MESSAGE"); }
        var sessionId = Guid.NewGuid().ToString("D");
        var pipeName = "meowfilm-" + Guid.NewGuid().ToString("D");
        NamedPipeServerStream? server = null;
        Process? child = null;
        OwnedProcessJob? job = null;
        RuntimeHttpClient? client = null;
        NodeRuntime? runtime = null;
        Task output = Task.CompletedTask, error = Task.CompletedTask;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var debugLogger = DebugLog.Current is { IsEnabled: true } activeLogger ? activeLogger : null;
        var started = debugLogger is not null ? Stopwatch.GetTimestamp() : 0L;
        if (debugLogger is not null)
        {
            DebugLog.Info(DebugCategories.RuntimeBridge, "node.start", "handshake.start",
                new { sessionId, pipeName, dataRoot, nodeExecutable = node, entry = bundle.EntryPath });
        }
        try
        {
            server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, PipeBufferBytes, PipeBufferBytes);
            var start = new ProcessStartInfo(node)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                WorkingDirectory = bundle.DirectoryPath
            };
            var inherited = new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" }
                .Select(key => (Key: key, Value: Environment.GetEnvironmentVariable(key))).ToArray();
            start.Environment.Clear();
            foreach (var pair in inherited) if (pair.Value is not null) start.Environment[pair.Key] = pair.Value;
            foreach (var argument in new[] { bundle.EntryPath, "--meow-pipe", pipeName, "--meow-session", sessionId })
                start.ArgumentList.Add(argument);
            child = Process.Start(start) ?? throw new BridgeException("CLOSED");
            string? stdoutLog = debugLogger is not null ? Path.Combine(debugLogger.SessionDir, "logs", "runtime-node.stdout.log") : null;
            string? stderrLog = debugLogger is not null ? Path.Combine(debugLogger.SessionDir, "logs", "runtime-node.stderr.log") : null;
            output = DrainAsync(child.StandardOutput.BaseStream, stdoutLog, isStderr: false);
            error = DrainAsync(child.StandardError.BaseStream, stderrLog, isStderr: true);
            job = OwnedProcessJob.Attach(child);
            child.StandardInput.Close();
            var exited = child.WaitForExitAsync();
            var connect = server.WaitForConnectionAsync(deadline.Token);
            if (await Task.WhenAny(connect, exited).ConfigureAwait(false) != connect)
            {
                await deadline.CancelAsync().ConfigureAwait(false);
                try { await connect.ConfigureAwait(false); } catch { }
                throw new BridgeException("CLOSED");
            }
            await connect.ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var pid) || pid != (uint)child.Id)
                throw new BridgeException("INVALID_MESSAGE");
            object handshakePayload;
            if (debugLogger is not null)
            {
                var h = debugLogger.CreateRuntimeHandshake();
                var debugObj = new
                {
                    enabled = true,
                    sessionId = h.SessionId,
                    sessionDir = h.SessionDir,
                    inlineLimit = h.InlineLimit,
                    eventBytesLimit = h.EventBytesLimit,
                    modules = h.Modules
                };
                handshakePayload = settingsStore is null
                    ? new { sessionId, dataRoot, debug = debugObj }
                    : (object)new { sessionId, dataRoot, nativeSettings = true, debug = debugObj };
            }
            else
            {
                handshakePayload = settingsStore is null
                    ? new { sessionId, dataRoot }
                    : (object)new { sessionId, dataRoot, nativeSettings = true };
            }
            var config = BridgeCodec.EncodeJSONValue(handshakePayload);
            await server.WriteAsync(config, deadline.Token).ConfigureAwait(false);
            await server.WriteAsync("\n"u8.ToArray(), deadline.Token).ConfigureAwait(false);
            await server.FlushAsync(deadline.Token).ConfigureAwait(false);
            var settingsPipe = new RuntimeSettingsPipe(server, settingsStore);
            var ready = await settingsPipe.ReadyAsync(deadline.Token).ConfigureAwait(false);
            client = new RuntimeHttpClient(ready);
            runtime = new NodeRuntime(child, server, client, output, error, exited, job,
                ready, ownerRevoked, settingsPipe);
            var description = await client.DescribeAsync(deadline.Token).ConfigureAwait(false);
            if (!description.GetProperty("ok").GetBoolean())
                throw new BridgeException(description.GetProperty("code").GetString()!);
            RuntimeHttpProtocol.ValidateDescription(description.GetProperty("value"), bundle.Product, expectedModules);
            deadline.Token.ThrowIfCancellationRequested();
            if (!runtime.IsConnected) throw new BridgeException("CLOSED");
            if (debugLogger is not null)
            {
                DebugLog.Info(DebugCategories.RuntimeBridge, "node.start", "handshake.ready",
                    new { sessionId, pid = child.Id, baseUrl = ready.AbsoluteUri },
                    durationMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            return runtime;
        }
        catch (Exception original)
        {
            if (debugLogger is not null)
            {
                int? exitCode = null;
                try { if (child is { HasExited: true }) exitCode = child.ExitCode; } catch { }
                DebugLog.Error(DebugCategories.RuntimeBridge, "node.start", "handshake.failed",
                    new { sessionId, exitCode }, error: original,
                    durationMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            var failedCleanup = false;
            var failure = original is BridgeException known ? known.Code :
                original is OperationCanceledException ? "TIMEOUT" : "CLOSED";
            if (runtime is not null)
            {
                try { await runtime.StopAsync().ConfigureAwait(false); }
                catch { failedCleanup = true; }
            }
            else
            {
                if (client is not null)
                { try { await client.DisposeAsync().ConfigureAwait(false); } catch { failedCleanup = true; } }
                try { server?.Dispose(); } catch { failedCleanup = true; }
                if (child is not null)
                {
                    try { await StopProcessAsync(child, job).ConfigureAwait(false); } catch { failedCleanup = true; }
                }
                try { job?.Dispose(); } catch { failedCleanup = true; }
                try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch { failedCleanup = true; }
                child?.Dispose();
            }
            if (failedCleanup) throw new RuntimeStartupCleanupException();
            throw new BridgeException(cancellationToken.IsCancellationRequested ? "CANCELLED" : failure);
        }
    }

    public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        if (!IsConnected) return Task.FromException<JsonElement>(new BridgeException("CLOSED"));
        return client.CallAsync(method, parameters, cancellationToken);
    }
    public Task<JsonElement> CallAsync(string method, object? parameters, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected) return Task.FromException<JsonElement>(new BridgeException("CLOSED"));
        return client.CallAsync(method, parameters, timeout, cancellationToken);
    }

    public IAsyncEnumerable<JsonElement> EventsAsync(string module, Guid sessionId, long after = 0,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected) throw new BridgeException("CLOSED");
        return client.EventsAsync(module, sessionId, after, cancellationToken);
    }

    public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
        ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default)
    {
        if (!IsConnected) return Task.FromException<JsonElement>(new BridgeException("CLOSED"));
        return client.UploadSourceAsync(module, installationId, sourceRevision, source, basename, cancellationToken);
    }

    public Task<byte[]> ReadImageAsync(string reference, CancellationToken cancellationToken = default)
    {
        if (!IsConnected) return Task.FromException<byte[]>(new BridgeException("CLOSED"));
        return client.ReadImageAsync(reference, cancellationToken);
    }

    private async Task MonitorAsync()
    {
        await Task.WhenAny(pipeEnd, processExit).ConfigureAwait(false);
        try { await StopAsync().ConfigureAwait(false); }
        catch { /* The owner observes actual release failure through Completion. */ }
    }

    private static async Task DrainAsync(Stream stream, string? logPath, bool isStderr)
    {
        var bytes = new byte[4096];
        if (string.IsNullOrEmpty(logPath))
        {
            // Non-debug mode discards child diagnostics: no payload, provider stack or credential is persisted.
            try { while (await stream.ReadAsync(bytes).ConfigureAwait(false) != 0) { } }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            return;
        }
        FileStream? file = null;
        var lineBuffer = isStderr ? new StringBuilder() : null;
        var decoder = isStderr ? Encoding.UTF8.GetDecoder() : null;
        var charBuf = isStderr ? new char[4096] : null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            file = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 8192);
        }
        catch
        {
            file = null;
        }
        try
        {
            int read;
            while ((read = await stream.ReadAsync(bytes).ConfigureAwait(false)) != 0)
            {
                if (file is not null)
                {
                    try
                    {
                        await file.WriteAsync(bytes.AsMemory(0, read)).ConfigureAwait(false);
                        await file.FlushAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        try { file.Dispose(); } catch { }
                        file = null;
                    }
                }
                if (lineBuffer is not null && decoder is not null && charBuf is not null)
                {
                    int decoded = decoder.GetChars(bytes, 0, read, charBuf, 0, flush: false);
                    if (decoded > 0) lineBuffer.Append(charBuf, 0, decoded);
                    FlushStderrLines(lineBuffer);
                }
            }
            if (lineBuffer is { Length: > 0 })
            {
                if (decoder is not null && charBuf is not null)
                {
                    int tail = decoder.GetChars([], 0, 0, charBuf, 0, flush: true);
                    if (tail > 0) lineBuffer.Append(charBuf, 0, tail);
                }
                DebugLog.Warn(DebugCategories.RuntimeNode, "stderr", "process.stderr",
                    new { text = lineBuffer.ToString().TrimEnd('\r', '\n') });
                lineBuffer.Clear();
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            if (file is not null)
            {
                try { await file.FlushAsync().ConfigureAwait(false); } catch { }
                file.Dispose();
            }
        }
    }

    private static void FlushStderrLines(StringBuilder buffer)
    {
        const int MaxBufferedLineChars = 64 * 1024;
        while (true)
        {
            int nl = -1;
            for (int i = 0; i < buffer.Length; i++)
                if (buffer[i] == '\n') { nl = i; break; }
            if (nl < 0)
            {
                if (buffer.Length >= MaxBufferedLineChars)
                {
                    DebugLog.Warn(DebugCategories.RuntimeNode, "stderr", "process.stderr", new { text = buffer.ToString() });
                    buffer.Clear();
                }
                return;
            }
            var line = buffer.ToString(0, nl).TrimEnd('\r');
            buffer.Remove(0, nl + 1);
            if (line.Length > 0)
                DebugLog.Warn(DebugCategories.RuntimeNode, "stderr", "process.stderr", new { text = line });
        }
    }

    private static async Task StopProcessAsync(Process child, OwnedProcessJob? job)
    {
        // The Job is the lifetime boundary. Do not wait for an HTTP close ACK or
        // a grace timer before stopping arbitrary third-party script execution.
        if (job is not null) job.Dispose();
        else
        {
            // Startup can fail before the child is attached to its Job.
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
        await child.WaitForExitAsync().ConfigureAwait(false);
    }

    /// <summary>Stops the owned executor and waits only for its actual release.</summary>
    public ValueTask DisposeAsync() => new(StopAsync());

    private Task StopAsync()
    {
        lock (gate)
        {
            if (disposal is not null) return disposal;
            disposal = completion.Task;
        }
        _ = StopCoreAsync();
        return disposal;
    }

    private async Task StopCoreAsync()
    {
        Exception? failure = null;
        async Task Attempt(Func<Task> action)
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); }
        }
        try
        {
            // Start every interrupt before waiting for callbacks or request settlement.
            var cancellation = Attempt(capability.CancelAsync);
            var requests = Attempt(() => client.DisposeAsync().AsTask());
            try { pipe.Dispose(); } catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); }
            await StopProcessAsync(process, job).ConfigureAwait(false);
            await Task.WhenAll(cancellation, requests,
                Attempt(() => processExit), Attempt(async () => { await pipeEnd.ConfigureAwait(false); }),
                Attempt(() => outputDrain), Attempt(() => errorDrain)).ConfigureAwait(false);
            process.Dispose();
            if (failure is null) completion.TrySetResult();
            else completion.TrySetException(new BridgeException("INTERNAL"));
        }
        catch { completion.TrySetException(new BridgeException("INTERNAL")); }
        finally
        {
            leaseRegistration?.Dispose();
            if (DebugLog.IsEnabled)
            {
                DebugLog.Info(DebugCategories.RuntimeBridge, "node.stop", "process.stopped",
                    new { pid = processId, hadFailure = failure is not null });
            }
            capability.Dispose();
        }
    }
}
