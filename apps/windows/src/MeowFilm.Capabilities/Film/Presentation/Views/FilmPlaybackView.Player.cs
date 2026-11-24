using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace MeowFilm.Capabilities;

public partial class FilmPlaybackView
{
    private readonly DispatcherTimer controlsTimer = new() { Interval = TimeSpan.FromMilliseconds(2200) };
    private readonly DispatcherTimer pictureClickTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private MenuFlyout? playerMenu;
    private FilmPlaybackPage? clickPage;
    private string? clickIdentity;
    private bool playerInputInitialized, keyboardControls, wasPlaying, wasBusy, wasBuffering;
    private int audibleVolume = 80;
    private bool? playingGlyph;
    private (double Width, string Pan, string Quality, int Pans, int Qualities, bool Actions, bool Volume, int TimeLength)? controlLayout;
    private Button[]? collapsibleButtons;
    private Geometry? playGlyphGeometry, pauseGlyphGeometry;

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
                await ExecuteAsync(page, page.PauseOrResumeAsync);
        };
    }

    private void DetachControls()
    {
        if (mediaSurface is { } surface)
        {
            ClosePlayerMenus(); surface.Content = null; mediaSurface = null;
        }
        if (!VideoPicture.Children.Contains(PlayerOverlay)) VideoPicture.Children.Add(PlayerOverlay);
    }
    private void Overlay_Loaded(object sender, RoutedEventArgs e) { InitializePlayerInput(); ShowPlayerControls(); }
    private void Overlay_Unloaded(object sender, RoutedEventArgs e)
    {
        if (PlayerOverlay.IsLoaded) return;
        CancelPlayerInput();
    }
    private void Overlay_SizeChanged(object sender, SizeChangedEventArgs e) =>
        SettingsPanel.MaxHeight = Math.Max(0, PlayerOverlay.ActualHeight - SettingsPanel.Margin.Top - SettingsPanel.Margin.Bottom);
    private bool CanHidePlayerControls() => boundPage is { IsPlaying: true, PlayerBusy: false, PlayerBuffering: false } &&
        !HasPlayerMenu && seekingPage is null && !(keyboardControls &&
            DesktopVisual.ContainsFocus(ControlBar));
    private bool HasPlayerMenu => playerMenu is not null || SettingsPanel.Visibility == Visibility.Visible;
    private void ShowPlayerControls()
    {
        if (PlayerChrome is null) return;
        InitializePlayerInput(); controlsTimer.Stop(); SetPlayerControls(true);
        if (CanHidePlayerControls()) controlsTimer.Start();
    }
    private void SetPlayerControls(bool visible)
    {
        bool becomingVisible = visible && PlayerChrome.Opacity == 0;
        PlayerChrome.IsHitTestVisible = visible;
        // Keep the measured control tree; opacity is composed with the picture, never in another window.
        PlayerChrome.Opacity = visible ? 1 : 0;
        if (becomingVisible) UpdateControlLayout();
    }
    private void Player_MouseEnter(object sender, PointerRoutedEventArgs e) => ShowPlayerControls();
    private void Player_MouseMove(object sender, PointerRoutedEventArgs e) { keyboardControls = false; ShowPlayerControls(); }
    private void Player_MouseLeave(object sender, PointerRoutedEventArgs e)
    {
        controlsTimer.Stop(); if (CanHidePlayerControls()) SetPlayerControls(false);
    }
    private void Controls_MouseDown(object sender, PointerRoutedEventArgs e) => ShowPlayerControls();
    private static bool IsPlayerControl(DependencyObject? target)
    {
        for (var item = target; item is not null; item = VisualTreeHelper.GetParent(item))
            if (item is ButtonBase or Slider or Selector or TextBox or PasswordBox or MenuFlyoutItemBase) return true;
        return false;
    }
    private void Picture_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsPlayerControl(e.OriginalSource as DependencyObject) || boundPage?.ShortVideo == true) return;
        e.Handled = true;
        if (HasPlayerMenu) { ClosePlayerMenus(); return; }
        SchedulePictureClick();
    }
    private void Picture_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (IsPlayerControl(e.OriginalSource as DependencyObject)) return;
        e.Handled = true; pictureClickTimer.Stop(); clickPage = null; clickIdentity = null;
        ToggleFullscreen();
    }
    private void SchedulePictureClick()
    {
        InitializePlayerInput(); pictureClickTimer.Stop();
        clickPage = boundPage; clickIdentity = boundPage?.MediaIdentity;
        pictureClickTimer.Start(); ShowPlayerControls();
    }
    private async void Player_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled) return;
        if (!TryHandlePlayerKey(e.Key, e.KeyStatus.WasKeyDown, DesktopVisual.Modifiers, e.OriginalSource as DependencyObject, out var work))
            return;
        e.Handled = true;
        if (work is not null)
        {
            try { await work; }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }
    }
    private bool TryHandlePlayerKey(VirtualKey key, bool repeat, VirtualKeyModifiers modifiers,
        DependencyObject? source, out Task? work)
    {
        work = null;
        if (modifiers != VirtualKeyModifiers.None || boundPage is not { } page) return false;
        if (key == VirtualKey.Escape && playerWindow is { } window)
        {
            window.Close(); return true;
        }
        keyboardControls = true; ShowPlayerControls();
        if (IsPlayerControl(source)) return false;
        switch (key)
        {
            case VirtualKey.Space:
                if (repeat) return false;
                work = ExecuteAsync(page, page.PauseOrResumeAsync);
                return true;
            case VirtualKey.Left when page.CanSeek:
            case VirtualKey.Right when page.CanSeek:
                double target = Math.Clamp(page.Position + (key == VirtualKey.Left ? -5 : 5), 0, page.Duration);
                work = ExecuteAsync(page, () => page.SeekAsync(target), "跳转失败");
                return true;
            default: return false;
        }
    }
    private void CancelPlayerInput()
    {
        controlsTimer.Stop(); pictureClickTimer.Stop(); clickPage = null; clickIdentity = null;
        ClosePlayerMenus(); CancelSeek(); swipeStart = null; ShortVideoGesture.ReleasePointerCaptures();
        BufferSpinner.IsActive = false; BufferSpinner.Visibility = Visibility.Collapsed; wasBuffering = false;
    }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        bool opening = SettingsPanel.Visibility != Visibility.Visible; ClosePlayerMenus();
        SettingsPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetItemStatus(SettingsButton, opening ? "已展开" : "已收起"); ShowPlayerControls();
    }
    private void ClosePlayerMenus()
    {
        if (playerMenu is { } menu) { playerMenu = null; menu.Hide(); }
        SettingsPanel.Visibility = Visibility.Collapsed;
        AutomationProperties.SetItemStatus(SettingsButton, "已收起");
    }
    private void Volume_Enter(object sender, PointerRoutedEventArgs e)
    {
        VolumeSlider.Visibility = Visibility.Visible; UpdateControlLayout();
    }
    private void Volume_Leave(object sender, PointerRoutedEventArgs e)
    {
        if (!DesktopVisual.ContainsFocus(VolumeSlider))
            VolumeSlider.Visibility = Visibility.Collapsed;
        UpdateControlLayout();
    }
    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (boundPage is not { HasOutput: true } page) return;
        if (page.Volume > 0) { audibleVolume = page.Volume; page.Volume = 0; } else page.Volume = audibleVolume;
        ShowPlayerControls();
    }
    private void UpdatePlayerWindowState()
    {
        bool full = playerWindow is not null && !pip;
        string fullscreenName = full ? "退出全屏" : "全屏";
        string pipName = playerWindow is not null && pip ? "退出画中画" : "画中画";
        AutomationProperties.SetName(FullscreenButton, fullscreenName);
        AutomationProperties.SetName(PipButton, pipName);
        AutomationProperties.SetItemStatus(PipButton, pip ? "已开启" : "已关闭");
    }
    private void UpdatePlayerProgress()
    {
        if (boundPage is not { } page || seekingPage is not null) return;
        int previousLength = PlayerTime.Text.Length;
        PlayerTime.Text = page.ProgressLabel;
        SeekSlider.Maximum = Math.Max(0, page.Duration);
        SeekSlider.Value = page.Position;
        if (PlayerTime.Text.Length != previousLength) UpdateControlLayout();
    }

    private void UpdatePlayerState()
    {
        if (boundPage is not { } page) return;
        if (wasPlaying != page.IsPlaying || wasBusy != page.PlayerBusy || wasBuffering != page.PlayerBuffering || !page.IsPlaying)
        {
            wasPlaying = page.IsPlaying; wasBusy = page.PlayerBusy; ShowPlayerControls();
        }
        wasBuffering = page.PlayerBuffering;
        BufferSpinner.Visibility = wasBuffering ? Visibility.Visible : Visibility.Collapsed;
        BufferSpinner.IsActive = wasBuffering;
        if (page.Volume > 0) audibleVolume = page.Volume;
        string muteLabel = page.Volume == 0 ? "取消静音" : "静音";
        AutomationProperties.SetName(MuteButton, muteLabel);
        if (playingGlyph != page.IsPlaying)
        {
            playingGlyph = page.IsPlaying;
            PlayIcon.Data = page.IsPlaying
                ? pauseGlyphGeometry ??= DesktopVisual.Geometry("M6,5 H10 V19 H6 Z M14,5 H18 V19 H14 Z")
                : playGlyphGeometry ??= DesktopVisual.Geometry("M8,5 V19 L19,12 Z");
        }
        if (seekingPage is null)
        {
            PlayerTime.Text = page.ProgressLabel;
            SeekSlider.Maximum = Math.Max(0, page.Duration); SeekSlider.Value = page.Position;
        }
        RetryMediaRecoveryButton.Visibility = page.NoOutput && page.CanRetryMedia ? Visibility.Visible : Visibility.Collapsed;
        OverviewToggle.Content = page.OverviewExpanded ? "收起简介" : "简介";
        AutomationProperties.SetName(OverviewToggle, page.OverviewExpanded ? "收起简介" : "展开简介");
        AutomationProperties.SetItemStatus(OverviewToggle, page.OverviewExpanded ? "已展开" : "已收起");
        OverviewToggle.Visibility = page.Overview.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(FavoriteButton, (page.IsFavorite ? "取消收藏 " : "收藏 ") + page.Title);
        UpdateControlLayout();
    }
    private void ControlBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateControlLayout();
    private void UpdateControlLayout()
    {
        if (boundPage is not { } page || ControlsRow.ActualWidth <= 0 || PlayerChrome.Opacity == 0) return;
        int panCount = page.PlayerPanOptionCount;
        int qualityCount = page.PlayerQualityOptionCount;
        var key = (ControlsRow.ActualWidth, page.PlayerPanLabel, page.PlayerQualityLabel,
            panCount, qualityCount, page.ShowPlayerSwitchActions,
            VolumeSlider.Visibility == Visibility.Visible, PlayerTime.Text.Length);
        if (controlLayout == key) return;
        controlLayout = key;
        PanButton.Visibility = panCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        QualityButton.Visibility = qualityCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        SwitchButton.Visibility = BlockButton.Visibility = page.ShowPlayerSwitchActions ? Visibility.Visible : Visibility.Collapsed;
        PipButton.Visibility = Visibility.Visible;
        LeftPill.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        RightPill.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double required = LeftPill.DesiredSize.Width + RightPill.DesiredSize.Width + 12;
        collapsibleButtons ??= [PanButton, QualityButton, SwitchButton, BlockButton, PipButton];
        foreach (var button in collapsibleButtons)
        {
            if (required <= ControlsRow.ActualWidth) break;
            required -= button.DesiredSize.Width; button.Visibility = Visibility.Collapsed;
        }
        Grid.SetRow(RightPill, required > ControlsRow.ActualWidth ? 1 : 0);
    }
}
