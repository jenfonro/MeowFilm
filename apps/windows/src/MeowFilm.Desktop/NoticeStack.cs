using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace MeowFilm.Desktop;

/// <summary>Per-window, bounded native notifications. No global bus or navigation dependency.</summary>
public sealed class NoticeStack : StackPanel
{
    private sealed record Item(object Source, string Message, Border Card, TextBlock Text, DispatcherTimer Timer);
    private readonly List<Item> items = [];
    public NoticeStack()
    {
        MaxWidth = 480;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        Unloaded += (_, _) => Clear();
    }
    public void Show(object source, string message)
    {
        if (items.Any(x => ReferenceEquals(x.Source, source) && x.Message == message)) return;
        foreach (var old in items.Where(x => ReferenceEquals(x.Source, source) || x.Message == message).ToArray()) Remove(old);
        if (string.IsNullOrWhiteSpace(message)) return;
        while (items.Count >= 3) Remove(items[0]);
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 0) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "MeowFilm.Brush.Text");
        AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
        var close = new Button { Content = "\uE711", Width = 30, Height = 30 };
        close.SetResourceReference(StyleProperty, "MeowFilm.IconButton");
        AutomationProperties.SetName(close, "关闭通知");
        DockPanel.SetDock(close, Dock.Right);
        var layout = new DockPanel(); layout.Children.Add(close); layout.Children.Add(text);
        var card = new Border
        {
            Child = layout, Padding = new(16, 10, 10, 10), Margin = new(0, 0, 0, 8),
            CornerRadius = new(10), BorderThickness = new(1),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = .16 }
        };
        card.SetResourceReference(Border.BackgroundProperty, "MeowFilm.Brush.Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "MeowFilm.Brush.Border");
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(8) };
        var item = new Item(source, message, card, text, timer);
        timer.Tick += (_, _) => { if (!card.IsMouseOver && !card.IsKeyboardFocusWithin) Remove(item); };
        close.Click += (_, _) => Remove(item);
        items.Add(item); Children.Add(card); timer.Start();
        ApplyContrast(item);
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            UIElementAutomationPeer.CreatePeerForElement(text)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == DesktopTheme.IsHighContrastProperty)
            foreach (var item in items) ApplyContrast(item);
    }
    private void ApplyContrast(Item item)
    {
        if (DesktopTheme.GetIsHighContrast(this))
        {
            item.Card.Background = SystemColors.WindowBrush;
            item.Card.BorderBrush = SystemColors.WindowTextBrush;
            item.Card.Effect = null;
            item.Text.Foreground = SystemColors.WindowTextBrush;
        }
        else
        {
            item.Card.SetResourceReference(Border.BackgroundProperty, "MeowFilm.Brush.Surface");
            item.Card.SetResourceReference(Border.BorderBrushProperty, "MeowFilm.Brush.Border");
            item.Text.SetResourceReference(TextBlock.ForegroundProperty, "MeowFilm.Brush.Text");
            item.Card.Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = .16 };
        }
    }
    private void Remove(Item item) { item.Timer.Stop(); items.Remove(item); Children.Remove(item.Card); }
    internal void Clear() { foreach (var item in items.ToArray()) Remove(item); }
}
