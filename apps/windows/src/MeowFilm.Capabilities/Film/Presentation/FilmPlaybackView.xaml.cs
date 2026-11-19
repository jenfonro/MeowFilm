using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using MeowFilm.Media;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using Windows.Graphics;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public partial class FilmPlaybackView : UserControl
{
    private FilmPlaybackPage? boundPage, seekingPage;
    private FilmEpisodeDirectory? shownDirectory;
    private ItemsControl? episodeList;
    private bool restoringDirectory, restoringPage, shownList;
    private FilmDirectoryDialog? directory;
    private DesktopWindow? playerWindow;
    private bool pip, selectingControl, projectingSources;
    private int rateIndex = 2, aspectIndex;
    private FilmVideoSurface? mediaSurface;
    private Point? swipeStart;
    private long lastSwipe;
    private long rateGeneration, aspectGeneration;
    private string? seekingIdentity;
    private string? controlsIdentity;

    public event EventHandler<FilmCardActivatedEventArgs>? RecommendationRequested;

    public FilmPlaybackView()
    {
        InitializeComponent();
        PlayerOverlay.SetBinding(DataContextProperty, new Binding { Path = new PropertyPath(nameof(DataContext)), Source = this });
        DataContextChanged += (_, _) =>
        {
            RememberPagePosition();
            CancelPlayerInput();
            if (boundPage is not null) boundPage.PropertyChanged -= PageChanged;
            directory?.Hide(); directory = null;
            playerWindow?.Close();
            var nextPage = DataContext as FilmPlaybackPage;
            if (!ReferenceEquals(VideoHost.Content, nextPage?.MediaView))
            {
                DetachControls();
                VideoHost.Content = null;
            }
            boundPage = nextPage;
            if (boundPage is not null) boundPage.PropertyChanged += PageChanged;
            UpdateSourceSelections();
            seekingPage = null;
            rateGeneration++; aspectGeneration++;
            controlsIdentity = null;
            UpdateControlSelections();
            RestorePagePosition();
            RestoreDirectory();
            UpdateMedia();
            ShowPlayerControls();
        };
        Loaded += (_, _) => { UpdateSourceSelections(); UpdateLayoutMode(); UpdateMedia();  };
        Unloaded += (_, _) =>
        {
            RememberPagePosition();
            if (!pip) { CancelPlayerInput(); DetachControls(); VideoHost.Content = null; }
        };
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility != Visibility.Visible) RememberPagePosition();
            else { RestorePagePosition(); UpdateMedia(); }
        });
        PageScroll.SizeChanged += (_, _) => UpdateDirectoryHeight();
        HeroActions.SizeChanged += (_, _) => UpdateMetadataHeight();
        PageScroll.ViewChanged += (_, _) =>
        {
            if (!restoringPage && MeowFilm.Desktop.DesktopVisual.IsVisible(this) && boundPage is { } page)
                page.PageOffset = PageScroll.VerticalOffset;
        };
        MetadataScroll.ViewChanged += (_, _) =>
        {
            if (!restoringPage && MeowFilm.Desktop.DesktopVisual.IsVisible(this) && boundPage is { } page)
                page.MetadataOffset = MetadataScroll.VerticalOffset;
        };
        RelatedScroll.ViewChanged += (_, _) =>
        {
            if (!restoringPage && MeowFilm.Desktop.DesktopVisual.IsVisible(this) && boundPage is { } page)
                page.RecommendationOffset = RelatedScroll.HorizontalOffset;
        };
        SeekSlider.AddHandler(PointerPressedEvent, new PointerEventHandler(Seek_Begin), true);
        SeekSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler(Seek_End), true);
        SeekSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(Seek_CaptureLost), true);
    }

    private void RememberPagePosition()
    {
        if (restoringPage || boundPage is not { } page) return;
        page.PageOffset = PageScroll.VerticalOffset;
        page.MetadataOffset = MetadataScroll.VerticalOffset;
        page.RecommendationOffset = RelatedScroll.HorizontalOffset;
    }

    private void RestorePagePosition()
    {
        restoringPage = true;
        var page = boundPage;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(page, boundPage)) return;
            PageScroll.ChangeView(null, page?.PageOffset ?? 0, null, true);
            MetadataScroll.ChangeView(null, page?.MetadataOffset ?? 0, null, true);
            RelatedScroll.ChangeView(page?.RecommendationOffset ?? 0, null, null, true);
            restoringPage = false;
        });
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
        UpdatePictureHeight();
        UpdateMetadataHeight();
        UpdateRailButtons();
    }

    private void Player_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePictureHeight();
        UpdateMetadataHeight();
    }

    private void UpdatePictureHeight()
    {
        if (playerWindow is null) VideoPicture.Height = PlayerFrame.ActualWidth * 9 / 16;
    }

    private void UpdateMetadataHeight()
    {
        if (MetadataPanel is not null)
        {
            MetadataPanel.Height = ActualWidth < 840 ? double.NaN : PlayerFrame.ActualHeight;
            MetadataOverlay.MinHeight = ActualWidth < 840 ? 300 : PlayerFrame.ActualHeight;
            bool wrapped = ActualWidth >= 840 && HeroActions.ActualHeight > 44;
            MetadataContent.Margin = new(20, wrapped ? 12 : 22, 20, wrapped ? 12 : 22);
            HeroActions.Margin = new(0, wrapped ? 8 : 14, 0, wrapped ? 8 : 16);
        }
    }

    private async void Episode_Click(object sender, RoutedEventArgs e)
    {

        if (DataContext is FilmPlaybackPage page && sender is Button { Tag: FilmEpisodeItem episode })
            await ExecuteAsync(page, () => page.SelectEpisodeAsync(episode));
    }

    private void HorizontalLayout_Click(object sender, RoutedEventArgs e)
    {

        if (DataContext is not FilmPlaybackPage page) return;
        page.SetListLayout(false);
        HorizontalLayout.SetBinding(ToggleButton.IsCheckedProperty, new Binding
        { Path = new PropertyPath(nameof(FilmPlaybackPage.IsHorizontalLayout)), Mode = BindingMode.OneWay });
    }

    private void PageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => PageChanged(sender, e));
            return;
        }
        if (e.PropertyName is nameof(FilmPlaybackPage.PlaybackRate) or nameof(FilmPlaybackPage.PlaybackAspect)) UpdateControlSelections();
        if (e.PropertyName is nameof(FilmPlaybackPage.Sources) or nameof(FilmPlaybackPage.Lines) or
            nameof(FilmPlaybackPage.SelectedSource) or nameof(FilmPlaybackPage.SelectedLine) || string.IsNullOrEmpty(e.PropertyName))
            UpdateSourceSelections();
        if (e.PropertyName is nameof(FilmPlaybackPage.CurrentCandidateId) or nameof(FilmPlaybackPage.CanBrowseRaw) or
            nameof(FilmPlaybackPage.EpisodeDirectory) || string.IsNullOrEmpty(e.PropertyName))
        {
            if (directory is { } dialog && boundPage is { } page)
            {
                if (dialog.IsCurrent(page)) dialog.RefreshCurrent();
                else { dialog.Hide(); directory = null; }
            }
        }
        if (e.PropertyName is nameof(FilmPlaybackPage.EpisodeDirectory) or nameof(FilmPlaybackPage.Episodes) or
            nameof(FilmPlaybackPage.ShowEpisodeList) || string.IsNullOrEmpty(e.PropertyName))
            RestoreDirectory();
        if (e.PropertyName is nameof(FilmPlaybackPage.MediaView) or nameof(FilmPlaybackPage.MediaIdentity) or
            nameof(FilmPlaybackPage.OwnsNativeMedia) || string.IsNullOrEmpty(e.PropertyName)) UpdateMedia();
        else if (e.PropertyName is nameof(FilmPlaybackPage.IsPlaying) or nameof(FilmPlaybackPage.PlayerBusy) or
            nameof(FilmPlaybackPage.PlayerBuffering) or nameof(FilmPlaybackPage.Position) or nameof(FilmPlaybackPage.Duration) or
            nameof(FilmPlaybackPage.Volume) or nameof(FilmPlaybackPage.CanRetryMedia) or nameof(FilmPlaybackPage.NoOutput) or
            nameof(FilmPlaybackPage.PlayerPanLabel) or nameof(FilmPlaybackPage.PlayerQualityLabel) or
            nameof(FilmPlaybackPage.OverviewExpanded) or nameof(FilmPlaybackPage.IsFavorite) or nameof(FilmPlaybackPage.Overview) or
            nameof(FilmPlaybackPage.HasOutput) or nameof(FilmPlaybackPage.PlayerPanOptions) or nameof(FilmPlaybackPage.PlayerQualityOptions) or
            nameof(FilmPlaybackPage.ShowPlayerSwitchActions))
            UpdatePlayerState();
    }

    private void RestoreDirectory()
    {
        bool showingList = boundPage?.ShowEpisodeList == true;
        bool changedLayout = showingList && !shownList;
        shownList = showingList;
        bool changedDirectory = !ReferenceEquals(shownDirectory, boundPage?.EpisodeDirectory);
        if (!changedLayout && !changedDirectory) return;
        if (changedDirectory)
        {
            shownDirectory = boundPage?.EpisodeDirectory;
            DirectoryHost.Children.Clear();
            episodeList = null;
            if (shownDirectory is { } selected)
            {
                if (selected.Presentation is null)
                {
                    var root = (FrameworkElement)((DataTemplate)Resources["Film.DirectoryPresentation"]).LoadContent();
                    var shelf = (Shelf)root.FindName("EpisodeShelf");
                    var list = (ItemsControl)root.FindName("EpisodeList");
                    var movies = (ItemsControl)root.FindName("MovieCandidates");
                    shelf.SetBinding(Shelf.ItemsSourceProperty, new Binding { Path = new PropertyPath(nameof(FilmEpisodeDirectory.Episodes)), Source = selected });
                    shelf.SetBinding(Shelf.HorizontalOffsetProperty, new Binding { Path = new PropertyPath(nameof(FilmEpisodeDirectory.HorizontalOffset)), Source = selected, Mode = BindingMode.TwoWay });
                    list.SetBinding(ItemsControl.ItemsSourceProperty, new Binding { Path = new PropertyPath(nameof(FilmEpisodeDirectory.Episodes)), Source = selected });
                    movies.SetBinding(ItemsControl.ItemsSourceProperty, new Binding { Path = new PropertyPath(nameof(FilmEpisodeDirectory.Candidates)), Source = selected });
                    selected.Presentation = root;
                }
                var presentation = selected.Presentation;
                presentation.DataContext = boundPage;
                DirectoryHost.Children.Add(presentation);
                episodeList = (ItemsControl)presentation.FindName("EpisodeList");
            }
        }
        UpdateDirectoryHeight();
        restoringDirectory = true;
        var directory = shownDirectory;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(directory, shownDirectory)) return;
            if (episodeList is { } list && FindScroll(list) is { } scroll)
            {
                scroll.ViewChanged -= EpisodeList_ViewChanged;
                scroll.ViewChanged += EpisodeList_ViewChanged;
                scroll.ChangeView(null, directory?.VerticalOffset ?? 0, null, true);
                scroll.UpdateLayout();
            }
            restoringDirectory = false;
        });
    }

    private void UpdateDirectoryHeight()
    {
        if (episodeList is { } list) list.MaxHeight = PageScroll.ActualHeight;
    }

    private void EpisodeList_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!restoringDirectory && episodeList?.Visibility == Visibility.Visible &&
            ReferenceEquals(shownDirectory, boundPage?.EpisodeDirectory) && shownDirectory is { } selected && sender is ScrollViewer scroll)
            selected.VerticalOffset = scroll.VerticalOffset;
    }

    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindScroll(VisualTreeHelper.GetChild(parent, index)) is { } child) return child;
        return null;
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
        // Mount the stable composition surface before the first committed frame.
        if (pip ? !page.OwnsNativeMedia : !IsLoaded)
        {
            DetachControls();
            VideoHost.Content = null;
            if (pip || !page.CanPlay) playerWindow?.Close();
        }
        else if (page.MediaView is FilmVideoSurface surface)
        {
            if (!ReferenceEquals(mediaSurface, surface))
            {
                DetachControls();
                VideoPicture.Children.Remove(PlayerOverlay);
                mediaSurface = surface;
                surface.Content = PlayerOverlay;
            }
            if (!ReferenceEquals(VideoHost.Content, surface)) VideoHost.Content = surface;
        }
        else
        {
            DetachControls();
            if (!ReferenceEquals(VideoHost.Content, page.MediaView)) VideoHost.Content = page.MediaView;
        }
        UpdatePlayerState();
        if (seekingPage is null)
        {
            SeekSlider.Maximum = Math.Max(0, page.Duration);
            SeekSlider.Value = page.Position;
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
                Equals(item.Tag, boundPage?.PlaybackAspect));
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

        if (DataContext is FilmPlaybackPage page) await ExecuteAsync(page, page.PlayAsync, "播放操作失败");
    }
    private async void PauseOrResume_Click(object sender, RoutedEventArgs e)
    {

        if (DataContext is FilmPlaybackPage page) await ExecuteAsync(page, page.PauseOrResumeAsync, "播放操作失败");
    }
    private async void Previous_Click(object sender, RoutedEventArgs e) {  await ActAsync("previous"); }
    private async void Next_Click(object sender, RoutedEventArgs e) {  await ActAsync("next"); }
    private async void Switch_Click(object sender, RoutedEventArgs e)
    {

        if (boundPage is { } page && !page.PlayerActionPending("switch")) await ActAsync("switch");
    }
    private async void RetryMedia_Click(object sender, RoutedEventArgs e)
    {

        if (DataContext is FilmPlaybackPage page)
            await ExecuteAsync(page, page.RetryMediaAsync, "取址失败");
    }
    private async void Block_Click(object sender, RoutedEventArgs e)
    {

        if (DataContext is not FilmPlaybackPage { HasCurrent: true, PlayerBusy: false } page) return;
        string attemptId = page.CurrentAttemptId!;
        await ExecuteAsync(page, () => page.CommandAsync("block", new { attemptId }), "片源屏蔽保存失败");
    }
    private void Pan_Click(object sender, RoutedEventArgs e)
    {

        if (boundPage is { } page && sender is Button button)
            ShowMenu(button, page.PlayerPanOptions, "pan", "provider");
    }
    private void Quality_Click(object sender, RoutedEventArgs e)
    {

        if (boundPage is { } page && sender is Button button)
            ShowMenu(button, page.PlayerQualityOptions, "quality", "quality");
    }
    private void ShowMenu(Button button, IReadOnlyList<FilmPlayerOption> options, string action, string field)
    {
        if (boundPage is not { } page || options.Count == 0) return;
        ClosePlayerMenus();
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };
        foreach (var choice in options)
        {
            var item = new ToggleMenuFlyoutItem { Text = choice.Label, IsChecked = choice.IsSelected };
            item.Click += async (_, _) =>
            {
                menu.Hide();
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
        playerMenu = menu; AutomationProperties.SetItemStatus(button, "已展开");
        menu.ShowAt(button); ShowPlayerControls();
    }
    private async void SearchMoreSources_Click(object sender, RoutedEventArgs e)
    {

        if (DataContext is not FilmPlaybackPage { CanSearchMore: true } page) return;
        SourcePicker.IsDropDownOpen = false;
        await ExecuteAsync(page, page.SearchMoreSourcesAsync, "搜索失败，请重试");
    }
    private async void SearchSources_Click(object sender, RoutedEventArgs e)
    {

        if (DataContext is FilmPlaybackPage page)
            await ExecuteAsync(page, page.SearchSourcesAsync, "搜索失败，请重试");
    }
    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {

        if (DataContext is FilmPlaybackPage page) await ExecuteAsync(page, page.ReconnectAsync, "重连失败");
    }
    private async void Favorite_Changed(object sender, RoutedEventArgs e)
    {

        if (DataContext is not FilmPlaybackPage page || sender is not ToggleButton toggle ||
            !page.CanFavorite || toggle.IsChecked == page.IsFavorite) return;
        try { await ExecuteAsync(page, page.ToggleFavoriteAsync, "收藏保存失败"); }
        finally { toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding
            { Path = new PropertyPath(nameof(FilmPlaybackPage.IsFavorite)), Mode = BindingMode.OneWay }); }
    }
    private async void Preview_Click(object sender, RoutedEventArgs e)
    {

        if (sender is not CheckBox box) return;
        try { await ActAsync("preview", new { enabled = box.IsChecked == true }, "点映设置保存失败"); }
        finally { box.SetBinding(ToggleButton.IsCheckedProperty, new Binding
            { Path = new PropertyPath(nameof(FilmPlaybackPage.PreviewEpisodes)), Mode = BindingMode.OneWay }); }
    }
    private async void ShortVideo_Click(object sender, RoutedEventArgs e)
    {

        if (sender is not CheckBox box) return;
        try { await ActAsync("shortvideo", new { enabled = box.IsChecked == true }); }
        finally { box.SetBinding(ToggleButton.IsCheckedProperty, new Binding
            { Path = new PropertyPath(nameof(FilmPlaybackPage.ShortVideo)), Mode = BindingMode.OneWay }); }
    }
    private void UpdateSourceSelections()
    {
        projectingSources = true;
        try
        {
            if (!ReferenceEquals(SourcePicker.ItemsSource, boundPage?.Sources)) SourcePicker.ItemsSource = boundPage?.Sources;
            if (!ReferenceEquals(LinePicker.ItemsSource, boundPage?.Lines)) LinePicker.ItemsSource = boundPage?.Lines;
            SourcePicker.SelectedItem = boundPage?.SelectedSource;
            LinePicker.SelectedItem = boundPage?.SelectedLine;
        }
        finally { projectingSources = false; }
    }
    private async void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!projectingSources && DataContext is FilmPlaybackPage { Applying: false } page &&
            SourcePicker.SelectedItem is FilmSelection value && value != page.SelectedSource)
            await ExecuteAsync(page, () => page.BrowseSourceAsync(value), "来源加载失败");
    }
    private async void Line_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!projectingSources && DataContext is FilmPlaybackPage { Applying: false } page &&
            LinePicker.SelectedItem is FilmSelection value && value != page.SelectedLine)
            await ExecuteAsync(page, () => page.BrowseLineAsync(value), "线路加载失败");
    }
    private async void Season_Click(object sender, RoutedEventArgs e)
    {

        if (DataContext is FilmPlaybackPage page && sender is Button { Tag: FilmSelection value } &&
            value.Season != page.SelectedSeason?.Season)
            await ExecuteAsync(page, () => page.BrowseSeasonAsync(value), "季度加载失败");
    }
    private async void MetadataMapping_Click(object sender, RoutedEventArgs e)
    {

        if (DataContext is FilmPlaybackPage page && sender is Button { Tag: FilmSelection value })
            await ExecuteAsync(page, () => page.BrowseMetadataMappingAsync(value), "分季映射加载失败");
    }
    private async void Candidate_Click(object sender, RoutedEventArgs e)
    {

        if (DataContext is FilmPlaybackPage page && sender is Button { Tag: FilmCandidateItem value })
            await ExecuteAsync(page, () => page.SelectCandidateAsync(value.Id), "候选播放失败");
    }
    private async void Directory_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not FilmPlaybackPage { CanBrowseRaw: true } page || directory is not null) return;
        var dialog = new FilmDirectoryDialog(page) { XamlRoot = XamlRoot };
        directory = dialog;
        try { await dialog.ShowAsync(); }
        finally { if (ReferenceEquals(directory, dialog)) directory = null; }
    }
    private void Seek_Begin(object sender, PointerRoutedEventArgs e)
    {
        if (boundPage is not { CanSeek: true }) return;
        seekingPage = boundPage; seekingIdentity = boundPage.MediaIdentity;
        ShowPlayerControls();
    }
    private async void Seek_End(object sender, PointerRoutedEventArgs e) => await CommitSeekAsync();
    private async Task CommitSeekAsync()
    {
        var page = seekingPage; seekingPage = null;
        var identity = seekingIdentity; seekingIdentity = null;
        if (page is not null && ReferenceEquals(page, boundPage) && page.MediaIdentity == identity)
            await ExecuteAsync(page, () => page.SeekAsync(SeekSlider.Value), "跳转失败");
        CancelSeek(); ShowPlayerControls();
    }
    private void Seek_CaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // PointerReleased is handled before capture loss; cancellation never seeks.
        CancelSeek();
    }
    private void Seek_LostFocus(object sender, RoutedEventArgs e) => CancelSeek();
    private void CancelSeek()
    {
        seekingPage = null; seekingIdentity = null;
        if (boundPage is { } page)
        {
            SeekSlider.Value = page.Position; PlayerTime.Text = page.ProgressLabel;
        }
    }
    private void Seek_Preview(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (seekingPage is not null && boundPage is { } page)
            PlayerTime.Text = $"{FilmJson.Time(SeekSlider.Value)} / {FilmJson.Time(page.Duration)}";
    }
    private static bool IsSeekKey(VirtualKey key) => key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or
        VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End;
    private void Seek_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (IsSeekKey(e.Key)) BeginKeyboardSeek(e.KeyStatus.WasKeyDown);
    }
    internal void BeginKeyboardSeek(bool repeat)
    {
        if (boundPage is not { CanSeek: true } || repeat && seekingPage is null) return;
        seekingPage = boundPage; seekingIdentity = boundPage.MediaIdentity; ShowPlayerControls();
    }
    private async void Seek_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!IsSeekKey(e.Key)) return;
        e.Handled = true; await CommitSeekAsync();
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
            sender is not ListBox box || box.SelectedItem is not ListBoxItem item) return;
        int previous = aspectIndex, selected = box.SelectedIndex;
        long ticket = ++aspectGeneration; string? identity = page.MediaIdentity;
        try
        {
            await page.AspectAsync((string?)item.Tag);
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
    private void Fullscreen_Click(object sender, RoutedEventArgs e) {  ToggleFullscreen(); }
    private void Pip_Click(object sender, RoutedEventArgs e)
    {

        if (playerWindow is not null && pip) playerWindow.Close(); else OpenPlayerWindow(true);
    }
    private void ToggleFullscreen()
    {
        pictureClickTimer.Stop(); clickPage = null; clickIdentity = null;
        if (playerWindow is not null && !pip) playerWindow.Close(); else OpenPlayerWindow(false);
    }
    private void OpenPlayerWindow(bool pictureInPicture)
    {
        if (boundPage is not { CanPlay: true } page || pictureInPicture && !page.HasOutput) return;
        ClosePlayerMenus(); playerWindow?.Close();
        HeroGrid.Children.Remove(PlayerFrame);
        VideoPicture.ClearValue(HeightProperty); PlayerFrame.VerticalAlignment = VerticalAlignment.Stretch;
        var host = new Grid { Background = new SolidColorBrush(Colors.Black), DataContext = page };
        host.Children.Add(PlayerFrame);
        var dialog = new FilmPlayerWindow(host) { Title = page.Title, MinWidth = 380, MinHeight = 214 };
        dialog.Resize(640, 360);
        dialog.AppWindow.SetPresenter(pictureInPicture ? AppWindowPresenterKind.CompactOverlay : AppWindowPresenterKind.FullScreen);
        playerWindow = dialog; pip = pictureInPicture;
        UpdatePlayerWindowState();
        bool restored = false;
        void Restore()
        {
            if (restored) return;
            restored = true; ClosePlayerMenus(); CancelPlayerInput();
            host.Children.Remove(PlayerFrame); PlayerFrame.VerticalAlignment = VerticalAlignment.Top;
            HeroGrid.Children.Insert(0, PlayerFrame); page.RetainOwner(null);
            if (ReferenceEquals(playerWindow, dialog)) { playerWindow = null; pip = false; }
            UpdatePlayerWindowState(); UpdateLayoutMode(); UpdateMedia(); ShowPlayerControls();
        }
        dialog.Closing += (_, _) => Restore();
        dialog.Closed += async (_, _) =>
        {
            Restore(); await ExecuteAsync(page, page.EndRetainedOwnerAsync, "停止画中画失败");
        };
        try { dialog.Activate(); if (pictureInPicture) page.RetainOwner(dialog); ShowPlayerControls(); }
        catch { Restore(); dialog.Close(); Notices.Message = "播放器窗口打开失败"; }
    }
    private sealed class FilmPlayerWindow : DesktopWindow
    {
        internal FilmPlayerWindow(Grid root) { Content = root; AttachRoot(); }
    }
    private void Swipe_Begin(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(ShortVideoGesture).Properties.IsLeftButtonPressed) return;
        e.Handled = true; swipeStart = e.GetCurrentPoint(ShortVideoGesture).Position;
        ShortVideoGesture.CapturePointer(e.Pointer);
    }
    private async void Swipe_End(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        var start = swipeStart; swipeStart = null; ShortVideoGesture.ReleasePointerCapture(e.Pointer);
        if (start is { } point)
        {
            double distance = e.GetCurrentPoint(ShortVideoGesture).Position.Y - point.Y;
            if (Math.Abs(distance) < 48) SchedulePictureClick(); else await SwipeAsync(distance);
        }
    }
    private async void Swipe_Completed(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        e.Handled = true; await SwipeAsync(e.Cumulative.Translation.Y);
    }
    private async void ShortVideo_Wheel(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is FilmPlaybackPage { ShortVideo: true })
            await SwipeAsync(e.GetCurrentPoint(ShortVideoGesture).Properties.MouseWheelDelta > 0 ? -80 : 80);
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

        if (DataContext is FilmPlaybackPage page)
        {
            page.SetListLayout(true);
            ListLayout.SetBinding(ToggleButton.IsCheckedProperty, new Binding
            { Path = new PropertyPath(nameof(FilmPlaybackPage.IsListLayout)), Mode = BindingMode.OneWay });
        }
    }

    private void Overview_Click(object sender, RoutedEventArgs e)
    {

        if (DataContext is FilmPlaybackPage page) page.ToggleOverview();
    }

    private void Recommendation_Click(object sender, RoutedEventArgs e)
    {

        if (sender is Button { Tag: FilmCardItem item })
            RecommendationRequested?.Invoke(this, new(item));
    }

    private void RelatedPrevious_Click(object sender, RoutedEventArgs e) => PageRail(RelatedScroll, -1, e);
    private void RelatedNext_Click(object sender, RoutedEventArgs e) => PageRail(RelatedScroll, 1, e);

    private static void PageRail(ScrollViewer rail, int direction, RoutedEventArgs e)
    {

        rail.ChangeView(rail.HorizontalOffset + direction * rail.ViewportWidth * .85, null, null);
    }

    private void Rail_ScrollChanged(object sender, ScrollViewerViewChangedEventArgs e) => UpdateRailButtons();

    private void UpdateRailButtons()
    {
        if (RelatedNext is null) return;
        RelatedPrevious.IsEnabled = RelatedScroll.HorizontalOffset > .5;
        RelatedNext.IsEnabled = RelatedScroll.ScrollableWidth - RelatedScroll.HorizontalOffset > .5;
    }

}
