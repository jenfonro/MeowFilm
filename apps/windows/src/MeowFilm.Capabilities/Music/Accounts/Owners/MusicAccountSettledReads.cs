// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Accounts;

public sealed partial class MusicAccountOwner
{
    private InitialObservationFlight? initialObservation;
    private readonly HashSet<InitialObservationFlight> initialObservationFlights = [];
    private JsonElement initialObservationReceipt;
    private bool initialObservationSucceeded;
    private sealed class InitialObservationFlight : IDisposable
    {
        internal CancellationTokenSource Cancellation { get; } = new();
        internal Task<JsonElement>? Work { get; set; }
        internal void Cancel()
        {
            try { Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (AggregateException) { }
        }
        public void Dispose() => Cancellation.Dispose();
    }

    // Search, recommendations and other native consumers may need the account
    // owner's first durable observation before registering their own sessions.
    // That first observation is owner state: concurrent callers share it. A
    // cancelled waiter stops waiting; the owner retains and drains the physical read.
    internal async Task<JsonElement> EnsureInitialObservationAsync(
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Failure("CANCELLED");
        InitialObservationFlight flight;
        lock (gate)
        {
            if (closed || readsStopping) return Failure("CLOSED");
            if (initialObservationSucceeded && states.Values.Any(state => state.Uncertain))
            {
                // A lost account CAS acknowledgement requires a new explicit
                // durable observation before consumers may reuse hydration.
                initialObservationSucceeded = false;
                initialObservationReceipt = default;
            }
            if (initialObservationSucceeded)
                return initialObservationReceipt.Clone();
            else
            {
                if (initialObservation is not { } current)
                {
                    flight = initialObservation = new();
                    initialObservationFlights.Add(flight);
                    flight.Work = ObserveInitialAsync(flight);
                    _ = flight.Work.ContinueWith(_ =>
                    {
                        lock (gate) initialObservationFlights.Remove(flight);
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                else flight = current;
            }
        }
        try { return await flight.Work!.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Failure("CANCELLED"); }
    }

    private async Task<JsonElement> ObserveInitialAsync(InitialObservationFlight flight)
    {
        await Task.Yield(); // Publish the shared task before entering storage or notifications.
        try
        {
            return await RestoreForDiscoveryAsync(flight.Cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(initialObservation, flight))
                    initialObservation = null;
                if (closed)
                {
                    initialObservationSucceeded = false;
                    initialObservationReceipt = default;
                }
            }
            flight.Dispose();
        }
    }

    private void RememberInitialObservation(JsonElement result)
    {
        if (!result.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            return;
        lock (gate)
        {
            if (closed) return;
            initialObservationSucceeded = true;
            initialObservationReceipt = result.Clone();
        }
    }

    internal async Task DrainInitialObservationAsync()
    {
        Task[] pending;
        lock (gate) pending = initialObservationFlights.Select(f => (Task)f.Work!).ToArray();
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch { /* The observation owns its result; this is only a physical barrier. */ }
    }

    // Native discovery hydration, not another JSON/Node command or credential owner.
    // Same list/reconciliation rules; only this physical read
    // and its synchronous notifications are awaited. No global barrier or replay.
    internal async Task<JsonElement> RestoreForDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        bool entered = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Enter(); entered = true;
            var output = await List(cancellationToken, awaitPhysical: true).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            output["ok"] = true;
            var result = JsonSerializer.SerializeToElement(output);
            RememberInitialObservation(result);
            return result;
        }
        catch (Exception error)
        {
            return Failure(cancellationToken.IsCancellationRequested ? "CANCELLED" : AccountRules.Error(error));
        }
        finally { if (entered) Leave(); }
    }

    // Only the discovery-specific session adapter opts into physical settlement.
    internal async ValueTask<MusicPlatformSession?> AcquireSettledAsync(string platform,
        CancellationToken cancellationToken = default)
    {
        try { return await AcquireCoreAsync(platform, cancellationToken, awaitPhysical: true).ConfigureAwait(false); }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // DiskAsync intentionally normalizes internal storage cancellation to an
            // account error. Once it has settled, preserve token cancellation for
            // this typed native port without exposing the original storage error.
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
