using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed record FilmSettingRow(string Title, JsonElement Value, bool Manual)
{
    public string Source => FilmJson.Text(Value, "sourceKey");
    public string DetailId => FilmJson.Text(Value, "detailId");
    public string Pan => FilmJson.Text(Value, Manual ? "provider" : "lineId");
    public string Season => FilmJson.Text(Value, "seasonHint");
    public string AutoDisable => FilmJson.Bool(Value, "autoDisable") ? "开启" : "关闭";
    public string State => $"{(FilmJson.Bool(Value, "enabled") ? "已启用" : "已停用")} · 连续失败{FilmJson.Integer(Value, "failureCount") ?? 0}次";
}
public sealed record FilmSettingGroup(string Id, string Title, IReadOnlyList<FilmSettingRow> Rows);

public partial class FilmPlaybackSettingsView : UserControl, INotifyPropertyChanged
{
    private readonly FilmPlaybackSettings service;
    private readonly Func<FilmPlaybackTarget?> target;
    private FilmPreferences? preferences;
    private CancellationTokenSource? read;
    private Task? reading;
    private Task<bool>? mutation;
    private Window? editor;
    private string section = "preferences";
    private long generation;
    private bool readBusy, mutating, stopping;
    private readonly ObservableCollection<OrderItem> ruleItems = [], panItems = [];
    private IReadOnlyList<FilmSettingRow> rows = [];

    internal FilmPlaybackSettingsView(FilmPlaybackSettings service, Func<FilmPlaybackTarget?> target, string section = "preferences")
    {
        this.service = service; this.target = target; this.section = section;
        InitializeComponent(); DataContext = this;
        RuleOrder.ItemsSource = ruleItems; PanOrder.ItemsSource = panItems;
        PresentSection();
    }
    public bool Idle => !stopping && !readBusy && !mutating;
    public bool ReadFailed { get; private set; }
    public string Work { get; private set; } = "";
    public IReadOnlyList<FilmSettingRow> Rows => rows;
    public IReadOnlyList<FilmSettingGroup> ManualGroups { get; private set; } = [];
    public IReadOnlyList<FilmSettingGroup> BlockGroups { get; private set; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed() => PropertyChanged?.Invoke(this, new(""));
    private void Notify(string value) => Notices.Message = value;
    internal void SelectSection(string value)
    {
        if (stopping) return;
        if (section != value) { Deactivate(); section = value; }
        PresentSection();
    }
    internal async Task ShowAsync(string value)
    {
        SelectSection(value);
        if (stopping) return;
        if (preferences is null && IsLoaded && IsVisible) await LoadAsync();
    }
    private void PresentSection()
    {
        PreferencesForm.Visibility = section == "preferences" ? Visibility.Visible : Visibility.Collapsed;
        CleanForm.Visibility = section == "clean" ? Visibility.Visible : Visibility.Collapsed;
    }
    internal void Deactivate()
    {
        generation++;
        read?.Cancel();
        editor?.Close(); editor = null;
        readBusy = false; ReadFailed = false; preferences = null; rows = []; ManualGroups = []; BlockGroups = []; Work = ""; Changed();
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
    private async void View_Loaded(object sender, RoutedEventArgs e) { if (preferences is null) await LoadAsync(); }
    private void View_Unloaded(object sender, RoutedEventArgs e) => Deactivate();
    private async void Retry_Click(object sender, RoutedEventArgs e) { if (ReadFailed) await LoadAsync(); }
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
        string? selectedManual = (ManualTargets.SelectedItem as FilmSettingGroup)?.Id;
        string? selectedBlock = (BlockTargets.SelectedItem as FilmSettingGroup)?.Id;
        readBusy = true; ReadFailed = false; Work = "读取中…"; rows = []; ManualGroups = []; BlockGroups = []; Changed();
        try
        {
            var loaded = await service.ReadPreferencesAsync(token);
            JsonElement? library = requestedSection == "preferences" ? await service.ReadLibraryAsync(token) : null;
            if (ticket != generation || token.IsCancellationRequested) return;
            preferences = loaded; PopulatePreferences();
            if (library is { } value)
            {
                var manual = ReadRows(value, "manual"); var blocks = ReadRows(value, "blocks");
                rows = manual.Concat(blocks).ToArray();
                ManualGroups = Group(manual); BlockGroups = Group(blocks);
                Changed();
                ManualTargets.SelectedItem = ManualGroups.FirstOrDefault(group => group.Id == selectedManual) ?? ManualGroups.FirstOrDefault();
                BlockTargets.SelectedItem = BlockGroups.FirstOrDefault(group => group.Id == selectedBlock) ?? BlockGroups.FirstOrDefault();
            }
            Work = "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { if (ticket == generation) { ReadFailed = true; Work = "读取失败"; Notify("影视设置读取失败"); } }
        finally { if (ticket == generation) { readBusy = false; Changed(); } }
    }
    private static FilmSettingRow[] ReadRows(JsonElement library, string field)
    {
        if (FilmJson.Member(library, field).ValueKind != JsonValueKind.Array) throw new FilmPlaybackException("INVALID_RESPONSE");
        return FilmJson.Array(library, field).Select(value => new FilmSettingRow(
            FilmJson.Text(value, "title") is { Length: > 0 } title ? title : FilmJson.Text(value, "targetId"), value.Clone(), field == "manual")).ToArray();
    }
    private static FilmSettingGroup[] Group(IEnumerable<FilmSettingRow> values) => values
        .GroupBy(row => FilmJson.Text(row.Value, "targetId"))
        .Select(group => new FilmSettingGroup(group.Key, group.First().Title, group.ToArray())).ToArray();
    private sealed record OrderItem(string Id, string Label);
    private static string Label(string id) => id switch
    {
        "quality" => "画质", "pan" => "网盘", "keyword" => "关键词", "yidong" => "移动云盘",
        "tianyi" => "天翼云盘", "quark" => "夸克", "uc" => "UC", "baidu" => "百度网盘", _ => id
    };
    private void PopulatePreferences()
    {
        if (preferences is not { } value) return;
        PopulateOrders();
        Keywords.Text = string.Join(",", value.Keywords);
        CleanKeywords.Text = string.Join(",", value.SiteCleanKeywords);
    }
    private void PopulateOrders()
    {
        if (preferences is not { } value) return;
        ReplaceOrder(ruleItems, value.RuleOrder); ReplaceOrder(panItems, value.PanOrder);
    }
    private static void ReplaceOrder(ObservableCollection<OrderItem> items, string[] order)
    {
        for (int index = 0; index < order.Length; index++)
        {
            var item = items.FirstOrDefault(item => item.Id == order[index]);
            if (item is null) items.Insert(index, new(order[index], Label(order[index])));
            else if (items.IndexOf(item) != index) items.Move(items.IndexOf(item), index);
        }
    }
    private async void Order_Move(object? sender, SettingsPriorityMoveEventArgs e)
    {
        if (preferences is null || !Idle || sender is not SettingsPriorityList list ||
            list.ItemsSource is not ObservableCollection<OrderItem> items) return;
        int source = items.IndexOf((OrderItem)e.Source), destination = items.IndexOf((OrderItem)e.Target);
        if (source < 0 || destination < 0 || source == destination) return;
        long ticket = generation;
        items.Move(source, destination);
        var order = items.Select(item => item.Id).ToArray();
        var draft = ReferenceEquals(list, RuleOrder) ? preferences with { RuleOrder = order } : preferences with { PanOrder = order };
        bool saved = await MutateAsync(() => service.SavePreferencesAsync(draft),
            value => { preferences = value; PopulateOrders(); }, "设置已保存");
        if (!saved && ticket == generation) PopulateOrders();
    }
    private static string[] Words(string value) => value.Split([',', '，']).Select(word => word.Trim()).Where(word => word.Length > 0).ToArray();
    private FilmPreferences Draft() => section == "clean" ? preferences! with { SiteCleanKeywords = Words(CleanKeywords.Text) }
        : preferences! with { Keywords = Words(Keywords.Text) };
    private async void SavePreferences_Click(object sender, RoutedEventArgs e)
    {
        if (preferences is null || !Idle) return;
        var draft = Draft();
        await MutateAsync(() => service.SavePreferencesAsync(draft), saved =>
        {
            preferences = saved;
            if (section == "clean") CleanKeywords.Text = string.Join(",", saved.SiteCleanKeywords);
            else Keywords.Text = string.Join(",", saved.Keywords);
        }, "设置已保存");
    }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (preferences is null || !Idle || sender is not Button { Tag: string group }) return;
        var value = preferences; var defaults = FilmPreferences.Defaults;
        var next = group switch
        {
            "rule" => value with { RuleOrder = defaults.RuleOrder }, "pan" => value with { PanOrder = defaults.PanOrder },
            "keywords" => value with { Keywords = defaults.Keywords }, _ => value with { SiteCleanKeywords = defaults.SiteCleanKeywords }
        };
        await MutateAsync(() => service.SavePreferencesAsync(next), saved =>
        {
            preferences = saved;
            if (group == "keywords") Keywords.Text = string.Join(",", saved.Keywords);
            else if (group == "clean") CleanKeywords.Text = string.Join(",", saved.SiteCleanKeywords);
            else PopulateOrders();
        }, "已恢复默认");
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
        long ticket = generation;
        if (!Confirm("删除记录", row.Title)) return;
        if (ticket != generation) return;
        await MutateAsync(async () =>
        {
            if (row.Manual) await service.RemoveManualAsync(FilmJson.RequiredText(row.Value, "id"));
            else await service.RemoveBlockAsync(row.Value);
            return true;
        }, null, "记录已删除");
        if (ticket == generation) await LoadAsync();
    }
    private async void ClearRecords_Click(object sender, RoutedEventArgs e)
    {
        if (!Idle || section != "preferences") return;
        long ticket = generation;
        if (!Confirm("清空匹配禁用记录", "清空全部匹配禁用记录？") || ticket != generation) return;
        await MutateAsync(() => service.RemoveBlockAsync(null), null, "匹配禁用记录已清空");
        if (ticket == generation) await LoadAsync();
    }
    private bool Confirm(string title, string content) => MessageBox.Show(Window.GetWindow(this), content, title,
        MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
    private void AddManual_Click(object sender, RoutedEventArgs e) => OpenEditor(null);
    private void Edit_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: FilmSettingRow row } && Rows.Contains(row)) OpenEditor(row.Value); }

    private void OpenEditor(JsonElement? stored)
    {
        if (!Idle || section != "preferences" || editor is not null) return;
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
        var urlBox = new TextBox { Text = FilmJson.Text(share, "url"), Style = (Style)FindResource("Settings.TextBox") };
        AutomationProperties.SetName(urlBox, "分享链接"); content.Children.Add(urlBox);
        content.Children.Add(new TextBlock { Text = "提取码", Margin = new(0, 10, 0, 5) });
        var password = new TextBox { Text = FilmJson.Text(share, "password"), Style = (Style)FindResource("Settings.TextBox") };
        AutomationProperties.SetName(password, "分享提取码"); content.Children.Add(password);
        var seasonBox = Field("季线索", FilmJson.Text(value, "seasonHint"));
        var enabled = new CheckBox { Content = "启用", IsChecked = stored is null || FilmJson.Bool(value, "enabled"), Margin = new(0, 16, 0, 0) };
        var autoDisable = new CheckBox { Content = "连续失败自动停用", IsChecked = FilmJson.Bool(value, "autoDisable"), Margin = new(0, 10, 0, 0) };
        content.Children.Add(enabled); content.Children.Add(autoDisable);
        var save = new Button { Content = "保存", Style = (Style)FindResource("Settings.PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0) };
        content.Children.Add(save);
        var dialog = new DesktopWindow { Owner = Window.GetWindow(this), Title = stored is null ? "添加人工匹配" : "编辑人工匹配",
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
                fields["share"] = new JsonObject { ["url"] = urlBox.Text.Trim(), ["password"] = password.Text };
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
