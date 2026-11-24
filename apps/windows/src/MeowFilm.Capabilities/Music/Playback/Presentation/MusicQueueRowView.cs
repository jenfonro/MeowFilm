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
    private MusicQueueSurfaceRow? observed;
    private IDisposable? themeObserver;
    private Button? playButton, removeButton;
    private Border? playMark;
    private TextBlock? titleText, artistText, durationText;
    private bool partsResolved;
    private bool pointerOver;
    private bool focusWithin;

    public MusicQueueRowView()
    {
        Background = DesktopVisual.TransparentBrush;
        CornerRadius = new CornerRadius(6);
        PointerEntered += (_, _) => SetPointerOver(true);
        PointerExited += (_, _) => SetPointerOver(false);
        PointerCaptureLost += (_, _) => SetPointerOver(false);
        GotFocus += (_, _) => SetFocusWithin(true);
        LostFocus += (_, _) => SetFocusWithin(false);
        Loaded += (_, _) =>
        {
            bool hadObserver = themeObserver is not null;
            Observe(refresh: hadObserver);
            themeObserver ??= DesktopTheme.Observe(this, Refresh);
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            pointerOver = false;
            focusWithin = false;
            if (observed is not null) observed.PropertyChanged -= RowChanged;
            observed = null;
            themeObserver?.Dispose();
            themeObserver = null;
        };
        DataContextChanged += (_, _) =>
        {
            pointerOver = false;
            focusWithin = false;
            Observe(refresh: true);
        };
    }

    private void SetPointerOver(bool value)
    {
        if (pointerOver == value) return;
        pointerOver = value;
        Refresh();
    }

    private void SetFocusWithin(bool value)
    {
        if (focusWithin == value) return;
        focusWithin = value;
        Refresh();
    }

    private void Observe(bool refresh)
    {
        if (observed is not null) observed.PropertyChanged -= RowChanged;
        observed = IsLoaded ? DataContext as MusicQueueSurfaceRow : null;
        if (observed is not null) observed.PropertyChanged += RowChanged;
        if (refresh) Refresh();
    }

    private void RowChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MusicQueueSurfaceRow.IsCurrent)) Refresh();
    }

    private void EnsureParts()
    {
        if (partsResolved) return;
        playButton = FindName("QueuePlayButton") as Button;
        removeButton = FindName("QueueRemoveButton") as Button;
        playMark = FindName("QueuePlayMark") as Border;
        titleText = FindName("QueueTitle") as TextBlock;
        artistText = FindName("QueueArtist") as TextBlock;
        durationText = FindName("QueueDuration") as TextBlock;
        partsResolved = playButton is not null || removeButton is not null || titleText is not null;
    }

    private void Refresh()
    {
        EnsureParts();
        bool highlighted = pointerOver || focusWithin;
        var row = DataContext as MusicQueueSurfaceRow;
        bool current = row is { IsCurrent: true };
        bool contrast = DesktopTheme.GetIsHighContrast(this);
        static Brush SystemBrush(UIElementType type) => SystemAppearance.GetElementBrush(type);
        Brush Resource(string name) => (Brush)DesktopVisual.Resource(this, name);
        var foreground = contrast ? SystemBrush(highlighted ? UIElementType.HighlightText : UIElementType.WindowText) :
            Resource("MeowFilm.Brush.Text");
        var muted = contrast ? foreground : Resource("MeowFilm.Brush.Muted");
        Background = contrast ? SystemBrush(highlighted ? UIElementType.Highlight : UIElementType.Window) :
            highlighted ? Resource("MeowFilm.Brush.ItemHover") : DesktopVisual.TransparentBrush;
        if (titleText is not null)
            titleText.Foreground = contrast ? foreground : current ? Resource("MeowFilm.Brush.Accent") : foreground;
        if (artistText is not null) artistText.Foreground = muted;
        if (durationText is not null) durationText.Foreground = muted;
        if (playButton is not null)
        {
            playButton.Foreground = foreground;
            if (row is not null) AutomationProperties.SetItemStatus(playButton, row.IsCurrent ? "正在播放" : "");
        }
        if (removeButton is not null)
        {
            removeButton.Foreground = foreground;
            removeButton.Opacity = highlighted ? 1 : 0;
            removeButton.IsHitTestVisible = highlighted;
        }
        if (playMark is not null)
            playMark.Opacity = highlighted || contrast && current ? 1 : 0;
    }
}
