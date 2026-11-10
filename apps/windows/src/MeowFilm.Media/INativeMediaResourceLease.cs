namespace MeowFilm.Media;

/// <summary>
/// One owned media receipt, not an address permission. The caller supplies the HTTP(S)
/// input and headers; media transport does not admit URLs by origin or resource graph.
/// Revoked cancels on loss of the source/account/runtime owner, even while paused.
/// DisposeAsync revokes and waits for the exact resource's actual release without UI dispatch.
/// Open and prepare take exclusive ownership immediately, including rejected calls.
/// Never persist a short-lived resource address or its request headers.
/// </summary>
public interface INativeMediaResourceLease : IAsyncDisposable
{
    Uri Address { get; }
    IReadOnlyDictionary<string, string> Headers { get; }
    CancellationToken Revoked { get; }
}
