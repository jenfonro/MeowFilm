using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed class MusicCatalogButton : Button
{
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(nameof(IsSelected), typeof(bool),
        typeof(MusicCatalogButton), new PropertyMetadata(false, (owner, _) => ((MusicCatalogButton)owner).Refresh()));
    public static readonly DependencyProperty IsTabProperty = DependencyProperty.Register(nameof(IsTab), typeof(bool),
        typeof(MusicCatalogButton), new PropertyMetadata(false, (owner, _) => ((MusicCatalogButton)owner).Refresh()));
    public bool IsSelected { get => (bool)GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
    public bool IsTab { get => (bool)GetValue(IsTabProperty); set => SetValue(IsTabProperty, value); }
    [ThreadStatic] private static SolidColorBrush? transparentBrush;
    private bool hovered;

    public MusicCatalogButton()
    {
        PointerEntered += (_, _) => { hovered = true; Refresh(); };
        PointerExited += (_, _) => { hovered = false; Refresh(); };
        Unloaded += (_, _) => hovered = false;
        DesktopTheme.Observe(this, Refresh);
    }
    private void Refresh()
    {
        if (!IsLoaded) return;
        bool selected = IsSelected;
        Brush Resource(string name) => (Brush)DesktopVisual.Resource(this, "MeowFilm.Brush." + name);
        var transparent = transparentBrush ??= DesktopVisual.Brush("#00000000");
        Foreground = Resource(selected ? IsTab ? "Text" : "Accent" : hovered ? "Text" : "Muted");
        BorderBrush = selected ? Resource("Accent") : IsTab ? transparent : Resource("Border");
        Background = IsTab ? transparent : Resource(selected ? "AccentSoft" : hovered ? "Hover" : "InputSurface");
        AutomationProperties.SetItemStatus(this, selected ? "已选中" : "");
    }
}
