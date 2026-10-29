using MeowFilm.Core.Runtime;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.Scripting;

public sealed partial class MusicScriptOwner : IRuntimeRecoveryScope
{
    private bool recoveringRuntime;
    private Task? runtimeRetirement;

    public Task PrepareRuntimeRecoveryAsync(CancellationToken cancellationToken)
    {
        lock (activityGate)
        {
            if (closed) return Task.CompletedTask;
            recoveringRuntime = true;
            foreach (var operation in onlineOperations.ToArray()) operation.Cancel();
            if (runtimeRetirement is null || runtimeRetirement.IsFaulted || runtimeRetirement.IsCanceled)
                runtimeRetirement = RetireRuntimeAsync(drained.Task, cancellationToken);
            return runtimeRetirement;
        }
    }
    private async Task RetireRuntimeAsync(Task callbacks, CancellationToken token)
    {
        await Task.Yield();
        // Includes accepted reverse storage calls. A cancelled RPC is not
        // evidence that its admitted native CAS has returned.
        await callbacks.WaitAsync(token).ConfigureAwait(false);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await storageGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                foreach (var grant in grants.Values) grant.State = "revoked";
                grants.Clear(); live.Clear(); touched.Clear();
            }
            finally { storageGate.Release(); }
            // Read-only reconciliation; uncertain writes are never replayed.
            await library.ReconcileAsync(token).ConfigureAwait(false);
            metadata = null; state = null; blocked = false;
        }
        finally { gate.Release(); }
    }
    public void ResumeAfterRuntimeRecovery()
    {
        lock (activityGate)
        {
            if (closed) return;
            LJ.Require(runtimeRetirement?.IsCompletedSuccessfully == true, "RUNTIME_RECOVERY_INCOMPLETE");
            recoveringRuntime = false; runtimeRetirement = null;
        }
    }
}
