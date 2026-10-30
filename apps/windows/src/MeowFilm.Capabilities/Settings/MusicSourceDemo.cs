using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Data;

namespace MeowFilm.Capabilities;

internal enum MusicSourceDemoState { Ready, Idle, Error, Disabled }

internal readonly record struct MusicSourceDemoHandle(MusicSourceDemoRow Row, long Epoch, long Revision);

internal sealed record MusicSourceDemoImport(string Key, string Name, string Version, string Address,
    string Origin, string[] Platforms, int Bytes = 0)
{
    public string FileMetadata => $"{Name} · {Version} · {Bytes / 1024} KB";
}

internal sealed class MusicSourceDemoEdit(MusicSourceDemoRow row)
{
    public string Name { get; set; } = row.Name;
    public string Address { get; set; } = row.Address;
    public bool Enabled { get; set; } = row.Enabled;
    public string Version { get; } = row.Version;
    public string PreviousVersion { get; } = row.PreviousVersion.Length == 0 ? "—" : row.PreviousVersion;
    public string Origin { get; } = row.Origin == "网络" ? "网络地址" : "本地文件";
    public string AddressLabel { get; } = row.Origin == "网络" ? "网络地址" : "文件名";
    public IReadOnlyList<string> Platforms { get; } = row.Platforms;
}

internal sealed class MusicSourceDemoRow : INotifyPropertyChanged
{
    private readonly MusicSourceDemo owner;

    internal MusicSourceDemoRow(MusicSourceDemo owner, string id, string key, string name, string version,
        string address, string origin, string[] platforms, MusicSourceDemoState state, bool enabled,
        string previous = "", string error = "")
    {
        this.owner = owner;
        Id = id; Key = key; Name = name; Version = version; Address = address; Origin = origin;
        Platforms = Array.AsReadOnly(platforms.ToArray());
        State = state; Enabled = enabled; PreviousVersion = previous; Error = error;
    }

    public string Id { get; }
    internal string Key { get; }
    internal long Revision { get; private set; }
    public string Name { get; private set; }
    public string Version { get; private set; }
    public string PreviousVersion { get; private set; }
    public string Address { get; private set; }
    public string Origin { get; }
    public IReadOnlyList<string> Platforms { get; }
    public IReadOnlyList<string> DisplayPlatforms => Platforms.Count <= 5 ? Platforms :
        Platforms.Take(5).Append("+" + (Platforms.Count - 5).ToString(CultureInfo.InvariantCulture)).ToArray();
    public string PlatformsText => string.Join("、", Platforms);
    public string Metadata => $"{Version} · {Origin}";
    public MusicSourceDemoState State { get; private set; }
    public string StateText => State switch
    {
        MusicSourceDemoState.Ready => "可用",
        MusicSourceDemoState.Idle => "待加载",
        MusicSourceDemoState.Error => "加载失败",
        _ => "已停用"
    };
    public string Error { get; private set; }
    public bool Enabled { get; private set; }
    public bool IsSelected { get; private set; }
    public bool IsCurrent => ReferenceEquals(owner.CurrentDemoSource, this);
    public bool IsDragging => ReferenceEquals(owner.DraggingSource, this);
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Select(bool value) { IsSelected = value; Changed(nameof(IsSelected)); }
    internal void SetEnabled(bool value)
    {
        Enabled = value;
        SetState(value ? MusicSourceDemoState.Idle : MusicSourceDemoState.Disabled);
    }
    internal void SetState(MusicSourceDemoState value, string error = "")
    {
        State = value; Error = error; Revision++;
        Changed(nameof(Enabled)); Changed(nameof(State)); Changed(nameof(StateText)); Changed(nameof(Error));
    }
    internal void Edit(MusicSourceDemoEdit edit)
    {
        Name = edit.Name.Trim(); Address = edit.Address.Trim();
        if (Enabled != edit.Enabled) SetEnabled(edit.Enabled);
        Revision++;
        Changed(nameof(Name)); Changed(nameof(Address));
    }
    internal void SetVersion(string version, string previous)
    {
        Version = version; PreviousVersion = previous; Revision++;
        Changed(nameof(Version)); Changed(nameof(PreviousVersion)); Changed(nameof(Metadata));
    }
    internal void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}

// This owner has no application, native settings, account, file, network or script dependency.
internal sealed class MusicSourceDemo : INotifyPropertyChanged
{
    internal const int MaximumSources = 200;
    private readonly ObservableCollection<MusicSourceDemoRow> sources = [];
    private readonly ListCollectionView visibleSources;
    private long epoch;
    private int nextImport;
    private bool active = true;
    private string platformFilter = "", stateFilter = "";
    private MusicSourceDemoRow? currentDemoSource, draggingSource;
    private string status = "";

    internal MusicSourceDemo()
    {
        AddInitial("stellar", "星河音源", "1.4.2", "网络", "https://sources.example/stellar.js",
            ["网易云", "QQ", "酷狗", "酷我", "咪咕"], MusicSourceDemoState.Ready, true, "1.4.1");
        AddInitial("qingt", "轻听音源", "1.2.0", "本地", "qingt.js",
            ["网易云", "QQ", "酷狗"], MusicSourceDemoState.Ready, true, "1.1.0");
        AddInitial("roaming", "漫游音源", "2.1.0", "网络", "https://sources.example/roaming.js",
            ["网易云", "QQ", "酷狗"], MusicSourceDemoState.Idle, true, "2.0.4");
        AddInitial("forest", "森屿音源", "1.0.6", "网络", "https://sources.example/forest.js",
            ["QQ", "酷狗"], MusicSourceDemoState.Error, false, "1.0.5", "连接超时");
        AddInitial("minimal", "极简音源", "1.0.0", "本地", "minimal.js",
            ["网易云", "QQ"], MusicSourceDemoState.Disabled, false);
        sources[2].Select(true);
        Sources = new(sources);
        visibleSources = new(sources) { Filter = value => value is MusicSourceDemoRow row && Matches(row) };
    }

    public ReadOnlyObservableCollection<MusicSourceDemoRow> Sources { get; }
    public ICollectionView VisibleSources => visibleSources;
    public string CustomCountText => $"{sources.Count} 个自定义音源";
    public int SelectedCount => sources.Count(row => row.IsSelected);
    public string SelectedCountText => $"已选择 {SelectedCount} 项";
    public bool HasSelection => active && SelectedCount > 0 && draggingSource is null;
    public bool CanImport => active && sources.Count < MaximumSources;
    public bool CanMoveUp => MoveTarget(-1) is not null;
    public bool CanMoveDown => MoveTarget(1) is not null;
    public bool? AllVisibleSelected
    {
        get
        {
            var rows = VisibleRows();
            int selected = rows.Count(row => row.IsSelected);
            return selected == 0 ? false : selected == rows.Length ? true : null;
        }
    }
    public bool HasVisibleSources => !visibleSources.IsEmpty;
    public MusicSourceDemoRow? CurrentDemoSource => currentDemoSource;
    public MusicSourceDemoRow? DraggingSource => draggingSource;
    public bool UsesOfficialSources => currentDemoSource is null;
    public string Status => status;
    internal long Epoch => epoch;
    internal bool IsActive => active;
    internal event Action? Invalidating;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal MusicSourceDemoRow[] VisibleRows() => visibleSources.Cast<MusicSourceDemoRow>().ToArray();
    internal MusicSourceDemoHandle? Capture(MusicSourceDemoRow row) =>
        active && sources.Contains(row) ? new(row, epoch, row.Revision) : null;
    internal bool Current(MusicSourceDemoHandle handle) => active && handle.Epoch == epoch &&
        handle.Revision == handle.Row.Revision && sources.Contains(handle.Row);
    internal void Activate() { active = true; Fence(); Changed(nameof(CanImport)); NotifySelection(); }
    internal void Deactivate()
    {
        active = false; Fence(); status = "";
        Changed(nameof(Status)); Changed(nameof(CanImport)); NotifySelection();
    }
    internal void Fence() { epoch++; Invalidating?.Invoke(); }

    internal void Select(MusicSourceDemoRow row, bool selected)
    {
        if (!active || !sources.Contains(row) || !Matches(row) || row.IsSelected == selected) return;
        Fence(); row.Select(selected); NotifySelection();
    }
    internal void SelectOnly(MusicSourceDemoRow row)
    {
        if (!active || !sources.Contains(row) || !Matches(row)) return;
        Fence();
        foreach (var item in sources.Where(item => item.IsSelected != ReferenceEquals(item, row)))
            item.Select(ReferenceEquals(item, row));
        NotifySelection();
    }
    internal void ToggleAll()
    {
        if (!active) return;
        var rows = VisibleRows();
        bool selected = AllVisibleSelected != true;
        Fence();
        foreach (var row in rows) row.Select(selected);
        NotifySelection();
    }
    internal void Filter(string platform, string state)
    {
        if (!active || platform == platformFilter && state == stateFilter) return;
        Fence(); platformFilter = platform; stateFilter = state;
        foreach (var row in sources.Where(row => row.IsSelected && !Matches(row))) row.Select(false);
        visibleSources.Refresh(); NotifySelection();
    }

    internal bool MoveSelected(int direction)
    {
        if (SelectedCount != 1) return false;
        var source = sources.Single(row => row.IsSelected);
        var target = MoveTarget(direction);
        return target is not null && Capture(source) is { } handle && Move(handle, target);
    }
    internal bool Move(MusicSourceDemoHandle handle, MusicSourceDemoRow target)
    {
        if (!Current(handle) || !sources.Contains(target) || !Matches(target) ||
            !Matches(handle.Row) || ReferenceEquals(handle.Row, target)) return false;
        int from = sources.IndexOf(handle.Row), to = sources.IndexOf(target);
        Fence(); sources.Move(from, to);
        NotifySelection(); Notice("顺序已更新"); return true;
    }
    internal void SetDragging(MusicSourceDemoRow? row)
    {
        var previous = draggingSource; draggingSource = row;
        previous?.Changed(nameof(MusicSourceDemoRow.IsDragging));
        row?.Changed(nameof(MusicSourceDemoRow.IsDragging)); NotifySelection();
    }

    internal bool Enable(MusicSourceDemoHandle handle, bool value)
    {
        if (!Current(handle)) return false;
        Fence(); handle.Row.SetEnabled(value);
        if (!value && ReferenceEquals(currentDemoSource, handle.Row)) SetCurrent(null);
        RefreshStates(); Notice(value ? "音源已启用" : "音源已停用"); return true;
    }
    internal void EnableSelected(bool value)
    {
        if (!HasSelection) return;
        var selected = sources.Where(row => row.IsSelected).ToArray();
        Fence();
        foreach (var row in selected) row.SetEnabled(value);
        if (!value && currentDemoSource is { IsSelected: true }) SetCurrent(null);
        RefreshStates(); Notice(value ? $"已启用 {selected.Length} 项" : $"已停用 {selected.Length} 项");
    }
    internal void DetectSelected()
    {
        if (!HasSelection) return;
        var selected = sources.Where(row => row.IsSelected).ToArray();
        Fence();
        foreach (var row in selected) Detect(row);
        RefreshStates();
        Notice(selected.Any(row => row.State == MusicSourceDemoState.Error) ? "检测完成 · 连接超时" : "检测完成");
    }
    internal bool Load(MusicSourceDemoHandle handle)
    {
        if (!Current(handle)) return false;
        Fence(); handle.Row.SetEnabled(true); Detect(handle.Row);
        RefreshStates(); Notice(handle.Row.StateText); return true;
    }
    internal bool Update(MusicSourceDemoHandle handle)
    {
        if (!Current(handle) || !Version.TryParse(handle.Row.Version, out var version)) return false;
        Fence();
        handle.Row.SetVersion($"{version.Major}.{version.Minor}.{Math.Max(0, version.Build) + 1}", handle.Row.Version);
        Notice("脚本版本已更新"); return true;
    }
    internal bool Rollback(MusicSourceDemoHandle handle)
    {
        if (!Current(handle) || handle.Row.PreviousVersion.Length == 0) return false;
        Fence(); handle.Row.SetVersion(handle.Row.PreviousVersion, handle.Row.Version);
        Notice("版本已回退"); return true;
    }
    internal bool Use(MusicSourceDemoHandle handle)
    {
        if (!Current(handle) || !handle.Row.Enabled || handle.Row.State == MusicSourceDemoState.Error) return false;
        Fence(); SetCurrent(handle.Row); Notice($"当前使用 · {handle.Row.Name}"); return true;
    }
    internal void UseOfficial()
    {
        if (!active) return;
        Fence(); SetCurrent(null); Notice("当前使用 · 官方音源");
    }
    internal bool Remove(MusicSourceDemoHandle handle)
    {
        if (!Current(handle)) return false;
        Fence();
        if (ReferenceEquals(currentDemoSource, handle.Row)) SetCurrent(null);
        sources.Remove(handle.Row);
        Changed(nameof(CustomCountText)); Changed(nameof(CanImport)); NotifySelection();
        Notice("音源已删除"); return true;
    }
    internal bool Save(MusicSourceDemoHandle handle, MusicSourceDemoEdit edit)
    {
        if (!Current(handle)) return false;
        if (string.IsNullOrWhiteSpace(edit.Name) || edit.Name.Trim().Length > 80 || edit.Name.Any(char.IsControl))
        { Notice("音源名称无效"); return false; }
        if (!ValidAddress(edit.Address, handle.Row.Origin))
        { Notice(handle.Row.Origin == "网络" ? "演示地址须使用 https://sources.example/ 下的 .js 地址" : "文件名须为 .js 文件名"); return false; }
        Fence(); handle.Row.Edit(edit);
        if (!handle.Row.Enabled && ReferenceEquals(currentDemoSource, handle.Row)) SetCurrent(null);
        RefreshStates(); Notice("音源已保存"); return true;
    }

    internal static MusicSourceDemoImport NetworkImport() => new("ambient", "拾光音源", "1.0.2",
        "https://sources.example/ambient.js", "网络", ["网易云", "QQ", "酷狗"]);
    internal static MusicSourceDemoImport[] LocalImports() =>
    [
        new("listenwind", "听风音源", "1.0.0", "listenwind.js", "本地", ["网易云", "QQ", "酷狗"], 188416),
        new("moonlight", "皓月音源", "1.1.3", "moonlight.js", "本地", ["网易云", "QQ", "酷狗"], 77824)
    ];
    internal bool Import(long ticket, IReadOnlyList<MusicSourceDemoImport> items, bool enabled)
    {
        if (!active || ticket != epoch || items.Count == 0) return false;
        if (sources.Count + items.Count > MaximumSources) { Notice("演示音源已达上限"); return false; }
        Fence();
        foreach (var item in items)
            sources.Add(new(this, $"{item.Key}-{++nextImport}", item.Key, item.Name, item.Version, item.Address,
                item.Origin, item.Platforms, enabled ? MusicSourceDemoState.Idle : MusicSourceDemoState.Disabled, enabled));
        Changed(nameof(CustomCountText)); Changed(nameof(CanImport)); NotifySelection();
        Notice($"已导入 {items.Count} 个音源"); return true;
    }
    internal string Export() => JsonSerializer.Serialize(new
    {
        kind = "meowfilm.music-sources.demo",
        production = false,
        currentDemoSource = currentDemoSource?.Id ?? "official",
        sources = sources.Select(row => new
        {
            id = row.Id, name = row.Name, version = row.Version, previous = row.PreviousVersion,
            address = row.Address, origin = row.Origin, platforms = row.Platforms,
            state = row.StateText, error = row.Error, enabled = row.Enabled
        }).ToArray()
    }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    internal static bool ValidAddress(string text, string origin)
    {
        text = text.Trim();
        if (text.Length is 0 or > 240 || text.Any(char.IsControl)) return false;
        if (origin == "本地")
            return text.EndsWith(".js", StringComparison.OrdinalIgnoreCase) &&
                text.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
            uri.Host == "sources.example" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
            uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
            uri.AbsolutePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase);
    }
    private void AddInitial(string id, string name, string version, string origin, string address,
        string[] platforms, MusicSourceDemoState state, bool enabled, string previous = "", string error = "") =>
        sources.Add(new(this, id, id, name, version, address, origin, platforms, state, enabled, previous, error));
    private bool Matches(MusicSourceDemoRow row) => (platformFilter.Length == 0 || row.Platforms.Contains(platformFilter)) &&
        (stateFilter.Length == 0 || row.StateText == stateFilter);
    private MusicSourceDemoRow? MoveTarget(int direction)
    {
        if (!active || draggingSource is not null || SelectedCount != 1 || direction is not (-1 or 1)) return null;
        var rows = VisibleRows();
        int index = Array.FindIndex(rows, row => row.IsSelected), target = index + direction;
        return index >= 0 && target >= 0 && target < rows.Length ? rows[target] : null;
    }
    private static void Detect(MusicSourceDemoRow row)
    {
        if (row.Key == "forest") row.SetState(MusicSourceDemoState.Error, "连接超时");
        else row.SetState(row.Enabled ? MusicSourceDemoState.Ready : MusicSourceDemoState.Disabled);
    }
    private void SetCurrent(MusicSourceDemoRow? row)
    {
        var previous = currentDemoSource; currentDemoSource = row;
        previous?.Changed(nameof(MusicSourceDemoRow.IsCurrent));
        row?.Changed(nameof(MusicSourceDemoRow.IsCurrent));
        Changed(nameof(CurrentDemoSource)); Changed(nameof(UsesOfficialSources));
    }
    private void RefreshStates()
    {
        foreach (var row in sources.Where(row => row.IsSelected && !Matches(row))) row.Select(false);
        visibleSources.Refresh(); NotifySelection();
    }
    private void NotifySelection()
    {
        foreach (var property in new[] { nameof(SelectedCount), nameof(SelectedCountText), nameof(HasSelection),
            nameof(AllVisibleSelected), nameof(HasVisibleSources), nameof(CanMoveUp), nameof(CanMoveDown) })
            Changed(property);
    }
    internal void Notice(string message)
    {
        string next = $"演示数据 · {message}";
        if (status == next) { status = ""; Changed(nameof(Status)); }
        status = next; Changed(nameof(Status));
    }
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}
