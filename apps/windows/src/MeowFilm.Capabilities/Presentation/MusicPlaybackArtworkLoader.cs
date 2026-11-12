using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private long presentationCacheEpoch;
    internal void AttachPlaybackArtwork(MusicRecordView record)
    {
        record.Loaded += (_, _) =>
        {
            if (ArtworkScope.GetLoader(record) is not { } loader || loader is PlaybackArtworkLoader) return;
            ArtworkScope.SetLoader(record, new PlaybackArtworkLoader(this, loader));
        };
    }

    private sealed class PlaybackArtworkLoader(MusicCapability owner, IArtworkLoader inner) : IArtworkLoader
    {
        private sealed class CachedLease(BitmapSource image) : IArtworkLease
        {
            public BitmapSource Image => image;
            public void Dispose() { }
        }
        public async Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default)
        {
            var song = owner.current;
            long epoch = owner.presentationCacheEpoch;
            if (song is null || address != owner.CurrentArtwork)
                return await inner.AcquireAsync(address, cancellationToken);
            var raw = JsonNode.Parse(song.Raw.GetRawText())!.AsObject();
            string key = MusicRecordingPresentationCache.Key(raw);
            var cache = new MusicRecordingPresentationCache(owner.cacheStore);
            try
            {
                var saved = await cache.ReadAsync(key, cancellationToken);
                if (saved?["artwork"] is JsonValue data && data.TryGetValue<string>(out var encoded) &&
                    encoded.Length <= (ArtworkService.MaximumBytes + 2) / 3 * 4)
                {
                    byte[] bytes = Convert.FromBase64String(encoded);
                    using var input = new MemoryStream(bytes, writable: false);
                    var image = new BitmapImage();
                    image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                    image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    image.StreamSource = input; image.DecodePixelWidth = 512; image.EndInit(); image.Freeze();
                    cancellationToken.ThrowIfCancellationRequested();
                    return new CachedLease(image);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* An unavailable cover does not discard this song's lyrics. */ }
            var lease = await inner.AcquireAsync(address, cancellationToken);
            try
            {
                if (owner.stopping || owner.presentationCacheEpoch != epoch || owner.current?.Id != song.Id) return lease;
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(lease.Image));
                using var output = new MemoryStream(); encoder.Save(output);
                if (output.Length <= ArtworkService.MaximumBytes)
                    await cache.PatchAsync(key, song.Id, value =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (owner.presentationCacheEpoch != epoch) throw new OperationCanceledException();
                        value["artwork"] = Convert.ToBase64String(output.GetBuffer(), 0, (int)output.Length);
                    }, cancellationToken);
                return lease;
            }
            catch (OperationCanceledException) { lease.Dispose(); throw; }
            catch { return lease; }
        }
    }
}
