using System.Collections.Specialized;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Windows.Tests;

public static partial class MusicCatalogTests
{
    private static IEnumerable<TestCase> ChannelPresentationCases()
    {
        yield return Case("channel switching shows one loading ring and retains visited controls pixels and receipts", async () =>
        {
            await using var f = await Fixture.Create();
            JsonElement ChannelPage(JsonElement reply) => Edit(reply, page =>
            {
                string id = page["id"]!.GetValue<string>();
                page["more"] = false; page["continuation"] = null;
                page["sections"] = JsonSerializer.SerializeToNode(new[] { new
                {
                    id = "playlists", title = "频道歌单", layout = "playlist-rail",
                    items = Enumerable.Range(1, 4).Select(index => new
                    {
                        id = id + index, title = "歌单 " + index, kind = "playlist",
                        cover = $"https://art.example.test/{id}-{index}.jpg",
                        action = new { type = "playlist", id = id + index }
                    }).ToArray()
                } });
            });
            var loader = new CatalogArtworkLoader();
            var view = new MusicCatalogView { DataContext = f.Module };
            ArtworkScope.SetLoader(view, loader);
            var window = DesktopTestUi.CreateWindow(view, 1042, 650);
            try
            {
                window.Show();
                var firstHold = new Held();
                f.Client.HoldReply("music.catalog.channel", firstHold, ChannelPage);
                var firstLoad = f.Module.NavigateAsync("catalog-channels", default);
                await firstHold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Drain();
                var ring = (ProgressRing)view.FindName("CatalogLoading");
                var scroll = (ScrollViewer)view.FindName("CatalogScroll");
                True(ring.IsActive && DesktopVisual.IsVisible(ring), "first channel waits with an active ring");
                True(!DesktopVisual.IsVisible(scroll), "no empty section body is presented before the receipt");
                await NativeDesktopCapture.SaveAsync(view, "channel-loading");
                firstHold.Release(); await firstLoad;
                DesktopTestUi.WindowRoot(window).UpdateLayout(); await Drain();
                await Until(() => Descendants<ArtworkImage>(view).Count(image => image.Source is not null) == 4);
                var firstSections = f.Module.ChannelSections;
                var firstCards = firstSections.Single().Items;
                var firstViews = Descendants<MusicCatalogSectionView>(view).ToArray();
                var firstImages = Descendants<ArtworkImage>(view).ToArray();
                var pixels = firstImages.Select(image => image.Source).ToArray();
                var contentNotifications = new List<string>();
                f.Module.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName is nameof(MusicCapability.CatalogSections) or nameof(MusicCapability.ChannelSections) or
                        nameof(MusicCapability.ContentPage) or nameof(MusicCapability.CurrentSongId)) contentNotifications.Add(args.PropertyName);
                };
                await f.Module.RunAsync(() => Task.CompletedTask);
                Equal(0, contentNotifications.Count);
                int changed = 0, unloaded = 0;
                firstSections.CollectionChanged += (_, _) => changed++;
                foreach (var section in firstViews) section.Unloaded += (_, _) => unloaded++;
                var a = f.Module.ChannelTabs.Single(card => card.ActionId == "feature");
                var b = f.Module.ChannelTabs.Single(card => card.ActionId == "rock");
                var secondHold = new Held();
                f.Client.HoldReply("music.catalog.channel", secondHold, ChannelPage);
                var secondLoad = f.Module.OpenCatalogCardAsync(b);
                await secondHold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Drain();
                True(ring.IsActive && DesktopVisual.IsVisible(ring), "uncached channel waits with a ring");
                True(!DesktopVisual.IsVisible(scroll), "old channel does not flash under the new selection");
                Equal(0, changed); Equal(0, unloaded);
                Equal(1, contentNotifications.Count(name => name == nameof(MusicCapability.CatalogSections)));
                contentNotifications.Clear();
                secondHold.Release(); await secondLoad;
                DesktopTestUi.WindowRoot(window).UpdateLayout(); await Drain();
                await Until(() => Descendants<ArtworkImage>(view).Count(image => DesktopVisual.IsVisible(image) && image.Source is not null) == 4);
                Equal(0, contentNotifications.Count);
                int reads = f.Client.Count("music.catalog.channel"), imagesRead = loader.Addresses.Count;
                await f.Module.OpenCatalogCardAsync(a); await Drain();
                Equal(1, contentNotifications.Count(name => name == nameof(MusicCapability.CatalogSections)));
                True(!contentNotifications.Contains(nameof(MusicCapability.CurrentSongId)), "channel navigation does not publish a playback identity change");
                True(ReferenceEquals(firstSections, f.Module.ChannelSections), "return selects the retained channel collection");
                True(ReferenceEquals(firstCards, f.Module.ChannelSections.Single().Items), "return retains card receipts");
                True(firstViews.All(section => section.IsLoaded && DesktopVisual.IsVisible(section)), "visited sections stay mounted");
                True(firstImages.Select(image => image.Source).SequenceEqual(pixels), "return retains decoded pixels");
                Equal(0, changed); Equal(0, unloaded);
                Equal(reads, f.Client.Count("music.catalog.channel")); Equal(imagesRead, loader.Addresses.Count);
                True(!ring.IsActive && !DesktopVisual.IsVisible(ring), "cached return has no loading flash");
                await NativeDesktopCapture.SaveAsync(view, "channel-return");
                f.Module.RetireCatalogSession(); await Drain();
                Equal(0, firstSections.Count);
                True(firstViews.All(section => !section.IsLoaded), "session retirement removes retained presentations");
            }
            finally
            {
                f.Client.ReleaseAll();
                var unloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Unloaded(object sender, RoutedEventArgs args) => unloaded.TrySetResult();
                bool wasLoaded = view.IsLoaded;
                if (wasLoaded) view.Unloaded += Unloaded;
                try
                {
                    window.Content = null;
                    if (wasLoaded) await unloaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally { view.Unloaded -= Unloaded; window.Close(); }
            }
        });
    }
}
