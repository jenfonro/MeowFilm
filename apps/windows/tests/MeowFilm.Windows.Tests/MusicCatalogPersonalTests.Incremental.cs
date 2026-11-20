using Microsoft.UI.Xaml.Controls;
using MeowFilm.Capabilities;
using MeowFilm.Desktop.Artwork;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Windows.Tests;

public static partial class MusicCatalogPersonalTests
{
    private static async Task RetainedCatalog(bool channel)
    {
        await using var f = await Fixture.Create();
        var page = E(new
        {
            ok = true, platform = "wy", more = false, channel = new { id = "rock" },
            sections = Enumerable.Range(0, channel ? 1 : 4).Select(section => new
            {
                id = "retained-" + section, title = "推荐 " + section, layout = "playlist-grid",
                items = Enumerable.Range(0, channel ? 48 : 12).Select(index => new
                {
                    id = (1000 + section * 200 + index).ToString(), kind = "playlist", title = "音乐 " + section + " / " + index,
                    cover = "https://art.example.test/retained/" + section + "/" + index,
                    action = new { type = "playlist", id = (1000 + section * 200 + index).ToString() }
                }).ToArray()
            }).ToArray()
        });
        f.App.TransformNext(channel ? "music.catalog.channel" : "music.catalog.home", _ => page);
        await f.Module.NavigateAsync(channel ? "catalog-channel/wy/rock" : "catalog-home", default);
        var artwork = new RetainedCatalogArtwork();
        ArtworkScope.SetLoader(f.Module.View, artwork);
        using var host = new HomeNoticeMount(f.Module.View);
        await TestUiHost.LoadedAsync(f.Module.View);
        var view = Visuals<MusicCatalogView>(f.Module.View).Single();
        var scroll = view.ActiveScroll;
        var source = channel ? f.Module.ChannelSections : f.Module.HomeSections;
        int count = source.Sum(section => section.Items.Count);
        await Until(() => scroll.ScrollableHeight > 500 && artwork.Live > 0);
        Check(artwork.Reads < count, "unseen cards do not read artwork on receipt");
        var sections = Visuals<MusicCatalogSectionView>(scroll).ToArray();
        var cards = Visuals<MusicCatalogCardView>(scroll).ToArray();
        var images = cards.SelectMany(Visuals<ArtworkImage>).ToArray();
        var events = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        source.CollectionChanged += (_, e) => events.Add(e.Action);
        async Task Traverse()
        {
            for (double offset = 0; offset < scroll.ScrollableHeight + scroll.ViewportHeight; offset += scroll.ViewportHeight * .75)
            {
                double next = Math.Min(offset, scroll.ScrollableHeight);
                scroll.ChangeView(null, next, null, true);
                await Until(() => Math.Abs(scroll.VerticalOffset - next) < 1);
                await Task.Delay(20);
            }
            scroll.ChangeView(null, 0, null, true); await Until(() => scroll.VerticalOffset < 1);
        }
        await Traverse(); await Until(() => artwork.Reads == count);
        int reads = artwork.Reads;
        await Traverse();
        f.Module.Volume = 37;
        await Drain();
        Check(artwork.Reads == reads && artwork.Live == count, "returning over admitted cards and an unrelated property do not reread or retire them");
        Check(events.Count == 0 && sections.SequenceEqual(Visuals<MusicCatalogSectionView>(scroll)) &&
            cards.SequenceEqual(Visuals<MusicCatalogCardView>(scroll)) && images.SequenceEqual(cards.SelectMany(Visuals<ArtworkImage>)),
            "scrolling and unrelated properties retain section card and image identities");
        var first = source[0];
        var added = new MusicCatalogCard("added", "新增", "", "https://art.example.test/retained/new", "playlist", "wy", ActionId: "added");
        source[0] = first with { Items = first.Items.Append(added).ToArray() };
        await Drain(); scroll.UpdateLayout(); await Drain();
        Check(sections.SequenceEqual(Visuals<MusicCatalogSectionView>(scroll)), "a new receipt retains the section controls");
        Check(cards.All(card => Visuals<MusicCatalogCardView>(scroll).Contains(card)) && images.All(image => image.Source is not null),
            "append retains existing card controls and their decoded images");
        var unchanged = cards.Where(card => !ReferenceEquals(card.DataContext, first.Items[0])).ToArray();
        var updated = first.Items[0] with { Title = "修改标题" };
        source[0] = source[0] with { Items = source[0].Items.Select(card => ReferenceEquals(card, first.Items[0]) ? updated : card).ToArray() };
        await Drain(); scroll.UpdateLayout(); await Drain();
        Check(unchanged.All(card => Visuals<MusicCatalogCardView>(scroll).Contains(card)), "one changed item leaves other card controls intact");
        source[0] = source[0] with { Items = source[0].Items.Skip(1).ToArray() };
        await Drain();
        Check(unchanged.All(card => Visuals<MusicCatalogCardView>(scroll).Contains(card)), "removing one item retains every surviving card");
        Check(!events.Contains(System.Collections.Specialized.NotifyCollectionChangedAction.Reset), "incremental receipts never reset the section collection");
        f.Module.View.Visibility = Visibility.Collapsed; await Drain();
        int hiddenReads = artwork.Reads;
        f.Module.View.Visibility = Visibility.Visible; await Drain();
        Check(artwork.Reads == hiddenReads, "showing retained content does not reload its successful images");
        DesktopTestUi.Unmount(f.Module.View); await Until(() => artwork.Live == 0);
        Check(f.Output.Opens == 0, "presentation changes remain read-only");
    }

    private sealed class RetainedCatalogArtwork : IArtworkLoader
    {
        private readonly ArtworkPixels pixels = MusicTestUi.Pixels(192, 192,
            (x, y) => global::Windows.UI.Color.FromArgb(255, (byte)(40 + x / 2), (byte)(50 + y / 2), 90));
        public int Reads, Live, Peak;
        public Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++; Live++; Peak = Math.Max(Peak, Live);
            return Task.FromResult<IArtworkLease>(new Lease(this, pixels));
        }
        private sealed class Lease(RetainedCatalogArtwork owner, ArtworkPixels image) : IArtworkLease
        {
            private bool disposed;
            public ArtworkPixels Image { get; } = image;
            public void Dispose() { if (!disposed) { disposed = true; owner.Live--; } }
        }
    }
}
