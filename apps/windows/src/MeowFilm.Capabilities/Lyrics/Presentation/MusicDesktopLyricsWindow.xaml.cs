using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

internal readonly record struct MusicDesktopLyricsWindowState(
    double Left, double Top, double Width, double Height, bool IsLocked, bool UserSized,
    MusicDesktopLyricsGeometry? Geometry);

internal partial class MusicDesktopLyricsWindow : Window
{
    private readonly MusicCapability module;
    private readonly DispatcherTimer pointerObserver;
    private HwndSource? source;
    private MusicDesktopLyricsUnlockWindow? unlockWindow;
    private MusicDesktopLyricColorWindow? colorWindow;
    private ContextMenu? settingsMenu;
    private DispatcherOperation? reposition;
    private bool locked, restoreLock, dragging, closed, sizing, userSized;
    private bool closePending, closeReady;
    private bool restoredGeometry;
    private MusicDesktopLyricsGeometry? openingGeometry, geometryAtMove;
    private Task closeWork = Task.CompletedTask;

    internal bool IsLyricsLocked => locked;
    internal bool IsPointerObserverRunning => pointerObserver.IsEnabled;
    internal bool IsClosePending => closePending;
    internal MusicDesktopLyricsUnlockWindow? UnlockWindow => unlockWindow;

    internal MusicDesktopLyricsWindow(MusicCapability module, Window anchor, MusicDesktopLyricsGeometry? geometry = null)
    {
        this.module = module;
        openingGeometry = geometry;
        InitializeComponent();
        UnlockAnchor.RenderTransform = new System.Windows.Media.TranslateTransform();
        DataContext = module;
        if (geometry is { } saved)
        {
            Width = saved.Width;
            Height = saved.Height;
        }
        else if (module.DesktopLyricLayout == "double") Height = 240;
        UpdateMinimumWidth();
        UpdateMinimumHeight();
        module.PropertyChanged += ModuleChanged;
        pointerObserver = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(100) };
        pointerObserver.Tick += ObservePointer;
        Loaded += WindowLoaded;
        IsVisibleChanged += VisibilityChanged;
        // This is a placement anchor, not WPF Owner: minimizing it must not hide the lyrics.
        var bounds = anchor.WindowState == WindowState.Normal
            ? new Rect(anchor.Left, anchor.Top, anchor.Width, anchor.Height) : anchor.RestoreBounds;
        if (bounds.IsEmpty || !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top))
            bounds = SystemParameters.WorkArea;
        Left = bounds.Left + Math.Max(0, (bounds.Width - Width) / 2);
        Top = bounds.Top + Math.Max(0, bounds.Height - Height - 32);
    }

    private void ModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (closed || closePending) return;
        if (e.PropertyName is nameof(MusicCapability.DesktopLyricLayout) or
            nameof(MusicCapability.DesktopLyricTopRow) or nameof(MusicCapability.DesktopLyricBottomRow) or
            nameof(MusicCapability.ShowLyricTranslation))
            UpdateMinimumHeight();
    }

    private void UpdateMinimumHeight()
    {
        double track = 54;
        bool topTranslation = module.ShowLyricTranslation &&
            !string.IsNullOrWhiteSpace(module.DesktopLyricTopRow?.Translation);
        // Each 50-DIP original may have a 27-DIP source translation, 3-DIP gap,
        // and the line view's 2-DIP outer margin on both sides.
        double topTrack = track + (topTranslation ? 30 : 0);
        double rows = topTrack;
        if (module.DesktopLyricLayout == "double")
        {
            bool bottomTranslation = module.ShowLyricTranslation &&
                !string.IsNullOrWhiteSpace(module.DesktopLyricBottomRow?.Translation);
            // The two star tracks are equal; the lower track also carries its
            // 8-DIP top margin, so both tracks use the larger natural height.
            double bottomTrack = track + (bottomTranslation ? 30 : 0) + 8;
            rows = Math.Max(topTrack, bottomTrack) * 2;
        }
        double minimum = 44 + rows + 12;
        if (MinHeight == minimum) return;
        double previous = Height;
        MinHeight = minimum;
        if (previous >= MinHeight) return;
        Height = MinHeight;
        if (IsLoaded) Top -= Height - previous;
        if (IsVisible) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(KeepReachableAndPersist));
    }

    private void UpdateMinimumWidth()
    {
        var visibility = Toolbar.Visibility;
        Toolbar.Visibility = Visibility.Visible;
        Toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        MinWidth = Toolbar.DesiredSize.Width;
        Toolbar.Visibility = visibility;
        if (Width < MinWidth) Width = MinWidth;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        source = HwndSource.FromHwnd(handle)!;
        source.AddHook(WindowMessage);
        MusicDesktopLyricsNative.SetInputStyle(handle, clickThrough: false);
        MusicDesktopLyricsNative.SetResizableStyle(handle);
        if (openingGeometry is { } geometry)
        {
            RestoreGeometry(handle, geometry);
            restoredGeometry = true;
            openingGeometry = null;
        }
        ApplyHighContrast(SystemParameters.HighContrast);
        SystemParameters.StaticPropertyChanged += SystemSettingChanged;
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return 3; } // WM_MOUSEACTIVATE / MA_NOACTIVATE
        if (message == 0x0024 && lParam != 0)
        {
            ApplyMinimumTrackSize(hwnd, lParam);
            handled = true;
            return 0;
        }
        if (message == 0x0084 && !locked)
        {
            var hit = MusicDesktopLyricsNative.ResizeHitTest(hwnd, lParam);
            if (hit != 0) { handled = true; return hit; }
        }
        if (message == 0x0231)
        {
            sizing = true;
            if (TryCaptureGeometry(out var start)) geometryAtMove = start;
            else
            {
                geometryAtMove = null;
                _ = module.Notify("桌面歌词位置和大小保存失败");
            }
        }
        else if (message == 0x0232 && sizing)
        {
            sizing = false;
            if (TryCaptureGeometry(out var current))
            {
                if (geometryAtMove is { } start &&
                    (start.Width != current.Width || start.Height != current.Height))
                    userSized = true;
                if (geometryAtMove != current)
                    PersistGeometry(current);
            }
            else _ = module.Notify("桌面歌词位置和大小保存失败");
            geometryAtMove = null;
        }
        if (message == 0x02E0) QueueUnlockPlacement(); // Let WPF accept WM_DPICHANGED first.
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        internal NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    private void ApplyMinimumTrackSize(nint hwnd, nint address)
    {
        var info = Marshal.PtrToStructure<MinMaxInfo>(address);
        uint dpi = MusicDesktopLyricsNative.GetDpiForWindow(hwnd);
        if (dpi == 0) throw new Win32Exception("The window DPI is unavailable.");
        // The layered WindowStyle.None surface has no non-client frame to add.
        info.MinTrackSize.X = (int)Math.Ceiling(MinWidth * dpi / 96d);
        info.MinTrackSize.Y = (int)Math.Ceiling(MinHeight * dpi / 96d);
        Marshal.StructureToPtr(info, address, false);
    }

    private void RestoreGeometry(nint hwnd, MusicDesktopLyricsGeometry geometry)
    {
        var monitors = MusicDesktopLyricsNative.Monitors();
        var monitor = monitors.FirstOrDefault(item => item.Device == geometry.Monitor);
        if (monitor.Handle == 0) monitor = monitors.FirstOrDefault(item => item.Primary);
        if (monitor.Handle == 0) throw new Win32Exception("No display monitor is available.");
        // Move onto the saved monitor first so WPF processes its per-monitor DPI
        // transition before converting the saved DIP offset.
        if (!MusicDesktopLyricsNative.SetLocation(hwnd, monitor.WorkLeft, monitor.WorkTop))
            throw new Win32Exception();
        uint dpi = MusicDesktopLyricsNative.GetDpiForWindow(hwnd);
        if (dpi == 0 || !MusicDesktopLyricsNative.GetWindowRect(hwnd, out var bounds))
            throw new Win32Exception();
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        double x = monitor.WorkLeft + geometry.X * dpi / 96d;
        double yOffset = geometry.Y - Math.Max(0, Height - geometry.Height);
        double y = monitor.WorkTop + yOffset * dpi / 96d;
        var position = ReachableOrigin(monitor, width, height, x, y);
        if (!MusicDesktopLyricsNative.SetLocation(hwnd, position.X, position.Y)) throw new Win32Exception();
    }

    private static (int X, int Y) ReachableOrigin(MusicDesktopLyricsNative.Monitor monitor,
        int width, int height, double x, double y)
    {
        int workWidth = monitor.WorkRight - monitor.WorkLeft,
            workHeight = monitor.WorkBottom - monitor.WorkTop;
        x = width > workWidth
            ? (int)Math.Round(((long)monitor.WorkLeft + monitor.WorkRight - width) / 2d)
            : Math.Clamp(x, monitor.WorkLeft, Math.Max(monitor.WorkLeft, monitor.WorkRight - width));
        y = height > workHeight
            ? monitor.WorkTop
            : Math.Clamp(y, monitor.WorkTop, Math.Max(monitor.WorkTop, monitor.WorkBottom - height));
        return ((int)Math.Round(x), (int)Math.Round(y));
    }

    private MusicDesktopLyricsGeometry CaptureGeometry()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var monitor = MusicDesktopLyricsNative.MonitorForWindow(handle);
        uint dpi = MusicDesktopLyricsNative.GetDpiForWindow(handle);
        if (dpi == 0) throw new Win32Exception("The display DPI is unavailable.");
        if (!MusicDesktopLyricsNative.GetWindowRect(handle, out var bounds)) throw new Win32Exception();
        return new(monitor.Device, (bounds.Left - monitor.WorkLeft) * 96d / dpi,
            (bounds.Top - monitor.WorkTop) * 96d / dpi,
            (bounds.Right - bounds.Left) * 96d / dpi, (bounds.Bottom - bounds.Top) * 96d / dpi);
    }

    private bool TryCaptureGeometry(out MusicDesktopLyricsGeometry geometry)
    {
        try { geometry = CaptureGeometry(); return true; }
        catch (Win32Exception) { geometry = default; return false; }
    }

    private void PersistGeometry(MusicDesktopLyricsGeometry geometry) =>
        _ = module.SaveDesktopLyricGeometryAsync(geometry);

    internal Task SaveGeometryForShutdown(MusicDesktopLyricsGeometry geometry) =>
        module.SaveDesktopLyricGeometryAsync(geometry);

    private void PersistCurrentGeometry()
    {
        if (closed || closePending || !IsVisible || !module.DesktopLyricSettingsLoaded) return;
        if (TryCaptureGeometry(out var geometry)) PersistGeometry(geometry);
        else _ = module.Notify("桌面歌词位置和大小保存失败");
    }

    private async void KeepReachableAndPersist()
    {
        if (closed || closePending || !IsVisible) return;
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var monitor = MusicDesktopLyricsNative.MonitorForWindow(handle);
            if (!MusicDesktopLyricsNative.GetWindowRect(handle, out var bounds)) throw new Win32Exception();
            int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
            var (x, y) = ReachableOrigin(monitor, width, height, bounds.Left, bounds.Top);
            if ((x != bounds.Left || y != bounds.Top) && !MusicDesktopLyricsNative.SetLocation(handle, x, y))
                throw new Win32Exception();
            PersistCurrentGeometry();
        }
        catch (Win32Exception) { await module.Notify("无法定位桌面歌词窗口。"); }
    }

    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (restoreLock) { restoreLock = false; _ = ChangeLockAsync(true); }
        else StartPointerObserver();
        if (restoredGeometry)
        {
            restoredGeometry = false;
            KeepReachableAndPersist();
        }
    }

    private void SystemSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName) && e.PropertyName != nameof(SystemParameters.HighContrast)) return;
        if (closed || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!closed) ApplyHighContrast(SystemParameters.HighContrast);
        });
    }

    internal void ApplyHighContrast(bool enabled)
    {
        DesktopTheme.SetIsHighContrast(this, enabled);
        if (unlockWindow is not null) DesktopTheme.SetIsHighContrast(unlockWindow, enabled);
        if (colorWindow is not null) DesktopTheme.SetIsHighContrast(colorWindow, enabled);
        if (settingsMenu is not null) DesktopTheme.SetIsHighContrast(settingsMenu, enabled);
    }

    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            pointerObserver.Stop();
            ClosePopups();
            unlockWindow?.Hide();
            ShowHover(false);
        }
        else if (locked)
        {
            unlockWindow?.Show();
            QueueUnlockPlacement();
        }
        else if (IsLoaded) StartPointerObserver();
    }

    private void StartPointerObserver()
    {
        if (closed || locked || !IsVisible) return;
        ObservePointer(null, EventArgs.Empty);
        pointerObserver.Start();
    }

    private void ObservePointer(object? sender, EventArgs e)
    {
        if (MusicDesktopLyricsNative.GetCursorPos(out var point)) RefreshHover(new(point.X, point.Y));
    }

    // Physical screen coordinates also cover alpha=0 pixels, which do not raise WPF MouseEnter.
    internal void RefreshHover(Point screenPoint)
    {
        if (closed || locked || !IsVisible) return;
        var handle = new WindowInteropHelper(this).Handle;
        bool over = MusicDesktopLyricsNative.GetWindowRect(handle, out var rect) &&
            screenPoint.X >= rect.Left && screenPoint.X < rect.Right &&
            screenPoint.Y >= rect.Top && screenPoint.Y < rect.Bottom;
        ShowHover(over || dragging || settingsMenu?.IsOpen == true || colorWindow?.IsVisible == true);
    }

    private void Lyrics_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!locked) ShowHover(true);
    }

    private void ShowHover(bool visible)
    {
        var next = visible ? Visibility.Visible : Visibility.Collapsed;
        if (HoverSurface.Visibility == next && Toolbar.Visibility == next) return;
        HoverSurface.Visibility = Toolbar.Visibility = next;
        foreach (var button in Toolbar.Children.OfType<Button>()) ToolTipService.SetIsEnabled(button, visible);
    }

    private void Lyrics_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (locked || e.Handled || e.ChangedButton != MouseButton.Left) return;
        // Toolbar controls own their gestures; only the lyric surface starts a move.
        for (DependencyObject? target = e.OriginalSource as DependencyObject; target is not null;
             target = System.Windows.Media.VisualTreeHelper.GetParent(target))
            if (target is ButtonBase) return;
        e.Handled = true;
        dragging = true;
        try { DragMove(); }
        finally
        {
            dragging = false;
            ObservePointer(null, EventArgs.Empty);
        }
    }

    private void Activate_Click(object sender, RoutedEventArgs e) => module.ActivateDesktopLyricsOwner();
    private void Delay_Click(object sender, RoutedEventArgs e) => module.AdjustLyricOffset(.5);
    private void Advance_Click(object sender, RoutedEventArgs e) => module.AdjustLyricOffset(-.5);
    private async void Previous_Click(object sender, RoutedEventArgs e) =>
        await module.ExecuteMediaShortcutAsync(MediaShortcutAction.Previous);
    private async void Playback_Click(object sender, RoutedEventArgs e) =>
        await module.ExecuteMediaShortcutAsync(MediaShortcutAction.TogglePlayback);
    private async void Next_Click(object sender, RoutedEventArgs e) =>
        await module.ExecuteMediaShortcutAsync(MediaShortcutAction.Next);

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        await module.EnsureDesktopLyricSettingsAsync();
        if (closed || locked || !IsVisible) return;
        settingsMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        var menu = new ContextMenu
        {
            PlacementTarget = SettingsButton, Placement = PlacementMode.Bottom,
            Style = (Style)FindResource("MeowFilm.ContextMenu"), Focusable = false
        };
        DesktopTheme.SetIsHighContrast(menu, DesktopTheme.GetIsHighContrast(this));
        var layout = Item("显示样式");
        AddChoice(layout, "单行", module.DesktopLyricLayout == "single",
            async () => await module.SetDesktopLyricLayoutAsync("single"));
        AddChoice(layout, "双行", module.DesktopLyricLayout == "double",
            async () => await module.SetDesktopLyricLayoutAsync("double"));
        layout.IsEnabled = module.CanEditDesktopLyricSettings;
        menu.Items.Add(layout);
        menu.Items.Add(ColorMenu("普通颜色", "normal", module.DesktopLyricNormalColor));
        menu.Items.Add(ColorMenu("逐字高亮颜色", "highlight", module.DesktopLyricHighlightColor));
        menu.Items.Add(new Separator());
        var translation = Item("显示译文");
        translation.IsCheckable = true;
        translation.IsChecked = module.ShowLyricTranslation;
        translation.Click += async (_, _) => await module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleTranslation);
        menu.Items.Add(translation);
        menu.Closed += SettingsClosed;
        settingsMenu = menu;
        ShowHover(true);
        menu.IsOpen = true;
    }

    private MenuItem Item(string header) => new()
    {
        Header = header, Style = (Style)FindResource("MeowFilm.MenuItem"), Focusable = false
    };

    private void AddChoice(MenuItem parent, string label, bool selected, Func<Task> apply)
    {
        var item = Item(label);
        item.IsCheckable = true; item.IsChecked = selected;
        item.Click += async (_, _) => await apply();
        parent.Items.Add(item);
    }

    private MenuItem ColorMenu(string label, string role, string selected)
    {
        var item = Item(label);
        item.IsEnabled = module.CanEditDesktopLyricSettings;
        foreach (var preset in MusicDesktopLyricColorWindow.Presets)
            AddChoice(item, preset.Name, selected == preset.Color,
                async () => await module.SetDesktopLyricColorAsync(role, preset.Color));
        item.Items.Add(new Separator());
        var custom = Item("自定义…");
        custom.Click += (_, _) => OpenColorEditor(role);
        item.Items.Add(custom);
        return item;
    }

    private void SettingsClosed(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu) menu.Closed -= SettingsClosed;
        if (ReferenceEquals(settingsMenu, sender)) settingsMenu = null;
        ObservePointer(null, EventArgs.Empty);
    }

    private void OpenColorEditor(string role)
    {
        settingsMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        colorWindow?.Close();
        var editor = new MusicDesktopLyricColorWindow(module, role) { Owner = this };
        DesktopTheme.SetIsHighContrast(editor, DesktopTheme.GetIsHighContrast(this));
        colorWindow = editor;
        editor.Closed += (_, _) =>
        {
            if (ReferenceEquals(colorWindow, editor)) colorWindow = null;
            ObservePointer(null, EventArgs.Empty);
        };
        try { editor.Show(); ShowHover(true); }
        catch { colorWindow = null; editor.Close(); throw; }
    }

    private async void Lock_Click(object sender, RoutedEventArgs e) => await ChangeLockAsync(true);

    internal void UnlockLyrics() => _ = ChangeLockAsync(false);

    private async Task ChangeLockAsync(bool value)
    {
        try { SetLocked(value); }
        catch (Win32Exception)
        {
            await module.Notify(value ? "无法锁定桌面歌词，请重试。" : "无法解锁桌面歌词，请重试。");
        }
    }

    internal void SetLocked(bool value)
    {
        Dispatcher.VerifyAccess();
        if (closed || locked == value) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (value)
        {
            ClosePopups();
            ReleaseOwnedCapture();
            UpdateUnlockAnchorOffset();
            var unlock = new MusicDesktopLyricsUnlockWindow(this);
            DesktopTheme.SetIsHighContrast(unlock, DesktopTheme.GetIsHighContrast(this));
            try
            {
                // Do not retire the usable HWND until its independent recovery control exists.
                unlock.Show();
                unlockWindow = unlock;
                PlaceUnlock();
                MusicDesktopLyricsNative.SetInputStyle(handle, clickThrough: true);
            }
            catch
            {
                unlockWindow = null;
                unlock.Close();
                throw;
            }
            locked = true;
            pointerObserver.Stop();
            ShowHover(false);
        }
        else
        {
            MusicDesktopLyricsNative.SetInputStyle(handle, clickThrough: false);
            locked = false;
            var unlock = unlockWindow; unlockWindow = null;
            unlock?.Close();
            StartPointerObserver();
        }
    }

    internal MusicDesktopLyricsWindowState CapturePresentationState()
    {
        MusicDesktopLyricsGeometry? geometry = null;
        if (TryCaptureGeometry(out var current)) geometry = current;
        else _ = module.Notify("桌面歌词位置和大小保存失败");
        return new(Left, Top, Width, Height, locked, userSized, geometry);
    }

    internal void RestorePresentationState(MusicDesktopLyricsWindowState state)
    {
        Left = state.Left; Top = state.Top;
        Width = Math.Max(state.Width, MinWidth);
        Height = Math.Max(state.Height, MinHeight);
        openingGeometry = state.Geometry;
        userSized = state.UserSized;
        if (IsLoaded) _ = ChangeLockAsync(state.IsLocked);
        else restoreLock = state.IsLocked;
    }

    private void UpdateUnlockAnchorOffset()
    {
        bool restoreToolbar = Toolbar.Visibility != Visibility.Visible;
        ShowHover(true);
        UpdateLayout();
        var root = (FrameworkElement)Content;
        var lockCenter = LockButton.TranslatePoint(new(LockButton.ActualWidth / 2, LockButton.ActualHeight / 2), root);
        ((System.Windows.Media.TranslateTransform)UnlockAnchor.RenderTransform).X = lockCenter.X - root.ActualWidth / 2;
        if (restoreToolbar) ShowHover(false);
    }

    private void QueueUnlockPlacement()
    {
        if (closed || unlockWindow is null || reposition is not null) return;
        reposition = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(async () =>
        {
            reposition = null;
            try { PlaceUnlock(); }
            catch (Win32Exception)
            {
                await ChangeLockAsync(false);
                await module.Notify("无法定位解锁按钮，已尝试解除歌词锁定。");
            }
        }));
    }

    private void PlaceUnlock()
    {
        if (!IsVisible || unlockWindow is null || !UnlockAnchor.IsArrangeValid) return;
        var start = UnlockAnchor.PointToScreen(new());
        var end = UnlockAnchor.PointToScreen(new(UnlockAnchor.ActualWidth, UnlockAnchor.ActualHeight));
        MusicDesktopLyricsNative.Place(new WindowInteropHelper(unlockWindow).Handle, start, end);
    }

    protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); QueueUnlockPlacement(); }
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        QueueUnlockPlacement();
    }

    private void ReleaseOwnedCapture()
    {
        if (Mouse.Captured is DependencyObject target && (IsAncestorOf(target) || Window.GetWindow(target) == this))
            Mouse.Capture(null);
    }

    private void ClosePopups()
    {
        if (settingsMenu is { } menu)
        {
            menu.Closed -= SettingsClosed;
            menu.IsOpen = false;
            settingsMenu = null;
        }
        colorWindow?.Close();
        colorWindow = null;
        foreach (var button in Toolbar.Children.OfType<Button>())
            ToolTipService.SetIsEnabled(button, false);
    }

    private async void Close_Click(object sender, RoutedEventArgs e) => await CloseAsync();

    internal Task CloseAsync()
    {
        Close();
        return closeWork;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        if (!closeReady && module.DesktopLyricSettingsLoaded && !closed)
        {
            e.Cancel = true;
            if (!closePending)
            {
                closePending = true;
                IsEnabled = false;
                pointerObserver.Stop();
                module.DesktopLyricsClosing();
                closeWork = CloseAfterGeometrySaveAsync();
            }
            return;
        }
        closed = true;
        pointerObserver.Stop();
        ClosePopups();
        ReleaseOwnedCapture();
        reposition?.Abort(); reposition = null;
        var unlock = unlockWindow; unlockWindow = null;
        unlock?.Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        pointerObserver.Tick -= ObservePointer;
        Loaded -= WindowLoaded;
        IsVisibleChanged -= VisibilityChanged;
        module.PropertyChanged -= ModuleChanged;
        if (source is { IsDisposed: false }) source.RemoveHook(WindowMessage);
        SystemParameters.StaticPropertyChanged -= SystemSettingChanged;
        source = null;
        DataContext = null;
        base.OnClosed(e);
    }

    private async Task CloseAfterGeometrySaveAsync()
    {
        Task save = TryCaptureGeometry(out var geometry)
            ? module.SaveDesktopLyricGeometryAsync(geometry)
            : module.Notify("桌面歌词位置和大小保存失败");
        // A deduplicated save can complete synchronously; leave OnClosing before
        // issuing the final Close, while retaining the geometry captured above.
        await Task.Yield();
        try
        {
            await save;
        }
        finally
        {
            closeReady = true;
            if (!closed) Close();
        }
    }

    internal void CloseForShutdown()
    {
        closeReady = true;
        Close();
    }
}
