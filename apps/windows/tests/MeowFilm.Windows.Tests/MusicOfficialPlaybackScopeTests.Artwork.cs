using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Windows.Tests;

public static partial class MusicOfficialPlaybackScopeTests
{
    private static IEnumerable<TestCase> ArtworkCases()
    {
        yield return Case("both players reuse listed artwork through discovery source and quality changes", async () =>
        {
            await using var f = await Scene.Create();
            const string original = "https://art.example.test/kg.png", discovered = "https://art.example.test/tx.png";
            var http = new ArtworkHttpProbe();
            await using var images = new ArtworkService(http);
            using var listed = await images.AcquireAsync(original);
            using var players = new ArtworkPlayers(f.Module, images);
            f.App.DiscoveryArtwork = discovered;
            f.App.Failures["kg"] = "NETWORK_ERROR";
            f.App.HoldDiscovery = true;
            var playing = f.Play(ArtworkSong("kg", "first", original));
            try
            {
                await f.App.DiscoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await players.Loaded(original);
                Check(players.Covers.All(cover => ReferenceEquals(cover.Source, listed.Image)),
                    "the already decoded song image is reused before platform discovery finishes");
                var changes = new List<string>();
                f.Module.PropertyChanged += (_, e) =>
                { if (e.PropertyName == nameof(MusicCapability.CurrentArtwork)) changes.Add(f.Module.CurrentArtwork); };
                f.App.DiscoveryRelease.TrySetResult();
                await playing;
                Check(f.Module.CurrentSource == "QQ 音乐" && f.Module.CurrentArtwork == original,
                    "the newly found higher-priority metadata provider does not replace the existing cover");
                f.App.MediaQuality = "320k";
                await f.SetQuality("320k");
                await f.Replay();
                Check(f.Module.CurrentQualityLabel == "高音质", "the test really changes the accepted audio quality");
                await players.Loaded(original);
                Check(changes.All(address => address == original) &&
                    players.Covers.All(cover => ReferenceEquals(cover.Source, listed.Image)),
                    "source and quality publication never clear or replace the image");
                Check(http.Addresses.SequenceEqual([original]), "neither player fetches another platform's artwork");
            }
            finally { f.App.DiscoveryRelease.TrySetResult(); await playing; }
        });
        yield return Case("missing artwork accepts discovered metadata while explicit preferences still apply", async () =>
        {
            await using var f = await Scene.Create();
            const string discovered = "https://art.example.test/discovered.png";
            f.App.DiscoveryArtwork = discovered;
            f.App.Failures["wy"] = "NETWORK_ERROR";
            await f.Play(ArtworkSong("wy", "missing", ""));
            Check(f.Module.CurrentArtwork == discovered, "an accepted alternate recording fills missing artwork");
            await f.Module.SetMetadataEnabledAsync(f.Module.MetadataPlatforms.Single(row => row.Platform == "tx"), false);
            Check(f.Module.CurrentArtwork == "", "disabled provider metadata cannot reenter from an accepted playback snapshot");
            await f.Module.SetMetadataEnabledAsync(f.Module.MetadataPlatforms.Single(row => row.Platform == "tx"), true);
            Check(f.Module.CurrentArtwork == discovered);
        });
        yield return Case("initial displayed cover survives priority differences until the user changes preferences", async () =>
        {
            await using var f = await Scene.Create();
            const string original = "https://art.example.test/primary.png", preferred = "https://art.example.test/preferred.png";
            var song = ArtworkSong("tx", "primary", original);
            song["sources"]!.AsArray().Add(ArtworkSong("wy", "alternate", preferred, "Track primary")["sources"]![0]!.DeepClone());
            await f.Play(song);
            Check(f.Module.CurrentArtwork == original, "starting a displayed song reuses its allowed primary cover");
            await f.Module.MoveMetadataPlatformAsync(f.Module.MetadataPlatforms.Single(row => row.Platform == "mg"),
                f.Module.MetadataPlatforms.Single(row => row.Platform == "kg"));
            Check(f.Module.CurrentArtwork == preferred, "an explicit preference change reapplies saved provider order");
            await f.Module.SetMetadataEnabledAsync(f.Module.MetadataPlatforms.Single(row => row.Platform == "wy"), false);
            Check(f.Module.CurrentArtwork == original, "disabling the selected provider falls back to enabled metadata");
        });
        yield return Case("artwork failures try distinct URLs and both players retain an explicit successful retry", async () =>
        {
            await using var f = await Scene.Create();
            const string original = "https://art.example.test/failed.png", fallback = "https://art.example.test/fallback.png";
            var song = ArtworkSong("wy", "first", original);
            song["sources"]!.AsArray().Add(ArtworkSong("tx", "duplicate", original, "Track first")["sources"]![0]!.DeepClone());
            song["sources"]!.AsArray().Add(ArtworkSong("kg", "fallback", fallback, "Track first")["sources"]![0]!.DeepClone());
            await f.Play(song);
            using var images = new ControlledArtwork();
            using var players = new ArtworkPlayers(f.Module, images);
            await Until(() => images.Calls.Count == 2);
            foreach (var call in images.Calls.ToArray()) call.Completion.TrySetException(new ArtworkException("ARTWORK_NETWORK"));
            await Until(() => images.Calls.Count == 4);
            Check(images.Calls.Skip(2).All(call => call.Address == fallback) && f.Module.CurrentArtwork == fallback,
                "each visible player switches to the next distinct URL only after failure");
            foreach (var call in images.Calls.Skip(2).ToArray()) call.Completion.TrySetException(new ArtworkException("ARTWORK_NETWORK"));
            await Until(() => players.Covers.All(cover => cover.Children.OfType<Button>().Single().IsVisible));
            Check(images.Calls.Count == 4 && f.Module.CurrentArtwork == fallback,
                "exhausted candidates keep the failed image's retry action without looping");
            foreach (var cover in players.Covers)
                cover.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => images.Calls.Count == 6);
            foreach (var call in images.Calls.Skip(4)) call.Completion.TrySetResult(new ArtworkTestLease());
            await players.Loaded(fallback);
            f.App.MediaQuality = "320k";
            await f.SetQuality("320k");
            await f.Replay();
            Check(f.Module.CurrentArtwork == fallback && images.Calls.Count == 6,
                "successfully retried artwork remains selected after audio changes");
        });
        foreach (bool success in new[] { false, true })
            yield return Case("changing songs rejects a late artwork " + (success ? "success" : "failure"), async () =>
            {
                await using var f = await Scene.Create();
                const string first = "https://art.example.test/first.png", next = "https://art.example.test/next.png";
                await f.Play(ArtworkSong("wy", "first", first), ArtworkSong("wy", "next", next));
                using var images = new ControlledArtwork();
                using var players = new ArtworkPlayers(f.Module, images);
                await Until(() => images.Calls.Count == 2);
                var prior = images.Calls.ToArray();
                await f.Module.StepAsync(true);
                await Until(() => images.Calls.Count == 4);
                Check(prior.All(call => call.Token.IsCancellationRequested));
                foreach (var call in images.Calls.Skip(2)) call.Completion.TrySetResult(new ArtworkTestLease());
                await players.Loaded(next);
                var current = players.Covers.Select(cover => cover.Source).ToArray();
                var late = new List<ArtworkTestLease>();
                foreach (var call in prior)
                {
                    if (success) { var lease = new ArtworkTestLease(); late.Add(lease); call.Completion.TrySetResult(lease); }
                    else call.Completion.TrySetException(new ArtworkException("ARTWORK_NETWORK"));
                }
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(f.Module.CurrentArtwork == next && players.Covers.Select(cover => cover.Source).SequenceEqual(current) &&
                    late.All(lease => lease.Disposed), "old results cannot replace or fail the next song's cover");
                await f.Module.StepAsync(false);
                await Until(() => images.Calls.Count == 6);
                foreach (var call in images.Calls.Skip(4)) call.Completion.TrySetResult(new ArtworkTestLease());
                await players.Loaded(first);
            });
    }

    private static JsonObject ArtworkSong(string platform, string id, string cover, string? title = null) =>
        MusicSongFixture.Online(platform, id, title ?? "Track " + id, "Artist", duration: 1, cover: cover);

    private sealed class ArtworkPlayers : IDisposable
    {
        private readonly Window window;
        internal ArtworkImage[] Covers { get; }
        internal ArtworkPlayers(MusicCapability module, IArtworkLoader images)
        {
            var mini = (MusicPlayerView)module.PlayerView;
            var detail = (MusicPlayerDetailView)module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            var layout = new Grid();
            layout.RowDefinitions.Add(new() { Height = new GridLength(100) });
            layout.RowDefinitions.Add(new());
            layout.Children.Add(mini); layout.Children.Add(detail); Grid.SetRow(detail, 1);
            window = new Window { Content = layout, Width = 1100, Height = 900, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            ArtworkScope.SetLoader(window, images);
            var miniRecord = (MusicRecordView)((Button)mini.FindName("ArtworkButton")).Content;
            var detailRecord = (MusicRecordView)detail.FindName("DetailRecord");
            Covers = [(ArtworkImage)miniRecord.FindName("Cover"), (ArtworkImage)detailRecord.FindName("Cover")];
            window.Show(); window.UpdateLayout();
        }
        internal Task Loaded(string address) =>
            Until(() => Covers.All(cover => cover.Address == address && cover.Source is not null));
        public void Dispose() { window.Content = null; window.Close(); }
    }

    private sealed class ControlledArtwork : IArtworkLoader, IDisposable
    {
        internal sealed record Call(string Address, CancellationToken Token, TaskCompletionSource<IArtworkLease> Completion);
        internal readonly List<Call> Calls = [];
        public Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<IArtworkLease>(TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Add(new(address, cancellationToken, completion));
            return completion.Task;
        }
        public void Dispose() { foreach (var call in Calls) call.Completion.TrySetCanceled(); }
    }
    private sealed class ArtworkTestLease : IArtworkLease
    {
        public BitmapSource Image { get; } = ArtworkPixels();
        internal bool Disposed;
        public void Dispose() => Disposed = true;
    }
    private static BitmapSource ArtworkPixels()
    {
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 60, 120, 180, 255 }, 4);
        image.Freeze(); return image;
    }
    private sealed class ArtworkHttpProbe : HttpMessageHandler
    {
        internal readonly List<string> Addresses = [];
        private readonly byte[] bytes = Encode();
        private static byte[] Encode()
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(ArtworkPixels()));
            using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Addresses) Addresses.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
