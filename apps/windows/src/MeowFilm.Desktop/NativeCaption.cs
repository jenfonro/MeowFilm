using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace MeowFilm.Desktop;

/// <summary>Native caption buttons/Snap and drag regions of the one application window.</summary>
internal sealed class NativeCaption : IDisposable
{
    private readonly DesktopWindow window;
    private readonly FrameworkElement titleBar;
    private readonly FrameworkElement[] controls;
    private readonly InputNonClientPointerSource input;
    internal NativeCaption(DesktopWindow window, FrameworkElement titleBar, params FrameworkElement[] controls)
    {
        this.window = window; this.titleBar = titleBar; this.controls = controls;
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(titleBar);
        input = InputNonClientPointerSource.GetForWindowId(window.AppWindow.Id);
        titleBar.SizeChanged += Changed;
        titleBar.Loaded += Loaded;
        foreach (var control in controls) control.LayoutUpdated += Layout;
        window.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        Update();
    }
    private void Loaded(object sender, RoutedEventArgs args) => Update();
    private void Changed(object sender, SizeChangedEventArgs args) => Update();
    private void Layout(object? sender, object args) => Update();
    private RectInt32[] regions = [];
    private void Update()
    {
        if (titleBar.XamlRoot is null) return;
        double scale = titleBar.XamlRoot.RasterizationScale;
        var next = controls.Where(control => control.IsLoaded && DesktopVisual.IsVisible(control)).Select(control =>
        {
            var bounds = control.TransformToVisual(window.Root).TransformBounds(new Windows.Foundation.Rect(0, 0, control.ActualWidth, control.ActualHeight));
            return new RectInt32((int)(bounds.X * scale), (int)(bounds.Y * scale), (int)Math.Ceiling(bounds.Width * scale), (int)Math.Ceiling(bounds.Height * scale));
        }).ToArray();
        if (regions.SequenceEqual(next)) return;
        regions = next;
        input.SetRegionRects(NonClientRegionKind.Passthrough, next);
    }
    public void Dispose()
    {
        titleBar.SizeChanged -= Changed; titleBar.Loaded -= Loaded;
        foreach (var control in controls) control.LayoutUpdated -= Layout;
        input.ClearRegionRects(NonClientRegionKind.Passthrough);
    }
}
