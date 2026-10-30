// SPDX-License-Identifier: GPL-3.0-only
namespace MeowFilm.Capabilities.Platforms;

internal sealed partial class MusicPlatformClientContextOwner
{
    private Task<MusicKgClientContext>? kgRegistration;
    private Task<MusicQqClientContext>? qqRegistration;

    // Shared client work is independent of account authorization. A cancelled
    // waiter cannot cancel another account operation's anonymous registration.
    internal Task<MusicKgClientContext> EnsureKgAsync(MusicPlatformClientProtocol protocol, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Task<MusicKgClientContext> task;
        lock (gate)
        {
            RequireOpen();
            if (kgRegistration is null || kgRegistration.IsCompleted)
            { kgRegistration = RegisterAsync(lifetime.Token); Track(kgRegistration); }
            task = kgRegistration;
        }
        return task.WaitAsync(token);
        async Task<MusicKgClientContext> RegisterAsync(CancellationToken generation)
        {
            await Task.Yield();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25), protocol.Clock);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(generation, deadline.Token);
            try
            {
                var device = await AcquireKgAsync(operation.Token).ConfigureAwait(false);
                if (device.Dfid is not null) return device;
                var dfid = await protocol.RegisterKgAsync(device, operation.Token).ConfigureAwait(false);
                return dfid is null ? device : await SaveKgAsync(device, dfid, operation.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (deadline.IsCancellationRequested && !generation.IsCancellationRequested &&
                error is OperationCanceledException or MusicPlatformException { Code: "CANCELLED" or "TIMEOUT" })
            { throw new MusicPlatformException("TIMEOUT"); }
        }
    }
    internal Task<MusicQqClientContext> EnsureQqAsync(MusicPlatformClientProtocol protocol, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Task<MusicQqClientContext> task;
        lock (gate)
        {
            RequireOpen();
            if (qqRegistration is null || qqRegistration.IsCompleted)
            { qqRegistration = RegisterAsync(lifetime.Token); Track(qqRegistration); }
            task = qqRegistration;
        }
        return task.WaitAsync(token);
        async Task<MusicQqClientContext> RegisterAsync(CancellationToken generation)
        {
            await Task.Yield();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25), protocol.Clock);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(generation, deadline.Token);
            try
            {
                var device = await AcquireQqAsync(operation.Token).ConfigureAwait(false);
                if (!device.QimeiCurrent(protocol.Clock.GetUtcNow()))
                {
                    var qimei = await protocol.RequestQqQimeiAsync(device, operation.Token).ConfigureAwait(false);
                    device = await SaveQqAsync(device, qimei, device.Session, operation.Token).ConfigureAwait(false);
                }
                if (!device.SessionCurrent(protocol.Clock.GetUtcNow()))
                {
                    var session = await protocol.RequestQqDeviceSessionAsync(device, operation.Token).ConfigureAwait(false);
                    device = await SaveQqAsync(device, device.Qimei, session, operation.Token).ConfigureAwait(false);
                }
                MusicClientContextDocument.Require(IsCurrent(device), "STORAGE_CONFLICT");
                return device;
            }
            catch (Exception error) when (deadline.IsCancellationRequested && !generation.IsCancellationRequested &&
                error is OperationCanceledException or MusicPlatformException { Code: "CANCELLED" or "TIMEOUT" })
            { throw new MusicPlatformException("TIMEOUT"); }
        }
    }
}
