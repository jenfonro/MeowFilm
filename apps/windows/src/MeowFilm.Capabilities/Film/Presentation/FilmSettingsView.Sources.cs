using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MeowFilm.Platform.Files;
using Microsoft.Win32;

namespace MeowFilm.Capabilities;

public partial class FilmSettingsView
{
    private static readonly UTF8Encoding StrictScriptUtf8 = new(false, true);
    private CancellationTokenSource? sourceOperation;
    private bool sourcesLoaded, sourcesBusy, sourcesFailed, catsRefreshed;
    private string sourceWork = "";
    public ObservableCollection<FilmSettingsCatRow> CatRows { get; } = [];
    public ObservableCollection<FilmSettingsVideoRow> VideoRows { get; } = [];
    public bool SourcesLoaded => sourcesLoaded;
    public bool SourcesBusy => sourcesBusy;
    public bool SourcesIdle => !sourcesBusy;
    public bool SourcesFailed => sourcesFailed;
    public bool CanEditSources => sourcesLoaded && !sourcesBusy;
    public string SourceWork => sourceWork;
    public bool CatsEmpty => sourcesLoaded && !sourcesBusy && !sourcesFailed && CatRows.Count == 0;
    public bool VideosEmpty => sourcesLoaded && !sourcesBusy && !sourcesFailed && VideoRows.Count == 0;
    public bool CanDeleteCats => CanEditSources && CatRows.Any(row => row.Selected);
    public bool CanDeleteVideos => CanEditSources && VideoRows.Any(row => row.Selected);
    public bool CanDetectVideos => CanEditSources && VideoRows.Any(row => row.Selected) &&
        VideoRows.Where(row => row.Selected).All(row => row.CanDetect);

    private void SourceStateChanged()
    {
        foreach (string name in new[] { nameof(SourcesLoaded), nameof(SourcesBusy), nameof(SourcesIdle),
            nameof(SourcesFailed), nameof(CanEditSources), nameof(SourceWork), nameof(CatsEmpty),
            nameof(VideosEmpty), nameof(CanDeleteCats), nameof(CanDeleteVideos), nameof(CanDetectVideos) }) Changed(name);
    }

    private Task LoadSourcesAsync(bool refresh) => RunSourcesAsync("正在读取来源…", async token =>
    {
        await sources.InitializeAsync(token);
        sourcesLoaded = true;
        if (refresh)
        {
            await sources.RefreshAsync(token);
            catsRefreshed = true;
        }
    });

    private async Task RunSourcesAsync(string label, Func<CancellationToken, Task> work, string? success = null)
    {
        if (!Active || SourcesBusy) return;
        using var operation = new CancellationTokenSource();
        sourceOperation = operation;
        long ticket = activation;
        sourcesBusy = true; sourcesFailed = false; sourceWork = label;
        Notices.Message = "";
        SourceStateChanged();
        try
        {
            await work(operation.Token);
            if (!operation.IsCancellationRequested && Active && ticket == activation && success is not null) Notify(success);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            if (Active && ticket == activation) Notify("来源状态已变化，操作已取消");
        }
        catch (Exception error)
        {
            if (Active && ticket == activation)
            {
                sourcesFailed = true;
                Notify("来源操作失败：" + error.Message);
            }
        }
        finally
        {
            sourceOperation = null; sourcesBusy = false; sourceWork = "";
            if (Active && sourcesLoaded) SynchronizeSources();
            SourceStateChanged();
        }
        if (Active && ticket != activation) await ActivateCurrentAsync();
    }

    private void Sources_Changed(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (Active && sourcesLoaded && !SourcesBusy) SynchronizeSources();
        });
    }

    private void SynchronizeSources()
    {
        var cats = sources.Cats;
        var videos = sources.Videos;
        Synchronize(CatRows, cats, row => row.Source.InstallationId, value => value.InstallationId,
            value => new(value), (row, value) => row.Update(value));
        bool Detectable(FilmVideoSource video) => cats.Any(cat => cat.InstallationId == video.Key.InstallationId && cat.CanDetect);
        Synchronize(VideoRows, videos.OrderBy(value => value.Order).ToArray(), row => row.Source.Key, value => value.Key,
            value => new(value, Detectable(value)), (row, value) => row.Update(value, Detectable(value)));
        UpdateSelectionHeaders();
        SourceStateChanged();
    }

    private void Synchronize<TRow, TValue, TKey>(ObservableCollection<TRow> rows, IReadOnlyList<TValue> values,
        Func<TRow, TKey> rowKey, Func<TValue, TKey> valueKey, Func<TValue, TRow> create, Action<TRow, TValue> update)
        where TRow : FilmSettingsSourceRow where TKey : notnull
    {
        var remaining = values.Select(valueKey).ToHashSet();
        foreach (var row in rows.Where(row => !remaining.Contains(rowKey(row))).ToArray())
        {
            row.PropertyChanged -= Selection_Changed;
            rows.Remove(row);
        }
        var byKey = rows.ToDictionary(rowKey);
        for (int index = 0; index < values.Count; index++)
        {
            var value = values[index];
            if (byKey.TryGetValue(valueKey(value), out var row))
            {
                update(row, value);
                int oldIndex = rows.IndexOf(row);
                if (oldIndex != index) rows.Move(oldIndex, index);
            }
            else
            {
                row = create(value);
                row.PropertyChanged += Selection_Changed;
                rows.Insert(index, row);
            }
        }
    }

    private void Selection_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FilmSettingsSourceRow.Selected)) return;
        UpdateSelectionHeaders();
        SourceStateChanged();
    }

    private void UpdateSelectionHeaders()
    {
        SelectAllCats.IsChecked = CatRows.Count > 0 && CatRows.All(row => row.Selected);
        SelectAllVideos.IsChecked = VideoRows.Count > 0 && VideoRows.All(row => row.Selected);
    }

    private bool CurrentCat(FrameworkElement element, FilmSettingsCatRow row) =>
        Active && section == "cats" && CanEditSources && element.IsVisible && element.IsEnabled &&
        ReferenceEquals(element.DataContext, row) && CatRows.Contains(row);
    private bool CurrentVideo(FrameworkElement element, FilmSettingsVideoRow row) =>
        Active && section == "videos" && CanEditSources && element.IsVisible && element.IsEnabled &&
        ReferenceEquals(element.DataContext, row) && VideoRows.Contains(row);

    private async void RefreshSources_Click(object sender, RoutedEventArgs e) => await LoadSourcesAsync(refresh: true);
    private void SourcesCancel_Click(object sender, RoutedEventArgs e) => sourceOperation?.Cancel();
    private void SelectAllCats_Click(object sender, RoutedEventArgs e)
    {
        bool selected = SelectAllCats.IsChecked == true;
        foreach (var row in CatRows) row.Selected = selected;
    }
    private void SelectAllVideos_Click(object sender, RoutedEventArgs e)
    {
        bool selected = SelectAllVideos.IsChecked == true;
        foreach (var row in VideoRows) row.Selected = selected;
    }

    private static string[] PickLocalScripts(Window? owner, bool multiple, string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title, CheckFileExists = true, Multiselect = multiple,
            Filter = "JavaScript 文件 (*.js)|*.js"
        };
        bool? accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return accepted == true ? dialog.FileNames : [];
    }

    internal static async Task<string> ReadLocalScriptAsync(string path, CancellationToken cancellationToken = default)
    {
        var snapshot = await SelectedScriptSnapshot.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return StrictScriptUtf8.GetString(snapshot.Bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(snapshot.Bytes);
        }
    }

    private void ImportLocal_Click(object sender, RoutedEventArgs e)
    {
        if (!Active || !CanEditSources || sender is not Button anchor) return;
        string? path = PickLocalScripts(Window.GetWindow(this), false, "导入猫源").FirstOrDefault();
        if (path is null) return;
        OpenPopup(anchor, "导入本地猫源", "", "导入", () =>
        {
            string name = CatNameBox.Text;
            string? id = (ImportTarget.SelectedItem as ImportChoice)?.InstallationId;
            return RunSourcesAsync("正在导入猫源…", async token =>
                await sources.ImportLocalAsync(name, await ReadLocalScriptAsync(path, token), id, token), "猫源已导入");
        }, edit: true, importType: "local");
        CatNameBox.Text = Path.GetFileNameWithoutExtension(path);
    }

    private void ImportNetwork_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditSources || sender is not Button anchor) return;
        OpenPopup(anchor, "网络导入猫源", "", "导入", () =>
        {
            string name = CatNameBox.Text, url = CatUrlBox.Text;
            string? id = (ImportTarget.SelectedItem as ImportChoice)?.InstallationId;
            return RunSourcesAsync("正在导入猫源…", async token =>
                await sources.ImportNetworkAsync(name, url, id, token), "猫源已导入");
        }, edit: true, importType: "network");
    }

    private void EditCat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FilmSettingsCatRow row } anchor || !CurrentCat(anchor, row)) return;
        string id = row.Source.InstallationId;
        bool network = row.Source.SourceType == "network";
        OpenPopup(anchor, "编辑猫源", "", "保存", () =>
        {
            string name = CatNameBox.Text, url = CatUrlBox.Text;
            return RunSourcesAsync("正在保存猫源…", async token =>
                await sources.EditCatAsync(id, name, network ? url : null, token), "猫源已保存");
        }, edit: true, cat: row.Source);
    }

    private async void CatEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: FilmSettingsCatRow row } box) return;
        bool enabled = box.IsChecked == true;
        if (CurrentCat(box, row))
            await RunSourcesAsync(enabled ? "正在启用猫源…" : "正在停用猫源…", async token =>
                await sources.EnableCatAsync(row.Source.InstallationId, enabled, token), enabled ? "猫源已启用" : "猫源已停用");
        box.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
    }

    private async void UpdateCat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FilmSettingsCatRow row } anchor || !CurrentCat(anchor, row)) return;
        string? path = null;
        if (row.Source.SourceType == "local")
        {
            path = PickLocalScripts(Window.GetWindow(this), false, "更新猫源").FirstOrDefault();
            if (path is null) return;
        }
        await RunSourcesAsync("正在更新猫源…", async token =>
        {
            string? source = path is null ? null : await ReadLocalScriptAsync(path, token);
            await sources.UpdateCatAsync(row.Source.InstallationId, source, token);
        }, "猫源更新完成");
    }

    private async void RestartCat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FilmSettingsCatRow row } anchor || !CurrentCat(anchor, row) || !row.CanRestart) return;
        await RunSourcesAsync("正在重启猫源…", async token =>
            await sources.RestartCatAsync(row.Source.InstallationId, token), "猫源已重启");
    }

    private void DeleteCat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FilmSettingsCatRow row } anchor && CurrentCat(anchor, row))
            ConfirmDeleteCats(anchor, [row]);
    }
    private void DeleteSelectedCats_Click(object sender, RoutedEventArgs e)
    {
        if (CanDeleteCats && sender is Button anchor) ConfirmDeleteCats(anchor, CatRows.Where(row => row.Selected).ToArray());
    }
    private void ConfirmDeleteCats(FrameworkElement anchor, FilmSettingsCatRow[] rows)
    {
        var ids = rows.Select(row => row.Source.InstallationId).ToArray();
        string names = string.Join("、", rows.Select(row => row.Name));
        OpenPopup(anchor, "删除猫源", $"删除“{names}”及其对应的视频源？", "删除", () =>
            RunSourcesAsync("正在删除猫源…", async token =>
            {
                foreach (string id in ids) await sources.DeleteCatAsync(id, token);
            }, "猫源已删除"));
    }

    private async void VideoOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: FilmSettingsVideoRow row, Tag: string option } box) return;
        bool enabled = box.IsChecked == true;
        if (CurrentVideo(box, row))
        {
            var options = option switch
            {
                "enabled" => row.Source.Options with { Enabled = enabled },
                "home" => row.Source.Options with { HomeVisible = enabled },
                "search" => row.Source.Options with { SearchEnabled = enabled },
                _ => throw new InvalidOperationException("未知视频源选项")
            };
            await RunSourcesAsync("正在保存视频源…", token =>
                sources.SaveVideoOptionsAsync(row.Source.Key, options, cancellationToken: token), "视频源设置已保存");
        }
        box.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
    }

    private async void AggregateImages_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { DataContext: FilmSettingsVideoRow row } radio) return;
        if (CurrentVideo(radio, row) && !row.AggregateImages)
            await RunSourcesAsync("正在保存图片来源…", token => sources.SaveVideoOptionsAsync(
                row.Source.Key, row.Source.Options with { AggregateImages = true }, cancellationToken: token), "聚合图片来源已保存");
        radio.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
    }

    private async void VideoOrder_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: FilmSettingsVideoRow row } box && CurrentVideo(box, row))
            await SaveVideoOrderAsync(row);
    }
    private async void VideoOrder_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: FilmSettingsVideoRow row } box || !CurrentVideo(box, row)) return;
        if (e.Key == Key.Escape) { row.ResetOrder(); e.Handled = true; }
        else if (e.Key == Key.Enter) { e.Handled = true; await SaveVideoOrderAsync(row); }
    }
    private async Task SaveVideoOrderAsync(FilmSettingsVideoRow row)
    {
        if (!int.TryParse(row.OrderText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int order))
        {
            Notify("排序必须是整数"); row.ResetOrder(); return;
        }
        if (order == row.Source.Order) return;
        await RunSourcesAsync("正在保存排序…", token =>
            sources.SaveVideoOptionsAsync(row.Source.Key, row.Source.Options, order, token), "排序已保存");
        row.ResetOrder();
    }

    private async void DetectVideo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FilmSettingsVideoRow row } anchor && CurrentVideo(anchor, row) && row.CanDetect)
            await DetectVideosAsync([row]);
    }
    private async void DetectSelectedVideos_Click(object sender, RoutedEventArgs e)
    {
        if (CanDetectVideos) await DetectVideosAsync(VideoRows.Where(row => row.Selected).ToArray());
    }
    private Task DetectVideosAsync(FilmSettingsVideoRow[] rows)
    {
        var keys = rows.Select(row => row.Source.Key).ToArray();
        return RunSourcesAsync("正在检测视频源…", async token =>
        {
            foreach (var key in keys) await sources.DetectAsync(key, token);
        }, "视频源检测完成");
    }
    private void DeleteVideo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FilmSettingsVideoRow row } anchor && CurrentVideo(anchor, row))
            ConfirmDeleteVideos(anchor, [row]);
    }
    private void DeleteSelectedVideos_Click(object sender, RoutedEventArgs e)
    {
        if (CanDeleteVideos && sender is Button anchor) ConfirmDeleteVideos(anchor, VideoRows.Where(row => row.Selected).ToArray());
    }
    private void ConfirmDeleteVideos(FrameworkElement anchor, FilmSettingsVideoRow[] rows)
    {
        var keys = rows.Select(row => row.Source.Key).ToArray();
        OpenPopup(anchor, "删除视频源", $"删除“{string.Join("、", rows.Select(row => row.Name))}”？", "删除", () =>
            RunSourcesAsync("正在删除视频源…", async token =>
            {
                foreach (var key in keys) await sources.DeleteVideoAsync(key, token);
            }, "视频源已删除"));
    }
}
