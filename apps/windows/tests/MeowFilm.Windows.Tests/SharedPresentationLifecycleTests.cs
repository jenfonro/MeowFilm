using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
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
            entry.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            var surface = (Grid)entry.FindName("Surface");
            CardMotion.Transition(surface, true, animate: true);
            entry.Visibility = Visibility.Collapsed;
            Check(entry.IsLoaded, "hiding retains the loaded card");
            Check(card.HasPreview, "hiding retains page-owned songs");
            Check(CardMotion.GetProgress(surface) == 0 && !surface.HasAnimatedProperties,
                "hidden card owns no hover animation clock");
            entry.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
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
                CardTemplate = (DataTemplate)XamlReader.Parse("""
                    <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                      <Border Height="120"><TextBlock Text="{Binding Title}"/></Border>
                    </DataTemplate>
                    """)
            };
            using var scene = new Scene(shelf);
            await Drain();
            var list = (ListBox)shelf.FindName("ItemsHost");
            var source = list.ItemsSource;
            var rail = shelf.ScrollHost ?? throw new InvalidOperationException("shelf scroller not realized");
            Check(rail.ScrollableWidth > 0, "fixture has real scrollable content");
            ((Button)shelf.FindName("Next")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            shelf.Visibility = Visibility.Collapsed;
            double saved = section.HorizontalOffset;
            Check(shelf.IsLoaded && ReferenceEquals(source, list.ItemsSource) && list.Items.Count == 24,
                "hiding does not detach rows or the shelf");
            Check(!shelf.HasAnimatedProperties, "hiding immediately stops the scrolling animation");
            ((Button)shelf.FindName("Next")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(section.HorizontalOffset == saved && !shelf.HasAnimatedProperties,
                "hidden page commands cannot restart movement");
            shelf.Visibility = Visibility.Visible;
            scene.Layout(); await Drain();
            Check(ReferenceEquals(rail, shelf.ScrollHost) && ReferenceEquals(source, list.ItemsSource),
                "returning reuses the existing scroller and rows");
            Check(Math.Abs(rail.HorizontalOffset - saved) < 1, "returning restores the settled position");
        });
        yield return Case("hidden scrollbar drops input activity and returns without stale intent", async () =>
        {
            var scroll = new ScrollViewer
            {
                Content = new Border { Width = 180, Height = 2000, Background = Brushes.White },
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            using var scene = new Scene(scroll);
            OverlayScrollChrome.SetEnabled(scroll, true);
            await Drain();
            ScrollWithIntent(scroll);
            await Drain();
            Check(OverlayScrollChrome.GetActive(scroll), "visible user scroll activates chrome");
            double offset = scroll.VerticalOffset;
            scroll.Visibility = Visibility.Collapsed;
            Check(scroll.IsLoaded && !OverlayScrollChrome.GetActive(scroll),
                "hiding retains the viewer but resets its activity");
            scroll.Visibility = Visibility.Visible;
            scene.Layout(); await Drain();
            Check(Math.Abs(scroll.VerticalOffset - offset) < 1 &&
                !OverlayScrollChrome.GetActive(scroll), "return preserves offset without restoring old input intent");
            scroll.ScrollToVerticalOffset(offset + 40);
            await Drain();
            Check(!OverlayScrollChrome.GetActive(scroll), "a programmatic move after return stays inactive");
            ScrollWithIntent(scroll);
            await Drain();
            Check(OverlayScrollChrome.GetActive(scroll), "a new user gesture works after return");
        });
    }

    private static void ScrollWithIntent(ScrollViewer scroll)
    {
        scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120)
            { RoutedEvent = Mouse.PreviewMouseWheelEvent });
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 80);
    }
    private static TestCase Case(string name, Func<Task> body) =>
        new("shared-presentation: " + name, () => MusicQualityConsumerTests.Sta(body));
    private static async Task Drain()
    {
        for (int index = 0; index < 4; index++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private sealed class Scene : IDisposable
    {
        private readonly HwndSource source;
        private readonly Grid root = new();
        internal Scene(FrameworkElement view)
        {
            root.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("/MeowFilm.Desktop;component/Theme.xaml", UriKind.Relative) });
            root.Children.Add(view);
            source = new HwndSource(new HwndSourceParameters("retained-presentation-fixture")
            {
                WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080,
                PositionX = -32000, PositionY = -32000, Width = 720, Height = 300
            });
            source.RootVisual = root;
            Layout();
        }
        internal void Layout()
        {
            var size = new Size(720, 300);
            root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
        }
        public void Dispose() { source.RootVisual = null; source.Dispose(); }
    }
}
