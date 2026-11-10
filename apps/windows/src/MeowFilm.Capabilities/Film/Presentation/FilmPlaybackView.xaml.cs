using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MeowFilm.Capabilities;

public partial class FilmPlaybackView : UserControl
{
    private const double MetadataFade = 20;
    private (bool Top, bool Bottom, double Height) metadataFade;
    private FilmPlaybackPage? boundPage, seekingPage;
    private FilmDirectoryDialog? directory;
    private Window? playerWindow;
    private bool pip, selectingControl;
    private int rateIndex = 1, aspectIndex;
    private Point? swipeStart;
    private long lastSwipe;
    private long rateGeneration, aspectGeneration;
    private string? seekingIdentity;
    private string? controlsIdentity;

    public event EventHandler<FilmCardActivatedEventArgs>? RecommendationRequested;

    public FilmPlaybackView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (boundPage is not null) boundPage.PropertyChanged -= PageChanged;
            directory?.Close(); directory = null;
            playerWindow?.Close();
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
        };
        Loaded += (_, _) => { UpdateLayoutMode(); UpdateMedia(); };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                directory?.Close();
                if (!pip) playerWindow?.Close();
            }
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
            rateGeneration++; aspectGeneration++;
            UpdateControlSelections();
        }
        if (!page.OwnsNativeMedia)
        {
            VideoHost.Content = null;
            playerWindow?.Close();
        }
        else if (!ReferenceEquals(VideoHost.Content, page.MediaView)) VideoHost.Content = page.MediaView;
        if (seekingPage is null) SeekSlider.Value = page.Position;
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
            rateIndex = RateBox.Items.Cast<ComboBoxItem>().ToList().FindIndex(item =>
                item.Tag is string value && double.Parse(value, CultureInfo.InvariantCulture) == rate);
            aspectIndex = AspectBox.Items.Cast<ComboBoxItem>().ToList().FindIndex(item =>
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
    private async void Switch_Click(object sender, RoutedEventArgs e) { e.Handled = true; await ActAsync("switch"); }
    private async void Block_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not FilmPlaybackPage { HasCurrent: true } page) return;
        string attemptId = page.CurrentAttemptId!;
        if (MessageBox.Show(Window.GetWindow(this), "屏蔽此影片的当前站点详情／线路，并匹配其他片源？",
            "片源错误", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            await ExecuteAsync(page, () => page.CommandAsync("block", new { attemptId }), "片源屏蔽保存失败");
    }
    private void Pan_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ShowMenu(sender, "pan", "provider",
        [
            ("yidong", "移动云盘"), ("tianyi", "天翼云盘"), ("quark", "夸克"), ("uc", "UC"), ("baidu", "百度网盘")
        ]);
    }
    private void Quality_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ShowMenu(sender, "quality", "quality",
        [
            ("4k_hdr", "4K HDR"), ("4k_fps", "4K 60帧"), ("4k", "4K"), ("1080p", "1080P"), ("720p", "720P")
        ]);
    }
    private void ShowMenu(object sender, string action, string field, (string Id, string Label)[] options)
    {
        if (sender is not Button button || DataContext is not FilmPlaybackPage page) return;
        var menu = new ContextMenu { PlacementTarget = button };
        foreach (var option in options)
        {
            var item = new MenuItem { Header = option.Label };
            item.Click += async (_, _) => await ExecuteAsync(page,
                () => page.CommandAsync(action, new Dictionary<string, string> { [field] = option.Id }));
            menu.Items.Add(item);
        }
        // Opening menus does not execute any business request.
        button.ContextMenu = menu; menu.IsOpen = true;
    }
    private async void SearchMore_Click(object sender, RoutedEventArgs e) { e.Handled = true; await ActAsync("search.more"); }
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
        seekingPage = DataContext as FilmPlaybackPage;
        seekingIdentity = seekingPage?.MediaIdentity;
    }
    private async void Seek_End(object sender, MouseButtonEventArgs e)
    {
        var page = seekingPage; seekingPage = null;
        if (page is not null && ReferenceEquals(page, boundPage) && page.MediaIdentity == seekingIdentity)
            await ExecuteAsync(page, () => page.SeekAsync(SeekSlider.Value), "跳转失败");
    }
    private void Seek_Cancel(object sender, MouseEventArgs e)
    {
        seekingPage = null; seekingIdentity = null;
        if (boundPage is { } page) SeekSlider.Value = page.Position;
    }
    private async void Seek_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)) return;
        if (DataContext is FilmPlaybackPage page) await ExecuteAsync(page, () => page.SeekAsync(SeekSlider.Value), "跳转失败");
    }
    private async void Rate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (selectingControl || DataContext is not FilmPlaybackPage { HasOutput: true } page ||
            RateBox.SelectedItem is not ComboBoxItem { Tag: string text }) return;
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
            sender is not ComboBox box || box.SelectedItem is not ComboBoxItem { Tag: string text }) return;
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
    private void Fullscreen_Click(object sender, RoutedEventArgs e) { e.Handled = true; OpenPlayerWindow(false); }
    private void Pip_Click(object sender, RoutedEventArgs e) { e.Handled = true; OpenPlayerWindow(true); }
    private void OpenPlayerWindow(bool pictureInPicture)
    {
        if (DataContext is not FilmPlaybackPage { HasOutput: true } page) return;
        if (page.HasExternalPlayer) { Notices.Message = "请先关闭外部播放器"; return; }
        playerWindow?.Close();
        var heightBinding = BindingOperations.GetBindingBase(VideoPicture, HeightProperty);
        HeroGrid.Children.Remove(PlayerFrame);
        VideoPicture.ClearValue(HeightProperty);
        PlayerFrame.RowDefinitions[0].Height = new(1, GridUnitType.Star);
        PlayerFrame.VerticalAlignment = VerticalAlignment.Stretch;
        var host = new Grid { Margin = new(8) }; host.Children.Add(PlayerFrame);
        var dialog = new Window
        {
            Title = page.Title, Owner = Window.GetWindow(this), Content = host, DataContext = page, Resources = Resources,
            Width = 520, Height = 490, MinWidth = 380, MinHeight = 360, Background = Background,
            WindowStyle = pictureInPicture ? WindowStyle.ToolWindow : WindowStyle.None,
            ResizeMode = pictureInPicture ? ResizeMode.CanResizeWithGrip : ResizeMode.NoResize,
            WindowState = pictureInPicture ? WindowState.Normal : WindowState.Maximized,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Topmost = true
        };
        playerWindow = dialog; pip = pictureInPicture;
        dialog.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { dialog.Close(); e.Handled = true; } };
        dialog.Closed += async (_, _) =>
        {
            host.Children.Remove(PlayerFrame);
            PlayerFrame.RowDefinitions[0].Height = GridLength.Auto;
            PlayerFrame.VerticalAlignment = VerticalAlignment.Top;
            if (heightBinding is not null) BindingOperations.SetBinding(VideoPicture, HeightProperty, heightBinding);
            HeroGrid.Children.Insert(0, PlayerFrame);
            page.RetainOwner(null);
            if (ReferenceEquals(playerWindow, dialog)) { playerWindow = null; pip = false; }
            UpdateLayoutMode();
            await ExecuteAsync(page, page.EndRetainedOwnerAsync, "停止画中画失败");
        };
        try { dialog.Show(); if (pictureInPicture) page.RetainOwner(dialog); }
        catch (Exception) { dialog.Close(); Notices.Message = "播放器窗口打开失败"; }
    }
    private async void External_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not FilmPlaybackPage { HasOutput: true } page) return;
        var picker = new OpenFileDialog { Title = "选择本机 mpv 播放器", Filter = "mpv 播放器 (mpv.exe)|mpv.exe", CheckFileExists = true };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        await ExecuteAsync(page, () => page.ExternalAsync(picker.FileName), "外部播放器启动失败");
    }
    private void Swipe_Begin(object sender, MouseButtonEventArgs e)
    {
        swipeStart = e.GetPosition(ShortVideoGesture);
        ShortVideoGesture.CaptureMouse();
    }
    private async void Swipe_End(object sender, MouseButtonEventArgs e)
    {
        var start = swipeStart; swipeStart = null; ShortVideoGesture.ReleaseMouseCapture();
        if (start is { } point) await SwipeAsync(e.GetPosition(ShortVideoGesture).Y - point.Y);
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
