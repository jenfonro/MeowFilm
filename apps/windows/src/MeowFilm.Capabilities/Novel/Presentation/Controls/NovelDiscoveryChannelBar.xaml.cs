using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace MeowFilm.Capabilities;

public sealed class NovelChannelItem : INotifyPropertyChanged
{
    private bool isSelected;
    private Visibility barVisibility = Visibility.Visible;
    private double maxItemWidth = double.PositiveInfinity;
    private double expandedMaxWidth = 240;

    public string Title { get; }
    public string AutomationId { get; }
    public string ExpandedAutomationId => AutomationId + ".expanded";
    public NovelKind PrimaryKind { get; }
    public IReadOnlyList<NovelKind> Kinds { get; }

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value) return;
            isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public Visibility BarVisibility
    {
        get => barVisibility;
        set
        {
            if (barVisibility == value) return;
            barVisibility = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BarVisibility)));
        }
    }

    public double MaxItemWidth
    {
        get => maxItemWidth;
        set
        {
            if (Math.Abs(maxItemWidth - value) < 0.5) return;
            maxItemWidth = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MaxItemWidth)));
        }
    }

    public double ExpandedMaxWidth
    {
        get => expandedMaxWidth;
        set
        {
            if (Math.Abs(expandedMaxWidth - value) < 0.5) return;
            expandedMaxWidth = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedMaxWidth)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public NovelChannelItem(string title, string automationId, NovelKind primaryKind, IReadOnlyList<NovelKind> kinds, bool isSelected)
    {
        Title = title;
        AutomationId = automationId;
        PrimaryKind = primaryKind;
        Kinds = kinds;
        this.isSelected = isSelected;
    }
}

public sealed partial class NovelDiscoveryChannelBar : UserControl
{
    private readonly ObservableCollection<NovelChannelItem> channels = [];
    private readonly RectangleGeometry barClip = new();
    private readonly NovelPresentation.VisibilityWatch visibility;
    private bool arranging;

    public event EventHandler<NovelChannelItem>? ChannelSelected;

    public NovelDiscoveryChannelBar()
    {
        InitializeComponent();
        BarItems.ItemsSource = channels;
        ExpandedItems.ItemsSource = channels;
        BarItems.Clip = barClip;
        BarItems.SizeChanged += (_, _) =>
        {
            barClip.Rect = new Rect(0, 0, BarItems.ActualWidth, BarItems.ActualHeight);
            ArrangeChannels();
        };
        Loaded += (_, _) => ArrangeChannels();
        SizeChanged += (_, _) => { Close(); ArrangeChannels(); };
        Unloaded += (_, _) => Close();
        visibility = new NovelPresentation.VisibilityWatch(this, visible => { if (!visible) Close(); });
    }

    public void SetChannels(IReadOnlyList<NovelChannelItem> items)
    {
        Close();
        channels.Clear();
        foreach (var item in items) channels.Add(item);
        DispatcherQueue?.TryEnqueue(ArrangeChannels);
    }

    public void UpdateSelection(string? kindUrl)
    {
        bool changed = false;
        foreach (var item in channels)
        {
            bool selected = !string.IsNullOrEmpty(kindUrl) && item.Kinds.Any(k => k.Url == kindUrl);
            if (item.IsSelected != selected)
            {
                item.IsSelected = selected;
                changed = true;
            }
        }
        if (changed) DispatcherQueue?.TryEnqueue(ArrangeChannels);
    }

    public void Close()
    {
        if (ExpandedFlyout.IsOpen) ExpandedFlyout.Hide();
    }

    private void ArrangeChannels()
    {
        if (!IsLoaded || ActualWidth <= 0 || arranging) return;
        arranging = true;
        try
        {
            int count = channels.Count;
            if (count == 0)
            {
                ExpandButton.Visibility = Visibility.Collapsed;
                Close();
                return;
            }

            var widths = new double[count];
            for (int i = 0; i < count; i++)
            {
                channels[i].MaxItemWidth = double.PositiveInfinity;
                channels[i].BarVisibility = Visibility.Visible;
                if (BarItems.ContainerFromIndex(i) is FrameworkElement container)
                {
                    container.Measure(new Size(double.PositiveInfinity, 44));
                    widths[i] = container.DesiredSize.Width;
                }
                else
                {
                    widths[i] = Math.Max(48, channels[i].Title.Length * 14 + 28);
                }
            }

            bool overflow = widths.Sum() > ActualWidth;
            ExpandButton.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
            double expandWidth = overflow ? ExpandButton.Width + ExpandButton.Margin.Left + ExpandButton.Margin.Right : 0;
            double available = Math.Max(0, ActualWidth - expandWidth);

            var visible = new List<int>();
            double used = 0;
            for (int i = 0; i < count && used + widths[i] <= available; i++)
            {
                visible.Add(i);
                used += widths[i];
            }

            int selected = -1;
            for (int i = 0; i < count; i++)
                if (channels[i].IsSelected) { selected = i; break; }

            if (selected >= 0 && !visible.Contains(selected))
            {
                while (visible.Count > 0 && used + widths[selected] > available)
                {
                    used -= widths[visible[^1]];
                    visible.RemoveAt(visible.Count - 1);
                }
                visible.Add(selected);
            }

            double itemMaxWidth = Math.Max(40, available - 28);
            double expandedMaxWidth = Math.Max(120, ActualWidth - 36);
            for (int i = 0; i < count; i++)
            {
                channels[i].BarVisibility = visible.Contains(i) ? Visibility.Visible : Visibility.Collapsed;
                channels[i].MaxItemWidth = itemMaxWidth;
                channels[i].ExpandedMaxWidth = expandedMaxWidth;
            }

            if (!overflow) Close();
        }
        finally
        {
            arranging = false;
        }
    }

    private void SelectItem(NovelChannelItem item)
    {
        Close();
        ChannelSelected?.Invoke(this, item);
    }

    private void ChannelTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NovelChannelItem item })
            SelectItem(item);
    }

    private void ExpandedChannel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NovelChannelItem item })
            SelectItem(item);
    }

    private void ChannelTab_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (channels.Count == 0 || sender is not FrameworkElement { DataContext: NovelChannelItem current }) return;
        if (e.Key is not (VirtualKey.Home or VirtualKey.End or VirtualKey.Left or VirtualKey.Right)) return;
        int index = channels.IndexOf(current);
        if (index < 0) return;
        int next = e.Key switch
        {
            VirtualKey.Home => 0,
            VirtualKey.End => channels.Count - 1,
            VirtualKey.Left => (index + channels.Count - 1) % channels.Count,
            _ => (index + 1) % channels.Count
        };
        SelectItem(channels[next]);
        e.Handled = true;
    }

    private void ExpandButton_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Down && !ExpandedFlyout.IsOpen && ExpandButton.Visibility == Visibility.Visible)
        {
            ExpandedFlyout.ShowAt(ExpandButton);
            e.Handled = true;
        }
    }

    private void ExpandedFlyout_Opening(object? sender, object e)
    {
        ExpandedScroll.Width = Math.Max(180, ActualWidth - 24);
        ChevronRotate.Angle = 180;
        AutomationProperties.SetName(ExpandButton, "收起频道");
    }

    private void ExpandedFlyout_Closed(object? sender, object e)
    {
        ChevronRotate.Angle = 0;
        AutomationProperties.SetName(ExpandButton, "展开频道");
    }
}
