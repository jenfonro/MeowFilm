namespace MeowFilm.Core.Runtime;

public enum RuntimeConnectionState { Dormant, Starting, Ready, Failed, Recovering, Closed }

/// <summary>
/// Product-local lifecycle contract. Recovery never replays business operations.
/// Registration is frozen at the first script request, not a global event bus.
/// </summary>
public interface IRuntimeRecovery
{
    long Generation { get; }
    RuntimeConnectionState State { get; }
    bool CanRetry { get; }
    void RegisterRecoveryScope(IRuntimeRecoveryScope scope);
    Task RetryAsync(CancellationToken cancellationToken = default);
}

public interface IRuntimeRecoveryScope
{
    /// <summary>
    /// Called after the old transport retires. Deny new script operations, drain
    /// accepted native callbacks and invalidate temporary grants/handles.
    /// Do not call a dead transport or replay an uncertain storage mutation.
    /// A cancelled wait is not proof that accepted work has drained.
    /// </summary>
    Task PrepareRuntimeRecoveryAsync(CancellationToken cancellationToken);

    /// <summary>Reopen admission after a fresh handshake, without loading scripts or replaying work.</summary>
    void ResumeAfterRuntimeRecovery();
}
