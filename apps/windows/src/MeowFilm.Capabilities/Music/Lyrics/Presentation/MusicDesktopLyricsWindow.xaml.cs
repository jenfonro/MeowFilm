using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace MeowFilm.Capabilities;

sealed partial class MusicDesktopLyricsWindow : Window
{
    public MusicLyricsController Lyrics { get; }
    public MusicPlaybackController Playback { get; }
    public MusicSettingsController Settings { get; }
    private readonly DispatcherQueueTimer pointerObserver;
    private readonly MusicDesktopLyricsNative.SubclassProc hook;
    private readonly nint handle;
    private readonly MusicDesktopLyricsInputRegion inputRegion;
    private MusicDesktopLyricsUnlockWindow? unlockWindow;
    private MusicDesktopLyricColorWindow? colorWindow;
    private MenuFlyout? settingsMenu;
    private bool locked, closed, sizing, closePending, closeReady, reposition, highContrast, subclassInstalled;
    private MusicDesktopLyricsGeometry? geometryAtMove;
    private Task closeWork = Task.CompletedTask;
    private double minimumHeight = 110;
    internal nint Handle => handle;
    internal bool IsWindowClosed => closed;
    internal FrameworkElement LyricsSurface => LyricsFrame;
    internal bool IsLyricsLocked => locked;
    internal bool IsClosePending => closePending;
    internal MusicDesktopLyricsUnlockWindow? UnlockWindow => unlockWindow;

    internal object? DescribeDebugBounds()
    {
        if (closed) return null;
        if (MusicDesktopLyricsNative.GetWindowRect(handle, out var rect))
        {
            return new
            {
                x = rect.Left,
                y = rect.Top,
                width = Math.Max(0, rect.Right - rect.Left),
                height = Math.Max(0, rect.Bottom - rect.Top),
                dpi = Math.Round(Dpi, 3)
            };
        }
        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        return new
        {
            x = pos.X,
            y = pos.Y,
            width = size.Width,
            height = size.Height,
            dpi = Math.Round(Dpi, 3)
        };
    }

    internal MusicDesktopLyricsWindow(MusicLyricsController lyrics, MusicPlaybackController playback,
        MusicSettingsController settings, Window anchor, MusicDesktopLyricsGeometry? geometry = null)
    {
        Lyrics = lyrics;
        Playback = playback;
        Settings = settings;
        InitializeComponent();
        Bindings?.Initialize();
        LyricPresenter.SizeChanged += (_, args) => LineSurface.Height = args.NewSize.Height;
        handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        inputRegion = new(handle, LyricsFrame, () => HoverSurface.Visibility == Visibility.Visible || sizing);
        hook = WindowMessage;
        pointerObserver = DispatcherQueue.CreateTimer(); pointerObserver.Interval = TimeSpan.FromMilliseconds(100);
        pointerObserver.Tick += (_, _) => ObservePointer();
        try
        {
            SystemBackdrop = new LyricsTransparentBackdrop();
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            { presenter.SetBorderAndTitleBar(false, false); presenter.IsAlwaysOnTop = true; presenter.IsMaximizable = false; presenter.IsMinimizable = false; }
            AppWindow.IsShownInSwitchers = false;
            MusicDesktopLyricsNative.SetInputStyle(handle, false);
            if (!MusicDesktopLyricsNative.SetWindowSubclass(handle, hook, 1, 0)) throw new Win32Exception();
            subclassInstalled = true;
            MusicDesktopLyricsNative.SetResizableStyle(handle);
            double dpi = Dpi;
            AppWindow.Resize(new((int)Math.Round((geometry?.Width ?? 760) * dpi), (int)Math.Round((geometry?.Height ?? (Settings.DesktopLyricLayout == "double" ? 240 : 144)) * dpi)));
            if (geometry is { } saved) RestoreGeometry(saved);
            else
            {
                var bounds = anchor.AppWindow.Position; var size = anchor.AppWindow.Size;
                AppWindow.Move(new(bounds.X + Math.Max(0, (size.Width - AppWindow.Size.Width) / 2), bounds.Y + Math.Max(0, size.Height - AppWindow.Size.Height - (int)(32 * dpi))));
            }
            Playback.PropertyChanged += PlaybackChanged;
            Settings.PropertyChanged += SettingsChanged;
            AppWindow.Closing += Closing;
            AppWindow.Changed += (_, args) =>
            {
                if (args.DidPositionChange || args.DidSizeChange) QueueUnlockPlacement();
                if (!args.DidVisibilityChange) return;
                if (!AppWindow.IsVisible)
                { pointerObserver.Stop(); ClosePopups(); unlockWindow?.Hide(); ShowHover(false); inputRegion.Refresh(); }
                else if (locked) { unlockWindow?.Show(); QueueUnlockPlacement(); }
                else if (LyricsFrame.IsLoaded) StartPointerObserver();
            };
            SystemAppearance.Changed += HighContrastChanged;
            Closed += (_, _) => Cleanup();
            foreach (var line in new[] { TopLine, BottomLine })
            {
                line.RegisterPropertyChangedCallback(MusicDesktopLyricLineView.RowProperty, (_, _) => UpdateContentBounds());
                line.RegisterPropertyChangedCallback(MusicDesktopLyricLineView.ShowTranslationProperty, (_, _) => UpdateContentBounds());
            }
            LyricsFrame.Loaded += (_, _) => { Bindings?.Update(); UpdatePresentation(); UpdatePlayback(); ApplyHighContrast(SystemAppearance.IsHighContrast); StartPointerObserver(); };
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    private void Cleanup()
    {
        if (closed) return;
        closed = true;
        pointerObserver.Stop();
        ClosePopups();
        unlockWindow?.Close();
        unlockWindow = null;
        inputRegion.Dispose();
        Playback.PropertyChanged -= PlaybackChanged;
        Settings.PropertyChanged -= SettingsChanged;
        SystemAppearance.Changed -= HighContrastChanged;
        if (subclassInstalled)
        {
            subclassInstalled = false;
            MusicDesktopLyricsNative.RemoveWindowSubclass(handle, hook, 1);
        }
    }

    private double Dpi => MusicDesktopLyricsNative.GetDpiForWindow(handle) / 96d;
    internal void Show() { AppWindow.Show(false); if (LyricsFrame.IsLoaded) StartPointerObserver(); }

    private void PlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (closed || closePending) return;
        if (e.PropertyName is null or nameof(MusicPlaybackController.IsPlaying)) UpdatePlayback();
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (closed || closePending) return;
        if (e.PropertyName is null or nameof(MusicSettingsController.DesktopLyricNormalColor)) UpdateNormalColor();
        if (e.PropertyName is null or nameof(MusicSettingsController.DesktopLyricHighlightColor)) UpdateHighlightColor();
        if (e.PropertyName is null or nameof(MusicSettingsController.DesktopLyricLayout)) UpdatePresentation();
    }

    private void UpdatePlayback() => PlaybackButton.Content = Playback.IsPlaying ? "\uE769" : "\uE768";
    private void UpdatePresentation()
    {
        bool doubleLine = Settings.DesktopLyricLayout == "double";
        BottomTrack.Height = doubleLine ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        BottomLine.Visibility = doubleLine ? Visibility.Visible : Visibility.Collapsed;
        TopLine.HorizontalContentAlignment = doubleLine ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        UpdateContentBounds();
    }
    private void UpdateContentBounds()
    {
        if (closed || closePending) return;
        Status.Visibility = TopLine.Row is null && BottomLine.Row is null ? Visibility.Visible : Visibility.Collapsed;
        double top = 54 + (TopLine.ShowTranslation && !string.IsNullOrWhiteSpace(TopLine.Row?.Translation) ? 30 : 0);
        double bottom = 62 + (BottomLine.ShowTranslation && !string.IsNullOrWhiteSpace(BottomLine.Row?.Translation) ? 30 : 0);
        minimumHeight = 56 + (Settings.DesktopLyricLayout == "double" ? Math.Max(top, bottom) * 2 : top);
        double dpi = Dpi;
        int height = (int)Math.Ceiling(minimumHeight * dpi);
        if (AppWindow.Size.Height < height)
        {
            var position = AppWindow.Position; int delta = height - AppWindow.Size.Height;
            AppWindow.MoveAndResize(new(position.X, position.Y - delta, Math.Max(AppWindow.Size.Width, (int)(340 * dpi)), height));
            KeepReachableAndPersist();
        }
        inputRegion.Refresh();
    }
    private nint WindowMessage(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        // Keep the resize capability without restoring a native non-client
        // frame around the transparent XAML surface. A window region otherwise
        // reintroduces those frame insets when USER32 recalculates its shape.
        if (message == 0x0083 && wParam != 0) return 0; // WM_NCCALCSIZE
        if (message == 0x0021) return 3;
        if (message == 0x0024 && lParam != 0)
        {
            var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            bounds.MinTrackSize.X = (int)Math.Ceiling(340 * Dpi); bounds.MinTrackSize.Y = (int)Math.Ceiling(minimumHeight * Dpi);
            Marshal.StructureToPtr(bounds, lParam, false); return 0;
        }
        if (message == 0x0084 && !locked)
        { var hit = MusicDesktopLyricsNative.ResizeHitTest(hwnd, lParam); if (hit != 0) return hit; }
        if (message == 0x0231) { sizing = true; inputRegion.Refresh(); geometryAtMove = TryCaptureGeometry(out var start) ? start : null; }
        else if (message == 0x0232 && sizing)
        {
            sizing = false;
            if (TryCaptureGeometry(out var current))
            {
                if (geometryAtMove != current) _ = Settings.SaveDesktopLyricGeometryAsync(current);
            }
            else _ = Lyrics.Notify("桌面歌词位置和大小保存失败");
            geometryAtMove = null;
            inputRegion.Refresh();
        }
        if (message == 0x02E0) QueueUnlockPlacement();
        return MusicDesktopLyricsNative.DefSubclassProc(hwnd, message, wParam, lParam);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { internal NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    private static (int X, int Y) ReachableOrigin(MusicDesktopLyricsNative.Monitor monitor, int width, int height, double x, double y)
    {
        int availableWidth = monitor.WorkRight - monitor.WorkLeft, availableHeight = monitor.WorkBottom - monitor.WorkTop;
        return ((int)Math.Round(width > availableWidth ? ((long)monitor.WorkLeft + monitor.WorkRight - width) / 2d : Math.Clamp(x, monitor.WorkLeft, monitor.WorkRight - width)),
            (int)Math.Round(height > availableHeight ? monitor.WorkTop : Math.Clamp(y, monitor.WorkTop, monitor.WorkBottom - height)));
    }
    private void RestoreGeometry(MusicDesktopLyricsGeometry geometry)
    {
        var monitors = MusicDesktopLyricsNative.Monitors();
        var monitor = monitors.FirstOrDefault(item => item.Device == geometry.Monitor);
        if (monitor.Handle == 0) monitor = monitors.FirstOrDefault(item => item.Primary);
        if (monitor.Handle == 0) throw new Win32Exception("No display monitor is available.");
        AppWindow.Move(new(monitor.WorkLeft, monitor.WorkTop)); double dpi = Dpi;
        int width = (int)Math.Round(geometry.Width * dpi), height = (int)Math.Round(Math.Max(geometry.Height, minimumHeight) * dpi);
        var (x, y) = ReachableOrigin(monitor, width, height, monitor.WorkLeft + geometry.X * dpi, monitor.WorkTop + geometry.Y * dpi);
        AppWindow.MoveAndResize(new(x, y, width, height));
    }
    internal bool TryCaptureGeometry(out MusicDesktopLyricsGeometry geometry)
    {
        try
        {
            var monitor = MusicDesktopLyricsNative.MonitorForWindow(handle);
            if (!MusicDesktopLyricsNative.GetWindowRect(handle, out var bounds) || Dpi <= 0) throw new Win32Exception();
            geometry = new(monitor.Device, (bounds.Left - monitor.WorkLeft) / Dpi, (bounds.Top - monitor.WorkTop) / Dpi, (bounds.Right - bounds.Left) / Dpi, (bounds.Bottom - bounds.Top) / Dpi);
            return true;
        }
        catch (Win32Exception) { geometry = default; return false; }
    }
    private void KeepReachableAndPersist()
    {
        if (closed || closePending) return;
        try
        {
            var monitor = MusicDesktopLyricsNative.MonitorForWindow(handle);
            var (x, y) = ReachableOrigin(monitor, AppWindow.Size.Width, AppWindow.Size.Height, AppWindow.Position.X, AppWindow.Position.Y);
            AppWindow.Move(new(x, y));
            if (Settings.DesktopLyricSettingsLoaded && TryCaptureGeometry(out var geometry)) _ = Settings.SaveDesktopLyricGeometryAsync(geometry);
        }
        catch (Win32Exception) { _ = Lyrics.Notify("无法定位桌面歌词窗口。"); }
    }
    private void HighContrastChanged(object? sender, EventArgs args) =>
        DispatcherQueue.TryEnqueue(() => { if (!closed) ApplyHighContrast(SystemAppearance.IsHighContrast); });
    internal void ApplyHighContrast(bool enabled)
    {
        if (closed) return;
        highContrast = enabled;
        DesktopTheme.SetIsHighContrast(LyricsFrame, enabled);
        unlockWindow?.ApplyHighContrast(enabled);
        colorWindow?.ApplyHighContrast(enabled);
        HoverSurface.Background = enabled ? SystemAppearance.GetElementBrush(UIElementType.Window) : DesktopVisual.Brush("#B3212121");
        UpdateNormalColor(); UpdateHighlightColor();
        var buttonForeground = enabled ? SystemAppearance.GetElementBrush(UIElementType.WindowText) : DesktopVisual.Brush("#F5F5F5");
        foreach (var button in Toolbar.Children.OfType<Button>())
            button.Foreground = buttonForeground;
    }
    private void UpdateNormalColor() => TopLine.Normal = BottomLine.Normal = StatusText.Foreground = new SolidColorBrush(highContrast
        ? SystemAppearance.GetElementColor(UIElementType.WindowText) : SystemAppearance.ParseColor(Settings.DesktopLyricNormalColor));
    private void UpdateHighlightColor() => TopLine.Highlight = BottomLine.Highlight = new SolidColorBrush(highContrast
        ? SystemAppearance.GetElementColor(UIElementType.Highlight) : SystemAppearance.ParseColor(Settings.DesktopLyricHighlightColor));
    private void StartPointerObserver() { if (closed || locked || !AppWindow.IsVisible) return; ObservePointer(); pointerObserver.Start(); }
    private void ObservePointer() { if (MusicDesktopLyricsNative.GetCursorPos(out var point)) RefreshHover(new(point.X, point.Y)); }
    internal void RefreshHover(Point point)
    {
        if (closed || locked || !AppWindow.IsVisible) return;
        bool over = MusicDesktopLyricsNative.GetWindowRect(handle, out var rect) && point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;
        ShowHover(over || sizing || settingsMenu is not null || colorWindow is not null);
    }
    private void ShowHover(bool value)
    {
        var visibility = value ? Visibility.Visible : Visibility.Collapsed;
        if (HoverSurface.Visibility == visibility) return;
        HoverSurface.Visibility = Toolbar.Visibility = visibility;
        inputRegion.Refresh();
    }
    private void Lyrics_PointerEntered(object sender, PointerRoutedEventArgs e) { if (!locked) ShowHover(true); }
    private void Lyrics_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (locked || e.Handled || !e.GetCurrentPoint(LyricsFrame).Properties.IsLeftButtonPressed) return;
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node)) if (node is ButtonBase) return;
        e.Handled = true; MusicDesktopLyricsNative.ReleaseCapture(); MusicDesktopLyricsNative.SendMessageW(handle, 0x00A1, 2, 0);
    }
    private void Activate_Click(object sender, RoutedEventArgs e) => Lyrics.ActivateDesktopLyricsOwner();
    private void Delay_Click(object sender, RoutedEventArgs e) => Lyrics.AdjustLyricOffset(.5);
    private void Advance_Click(object sender, RoutedEventArgs e) => Lyrics.AdjustLyricOffset(-.5);
    private async void RunUiAction(Func<Task> action)
    {
        if (closed || closePending) return;
        try { await action(); }
        catch { /* Window actions never escape into the WinUI UI thread. */ }
    }
    private void Previous_Click(object sender, RoutedEventArgs e) => RunUiAction(() => Lyrics.ExecuteMediaShortcutAsync(MediaShortcutAction.Previous));
    private void Playback_Click(object sender, RoutedEventArgs e) => RunUiAction(() => Lyrics.ExecuteMediaShortcutAsync(MediaShortcutAction.TogglePlayback));
    private void Next_Click(object sender, RoutedEventArgs e) => RunUiAction(() => Lyrics.ExecuteMediaShortcutAsync(MediaShortcutAction.Next));
    private void Settings_Click(object sender, RoutedEventArgs e) => RunUiAction(async () =>
    {
        await Settings.EnsureDesktopLyricSettingsAsync(); if (closed || closePending || locked || !AppWindow.IsVisible) return;
        settingsMenu?.Hide(); var menu = new MenuFlyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            MenuFlyoutPresenterStyle = (Style)DesktopVisual.Resource(LyricsFrame, "MeowFilm.ContextMenu")
        };
        var layout = new MenuFlyoutSubItem { Text = "显示样式", IsEnabled = Settings.CanEditDesktopLyricSettings };
        AddChoice(layout, "单行", Settings.DesktopLyricLayout == "single", () => Settings.SetDesktopLyricLayoutAsync("single"));
        AddChoice(layout, "双行", Settings.DesktopLyricLayout == "double", () => Settings.SetDesktopLyricLayoutAsync("double"));
        menu.Items.Add(layout); menu.Items.Add(ColorMenu("普通颜色", "normal", Settings.DesktopLyricNormalColor));
        menu.Items.Add(ColorMenu("逐字高亮颜色", "highlight", Settings.DesktopLyricHighlightColor)); menu.Items.Add(new MenuFlyoutSeparator());
        var translation = new ToggleMenuFlyoutItem { Text = "显示译文", IsChecked = Lyrics.ShowLyricTranslation };
        translation.Click += (_, _) => RunUiAction(() => Lyrics.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleTranslation)); menu.Items.Add(translation);
        menu.Closed += (_, _) => { if (ReferenceEquals(settingsMenu, menu)) settingsMenu = null; ObservePointer(); };
        settingsMenu = menu; ShowHover(true); menu.ShowAt(SettingsButton);
    });
    private void AddChoice(MenuFlyoutSubItem parent, string label, bool selected, Func<Task> apply)
    { var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = selected }; item.Click += (_, _) => RunUiAction(apply); parent.Items.Add(item); }
    private MenuFlyoutSubItem ColorMenu(string label, string role, string selected)
    {
        var menu = new MenuFlyoutSubItem { Text = label, IsEnabled = Settings.CanEditDesktopLyricSettings };
        foreach (var preset in MusicDesktopLyricColorWindow.Presets) AddChoice(menu, preset.Name, selected == preset.Color, () => Settings.SetDesktopLyricColorAsync(role, preset.Color));
        menu.Items.Add(new MenuFlyoutSeparator()); var custom = new MenuFlyoutItem { Text = "自定义…" };
        custom.Click += (_, _) => RunUiAction(async () =>
        {
            var prior = colorWindow; colorWindow = null; prior?.Close();
            var editor = new MusicDesktopLyricColorWindow(Settings, role); colorWindow = editor;
            editor.ApplyHighContrast(highContrast);
            try { await editor.ShowModalAsync(); } finally { if (ReferenceEquals(colorWindow, editor)) colorWindow = null; ObservePointer(); }
        });
        menu.Items.Add(custom); return menu;
    }
    private void Lock_Click(object sender, RoutedEventArgs e) => RunUiAction(() => ChangeLockAsync(true));
    internal void UnlockLyrics() => _ = ChangeLockAsync(false);
    private async Task ChangeLockAsync(bool value)
    { try { SetLocked(value); } catch (Win32Exception) { await Lyrics.Notify(value ? "无法锁定桌面歌词，请重试。" : "无法解锁桌面歌词，请重试。"); } }
    internal void SetLocked(bool value)
    {
        if (closed || locked == value) return;
        if (value)
        {
            ClosePopups(); LyricsFrame.ReleasePointerCaptures(); ShowHover(true); LyricsFrame.UpdateLayout();
            var unlock = new MusicDesktopLyricsUnlockWindow(this);
            try { unlock.ApplyHighContrast(highContrast); unlock.Show(); unlockWindow = unlock; PlaceUnlock(); MusicDesktopLyricsNative.SetInputStyle(handle, true); }
            catch { unlockWindow = null; unlock.Close(); throw; }
            locked = true; pointerObserver.Stop(); ShowHover(false);
        }
        else
        {
            MusicDesktopLyricsNative.SetInputStyle(handle, false); locked = false;
            var unlock = unlockWindow; unlockWindow = null; unlock?.Close();
            MusicDesktopLyricsNative.BringToTop(handle); StartPointerObserver();
        }
    }
    private void QueueUnlockPlacement()
    {
        if (closed || unlockWindow is null || reposition) return;
        reposition = true;
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            reposition = false; if (closed || closePending) return;
            try { PlaceUnlock(); }
            catch (Win32Exception)
            {
                try { await ChangeLockAsync(false); await Lyrics.Notify("无法定位解锁按钮，已尝试解除歌词锁定。"); }
                catch { }
            }
        })) reposition = false;
    }
    private void PlaceUnlock()
    {
        if (unlockWindow is null || !AppWindow.IsVisible) return;
        double dpi = Dpi; var origin = LockButton.TransformToVisual(LyricsFrame).TransformPoint(new Point());
        var start = new Point(AppWindow.Position.X + origin.X * dpi, AppWindow.Position.Y + origin.Y * dpi);
        MusicDesktopLyricsNative.Place(unlockWindow.Handle, start, new(start.X + 32 * dpi, start.Y + 32 * dpi));
    }
    private void ClosePopups() { settingsMenu?.Hide(); settingsMenu = null; colorWindow?.Close(); colorWindow = null; }
    private void Close_Click(object sender, RoutedEventArgs e) => RunUiAction(CloseAsync);
    internal Task CloseAsync() { Close(); return closeWork; }
    private void Closing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (closeReady || closed || !Settings.DesktopLyricSettingsLoaded) return;
        e.Cancel = true; if (closePending) return;
        closePending = true; LyricsFrame.IsHitTestVisible = false;
        MusicDesktopLyricsNative.EnableWindow(handle, false); pointerObserver.Stop(); Lyrics.DesktopLyricsClosing();
        closeWork = FinishCloseAsync();
    }
    private async Task FinishCloseAsync()
    {
        Task save = TryCaptureGeometry(out var geometry) ? Settings.SaveDesktopLyricGeometryAsync(geometry) : Lyrics.Notify("桌面歌词位置和大小保存失败");
        await Task.Yield();
        try { await save; } finally { closeReady = true; if (!closed) Close(); }
    }
    internal void CloseForShutdown() { closeReady = true; Close(); }
}

// The desktop-lyrics window is intentionally transparent; all controls still
// belong to its own WinUI root. This is not the in-page video integration path.
internal sealed class LyricsTransparentBackdrop : SystemBackdrop
{
    private static object? systemDispatcher;
    private Windows.UI.Composition.Compositor? compositor;
    private Windows.UI.Composition.CompositionColorBrush? brush;

    protected override void OnTargetConnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        EnsureSystemDispatcher();
        compositor = new Windows.UI.Composition.Compositor();
        brush = compositor.CreateColorBrush(Microsoft.UI.Colors.Transparent);
        target.SystemBackdrop = brush;
    }
    protected override void OnTargetDisconnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop target)
    {
        target.SystemBackdrop = null;
        brush?.Dispose(); brush = null;
        compositor?.Dispose(); compositor = null;
        base.OnTargetDisconnected(target);
    }

    private static void EnsureSystemDispatcher()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null) return;
        var options = new DispatcherQueueOptions { Size = Marshal.SizeOf<DispatcherQueueOptions>(), ThreadType = 2, ApartmentType = 2 };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, ref systemDispatcher));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions { internal int Size, ThreadType, ApartmentType; }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options,
        [MarshalAs(UnmanagedType.IUnknown)] ref object? dispatcherQueueController);
}
