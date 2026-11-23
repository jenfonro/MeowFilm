using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed class FilmDirectoryRow(string name, string path, bool isDirectory, string? candidateId,
    JsonElement? playbackTarget, bool isCurrent)
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public bool IsDirectory { get; } = isDirectory;
    public string? CandidateId { get; } = candidateId;
    public JsonElement? PlaybackTarget { get; } = playbackTarget;
    public bool IsCurrent { get; } = isCurrent;
    public string Label => IsDirectory ? "目录 · " + Name : IsCurrent ? "当前 · " + Name : Name;

    internal FilmDirectoryRow WithCurrent(bool current) =>
        IsCurrent == current ? this : new(Name, Path, IsDirectory, CandidateId, PlaybackTarget, current);
}

/// <summary>Only groups already returned exact file paths. Browsing never requests or transfers a file.</summary>
internal sealed class FilmDirectory
{
    private sealed class Folder(string path, Folder? parent)
    {
        internal readonly string Path = path;
        internal readonly Folder? Parent = parent;
        internal readonly Dictionary<string, Folder> Children = new(StringComparer.Ordinal);
        internal readonly List<FilmDirectoryRow> Files = [];
    }

    private readonly Folder root = new("", null);
    private Folder current;
    private string currentCandidate;

    internal FilmDirectory(JsonElement browsing, string currentCandidate)
    {
        current = root;
        this.currentCandidate = currentCandidate;
        foreach (var file in FilmJson.Array(browsing, "originalEpisodes"))
        {
            string name = FilmJson.Text(file, "name"), path = FilmJson.Text(file, "path");
            string[] segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            var folder = root;
            for (int index = 0; index < segments.Length - 1; index++)
            {
                string segment = segments[index];
                if (!folder.Children.TryGetValue(segment, out var child))
                    folder.Children.Add(segment, child = new(folder.Path + "/" + segment, folder));
                folder = child;
            }
            var target = FilmJson.Member(file, "playbackTarget");
            var exact = FilmJson.FileTarget(target);
            string candidate = FilmJson.RequiredText(exact.Source!.Value, "candidateId");
            folder.Files.Add(new(name.Length > 0 ? name : segments.LastOrDefault() ?? "", path, false,
                candidate, target.Clone(), false));
        }
    }

    internal string Path => current.Path.Length == 0 ? "/" : current.Path;
    internal bool CanBack => current.Parent is not null;
    internal IReadOnlyList<FilmDirectoryRow> Rows => current.Children.Select(pair =>
        new FilmDirectoryRow(pair.Key, pair.Value.Path, true, null, null, false)).Concat(current.Files.Select(row =>
            row.WithCurrent(row.CandidateId == currentCandidate))).ToArray();

    internal bool UpdateCurrent(string candidateId)
    {
        if (currentCandidate == candidateId) return false;
        currentCandidate = candidateId;
        return true;
    }

    internal void Back() { if (current.Parent is { } parent) current = parent; }

    internal void Enter(FilmDirectoryRow row)
    {
        if (!row.IsDirectory || !current.Children.TryGetValue(row.Name, out var folder) || folder.Path != row.Path)
            throw new FilmPlaybackException("INVALID_REQUEST");
        current = folder;
    }
}

public partial class FilmDirectoryDialogView : UserControl, INotifyPropertyChanged
{
    private readonly FilmPlaybackPage page;
    private readonly FrameworkElement owner;
    private readonly JsonElement browsing;
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
        browsing = page.RawBrowsing.Clone();
        directory = new(browsing, page.CurrentCandidateId);
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
        JsonElement.DeepEquals(FilmJson.Member(browsing, "sourceId"), FilmJson.Member(value.RawBrowsing, "sourceId")) &&
        JsonElement.DeepEquals(FilmJson.Member(browsing, "lineId"), FilmJson.Member(value.RawBrowsing, "lineId")) &&
        JsonElement.DeepEquals(FilmJson.Member(browsing, "originalEpisodes"), FilmJson.Member(value.RawBrowsing, "originalEpisodes")) &&
        value.CanBrowseRaw;

    internal void RefreshCurrent()
    {
        if (!directory.UpdateCurrent(page.CurrentCandidateId)) return;
        var selected = FileList.SelectedItem as FilmDirectoryRow;
        rows = directory.Rows;
        Changed(nameof(Rows));
        if (selected is not null)
            FileList.SelectedItem = rows.FirstOrDefault(row =>
                row.IsDirectory == selected.IsDirectory && row.CandidateId == selected.CandidateId && row.Path == selected.Path);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        directory.Back();
        Populate();
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool next = FileList.SelectedItem is FilmDirectoryRow { IsDirectory: false, PlaybackTarget: not null, CandidateId: not null };
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

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not FilmDirectoryRow { IsDirectory: false, PlaybackTarget: { } target, CandidateId: not null } || !IsCurrent(page)) return;
        try { await page.SelectOriginalTargetAsync(target); }
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
