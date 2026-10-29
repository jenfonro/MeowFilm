namespace MeowFilm.Media;

/// <summary>
/// A native-authorized, revocable HTTP resource, not an arbitrary local URL or an offline file.
/// Address must use literal 127.0.0.1 and the actual registered service port. Authorizes is
/// synchronous, thread-safe, bounded and must validate each root, redirect and adaptive child
/// against the same live capability. Public children additionally obey native public-address
/// policy and never receive the root headers across origins. Never persist a capability.
/// OpenResourceAsync takes exclusive ownership immediately, including rejected calls.
/// Revoked must cancel on loss of source/session/permission even while playback is paused.
/// DisposeAsync revokes and waits for the owned resource's actual release, without UI dispatch.
/// </summary>
public interface INativeMediaResourceLease : IAsyncDisposable
{
    Uri Address { get; }
    IReadOnlyDictionary<string, string> Headers { get; }
    CancellationToken Revoked { get; }
    bool Authorizes(Uri address);
}
