using System.IO;

namespace MeowFilm.Media;

/// <summary>Bounded complete progressive transfer using the same controlled HTTP transport as native playback.</summary>
public static class NativeHttpMediaTransfer
{
    public static async Task<long> CopyCompleteAsync(Uri uri, IReadOnlyDictionary<string, string> headers,
        Stream destination, long maximumBytes, NativeHttpMediaOptions? options = null,
        CancellationToken token = default, Action<long>? progress = null, Action<long?>? opened = null)
    {
        if (maximumBytes <= 0) throw new PlaybackException(PlaybackError.InvalidRequest);
        token.ThrowIfCancellationRequested();
        try
        {
            using var transport = new FFmpegHttpTransport(uri, headers, options ?? new(), token);
            return await transport.CopyCompleteAsync(uri, destination, maximumBytes, token, progress, opened)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            token.ThrowIfCancellationRequested();
            throw new PlaybackException(NativeHttpMediaPolicy.Classify(error));
        }
    }
}
