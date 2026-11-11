using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using MeowFilm.Core.Runtime;
using Microsoft.Win32.SafeHandles;

namespace MeowFilm.Platform.Runtime;

/// <summary>Owns one child, its Job, private lifetime pipe and HTTP client.</summary>
public sealed partial class NodeRuntime : IRuntimeSession, IRuntimeConnectionSource
{
    private const int HandshakeBytes = 64 * 1024;
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
    private Task? disposal;

    private NodeRuntime(Process process, NamedPipeServerStream pipe, RuntimeHttpClient client,
        Task output, Task error, Task processExit, OwnedProcessJob job, Uri businessOrigin, CancellationToken ownerRevoked)
    {
        this.process = process; processId = process.Id; this.pipe = pipe; this.client = client;
        outputDrain = output; errorDrain = error; this.processExit = processExit; this.job = job;
        this.businessOrigin = businessOrigin;
        capability = CancellationTokenSource.CreateLinkedTokenSource(ownerRevoked);
        revoked = capability.Token;
        pipeEnd = WatchPipeAsync(pipe);
        _ = MonitorAsync();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    public int ProcessId => processId;
    public bool IsConnected { get { lock (gate) return disposal is null && !revoked.IsCancellationRequested &&
        !processExit.IsCompleted && !pipeEnd.IsCompleted; } }
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
        CancellationToken cancellationToken = default, CancellationToken ownerRevoked = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string node, dataRoot;
        try
        {
            node = Path.GetFullPath(nodeExecutable);
            RuntimeBundle.EnsureOrdinaryAncestors(node);
            if (!File.Exists(node) || !Path.IsPathFullyQualified(nodeExecutable)
                || !Path.IsPathFullyQualified(scriptDataRoot)
                || scriptDataRoot.StartsWith(@"\\", StringComparison.Ordinal)
                || scriptDataRoot.StartsWith("//", StringComparison.Ordinal)
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
        try
        {
            server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, HandshakeBytes, HandshakeBytes);
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
            output = DrainAsync(child.StandardOutput.BaseStream);
            error = DrainAsync(child.StandardError.BaseStream);
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
            var config = BridgeCodec.EncodeJSONValue(new { sessionId, dataRoot });
            if (config.Length + 1 > HandshakeBytes) throw new BridgeException("INVALID_MESSAGE");
            await server.WriteAsync(config, deadline.Token).ConfigureAwait(false);
            await server.WriteAsync("\n"u8.ToArray(), deadline.Token).ConfigureAwait(false);
            await server.FlushAsync(deadline.Token).ConfigureAwait(false);
            var ready = await ReadReadyAsync(server, deadline.Token).ConfigureAwait(false);
            client = new RuntimeHttpClient(ready);
            runtime = new NodeRuntime(child, server, client, output, error, exited, job,
                RuntimeHttpProtocol.ValidateEndpoint(ready), ownerRevoked);
            var description = await client.DescribeAsync(deadline.Token).ConfigureAwait(false);
            if (!description.GetProperty("ok").GetBoolean())
                throw new BridgeException(description.GetProperty("code").GetString()!);
            _ = ValidateDescription(description.GetProperty("value"), bundle.Product, expectedModules);
            deadline.Token.ThrowIfCancellationRequested();
            if (!runtime.IsConnected) throw new BridgeException("CLOSED");
            return runtime;
        }
        catch (Exception original)
        {
            var failedCleanup = false;
            var failure = original is BridgeException known ? known.Code :
                original is OperationCanceledException ? "TIMEOUT" : "CLOSED";
            if (runtime is not null)
            {
                try { await runtime.StopAsync(false).ConfigureAwait(false); }
                catch (BridgeException stopped) when (runtime.Completion.IsCompletedSuccessfully) { failure = stopped.Code; }
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
            throw new BridgeException(failedCleanup ? "INTERNAL" : cancellationToken.IsCancellationRequested ? "CANCELLED" : failure);
        }
    }

    private static async Task<string> ReadReadyAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new BridgeException("CLOSED");
            if (line.Length + count > HandshakeBytes) throw new BridgeException("INVALID_RESPONSE");
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, count);
            if (newline >= 0 && newline != count - 1) throw new BridgeException("INVALID_RESPONSE");
            line.Write(buffer, 0, newline < 0 ? count : newline);
            if (newline < 0) continue;
            try
            {
                var ready = BridgeCodec.DecodeJSONValue(line.ToArray());
                if (ready.ValueKind != JsonValueKind.Object || ready.EnumerateObject().Count() != 2
                    || ready.GetProperty("protocol").GetString() != RuntimeHttpProtocol.Name)
                    throw new BridgeException("INVALID_RESPONSE");
                var baseUrl = ready.GetProperty("baseUrl").GetString()!;
                _ = RuntimeHttpProtocol.ValidateEndpoint(baseUrl);
                return baseUrl;
            }
            catch { throw new BridgeException("INVALID_RESPONSE"); }
        }
    }

    public static HashSet<string> ValidateDescription(JsonElement description, string product,
        IReadOnlyList<RuntimeModule> modules) => RuntimeHttpProtocol.ValidateDescription(description, product, modules);

    public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        if (!IsConnected) return Task.FromException<JsonElement>(new BridgeException("CLOSED"));
        return client.CallAsync(method, parameters, cancellationToken);
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

    public Task<byte[]> ReadImageAsync(string reference, int maximumBytes, CancellationToken cancellationToken = default)
    {
        if (!IsConnected) return Task.FromException<byte[]>(new BridgeException("CLOSED"));
        return client.ReadImageAsync(reference, maximumBytes, cancellationToken);
    }

    public Task<byte[]> ReadImageAsync(string reference, CancellationToken cancellationToken = default)
    {
        if (!IsConnected) return Task.FromException<byte[]>(new BridgeException("CLOSED"));
        return client.ReadImageAsync(reference, cancellationToken);
    }

    private static async Task<string> WatchPipeAsync(Stream stream)
    {
        try { return await stream.ReadAsync(new byte[1]).ConfigureAwait(false) == 0 ? "CLOSED" : "INVALID_RESPONSE"; }
        catch { return "CLOSED"; }
    }

    private async Task MonitorAsync()
    {
        var first = await Task.WhenAny(pipeEnd, processExit).ConfigureAwait(false);
        // An exited child or pipe EOF can retire successfully for explicit recovery.
        // That shared retirement task proves release, not a /v1/close receipt.
        try { await StopAsync(false, first == pipeEnd && await pipeEnd.ConfigureAwait(false) == "INVALID_RESPONSE"
            ? "INVALID_RESPONSE" : null).ConfigureAwait(false); }
        catch { /* DisposeAsync retains the sanitized failure; ownership still converges. */ }
    }

    private static async Task DrainAsync(Stream stream)
    {
        // Discard child diagnostics: no payload, provider stack or credential is persisted.
        var bytes = new byte[4096];
        try { while (await stream.ReadAsync(bytes).ConfigureAwait(false) != 0) { } }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task<bool> StopProcessAsync(Process child, OwnedProcessJob? job)
    {
        var exited = child.WaitForExitAsync();
        try { await exited.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); return true; }
        catch (TimeoutException)
        {
            // The grace period is not proof of release: the Job and exact child
            // are terminated, then actual process exit is still awaited.
            job?.Dispose();
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            finally { await exited.ConfigureAwait(false); }
            return false;
        }
    }

    /// <summary>Releases ownership. A live session first requires a validated close ACK;
    /// an already-retired session returns its existing release outcome.</summary>
    public ValueTask DisposeAsync() => new(StopAsync(true));

    private Task StopAsync(bool graceful, string? failure = null)
    {
        TaskCompletionSource stopped;
        lock (gate)
        {
            if (disposal is not null) return disposal;
            stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = stopped.Task;
        }
        _ = StopCoreAsync(graceful, failure, stopped);
        return stopped.Task;
    }

    private async Task StopCoreAsync(bool graceful, string? failure, TaskCompletionSource stopped)
    {
        try
        {
            try { await capability.CancelAsync().ConfigureAwait(false); } catch { failure ??= "INTERNAL"; }
            if (graceful)
            {
                try { await client.ShutdownAsync().ConfigureAwait(false); }
                catch (Exception error) { failure ??= error is BridgeException known ? known.Code : "INTERNAL"; }
            }
            try { await client.DisposeAsync().ConfigureAwait(false); } catch { failure ??= "INTERNAL"; }
            try { pipe.Dispose(); } catch { failure ??= "INTERNAL"; }
            try
            {
                var naturalExit = await StopProcessAsync(process, job).ConfigureAwait(false);
                if (graceful && !naturalExit) failure ??= "CLOSED";
            }
            catch { failure ??= "INTERNAL"; }
            finally { job.Dispose(); }
            // Neither a cancelled HTTP await nor Job disposal proves exit.
            await processExit.ConfigureAwait(false);
            if (await pipeEnd.ConfigureAwait(false) == "INVALID_RESPONSE") failure ??= "INVALID_RESPONSE";
            await Task.WhenAll(outputDrain, errorDrain).ConfigureAwait(false);
            process.Dispose();
            completion.TrySetResult();
        }
        catch
        {
            failure ??= "INTERNAL";
            completion.TrySetException(new BridgeException(failure));
        }
        finally
        {
            capability.Dispose();
            if (failure is null) stopped.TrySetResult();
            else stopped.TrySetException(new BridgeException(failure));
        }
    }
}
