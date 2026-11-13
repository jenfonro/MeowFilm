using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

internal readonly record struct MusicDesktopLyricsWindowState(
    double Left, double Top, double Width, double Height, bool IsLocked, bool UserSized);

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
    private Size sizeAtMove;

    internal bool IsLyricsLocked => locked;
    internal bool IsPointerObserverRunning => pointerObserver.IsEnabled;
    internal MusicDesktopLyricsUnlockWindow? UnlockWindow => unlockWindow;

    internal MusicDesktopLyricsWindow(MusicCapability module, Window anchor)
    {
        this.module = module;
        InitializeComponent();
        UnlockAnchor.RenderTransform = new System.Windows.Media.TranslateTransform();
        DataContext = module;
        UpdateLayoutHeight();
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
        if (e.PropertyName == nameof(MusicCapability.DesktopLyricLayout)) UpdateLayoutHeight();
    }

    // Window's native resize publication takes local precedence over style setters.
    private void UpdateLayoutHeight()
    {
        if (userSized || sizing) return;
        double next = module.DesktopLyricLayout == "double" ? 240 : 144;
        double previous = Height;
        Height = next;
        // Keep the bottom anchor when loading a saved double layout near the screen edge.
        if (IsLoaded) Top += previous - next;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        source = HwndSource.FromHwnd(handle)!;
        source.AddHook(WindowMessage);
        MusicDesktopLyricsNative.SetInputStyle(handle, clickThrough: false);
        MusicDesktopLyricsNative.SetResizableStyle(handle);
        ApplyHighContrast(SystemParameters.HighContrast);
        SystemParameters.StaticPropertyChanged += SystemSettingChanged;
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return 3; } // WM_MOUSEACTIVATE / MA_NOACTIVATE
        if (message == 0x0084 && !locked)
        {
            var hit = MusicDesktopLyricsNative.ResizeHitTest(hwnd, lParam);
            if (hit != 0) { handled = true; return hit; }
        }
        if (message == 0x0231)
        {
            sizing = true;
            sizeAtMove = new(ActualWidth, ActualHeight);
        }
        else if (message == 0x0232)
        {
            if (sizing && (Math.Abs(ActualWidth - sizeAtMove.Width) > .5 ||
                Math.Abs(ActualHeight - sizeAtMove.Height) > .5))
                userSized = true;
            sizing = false;
        }
        if (message == 0x02E0) QueueUnlockPlacement(); // Let WPF accept WM_DPICHANGED first.
        return 0;
    }

    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (restoreLock) { restoreLock = false; _ = ChangeLockAsync(true); }
        else StartPointerObserver();
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
        finally { dragging = false; ObservePointer(null, EventArgs.Empty); }
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

    internal MusicDesktopLyricsWindowState CapturePresentationState() =>
        new(Left, Top, Width, Height, locked, userSized);

    internal void RestorePresentationState(MusicDesktopLyricsWindowState state)
    {
        Left = state.Left; Top = state.Top; Width = state.Width; Height = state.Height;
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

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
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
}
