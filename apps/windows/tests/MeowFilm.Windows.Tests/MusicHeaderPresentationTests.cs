using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Reflection;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class MusicHeaderPresentationTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.HeaderPresentationCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> HeaderPresentationCases()
    {
        yield return Case("both retained song tables initialize overlay scrollbar placement", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Policy(["wy"], []);
            var window = SearchPlaylistWindow(f.Module.View, 1042, 700);
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                // Both templates may load while their table is initially hidden on recommendation.
                foreach (string name in new[] { "SongsGrid", "SearchSongsGrid" })
                    ((DataGrid)f.Module.View.FindName(name)).ApplyTemplate();
                await f.Search("滚动条"); await f.Module.OpenSearchTabAsync("songs");
                window.UpdateLayout(); await Drain();
                AssertChrome("SearchSongsGrid");
                string searchLocation = f.Module.CaptureNavigationLocation();
                await f.Module.NavigateAsync("favorites", default); window.UpdateLayout(); await Drain();
                AssertChrome("SongsGrid");
                await f.Module.RestoreNavigationAsync(searchLocation, default); window.UpdateLayout(); await Drain();
                AssertChrome("SearchSongsGrid");
            }
            finally { window.Content = null; window.Close(); }

            void AssertChrome(string name)
            {
                var grid = (DataGrid)f.Module.View.FindName(name);
                var scroll = Visuals(grid).OfType<ScrollViewer>().First(value => ReferenceEquals(value.TemplatedParent, grid));
                var bar = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
                var content = (FrameworkElement)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
                True(ReferenceEquals(bar.Style, grid.FindResource("Capability.OverlayScrollBar")), name + " uses overlay style");
                True(ReferenceEquals(VisualTreeHelper.GetParent(bar), VisualTreeHelper.GetParent(content)), name + " has common grid parent");
                Equal(Grid.GetColumn(content), Grid.GetColumn(bar));
                Equal(Grid.GetRow(content), Grid.GetRow(bar));
                True(Panel.GetZIndex(bar) > Panel.GetZIndex(content), name + " overlays content");
            }
        });
        yield return Case("music header suppresses unchanged refresh publications and changes with real actions", async () =>
        {
            await using var f = await Fixture.Create();
            var source = (IDesktopHeaderSource)f.Module.View;
            var header = source.Header;
            var refreshBindings = typeof(MusicCapability).GetMethod("RefreshBindings",
                BindingFlags.Instance | BindingFlags.NonPublic) ??
                throw new InvalidOperationException("Music binding refresh seam unavailable");
            True(Equals(0, header.HeaderActions.Count), "favorites owns no collection detail actions");

            var count = 0;
            source.HeaderChanged += Count;
            try
            {
                for (var i = 0; i < 3; i++) refreshBindings.Invoke(f.Module, null);
                True(Equals(0, count), "repeated unchanged bindings publish no header change");

                await f.Module.NavigateAsync("history", default);
                await Drain();
                header = source.Header;
                True(header.HeaderActions.Select(action => action.Id).SequenceEqual(new[] { "music.refresh" }),
                    "history keeps the local-library refresh action");
                True(header.HeaderActions.All(action => action.IsEnabled), "CanEdit remains part of action availability");
                True(count > 0, "real action changes notify the shell");
                var changed = count;
                for (var i = 0; i < 3; i++) refreshBindings.Invoke(f.Module, null);
                True(Equals(changed, count), "stable bindings do not republish the same action role");
            }
            finally { source.HeaderChanged -= Count; }

            void Count(object? sender, EventArgs e) => count++;
        });

        yield return Case("music collection header keeps detail actions and CanEdit protection", async () =>
        {
            await using var f = await Fixture.Create();
            var source = (IDesktopHeaderSource)f.Module.View;
            var header = source.Header;
            var refreshBindings = typeof(MusicCapability).GetMethod("RefreshBindings",
                BindingFlags.Instance | BindingFlags.NonPublic) ??
                throw new InvalidOperationException("Music binding refresh seam unavailable");
            True(Equals(0, header.HeaderActions.Count), "favorites cannot be deleted from the header");
            var count = 0;
            source.HeaderChanged += Count;
            try
            {
                await f.Module.CreatePlaylistAsync("页头测试歌单");
                header = source.Header;
                True(header.HeaderActions.Select(action => action.Id)
                    .SequenceEqual(new[] { "music.playlist.rename", "music.playlist.delete" }),
                    "a created playlist keeps only its detail actions");
                True(count > 0, "entering the collection publishes its real actions");

                await f.Module.PrepareShutdownAsync();
                header = source.Header;
                True(header.HeaderActions.Select(action => action.Id)
                    .SequenceEqual(new[] { "music.playlist.rename", "music.playlist.delete" }),
                    "the action role survives while admission is closed");
                True(header.HeaderActions.All(action => !action.IsEnabled),
                    "CanEdit still closes every collection action");
                var changed = count;
                refreshBindings.Invoke(f.Module, null);
                True(Equals(changed, count), "the changed action state is published only once");
            }
            finally
            {
                source.HeaderChanged -= Count;
                await f.Module.CancelShutdownPreparationAsync();
                await Until(() => f.Module.CanEdit);
            }

            void Count(object? sender, EventArgs e) => count++;
        });
    }
}
