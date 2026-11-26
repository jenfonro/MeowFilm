namespace MeowFilm.Capabilities;

public sealed record FilmPlayerOption(string Value, string Label, bool IsSelected);

public sealed partial class FilmPlaybackPage
{
    private static readonly string[] PlayerProviders = ["yidong", "tianyi", "quark", "uc", "baidu"];
    private static readonly string[] PlayerQualities = ["4k_hdr", "4k_fps", "4k", "1080p", "720p"];

    private string PlayerStage => HasExecution ? session?.SnapshotFor(this)?.Stage ?? "idle" : "idle";

    private string PlayerPanValue => PlayerProviderFamily(
        snapshot?.ActivePan is { Length: > 0 } pan ? pan :
        snapshot?.Current?.Provider ?? snapshot?.Attempt?.Provider ?? "");

    private string PlayerQualityValue =>
        snapshot?.ActiveQuality is { Length: > 0 } q ? q :
        snapshot?.Current?.Quality is { Length: > 0 } cq ? cq :
        snapshot?.Attempt?.Quality ?? "";

    public string PlayerPanLabel => PlayerPanValue.Length > 0
        ? PlayerPanName(PlayerPanValue)
        : snapshot?.ActiveSourceLabel is { Length: > 0 } sl ? sl : "未知";

    public string PlayerQualityLabel => PlayerQualityName(PlayerQualityValue);

    public IReadOnlyList<FilmPlayerOption> PlayerPanOptions
    {
        get
        {
            if (NoSources || !CanPlay) return [];
            if (snapshot?.AvailablePans is { Count: > 0 } available)
                return available;
            return PlayerProviders
                .Select(provider => new FilmPlayerOption(provider, PlayerPanName(provider), provider == PlayerPanValue))
                .ToArray();
        }
    }

    public IReadOnlyList<FilmPlayerOption> PlayerQualityOptions
    {
        get
        {
            if (NoSources || !CanPlay) return [];
            if (snapshot?.AvailableQualities is { Count: > 0 } available)
                return available;
            return PlayerQualities
                .Select(quality => new FilmPlayerOption(quality, PlayerQualityName(quality), quality == PlayerQualityValue))
                .ToArray();
        }
    }

    internal int PlayerPanOptionCount => PlayerPanOptions.Count;
    internal int PlayerQualityOptionCount => PlayerQualityOptions.Count;
    public bool ShowPlayerSwitchActions => !NoSources && snapshot?.CanSwitchSource == true;

    public bool PlayerBusy => !ConnectionFailed && !ExecutionFailed && !NoSources &&
        (snapshot is null || HasExecution &&
            PlayerStage is "loading" or "searching" or "history" or "manual" or "cached" or "resolving" or "pending");

    public bool PlayerBuffering => HasOutput && session?.IsBuffering == true;

    public bool PlayerStatusVisible => PlayerStatusText.Length > 0 &&
        (NoSources || PlayerBusy || !HasOutput || ConnectionFailed || ExecutionFailed || PlayerStage is "error" or "unavailable");

    public string PlayerStatusText =>
        ConnectionFailed || ExecutionFailed ? StatusText :
        snapshot is null ? "加载中..." :
        NoSources ? "暂无可用视频源" :
        PlayerStage switch
        {
            "loading" => "加载中...",
            "searching" or "history" or "manual" or "cached" => "正在搜索片源...",
            "resolving" => "正在获取播放地址...",
            "pending" => "正在准备媒体...",
            "unavailable" => FilmPlaybackError.Message(CurrentSnapshotError, "未找到匹配片源"),
            "idle" => "待播放",
            _ => StatusText
        };

    // Discrete search, address and native preparation phases, not download progress.
    public double PlayerStageProgress => ConnectionFailed || ExecutionFailed || NoSources ? 0 : PlayerStage switch
    {
        "cached" or "history" or "manual" or "searching" => 1d / 6,
        "resolving" => 1d / 2,
        "prepared" => 2d / 3,
        "pending" => 5d / 6,
        "playing" or "ended" => 1,
        _ => 0
    };

    internal bool PlayerActionPending(string action, string value = "")
    {
        if (!PlayerBusy || snapshot is null || snapshot.ConstraintAction != action) return false;
        return action == "switch" ||
            (action == "pan" && PlayerProviderFamily(snapshot.ConstraintProvider) == PlayerProviderFamily(value)) ||
            (action == "quality" && snapshot.ConstraintQuality == value);
    }

    private static string PlayerProviderFamily(string provider) => provider switch
    {
        "yidong" or "tianyi" or "quark" or "uc" or "baidu" => provider,
        _ => ""
    };

    private static string PlayerPanName(string provider) => provider switch
    {
        "yidong" => "移动云盘",
        "tianyi" => "天翼云盘",
        "quark" => "夸克",
        "uc" => "UC",
        "baidu" => "百度网盘",
        "" => "未知",
        _ => provider
    };

    private static string PlayerQualityName(string quality) => quality switch
    {
        "8k" => "8K",
        "4k_hdr" => "4K·HDR",
        "4k_fps" => "4K·60帧",
        "4k" => "4K",
        "1080p" => "1080P",
        "720p" => "720P",
        "" => "未知",
        _ => quality
    };
}
