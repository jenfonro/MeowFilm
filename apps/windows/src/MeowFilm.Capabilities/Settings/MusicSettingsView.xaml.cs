using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MeowFilm.Capabilities;

public partial class MusicSettingsView : UserControl, MeowFilm.Desktop.IDesktopHeaderSource
{
    private readonly MusicCapability module;
    private Point dragStart;
    private object? dragItem;
    private ItemsControl? dragOwner;

    public MusicSettingsView(MusicCapability module)
    {
        this.module = module;
        InitializeComponent();
        DataContext = module;
        Loaded += async (_, _) => await module.ActivateSettingsAsync();
        Unloaded += async (_, _) => await module.DeactivateSettingsAsync();
        module.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MusicCapability.CanEdit))
            {
                module.QualityBindings();
                module.AppearanceBindings();
            }
        };
    }

    public MeowFilm.Desktop.DesktopHeaderContribution Header =>
        new("设置", SearchVisible: false, SearchEnabled: false);
    public event EventHandler? HeaderChanged { add { } remove { } }

    private async void ClearPlaybackCache_Click(object sender, RoutedEventArgs e) =>
        await module.ClearPlaybackCacheAsync();
    private async void SearchHistoryClear_Click(object sender, RoutedEventArgs e) =>
        await module.ClearSearchHistoryAsync();

    private async void SearchMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || sender is not ComboBox { Tag: string kind, SelectedValue: string value }) return;
        var current = kind == "songs" ? module.SongSearch.Mode : module.PlaylistSearch.Mode;
        if (current != value) await module.SetSearchModeAsync(kind, value);
    }

    private async void SearchPlatform_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || sender is not ComboBox { Tag: string kind, SelectedValue: string value }) return;
        var current = kind == "songs" ? module.SongSearch.Platform : module.PlaylistSearch.Platform;
        if (current != value) await module.SetSearchPlatformAsync(kind, value);
    }

    private async void SearchPlatformEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: MusicSearchPlatformRow row } box ||
            FindAncestor<ListBox>(box) is not { Tag: string kind }) return;
        await module.SetSearchPlatformEnabledAsync(kind, row, box.IsChecked == true);
    }

    private async void PlaybackQuality_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || sender is not ComboBox { SelectedItem: string value } ||
            value == module.PlaybackQualityRequest) return;
        await module.SetPlaybackQualityRequestAsync(value);
    }

    private async void QualityEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: MusicQualityRow row } box ||
            FindAncestor<ListBox>(box) is not { Tag: string kind }) return;
        await module.SetQualityEnabledAsync(row, box.IsChecked == true, kind == "spatial");
    }

    private async void Appearance_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || sender is not ComboBox { Tag: string setting, SelectedValue: string value }) return;
        string current = setting switch
        {
            "playlistView" => module.AppearancePlaylistView,
            "colorMode" => module.AppearanceColorMode,
            "colorPalette" => module.AppearanceColorPalette,
            _ => value
        };
        if (current != value) await module.SetAppearanceAsync(setting, value);
    }

    private async void MetadataEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: MusicMetadataPlatformRow row } box)
            await module.SetMetadataEnabledAsync(row, box.IsChecked == true);
    }

    private void SearchList_MouseDown(object sender, MouseButtonEventArgs e) =>
        BeginDrag(sender as ItemsControl, e, typeof(MusicSearchPlatformRow));
    private void QualityList_MouseDown(object sender, MouseButtonEventArgs e) =>
        BeginDrag(sender as ItemsControl, e, typeof(MusicQualityRow));
    private void MetadataList_MouseDown(object sender, MouseButtonEventArgs e) =>
        BeginDrag(sender as ItemsControl, e, typeof(MusicMetadataPlatformRow));

    private void SearchList_MouseMove(object sender, MouseEventArgs e) => ContinueDrag(sender as ItemsControl, e);
    private void QualityList_MouseMove(object sender, MouseEventArgs e) => ContinueDrag(sender as ItemsControl, e);
    private void MetadataList_MouseMove(object sender, MouseEventArgs e) => ContinueDrag(sender as ItemsControl, e);

    private async void SearchList_Drop(object sender, DragEventArgs e)
    {
        if (sender is ListBox { Tag: string kind } list &&
            dragItem is MusicSearchPlatformRow source &&
            DropTarget<MusicSearchPlatformRow>(list, e) is { } target)
            await module.MoveSearchPlatformAsync(kind, source, target);
        EndDrag();
    }

    private async void QualityList_Drop(object sender, DragEventArgs e)
    {
        if (sender is ListBox { Tag: string kind } list &&
            dragItem is MusicQualityRow source &&
            DropTarget<MusicQualityRow>(list, e) is { } target)
            await module.MoveQualityAsync(source, target, kind == "spatial");
        EndDrag();
    }

    private async void MetadataList_Drop(object sender, DragEventArgs e)
    {
        if (sender is ListBox list && dragItem is MusicMetadataPlatformRow source &&
            DropTarget<MusicMetadataPlatformRow>(list, e) is { } target)
            await module.MoveMetadataPlatformAsync(source, target);
        EndDrag();
    }

    private void BeginDrag(ItemsControl? owner, MouseButtonEventArgs e, Type type)
    {
        if (owner is null || e.OriginalSource is not DependencyObject source ||
            FindAncestor<CheckBox>(source) is not null) return;
        if (ItemsControl.ContainerFromElement(owner, source) is not FrameworkElement container ||
            container.DataContext is not { } item || !type.IsInstanceOfType(item)) return;
        dragStart = e.GetPosition(owner);
        dragItem = item;
        dragOwner = owner;
    }

    private void ContinueDrag(ItemsControl? owner, MouseEventArgs e)
    {
        if (owner is null || !ReferenceEquals(owner, dragOwner) || dragItem is null ||
            e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(owner);
        if (Math.Abs(point.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(owner, dragItem, DragDropEffects.Move);
        EndDrag();
    }

    private static T? DropTarget<T>(ItemsControl owner, DragEventArgs e) where T : class
    {
        if (e.OriginalSource is not DependencyObject source) return null;
        return ItemsControl.ContainerFromElement(owner, source) is FrameworkElement { DataContext: T item }
            ? item : null;
    }

    private void EndDrag()
    {
        dragItem = null;
        dragOwner = null;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = source is FrameworkContentElement content
                ? content.Parent
                : VisualTreeHelper.GetParent(source);
        }
        return null;
    }
}
