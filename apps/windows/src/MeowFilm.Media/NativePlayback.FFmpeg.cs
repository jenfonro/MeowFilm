using System.Security.Cryptography;

namespace MeowFilm.Media;

public sealed partial class NativePlayback
{
    private sealed class RuntimeVideoMaterial : IDisposable
    {
        internal readonly NativeVideoFormat Format;
        internal readonly byte[] Pairs;
        internal readonly byte[]? DefaultKey;
        internal RuntimeVideoMaterial(NativeVideoInput input)
        {
            if (input is null) throw Error(PlaybackError.InvalidRequest);
            Format = input.Format;
            (Pairs, DefaultKey) = input.Take();
        }
        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Pairs);
            if (DefaultKey is not null) CryptographicOperations.ZeroMemory(DefaultKey);
        }
    }

    /// <summary>HTTP(S) input and private typed format/keys; no URL access policy.</summary>
    public Task PrepareRuntimeMediaAsync(RuntimeMediaPlaybackIntent intent, Uri address,
        NativeVideoInput input, CancellationToken token = default) =>
        PrepareRuntimeVideoOwnedAsync(intent, address, null, input, token);

    /// <summary>Immediately consumes both input and the exact capability, including validation/dispatcher rejection.</summary>
    public Task PrepareRuntimeMediaAsync(RuntimeMediaPlaybackIntent intent, INativeMediaResourceLease lease,
        NativeVideoInput input, CancellationToken token = default)
    {
        if (lease is null)
        {
            input?.Dispose();
            throw Error(PlaybackError.InvalidResourceLease);
        }
        return PrepareRuntimeVideoOwnedAsync(intent, null, lease, input, token);
    }

    private Task PrepareRuntimeVideoOwnedAsync(RuntimeMediaPlaybackIntent intent, Uri? address,
        INativeMediaResourceLease? lease, NativeVideoInput input, CancellationToken token) =>
        TrackOwnedMediaRequestAsync(() => PrepareRuntimeVideoCoreAsync(intent, address, lease, input, token));

    private async Task PrepareRuntimeVideoCoreAsync(RuntimeMediaPlaybackIntent intent, Uri? address,
        INativeMediaResourceLease? lease, NativeVideoInput input, CancellationToken token)
    {
        var resource = lease is not null ? new MediaResourceSession(lease) : null;
        RuntimeVideoMaterial? material = null;
        try
        {
            material = new(input);
            input.Dispose();
            if (resource is null && address is null) throw Error(PlaybackError.InvalidRequest);
            await DispatchAsync(async () =>
            {
                resource?.Prepare(token);
                await PrepareRuntimeCoreAsync(intent, resource?.Address ?? address!, resource,
                    resource?.Token ?? token, material);
            }).ConfigureAwait(false);
        }
        catch
        {
            if (resource is not null) await resource.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally { material?.Dispose(); input?.Dispose(); }
    }
}
