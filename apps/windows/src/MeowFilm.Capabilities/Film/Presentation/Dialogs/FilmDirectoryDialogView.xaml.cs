using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public partial class FilmDirectoryDialogView : UserControl, INotifyPropertyChanged
{
    private readonly FilmPlaybackPage page;
    private readonly FrameworkElement owner;
    private readonly string? sourceId;
    private readonly string? lineId;
    private readonly JsonElement rawItems;
    private readonly FilmDirectory directory;
    private CancellationTokenSource? lifetime;
    private string currentPath = "/";
    private bool canBack;
    private bool canPlay;
    private string errorMessage = "";
    private IReadOnlyList<FilmDirectoryRow> rows = [];

    public string CurrentPath => currentPath;
    public bool CanBack => canBack;
    public bool CanPlay => canPlay;
    public string ErrorMessage => errorMessage;
    public IReadOnlyList<FilmDirectoryRow> Rows => rows;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal FilmDirectoryDialogView(FrameworkElement owner, FilmPlaybackPage page)
    {
        this.owner = owner;
        this.page = page;
        sourceId = page.SourceId;
        lineId = page.LineId;
        rawItems = page.DirectoryRawItems.Clone();
        directory = new(rawItems, page.ActiveItemId);
        InitializeComponent();
        Populate();
    }

    internal async Task ShowAsync()
    {
        lifetime = new CancellationTokenSource();
        try
        {
            var request = new DesktopDialogRequest
            {
                Title = "原始目录",
                Content = this,
                CloseButtonText = "关闭",
                MinWidth = 380,
                MaxWidth = 560,
                OnOpened = () => FileList.Focus(FocusState.Programmatic)
            };
            await DesktopModalHost.ShowDialogAsync(owner, request, lifetime.Token);
        }
        finally
        {
            lifetime.Dispose();
            lifetime = null;
        }
    }

    internal void Hide() => lifetime?.Cancel();

    internal bool IsCurrent(FilmPlaybackPage value) => ReferenceEquals(page, value) &&
        value.CanBrowseRaw &&
        sourceId == value.SourceId &&
        lineId == value.LineId &&
        FilmJson.Equal(rawItems, value.DirectoryRawItems);

    internal void RefreshCurrent()
    {
        if (!directory.UpdateCurrent(page.ActiveItemId)) return;
        var selected = FileList.SelectedItem as FilmDirectoryRow;
        rows = directory.Rows;
        Changed(nameof(Rows));
        if (selected is not null)
            FileList.SelectedItem = rows.FirstOrDefault(row =>
                row.IsDirectory == selected.IsDirectory && row.ItemId == selected.ItemId && row.Path == selected.Path);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        directory.Back();
        Populate();
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool next = FileList.SelectedItem is FilmDirectoryRow { IsDirectory: false, ItemId: { Length: > 0 } };
        if (canPlay == next) return;
        canPlay = next;
        Changed(nameof(CanPlay));
    }

    private void FileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => Enter();

    private void FileList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && FileList.SelectedItem is FilmDirectoryRow { IsDirectory: true })
        {
            Enter();
            e.Handled = true;
        }
    }

    private void ClearError()
    {
        if (errorMessage.Length == 0) return;
        errorMessage = "";
        Changed(nameof(ErrorMessage));
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not FilmDirectoryRow { IsDirectory: false, ItemId: { Length: > 0 } itemId } || !IsCurrent(page)) return;
        ClearError();
        try { await page.SelectItemAsync(itemId); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            errorMessage = FilmPlaybackError.Message(error, "文件播放失败");
            Changed(nameof(ErrorMessage));
        }
    }

    private void Enter()
    {
        if (FileList.SelectedItem is not FilmDirectoryRow { IsDirectory: true } row) return;
        directory.Enter(row);
        Populate();
    }

    private void Populate()
    {
        ClearError();
        currentPath = directory.Path;
        canBack = directory.CanBack;
        canPlay = false;
        rows = directory.Rows;
        Changed(nameof(CurrentPath));
        Changed(nameof(CanBack));
        Changed(nameof(CanPlay));
        Changed(nameof(Rows));
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
