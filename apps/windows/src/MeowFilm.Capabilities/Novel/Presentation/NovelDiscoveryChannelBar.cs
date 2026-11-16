using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

internal sealed class NovelDiscoveryChannelBar : Grid
{
    private readonly RadioButton[] tabs;
    private readonly Button more;
    private readonly RotateTransform chevron = new();
    private readonly Canvas overlay = new() { Visibility = Visibility.Collapsed };
    private readonly Border expansion = new() { Padding = new(12, 0, 12, 0) };
    private readonly WrapPanel expandedChoices = new();
    private Window? owner;
    private ScrollViewer? pageScroll;
    private bool arranging;

    internal NovelDiscoveryChannelBar(IEnumerable<RadioButton> tabs)
    {
        this.tabs = tabs.ToArray();
        Height = 44; Margin = new(0, 0, 12, 0);
        AutomationProperties.SetName(this, "发现频道");
        AutomationProperties.SetAutomationId(this, "novel.discovery.channels");
        ColumnDefinitions.Add(new()); ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var row = new StackPanel { Orientation = Orientation.Horizontal, ClipToBounds = true };
        Children.Add(row);
        foreach (var tab in this.tabs)
        {
            row.Children.Add(tab);
            tab.Checked += (_, _) => ArrangeChannels();
            tab.Click += (_, _) => Close();
            tab.PreviewKeyDown += ChannelKeyDown;
        }
        more = NovelUi.Button("展开频道", Toggle);
        more.SetResourceReference(StyleProperty, "Novel.Discovery.Action");
        more.Margin = new(8, 0, 8, 0); more.Padding = new(0); more.Width = 30;
        more.VerticalAlignment = VerticalAlignment.Center; more.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(more, "novel.discovery.channels.expand");
        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M1,1 L6,6 11,1"), Width = 11, Height = 6, Stretch = Stretch.Fill,
            StrokeThickness = 1.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, RenderTransformOrigin = new(.5, .5), RenderTransform = chevron
        };
        icon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = more });
        more.Content = icon; Grid.SetColumn(more, 1); Children.Add(more);
        expansion.SetResourceReference(StyleProperty, "MeowFilm.FloatingSurface");
        AutomationProperties.SetName(expansion, "全部频道");
        AutomationProperties.SetAutomationId(expansion, "novel.discovery.channels.expanded");
        var scroll = new ScrollViewer
        {
            Content = expandedChoices, MaxHeight = 180, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        scroll.Resources.MergedDictionaries.Add(new()
            { Source = new Uri("/MeowFilm.Desktop;component/SettingsScrollChrome.xaml", UriKind.Relative) });
        scroll.SetResourceReference(StyleProperty, "MeowFilm.SettingsScrollViewer");
        expansion.Child = scroll; overlay.Children.Add(expansion); Canvas.SetTop(expansion, 48);
        Grid.SetColumnSpan(overlay, 2); Children.Add(overlay);
        Loaded += (_, _) => ArrangeChannels();
        SizeChanged += (_, _) => { Close(); ArrangeChannels(); };
        Unloaded += (_, _) => Close();
        IsVisibleChanged += (_, _) => { if (!IsVisible) Close(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && overlay.Visibility == Visibility.Visible)
            { Close(); more.Focus(); e.Handled = true; }
            else if (ReferenceEquals(e.OriginalSource, more) && e.Key == Key.Down)
            { if (overlay.Visibility != Visibility.Visible) Toggle(); e.Handled = true; }
        };
    }

    protected override Size MeasureOverride(Size available)
    {
        var desired = base.MeasureOverride(available);
        return new(Math.Min(available.Width, desired.Width), 44);
    }

    private void ArrangeChannels()
    {
        if (!IsLoaded || ActualWidth <= 0 || arranging) return;
        arranging = true;
        try
        {
            var widths = new double[tabs.Length];
            for (int i = 0; i < tabs.Length; i++)
            {
                tabs[i].ClearValue(MaxWidthProperty); tabs[i].Visibility = Visibility.Visible;
                tabs[i].Measure(new(double.PositiveInfinity, 44)); widths[i] = tabs[i].DesiredSize.Width;
            }
            bool overflow = widths.Sum() > ActualWidth;
            more.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
            double available = Math.Max(0, ActualWidth - (overflow ? more.Width + more.Margin.Left + more.Margin.Right : 0));
            var visible = new List<int>();
            double used = 0;
            for (int i = 0; i < tabs.Length && used + widths[i] <= available; i++)
            { visible.Add(i); used += widths[i]; }
            int selected = Array.FindIndex(tabs, tab => tab.IsChecked == true);
            if (selected >= 0 && !visible.Contains(selected))
            {
                while (visible.Count > 0 && used + widths[selected] > available)
                { used -= widths[visible[^1]]; visible.RemoveAt(visible.Count - 1); }
                visible.Add(selected);
            }
            for (int i = 0; i < tabs.Length; i++)
            {
                tabs[i].Visibility = visible.Contains(i) ? Visibility.Visible : Visibility.Collapsed;
                tabs[i].MaxWidth = Math.Max(0, available - tabs[i].Margin.Left - tabs[i].Margin.Right);
            }
            if (!overflow) Close();
        }
        finally { arranging = false; }
    }

    private void Toggle()
    {
        if (overlay.Visibility == Visibility.Visible) { Close(); return; }
        if (!IsVisible || more.Visibility != Visibility.Visible) return;
        expandedChoices.Children.Clear();
        foreach (var tab in tabs)
        {
            var choice = new RadioButton
            {
                Content = tab.Content, ToolTip = tab.Content, IsChecked = tab.IsChecked,
                GroupName = "novel.discovery.expanded-channels", MaxWidth = Math.Max(0, ActualWidth - 24)
            };
            choice.SetResourceReference(StyleProperty, "Novel.Discovery.Tab");
            AutomationProperties.SetName(choice, AutomationProperties.GetName(tab));
            AutomationProperties.SetAutomationId(choice, AutomationProperties.GetAutomationId(tab) + ".expanded");
            choice.Checked += (_, _) => { if (overlay.Visibility == Visibility.Visible) Select(tab); };
            choice.Click += (_, _) => { if (overlay.Visibility == Visibility.Visible) Select(tab); };
            choice.PreviewKeyDown += (_, e) => Navigate(tab, e);
            expandedChoices.Children.Add(choice);
        }
        expansion.Width = ActualWidth; overlay.Visibility = Visibility.Visible;
        chevron.Angle = 180; AutomationProperties.SetName(more, "收起频道");
        owner = Window.GetWindow(this);
        if (owner is not null)
        {
            owner.PreviewMouseDown += OutsidePressed; owner.Deactivated += OwnerChanged;
            owner.LocationChanged += OwnerChanged; owner.SizeChanged += OwnerResized;
        }
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null;
            parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer scroll) { pageScroll = scroll; scroll.ScrollChanged += PageScrolled; break; }
        expandedChoices.Children.OfType<RadioButton>().FirstOrDefault(choice => choice.IsChecked == true)?.Focus();
    }

    internal void Close()
    {
        overlay.Visibility = Visibility.Collapsed; chevron.Angle = 0;
        AutomationProperties.SetName(more, "展开频道");
        if (owner is not null)
        {
            owner.PreviewMouseDown -= OutsidePressed; owner.Deactivated -= OwnerChanged;
            owner.LocationChanged -= OwnerChanged; owner.SizeChanged -= OwnerResized; owner = null;
        }
        if (pageScroll is not null) { pageScroll.ScrollChanged -= PageScrolled; pageScroll = null; }
    }

    private void Select(RadioButton tab)
    {
        Close(); tab.Visibility = Visibility.Visible; tab.Focus(); tab.IsChecked = true; ArrangeChannels();
    }
    private void ChannelKeyDown(object sender, KeyEventArgs e) => Navigate((RadioButton)sender, e);
    private void Navigate(RadioButton tab, KeyEventArgs e)
    {
        if (tabs.Length == 0 || e.Key is not (Key.Home or Key.End or Key.Left or Key.Right)) return;
        int index = Array.IndexOf(tabs, tab);
        int next = e.Key switch { Key.Home => 0, Key.End => tabs.Length - 1,
            Key.Left => (index + tabs.Length - 1) % tabs.Length, _ => (index + 1) % tabs.Length };
        Select(tabs[next]); e.Handled = true;
    }
    private void OutsidePressed(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject target || !IsAncestorOf(target)) Close();
    }
    private void OwnerChanged(object? sender, EventArgs e) => Close();
    private void OwnerResized(object sender, SizeChangedEventArgs e) => Close();
    private void PageScrolled(object sender, ScrollChangedEventArgs e)
    { if (ReferenceEquals(e.OriginalSource, pageScroll) && (e.VerticalChange != 0 || e.HorizontalChange != 0)) Close(); }
}
