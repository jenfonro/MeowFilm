using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using MeowFilm.Desktop;

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
        "ready" => "正常", "starting" => "启动中", "stopping" => "停止中",
        "error" or "failed" => "运行失败", "stopped" or "idle" => Source.Enabled ? "未运行" : "已停用",
        "disabled" => "已停用", _ => Source.State
    };
    public ManagementStatusTone StateTone => Source.State switch
    {
        "ready" => ManagementStatusTone.Success,
        "error" or "failed" => ManagementStatusTone.Failure,
        _ => ManagementStatusTone.Neutral
    };
    public string ModifiedText => Source.ModifiedAt is { } timestamp
        ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToLocalTime().ToString("yyyy-MM-dd\nHH:mm", CultureInfo.CurrentCulture) : "—";
    public string UpdateText => Source.UpdateState switch
    {
        "updated" or "success" => "成功", "unchanged" or "current" => "无变化",
        "checking" or "updating" => "更新中", "failed" or "error" => "失败",
        "" or "idle" or "none" or "never" => "—", _ => Source.UpdateState
    };
    public ManagementStatusTone UpdateTone => Source.UpdateState switch
    {
        "updated" or "success" => ManagementStatusTone.Success,
        "failed" or "error" => ManagementStatusTone.Failure,
        _ => ManagementStatusTone.Neutral
    };
    public string? ErrorText => Source.Error;
    public string? UpdateErrorText => Source.UpdateError;
    public string ErrorHelpText => ErrorText ?? "";
    public string UpdateErrorHelpText => UpdateErrorText ?? "";
    public string StateAccessibleName => $"猫源状态 {Name}";
    public string EnabledAccessibleName => $"启用猫源 {Name}";
    public string UpdateAccessibleName => $"猫源更新状态 {Name}";
    public string EditAccessibleName => $"编辑猫源 {Name}";
    public string UpdateActionAccessibleName => $"更新猫源 {Name}";
    public string RestartAccessibleName => $"重启猫源 {Name}";
    public string DeleteAccessibleName => $"删除猫源 {Name}";
    public void Update(FilmCatSource source) { Source = source; Changed(""); }
}

public sealed class FilmSettingsVideoRow(FilmVideoSource source, bool canDetect) : FilmSettingsSourceRow
{
    public FilmVideoSource Source { get; private set; } = source;
    public string Name => Source.Name;
    public bool Enabled => Source.Options.Enabled;
    public bool HomeVisible => Source.Options.HomeVisible;
    public bool SearchEnabled => Source.Options.SearchEnabled;
    public bool AggregateImages => Source.Options.AggregateImages;
    public bool CanDetect { get; private set; } = canDetect;
    public bool Detecting { get; private set; }
    public string StateText => Detecting ? "检测中" : Source.Detection?.Result switch
    {
        "valid" => "通过", "search_error" => "搜索异常", "category_error" => "分类异常",
        "invalid" => "失效", "skipped" => "跳过", null => "未检测", var state => state
    };
    public ManagementStatusTone StateTone => Detecting ? ManagementStatusTone.Neutral : Source.Detection?.Result switch
    {
        "valid" => ManagementStatusTone.Success,
        "invalid" or "search_error" or "category_error" => ManagementStatusTone.Failure,
        _ => ManagementStatusTone.Neutral
    };
    public bool HasDiagnostics => !Detecting && !string.IsNullOrWhiteSpace(Source.Detection?.Error);
    public string Diagnostics => HasDiagnostics ? Source.Detection!.Error! : "";
    public string StateAccessibleName => $"检测状态 {Name}";
    public string EnabledAccessibleName => $"启用视频源 {Name}";
    public string HomeAccessibleName => $"首页显示 {Name}";
    public string SearchAccessibleName => $"搜索启用 {Name}";
    public string DetectAccessibleName => $"检测视频源 {Name}";
    public string DeleteAccessibleName => $"删除视频源 {Name}";
    public string MoreAccessibleName => $"视频源更多操作 {Name}";
    public void SetDetecting(bool detecting) { Detecting = detecting; Changed(""); }
    public void Update(FilmVideoSource source, bool canDetect)
    {
        Source = source; CanDetect = canDetect; Changed("");
    }
}
