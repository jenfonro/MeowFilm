using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed class MusicCatalogButton : Button
{
    public static readonly DependencyProperty ChannelKeyProperty = DependencyProperty.Register(nameof(ChannelKey), typeof(string),
        typeof(MusicCatalogButton), new PropertyMetadata("", (owner, _) => ((MusicCatalogButton)owner).Refresh()));
    public static readonly DependencyProperty IsTabProperty = DependencyProperty.Register(nameof(IsTab), typeof(bool),
        typeof(MusicCatalogButton), new PropertyMetadata(false, (owner, _) => ((MusicCatalogButton)owner).Refresh()));
    public string ChannelKey { get => (string)GetValue(ChannelKeyProperty); set => SetValue(ChannelKeyProperty, value); }
    public bool IsTab { get => (bool)GetValue(IsTabProperty); set => SetValue(IsTabProperty, value); }
    private bool hovered;

    public MusicCatalogButton()
    {
        DataContextChanged += (_, _) => Refresh();
        PointerEntered += (_, _) => { hovered = true; Refresh(); };
        PointerExited += (_, _) => { hovered = false; Refresh(); };
        Unloaded += (_, _) => hovered = false;
        DesktopTheme.Observe(this, Refresh);
    }
    private void Refresh()
    {
        if (!IsLoaded) return;
        bool selected = DataContext switch
        {
            MusicCatalogCard card => ChannelKey == card.Platform + "/" + Uri.EscapeDataString(card.ActionId),
            MusicCatalogTag tag => tag.Selected,
            _ => false
        };
        Brush Resource(string name) => (Brush)DesktopVisual.Resource(this, "MeowFilm.Brush." + name);
        Foreground = Resource(selected ? IsTab ? "Text" : "Accent" : hovered ? "Text" : "Muted");
        BorderBrush = selected ? Resource("Accent") : IsTab ? MusicVisuals.Brush("#00000000") : Resource("Border");
        Background = IsTab ? MusicVisuals.Brush("#00000000") : Resource(selected ? "AccentSoft" : hovered ? "Hover" : "InputSurface");
        AutomationProperties.SetItemStatus(this, selected ? "已选中" : "");
    }
}
