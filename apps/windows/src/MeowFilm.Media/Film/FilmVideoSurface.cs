using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace MeowFilm.Media;

/// <summary>Native picture and transparent WPF controls share one child HWND container.</summary>
public sealed class FilmVideoSurface : ContentControl, IDisposable
{
    private readonly HwndSource parking;
    private readonly PictureHost picture;
    private readonly ContentPresenter foreground = new();
    private HwndSource? overlay;
    private static readonly DependencyPropertyDescriptor ClipPropertyDescriptor =
        DependencyPropertyDescriptor.FromProperty(UIElement.ClipProperty, typeof(UIElement));
    private UIElement? controls;
    private Geometry? controlsClip;
    private Rect? pictureClip;
    private bool closed;

    public FilmVideoSurface()
    {
        Focusable = false; IsTabStop = false;
        AutomationProperties.SetName(this, "视频画面");
        parking = new HwndSource(new HwndSourceParameters("meowfilm-film-parking")
        {
            Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000),
            ExtendedWindowStyle = 0x08000080
        });
        try { picture = new PictureHost(parking.Handle, CloseOverlay); }
        catch { parking.Dispose(); throw; }
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.Name = "PART_Picture";
        Template = new ControlTemplate(typeof(FilmVideoSurface)) { VisualTree = factory };
        Loaded += LoadedSurface;
        Unloaded += UnloadedSurface;
        LayoutUpdated += LayoutSurface;
        IsVisibleChanged += VisibilityChanged;
    }

    public IntPtr PictureHandle => picture.PictureHandle;

    /// <summary>Optional local-DIP picture/control clip; ancestor clips are also applied.</summary>
    public void SetPictureClip(Rect? clip)
    {
        Dispatcher.VerifyAccess();
        pictureClip = clip;
        UpdateWindows();
    }

    public override void OnApplyTemplate()
    {
        picture.Park();
        if (VisualTreeHelper.GetParent(picture) is Border previous) previous.Child = null;
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_Picture") is Border border) border.Child = picture;
        UpdateWindows();
    }
    protected override void OnContentChanged(object oldContent, object newContent)
    {
        UnwatchControls();
        base.OnContentChanged(oldContent, newContent);
        foreground.Content = newContent;
        controls = newContent as UIElement;
        if (controls is not null)
        {
            ClipPropertyDescriptor.AddValueChanged(controls, ControlsClipChanged);
            WatchControlsClip();
        }
        UpdateWindows();
    }
    private void ControlsClipChanged(object? sender, EventArgs args)
    {
        WatchControlsClip();
        UpdateWindows();
    }
    private void GeometryChanged(object? sender, EventArgs args) => UpdateWindows();
    private void WatchControlsClip()
    {
        if (controlsClip is { IsFrozen: false }) controlsClip.Changed -= GeometryChanged;
        controlsClip = controls?.Clip;
        if (controlsClip is { IsFrozen: false }) controlsClip.Changed += GeometryChanged;
    }
    private void UnwatchControls()
    {
        if (controls is not null) ClipPropertyDescriptor.RemoveValueChanged(controls, ControlsClipChanged);
        if (controlsClip is { IsFrozen: false }) controlsClip.Changed -= GeometryChanged;
        controls = null; controlsClip = null;
    }
    private void LoadedSurface(object sender, RoutedEventArgs args) => UpdateWindows();
    private void UnloadedSurface(object sender, RoutedEventArgs args)
    {
        if (closed) return;
        picture.Park();
        CloseOverlay();
    }
    private void LayoutSurface(object? sender, EventArgs args) => UpdateWindows();
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdateWindows();

    private void UpdateWindows()
    {
        if (closed) return;
        if (!IsLoaded || !IsVisible || ActualWidth <= 0 || ActualHeight <= 0 ||
            PresentationSource.FromVisual(this) is not HwndSource || picture.Handle == IntPtr.Zero)
        {
            picture.Park();
            if (overlay is not null) ShowWindow(overlay.Handle, 0);
            return;
        }
        picture.Mount();
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
        SetWindowPos(PictureHandle, IntPtr.Zero, 0, 0, width, height, 0x0014);
        if (overlay is null)
        {
            overlay = new HwndSource(new HwndSourceParameters("meowfilm-film-controls")
            {
                ParentWindow = picture.Handle, Width = width, Height = height,
                WindowStyle = 0x40000000 | 0x04000000, ExtendedWindowStyle = 0x08000000,
                UsesPerPixelTransparency = true, TreatAsInputRoot = true
            });
            if (overlay.CompositionTarget is { } composition) composition.BackgroundColor = Colors.Transparent;
            overlay.RootVisual = foreground;
        }
        foreground.Width = ActualWidth;
        foreground.Height = ActualHeight;
        SetWindowPos(overlay.Handle, IntPtr.Zero, 0, 0, width, height, 0x0010);
        var clip = VisibleRectangle();
        ApplyRegion(PictureHandle, clip, dpi);
        ApplyRegion(picture.Handle, clip, dpi);
        ApplyRegion(overlay.Handle, clip, dpi);
        ShowWindow(PictureHandle, clip.IsEmpty ? 0 : 4);
        ShowWindow(overlay.Handle, clip.IsEmpty ? 0 : 4);
    }

    private Rect VisibleRectangle()
    {
        var visible = new Rect(RenderSize);
        if (pictureClip is { } requested) visible.Intersect(requested);
        // The page supplies its scroll intersection on the custom controls root.
        // Both native windows use that same local coordinate space.
        if (Content is UIElement controls && controls.Clip is { } controlsClip)
            visible.Intersect(controlsClip.Bounds);
        DependencyObject? node = this;
        while (node is Visual visual)
        {
            if (visual is UIElement element)
            {
                var transform = element.TransformToVisual(this);
                if (element.Clip is { } clip) visible.Intersect(transform.TransformBounds(clip.Bounds));
                if (element.ClipToBounds || element is ScrollContentPresenter)
                    visible.Intersect(transform.TransformBounds(new Rect(element.RenderSize)));
            }
            node = VisualTreeHelper.GetParent(node);
        }
        return visible;
    }
    private static void ApplyRegion(IntPtr window, Rect clip, DpiScale dpi)
    {
        var region = clip.IsEmpty ? CreateRectRgn(0, 0, 0, 0) : CreateRectRgn(
            (int)Math.Ceiling(clip.Left * dpi.DpiScaleX), (int)Math.Ceiling(clip.Top * dpi.DpiScaleY),
            (int)Math.Floor(clip.Right * dpi.DpiScaleX), (int)Math.Floor(clip.Bottom * dpi.DpiScaleY));
        if (region == IntPtr.Zero) return;
        if (SetWindowRgn(window, region, true) == 0) DeleteObject(region);
    }
    private void CloseOverlay()
    {
        var source = overlay;
        overlay = null;
        // The container releases its controls before destroying its child HWNDs.
        if (source is null || source.IsDisposed) return;
        source.RootVisual = null;
        source.Dispose();
    }
    public void Dispose()
    {
        Dispatcher.VerifyAccess();
        if (closed) return;
        closed = true;
        Loaded -= LoadedSurface; Unloaded -= UnloadedSurface;
        LayoutUpdated -= LayoutSurface; IsVisibleChanged -= VisibilityChanged;
        UnwatchControls(); CloseOverlay(); foreground.Content = null;
        picture.Close(); picture.Dispose(); parking.Dispose();
    }

    private sealed class PictureHost : HwndHost
    {
        private readonly IntPtr parking;
        private readonly Action closeOverlay;
        internal IntPtr PictureHandle { get; private set; }
        internal PictureHost(IntPtr parking, Action closeOverlay)
        {
            this.parking = parking;
            this.closeOverlay = closeOverlay;
            PictureHandle = CreateWindowEx(0, "static", "", 0x40000000 | 0x02000000,
                0, 0, 1, 1, parking, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (PictureHandle == IntPtr.Zero) throw new PlaybackException("FILM_RENDER_FAILED");
        }
        protected override HandleRef BuildWindowCore(HandleRef parent)
        {
            var host = CreateWindowEx(0, "static", "", 0x40000000 | 0x10000000 | 0x02000000,
                0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (host == IntPtr.Zero) throw new PlaybackException("FILM_RENDER_FAILED");
            SetParent(PictureHandle, host);
            return new HandleRef(this, host);
        }
        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            closeOverlay(); Park(); DestroyWindow(hwnd.Handle);
        }
        internal void Mount()
        {
            if (Handle != IntPtr.Zero && PictureHandle != IntPtr.Zero) SetParent(PictureHandle, Handle);
        }
        internal void Park()
        {
            if (PictureHandle == IntPtr.Zero) return;
            ShowWindow(PictureHandle, 0); SetParent(PictureHandle, parking);
        }
        internal void Close()
        {
            if (PictureHandle == IntPtr.Zero) return;
            DestroyWindow(PictureHandle); PictureHandle = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string name, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after,
        int x, int y, int width, int height, int flags);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
}
