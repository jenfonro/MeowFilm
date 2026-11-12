using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MeowFilm.Capabilities;

public partial class FilmPlaybackView
{
    private readonly DispatcherTimer controlsTimer = new() { Interval = TimeSpan.FromMilliseconds(2200) };
    private readonly DispatcherTimer pictureClickTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private ContextMenu? playerMenu;
    private Window? keyboardWindow;
    private FilmPlaybackPage? clickPage;
    private string? clickIdentity;
    private bool playerInputInitialized, keyboardControls, wasPlaying, wasBusy, wasBuffering;
    private int audibleVolume = 80;
    private Rect? overlayViewport;
    private readonly ScaleTransform controlsScale = new();
    private (double Width, string Pan, string Quality, int Pans, int Qualities, bool Actions, bool Volume, string Time, bool Contrast)? controlLayout;

    private void InitializePlayerInput()
    {
        if (playerInputInitialized) return;
        playerInputInitialized = true;
        controlsTimer.Tick += (_, _) =>
        {
            controlsTimer.Stop();
            if (CanHidePlayerControls()) SetPlayerControls(false);
        };
        pictureClickTimer.Tick += async (_, _) =>
        {
            pictureClickTimer.Stop();
            var page = clickPage; var identity = clickIdentity; clickPage = null; clickIdentity = null;
            if (page is not null && ReferenceEquals(page, boundPage) && identity == page.MediaIdentity)
                await ExecuteAsync(page, page.PlayAsync);
        };
    }

    private void DetachOverlay()
    {
        if (mediaSurface is { } surface)
        {
            ClosePlayerMenus();
            // VideoView resets its Content DP to null while keeping the child in
            // its foreground window. A non-UI value forces its change callback
            // to release that child even when Content already reads null.
            surface.Content = string.Empty;
            surface.Content = null;
            mediaSurface = null;
        }
        if (!VideoPicture.Children.Contains(PlayerOverlay)) VideoPicture.Children.Add(PlayerOverlay);
    }

    private void Overlay_Loaded(object sender, RoutedEventArgs e)
    {
        InitializePlayerInput();
        ShowPlayerControls();
        UpdateOverlayClip();
    }
    private void Overlay_Unloaded(object sender, RoutedEventArgs e) => CancelPlayerInput();
    private void Overlay_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateOverlayClip();
    private void AttachKeyboard()
    {
        var window = Window.GetWindow(this);
        if (ReferenceEquals(window, keyboardWindow)) return;
        DetachKeyboard(); keyboardWindow = window;
        if (window is not null)
        {
            window.PreviewKeyDown += Player_KeyDown;
            window.PreviewMouseDown += Menu_MouseDown;
        }
    }
    private void DetachKeyboard()
    {
        if (keyboardWindow is { } window)
        {
            window.PreviewKeyDown -= Player_KeyDown;
            window.PreviewMouseDown -= Menu_MouseDown;
        }
        keyboardWindow = null;
    }

    private bool CanHidePlayerControls() => boundPage is { IsPlaying: true, PlayerBusy: false, PlayerBuffering: false } &&
        !HasPlayerMenu && seekingPage is null && !(keyboardControls && ControlBar.IsKeyboardFocusWithin);
    private bool HasPlayerMenu => playerMenu?.IsOpen == true || SettingsPanel.Visibility == Visibility.Visible;
    private void ShowPlayerControls()
    {
        if (PlayerChrome is null) return;
        InitializePlayerInput();
        controlsTimer.Stop();
        SetPlayerControls(true);
        if (CanHidePlayerControls()) controlsTimer.Start();
    }
    private void SetPlayerControls(bool visible)
    {
        bool wasVisible = PlayerChrome.Visibility == Visibility.Visible;
        PlayerChrome.BeginAnimation(OpacityProperty, null);
        PlayerChrome.IsHitTestVisible = visible;
        PlayerChrome.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        PlayerChrome.Opacity = visible ? 1 : 0;
        if (visible && !wasVisible && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
            PlayerChrome.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }
    private void Player_MouseEnter(object sender, MouseEventArgs e) => ShowPlayerControls();
    private void Player_MouseMove(object sender, MouseEventArgs e)
    {
        keyboardControls = false;
        ShowPlayerControls();
    }
    private void Player_MouseLeave(object sender, MouseEventArgs e)
    {
        controlsTimer.Stop();
        if (CanHidePlayerControls()) SetPlayerControls(false);
    }
    private void Controls_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Do not mark handled: the slider/button must still receive its native WPF input.
        keyboardControls = false;
        if (e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject target &&
            (ReferenceEquals(target, SeekSlider) || SeekSlider.IsAncestorOf(target)))
            Seek_Begin(SeekSlider, e);
        ShowPlayerControls();
    }
    private async void Controls_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject target &&
            (ReferenceEquals(target, SeekSlider) || SeekSlider.IsAncestorOf(target)))
            await CommitSeekAsync();
    }
    private static bool IsPlayerControl(DependencyObject? target)
    {
        for (var item = target; item is not null; item = item is Visual ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item))
            if (item is ButtonBase or Slider or Selector or TextBoxBase or PasswordBox or MenuItem) return true;
        return false;
    }
    private void Picture_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsPlayerControl(e.OriginalSource as DependencyObject) || SettingsPanel.IsMouseOver || ControlBar.IsMouseOver) return;
        bool menuWasOpen = HasPlayerMenu;
        ClosePlayerMenus();
        if (menuWasOpen) { e.Handled = true; return; }
        if (boundPage?.ShortVideo == true) return; // The picture's swipe surface owns this gesture.
        e.Handled = true;
        PlayerOverlay.Focus();
        if (e.ClickCount >= 2)
        {
            pictureClickTimer.Stop(); clickPage = null; clickIdentity = null;
            ToggleFullscreen();
        }
        else SchedulePictureClick();
    }
    private void SchedulePictureClick()
    {
        InitializePlayerInput();
        pictureClickTimer.Stop();
        clickPage = boundPage; clickIdentity = boundPage?.MediaIdentity;
        pictureClickTimer.Start(); ShowPlayerControls();
    }
    private async void Player_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None ||
            boundPage is not { } page || !PlayerOverlay.IsVisible) return;
        if (e.Key == Key.Escape && playerWindow is { } window)
        {
            window.Close(); e.Handled = true; return;
        }
        keyboardControls = true;
        ShowPlayerControls();
        if (IsPlayerControl(e.OriginalSource as DependencyObject)) return;
        switch (e.Key)
        {
            case Key.Space:
                if (e.IsRepeat) return;
                e.Handled = true; await ExecuteAsync(page, page.PlayAsync); break;
            case Key.Left when page.CanSeek:
            case Key.Right when page.CanSeek:
                e.Handled = true;
                await ExecuteAsync(page, () => page.SeekAsync(Math.Clamp(page.Position + (e.Key == Key.Left ? -5 : 5), 0, page.Duration)), "跳转失败");
                break;
        }
    }
    private void CancelPlayerInput()
    {
        controlsTimer.Stop(); pictureClickTimer.Stop();
        clickPage = null; clickIdentity = null;
        ClosePlayerMenus();
        CancelSeek();
        swipeStart = null;
        if (ShortVideoGesture.IsMouseCaptured) ShortVideoGesture.ReleaseMouseCapture();
        ((RotateTransform)BufferSpinner.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, null);
        BufferSpinner.Visibility = Visibility.Collapsed;
        wasBuffering = false;
    }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        bool opening = SettingsPanel.Visibility != Visibility.Visible;
        ClosePlayerMenus();
        SettingsPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetItemStatus(SettingsButton, opening ? "已展开" : "已收起");
        ShowPlayerControls();
    }
    private void ClosePlayerMenus()
    {
        if (playerMenu is { } menu) { playerMenu = null; menu.IsOpen = false; }
        SettingsPanel.Visibility = Visibility.Collapsed;
        AutomationProperties.SetItemStatus(SettingsButton, "已收起");
    }
    private void Menu_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            ReferenceEquals(source, SettingsButton) || SettingsButton.IsAncestorOf(source) ||
            ReferenceEquals(source, SettingsPanel) || SettingsPanel.IsAncestorOf(source)) return;
        if (HasPlayerMenu) ClosePlayerMenus();
    }
    private void Volume_Enter(object sender, MouseEventArgs e)
    {
        VolumeSlider.Visibility = Visibility.Visible;
        UpdateControlLayout();
    }
    private void Volume_Leave(object sender, MouseEventArgs e)
    {
        if (VolumeSlider.IsKeyboardFocusWithin) return;
        VolumeSlider.Visibility = Visibility.Collapsed;
        UpdateControlLayout();
    }
    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (boundPage is not { HasOutput: true } page) return;
        if (page.Volume > 0) { audibleVolume = page.Volume; page.Volume = 0; }
        else page.Volume = audibleVolume;
        ShowPlayerControls();
    }
    private void UpdatePlayerWindowState()
    {
        bool full = playerWindow is not null && !pip;
        string fullscreenName = full ? "退出全屏" : "全屏";
        string pipName = playerWindow is not null && pip ? "退出画中画" : "画中画";
        FullscreenButton.ToolTip = fullscreenName; AutomationProperties.SetName(FullscreenButton, fullscreenName);
        PipButton.ToolTip = pipName; AutomationProperties.SetName(PipButton, pipName);
        AutomationProperties.SetItemStatus(PipButton, pip ? "已开启" : "已关闭");
        ((System.Windows.Shapes.Path)FullscreenButton.Content).Data = Geometry.Parse(full
            ? "M4,8 H8 V4 H10 V10 H4 Z M14,4 H16 V8 H20 V10 H14 Z M4,14 H10 V20 H8 V16 H4 Z M14,14 H20 V16 H16 V20 H14 Z"
            : "M4,4 H10 V6 H6 V10 H4 Z M14,4 H20 V10 H18 V6 H14 Z M4,14 H6 V18 H10 V20 H4 Z M18,14 H20 V20 H14 V18 H18 Z");
    }
    private void UpdatePlayerState()
    {
        if (boundPage is not { } page) return;
        if (wasPlaying != page.IsPlaying || wasBusy != page.PlayerBusy || wasBuffering != page.PlayerBuffering || !page.IsPlaying)
        {
            wasPlaying = page.IsPlaying; wasBusy = page.PlayerBusy;
            ShowPlayerControls();
        }
        if (wasBuffering != page.PlayerBuffering)
        {
            wasBuffering = page.PlayerBuffering;
            BufferSpinner.Visibility = wasBuffering ? Visibility.Visible : Visibility.Collapsed;
            ((RotateTransform)BufferSpinner.RenderTransform).BeginAnimation(RotateTransform.AngleProperty,
                wasBuffering && SystemParameters.ClientAreaAnimation
                    ? new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever }
                    : null);
        }
        if (page.Volume > 0) audibleVolume = page.Volume;
        string muteLabel = page.Volume == 0 ? "取消静音" : "静音";
        AutomationProperties.SetName(MuteButton, muteLabel); MuteButton.ToolTip = muteLabel;
        VolumeIcon.Data = Geometry.Parse(page.Volume == 0
            ? "M3,10 V14 H7 L12,19 V5 L7,10 Z M15,8 L17.5,10.5 L20,8 L21,9 L18.5,11.5 L21,14 L20,15 L17.5,12.5 L15,15 L14,14 L16.5,11.5 L14,9 Z"
            : "M3,10 V14 H7 L12,19 V5 L7,10 Z M14,8 A4.5,4.5 0 0 1 14,16 L14,14 A2.5,2.5 0 0 0 14,10 Z");
        if (seekingPage is null) PlayerTime.Text = page.ProgressLabel;
        UpdateControlLayout(); UpdateOverlayClip();
    }
    private void ControlBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateControlLayout();
    private void UpdateControlLayout()
    {
        if (boundPage is not { } page || ControlsRow.ActualWidth <= 0) return;
        var key = (ControlsRow.ActualWidth, page.PlayerPanLabel, page.PlayerQualityLabel,
            page.PlayerPanOptions.Count, page.PlayerQualityOptions.Count, page.ShowPlayerSwitchActions,
            VolumeSlider.Visibility == Visibility.Visible, PlayerTime.Text, SystemParameters.HighContrast);
        if (controlLayout == key) return;
        controlLayout = key;
        controlsScale.ScaleX = controlsScale.ScaleY = 1;
        PanButton.GetBindingExpression(ContentControl.ContentProperty)?.UpdateTarget();
        QualityButton.GetBindingExpression(ContentControl.ContentProperty)?.UpdateTarget();
        PanButton.Visibility = key.Item4 > 0 ? Visibility.Visible : Visibility.Collapsed;
        QualityButton.Visibility = key.Item5 > 0 ? Visibility.Visible : Visibility.Collapsed;
        SwitchButton.Visibility = BlockButton.Visibility = key.Item6 ? Visibility.Visible : Visibility.Collapsed;
        PipButton.Visibility = Visibility.Visible;
        var size = new Size(double.PositiveInfinity, double.PositiveInfinity);
        LeftPill.Child.InvalidateMeasure();
        LeftPill.InvalidateMeasure();
        LeftPill.Measure(size);
        double required = LeftPill.DesiredSize.Width + RightPill.Padding.Left + RightPill.Padding.Right +
            RightPill.BorderThickness.Left + RightPill.BorderThickness.Right + 12;
        // Child visibility does not immediately invalidate every ancestor's cached
        // DesiredSize. Measure each button once, then subtract its full width and gap.
        foreach (FrameworkElement button in ((Panel)RightPill.Child).Children)
        {
            button.Measure(size);
            required += button.DesiredSize.Width;
        }
        foreach (var button in new[] { PanButton, QualityButton, SwitchButton, BlockButton, PipButton })
        {
            if (required <= ControlsRow.ActualWidth + .5) break;
            required -= button.DesiredSize.Width;
            button.Visibility = Visibility.Collapsed;
        }
        // A minimum-size PiP can be narrower than the two essential capsules.
        // Keep their gap and contents instead of drawing one over the other.
        controlsScale.ScaleX = controlsScale.ScaleY =
            Math.Clamp((ControlsRow.ActualWidth - 12) / (required - 12), 0, 1);
    }
    private void Page_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateOverlayClip();
    private void UpdateOverlayClip()
    {
        if (!PlayerOverlay.IsLoaded) return;
        SettingsPanel.MaxHeight = Math.Max(0, PlayerOverlay.ActualHeight -
            SettingsPanel.Margin.Top - SettingsPanel.Margin.Bottom);
        var visible = new Rect(0, 0, PlayerOverlay.ActualWidth, PlayerOverlay.ActualHeight);
        // A native foreground window is not a child of the ScrollViewer: explicitly carry its viewport clip.
        if (playerWindow is null)
        {
            // Loaded notifications can still be queued while the surface is being
            // reparented. PointToScreen requires a live presentation source.
            var source = PresentationSource.FromVisual(VideoPicture);
            if (source?.CompositionTarget is not { } target || PresentationSource.FromVisual(PageScroll) is null) return;
            var viewport = PageScroll.PointToScreen(new Point(0, 0));
            var origin = VideoPicture.PointToScreen(new Point(0, 0));
            var transform = target.TransformFromDevice;
            var offset = transform.Transform(viewport) - transform.Transform(origin);
            visible.Intersect(new Rect(offset.X, offset.Y, PageScroll.ActualWidth, PageScroll.ActualHeight));
        }
        if (overlayViewport == visible) return;
        overlayViewport = visible;
        PlayerOverlay.Clip = visible.IsEmpty ? Geometry.Empty : new RectangleGeometry(visible);
    }
}
