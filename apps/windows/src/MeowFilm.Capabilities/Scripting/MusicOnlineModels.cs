using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MeowFilm.Capabilities;

public enum MusicSourceKind { BuiltIn, Local, Network }

/// <summary>One platform cell of a source row: ok, failed, idle (not checked) or unsupported.</summary>
public sealed record MusicSourcePlatformState(string Platform, string State)
{
    public string Name => MusicSourceRow.PlatformName(Platform);
    public string StateText => State switch
    {
        "ok" => "可用", "failed" => "不可用", "unsupported" => "不支持", _ => "未检测"
    };
}

/// <summary>
/// A management row: a built-in official provider or a script installation. Health is a
/// derived check fact, independent of enablement. Rows are replaced on refresh; selection
/// and in-flight checks are owned by the capability and reapplied by key.
/// </summary>
public sealed class MusicSourceRow : INotifyPropertyChanged
{
    private bool selected, checking;

    internal MusicSourceRow(string key, MusicSourceKind kind, string name, string version, bool enabled,
        string state, IReadOnlyList<MusicSourcePlatformState> platforms, string summary, string? installationId = null,
        string? platform = null, string? host = null, bool rollbackAvailable = false, JsonElement? sites = null,
        string executionHash = "")
    {
        Key = key; Kind = kind; Name = name; Version = version; Enabled = enabled; State = state;
        Platforms = platforms; Summary = summary; InstallationId = installationId; Platform = platform;
        Host = host; RollbackAvailable = rollbackAvailable; Sites = sites; ExecutionHash = executionHash;
    }
    public string Key { get; }
    public MusicSourceKind Kind { get; }
    public string Name { get; }
    public string Version { get; }
    public bool Enabled { get; }
    /// <summary>Script runtime state: ready, idle, disabled or failed. Built-in rows are ready.</summary>
    public string State { get; }
    public IReadOnlyList<MusicSourcePlatformState> Platforms { get; }
    /// <summary>Derived from the last check: unchecked, normal, partial or abnormal.</summary>
    public string Summary { get; }
    public string? InstallationId { get; }
    /// <summary>The official platform of a built-in row.</summary>
    public string? Platform { get; }
    public string? Host { get; }
    public bool RollbackAvailable { get; }
    /// <summary>Declared music sites of a script installation; null when never declared.</summary>
    public JsonElement? Sites { get; }
    public string ExecutionHash { get; }
    public bool IsBuiltIn => Kind == MusicSourceKind.BuiltIn;
    public bool IsScript => !IsBuiltIn;
    public bool HasVersion => Version.Length > 0;
    public string TypeText => Kind switch { MusicSourceKind.BuiltIn => "内置", MusicSourceKind.Local => "本地", _ => "网络" };
    public string Label => IsBuiltIn ? Name : $"{Name} · {InstallationId![^8..]}";
    public string StatusKind => checking ? "checking" : State == "failed" ? "abnormal" : Summary;
    public string StatusText => StatusKind switch
    {
        "checking" => "检测中", "normal" => "正常", "partial" => "部分可用", "abnormal" => "异常", _ => "未检测"
    };
    public bool Selected
    {
        get => selected;
        set { if (selected == value) return; selected = value; Changed(); }
    }
    public bool Checking
    {
        get => checking;
        internal set { if (checking == value) return; checking = value; Changed(); Changed(nameof(StatusKind)); Changed(nameof(StatusText)); }
    }
    /// <summary>The platforms this row may resolve, as far as its declaration is known.</summary>
    internal bool Declares(string platform) => IsBuiltIn ? Platform == platform :
        Sites is not { ValueKind: JsonValueKind.Object } sites ||
        sites.TryGetProperty(platform, out var site) &&
        site.GetProperty("actions").EnumerateArray().Any(action => action.GetString() == "musicUrl");
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));

    internal static readonly string[] AllPlatforms = ["wy", "tx", "kw", "kg", "mg"];
    internal static string PlatformName(string platform) => platform switch
    {
        "wy" => "网易云", "tx" => "QQ", "kg" => "酷狗", "kw" => "酷我", "mg" => "咪咕",
        "local" => "本地", _ => platform
    };
    internal static string OfficialName(string platform) => platform switch
    {
        "wy" => "网易云音乐", "tx" => "QQ 音乐", "kg" => "酷狗音乐", _ => platform
    };
    /// <summary>Platform cells from a check fact (`sites` keyed by platform) or none.</summary>
    internal static IReadOnlyList<MusicSourcePlatformState> Cells(JsonElement? check, Func<string, bool> declared) =>
        AllPlatforms.Select(platform =>
        {
            if (check is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(platform, out var site))
                return new MusicSourcePlatformState(platform, MusicDto.Text(site, "state") switch
                {
                    "ok" => "ok", "failed" => "failed", _ => "unsupported"
                });
            return new MusicSourcePlatformState(platform, declared(platform) ? "idle" : "unsupported");
        }).ToArray();
    internal static MusicSourceRow Read(JsonElement value)
    {
        var id = MusicDto.Text(value, "installationId");
        MusicDto.Require(id.Length >= 8 && id.Length <= 128);
        var origin = value.GetProperty("origin");
        var kind = MusicDto.Text(origin, "kind") == "network" ? MusicSourceKind.Network : MusicSourceKind.Local;
        JsonElement? sites = value.GetProperty("sites") is { ValueKind: JsonValueKind.Object } declared ? declared.Clone() : null;
        var check = value.GetProperty("check");
        JsonElement? checkSites = check.ValueKind == JsonValueKind.Object ? check.GetProperty("sites").Clone() : null;
        bool Declared(string platform) => sites is not { } known || known.TryGetProperty(platform, out var site) &&
            site.GetProperty("actions").EnumerateArray().Any(action => action.GetString() == "musicUrl");
        return new($"script:{id}", kind, MusicDto.Text(value, "name"), MusicDto.Text(value, "version"),
            value.GetProperty("enabled").GetBoolean(), MusicDto.Text(value, "state"),
            Cells(checkSites, Declared), check.ValueKind == JsonValueKind.Object ? MusicDto.Text(check, "summary") : "unchecked",
            installationId: id, host: kind == MusicSourceKind.Network ? MusicDto.Text(origin, "host") : null,
            rollbackAvailable: value.GetProperty("rollbackAvailable").GetBoolean(), sites: sites,
            executionHash: MusicDto.Text(value, "executionHash"));
    }
    internal static MusicSourceRow BuiltIn(string platform, bool enabled, MusicSourcePlatformState? checkedState)
    {
        var cells = AllPlatforms.Select(item => item == platform
            ? checkedState ?? new MusicSourcePlatformState(item, "idle")
            : new MusicSourcePlatformState(item, "unsupported")).ToArray();
        string summary = checkedState?.State switch { "ok" => "normal", "failed" => "abnormal", _ => "unchecked" };
        return new($"official:{platform}", MusicSourceKind.BuiltIn, OfficialName(platform), "", enabled, "ready",
            cells, summary, platform: platform);
    }
}

public sealed record MusicLyricRow(double Time, string Text, string Translation, string Romanization = "",
    IReadOnlyList<MusicLyricWord>? Words = null, string Timing = "");
public sealed record MusicLyricWord(string Text, double Time, double Duration);
public sealed record MusicLyricSourceRow(string SourceKey, string Label);
public sealed record MusicRecordingRow(string SongId, string SourceKey, string Platform, string Label);
