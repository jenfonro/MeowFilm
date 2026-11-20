using Windows.System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Data;
using MeowFilm.Desktop;
using Windows.Storage.Pickers;

namespace MeowFilm.Capabilities;

public partial class FilmSettingsView
{
    private CancellationTokenSource? sourceOperation;
    private bool sourcesLoaded, sourcesBusy, sourcesFailed;
    private string sourceWork = "";
    public ObservableCollection<FilmSettingsCatRow> CatRows { get; } = [];
    public ObservableCollection<FilmSettingsVideoRow> VideoRows { get; } = [];
    public bool SourcesLoaded => sourcesLoaded;
    public bool SourcesBusy => sourcesBusy;
    public bool SourcesIdle => !sourcesBusy;
    public bool SourcesFailed => sourcesFailed;
    public bool CanEditSources => sourcesLoaded && !sourcesBusy;
    public string SourceWork => sourceWork;
    public bool HasSourceWork => SourcesBusy || SourcesFailed;
    public bool CatsEmpty => sourcesLoaded && !sourcesBusy && !sourcesFailed && CatRows.Count == 0;
    public bool VideosEmpty => sourcesLoaded && !sourcesBusy && !sourcesFailed && VideoRows.Count == 0;
    public bool CanDeleteCats => CanEditSources && CatRows.Any(row => row.Selected);
    public bool CanDeleteVideos => CanEditSources && VideoRows.Any(row => row.Selected);
    public bool CanDetectVideos => CanEditSources && VideoRows.Any(row => row.Selected) &&
        VideoRows.Where(row => row.Selected).All(row => row.CanDetect);

    private void SourceStateChanged()
    {
        foreach (string name in new[] { nameof(SourcesLoaded), nameof(SourcesBusy), nameof(SourcesIdle),
            nameof(SourcesFailed), nameof(HasSourceWork), nameof(CanEditSources), nameof(SourceWork), nameof(CatsEmpty),
            nameof(VideosEmpty), nameof(CanDeleteCats), nameof(CanDeleteVideos), nameof(CanDetectVideos) }) Changed(name);
    }

    private Task LoadSourcesAsync() => RunSourcesAsync("正在读取来源…", async token =>
    {
        await sources.InitializeAsync(token);
        sourcesLoaded = true;
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
        DispatcherQueue.TryEnqueue(() =>
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
        RenderSourceLists();
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
        if (e.PropertyName == nameof(FilmSettingsSourceRow.Selected)) SourceStateChanged();
    }

    private void ConfigureSourceLists()
    {
        CatsList.ShowToolbar = VideosList.ShowToolbar = false;
        CatsList.ShowBulkActions = VideosList.ShowBulkActions = false;
        CatsList.MaxWidth = VideosList.MaxWidth = 1400;
        CatsList.HorizontalAlignment = VideosList.HorizontalAlignment = HorizontalAlignment.Stretch;
        UpdateSourceColumns(compact: true);
        VideosList.SetColumns([
            new("站源名称", new(1, GridUnitType.Star)), new("检测状态", new(90)), new("启用", new(44)),
            new("首页显示", new(66)), new("搜索启用", new(66)), new("操作", new(142))]);
        CatsList.SelectionChanged += () =>
        {
            foreach (var row in CatRows) row.Selected = CatsList.Selected.Contains(row.Source.InstallationId);
            SourceStateChanged();
        };
        VideosList.SelectionChanged += () =>
        {
            foreach (var row in VideoRows) row.Selected = VideosList.Selected.Contains(VideoId(row));
            SourceStateChanged();
        };
        VideosList.EnableRowDrag = true;
        VideosList.OrderChanged += async order =>
        {
            if (!Active || section != "videos" || !CanEditSources) return;
            var byId = VideoRows.ToDictionary(VideoId);
            if (order.Count != byId.Count || order.Distinct().Count() != byId.Count || order.Any(id => !byId.ContainsKey(id))) return;
            await SaveVideoOrderAsync(order.Select(id => byId[id].Source.Key).ToArray());
        };
    }

    private bool compactSources;
    private void UpdateSourceColumns(bool compact)
    {
        compactSources = compact;
        CatsList.SetColumns([
            new("名称", new(1, GridUnitType.Star)), new("类型", new(46)), new("状态", new(88)),
            new("启用", new(44)), new("修改时间", new(90)), new("更新状态", new(82)), new("操作", new(compact ? 88 : 176))]);
    }
    private static string VideoId(FilmSettingsVideoRow row) => System.Text.Json.JsonSerializer.Serialize(row.Source.Key);
    private void RenderSourceLists()
    {
        var cats = CatRows.Select(row => new ManagementRow(row.Source.InstallationId, row.Name, "", row.Enabled,
            () => { }, _ => { }, new MenuFlyout(), Cells: () => CatCells(row), CellsIdentity: row)).ToArray();
        var videos = VideoRows.Select(row => new ManagementRow(VideoId(row), row.Name, "", row.Enabled,
            () => { }, _ => { }, new MenuFlyout(), Cells: () => VideoCells(row), CellsIdentity: row)).ToArray();
        CatsList.SetRows(cats, cats.Length, cats.Select(row => row.Id), "暂无猫源");
        VideosList.SetRows(videos, videos.Length, videos.Select(row => row.Id), "暂无视频源");
        foreach (var row in CatRows) row.Selected = CatsList.Selected.Contains(row.Source.InstallationId);
        foreach (var row in VideoRows) row.Selected = VideosList.Selected.Contains(VideoId(row));
    }

    private TextBlock CellText(FilmSettingsSourceRow row, string property)
    {
        var text = new TextBlock
        {
            DataContext = row, VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0, 6, 0),
            Style = (Style)MeowFilm.Desktop.DesktopVisual.Resource(this, "FilmSettings.CellText")
        };
        text.SetBinding(TextBlock.TextProperty, new Binding { Path = new PropertyPath(property) });
        return text;
    }
    private static void BindRowName(FrameworkElement element, FilmSettingsSourceRow row, string prefix) =>
        element.SetBinding(AutomationProperties.NameProperty, new Binding
        {
            Source = row, Path = new PropertyPath("Name"), Converter = new FilmActionNameConverter(), ConverterParameter = prefix
        });
    private Button RowButton(string text, FilmSettingsSourceRow row, RoutedEventHandler click, string name, string style = "MeowFilm.ManagementRowButton")
    {
        var button = new Button { Content = text, DataContext = row, Padding = new(6, 0, 6, 0) };
        button.SetValue(StyleProperty, MeowFilm.Desktop.DesktopVisual.Resource(this, style));
        BindRowName(button, row, name);
        button.Click += click;
        return button;
    }
    private CheckBox RowCheck(string property, FilmSettingsSourceRow row, RoutedEventHandler click, string name, string? tag = null)
    {
        var box = new CheckBox { DataContext = row, Tag = tag, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        box.SetValue(StyleProperty, MeowFilm.Desktop.DesktopVisual.Resource(this, tag is null or "enabled" ? "MeowFilm.ManagementSwitch" : "MeowFilm.ManagementCheck"));
        box.SetBinding(ToggleButton.IsCheckedProperty, new Binding { Path = new PropertyPath(property), Mode = BindingMode.OneWay });
        BindRowName(box, row, name);
        box.Click += click;
        return box;
    }
    private ManagementStatus RowStatus(FilmSettingsSourceRow row, string text, string tone, string name)
    {
        var status = new ManagementStatus { DataContext = row, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(4, 0, 0, 0) };
        status.SetBinding(ContentControl.ContentProperty, new Binding { Path = new PropertyPath(text) });
        status.SetBinding(ManagementStatus.ToneProperty, new Binding { Path = new PropertyPath(tone) });
        BindRowName(status, row, name);
        return status;
    }
    private IReadOnlyList<FrameworkElement> CatCells(FilmSettingsCatRow row)
    {
        var state = RowStatus(row, nameof(row.StateText), nameof(row.StateTone), "猫源状态");
        state.SetBinding(ToolTipService.ToolTipProperty, new Binding { Path = new PropertyPath(nameof(row.ErrorText)) });
        state.SetBinding(AutomationProperties.HelpTextProperty, new Binding { Path = new PropertyPath(nameof(row.ErrorText)) });
        var update = RowStatus(row, nameof(row.UpdateText), nameof(row.UpdateTone), "猫源更新状态");
        update.SetBinding(ToolTipService.ToolTipProperty, new Binding { Path = new PropertyPath(nameof(row.UpdateErrorText)) });
        update.SetBinding(AutomationProperties.HelpTextProperty, new Binding { Path = new PropertyPath(nameof(row.UpdateErrorText)) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(RowButton("编辑", row, EditCat_Click, "编辑猫源"));
        actions.Children.Add(RowButton("更新", row, UpdateCat_Click, "更新猫源", "MeowFilm.ManagementRowButton.Primary"));
        var restart = RowButton("重启", row, RestartCat_Click, "重启猫源");
        restart.SetBinding(IsEnabledProperty, new Binding { Path = new PropertyPath(nameof(row.CanRestart)) });
        actions.Children.Add(restart);
        actions.Children.Add(RowButton("删除", row, DeleteCat_Click, "删除猫源", "MeowFilm.ManagementRowButton.Danger"));
        return [CellText(row, nameof(row.Name)), CellText(row, nameof(row.TypeText)), state,
            RowCheck(nameof(row.Enabled), row, CatEnabled_Click, "启用猫源"), CellText(row, nameof(row.ModifiedText)), update, actions];
    }
    private IReadOnlyList<FrameworkElement> VideoCells(FilmSettingsVideoRow row)
    {
        var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0, 6, 0) };
        {
            var badgeText = new TextBlock { Text = "聚合", Margin = new(6, 0, 6, 0) };
            badgeText.SetValue(StyleProperty, MeowFilm.Desktop.DesktopVisual.Resource(this, "FilmSettings.AggregateText"));
            var badge = new Border { Child = badgeText, CornerRadius = new(4), BorderThickness = new(1), Margin = new(6, 0, 0, 0), Padding = new(0, 2, 0, 2) };
            badge.SetValue(StyleProperty, MeowFilm.Desktop.DesktopVisual.Resource(this, "FilmSettings.AggregateBadge"));
            badge.SetBinding(VisibilityProperty, new Binding { Source = row, Path = new PropertyPath(nameof(row.AggregateImages)), Converter = new FilmVisibilityConverter() });
            name.Children.Add(badge);
        }
        var title = CellText(row, nameof(row.Name)); title.Margin = new(0); name.Children.Add(title);
        var state = RowStatus(row, nameof(row.StateText), nameof(row.StateTone), "检测状态");
        state.SetBinding(IsTabStopProperty, new Binding { Path = new PropertyPath(nameof(row.HasDiagnostics)) });
        state.Click += VideoStatus_Click;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var detect = RowButton("检测", row, DetectVideo_Click, "检测视频源");
        detect.SetBinding(IsEnabledProperty, new Binding { Path = new PropertyPath(nameof(row.CanDetect)) });
        actions.Children.Add(detect);
        actions.Children.Add(RowButton("删除", row, DeleteVideo_Click, "删除视频源", "MeowFilm.ManagementRowButton.Danger"));
        var more = RowButton("···", row, (_, _) => { }, "视频源更多操作");
        var menu = new MenuFlyout();
        var aggregate = new ToggleMenuFlyoutItem { Text = "设置聚合图片" };
        aggregate.SetBinding(ToggleMenuFlyoutItem.IsCheckedProperty, new Binding { Source = row, Path = new PropertyPath(nameof(row.AggregateImages)), Mode = BindingMode.OneWay });
        aggregate.Click += async (_, _) => await SetAggregateImagesAsync(row);
        menu.Items.Add(aggregate); more.Flyout = menu;
        actions.Children.Add(more);
        return [name, state, RowCheck(nameof(row.Enabled), row, VideoOption_Click, "启用视频源", "enabled"),
            RowCheck(nameof(row.HomeVisible), row, VideoOption_Click, "首页显示", "home"),
            RowCheck(nameof(row.SearchEnabled), row, VideoOption_Click, "搜索启用", "search"), actions];
    }

    private bool CurrentCat(FrameworkElement element, FilmSettingsCatRow row) =>
        Active && section == "cats" && CanEditSources && MeowFilm.Desktop.DesktopVisual.IsVisible(element) && element is Control { IsEnabled: true } &&
        ReferenceEquals(element.DataContext, row) && CatRows.Contains(row);
    private bool CurrentVideo(FrameworkElement element, FilmSettingsVideoRow row) =>
        Active && section == "videos" && CanEditSources && MeowFilm.Desktop.DesktopVisual.IsVisible(element) && element is Control { IsEnabled: true } &&
        ReferenceEquals(element.DataContext, row) && VideoRows.Contains(row);

    private async void SourcesRetry_Click(object sender, RoutedEventArgs e) => await LoadSourcesAsync();
    private void SourcesCancel_Click(object sender, RoutedEventArgs e) => sourceOperation?.Cancel();
    private async Task<string[]> PickLocalScriptsAsync(string title)
    {
        if (DesktopWindow.GetWindow(this) is not { } owner) return [];
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".js");
        picker.CommitButtonText = title;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, owner.Handle);
        var file = await picker.PickSingleFileAsync();
        return file is null ? [] : [file.Path];
    }

    private void ImportLocal_Click(object sender, RoutedEventArgs e)
    {
        if (!Active || !CanEditSources || sender is not Button anchor) return;
        OpenPopup(anchor, "添加本地文件", "", "添加", () =>
        {
            string name = CatNameBox.Text, path = catFilePath!;
            return RunSourcesAsync("正在添加猫源…", async token =>
                await sources.ImportLocalAsync(name, path, cancellationToken: token), "猫源已添加");
        }, edit: true, importType: "local");
    }

    private void ImportNetwork_Click(object sender, RoutedEventArgs e)
    {
        if (!Active || !CanEditSources || sender is not Button anchor) return;
        OpenPopup(anchor, "添加网络源", "", "添加", () =>
        {
            string name = CatNameBox.Text, url = CatUrlBox.Text;
            return RunSourcesAsync("正在添加猫源…", async token =>
                await sources.ImportNetworkAsync(name, url, cancellationToken: token), "猫源已添加");
        }, edit: true, importType: "network");
    }

    private void EditCat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FilmSettingsCatRow row } anchor || !CurrentCat(anchor, row)) return;
        string id = row.Source.InstallationId;
        OpenPopup(anchor, "编辑猫源", "", "保存", () =>
        {
            string name = CatNameBox.Text, type = CatInputType, url = CatUrlBox.Text;
            string? path = catFilePath;
            return RunSourcesAsync("正在保存猫源…", async token =>
                await sources.EditCatAsync(id, name, type, type == "network" ? url : null,
                    type == "local" ? path : null, token), "猫源已保存");
        }, edit: true, cat: row.Source);
    }

    private async void CatEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: FilmSettingsCatRow row } box) return;
        bool enabled = box.IsChecked == true;
        if (CurrentCat(box, row))
            await RunSourcesAsync(enabled ? "正在启用猫源…" : "正在停用猫源…", async token =>
                await sources.EnableCatAsync(row.Source.InstallationId, enabled, token), enabled ? "猫源已启用" : "猫源已停用");
        box.IsChecked = row.Enabled;
    }

    private async void UpdateCat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FilmSettingsCatRow row } anchor || !CurrentCat(anchor, row)) return;
        string? path = null;
        if (row.Source.SourceType == "local")
        {
            path = (await PickLocalScriptsAsync("更新猫源")).FirstOrDefault();
            if (path is null) return;
        }
        await RunSourcesAsync("正在更新猫源…", async token =>
        {
            await sources.UpdateCatAsync(row.Source.InstallationId, path, token);
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
        box.IsChecked = option switch { "enabled" => row.Enabled, "home" => row.HomeVisible, _ => row.SearchEnabled };
    }

    private async Task SetAggregateImagesAsync(FilmSettingsVideoRow row)
    {
        if (!Active || section != "videos" || !CanEditSources || !VideoRows.Contains(row) || row.AggregateImages) return;
        await RunSourcesAsync("正在保存图片来源…", token => sources.SaveVideoOptionsAsync(
            row.Source.Key, row.Source.Options with { AggregateImages = true }, cancellationToken: token), "聚合图片来源已保存");
    }

    private Task SaveVideoOrderAsync(IReadOnlyList<FilmSourceKey> order) =>
        RunSourcesAsync("正在保存排序…", token => sources.ReorderVideosAsync(order, token), "排序已保存");

    private void VideoStatus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FilmSettingsVideoRow row } anchor ||
            !CurrentVideo(anchor, row) || !row.HasDiagnostics) return;
        OpenPopup(anchor, row.Name + "：检测详情", row.Diagnostics, "关闭", () => Task.CompletedTask);
        PopupCancel.Visibility = Visibility.Collapsed;
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
            foreach (var row in rows) row.SetDetecting(true);
            try
            {
                foreach (var key in keys) await sources.DetectAsync(key, token);
            }
            finally
            {
                foreach (var row in rows) row.SetDetecting(false);
            }
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
