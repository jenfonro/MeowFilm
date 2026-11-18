using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;
using Windows.UI.ViewManagement;

namespace MeowFilm.Capabilities;

public sealed class MusicQueueRowView : Grid
{
    public MusicQueueRowView()
    {
        Loaded += (_, _) => Refresh();
        DataContextChanged += (_, _) => Refresh();
        ActualThemeChanged += (_, _) => Refresh();
        RegisterPropertyChangedCallback(CardMotion.ProgressProperty, (_, _) => Refresh());
        RegisterPropertyChangedCallback(DesktopTheme.IsHighContrastProperty, (_, _) => Refresh());
    }
    private void Refresh()
    {
        bool highlighted = CardMotion.GetProgress(this) > .001;
        bool current = DataContext is MusicQueueSurfaceRow { IsCurrent: true };
        bool contrast = DesktopTheme.GetIsHighContrast(this);
        var settings = new UISettings();
        Brush SystemBrush(UIElementType type) => new SolidColorBrush(settings.UIElementColor(type));
        Brush Resource(string name) => (Brush)Application.Current.Resources[name];
        var foreground = contrast ? SystemBrush(highlighted ? UIElementType.HighlightText : UIElementType.WindowText) :
            Resource("MeowFilm.Brush.Text");
        Background = contrast ? SystemBrush(highlighted ? UIElementType.Highlight : UIElementType.Window) :
            highlighted ? Resource("MeowFilm.Brush.ItemHover") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
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
