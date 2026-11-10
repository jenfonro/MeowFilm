using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

internal sealed record FilmDirectoryRow(string Name, string Path, bool IsDirectory, string? CandidateId, bool IsCurrent)
{
    public string Label => IsDirectory ? "目录 · " + Name : IsCurrent ? "当前 · " + Name : Name;
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
        current = root; this.currentCandidate = currentCandidate;
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
            string candidate = FilmJson.Text(file, "candidateId");
            folder.Files.Add(new(name.Length > 0 ? name : segments.LastOrDefault() ?? "", path, false,
                candidate.Length > 0 ? candidate : null, false));
        }
    }
    internal string Path => current.Path.Length == 0 ? "/" : current.Path;
    internal bool CanBack => current.Parent is not null;
    internal IReadOnlyList<FilmDirectoryRow> Rows => current.Children.Select(pair =>
        new FilmDirectoryRow(pair.Key, pair.Value.Path, true, null, false)).Concat(current.Files.Select(row =>
            row with { IsCurrent = row.CandidateId == currentCandidate })).ToArray();
    internal bool UpdateCurrent(string candidateId)
    {
        if (currentCandidate == candidateId) return false;
        currentCandidate = candidateId; return true;
    }
    internal void Back() { if (current.Parent is { } parent) current = parent; }
    internal void Enter(FilmDirectoryRow row)
    {
        if (!row.IsDirectory || !current.Children.TryGetValue(row.Name, out var folder) || folder.Path != row.Path)
            throw new FilmPlaybackException("INVALID_REQUEST");
        current = folder;
    }
}

internal sealed class FilmDirectoryDialog : Window
{
    private readonly FilmPlaybackPage page;
    private readonly JsonElement browsing;
    private readonly FilmDirectory directory;
    private readonly ListBox list = new() { DisplayMemberPath = nameof(FilmDirectoryRow.Label) };
    private readonly TextBlock path = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 12) };
    private readonly Button back = new() { Content = "返回上级", MinHeight = 32 };
    private readonly Button play = new() { Content = "播放所选文件", MinHeight = 32 };
    private readonly NoticeOutlet notices = new();

    internal FilmDirectoryDialog(FilmPlaybackPage page)
    {
        this.page = page; browsing = page.RawBrowsing.Clone();
        directory = new(browsing, page.CurrentCandidateId);
        Title = "原始目录"; Width = 580; Height = 520; MinWidth = 360; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        AutomationProperties.SetName(list, "原始目录与文件");
        var body = new DockPanel { Margin = new(20) };
        var header = new StackPanel(); header.Children.Add(back); header.Children.Add(path);
        DockPanel.SetDock(header, Dock.Top); body.Children.Add(header);
        DockPanel.SetDock(play, Dock.Bottom); body.Children.Add(play);
        DockPanel.SetDock(notices, Dock.Bottom); body.Children.Add(notices);
        body.Children.Add(list); Content = body;
        back.Click += (_, _) => { directory.Back(); Populate(); };
        list.SelectionChanged += (_, _) => play.IsEnabled = list.SelectedItem is FilmDirectoryRow { IsDirectory: false, CandidateId: not null };
        list.MouseDoubleClick += (_, _) => Enter();
        list.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && list.SelectedItem is FilmDirectoryRow { IsDirectory: true }) { Enter(); e.Handled = true; } };
        play.Click += async (_, _) =>
        {
            if (list.SelectedItem is not FilmDirectoryRow { IsDirectory: false, CandidateId: { } id } || !IsCurrent(page)) return;
            try { await page.SelectOriginalCandidateAsync(id); }
            catch (OperationCanceledException) { }
            catch (Exception) { notices.Message = "文件播放失败"; }
        };
        Populate();
    }
    internal bool IsCurrent(FilmPlaybackPage value) => ReferenceEquals(page, value) &&
        JsonElement.DeepEquals(FilmJson.Member(browsing, "sourceId"), FilmJson.Member(value.RawBrowsing, "sourceId")) &&
        JsonElement.DeepEquals(FilmJson.Member(browsing, "lineId"), FilmJson.Member(value.RawBrowsing, "lineId")) &&
        !value.ListLoading && value.ListError.Length == 0;
    internal void RefreshCurrent()
    {
        if (!directory.UpdateCurrent(page.CurrentCandidateId)) return;
        var selected = list.SelectedItem as FilmDirectoryRow;
        var rows = directory.Rows;
        list.ItemsSource = rows;
        if (selected is not null) list.SelectedItem = rows.FirstOrDefault(row =>
            row.IsDirectory == selected.IsDirectory && row.CandidateId == selected.CandidateId && row.Path == selected.Path);
    }
    private void Enter()
    {
        if (list.SelectedItem is not FilmDirectoryRow { IsDirectory: true } row) return;
        directory.Enter(row); Populate();
    }
    private void Populate()
    {
        path.Text = directory.Path; back.IsEnabled = directory.CanBack; play.IsEnabled = false;
        list.ItemsSource = directory.Rows;
    }
}
