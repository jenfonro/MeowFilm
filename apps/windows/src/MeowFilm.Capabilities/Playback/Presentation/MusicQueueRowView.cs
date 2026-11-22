using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;
using System.ComponentModel;
using Windows.UI.ViewManagement;

namespace MeowFilm.Capabilities;

public sealed class MusicQueueRowView : Grid
{
    private static readonly SolidColorBrush TransparentBrush = new(Microsoft.UI.Colors.Transparent);
    private MusicQueueSurfaceRow? observed;
    private IDisposable? themeObserver;
    public MusicQueueRowView()
    {
        Background = TransparentBrush;
        CornerRadius = new CornerRadius(6);
        Loaded += (_, _) =>
        {
            themeObserver ??= DesktopTheme.Observe(this, Refresh);
            Observe();
        };
        Unloaded += (_, _) =>
        {
            if (observed is not null) observed.PropertyChanged -= RowChanged;
            observed = null;
            themeObserver?.Dispose();
            themeObserver = null;
        };
        DataContextChanged += (_, _) => Observe();
        RegisterPropertyChangedCallback(CardMotion.ProgressProperty, (_, _) => Refresh());
    }
    private void Observe()
    {
        if (observed is not null) observed.PropertyChanged -= RowChanged;
        observed = IsLoaded ? DataContext as MusicQueueSurfaceRow : null;
        if (observed is not null) observed.PropertyChanged += RowChanged;
        Refresh();
    }
    private void RowChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MusicQueueSurfaceRow.IsCurrent)) Refresh();
    }
    private void Refresh()
    {
        bool highlighted = CardMotion.GetProgress(this) > .001;
        bool current = DataContext is MusicQueueSurfaceRow { IsCurrent: true };
        bool contrast = DesktopTheme.GetIsHighContrast(this);
        static Brush SystemBrush(UIElementType type) => SystemAppearance.GetElementBrush(type);
        Brush Resource(string name) => (Brush)Application.Current.Resources[name];
        var foreground = contrast ? SystemBrush(highlighted ? UIElementType.HighlightText : UIElementType.WindowText) :
            Resource("MeowFilm.Brush.Text");
        Background = contrast ? SystemBrush(highlighted ? UIElementType.Highlight : UIElementType.Window) :
            highlighted ? Resource("MeowFilm.Brush.ItemHover") : TransparentBrush;
        foreach (var node in Descendants(this))
        {
            if (node is TextBlock text)
                text.Foreground = contrast ? foreground : text.Name == "QueueTitle" ?
                    current ? Resource("MeowFilm.Brush.Accent") : foreground : Resource("MeowFilm.Brush.Muted");
            if (node is Button button)
            {
                button.Foreground = foreground;
                if (DataContext is MusicQueueSurfaceRow row && button.Tag is MusicQueueSurfaceRow)
                    AutomationProperties.SetItemStatus(button, row.IsCurrent ? "正在播放" : "");
            }
            if (node is Border { Name: "QueuePlayMark" } mark)
                mark.Opacity = highlighted || contrast && current ? 1 : 0;
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
