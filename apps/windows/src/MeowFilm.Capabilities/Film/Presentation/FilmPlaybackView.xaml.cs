using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MeowFilm.Capabilities;

public partial class FilmPlaybackView : UserControl
{
    private const double MetadataFade = 20;
    private (bool Top, bool Bottom, double Height) metadataFade;

    public event EventHandler<FilmCardActivatedEventArgs>? RecommendationRequested;

    public FilmPlaybackView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            PageScroll.ScrollToTop();
            HorizontalEpisodes.ScrollToLeftEnd();
            RelatedScroll.ScrollToLeftEnd();
        };
        Loaded += (_, _) => UpdateLayoutMode();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateLayoutMode();

    private void UpdateLayoutMode()
    {
        bool stacked = ActualWidth < 840;
        PageContent.Margin = new(stacked ? 24 : 32, 12, stacked ? 24 : 32, 32);
        PlayerColumn.Width = new(1.85, GridUnitType.Star);
        HeroGutter.Width = new(stacked ? 0 : 24);
        MetadataColumn.Width = stacked ? new(0) : new(1, GridUnitType.Star);
        HeroRowGutter.Height = new(stacked ? 20 : 0);
        Grid.SetColumn(MetadataPanel, stacked ? 0 : 2);
        Grid.SetRow(MetadataPanel, stacked ? 2 : 0);
        UpdateMetadataHeight();
        UpdateRailButtons();
    }

    private void Player_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateMetadataHeight();

    private void UpdateMetadataHeight()
    {
        if (MetadataPanel is not null)
            MetadataPanel.Height = ActualWidth < 840 ? double.NaN : PlayerFrame.ActualHeight;
    }

    // Fade only the edges where metadata continues beyond the panel.
    private void Metadata_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var scroll = MetadataScroll;
        (bool Top, bool Bottom, double Height) fade = (scroll.VerticalOffset > .5,
            scroll.ScrollableHeight - scroll.VerticalOffset > .5, scroll.ActualHeight);
        if (fade == metadataFade) return;
        metadataFade = fade;
        if ((!fade.Top && !fade.Bottom) || fade.Height < MetadataFade * 2)
        {
            scroll.ClearValue(OpacityMaskProperty);
            return;
        }
        var clear = Color.FromArgb(0, 0, 0, 0);
        double edge = MetadataFade / fade.Height;
        var mask = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute, StartPoint = new(0, 0), EndPoint = new(0, fade.Height)
        };
        mask.GradientStops.Add(new(fade.Top ? clear : Colors.Black, 0));
        mask.GradientStops.Add(new(Colors.Black, edge));
        mask.GradientStops.Add(new(Colors.Black, 1 - edge));
        mask.GradientStops.Add(new(fade.Bottom ? clear : Colors.Black, 1));
        mask.Freeze();
        scroll.OpacityMask = mask;
    }

    private void Episode_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is FilmPlaybackPage page && sender is Button { Tag: FilmEpisodeItem episode })
            page.SelectEpisode(episode);
    }

    private void HorizontalLayout_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not FilmPlaybackPage page) return;
        page.SetListLayout(false);
        // Return to the selected episode, including one chosen near the end of the list.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!IsVisible || !ReferenceEquals(DataContext, page)) return;
            int index = page.Episodes.ToList().IndexOf(page.SelectedEpisode);
            HorizontalEpisodes.ScrollToHorizontalOffset(index * 276);
        }));
    }

    private void ListLayout_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is FilmPlaybackPage page) page.SetListLayout(true);
    }

    private void Overview_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is FilmPlaybackPage page) page.ToggleOverview();
    }

    private void Recommendation_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: FilmCardItem item })
            RecommendationRequested?.Invoke(this, new(item));
    }

    private void EpisodePrevious_Click(object sender, RoutedEventArgs e) => PageRail(HorizontalEpisodes, -1, e);
    private void EpisodeNext_Click(object sender, RoutedEventArgs e) => PageRail(HorizontalEpisodes, 1, e);
    private void RelatedPrevious_Click(object sender, RoutedEventArgs e) => PageRail(RelatedScroll, -1, e);
    private void RelatedNext_Click(object sender, RoutedEventArgs e) => PageRail(RelatedScroll, 1, e);

    private static void PageRail(ScrollViewer rail, int direction, RoutedEventArgs e)
    {
        e.Handled = true;
        rail.ScrollToHorizontalOffset(rail.HorizontalOffset + direction * rail.ViewportWidth * .85);
    }

    private void Rail_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateRailButtons();

    private void UpdateRailButtons()
    {
        if (EpisodePrevious is null || RelatedNext is null) return;
        EpisodePrevious.IsEnabled = HorizontalEpisodes.HorizontalOffset > .5;
        EpisodeNext.IsEnabled = HorizontalEpisodes.ScrollableWidth - HorizontalEpisodes.HorizontalOffset > .5;
        RelatedPrevious.IsEnabled = RelatedScroll.HorizontalOffset > .5;
        RelatedNext.IsEnabled = RelatedScroll.ScrollableWidth - RelatedScroll.HorizontalOffset > .5;
    }

    private void Rail_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer rail) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            rail.ScrollToHorizontalOffset(rail.HorizontalOffset - e.Delta * 2);
        else
            PageScroll.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = Mouse.MouseWheelEvent,
                Source = PageScroll
            });
        e.Handled = true;
    }

    private void Rail_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ScrollViewer rail || e.KeyboardDevice.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.PageUp: PageRail(rail, -1, e); break;
            case Key.PageDown: PageRail(rail, 1, e); break;
            case Key.Home: rail.ScrollToLeftEnd(); e.Handled = true; break;
            case Key.End: rail.ScrollToRightEnd(); e.Handled = true; break;
        }
    }
}
