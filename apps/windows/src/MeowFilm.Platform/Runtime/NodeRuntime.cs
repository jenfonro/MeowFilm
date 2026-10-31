using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using MeowFilm.Core.Runtime;
using Microsoft.Win32.SafeHandles;

namespace MeowFilm.Platform.Runtime;

/// <summary>Owns exactly one child and one private control connection for this App runtime identity.</summary>
public sealed partial class NodeRuntime : IRuntimeSession
{
    private readonly Process process;
    private readonly BridgePeer peer;
    private readonly HashSet<string> allowedMethods;
    private readonly Task outputDrain, errorDrain;
    private readonly OwnedProcessJob job;
    private int disposed;
    private NodeRuntime(Process process, BridgePeer peer, HashSet<string> methods, Task output, Task error, OwnedProcessJob job)
        => (this.process, this.peer, allowedMethods, outputDrain, errorDrain, this.job) = (process, peer, methods, output, error, job);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    public int ProcessId => process.Id;
    public bool IsConnected => !peer.IsClosed && Volatile.Read(ref disposed) == 0;
    public Task Completion => peer.Completion;

    public static async Task<NodeRuntime> StartAsync(string nodeExecutable, RuntimeBundle bundle,
        IReadOnlyList<RuntimeModule> expectedModules,
        IReadOnlyDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> platformMethods,
        string scriptDataRoot,
        CancellationToken cancellationToken = default)
    {
        var node = Path.GetFullPath(nodeExecutable);
        RuntimeBundle.EnsureOrdinaryAncestors(node);
        if (!File.Exists(node) || !Path.IsPathFullyQualified(nodeExecutable)) throw new BridgeException("INVALID_MESSAGE");
        string dataRoot;
        try
        {
            if (!Path.IsPathFullyQualified(scriptDataRoot)
                || scriptDataRoot.StartsWith(@"\\", StringComparison.Ordinal)
                || scriptDataRoot.StartsWith("//", StringComparison.Ordinal)
                || scriptDataRoot.Any(char.IsControl)) throw new BridgeException("INVALID_MESSAGE");
            dataRoot = Path.GetFullPath(scriptDataRoot);
        }
        catch (BridgeException) { throw; }
        catch { throw new BridgeException("INVALID_MESSAGE"); }
        var session = Guid.NewGuid().ToString("D");
        var pipeName = "meowfilm-" + Guid.NewGuid().ToString("D");
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 64 * 1024, 64 * 1024);
        Process? child = null;
        BridgePeer? peer = null;
        OwnedProcessJob? job = null;
        Task output = Task.CompletedTask, error = Task.CompletedTask;
        try
        {
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
            start.Environment["MEOWFILM_SCRIPT_DATA_ROOT"] = dataRoot;
            foreach (var argument in new[] { bundle.EntryPath, "--meow-pipe", pipeName, "--meow-session", session })
                start.ArgumentList.Add(argument);
            child = Process.Start(start) ?? throw new BridgeException("CLOSED");
            job = OwnedProcessJob.Attach(child);
            child.StandardInput.Close();
            output = DrainAsync(child.StandardOutput.BaseStream);
            error = DrainAsync(child.StandardError.BaseStream);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var connect = server.WaitForConnectionAsync(deadline.Token);
            var exited = child.WaitForExitAsync(deadline.Token);
            if (await Task.WhenAny(connect, exited).ConfigureAwait(false) != connect) throw new BridgeException("CLOSED");
            await connect.ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var pid) || pid != (uint)child.Id)
                throw new BridgeException("INVALID_MESSAGE");
            peer = new BridgePeer(server, session, platformMethods);
            var description = await peer.CallAsync("runtime.describe", null, deadline.Token).ConfigureAwait(false);
            var methods = ValidateDescription(description, bundle.Product, expectedModules);
            return new NodeRuntime(child, peer, methods, output, error, job);
        }
        catch (Exception original)
        {
            if (peer is not null) await peer.DisposeAsync().ConfigureAwait(false);
            server.Dispose();
            job?.Dispose();
            if (child is not null) { await StopProcessAsync(child).ConfigureAwait(false); child.Dispose(); }
            await Task.WhenAll(output, error).ConfigureAwait(false);
            throw original is BridgeException ? original : new BridgeException(
                cancellationToken.IsCancellationRequested ? "CANCELLED" : original is OperationCanceledException ? "TIMEOUT" : "CLOSED");
        }
    }

    public static HashSet<string> ValidateDescription(JsonElement description, string product, IReadOnlyList<RuntimeModule> modules)
    {
        try
        {
            if (description.GetProperty("protocol").GetInt32() != 1
                || description.GetProperty("product").GetString() != product) throw new BridgeException("INVALID_MESSAGE");
            var remaining = modules.ToDictionary(item => item.Id, item => item.Version, StringComparer.Ordinal);
            var methods = new HashSet<string>(StringComparer.Ordinal);
            foreach (var module in description.GetProperty("modules").EnumerateArray())
            {
                var id = module.GetProperty("id").GetString()!;
                if (!remaining.Remove(id, out var version) || module.GetProperty("version").GetString() != version)
                    throw new BridgeException("INVALID_MESSAGE");
                foreach (var declared in module.GetProperty("methods").EnumerateArray())
                {
                    var method = declared.GetString()!;
                    if (!BridgeCodec.ValidMethod(method) || !method.StartsWith(id + ".", StringComparison.Ordinal)
                        || !methods.Add(method)) throw new BridgeException("INVALID_MESSAGE");
                }
            }
            if (remaining.Count != 0) throw new BridgeException("INVALID_MESSAGE");
            return methods;
        }
        catch (BridgeException) { throw; }
        catch { throw new BridgeException("INVALID_MESSAGE"); }
    }

    public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        if (!allowedMethods.Contains(method)) return Task.FromException<JsonElement>(new BridgeException("METHOD_UNAVAILABLE"));
        return peer.CallAsync(method, parameters, cancellationToken);
    }

    private static async Task DrainAsync(Stream stream)
    {
        // No business payload, URL, credential or provider stack is persisted as diagnostics.
        var bytes = new byte[4096];
        try { while (await stream.ReadAsync(bytes).ConfigureAwait(false) != 0) { } }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task StopProcessAsync(Process child)
    {
        if (child.HasExited) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await child.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { child.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await child.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            await peer.DisposeAsync().ConfigureAwait(false);
            await StopProcessAsync(process).ConfigureAwait(false);
        }
        finally
        {
            job.Dispose();
            try { await Task.WhenAll(outputDrain, errorDrain).ConfigureAwait(false); }
            finally { process.Dispose(); }
        }
    }
}
