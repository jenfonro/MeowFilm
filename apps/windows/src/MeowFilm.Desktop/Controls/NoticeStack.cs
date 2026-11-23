using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Desktop;

/// <summary>Per-window, bounded notifications inside the common content composition.</summary>
public sealed class NoticeStack : StackPanel
{
    private sealed class Item(object source, string message, Border card, DispatcherTimer? timer, bool persistent)
    {
        public object Source { get; } = source;
        public string Message { get; } = message;
        public Border Card { get; } = card;
        public DispatcherTimer? Timer { get; } = timer;
        public bool Persistent { get; } = persistent;
        public bool Hovered { get; set; }
    }
    private readonly List<Item> items = [];
    public NoticeStack()
    {
        MaxWidth = 460; HorizontalAlignment = HorizontalAlignment.Right; VerticalAlignment = VerticalAlignment.Bottom;
        Unloaded += (_, _) => { if (!IsLoaded) Clear(); };
    }
    public void Show(object source, string message, bool persistent = false)
    {
        if (items.Any(item => ReferenceEquals(item.Source, source) && item.Message == message)) return;
        foreach (var old in items.Where(item => ReferenceEquals(item.Source, source) || !persistent && !item.Persistent && item.Message == message).ToArray()) Remove(old);
        if (string.IsNullOrWhiteSpace(message)) return;
        while (!persistent && items.Count(item => !item.Persistent) >= 3) Remove(items.First(item => !item.Persistent));
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        text.UseResource(TextBlock.ForegroundProperty, "MeowFilm.Brush.Text");
        AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
        var close = new Button { Content = "\uE711", Visibility = persistent ? Visibility.Collapsed : Visibility.Visible };
        close.UseResource(StyleProperty, "MeowFilm.IconButton");
        close.Width = close.Height = close.MinWidth = close.MinHeight = 30;
        close.Padding = new(0);
        close.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(close, "关闭通知");
        var layout = new Grid { ColumnSpacing = 12 };
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumn(text, 0);
        Grid.SetColumn(close, 1);
        layout.Children.Add(text);
        layout.Children.Add(close);
        var card = new Border
        {
            Child = layout,
            Padding = new(16, 10, 10, 10),
            Margin = new(0, 0, 0, 8),
            CornerRadius = new(10),
            BorderThickness = new(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            Shadow = new ThemeShadow(),
            Translation = new Vector3(0, 0, 16)
        };
        card.UseResource(Border.BackgroundProperty, "MeowFilm.Brush.FloatingSurface");
        card.UseResource(Border.BorderBrushProperty, "MeowFilm.Brush.Border");
        var timer = persistent ? null : new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        var item = new Item(source, message, card, timer, persistent);
        if (timer is not null)
        {
            timer.Tick += (_, _) => { if (!item.Hovered && !DesktopVisual.ContainsFocus(card)) Remove(item); };
            card.PointerEntered += (_, _) => item.Hovered = true;
            card.PointerExited += (_, _) => item.Hovered = false;
        }
        close.Click += (_, _) => Remove(item);
        items.Add(item); Children.Add(card); timer?.Start();
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            FrameworkElementAutomationPeer.CreatePeerForElement(text)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
    private void Remove(Item item) { item.Timer?.Stop(); items.Remove(item); Children.Remove(item.Card); }
    internal void Clear() { foreach (var item in items.ToArray()) Remove(item); }
}
