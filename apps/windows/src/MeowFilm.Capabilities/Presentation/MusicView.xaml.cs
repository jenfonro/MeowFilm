using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Capabilities;

public partial class MusicView : UserControl, MeowFilm.Desktop.IDesktopHeaderSource
{
    private readonly MusicCapability module;
    private DataGrid ActiveSongsGrid => module.IsSearchPage ? SearchSongsGrid : SongsGrid;
    internal void ResetSearchPresentation() => CatalogView.ResetSearchPlaylistProjection();
    private readonly MeowFilm.Desktop.DesktopHeaderAction refreshAction;
    private readonly MeowFilm.Desktop.DesktopHeaderAction renamePlaylistAction, deletePlaylistAction;
    private async void UpdateVisibilityActivity()
    {
        bool active = IsLoaded && IsVisible;
        if (!active)
        {
            CloseCreatePlaylistDialog(restoreFocus: false);
            songScrollIntent.Clear();
            EndSongDrag();
        }
        try
        {
            await Task.WhenAll(active ? module.ActivateDiscoveryViewAsync() : module.DeactivateDiscoveryViewAsync(),
                module.UpdateVipPageAsync(active));
        }
        catch { /* Navigation and shutdown retain final drain ownership. */ }
    }
    private (bool CollectionActions, bool RefreshAction, bool Editable) publishedHeaderState;
    private bool syncingDate;
    private bool playlistDialogOpen, playlistDialogSubmitting;
    private IInputElement? playlistDialogReturnFocus;
    private Point songDragStart;
    private SongRow? songDragItem;
    private AdornerLayer? songDragLayer;
    private PlaylistDragAdorner? songDragAdorner;
    private AdornerLayer? songInsertLayer;
    private PlaylistInsertAdorner? songInsertAdorner;
    private DataGridRow? songInsertRow;
    private bool songInsertAfter;
    private readonly UserScrollIntent songScrollIntent;
    private bool loadingMoreSongs;
    private Brush playerSample = Brushes.DimGray;
    public Brush PlayerBackground { get; private set; } = MusicPlayerArtwork.Backdrop(Brushes.DimGray);
    public Brush PlayerProgress { get; private set; } = MusicPlayerArtwork.Progress(Brushes.DimGray);
    internal MeowFilm.Desktop.DesktopNavigationGroupAction CreatePlaylistAction { get; }
    public MusicView(MusicCapability module)
    {
        this.module = module;
        songScrollIntent = new(Dispatcher);
        CreatePlaylistAction = new("创建歌单", OpenCreatePlaylistDialog,
            () => module.CanEdit && !playlistDialogOpen);
        refreshAction = new("music.refresh", "刷新", async token =>
        {
            token.ThrowIfCancellationRequested();
            await module.RefreshAsync();
            token.ThrowIfCancellationRequested();
        }, () => module.CanEdit, automationName: "刷新音乐内容");
        renamePlaylistAction = new("music.playlist.rename", "重命名",
            RenamePlaylistNameAsync, () => module.CanEdit && module.CanDeleteCollection);
        deletePlaylistAction = new("music.playlist.delete", "删除",
            token => { token.ThrowIfCancellationRequested(); return module.DeletePlaylistAsync(); },
            () => module.CanEdit && module.CanDeleteCollection);
        InitializeComponent(); DataContext = module;
        AddHandler(ScrollBar.ScrollEvent, new ScrollEventHandler((_, args) =>
        {
            if (IsVisible && Descendant<ScrollViewer>(ActiveSongsGrid) is { } scroll) songScrollIntent.ObserveScroll(scroll, args);
        }), true);
        publishedHeaderState = HeaderState();
        Loaded += (_, _) => UpdateVisibilityActivity();
        Unloaded += (_, _) => UpdateVisibilityActivity();
        IsVisibleChanged += (_, _) => UpdateVisibilityActivity();
        module.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(MusicCapability.CanReorderPlaylist) && !module.CanReorderPlaylist)
                EndSongDrag();
            if (e.PropertyName is nameof(MusicCapability.HistoryDate) or nameof(MusicCapability.CanEdit))
            {
                SyncDate();
                CreatePlaylistAction.Refresh();
                UpdateCreatePlaylistDialog();
            }
            if (playlistDialogOpen && !playlistDialogSubmitting &&
                e.PropertyName is nameof(MusicCapability.Heading) or nameof(MusicCapability.IsCollectionPage))
                CloseCreatePlaylistDialog();
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MusicCapability.IsVipChannel))
            {
                try { await module.UpdateVipPageAsync(IsLoaded && IsVisible); }
                catch { /* Navigation or shutdown owns the published state and final drain. */ }
            }
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(MusicCapability.Heading) or
                nameof(MusicCapability.CountText) or nameof(MusicCapability.CanEdit) or
                nameof(MusicCapability.IsCatalogPage) or nameof(MusicCapability.IsCollectionPage) or
                nameof(MusicCapability.CanDeleteCollection) or nameof(MusicCapability.IsLocalLibraryPage) or
                nameof(MusicCapability.IsHistory) or nameof(MusicCapability.IsArchives) or nameof(MusicCapability.IsSongList))
            {
                var state = HeaderState();
                if (state != publishedHeaderState)
                {
                    publishedHeaderState = state;
                    HeaderChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        };
    }
    private (bool CollectionActions, bool RefreshAction, bool Editable) HeaderState()
    {
        bool collectionActions = module.IsCollectionPage && module.CanDeleteCollection;
        bool refresh = module.IsLocalLibraryPage && !module.IsCollectionPage;
        return (collectionActions, refresh, (collectionActions || refresh) && module.CanEdit);
    }
    public MeowFilm.Desktop.DesktopHeaderContribution Header => new("", "",
        SearchVisible: true, SearchEnabled: true,
        Actions: module.IsCollectionPage && module.CanDeleteCollection
            ? [renamePlaylistAction, deletePlaylistAction] :
            module.IsCollectionPage ? [] :
            module.IsLocalLibraryPage ? [refreshAction] : []);
    public event EventHandler? HeaderChanged;
    private async Task RenamePlaylistNameAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var input = new TextBox { Text = module.Heading, MaxLength = 200, Margin = new(0, 0, 0, 16) };
        input.SetResourceReference(StyleProperty, "MeowFilm.TextBox");
        System.Windows.Automation.AutomationProperties.SetName(input, "歌单名称");
        var confirm = new Button { Content = "重命名", IsDefault = true };
        confirm.SetResourceReference(StyleProperty, "MeowFilm.PrimaryButton");
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new(8, 0, 0, 0) };
        cancel.SetResourceReference(StyleProperty, "MeowFilm.Button");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(confirm); actions.Children.Add(cancel);
        var content = new StackPanel { Margin = new(24) };
        content.Children.Add(input); content.Children.Add(actions);
        var dialog = new Window
        {
            Owner = Window.GetWindow(this), Title = "重命名歌单",
            Width = 400, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Content = content
        };
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/MeowFilm.Capabilities;component/Presentation/CapabilityTheme.xaml", UriKind.Relative) });
        dialog.SetResourceReference(BackgroundProperty, "MeowFilm.Brush.Surface");
        dialog.SetResourceReference(FontFamilyProperty, SystemFonts.MessageFontFamilyKey);
        TextOptions.SetTextFormattingMode(dialog, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(dialog, TextRenderingMode.Grayscale);
        confirm.IsEnabled = input.Text.Trim().Length > 0;
        input.TextChanged += (_, _) => confirm.IsEnabled = input.Text.Trim().Length > 0;
        confirm.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        using var cancellation = token.Register(() => dialog.Dispatcher.BeginInvoke(new Action(() =>
        { if (dialog.IsVisible) dialog.Close(); })));
        token.ThrowIfCancellationRequested();
        if (dialog.ShowDialog() != true) return;
        token.ThrowIfCancellationRequested();
        await module.RenamePlaylistAsync(input.Text);
    }
    private void OpenCreatePlaylistDialog()
    {
        if (playlistDialogOpen || !module.CanEdit) return;
        playlistDialogOpen = true;
        playlistDialogSubmitting = false;
        playlistDialogReturnFocus = Keyboard.FocusedElement;
        PlaylistNameInput.Text = "";
        PageContent.IsHitTestVisible = false;
        PlaylistDialogOverlay.Visibility = Visibility.Visible;
        CreatePlaylistAction.Refresh();
        UpdateCreatePlaylistDialog();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (!playlistDialogOpen) return;
            PlaylistNameInput.Focus();
            Keyboard.Focus(PlaylistNameInput);
        }));
    }
    private void CloseCreatePlaylistDialog(bool restoreFocus = true)
    {
        if (!playlistDialogOpen) return;
        playlistDialogOpen = false;
        playlistDialogSubmitting = false;
        PlaylistDialogOverlay.Visibility = Visibility.Collapsed;
        PageContent.IsHitTestVisible = true;
        PlaylistNameInput.IsEnabled = true;
        ClosePlaylistDialogButton.IsEnabled = true;
        PlaylistNameInput.Text = "";
        CreatePlaylistAction.Refresh();
        var target = playlistDialogReturnFocus;
        playlistDialogReturnFocus = null;
        if (!restoreFocus || target is null) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (target is UIElement element && element.IsVisible && element.IsEnabled)
                element.Focus();
        }));
    }
    private void UpdateCreatePlaylistDialog()
    {
        if (PlaylistNameInput is null || PlaylistNamePlaceholder is null ||
            PlaylistNameCount is null || CreatePlaylistButton is null) return;
        var hasName = PlaylistNameInput.Text.Trim().Length > 0;
        PlaylistNamePlaceholder.Visibility = PlaylistNameInput.Text.Length == 0
            ? Visibility.Visible : Visibility.Collapsed;
        PlaylistNameCount.Text = $"{PlaylistNameInput.Text.Length} / {PlaylistNameInput.MaxLength}";
        CreatePlaylistButton.IsEnabled = playlistDialogOpen && !playlistDialogSubmitting &&
            module.CanEdit && hasName;
    }
    private void PlaylistName_Changed(object sender, TextChangedEventArgs e) => UpdateCreatePlaylistDialog();
    private void ClosePlaylistDialog_Click(object sender, RoutedEventArgs e)
    {
        if (!playlistDialogSubmitting) CloseCreatePlaylistDialog();
    }
    private void PlaylistDialog_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.KeyboardDevice.Modifiers != ModifierKeys.None ||
            playlistDialogSubmitting) return;
        e.Handled = true;
        CloseCreatePlaylistDialog();
    }
    private async void CreatePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (!playlistDialogOpen || playlistDialogSubmitting ||
            PlaylistNameInput.Text.Trim().Length == 0 || !module.CanEdit) return;
        playlistDialogSubmitting = true;
        PlaylistNameInput.IsEnabled = false;
        ClosePlaylistDialogButton.IsEnabled = false;
        UpdateCreatePlaylistDialog();
        var created = await module.TryCreatePlaylistAsync(PlaylistNameInput.Text);
        if (!playlistDialogOpen) return;
        if (created)
        {
            CloseCreatePlaylistDialog();
            return;
        }
        playlistDialogSubmitting = false;
        PlaylistNameInput.IsEnabled = true;
        ClosePlaylistDialogButton.IsEnabled = true;
        UpdateCreatePlaylistDialog();
        PlaylistNameInput.Focus();
        PlaylistNameInput.SelectAll();
    }
    internal void RefreshAppearance(Brush sampled, bool animate = true)
    {
        playerSample = sampled;
        var palette = module.PlayerPalette(sampled);
        var next = (GradientBrush)MusicPlayerArtwork.Backdrop(palette);
        var prior = PlayerBackground as GradientBrush;
        // Each replacement samples the presentation colors once. WPF owns the
        // animation clocks; a retarget never queues per-frame business work.
        if (animate && IsVisible && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast &&
            prior is not null && prior.GradientStops.Count == next.GradientStops.Count)
        {
            next = next.Clone();
            for (int i = 0; i < next.GradientStops.Count; i++)
                next.GradientStops[i].BeginAnimation(GradientStop.ColorProperty,
                    new ColorAnimation(prior.GradientStops[i].Color, next.GradientStops[i].Color,
                        TimeSpan.FromMilliseconds(300)) { FillBehavior = FillBehavior.Stop });
        }
        PlayerBackground = next;
        PlayerProgress = MusicPlayerArtwork.Progress(palette);
        module.RefreshPlayerBackground();
    }
    internal void ReapplyAppearance() => RefreshAppearance(playerSample);
    private void SyncDate()
    {
        syncingDate = true;
        try { HistoryPicker.SelectedItem = module.HistoryDate; }
        finally { syncingDate = false; }
    }
    internal double CaptureScroll() => ActiveScroll()?.VerticalOffset ?? 0;
    internal void RestoreScroll(double offset)
    {
        if (IsVisible) UpdateLayout();
        ActiveScroll()?.ScrollToVerticalOffset(offset);
    }
    private ScrollViewer? ActiveScroll() => module.IsArchives ? ArchiveScroll :
        module.IsDiscovery ? Descendant<ScrollViewer>(DiscoveryView) :
        module.IsContentDetailPage || module.IsSearchSongs ? Descendant<ScrollViewer>(ActiveSongsGrid) :
        module.IsCatalogPage ? CatalogView.ActiveScroll : Descendant<ScrollViewer>(ActiveSongsGrid);
    private static T? Descendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (Descendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
    private void SongsGridScroll_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer { TemplatedParent: DataGrid grid } scroll ||
            !ReferenceEquals(grid, SongsGrid) && !ReferenceEquals(grid, SearchSongsGrid)) return;
        scroll.ApplyTemplate();
        if (scroll.Template.FindName("PART_VerticalScrollBar", scroll) is not ScrollBar bar) return;
        bar.SetResourceReference(StyleProperty, "Capability.OverlayScrollBar");
        if (scroll.Template.FindName("PART_ScrollContentPresenter", scroll) is not FrameworkElement content ||
            !ReferenceEquals(VisualTreeHelper.GetParent(bar), VisualTreeHelper.GetParent(content)) ||
            VisualTreeHelper.GetParent(bar) is not Grid) return;
        Grid.SetColumn(bar, Grid.GetColumn(content));
        Grid.SetRow(bar, Grid.GetRow(content));
        Grid.SetColumnSpan(bar, Grid.GetColumnSpan(content));
        Grid.SetRowSpan(bar, Grid.GetRowSpan(content));
        Panel.SetZIndex(bar, Panel.GetZIndex(content) + 1);
    }
    private async void PlayContentDetail_Click(object sender, RoutedEventArgs e) => await module.PlayContentDetailAsync();
    private async void HistoryDate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (syncingDate || sender is not ComboBox { SelectedItem: string day } || day == module.HistoryDate) return;
        if (module.CanEdit) await module.SelectHistoryDateAsync(day);
        SyncDate();
    }
    private async void RestoreArchive_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: string id }) await module.RestoreArchiveAsync(id); }
    private async void DeleteArchive_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: string id }) await module.DeleteArchiveAsync(id); }
    private async void Song_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (module.CanEdit && e.OriginalSource is DependencyObject source && FindRow(source) is { Item: SongRow song })
        { module.SelectedSong = song; await module.PlayContentSelectionAsync(); }
    }
    private void Song_RightClick(object sender, MouseButtonEventArgs e)
    {
        var row = FindRow(e.OriginalSource as DependencyObject);
        module.SelectedSong = row?.Item as SongRow;
        row?.Focus();
        if (ActiveSongsGrid.ContextMenu is { } menu)
        {
            menu.PlacementTarget = ActiveSongsGrid;
            menu.Placement = PlacementMode.MousePoint;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;
            menu.GetBindingExpression(FrameworkElement.DataContextProperty)?.UpdateTarget();
        }
    }
    private async void Song_KeyDown(object sender, KeyEventArgs e)
    {
        songScrollIntent.Set(e.Key is Key.Down or Key.PageDown or Key.End);
        if (songScrollIntent.Active) LoadMoreSongsNearEnd();
        if (e.Key == Key.Enter && module.HasSelection)
        { e.Handled = true; await module.PlayContentSelectionAsync(); }
        else if (e.Key == Key.Delete && module.CanRemoveSelection)
        { e.Handled = true; await module.RemoveSongAsync(); }
    }
    private void SongsGrid_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        songScrollIntent.Set(e.Delta < 0);
        if (songScrollIntent.Active) LoadMoreSongsNearEnd();
    }
    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null && source is not T)
            source = source is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(source);
        return source as T;
    }
    private static DataGridRow? FindRow(DependencyObject? source) => FindAncestor<DataGridRow>(source);
    private bool SelectActionSong(object sender)
    {
        if (sender is Button { CommandParameter: SongRow song }) module.SelectedSong = song;
        return module.HasSelection;
    }
    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (SelectActionSong(sender)) await module.PlayContentSelectionAsync();
    }
    private async void NextInQueue_Click(object sender, RoutedEventArgs e)
    {
        if (SelectActionSong(sender)) await module.AppendNextAsync();
    }
    private async void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (SelectActionSong(sender)) await module.FavoriteAsync();
    }
    private void SongMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !SelectActionSong(button) || ActiveSongsGrid.ContextMenu is not { } menu) return;
        button.Focus();
        menu.IsOpen = false;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = -2;
        menu.GetBindingExpression(FrameworkElement.DataContextProperty)?.UpdateTarget();
        menu.IsOpen = true;
        e.Handled = true;
    }
    private async void Remove_Click(object sender, RoutedEventArgs e) => await module.RemoveSongAsync();
    private async void PreviousPage_Click(object sender, RoutedEventArgs e) => await module.PageAsync(-1);
    private async void NextPage_Click(object sender, RoutedEventArgs e) => await module.PageAsync(1);
    private async void AddToPlaylist_Click(object sender, RoutedEventArgs e)
    { if (sender is MenuItem { Tag: string id }) await module.AddSelectionToPlaylistAsync(id); }
    private void SongsGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange <= 0 || !songScrollIntent.Accepts(ActiveSongsGrid)) return;
        LoadMoreSongsNearEnd();
    }
    private async void LoadMoreSongsNearEnd()
    {
        if (loadingMoreSongs || !ActiveSongsGrid.IsVisible ||
            (!module.CanLoadMoreCollection && !module.CanLoadMoreCatalogPlaylist &&
             !module.CanLoadMoreSearchSongs) ||
            Descendant<ScrollViewer>(ActiveSongsGrid) is not { } scroll ||
            scroll.ExtentHeight <= 0 || scroll.VerticalOffset + scroll.ViewportHeight < scroll.ExtentHeight - 96) return;
        loadingMoreSongs = true;
        songScrollIntent.Clear();
        try
        {
            if (module.CanLoadMoreCollection) await module.LoadMoreCollectionAsync();
            else if (module.CanLoadMoreCatalogPlaylist) await module.LoadMoreCatalogPlaylistAsync();
            else if (module.CanLoadMoreSearchSongs) await module.MoreSearchAsync();
        }
        finally
        {
            loadingMoreSongs = false;
            songScrollIntent.Clear();
        }
    }

    private async void SearchTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tab })
            await module.OpenSearchTabAsync(tab);
    }

    private void SongsGrid_MouseDown(object sender, MouseButtonEventArgs e)
    {
        EndSongDrag();
        if (!module.CanReorderPlaylist || e.OriginalSource is not DependencyObject source ||
            FindAncestor<Button>(source) is not null ||
            FindRow(source) is not { Item: SongRow song }) return;
        songDragStart = e.GetPosition(ActiveSongsGrid);
        songDragItem = song;
    }

    private void SongsGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (songDragItem is null || e.LeftButton != MouseButtonState.Pressed)
        {
            if (e.LeftButton != MouseButtonState.Pressed) EndSongDrag();
            return;
        }
        var point = e.GetPosition(ActiveSongsGrid);
        if (Math.Abs(point.X - songDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - songDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = songDragItem;
        var layer = AdornerLayer.GetAdornerLayer(ActiveSongsGrid);
        if (layer is not null)
        {
            songDragLayer = layer;
            songDragAdorner = new(ActiveSongsGrid, item);
            layer.Add(songDragAdorner);
            songDragAdorner.Move(point);
        }
        try
        {
            DragDrop.DoDragDrop(ActiveSongsGrid, new DataObject(typeof(SongRow), item), DragDropEffects.Move);
        }
        finally
        {
            EndSongDrag();
        }
    }

    private void SongsGrid_DragOver(object sender, DragEventArgs e)
    {
        if (!module.CanReorderPlaylist || !e.Data.GetDataPresent(typeof(SongRow)) ||
            e.Data.GetData(typeof(SongRow)) is not SongRow source ||
            e.OriginalSource is not DependencyObject origin ||
            FindRow(origin) is not { Item: SongRow target } row ||
            ReferenceEquals(source, target))
        {
            e.Effects = DragDropEffects.None;
            RemoveInsertAdorner();
            e.Handled = true;
            return;
        }
        bool after = e.GetPosition(row).Y >= row.ActualHeight / 2;
        ShowInsertAdorner(row, after);
        songDragAdorner?.Move(e.GetPosition(ActiveSongsGrid));
        AutoScrollSongs(e.GetPosition(ActiveSongsGrid).Y);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void SongsGrid_DragLeave(object sender, DragEventArgs e)
    {
        var point = e.GetPosition(ActiveSongsGrid);
        if (point.X < 0 || point.Y < 0 || point.X > ActiveSongsGrid.ActualWidth || point.Y > ActiveSongsGrid.ActualHeight)
            RemoveInsertAdorner();
    }

    private async void SongsGrid_Drop(object sender, DragEventArgs e)
    {
        var source = e.Data.GetData(typeof(SongRow)) as SongRow;
        var target = songInsertRow?.Item as SongRow;
        bool after = songInsertAfter;
        e.Effects = source is not null && target is not null && !ReferenceEquals(source, target)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
        EndSongDrag();
        if (source is null || target is null || ReferenceEquals(source, target)) return;
        await module.ReorderPlaylistSongAsync(source, target, after);
    }

    private void SongsGrid_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (e.EscapePressed || e.Action == DragAction.Cancel)
            EndSongDrag();
    }

    private void ShowInsertAdorner(DataGridRow row, bool after)
    {
        if (ReferenceEquals(songInsertRow, row) && songInsertAfter == after) return;
        RemoveInsertAdorner();
        var layer = AdornerLayer.GetAdornerLayer(row);
        if (layer is null) return;
        songInsertLayer = layer;
        songInsertRow = row;
        songInsertAfter = after;
        songInsertAdorner = new(row, after);
        layer.Add(songInsertAdorner);
    }

    private void RemoveInsertAdorner()
    {
        if (songInsertAdorner is not null)
            songInsertLayer?.Remove(songInsertAdorner);
        songInsertLayer = null;
        songInsertAdorner = null;
        songInsertRow = null;
    }

    private void EndSongDrag()
    {
        RemoveInsertAdorner();
        if (songDragAdorner is not null)
            songDragLayer?.Remove(songDragAdorner);
        songDragLayer = null;
        songDragAdorner = null;
        songDragItem = null;
    }

    private void AutoScrollSongs(double y)
    {
        if (Descendant<ScrollViewer>(ActiveSongsGrid) is not { } scroll) return;
        const double edge = 36;
        if (y < edge) scroll.ScrollToVerticalOffset(Math.Max(0, scroll.VerticalOffset - 12));
        else if (y > ActiveSongsGrid.ActualHeight - edge)
            scroll.ScrollToVerticalOffset(Math.Min(scroll.ScrollableHeight, scroll.VerticalOffset + 12));
    }

    private sealed class PlaylistInsertAdorner : Adorner
    {
        private readonly bool after;
        private static readonly Pen Line = CreatePen();
        internal PlaylistInsertAdorner(UIElement adornedElement, bool after) : base(adornedElement)
        {
            this.after = after;
            IsHitTestVisible = false;
        }
        protected override void OnRender(DrawingContext drawingContext)
        {
            double y = after ? AdornedElement.RenderSize.Height : 0;
            drawingContext.DrawLine(Line, new Point(8, y), new Point(Math.Max(8, AdornedElement.RenderSize.Width - 8), y));
        }
        private static Pen CreatePen()
        {
            var brush = new SolidColorBrush(Color.FromRgb(255, 59, 90));
            brush.Freeze();
            var pen = new Pen(brush, 2);
            pen.Freeze();
            return pen;
        }
    }

    private sealed class PlaylistDragAdorner : Adorner
    {
        private readonly VisualCollection visuals;
        private readonly Border card;
        private Point position;

        internal PlaylistDragAdorner(UIElement adornedElement, SongRow song) : base(adornedElement)
        {
            IsHitTestVisible = false;
            var image = new ArtworkImage
            {
                Address = song.Cover,
                Width = 36,
                Height = 36,
                Stretch = Stretch.UniformToFill,
                Margin = new Thickness(0, 0, 10, 0)
            };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = song.Title,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(45, 50, 62)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 150
            });
            text.Children.Add(new TextBlock
            {
                Text = song.Artist,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(128, 136, 153)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 150
            });
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(image);
            content.Children.Add(text);
            card = new Border
            {
                Width = 220,
                Height = 52,
                Padding = new Thickness(7),
                CornerRadius = new CornerRadius(6),
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(232, 235, 240)),
                BorderThickness = new Thickness(1),
                Opacity = .96,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 18,
                    ShadowDepth = 5,
                    Opacity = .18
                },
                Child = content
            };
            visuals = new VisualCollection(this) { card };
        }

        internal void Move(Point point)
        {
            position = new(point.X + 14, point.Y + 12);
            InvalidateArrange();
        }

        protected override int VisualChildrenCount => visuals.Count;
        protected override Visual GetVisualChild(int index) => visuals[index];
        protected override Size MeasureOverride(Size constraint)
        {
            card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return constraint;
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            card.Arrange(new Rect(position, card.DesiredSize));
            return finalSize;
        }
    }
}
