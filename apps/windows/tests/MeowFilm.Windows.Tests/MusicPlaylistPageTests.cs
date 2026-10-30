using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
            var grid = (DataGrid)f.Module.View.FindName("SongsGrid");
            True(grid.EnableRowVirtualization && VirtualizingPanel.GetIsVirtualizing(grid) &&
                VirtualizingPanel.GetVirtualizationMode(grid) == VirtualizationMode.Recycling,
                "playlist rows remain virtualized");
            Equal(4, grid.Columns.Count);
            Equal("歌曲 / 歌手", grid.Columns[0].Header);
            Equal("专辑", grid.Columns[1].Header);
            Equal("时长", grid.Columns[2].Header);
            Equal("操作", grid.Columns[3].Header);
            True(grid.Columns[0].Width.IsStar && grid.Columns[0].Width.Value == 3 &&
                grid.Columns[1].Width.IsStar && grid.Columns[1].Width.Value == 2,
                "song and album columns retain the shared three-to-two ratio");
            True(f.Module.IsContentDetailPage && f.Module.ShowsSongTable, "local playlist uses the shared detail surface");
            Equal("3", f.Module.DetailCountText);
            Equal(50d, grid.RowHeight);
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
            var window = new Window
            {
                Content = page, Width = 760, Height = 360, WindowStyle = WindowStyle.None,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false
            };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                var grid = (DataGrid)page.FindName("SongsGrid");
                var scroll = Visuals(grid).OfType<ScrollViewer>()
                    .Single(candidate => ReferenceEquals(candidate.TemplatedParent, grid));
                True(OverlayScrollChrome.GetEnabled(scroll), "song table mounts the shared scroll behavior");
                var bar = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
                True(ReferenceEquals(bar.Style, page.FindResource("Capability.OverlayScrollBar")),
                    "song table uses the shared overlay scrollbar visual");
                if (!DesktopTheme.GetIsHighContrast(bar))
                    True(bar.Opacity == 0, "song scrollbar is hidden at rest");
                var content = (FrameworkElement)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
                True(ReferenceEquals(VisualTreeHelper.GetParent(bar), VisualTreeHelper.GetParent(content)) &&
                    Grid.GetColumn(bar) == Grid.GetColumn(content) && Grid.GetRow(bar) == Grid.GetRow(content),
                    "song scrollbar overlays the data viewport");
                var headers = Visuals(grid).OfType<DataGridColumnHeadersPresenter>().Single();
                True(headers.IsVisible && headers.ActualHeight > 0, "data grid column headers remain intact");
                True(scroll.ScrollableHeight > 0 && scroll.CanContentScroll, "virtualized song rows remain scrollable");
                bar.ApplyTemplate();
                var track = (Track)bar.Template.FindName("PART_Track", bar);
                ((RoutedCommand)track.IncreaseRepeatButton.Command).Execute(null, track.IncreaseRepeatButton);
                await Drain();
                True(scroll.VerticalOffset > 0 && OverlayScrollChrome.GetActive(scroll) && bar.Opacity > 0,
                    "real table scrolling reveals the shared chrome");
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        });
    }

    private static async Task NativePlaylistReorder()
    {
        await using var store = new DocumentProbe();
        await using var library = new MusicLibraryOwner(store);
        var created = await library.ExecuteAsync("music.library.create", new
        {
            name = "排序",
            songs = new[] { PlaylistSongFixture("a"), PlaylistSongFixture("b"), PlaylistSongFixture("c") }
        });
        True(created.GetProperty("ok").GetBoolean(), "playlist creation succeeds");
        string id = created.GetProperty("collection").GetProperty("id").GetString()!;
        var moved = await library.ExecuteAsync("music.library.reorderSong", new
        {
            id,
            ifRevision = 1,
            songId = "library:local:c",
            targetId = "library:local:a",
            after = false
        });
        True(moved.GetProperty("ok").GetBoolean(), "playlist reorder succeeds");
        Equal(2L, moved.GetProperty("collection").GetProperty("revision").GetInt64());
        var detail = await library.ExecuteAsync("music.library.detail", new { id, offset = 0, limit = 100 });
        True(detail.GetProperty("songs").EnumerateArray()
            .Select(row => row.GetProperty("songId").GetString())
            .SequenceEqual(new[] { "library:local:c", "library:local:a", "library:local:b" }),
            "one owner persists the exact order");
    }

    private static object PlaylistSongFixture(string id) =>
        MusicSongFixture.Local("library:local:" + id, "Track " + id, "Artist", "Album", 180);
}
