using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MeowFilm.Capabilities;

public sealed class NovelCatalogChapterItem(NovelChapter chapter)
{
    public NovelChapter Chapter { get; } = chapter;
    public int Index => Chapter.Index;
    public string DisplayTitle => Chapter.Name;
    public Visibility PaidBadgeVisibility => Chapter.IsVip && !Chapter.IsPay ? Visibility.Visible : Visibility.Collapsed;
}

public sealed partial class NovelCatalogDialogView : UserControl
{
    private readonly NovelCatalogChapterItem[] ascending;
    private readonly NovelCatalogChapterItem[] descending;
    private NovelCatalogChapterItem? selectedItem;
    private bool accepted;

    private NovelCatalogDialogView(IReadOnlyList<NovelChapter> chapters, int currentIndex)
    {
        InitializeComponent();
        ascending = chapters.Select(c => new NovelCatalogChapterItem(c)).ToArray();
        descending = ascending.Reverse().ToArray();
        ChapterList.ItemsSource = ascending;
        if (ascending.Length > 0)
        {
            int idx = Math.Clamp(currentIndex, 0, ascending.Length - 1);
            ChapterList.SelectedIndex = idx;
            selectedItem = ascending[idx];
            Loaded += (_, _) =>
            {
                if (ChapterList.SelectedItem is not null)
                    ChapterList.ScrollIntoView(ChapterList.SelectedItem);
            };
        }
        OpenButton.IsEnabled = ChapterList.SelectedItem is not null;
    }

    internal static async Task<int?> ShowAsync(
        FrameworkElement owner,
        IReadOnlyList<NovelChapter> chapters,
        int currentIndex,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var dialog = new NovelCatalogDialogView(chapters, currentIndex);
        await DesktopModalHost.ShowSurfaceAsync(
            owner,
            dialog,
            dismissOnOutside: true,
            onOpened: () => dialog.ChapterList.Focus(FocusState.Programmatic),
            cancellationToken: token);
        return dialog.accepted && !token.IsCancellationRequested ? dialog.selectedItem?.Index : null;
    }

    private void Ascending_Click(object sender, RoutedEventArgs e)
    {
        var current = ChapterList.SelectedItem as NovelCatalogChapterItem;
        ChapterList.ItemsSource = ascending;
        if (current is not null) ChapterList.SelectedItem = current;
    }

    private void Descending_Click(object sender, RoutedEventArgs e)
    {
        var current = ChapterList.SelectedItem as NovelCatalogChapterItem;
        ChapterList.ItemsSource = descending;
        if (current is not null) ChapterList.SelectedItem = current;
    }

    private void ScrollTop_Click(object sender, RoutedEventArgs e)
    {
        if (ChapterList.Items.Count > 0) ChapterList.ScrollIntoView(ChapterList.Items[0]);
    }

    private void ScrollBottom_Click(object sender, RoutedEventArgs e)
    {
        if (ChapterList.Items.Count > 0) ChapterList.ScrollIntoView(ChapterList.Items[^1]);
    }

    private void ScrollCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (ChapterList.SelectedItem is not null) ChapterList.ScrollIntoView(ChapterList.SelectedItem);
    }

    private void ChapterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        selectedItem = ChapterList.SelectedItem as NovelCatalogChapterItem;
        OpenButton.IsEnabled = selectedItem is not null;
    }

    private void ChapterList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ChapterList.SelectedItem is NovelCatalogChapterItem item)
        {
            selectedItem = item;
            accepted = true;
            DesktopModalHost.CloseFor(this, this);
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (ChapterList.SelectedItem is not NovelCatalogChapterItem item) return;
        selectedItem = item;
        accepted = true;
        DesktopModalHost.CloseFor(this, this);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DesktopModalHost.CloseFor(this, this);
}
