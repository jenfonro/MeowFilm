using System.Text.Json;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Search;

namespace MeowFilm.Capabilities;

/// <summary>Current native discovery consumer port. No script, credential or document access.</summary>
public interface IMusicDiscoverySource
{
    event Action? DiscoveryInvalidated;
    Task<MusicSearchDiscoverySnapshot> ReadDiscoveryAsync(bool refresh, CancellationToken token = default);
    Task<MusicSearchDiscoverySnapshot> RotateDiscoveryAsync(long generation, CancellationToken token = default);
    bool IsDiscoveryCurrent(long generation);
    Task CancelDiscoveryAndDrainAsync();
}

public sealed partial class MusicNativeApplication
{
    private readonly IMusicSearchDiscovery discovery;
    private static readonly JsonSerializerOptions DiscoveryJson = new(JsonSerializerDefaults.Web);
    private long discoveryAccountEpoch, issuedDiscoveryGeneration, discoveryReadSerial;
    private int discoveryInvalidations;
    public event Action? DiscoveryInvalidated;

    private void DiscoveryAccountChanged(string platform)
    {
        if (platform != "wy") return;
        lock (gate) { discoveryInvalidations++; discoveryAccountEpoch++; issuedDiscoveryGeneration = 0; }
        try { discovery.Invalidate(platform); }
        finally
        {
            // The service can synchronously cancel arbitrary pending native work.
            // Do not hold app gate across it, but fence both ends so a concurrent
            // anonymous-cache read cannot reissue the old generation in the gap.
            lock (gate) { discoveryAccountEpoch++; issuedDiscoveryGeneration = 0; discoveryInvalidations--; }
        }
        foreach (var observer in DiscoveryInvalidated?.GetInvocationList() ?? [])
        {
            try { ((Action)observer)(); }
            catch { /* A display listener cannot prevent account invalidation. */ }
        }
    }

    public bool IsDiscoveryCurrent(long generation)
    {
        lock (gate) return !closed && !stopping && !accountReceiptUnknown && discoveryInvalidations == 0 &&
            generation > 0 && generation == issuedDiscoveryGeneration;
    }
    public Task<MusicSearchDiscoverySnapshot> ReadDiscoveryAsync(bool refresh, CancellationToken token = default) =>
        DiscoveryCall("music.discovery.read", new { refresh }, token);
    public Task<MusicSearchDiscoverySnapshot> RotateDiscoveryAsync(long generation, CancellationToken token = default) =>
        DiscoveryCall("music.discovery.rotate", new { generation }, token);
    public Task CancelDiscoveryAndDrainAsync() => discovery.CancelAndDrainAsync();

    private async Task<MusicSearchDiscoverySnapshot> DiscoveryCall(string method, object input, CancellationToken token)
    {
        // Same bounded native admission, lifetime and pending-operation drain as all app commands.
        var reply = await ExecuteAsync(method, input, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!Okay(reply)) throw new MusicOperationException(reply.GetProperty("code").GetString() ?? "INVALID_RESPONSE");
        return reply.GetProperty("snapshot").Deserialize<MusicSearchDiscoverySnapshot>(DiscoveryJson) ??
            throw new MusicOperationException("INVALID_RESPONSE");
    }
    private async Task<JsonElement> ExecuteDiscoveryAsync(string method, object? parameters, CancellationToken token)
    {
        var input = parameters is JsonElement element ? element : JsonSerializer.SerializeToElement(parameters);
        if (input.ValueKind != JsonValueKind.Object) return Failed("INVALID_REQUEST");
        var fields = input.EnumerateObject().ToArray();
        if (fields.Length != 1) return Failed("INVALID_REQUEST");
        bool refresh = false;
        long generation = 0;
        if (method == "music.discovery.read")
        {
            if (fields[0].Name != "refresh" || fields[0].Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return Failed("INVALID_REQUEST");
            refresh = fields[0].Value.GetBoolean();
            // Initial durable-load notifications must run before a new discovery flight is admitted.
            var restored = await accounts.RestoreForDiscoveryAsync(token).ConfigureAwait(false);
            if (!Okay(restored)) return restored;
        }
        else if (method == "music.discovery.rotate")
        {
            if (fields[0].Name != "generation" || fields[0].Value.ValueKind != JsonValueKind.Number ||
                !fields[0].Value.TryGetInt64(out generation) || generation < 1) return Failed("INVALID_REQUEST");
        }
        else return Failed("INVALID_REQUEST");
        long account, serial, previousIssued;
        lock (gate)
        {
            if (accountReceiptUnknown) return Failed("COMMIT_UNKNOWN");
            if (discoveryInvalidations > 0) return Failed("STALE_SESSION");
            if (method == "music.discovery.rotate" && generation != issuedDiscoveryGeneration) return Failed("STALE_SESSION");
            previousIssued = issuedDiscoveryGeneration;
            serial = ++discoveryReadSerial;
            if (refresh) issuedDiscoveryGeneration = 0;
            account = discoveryAccountEpoch;
        }
        try
        {
            var snapshot = method == "music.discovery.read"
                ? await discovery.ReadAsync(refresh, token).ConfigureAwait(false)
                : discovery.Rotate(generation);
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (closed || stopping) return Failed("CLOSED");
                if (accountReceiptUnknown) return Failed("COMMIT_UNKNOWN");
                if (discoveryInvalidations > 0 || account != discoveryAccountEpoch || serial != discoveryReadSerial) return Failed("STALE_SESSION");
                issuedDiscoveryGeneration = snapshot.Generation;
            }
            return JsonSerializer.SerializeToElement(new { ok = true, snapshot }, DiscoveryJson);
        }
        catch (MusicPlatformException error) { RestoreFailedRefresh(); return Failed(error.Code); }
        catch (OperationCanceledException) { RestoreFailedRefresh(); throw; }
        void RestoreFailedRefresh()
        {
            // The service retains the previous completed cache after a failed
            // refresh. Preserve it only while the same native account and latest
            // request still own it, never across logout or a later refresh.
            lock (gate)
                if (refresh && !closed && !stopping && !accountReceiptUnknown && discoveryInvalidations == 0 &&
                    account == discoveryAccountEpoch && serial == discoveryReadSerial)
                    issuedDiscoveryGeneration = previousIssued;
        }
    }

    private sealed class DiscoverySessions(Accounts.MusicAccountOwner owner) : IMusicPlatformSessionSource
    {
        // The ordinary account API permits abandoning a read wait while its
        // owner retains physical work. Discovery's page-level drain is stricter.
        public ValueTask<MusicPlatformSession?> AcquireAsync(string platform, CancellationToken cancellationToken) =>
            owner.AcquireSettledAsync(platform, cancellationToken);
        public bool IsCurrent(MusicPlatformSession session) => owner.IsCurrent(session);
    }
}
