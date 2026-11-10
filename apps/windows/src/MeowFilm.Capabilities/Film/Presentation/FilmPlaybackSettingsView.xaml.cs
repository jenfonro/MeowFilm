using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;

namespace MeowFilm.Capabilities;

public sealed record FilmSettingRow(string Title, string Detail, JsonElement Value, bool CanEdit = false, FilmCardItem? Favorite = null);

public partial class FilmPlaybackSettingsView : UserControl, INotifyPropertyChanged
{
    private readonly FilmPlaybackSettings service;
    private readonly FilmLibrary? favorites;
    private readonly Func<FilmPlaybackTarget?> target;
    private FilmPreferences? preferences;
    private CancellationTokenSource? read;
    private Task? reading;
    private Task<bool>? mutation;
    private Window? editor;
    private string section = "preferences";
    private long generation;
    private bool readBusy, mutating, stopping;
    private IReadOnlyList<FilmSettingRow> rows = [];

    internal FilmPlaybackSettingsView(FilmPlaybackSettings service, FilmLibrary? favorites, Func<FilmPlaybackTarget?> target)
    {
        this.service = service; this.favorites = favorites; this.target = target;
        InitializeComponent(); DataContext = this;
    }
    public bool Idle => !stopping && !readBusy && !mutating;
    public string Work { get; private set; } = "";
    public IReadOnlyList<FilmSettingRow> Rows => rows;
    public bool Empty => Idle && Work.Length == 0 && rows.Count == 0 && section is not ("preferences" or "clean");
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed() => PropertyChanged?.Invoke(this, new(""));
    private void Notify(string value) => Notices.Message = value;
    internal async Task ShowAsync(string value)
    {
        if (stopping) return;
        Deactivate();
        section = value;
        PreferencesForm.Visibility = value == "preferences" ? Visibility.Visible : Visibility.Collapsed;
        CleanForm.Visibility = value == "clean" ? Visibility.Visible : Visibility.Collapsed;
        AddManual.Visibility = value == "manual" ? Visibility.Visible : Visibility.Collapsed;
        ClearRecords.Visibility = value is "history" or "blocks" ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(ClearRecords, value == "blocks" ? "清空片源屏蔽" : "清空观看历史");
        RecordRows.Visibility = value is "preferences" or "clean" ? Visibility.Collapsed : Visibility.Visible;
        if (IsLoaded && IsVisible) await LoadAsync();
    }
    internal void Deactivate()
    {
        generation++;
        read?.Cancel();
        editor?.Close(); editor = null;
        readBusy = false; preferences = null; rows = []; Work = ""; Changed();
    }
    internal async Task PrepareShutdownAsync()
    {
        stopping = true; Deactivate();
        // Drain admitted work, not the historical outcome of the last save. A failed
        // save is reported by its UI wrapper; it is not a failed resource retirement.
        await Task.WhenAll(reading ?? Task.CompletedTask, (Task?)mutation ?? Task.CompletedTask);
    }
    internal void Resume()
    {
        stopping = false; Changed();
        _ = LoadAsync();
    }
    private async void View_Loaded(object sender, RoutedEventArgs e) => await LoadAsync();
    private void View_Unloaded(object sender, RoutedEventArgs e) => Deactivate();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();
    private Task LoadAsync()
    {
        if (stopping || !IsLoaded || !IsVisible) return Task.CompletedTask;
        if (mutation is { IsCompleted: false } || reading is { IsCompleted: false } && read?.IsCancellationRequested == true)
            return AfterPendingAsync(generation);
        if (reading is { IsCompleted: false }) return reading;
        return reading = ReadAsync();
    }
    private async Task AfterPendingAsync(long ticket)
    {
        if (mutation is { } write) await write;
        if (reading is { } load) await load;
        if (ticket == generation) await LoadAsync();
    }
    private async Task ReadAsync()
    {
        read?.Dispose(); read = new();
        var token = read.Token;
        long ticket = generation;
        string requestedSection = section;
        readBusy = true; Work = "读取中…"; rows = []; Changed();
        try
        {
            if (requestedSection is "preferences" or "clean")
            {
                var loaded = await service.ReadPreferencesAsync(token);
                if (ticket != generation || token.IsCancellationRequested) return;
                preferences = loaded; PopulatePreferences();
            }
            else if (requestedSection == "favorites")
            {
                IReadOnlyList<FilmCardItem> items = favorites is null ? [] : await favorites.ReadAsync(token);
                if (ticket != generation || token.IsCancellationRequested) return;
                rows = items.Select(item => new FilmSettingRow(item.Title, "", default, Favorite: item)).ToArray();
            }
            else
            {
                var library = await service.ReadLibraryAsync(token);
                if (ticket != generation || token.IsCancellationRequested) return;
                string field = requestedSection == "manual" ? "manual" : requestedSection == "blocks" ? "blocks" : "history";
                if (FilmJson.Member(library, field).ValueKind != JsonValueKind.Array)
                    throw new FilmPlaybackException("INVALID_RESPONSE");
                rows = FilmJson.Array(library, field).Select(value =>
                {
                    string title = FilmJson.Text(value, "title");
                    if (title.Length == 0) title = FilmJson.Text(value, "targetId");
                    string detail = requestedSection == "history" ? $"{FilmJson.Time(FilmJson.Number(value, "position"))} / {FilmJson.Time(FilmJson.Number(value, "duration"))}" :
                        requestedSection == "manual" ? $"{(FilmJson.Bool(value, "enabled") ? "已启用" : "已停用")} · 连续失败{FilmJson.Integer(value, "failureCount") ?? 0}次" :
                        string.Join(" / ", new[] { FilmJson.Text(value, "sourceKey"), FilmJson.Text(value, "detailId"), FilmJson.Text(value, "lineId") });
                    return new FilmSettingRow(title, detail, value.Clone(), requestedSection == "manual");
                }).ToArray();
            }
            Work = "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { if (ticket == generation) { Work = "读取失败"; Notify("影视设置读取失败"); } }
        finally { if (ticket == generation) { readBusy = false; Changed(); } }
    }
    private sealed record OrderItem(string Id, string Label);
    private static string Label(string id) => id switch
    {
        "quality" => "画质", "pan" => "网盘", "keyword" => "关键词", "yidong" => "移动云盘",
        "tianyi" => "天翼云盘", "quark" => "夸克", "uc" => "UC", "baidu" => "百度网盘", _ => id
    };
    private void PopulatePreferences()
    {
        if (preferences is not { } value) return;
        RuleOrder.ItemsSource = value.RuleOrder.Select(id => new OrderItem(id, Label(id))).ToList();
        PanOrder.ItemsSource = value.PanOrder.Select(id => new OrderItem(id, Label(id))).ToList();
        Keywords.Text = string.Join(Environment.NewLine, value.Keywords);
        CleanKeywords.Text = string.Join(Environment.NewLine, value.SiteCleanKeywords);
        AutoPrepare.IsChecked = value.IosAutoPrepare;
    }
    private void Move_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        var parts = tag.Split(':');
        var list = parts[0] == "rule" ? RuleOrder : PanOrder;
        var items = list.Items.Cast<OrderItem>().ToList();
        int index = list.SelectedIndex, next = index + int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        if (index < 0 || next < 0 || next >= items.Count) return;
        (items[index], items[next]) = (items[next], items[index]);
        list.ItemsSource = items; list.SelectedIndex = next;
    }
    private static string[] Words(string value) => value.Replace("\r", "").Split('\n').Select(word => word.Trim()).Where(word => word.Length > 0).ToArray();
    private FilmPreferences Draft() => new(RuleOrder.Items.Cast<OrderItem>().Select(item => item.Id).ToArray(),
        PanOrder.Items.Cast<OrderItem>().Select(item => item.Id).ToArray(), Words(Keywords.Text), Words(CleanKeywords.Text), AutoPrepare.IsChecked == true);
    private async void SavePreferences_Click(object sender, RoutedEventArgs e)
    {
        if (preferences is null || !Idle) return;
        var draft = Draft();
        await MutateAsync(() => service.SavePreferencesAsync(draft), saved =>
        { preferences = saved; PopulatePreferences(); }, "设置已保存");
    }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (preferences is null || sender is not Button { Tag: string group }) return;
        var value = Draft(); var defaults = FilmPreferences.Defaults;
        var next = group switch
        {
            "rule" => value with { RuleOrder = defaults.RuleOrder }, "pan" => value with { PanOrder = defaults.PanOrder },
            "keywords" => value with { Keywords = defaults.Keywords }, _ => value with { SiteCleanKeywords = defaults.SiteCleanKeywords }
        };
        await MutateAsync(() => service.SavePreferencesAsync(next), saved =>
        { preferences = saved; PopulatePreferences(); }, "已恢复默认");
    }
    private Task<bool> MutateAsync<T>(Func<Task<T>> action, Action<T>? publish, string success)
    {
        if (!Idle) return Task.FromResult(false);
        mutating = true; Changed(); long ticket = generation;
        return mutation = RunAsync();

        async Task<bool> RunAsync()
        {
            try
            {
                // Durable work keeps its original payload and settles after a view retires.
                var saved = await action();
                if (ticket == generation && IsLoaded && IsVisible) { publish?.Invoke(saved); Notify(success); }
                return true;
            }
            catch (Exception)
            {
                // An admitted write belongs to the settings owner, not the selected
                // section. Its failure is still a notification, never stale row data.
                Notify("保存失败");
                return false;
            }
            finally { mutating = false; Changed(); }
        }
    }
    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (!Idle || sender is not Button { Tag: FilmSettingRow row } || !Rows.Contains(row)) return;
        long ticket = generation; string requestedSection = section;
        if (!Confirm("删除记录", row.Title)) return;
        if (ticket != generation) return;
        await MutateAsync(async () =>
        {
            if (row.Favorite is { } item) await favorites!.SetFavoriteAsync(item, false);
            else if (requestedSection == "manual") await service.RemoveManualAsync(FilmJson.RequiredText(row.Value, "id"));
            else if (requestedSection == "blocks") await service.RemoveBlockAsync(row.Value);
            else await service.RemoveHistoryAsync(FilmJson.RequiredText(row.Value, "targetId"));
            return true;
        }, null, "记录已删除");
        if (ticket == generation) await LoadAsync();
    }
    private async void ClearRecords_Click(object sender, RoutedEventArgs e)
    {
        if (!Idle || section is not ("history" or "blocks")) return;
        long ticket = generation; string requestedSection = section;
        string title = requestedSection == "history" ? "观看历史" : "片源屏蔽";
        if (!Confirm("清空" + title, $"清空全部{title}？") || ticket != generation) return;
        await MutateAsync(() => requestedSection == "history" ? service.RemoveHistoryAsync(null) : service.RemoveBlockAsync(null),
            null, title + "已清空");
        if (ticket == generation) await LoadAsync();
    }
    private bool Confirm(string title, string content) => MessageBox.Show(Window.GetWindow(this), content, title,
        MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
    private void AddManual_Click(object sender, RoutedEventArgs e) => OpenEditor(null);
    private void Edit_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: FilmSettingRow row }) OpenEditor(row.Value); }

    private void OpenEditor(JsonElement? stored)
    {
        if (!Idle || section != "manual" || editor is not null) return;
        long ticket = generation;
        var value = stored ?? default;
        var content = new StackPanel { Margin = new(20) };
        TextBox Field(string label, string text)
        {
            content.Children.Add(new TextBlock { Text = label, Margin = new(0, 10, 0, 5) });
            var box = new TextBox { Text = text, Style = (Style)FindResource("Settings.TextBox") };
            AutomationProperties.SetName(box, label); content.Children.Add(box); return box;
        }
        var id = FilmJson.Text(value, "id");
        var targetBox = Field("目标标识", FilmJson.Text(value, "targetId") is { Length: > 0 } targetId ? targetId : target()?.Id ?? "");
        var titleBox = Field("名称", FilmJson.Text(value, "title"));
        var nameBox = Field("匹配名称", FilmJson.Text(value, "name"));
        var kinds = new ComboBox { ItemsSource = new[] { new OrderItem("site", "站点"), new OrderItem("share", "分享") }, DisplayMemberPath = "Label", SelectedValuePath = "Id", Margin = new(0, 12, 0, 0) };
        AutomationProperties.SetName(kinds, "人工匹配类型");
        kinds.SelectedValue = FilmJson.Text(value, "kind") == "share" ? "share" : "site";
        content.Children.Add(kinds);
        var sourceBox = Field("来源标识", FilmJson.Text(value, "sourceKey"));
        var detailBox = Field("详情标识", FilmJson.Text(value, "detailId"));
        var detailType = new ComboBox
        {
            ItemsSource = new[] { new OrderItem("string", "文本标识"), new OrderItem("number", "数字标识") },
            DisplayMemberPath = "Label", SelectedValuePath = "Id", Margin = new(0, 6, 0, 0),
            SelectedValue = FilmJson.Member(value, "detailId").ValueKind == JsonValueKind.Number ? "number" : "string"
        };
        AutomationProperties.SetName(detailType, "详情标识类型"); content.Children.Add(detailType);
        var providerBox = new ComboBox { ItemsSource = FilmPreferences.Defaults.PanOrder.Select(provider => new OrderItem(provider, Label(provider))).ToArray(), DisplayMemberPath = "Label", SelectedValuePath = "Id", Margin = new(0, 12, 0, 0) };
        AutomationProperties.SetName(providerBox, "分享网盘");
        providerBox.SelectedValue = FilmJson.Text(value, "provider"); content.Children.Add(providerBox);
        var share = FilmJson.Member(value, "share");
        content.Children.Add(new TextBlock { Text = "分享链接", Margin = new(0, 10, 0, 5) });
        var urlBox = new PasswordBox { Password = FilmJson.Text(share, "url"), Padding = new(8) };
        AutomationProperties.SetName(urlBox, "分享链接"); content.Children.Add(urlBox);
        content.Children.Add(new TextBlock { Text = "提取码", Margin = new(0, 10, 0, 5) });
        var password = new PasswordBox { Password = FilmJson.Text(share, "password"), Padding = new(8) };
        AutomationProperties.SetName(password, "分享提取码"); content.Children.Add(password);
        var seasonBox = Field("季线索", FilmJson.Text(value, "seasonHint"));
        var enabled = new CheckBox { Content = "启用", IsChecked = stored is null || FilmJson.Bool(value, "enabled"), Margin = new(0, 16, 0, 0) };
        var autoDisable = new CheckBox { Content = "连续失败自动停用", IsChecked = FilmJson.Bool(value, "autoDisable"), Margin = new(0, 10, 0, 0) };
        content.Children.Add(enabled); content.Children.Add(autoDisable);
        var save = new Button { Content = "保存", Style = (Style)FindResource("Settings.PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0) };
        content.Children.Add(save);
        var dialog = new Window { Owner = Window.GetWindow(this), Title = stored is null ? "添加人工匹配" : "编辑人工匹配",
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Width = 460, Height = 700,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        editor = dialog;
        dialog.Closed += (_, _) => { urlBox.Clear(); password.Clear(); if (ReferenceEquals(editor, dialog)) editor = null; };
        save.Click += async (_, _) =>
        {
            if (ticket != generation || !ReferenceEquals(editor, dialog) || !Idle) return;
            if (!int.TryParse(seasonBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int season) && seasonBox.Text.Length > 0 ||
                seasonBox.Text.Length > 0 && season < 0)
            { Notify("季线索无效"); return; }
            string kind = (string)kinds.SelectedValue;
            var fields = new JsonObject
            {
                ["id"] = id.Length > 0 ? id : Guid.NewGuid().ToString(), ["targetId"] = targetBox.Text.Trim(),
                ["title"] = titleBox.Text.Trim(), ["kind"] = kind, ["enabled"] = enabled.IsChecked == true,
                ["autoDisable"] = autoDisable.IsChecked == true
            };
            if (nameBox.Text.Length > 0) fields["name"] = nameBox.Text;
            if (FilmJson.Integer(value, "failureCount") is { } failures) fields["failureCount"] = failures;
            if (seasonBox.Text.Length > 0) fields["seasonHint"] = season;
            if (kind == "site")
            {
                fields["sourceKey"] = sourceBox.Text;
                if (Equals(detailType.SelectedValue, "number"))
                {
                    if (!long.TryParse(detailBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long detailId) ||
                        detailId is < -9007199254740991 or > 9007199254740991)
                    { Notify("数字详情标识无效"); return; }
                    fields["detailId"] = detailId;
                }
                else fields["detailId"] = detailBox.Text;
            }
            else
            {
                fields["provider"] = providerBox.SelectedValue as string;
                fields["share"] = new JsonObject { ["url"] = urlBox.Password.Trim(), ["password"] = password.Password };
            }
            var payload = JsonSerializer.SerializeToElement(fields);
            save.IsEnabled = false;
            bool saved = await MutateAsync(() => service.SaveManualAsync(payload), null, "人工匹配已保存");
            if (ticket != generation || !ReferenceEquals(editor, dialog)) return;
            if (saved) { dialog.Close(); await LoadAsync(); } else save.IsEnabled = true;
        };
        dialog.Show();
    }
}
