using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MeowFilm.Capabilities;

public partial class FilmPlaybackView : UserControl
{
    private const double MetadataFade = 20;
    private (bool Top, bool Bottom, double Height) metadataFade;
    private FilmPlaybackPage? boundPage, seekingPage;
    private FilmDirectoryDialog? directory;
    private Window? playerWindow;
    private bool pip, selectingControl;
    private int rateIndex = 2, aspectIndex;
    private ContentControl? mediaSurface;
    private Point? swipeStart;
    private long lastSwipe;
    private long rateGeneration, aspectGeneration;
    private string? seekingIdentity;
    private string? controlsIdentity;

    public event EventHandler<FilmCardActivatedEventArgs>? RecommendationRequested;

    public FilmPlaybackView()
    {
        InitializeComponent();
        LeftPill.LayoutTransform = RightPill.LayoutTransform = controlsScale;
        // Keep the overlay's context when VideoView moves it to another window.
        // A binding also avoids re-entering DataContext inheritance in its change callback.
        PlayerOverlay.SetBinding(DataContextProperty, new Binding(nameof(DataContext)) { Source = this });
        DataContextChanged += (_, _) =>
        {
            CancelPlayerInput();
            if (boundPage is not null) boundPage.PropertyChanged -= PageChanged;
            directory?.Close(); directory = null;
            playerWindow?.Close();
            DetachOverlay();
            VideoHost.Content = null;
            boundPage = DataContext as FilmPlaybackPage;
            if (boundPage is not null) boundPage.PropertyChanged += PageChanged;
            seekingPage = null;
            rateGeneration++; aspectGeneration++;
            controlsIdentity = null;
            UpdateControlSelections();
            PageScroll.ScrollToTop();
            MetadataScroll.ScrollToTop();
            HorizontalEpisodes.ScrollToLeftEnd();
            RelatedScroll.ScrollToLeftEnd();
            UpdateMedia();
            ShowPlayerControls();
        };
        Loaded += (_, _) => { UpdateLayoutMode(); UpdateMedia(); AttachKeyboard(); };
        Unloaded += (_, _) =>
        {
            DetachKeyboard();
            if (!pip) { CancelPlayerInput(); DetachOverlay(); VideoHost.Content = null; }
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                directory?.Close();
                if (!pip)
                {
                    CancelPlayerInput();
                    playerWindow?.Close();
                    DetachOverlay();
                    VideoHost.Content = null;
                }
            }
            else UpdateMedia();
        };
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateLayoutMode();

    private void UpdateLayoutMode()
    {
        bool stacked = ActualWidth < 840;
        PageContent.Margin = new(stacked ? 24 : 32, 12, stacked ? 24 : 32, 32);
        PlayerColumn.Width = new(1.85, GridUnitType.Star);
        HeroGutter.Width = new(stacked ? 0 : 24);
        MetadataColumn.Width = stacked ? new(0) : new(1, GridUnitType.Star);
        HeroRowGutter.Height = new(stacked ? 20 : 0);
        Grid.SetColumn(MetadataPanel, stacked ? 0 : 2);
        Grid.SetRow(MetadataPanel, stacked ? 2 : 0);
        UpdateMetadataHeight();
        UpdateRailButtons();
    }

    private void Player_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateMetadataHeight();

    private void UpdateMetadataHeight()
    {
        if (MetadataPanel is not null)
        {
            MetadataPanel.Height = ActualWidth < 840 ? double.NaN : PlayerFrame.ActualHeight;
            MetadataOverlay.MinHeight = ActualWidth < 840 ? 300 : PlayerFrame.ActualHeight;
        }
    }

    // Fade only the edges where metadata continues beyond the panel.
    private void Metadata_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var scroll = MetadataScroll;
        (bool Top, bool Bottom, double Height) fade = (scroll.VerticalOffset > .5,
            scroll.ScrollableHeight - scroll.VerticalOffset > .5, scroll.ActualHeight);
        if (fade == metadataFade) return;
        metadataFade = fade;
        if ((!fade.Top && !fade.Bottom) || fade.Height < MetadataFade * 2)
        {
            scroll.ClearValue(OpacityMaskProperty);
            return;
        }
        var clear = Color.FromArgb(0, 0, 0, 0);
        double edge = MetadataFade / fade.Height;
        var mask = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute, StartPoint = new(0, 0), EndPoint = new(0, fade.Height)
        };
        mask.GradientStops.Add(new(fade.Top ? clear : Colors.Black, 0));
        mask.GradientStops.Add(new(Colors.Black, edge));
        mask.GradientStops.Add(new(Colors.Black, 1 - edge));
        mask.GradientStops.Add(new(fade.Bottom ? clear : Colors.Black, 1));
        mask.Freeze();
        scroll.OpacityMask = mask;
    }

    private async void Episode_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is FilmPlaybackPage page && sender is Button { Tag: FilmEpisodeItem episode })
            await ExecuteAsync(page, () => page.SelectEpisodeAsync(episode));
    }

    private void HorizontalLayout_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not FilmPlaybackPage page) return;
        page.SetListLayout(false);
        // Return to the selected episode, including one chosen near the end of the list.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!IsVisible || !ReferenceEquals(DataContext, page)) return;
            int index = page.SelectedEpisode is { } selected ? page.Episodes.ToList().IndexOf(selected) : 0;
            HorizontalEpisodes.ScrollToHorizontalOffset(index * 276);
        }));
    }

    private void PageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(new Action(UpdateMedia)); return; }
        UpdateMedia();
    }

    private void UpdateMedia()
    {
        if (boundPage is not { } page) return;
        if (controlsIdentity != page.MediaIdentity)
        {
            controlsIdentity = page.MediaIdentity;
            ClosePlayerMenus();
            CancelSeek();
            rateGeneration++; aspectGeneration++;
            UpdateControlSelections();
        }
        if (!page.OwnsNativeMedia || !pip && (!IsLoaded || !IsVisible))
        {
            DetachOverlay();
            VideoHost.Content = null;
            if (pip || !page.CanPlay) playerWindow?.Close();
        }
        else if (page.MediaView is { } surface)
        {
            if (!ReferenceEquals(mediaSurface, surface))
            {
                DetachOverlay();
                VideoPicture.Children.Remove(PlayerOverlay);
                mediaSurface = surface;
                surface.Content = PlayerOverlay;
            }
            if (!ReferenceEquals(VideoHost.Content, surface)) VideoHost.Content = surface;
        }
        UpdatePlayerState();
        if (seekingPage is null)
        {
            SeekSlider.GetBindingExpression(RangeBase.MaximumProperty)?.UpdateTarget();
            SeekSlider.Value = page.Position;
        }
        if (directory is { } dialog)
        {
            if (dialog.IsCurrent(page)) dialog.RefreshCurrent();
            else { dialog.Close(); directory = null; }
        }
    }
    private void UpdateControlSelections()
    {
        selectingControl = true;
        try
        {
            double rate = boundPage?.PlaybackRate ?? 1;
            rateIndex = RateBox.Items.Cast<ListBoxItem>().ToList().FindIndex(item =>
                item.Tag is string value && double.Parse(value, CultureInfo.InvariantCulture) == rate);
            aspectIndex = AspectBox.Items.Cast<ListBoxItem>().ToList().FindIndex(item =>
                Equals(item.Tag, boundPage?.PlaybackAspect ?? ""));
            RateBox.SelectedIndex = rateIndex;
            AspectBox.SelectedIndex = aspectIndex;
        }
        finally { selectingControl = false; }
    }

    private async Task ExecuteAsync(FilmPlaybackPage page, Func<Task> operation, string failure = "操作失败，请重试")
    {
        try { await operation(); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (ReferenceEquals(boundPage, page)) Notices.Message = FilmPlaybackError.Message(error, failure);
        }
    }

    private Task ActAsync(string action, object? parameters = null, string failure = "操作失败，请重试") =>
        DataContext is FilmPlaybackPage page ? ExecuteAsync(page, () => page.CommandAsync(action, parameters), failure) : Task.CompletedTask;
    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is FilmPlaybackPage page) await ExecuteAsync(page, page.PlayAsync, "播放操作失败");
    }
    private async void Previous_Click(object sender, RoutedEventArgs e) { e.Handled = true; await ActAsync("previous"); }
    private async void Next_Click(object sender, RoutedEventArgs e) { e.Handled = true; await ActAsync("next"); }
    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (boundPage is { } page && !page.PlayerActionPending("switch")) await ActAsync("switch");
    }
    private async void RefreshBrowsing_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is FilmPlaybackPage page)
            await ExecuteAsync(page, page.RefreshBrowsingAsync, "资料刷新失败");
    }
    private async void RetryMedia_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is FilmPlaybackPage page)
            await ExecuteAsync(page, page.RetryMediaAsync, "取址失败");
    }
    private async void Block_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not FilmPlaybackPage { HasCurrent: true, PlayerBusy: false } page) return;
        string attemptId = page.CurrentAttemptId!;
        await ExecuteAsync(page, () => page.CommandAsync("block", new { attemptId }), "片源屏蔽保存失败");
    }
    private void Pan_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (boundPage is { } page && sender is Button button)
            ShowMenu(button, page.PlayerPanOptions, "pan", "provider");
    }
    private void Quality_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (boundPage is { } page && sender is Button button)
            ShowMenu(button, page.PlayerQualityOptions, "quality", "quality");
    }
    private void ShowMenu(Button button, IReadOnlyList<FilmPlayerOption> options, string action, string field)
    {
        if (boundPage is not { } page || options.Count == 0) return;
        if (button.ContextMenu is { IsOpen: true } existing) { existing.IsOpen = false; return; }
        ClosePlayerMenus();
        var menu = new ContextMenu
        {
            PlacementTarget = button, Placement = PlacementMode.Top, VerticalOffset = -10,
            Style = (Style)FindResource("Film.PlayerMenu")
        };
        foreach (var choice in options)
        {
            var item = new MenuItem { Header = choice.Label, IsCheckable = true, IsChecked = choice.IsSelected,
                Style = (Style)FindResource("Film.PlayerMenuItem") };
            item.Click += async (_, _) =>
            {
                menu.IsOpen = false;
                if (ReferenceEquals(boundPage, page) && !choice.IsSelected && !page.PlayerActionPending(action, choice.Value))
                    await ExecuteAsync(page, () => page.CommandAsync(action, FilmJson.Encode(new Dictionary<string, string> { [field] = choice.Value })));
            };
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) =>
        {
            AutomationProperties.SetItemStatus(button, "已收起");
            if (ReferenceEquals(playerMenu, menu)) playerMenu = null;
            ShowPlayerControls();
        };
        button.ContextMenu = playerMenu = menu;
        AutomationProperties.SetItemStatus(button, "已展开");
        menu.IsOpen = true;
        ShowPlayerControls();
    }
    private async void SearchMore_Click(object sender, RoutedEventArgs e) { e.Handled = true; await ActAsync("search.more"); }
    private async void SearchSources_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is FilmPlaybackPage page)
            await ExecuteAsync(page, page.SearchSourcesAsync, "搜索失败，请重试");
    }
    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is FilmPlaybackPage page) await ExecuteAsync(page, page.ReconnectAsync, "重连失败");
    }
    private async void Favorite_Changed(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not FilmPlaybackPage page || sender is not ToggleButton toggle ||
            !page.CanFavorite || toggle.IsChecked == page.IsFavorite) return;
        try { await ExecuteAsync(page, page.ToggleFavoriteAsync, "收藏保存失败"); }
        finally { toggle.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget(); }
    }
    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not CheckBox box) return;
        try { await ActAsync("preview", new { enabled = box.IsChecked == true }, "点映设置保存失败"); }
        finally { box.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateTarget(); }
    }
    private async void ShortVideo_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not CheckBox box) return;
        try { await ActAsync("shortvideo", new { enabled = box.IsChecked == true }); }
        finally { box.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateTarget(); }
    }
    private async void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is FilmPlaybackPage { Applying: false } page &&
            SourcePicker.SelectedItem is FilmSelection value && value != page.SelectedSource)
            await ExecuteAsync(page, () => page.BrowseSourceAsync(value), "来源加载失败");
    }
    private async void Line_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is FilmPlaybackPage { Applying: false } page &&
            LinePicker.SelectedItem is FilmSelection value && value != page.SelectedLine)
            await ExecuteAsync(page, () => page.BrowseLineAsync(value), "线路加载失败");
    }
    private async void Season_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is FilmPlaybackPage page && sender is Button { Tag: FilmSelection value } &&
            value.Season != page.SelectedSeason?.Season)
            await ExecuteAsync(page, () => page.BrowseSeasonAsync(value), "季度加载失败");
    }
    private async void MetadataMapping_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is FilmPlaybackPage page && sender is Button { Tag: FilmSelection value } &&
            value.MetadataSourceId != page.MetadataSourceId)
            await ExecuteAsync(page, () => page.BrowseMetadataMappingAsync(value), "分季映射加载失败");
    }
    private async void Candidate_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is FilmPlaybackPage page && sender is Button { Tag: FilmCandidateItem value })
            await ExecuteAsync(page, () => page.SelectCandidateAsync(value.Id), "候选播放失败");
    }
    private void Directory_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not FilmPlaybackPage { CanBrowseRaw: true } page) return;
        if (directory is not null) { directory.Activate(); return; }
        var dialog = new FilmDirectoryDialog(page) { Owner = Window.GetWindow(this) };
        directory = dialog;
        dialog.Closed += (_, _) => { if (ReferenceEquals(directory, dialog)) directory = null; };
        dialog.Show();
    }
    private void Seek_Begin(object sender, MouseButtonEventArgs e)
    {
        if (boundPage is not { CanSeek: true }) return;
        seekingPage = boundPage;
        seekingIdentity = seekingPage?.MediaIdentity;
        ShowPlayerControls();
    }
    private async void Seek_End(object sender, MouseButtonEventArgs e) => await CommitSeekAsync();
    private async Task CommitSeekAsync()
    {
        var page = seekingPage; seekingPage = null;
        var identity = seekingIdentity; seekingIdentity = null;
        if (page is not null && ReferenceEquals(page, boundPage) && page.MediaIdentity == identity)
            await ExecuteAsync(page, () => page.SeekAsync(SeekSlider.Value), "跳转失败");
        if (seekingPage is null) CancelSeek();
        ShowPlayerControls();
    }
    private void Seek_CaptureLost(object sender, MouseEventArgs e)
    {
        // A normal release commits in the control bar's preview route, before Thumb drops capture.
        // Capture lost by deactivation/cancellation must not issue a seek.
        CancelSeek();
    }
    private void Seek_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CancelSeek();
    private void CancelSeek()
    {
        seekingPage = null; seekingIdentity = null;
        if (Mouse.Captured is UIElement captured &&
            (ReferenceEquals(captured, SeekSlider) || SeekSlider.IsAncestorOf(captured)))
            captured.ReleaseMouseCapture();
        if (boundPage is { } page)
        {
            SeekSlider.Value = page.Position;
            PlayerTime.Text = page.ProgressLabel;
            UpdateControlLayout();
        }
    }
    private void Seek_Preview(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (seekingPage is not null && boundPage is { } page)
        {
            PlayerTime.Text = $"{FilmJson.Time(SeekSlider.Value)} / {FilmJson.Time(page.Duration)}";
            UpdateControlLayout();
        }
    }
    private static bool IsSeekKey(Key key) =>
        key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End;
    private void Seek_KeyDown(object sender, KeyEventArgs e)
    {
        if (!IsSeekKey(e.Key) || e.IsRepeat || boundPage is not { CanSeek: true }) return;
        seekingPage = boundPage;
        seekingIdentity = seekingPage.MediaIdentity;
        ShowPlayerControls();
    }
    private async void Seek_KeyUp(object sender, KeyEventArgs e)
    {
        if (!IsSeekKey(e.Key)) return;
        e.Handled = true;
        await CommitSeekAsync();
    }
    private async void Rate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (selectingControl || DataContext is not FilmPlaybackPage { HasOutput: true } page ||
            RateBox.SelectedItem is not ListBoxItem { Tag: string text }) return;
        int previous = rateIndex, selected = RateBox.SelectedIndex;
        long ticket = ++rateGeneration; string? identity = page.MediaIdentity;
        try
        {
            await page.RateAsync(double.Parse(text, CultureInfo.InvariantCulture));
            if (ticket == rateGeneration && ReferenceEquals(page, boundPage) && identity == page.MediaIdentity)
                rateIndex = selected;
        }
        catch (Exception error)
        {
            if (ticket != rateGeneration || !ReferenceEquals(page, boundPage) || identity != page.MediaIdentity) return;
            selectingControl = true; RateBox.SelectedIndex = previous; selectingControl = false;
            if (error is not OperationCanceledException) Notices.Message = FilmPlaybackError.Message(error, "倍速设置失败");
        }
    }
    private async void Aspect_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (selectingControl || DataContext is not FilmPlaybackPage { HasOutput: true } page ||
            sender is not ListBox box || box.SelectedItem is not ListBoxItem { Tag: string text }) return;
        int previous = aspectIndex, selected = box.SelectedIndex;
        long ticket = ++aspectGeneration; string? identity = page.MediaIdentity;
        try
        {
            await page.AspectAsync(text.Length == 0 ? null : text);
            if (ticket == aspectGeneration && ReferenceEquals(page, boundPage) && identity == page.MediaIdentity)
                aspectIndex = selected;
        }
        catch (Exception error)
        {
            if (ticket != aspectGeneration || !ReferenceEquals(page, boundPage) || identity != page.MediaIdentity) return;
            selectingControl = true; box.SelectedIndex = previous; selectingControl = false;
            if (error is not OperationCanceledException) Notices.Message = FilmPlaybackError.Message(error, "比例设置失败");
        }
    }
    private void Fullscreen_Click(object sender, RoutedEventArgs e) { e.Handled = true; ToggleFullscreen(); }
    private void Pip_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (playerWindow is not null && pip) playerWindow.Close(); else OpenPlayerWindow(true);
    }
    private void ToggleFullscreen()
    {
        if (playerWindow is not null && !pip) playerWindow.Close(); else OpenPlayerWindow(false);
    }
    private void OpenPlayerWindow(bool pictureInPicture)
    {
        if (boundPage is not FilmPlaybackPage { CanPlay: true } page || pictureInPicture && !page.HasOutput) return;
        ClosePlayerMenus();
        playerWindow?.Close();
        var heightBinding = BindingOperations.GetBindingBase(VideoPicture, HeightProperty);
        HeroGrid.Children.Remove(PlayerFrame);
        VideoPicture.ClearValue(HeightProperty);
        PlayerFrame.VerticalAlignment = VerticalAlignment.Stretch;
        var host = new Grid { Background = Brushes.Black }; host.Children.Add(PlayerFrame);
        var dialog = new Window
        {
            Title = page.Title, Owner = Window.GetWindow(this), Content = host, DataContext = page, Resources = Resources,
            Width = 640, Height = 360, MinWidth = 380, MinHeight = 214, Background = Brushes.Black,
            WindowStyle = pictureInPicture ? WindowStyle.ToolWindow : WindowStyle.None,
            ResizeMode = pictureInPicture ? ResizeMode.CanResizeWithGrip : ResizeMode.NoResize,
            WindowState = pictureInPicture ? WindowState.Normal : WindowState.Maximized,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Topmost = pictureInPicture
        };
        playerWindow = dialog; pip = pictureInPicture;
        UpdatePlayerWindowState();
        dialog.PreviewKeyDown += Player_KeyDown;
        // VideoView's foreground HWND is owned by its current host. Unmount it before
        // the window really closes, otherwise that foreground is closed permanently.
        bool restored = false;
        dialog.Closing += (_, e) =>
        {
            if (restored) return;
            e.Cancel = true;
            restored = true;
            RestorePlayerFrame();
            _ = dialog.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(dialog.Close));
        };
        dialog.Closed += async (_, _) =>
        {
            if (!restored) RestorePlayerFrame();
            await ExecuteAsync(page, page.EndRetainedOwnerAsync, "停止画中画失败");
        };
        void RestorePlayerFrame()
        {
            ClosePlayerMenus(); CancelPlayerInput();
            host.Children.Remove(PlayerFrame);
            PlayerFrame.VerticalAlignment = VerticalAlignment.Top;
            if (heightBinding is not null) BindingOperations.SetBinding(VideoPicture, HeightProperty, heightBinding);
            HeroGrid.Children.Insert(0, PlayerFrame);
            page.RetainOwner(null);
            if (ReferenceEquals(playerWindow, dialog)) { playerWindow = null; pip = false; }
            UpdatePlayerWindowState();
            UpdateLayoutMode(); UpdateMedia(); ShowPlayerControls();
        }
        try { dialog.Show(); if (pictureInPicture) page.RetainOwner(dialog); ShowPlayerControls(); }
        catch (Exception) { dialog.Close(); Notices.Message = "播放器窗口打开失败"; }
    }
    private void Swipe_Begin(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        swipeStart = e.GetPosition(ShortVideoGesture);
        ShortVideoGesture.CaptureMouse();
    }
    private async void Swipe_End(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var start = swipeStart; swipeStart = null; ShortVideoGesture.ReleaseMouseCapture();
        if (start is { } point)
        {
            double distance = e.GetPosition(ShortVideoGesture).Y - point.Y;
            if (Math.Abs(distance) < 48) SchedulePictureClick(); else await SwipeAsync(distance);
        }
    }
    private async void Swipe_Completed(object sender, ManipulationCompletedEventArgs e)
    {
        e.Handled = true; await SwipeAsync(e.TotalManipulation.Translation.Y);
    }
    private async void ShortVideo_Wheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (DataContext is FilmPlaybackPage { ShortVideo: true }) await SwipeAsync(e.Delta > 0 ? -80 : 80);
    }
    private Task SwipeAsync(double distance)
    {
        if (DataContext is not FilmPlaybackPage { ShortVideo: true } page || Math.Abs(distance) < 48) return Task.CompletedTask;
        long now = Environment.TickCount64;
        if (now - lastSwipe < 250) return Task.CompletedTask;
        lastSwipe = now;
        return ExecuteAsync(page, () => page.CommandAsync(distance < 0 ? "next" : "previous"));
    }

    private void ListLayout_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is FilmPlaybackPage page) page.SetListLayout(true);
    }

    private void Overview_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is FilmPlaybackPage page) page.ToggleOverview();
    }

    private void Recommendation_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: FilmCardItem item })
            RecommendationRequested?.Invoke(this, new(item));
    }

    private void EpisodePrevious_Click(object sender, RoutedEventArgs e) => PageRail(HorizontalEpisodes, -1, e);
    private void EpisodeNext_Click(object sender, RoutedEventArgs e) => PageRail(HorizontalEpisodes, 1, e);
    private void RelatedPrevious_Click(object sender, RoutedEventArgs e) => PageRail(RelatedScroll, -1, e);
    private void RelatedNext_Click(object sender, RoutedEventArgs e) => PageRail(RelatedScroll, 1, e);

    private static void PageRail(ScrollViewer rail, int direction, RoutedEventArgs e)
    {
        e.Handled = true;
        rail.ScrollToHorizontalOffset(rail.HorizontalOffset + direction * rail.ViewportWidth * .85);
    }

    private void Rail_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateRailButtons();

    private void UpdateRailButtons()
    {
        if (EpisodePrevious is null || RelatedNext is null) return;
        EpisodePrevious.IsEnabled = HorizontalEpisodes.HorizontalOffset > .5;
        EpisodeNext.IsEnabled = HorizontalEpisodes.ScrollableWidth - HorizontalEpisodes.HorizontalOffset > .5;
        RelatedPrevious.IsEnabled = RelatedScroll.HorizontalOffset > .5;
        RelatedNext.IsEnabled = RelatedScroll.ScrollableWidth - RelatedScroll.HorizontalOffset > .5;
    }

    private void Rail_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer rail) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            rail.ScrollToHorizontalOffset(rail.HorizontalOffset - e.Delta * 2);
        else
            PageScroll.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = Mouse.MouseWheelEvent,
                Source = PageScroll
            });
        e.Handled = true;
    }

    private void Rail_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ScrollViewer rail || e.KeyboardDevice.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.PageUp: PageRail(rail, -1, e); break;
            case Key.PageDown: PageRail(rail, 1, e); break;
            case Key.Home: rail.ScrollToLeftEnd(); e.Handled = true; break;
            case Key.End: rail.ScrollToRightEnd(); e.Handled = true; break;
        }
    }
}
