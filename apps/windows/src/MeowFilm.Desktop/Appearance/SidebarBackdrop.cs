using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Desktop;

/// <summary>
/// Lightweight static sidebar backdrop mirroring the application background without
/// allocating a full-window offscreen GaussianBlurEffect CompositionVisualSurface on resize/drag.
/// </summary>
public sealed class SidebarBackdrop : Grid
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(FrameworkElement), typeof(SidebarBackdrop), new PropertyMetadata(null, Changed));
    public FrameworkElement? Source { get => (FrameworkElement?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    private Border? observedSource;
    private long backgroundCallback;
    public SidebarBackdrop()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => ObserveSource();
        Unloaded += (_, _) => { if (!IsLoaded) ObserveSource(null); };
    }
    private static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var backdrop = (SidebarBackdrop)owner;
        backdrop.ObserveSource();
        backdrop.Refresh();
    }
    private void ObserveSource() => ObserveSource(IsLoaded ? Source as Border : null);
    private void ObserveSource(Border? source)
    {
        if (ReferenceEquals(observedSource, source)) return;
        if (observedSource is { } previous)
        {
            previous.UnregisterPropertyChangedCallback(Border.BackgroundProperty, backgroundCallback);
            backgroundCallback = 0;
        }
        observedSource = source;
        if (source is not null)
        {
            backgroundCallback = source.RegisterPropertyChangedCallback(Border.BackgroundProperty, (_, _) => Refresh());
        }
        Refresh();
    }
    private void Refresh()
    {
        Background = observedSource?.Background ?? new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }
}
