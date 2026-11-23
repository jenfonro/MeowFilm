using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed class FilmOrderOption(string id, string label) : ISettingsPriorityItem
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public bool Enabled => false;
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

public sealed record FilmSettingRow(string Title, JsonElement Value, bool Manual)
{
    public string Source => FilmJson.Text(Value, "sourceKey");
    public string DetailId => FilmJson.Text(Value, "detailId");
    public string Pan => FilmPlaybackSettingsView.FormatPanLabel(FilmJson.Text(Value, Manual ? "provider" : "lineId"));
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
    private CancellationTokenSource? editorLifetime;
    private CancellationTokenSource? confirmLifetime;
    private string section = "preferences";
    private long generation;
    private bool readBusy, libraryBusy, mutating, stopping;
    private readonly ObservableCollection<FilmOrderOption> ruleItems = [], panItems = [];
    private IReadOnlyList<FilmSettingRow> rows = [];

    internal FilmPlaybackSettingsView(FilmPlaybackSettings service, Func<FilmPlaybackTarget?> target, string section = "preferences")
    {
        this.service = service; this.target = target; this.section = section;
        InitializeComponent();
        RuleOrder.ItemsSource = ruleItems; PanOrder.ItemsSource = panItems;
        PresentSection();
    }
    public bool Idle => !stopping && !readBusy && !mutating;
    public bool ReadFailed { get; private set; }
    public bool LibraryFailed { get; private set; }
    public bool CanRetryRead => Idle && !libraryBusy;
    public bool HasReadFailure => ReadFailed || LibraryFailed;
    public bool CanEditLibrary => Idle && !libraryBusy && !LibraryFailed;
    public string Work { get; private set; } = "";
    public IReadOnlyList<FilmSettingRow> Rows => rows;
    public IReadOnlyList<FilmSettingGroup> ManualGroups { get; private set; } = [];
    public IReadOnlyList<FilmSettingGroup> BlockGroups { get; private set; } = [];
    public IReadOnlyList<FilmSettingRow> SelectedManualRows => (ManualTargets.SelectedItem as FilmSettingGroup)?.Rows ?? [];
    public IReadOnlyList<FilmSettingRow> SelectedBlockRows => (BlockTargets.SelectedItem as FilmSettingGroup)?.Rows ?? [];
    public bool HasNoManualRows => SelectedManualRows.Count == 0;
    public bool HasNoBlockRows => SelectedBlockRows.Count == 0;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name = "") => PropertyChanged?.Invoke(this, new(name));
    private void ManualTargets_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Changed(nameof(SelectedManualRows));
        Changed(nameof(HasNoManualRows));
    }
    private void BlockTargets_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Changed(nameof(SelectedBlockRows));
        Changed(nameof(HasNoBlockRows));
    }
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
        if (preferences is null && IsLoaded && DesktopVisual.IsVisible(this)) await LoadAsync();
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
        editorLifetime?.Cancel();
        confirmLifetime?.Cancel();
        readBusy = libraryBusy = false; ReadFailed = LibraryFailed = false; preferences = null; rows = []; ManualGroups = []; BlockGroups = []; Work = ""; Changed();
    }
    internal async Task PrepareShutdownAsync()
    {
        stopping = true; Deactivate();
        // Drain admitted work, not the historical outcome of the last save. A failed
        // save is reported by its UI wrapper; it is not a failed resource retirement.
        await Task.WhenAll(reading ?? Task.CompletedTask, (Task?)mutation ?? Task.CompletedTask);
    }
    private async void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (preferences is null)
            try { await LoadAsync(); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
    }
    private void View_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) Deactivate();
    }
    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (HasReadFailure && CanRetryRead)
            try { await LoadAsync(); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
    }
    private Task LoadAsync()
    {
        if (stopping || !IsLoaded || !DesktopVisual.IsVisible(this)) return Task.CompletedTask;
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
        using var currentRead = new CancellationTokenSource();
        read = currentRead;
        var token = currentRead.Token;
        long ticket = generation;
        string requestedSection = section;
        string? selectedManual = (ManualTargets.SelectedItem as FilmSettingGroup)?.Id;
        string? selectedBlock = (BlockTargets.SelectedItem as FilmSettingGroup)?.Id;
        readBusy = preferences is null; ReadFailed = LibraryFailed = false; Work = ""; Changed();
        try
        {
            var loaded = preferences ?? await service.ReadPreferencesAsync(token);
            if (ticket != generation || token.IsCancellationRequested) return;
            if (preferences is null) { PopulatePreferences(loaded); preferences = loaded; }
            readBusy = false;
            libraryBusy = requestedSection == "preferences";
            Changed();
            if (libraryBusy)
            {
                try
                {
                    var value = await service.ReadLibraryAsync(token);
                    if (ticket != generation || token.IsCancellationRequested) return;
                    var manual = ReadRows(value, "manual"); var blocks = ReadRows(value, "blocks");
                    rows = manual.Concat(blocks).ToArray();
                    ManualGroups = Group(manual); BlockGroups = Group(blocks);
                    Changed();
                    ManualTargets.SelectedItem = ManualGroups.FirstOrDefault(group => group.Id == selectedManual) ?? ManualGroups.FirstOrDefault();
                    BlockTargets.SelectedItem = BlockGroups.FirstOrDefault(group => group.Id == selectedBlock) ?? BlockGroups.FirstOrDefault();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception)
                {
                    if (ticket == generation) { LibraryFailed = true; Notify("匹配记录读取失败"); }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { if (ticket == generation) { ReadFailed = true; Work = "读取失败"; Notify("影视设置读取失败"); } }
        finally
        {
            if (ReferenceEquals(read, currentRead)) read = null;
            if (ticket == generation) { readBusy = libraryBusy = false; Changed(); }
        }
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
    internal static string FormatPanLabel(string value) => value.Trim().ToLowerInvariant() switch
    {
        "yidong" or "mobile" or "139" => "移动云盘",
        "tianyi" or "189" => "天翼云盘",
        "quark" => "夸克网盘",
        "uc" => "UC 网盘",
        "baidu" => "百度网盘",
        "115" => "115 网盘",
        "123" => "123 云盘",
        "ali" or "aliyun" or "aliyundrive" => "阿里云盘",
        "xunlei" => "迅雷云盘",
        "pikpak" => "PikPak",
        _ => value
    };
    private static string Label(string id) => id switch
    {
        "quality" => "画质",
        "pan" => "网盘",
        "keyword" => "关键词",
        _ => FormatPanLabel(id)
    };
    private void PopulatePreferences(FilmPreferences value)
    {
        if (section == "clean") CleanKeywords.Text = string.Join(",", value.SiteCleanKeywords);
        else
        {
            var rules = value.RuleOrder; var pans = value.PanOrder; var keywords = value.Keywords;
            ReplaceOrder(ruleItems, rules); ReplaceOrder(panItems, pans);
            Keywords.Text = string.Join(",", keywords);
        }
    }
    private void PopulateOrders()
    {
        if (preferences is not { } value) return;
        ReplaceOrder(ruleItems, value.RuleOrder); ReplaceOrder(panItems, value.PanOrder);
    }
    private static void ReplaceOrder(ObservableCollection<FilmOrderOption> items, string[] order)
    {
        order = order.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var item in items.Where(item => !order.Contains(item.Id, StringComparer.Ordinal)).ToArray()) items.Remove(item);
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
            list.ItemsSource is not ObservableCollection<FilmOrderOption> items) return;
        int source = items.IndexOf((FilmOrderOption)e.Source), destination = items.IndexOf((FilmOrderOption)e.Target);
        if (source < 0 || destination < 0 || source == destination) return;
        long ticket = generation;
        items.Move(source, destination);
        var order = items.Select(item => item.Id).ToArray();
        var draft = ReferenceEquals(list, RuleOrder) ? preferences with { RuleOrder = order } : preferences with { PanOrder = order };
        bool saved = await MutateAsync(() => service.SavePreferencesAsync(draft, ReferenceEquals(list, RuleOrder) ? "ruleOrder" : "panOrder"),
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
        await MutateAsync(() => service.SavePreferencesAsync(draft, section == "clean" ? "siteCleanKeywords" : "keywords"), saved =>
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
        string field = group switch { "rule" => "ruleOrder", "pan" => "panOrder", "keywords" => "keywords", _ => "siteCleanKeywords" };
        await MutateAsync(() => service.SavePreferencesAsync(next, field), saved =>
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
                if (ticket == generation && IsLoaded && DesktopVisual.IsVisible(this)) { publish?.Invoke(saved); Notify(success); }
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
        if (!CanEditLibrary || sender is not Button { Tag: FilmSettingRow row } || !Rows.Contains(row)) return;
        long ticket = generation;
        if (!await ConfirmAsync("删除记录", row.Title)) return;
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
        if (!CanEditLibrary || section != "preferences") return;
        long ticket = generation;
        if (!await ConfirmAsync("清空匹配禁用记录", "清空全部匹配禁用记录？") || ticket != generation) return;
        await MutateAsync(() => service.RemoveBlockAsync(null), null, "匹配禁用记录已清空");
        if (ticket == generation) await LoadAsync();
    }
    private async Task<bool> ConfirmAsync(string title, string content)
    {
        if (confirmLifetime is not null) return false;
        using var cts = new CancellationTokenSource();
        confirmLifetime = cts;
        var request = new DesktopDialogRequest
        {
            Title = title,
            Message = content,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = DesktopDialogDefaultButton.Close
        };
        try
        {
            return await DesktopModalHost.ShowDialogAsync(this, request, cts.Token) == DesktopDialogResult.Primary;
        }
        catch (OperationCanceledException) { return false; }
        catch (ObjectDisposedException) { return false; }
        catch (InvalidOperationException) { return false; }
        finally { if (ReferenceEquals(confirmLifetime, cts)) confirmLifetime = null; }
    }
    private void AddManual_Click(object sender, RoutedEventArgs e) => OpenEditor(null);
    private void Edit_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: FilmSettingRow row } && Rows.Contains(row)) OpenEditor(row.Value); }

    private async void OpenEditor(JsonElement? stored)
    {
        if (!CanEditLibrary || section != "preferences" || editorLifetime is not null) return;
        long ticket = generation;
        var editor = new FilmManualMatchDialogView(stored, target()?.Id ?? "");
        var cts = new CancellationTokenSource();
        editorLifetime = cts;
        bool savedOk = false;
        var request = new DesktopDialogRequest
        {
            Title = stored is null ? "添加人工匹配" : "编辑人工匹配",
            Content = editor,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = DesktopDialogDefaultButton.Primary,
            MinWidth = 380,
            MaxWidth = 520,
            OnPrimaryAsync = async () =>
            {
                if (ticket != generation || !ReferenceEquals(editorLifetime, cts) || !Idle) return false;
                if (!editor.TryBuildPayload(out var payload, out var errorMessage))
                {
                    Notify(errorMessage!);
                    return false;
                }
                bool saved = await MutateAsync(() => service.SaveManualAsync(payload), null, "人工匹配已保存");
                if (ticket != generation || !ReferenceEquals(editorLifetime, cts)) return true;
                if (saved) { savedOk = true; return true; }
                return false;
            }
        };
        try
        {
            await DesktopModalHost.ShowDialogAsync(this, request, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        finally
        {
            editor.ClearSensitiveInputs();
            if (ReferenceEquals(editorLifetime, cts)) editorLifetime = null;
            cts.Dispose();
        }
        if (savedOk && ticket == generation) await LoadAsync();
    }
}
