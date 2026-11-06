using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace MeowFilm.Capabilities;

public abstract class FilmSettingsSourceRow : INotifyPropertyChanged
{
    private bool selected;
    public bool Selected { get => selected; set { if (selected == value) return; selected = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class FilmSettingsCatRow(FilmCatSource source) : FilmSettingsSourceRow
{
    public FilmCatSource Source { get; private set; } = source;
    public string Name => Source.Name;
    public bool Enabled => Source.Enabled;
    public bool CanRestart => Source.CanRestart;
    public string TypeText => Source.SourceType switch { "local" => "本地", "network" => "网络", _ => Source.SourceType };
    public string StateText => Source.State switch
    {
        "ready" => "运行中", "starting" => "启动中", "stopping" => "停止中",
        "error" or "failed" => "运行失败", "stopped" or "idle" => Source.Enabled ? "未运行" : "已停用",
        "disabled" => "已停用", _ => Source.State
    };
    public string ModifiedText => Source.ModifiedAt is { } timestamp
        ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) : "—";
    public string UpdateText => Source.UpdateState switch
    {
        "updated" or "success" => "更新成功", "unchanged" or "current" => "无变化",
        "checking" or "updating" => "更新中", "failed" or "error" => "更新失败",
        "" or "idle" or "none" or "never" => "—", _ => Source.UpdateState
    };
    public string? ErrorText => Source.Error;
    public string? UpdateErrorText => Source.UpdateError;
    public void Update(FilmCatSource source) { Source = source; Changed(""); }
}

public sealed class FilmSettingsVideoRow(FilmVideoSource source, bool canDetect) : FilmSettingsSourceRow
{
    private string orderText = source.Order.ToString(CultureInfo.InvariantCulture);
    public FilmVideoSource Source { get; private set; } = source;
    public string Name => Source.Name;
    public string Api => Source.Api;
    public bool Enabled => Source.Options.Enabled;
    public bool HomeVisible => Source.Options.HomeVisible;
    public bool SearchEnabled => Source.Options.SearchEnabled;
    public bool AggregateImages => Source.Options.AggregateImages;
    public bool CanDetect { get; private set; } = canDetect;
    public string OrderText { get => orderText; set { if (orderText == value) return; orderText = value; Changed(); } }
    public string StateText => Source.Detection?.Result switch
    {
        "valid" => "检测通过", "search_error" => "搜索异常", "category_error" => "分类异常",
        "invalid" => "检测失败", null => "未检测", var state => state
    };
    public string ErrorText => Source.Detection?.Error ?? "—";
    public void Update(FilmVideoSource source, bool canDetect)
    {
        bool editingOrder = OrderText != Source.Order.ToString(CultureInfo.InvariantCulture);
        Source = source; CanDetect = canDetect;
        if (!editingOrder) ResetOrder();
        Changed("");
    }
    public void ResetOrder() => OrderText = Source.Order.ToString(CultureInfo.InvariantCulture);
}
