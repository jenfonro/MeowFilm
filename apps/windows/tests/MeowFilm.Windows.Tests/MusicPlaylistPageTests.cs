using System.Text.Json;
using System.Windows.Controls;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;

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
            Equal("#", grid.Columns[0].Header);
            Equal("标题", grid.Columns[1].Header);
            True(f.Module.IsContentDetailPage && f.Module.ShowsSongTable, "local playlist uses the shared detail surface");
            Equal("3", f.Module.DetailCountText);
            Equal(50d, grid.RowHeight);
            True(f.Module.CanReorderPlaylist, "same page owns drag reorder");
            True(((MeowFilm.Desktop.IDesktopHeaderSource)f.Module.View).Header.Slot is null,
                "playlist detail does not publish the home-only create control");
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
