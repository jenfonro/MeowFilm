using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Windows.Tests;

public static partial class MusicCatalogPersonalTests
{
    private static async Task ChannelEvictionRetirement()
    {
        await using var f = await Fixture.Create();
        f.Platform.ChannelEntries = [MissingArtworkCard("500", "retired")];
        await f.Module.NavigateAsync("catalog-channel/wy/retired", default);
        var first = f.Module.ChannelSections;
        var card = first.Single().Items.Single();
        var held = f.App.HoldNext("music.catalog.playlists");
        f.Module.UpdateCatalogVisibleCards([card]);
        var optional = f.Module.CatalogEntryInitialization;
        await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var host = new DesktopTestUi.Host(f.Module.View, 900, 480);
        await TestUiHost.LoadedAsync(f.Module.View); await Drain();
        var section = Visuals<MusicCatalogSectionView>(f.Module.View).Single();
        int unloaded = 0; section.Unloaded += (_, _) => unloaded++;
        f.Platform.ChannelEntries = [MissingArtworkCard("501", "retained", cover: "https://art.example.test/retained")];
        for (int index = 1; index <= 8; index++)
            await f.Module.NavigateAsync("catalog-channel/wy/visited-" + index, default);
        Check(first.Count > 0 && !held.Cancelled.Task.IsCompleted, "the saved channel retains its admitted optional read before eviction");
        var next = f.App.HoldNext("music.catalog.playlist", before: true);
        var navigating = f.Module.NavigateAsync("catalog-playlist/tx/600", default);
        try
        {
            await next.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Drain();
            await held.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(first.Count == 0 && unloaded == 1 && !section.IsLoaded,
                "cache eviction removes the mounted channel without waiting for the next receipt");
            Check(!optional.IsCompleted, "eviction cancellation still waits for the actual noncooperative optional receipt");
            held.Release(); await optional;
            Equal("", card.Cover);
            next.Release(); await navigating;
            Equal(0, f.Output.Opens);
        }
        finally { held.Release(); next.Release(); await navigating; }
    }

    private static async Task HiddenChannelAccountRetirement(bool anotherPlatform)
    {
        await using var f = await Fixture.Create();
        f.Platform.ChannelEntries = [MissingArtworkCard("500", "old-account", cover: "https://art.example.test/old-account")];
        await f.Module.NavigateAsync("catalog-channel/wy/rock", default);
        var first = f.Module.ChannelSections;
        using var host = new DesktopTestUi.Host(f.Module.View, 900, 480);
        await TestUiHost.LoadedAsync(f.Module.View);
        var section = Visuals<MusicCatalogSectionView>(f.Module.View).Single();
        var artwork = new RetainedCatalogArtwork(); ArtworkScope.SetLoader(section, artwork);
        await Until(() => artwork.Live > 0);
        if (anotherPlatform)
        {
            await f.Module.NavigateAsync("catalog-playlist/tx/600", default); await Drain();
            Equal("tx", f.Platform.Last<MusicPlaylistRequest>().Platform);
            await Until(() => !section.IsLoaded && artwork.Live == 0);
            Check(first.Count > 0, "a genuinely unloaded channel releases presentation resources while retaining its saved receipt owner");
        }
        else
        {
            f.Platform.ChannelEntries = [MissingArtworkCard("600", "current-channel", cover: "https://art.example.test/current-channel")];
            await f.Module.NavigateAsync("catalog-channel/wy/other", default); await Drain();
            Check(section.IsLoaded && first.Count > 0 && artwork.Live > 0,
                "switching channels of the current source retains hidden mounted content and admitted artwork");
        }
        var current = f.Module.CatalogSections;
        var receipts = current.ToArray();
        f.App.RevokeAccount("wy");
        await Until(() => first.Count == 0 && !section.IsLoaded && artwork.Live == 0);
        if (anotherPlatform)
            Check(ReferenceEquals(current, f.Module.CatalogSections) && current.SequenceEqual(receipts, ReferenceEqualityComparer.Instance),
                "retiring another platform's saved channel retains the current platform receipts");
        Check(!f.Module.RetainedChannelSections.Contains(first), "the invalid account no longer owns a retained channel page");
        Equal(0, f.Output.Opens);
    }
}
