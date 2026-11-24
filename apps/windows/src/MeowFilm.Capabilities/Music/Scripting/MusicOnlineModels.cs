using System.ComponentModel;
using System.Runtime.CompilerServices;
using MeowFilm.Capabilities.Scripting;

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
    private bool selected, checking, canToggle = true;

    internal MusicSourceRow(string key, MusicSourceKind kind, string name, string version, bool enabled,
        string state, IReadOnlyList<MusicSourcePlatformState> platforms, string summary, string? installationId = null,
        string? platform = null, string? host = null, bool rollbackAvailable = false,
        IReadOnlyDictionary<string, MusicScriptSiteCapability>? sites = null,
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
    public IReadOnlyDictionary<string, MusicScriptSiteCapability>? Sites { get; }
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
    public bool CanToggle
    {
        get => canToggle;
        internal set { if (canToggle == value) return; canToggle = value; Changed(); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));

    internal bool MatchesData(MusicSourceRow other) =>
        Key == other.Key &&
        Kind == other.Kind &&
        Name == other.Name &&
        Version == other.Version &&
        Enabled == other.Enabled &&
        State == other.State &&
        Summary == other.Summary &&
        InstallationId == other.InstallationId &&
        Platform == other.Platform &&
        Host == other.Host &&
        RollbackAvailable == other.RollbackAvailable &&
        ExecutionHash == other.ExecutionHash &&
        Platforms.SequenceEqual(other.Platforms) &&
        SitesEqual(Sites, other.Sites);

    private static bool SitesEqual(
        IReadOnlyDictionary<string, MusicScriptSiteCapability>? a,
        IReadOnlyDictionary<string, MusicScriptSiteCapability>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Count != b.Count) return false;
        foreach (var (k, v) in a)
            if (!b.TryGetValue(k, out var ov) || !v.ContentEquals(ov)) return false;
        return true;
    }

    internal static readonly string[] AllPlatforms = ["wy", "tx", "kw", "kg", "mg"];
    internal static string PlatformName(string platform) => platform switch
    {
        "wy" => "网易云", "tx" => "QQ", "kg" => "酷狗", "kw" => "酷我", "mg" => "咪咕",
        "local" => "本地", _ => platform
    };
    internal static string OfficialName(string platform) => platform is "wy" or "tx" or "kg"
        ? MusicPlatformInfo.DisplayName(platform) : platform;
    /// <summary>Platform cells from a check fact or none.</summary>
    internal static IReadOnlyList<MusicSourcePlatformState> Cells(MusicScriptCheckInfo? check, Func<string, bool> declared) =>
        AllPlatforms.Select(platform =>
        {
            if (check is not null && check.Sites.TryGetValue(platform, out var site))
                return new MusicSourcePlatformState(platform, site.State switch
                {
                    "ok" => "ok", "failed" => "failed", _ => "unsupported"
                });
            return new MusicSourcePlatformState(platform, declared(platform) ? "idle" : "unsupported");
        }).ToArray();
    internal static MusicSourceRow FromScript(MusicScriptSourceItem item)
    {
        var kind = item.Origin.Kind == "network" ? MusicSourceKind.Network : MusicSourceKind.Local;
        bool Declared(string platform) => item.Sites is not { } known ||
            known.TryGetValue(platform, out var site) && site.SupportsAction("musicUrl");
        return new($"script:{item.InstallationId}", kind, item.Name, item.Version,
            item.Enabled, item.State, Cells(item.Check, Declared),
            item.Check?.Summary is { Length: > 0 } summary ? summary : "unchecked",
            installationId: item.InstallationId,
            host: kind == MusicSourceKind.Network ? item.Origin.Host ?? "" : null,
            rollbackAvailable: item.RollbackAvailable, sites: item.Sites,
            executionHash: item.ExecutionHash);
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
    IReadOnlyList<MusicLyricWord>? Words = null);
public sealed record MusicLyricWord(string Text, double Time, double Duration);
public sealed record MusicLyricSourceRow(string SourceKey, string Label);
