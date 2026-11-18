using Microsoft.UI.Xaml;
using Windows.Foundation;
using Window = MeowFilm.Desktop.DesktopWindow;
using Microsoft.UI.Xaml.Controls;
using Key = System.Windows.Input.Key;
using ModifierKeys = System.Windows.Input.ModifierKeys;
using KeyInterop = System.Windows.Input.KeyInterop;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Color = Windows.UI.Color;
using Colors = Microsoft.UI.Colors;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class SharedPresentationLifecycleTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("hidden cards stop motion and retain initialized songs without unloading", async () =>
        {
            var entry = new EntryCard
            {
                Width = 168,
                DataContext = new MusicCatalogCard("daily", "每日推荐", "", "", "daily", "wy",
                    ActionId: "daily", Presentation: MusicCatalogEntryPresentation.Preview)
            };
            using var scene = new Scene(entry);
            await Drain();
            var card = (MusicCatalogCard)entry.DataContext;
            card.InitializePreview([new("初始化曲目", "艺人", "", 1, null)]);
            var surface = (Grid)entry.FindName("Surface");
            var focus = DesktopTestUi.Visuals(entry).OfType<Control>().First(control => control.IsTabStop && control.IsEnabled);
            Check(focus.Focus(FocusState.Programmatic), "card accepts actual native focus");
            await Drain();
            entry.Visibility = Visibility.Collapsed;
            Check(entry.IsLoaded, "hiding retains the loaded card");
            Check(card.HasPreview, "hiding retains page-owned songs");
            Check(CardMotion.GetProgress(surface) == 0,
                "hidden card owns no hover animation clock");
            Check(card.PreviewTitles.SequenceEqual(["1 初始化曲目"]), "hidden input cannot change initialized songs");
            entry.Visibility = Visibility.Visible;
            scene.Layout(); await Drain();
            Check(card.PreviewTitles.SequenceEqual(["1 初始化曲目"]), "returning reuses initialized songs");
        });
        yield return Case("hidden shelf retains rows and settled position but stops paging motion", async () =>
        {
            var section = new MusicCatalogSection("测试栏目", Enumerable.Range(0, 24)
                .Select(index => new MusicCatalogCard(index.ToString(), $"歌曲 {index}", "", "", "playlist", "wy"))
                .ToArray());
            var shelf = new Shelf
            {
                Height = 160,
                DataContext = section,
                CardTemplate = (DataTemplate)XamlReader.Load("""
                    <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                      <Border Height="120"><TextBlock Text="{Binding Title}"/></Border>
                    </DataTemplate>
                    """)
            };
            using var scene = new Scene(shelf);
            await Drain();
            var list = (ListView)shelf.FindName("ItemsHost");
            var source = list.ItemsSource;
            var rail = shelf.ScrollHost ?? throw new InvalidOperationException("shelf scroller not realized");
            Check(rail.ScrollableWidth > 0, "fixture has real scrollable content");
            DesktopTestUi.Invoke((Button)shelf.FindName("Next"));
            shelf.Visibility = Visibility.Collapsed;
            double saved = section.HorizontalOffset;
            Check(shelf.IsLoaded && ReferenceEquals(source, list.ItemsSource) && list.Items.Count == 24,
                "hiding does not detach rows or the shelf");
            Check(shelf.IsSettled, "hiding immediately stops the scrolling animation");
            DesktopTestUi.Invoke((Button)shelf.FindName("Next"));
            Check(section.HorizontalOffset == saved && shelf.IsSettled,
                "hidden page commands cannot restart movement");
            shelf.Visibility = Visibility.Visible;
            scene.Layout(); await Drain();
            Check(ReferenceEquals(rail, shelf.ScrollHost) && ReferenceEquals(source, list.ItemsSource),
                "returning reuses the existing scroller and rows");
            Check(Math.Abs(rail.HorizontalOffset - saved) < 1, "returning restores the settled position");
        });
        yield return Case("hidden scroll viewers retain their settled position and content", async () =>
        {
            var content = new Border { Width = 180, Height = 2000 };
            var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            using var scene = new Scene(scroll);
            OverlayScrollChrome.SetEnabled(scroll, true); await Drain();
            scroll.ChangeView(null, 200, null, true); await Drain();
            double offset = scroll.VerticalOffset;
            scroll.Visibility = Visibility.Collapsed; await Drain();
            Check(scroll.IsLoaded && ReferenceEquals(content, scroll.Content), "hiding retains the viewer and content");
            scroll.Visibility = Visibility.Visible; scene.Layout(); await Drain();
            Check(Math.Abs(scroll.VerticalOffset - offset) < 1, "return preserves the settled offset");
            scroll.ChangeView(null, offset + 40, null, true); await Drain();
            Check(Math.Abs(scroll.VerticalOffset - offset - 40) < 1, "retained scrolling remains available");
        });
    }

    private static TestCase Case(string name, Func<Task> body) =>
        new("shared-presentation: " + name, () => TestUiHost.InvokeAsync(body));
    private static async Task Drain()
    {
        for (int index = 0; index < 4; index++) await DesktopTestUi.DrainAsync();
    }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private sealed class Scene : IDisposable
    {
        private readonly Grid root = new();
        private readonly DesktopTestUi.Host host;
        internal Scene(FrameworkElement view)
        {
            root.Children.Add(view);
            host = new(root, 720, 300);
        }
        internal void Layout() => root.UpdateLayout();
        public void Dispose() => host.Dispose();
    }
}
