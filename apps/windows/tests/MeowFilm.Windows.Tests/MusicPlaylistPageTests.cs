using Microsoft.UI.Xaml.Automation;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

using Microsoft.UI.Xaml.Media;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class MusicPlaylistPageTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.PlaylistPageCases();
}

public static partial class MusicUiTests
{
    public static IEnumerable<TestCase> PlaylistPageCases()
    {
        yield return new("Native playlist reorder is one current-format CAS command", NativePlaylistReorder);
        yield return Case("playlist page incrementally loads and reorders across loaded pages", async () =>
        {
            await using var f = await Fixture.Create();
            string id = await f.Collection("增量歌单", 205);
            await f.Module.NavigateAsync("playlist/" + id, default);
            Equal(100, f.Module.Songs.Count);
            True(f.Module.CanLoadMoreCollection, "first page exposes more");
            Equal("", f.Module.PageText);

            await f.Module.LoadMoreCollectionAsync();
            Equal(200, f.Module.Songs.Count);
            var source = f.Module.Songs[99];
            var target = f.Module.Songs[100];
            await f.Module.ReorderPlaylistSongAsync(source, target, true);
            Equal(source.Id, f.Module.Songs[100].Id);
            Equal(1, f.Calls.Count("music.library.reorderSong"));

            await f.Module.LoadMoreCollectionAsync();
            Equal(205, f.Module.Songs.Count);
            True(!f.Module.CanLoadMoreCollection, "last page terminates");
            var detail = await f.Rpc("music.library.detail", new { id, offset = 95, limit = 10 });
            Equal(source.Id, detail.GetProperty("songs")[5].GetProperty("songId").GetString());
        });
        yield return Case("playlist page coalesces repeated load-more requests and rejects stale pages", async () =>
        {
            await using var f = await Fixture.Create();
            string id = await f.Collection("分页代次", 205);
            await f.Module.NavigateAsync("playlist/" + id, default);
            var hold = f.Calls.HoldNext("music.library.detail", p => p.GetProperty("offset").GetInt32() == 100);
            var first = f.Module.LoadMoreCollectionAsync();
            await hold.Arrived.Task;
            var duplicate = f.Module.LoadMoreCollectionAsync();
            True(duplicate.IsCompleted, "in-flight page owns the offset");
            await f.Module.NavigateAsync("favorites", default);
            hold.Release();
            await first;
            Equal("我的收藏", f.Module.Heading);
            Equal(0, f.Module.Songs.Count);
            Equal(1, f.Calls.Log.Count(call => call.Method == "music.library.detail" &&
                call.Parameters.TryGetProperty("offset", out var offset) && offset.GetInt32() == 100));
        });
        yield return Case("playlist page uses the inline virtualized row surface only", async () =>
        {
            await using var f = await Fixture.Create();
            string id = await f.Collection("内联页面", 3);
            await f.Module.NavigateAsync("playlist/" + id, default);
            using var host = new DesktopTestUi.Host(f.Module.View, 1080, 680); await MusicTestUi.Drain();
            var grid = (ListView)f.Module.View.FindName("SongsGrid");
            True(grid.ItemsPanelRoot is ItemsStackPanel, "playlist rows use the native recycling panel");
            var header = DesktopTestUi.Visuals((FrameworkElement)f.Module.View.FindName("SongHeaders")).OfType<Grid>()
                .Single(panel => panel.ColumnDefinitions.Count == 4);
            Equal(4, header.ColumnDefinitions.Count);
            True(header.Children.OfType<TextBlock>().Select(text => text.Text).SequenceEqual(new[] { "歌曲 / 歌手", "专辑", "时长", "操作" }),
                "all four song table column labels remain visible");
            True(header.ColumnDefinitions[0].Width.IsStar && header.ColumnDefinitions[0].Width.Value == 3 &&
                header.ColumnDefinitions[1].Width.IsStar && header.ColumnDefinitions[1].Width.Value == 2,
                "song and album columns retain the shared three-to-two ratio");
            True(f.Module.IsContentDetailPage && f.Module.ShowsSongTable, "local playlist uses the shared detail surface");
            Equal("3", f.Module.DetailCountText);
            var row = (FrameworkElement)grid.ContainerFromIndex(0);
            Equal(50d, DesktopTestUi.Visuals(row).OfType<Grid>().Single(panel => panel.ColumnDefinitions.Count == 4).Height);
            True(f.Module.CanReorderPlaylist, "same page owns drag reorder");
            True(((MeowFilm.Desktop.IDesktopHeaderSource)f.Module.View).Header.Slot is null,
                "playlist detail does not publish the home-only create control");
        });
        yield return Case("song table reuses the shared overlay scroll chrome", async () =>
        {
            await using var f = await Fixture.Create();
            string id = await f.Collection("滚动表格", 80);
            await f.Module.NavigateAsync("playlist/" + id, default);
            var page = (MusicView)f.Module.View;
            using var host = new DesktopTestUi.Host(page, 760, 360); await MusicTestUi.Drain();
            var grid = (ListView)page.FindName("SongsGrid");
            var scroll = DesktopTestUi.Visuals(grid).OfType<ScrollViewer>().First();
            var bar = DesktopTestUi.Visuals(scroll).OfType<ScrollBar>().Single(value => value.Orientation == Orientation.Vertical);
            var content = DesktopTestUi.Visuals(scroll).OfType<ScrollContentPresenter>().First();
            True(scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto, "native overlay scrollbars follow content overflow");
            True(ReferenceEquals(VisualTreeHelper.GetParent(bar), VisualTreeHelper.GetParent(content)) &&
                Grid.GetColumn(bar) == Grid.GetColumn(content) && Grid.GetRow(bar) == Grid.GetRow(content),
                "the native scrollbar overlays the data viewport");
            var headers = (FrameworkElement)page.FindName("SongHeaders");
            True(DesktopVisual.IsVisible(headers) && headers.ActualHeight > 0, "column headers remain intact");
            True(scroll.ScrollableHeight > 0 && grid.ItemsPanelRoot is ItemsStackPanel, "virtualized rows remain scrollable");
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(scroll)!;
            ((IScrollProvider)peer.GetPattern(PatternInterface.Scroll)).Scroll(ScrollAmount.NoAmount, ScrollAmount.SmallIncrement);
            await MusicTestUi.Drain();
            True(scroll.VerticalOffset > 0 && DesktopVisual.IsVisible(bar), "native scroll provider moves the row surface without input simulation");

        });
    }

    private static async Task NativePlaylistReorder()
    {
        await using var store = new DocumentProbe();
        await using var library = new MusicLibraryOwner(MusicTestStores.For(store));
        var created = await library.ExecuteAsync("music.library.create", new
        {
            name = "排序",
            songs = new[] { PlaylistSongFixture("a"), PlaylistSongFixture("b"), PlaylistSongFixture("c") }
        });
        True(created.GetProperty("ok").GetBoolean(), "playlist creation succeeds");
        string id = created.GetProperty("collection").GetProperty("id").GetString()!;
        var initial = await library.ExecuteAsync("music.library.detail", new { id });
        var ids = initial.GetProperty("songs").EnumerateArray().Select(song => song.GetProperty("songId").GetString()!).ToArray();
        True(ids.All(songId => Guid.TryParse(songId, out _)), "the reorder consumes permanent repository IDs");
        var moved = await library.ExecuteAsync("music.library.reorderSong", new
        {
            id,
            ifRevision = 1,
            songId = ids[2],
            targetId = ids[0],
            after = false
        });
        True(moved.GetProperty("ok").GetBoolean(), "playlist reorder succeeds");
        Equal(2L, moved.GetProperty("collection").GetProperty("revision").GetInt64());
        var detail = await library.ExecuteAsync("music.library.detail", new { id, offset = 0, limit = 100 });
        True(detail.GetProperty("songs").EnumerateArray()
            .Select(row => row.GetProperty("songId").GetString())
            .SequenceEqual(new[] { ids[2], ids[0], ids[1] }),
            "one owner persists the exact order");
    }

    private static object PlaylistSongFixture(string id) =>
        MusicSongFixture.Local("library:local:" + id, "Track " + id, "Artist", "Album", 180);
}
