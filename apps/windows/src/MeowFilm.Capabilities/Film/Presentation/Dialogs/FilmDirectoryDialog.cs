using Windows.System;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

internal sealed record FilmDirectoryRow(string Name, string Path, bool IsDirectory, string? CandidateId,
    JsonElement? PlaybackTarget, bool IsCurrent)
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

internal sealed class FilmDirectoryDialog
{
    private readonly FilmPlaybackPage page;
    private readonly FrameworkElement owner;
    private readonly JsonElement browsing;
    private readonly FilmDirectory directory;
    private readonly ListView list = new() { DisplayMemberPath = nameof(FilmDirectoryRow.Label), MinHeight = 220, MaxHeight = 400 };
    private readonly TextBlock path = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 12) };
    private readonly Button back = new() { Content = "返回上级", MinHeight = 32 };
    private readonly Button play = new() { Content = "播放所选文件", MinHeight = 32 };
    private readonly NoticeOutlet notices = new();
    private readonly StackPanel body = new() { Spacing = 8, MinWidth = 340 };
    private CancellationTokenSource? lifetime;

    internal FilmDirectoryDialog(FrameworkElement owner, FilmPlaybackPage page)
    {
        this.owner = owner;
        this.page = page; browsing = page.RawBrowsing.Clone();
        directory = new(browsing, page.CurrentCandidateId);
        AutomationProperties.SetName(list, "原始目录与文件");
        body.Children.Add(back); body.Children.Add(path); body.Children.Add(list);
        body.Children.Add(play); body.Children.Add(notices);
        back.Click += (_, _) => { directory.Back(); Populate(); };
        list.SelectionChanged += (_, _) => play.IsEnabled = list.SelectedItem is FilmDirectoryRow
            { IsDirectory: false, PlaybackTarget: not null, CandidateId: not null };
        list.DoubleTapped += (_, _) => Enter();
        list.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter && list.SelectedItem is FilmDirectoryRow { IsDirectory: true }) { Enter(); e.Handled = true; } };
        play.Click += async (_, _) =>
        {
            if (list.SelectedItem is not FilmDirectoryRow { IsDirectory: false, PlaybackTarget: { } target, CandidateId: not null } || !IsCurrent(page)) return;
            try { await page.SelectOriginalTargetAsync(target); }
            catch (OperationCanceledException) { }
            catch (Exception error) { notices.Message = FilmPlaybackError.Message(error, "文件播放失败"); }
        };
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
                Content = body,
                CloseButtonText = "关闭",
                MinWidth = 380,
                MaxWidth = 560,
                OnOpened = () => list.Focus(FocusState.Programmatic)
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
