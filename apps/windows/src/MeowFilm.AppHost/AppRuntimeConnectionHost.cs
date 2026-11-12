using System.Runtime.CompilerServices;
using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.AppHost;

/// <summary>
/// Composition adapter for the existing AppRuntimeClient. Captures the actual
/// factory result and its owner token, never infers an epoch from Generation.
/// </summary>
internal sealed class AppRuntimeConnectionHost : IRuntimeClient, IRuntimeImageClient, IRuntimeRecovery, IRuntimeConnectionSource, IAsyncDisposable
{
    private sealed class Capture(CancellationToken owner)
    {
        internal readonly CancellationToken Owner = owner;
        internal readonly TaskCompletionSource<RuntimeConnection> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly AppRuntimeClient runtime;
    private readonly object gate = new();
    private Capture? current;

    internal AppRuntimeConnectionHost(Func<CancellationToken, Task<IRuntimeSession>> factory)
    {
        runtime = new AppRuntimeClient(async owner =>
        {
            var capture = new Capture(owner);
            lock (gate) current = capture;
            try
            {
                var session = await factory(owner).ConfigureAwait(false);
                try
                {
                    if (session is not IRuntimeConnectionSource source) throw new BridgeException("INTERNAL");
                    var connection = await source.CaptureConnectionAsync(owner).ConfigureAwait(false);
                    owner.ThrowIfCancellationRequested();
                    connection.Revoked.ThrowIfCancellationRequested();
                    if (!connection.Revoked.CanBeCanceled) throw new BridgeException("INVALID_RESPONSE");
                    capture.Ready.TrySetResult(connection);
                    return session;
                }
                catch
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            catch (Exception error)
            {
                capture.Ready.TrySetException(error is BridgeException bridge ? new BridgeException(bridge.Code) :
                    new BridgeException(owner.IsCancellationRequested ? "CLOSED" : "INTERNAL"));
                _ = capture.Ready.Task.Exception;
                throw;
            }
        });
    }

    internal void Start() => runtime.Start();
    public long Generation => runtime.Generation;
    public RuntimeConnectionState State => runtime.State;
    public bool CanRetry => runtime.CanRetry;
    public void RegisterRecoveryScope(IRuntimeRecoveryScope scope) => runtime.RegisterRecoveryScope(scope);
    public Task RetryAsync(CancellationToken cancellationToken = default) => runtime.RetryAsync(cancellationToken);

    public async Task<RuntimeConnection> CaptureConnectionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State is not (RuntimeConnectionState.Starting or RuntimeConnectionState.Ready)) throw new BridgeException("CLOSED");
        Capture selected;
        lock (gate)
        {
            if (current is null) throw new BridgeException("CLOSED");
            selected = current;
        }
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, selected.Owner);
        RuntimeConnection connection;
        try { connection = await selected.Ready.Task.WaitAsync(wait.Token).ConfigureAwait(false); }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selected.Owner.IsCancellationRequested) throw new BridgeException("CLOSED");
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (State is not (RuntimeConnectionState.Starting or RuntimeConnectionState.Ready)) throw new BridgeException("CLOSED");
        lock (gate)
        {
            if (!ReferenceEquals(current, selected) || selected.Owner.IsCancellationRequested ||
                connection.Revoked.IsCancellationRequested)
                throw new BridgeException("CLOSED");
            return connection;
        }
    }

    public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) =>
        runtime.CallAsync(method, parameters, cancellationToken);
    public Task<JsonElement> CallAsync(string method, object? parameters, TimeSpan timeout,
        CancellationToken cancellationToken = default) => runtime.CallAsync(method, parameters, timeout, cancellationToken);
    public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
        ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default) =>
        runtime.UploadSourceAsync(module, installationId, sourceRevision, source, basename, cancellationToken);
    public Task<byte[]> ReadImageAsync(string reference, int maximumBytes, CancellationToken cancellationToken = default) =>
        runtime.ReadImageAsync(reference, maximumBytes, cancellationToken);
    public Task<byte[]> ReadImageAsync(string reference, CancellationToken cancellationToken = default) =>
        runtime.ReadImageAsync(reference, cancellationToken);
    public async IAsyncEnumerable<JsonElement> EventsAsync(string module, Guid sessionId, long after = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var connection = await CaptureConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var observation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connection.Revoked);
        await foreach (var value in connection.Client.EventsAsync(module, sessionId, after, observation.Token).ConfigureAwait(false))
            yield return value;
    }
    public ValueTask DisposeAsync() => runtime.DisposeAsync();
}
