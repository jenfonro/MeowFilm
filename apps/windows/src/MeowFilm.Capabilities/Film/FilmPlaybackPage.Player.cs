using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed record FilmPlayerOption(string Value, string Label, bool IsSelected);

public sealed partial class FilmPlaybackPage
{
    private static readonly string[] PlayerProviders = ["yidong", "tianyi", "quark", "uc", "baidu"];
    private static readonly string[] PlayerQualities = ["4k_hdr", "4k_fps", "4k", "1080p", "720p"];
    private bool IsMetadataPlayback => ActiveTarget.Kind is "movie" or "tv" &&
        ActiveTarget.MetadataProvider is "tmdb" or "douban";
    private string PlayerStage => FilmJson.Text(snapshot?.Raw ?? default, "stage");
    private JsonElement PlayerCandidate => FilmJson.Member(snapshot?.Current ?? default, "candidate") is
        { ValueKind: JsonValueKind.Object } current ? current : FilmJson.Member(snapshot?.Attempt ?? default, "candidate");
    private string PlayerPanValue => PlayerProviderFamily(FilmJson.Text(PlayerCandidate, "provider"));
    private string PlayerQualityValue => FilmJson.Text(PlayerCandidate, "quality");

    public string PlayerPanLabel => PlayerPanValue.Length > 0 ? PlayerPanName(PlayerPanValue) :
        FilmJson.Text(PlayerCandidate, "lineLabel") is { Length: > 0 } label ? label : "未知";
    public string PlayerQualityLabel => PlayerQualityName(PlayerQualityValue);
    public IReadOnlyList<FilmPlayerOption> PlayerPanOptions
    {
        get
        {
            IEnumerable<string> providers = IsMetadataPlayback ? PlayerProviders :
                FilmJson.Array(snapshot?.Browsing ?? default, "lines")
                    .Select(line => PlayerProviderFamily(FilmJson.Text(line, "provider")))
                    .Where(provider => provider.Length > 0).Distinct(StringComparer.Ordinal);
            return providers
                .Select(provider => new FilmPlayerOption(provider, PlayerPanName(provider), provider == PlayerPanValue)).ToArray();
        }
    }
    public IReadOnlyList<FilmPlayerOption> PlayerQualityOptions
    {
        get
        {
            IEnumerable<string> qualities = IsMetadataPlayback ? PlayerQualities :
                PlayerQualities.Where(quality => quality == PlayerQualityValue);
            return qualities
                .Select(quality => new FilmPlayerOption(quality, PlayerQualityName(quality), quality == PlayerQualityValue))
                .ToArray();
        }
    }
    public bool ShowPlayerSwitchActions => IsMetadataPlayback && HasCurrent;
    public FilmPlayerOption? SelectedPanOption => PlayerPanOptions.FirstOrDefault(option => option.IsSelected);
    public bool PlayerBusy => !ConnectionFailed && (snapshot is null ||
        PlayerStage is "loading" or "searching" or "history" or "manual" or "cached" or "resolving" or "pending");
    public bool PlayerBuffering => HasOutput && IsPlaying && session?.IsBuffering == true;
    public string MetadataAssociationErrorMessage => MetadataAssociationFailed ? "关联资料加载失败" : "";
    public bool PlayerStatusVisible => PlayerStatusText.Length > 0 &&
        (PlayerBusy || !HasOutput || ConnectionFailed || PlayerStage is "error" or "unavailable");
    public string PlayerStatusText => ConnectionFailed ? StatusText :
        HasOutput && PlayerBusy ? "正在搜索片源..." : PlayerStage switch
        {
            "loading" => "加载中...",
            "searching" or "history" or "manual" or "cached" => "正在搜索片源...",
            "resolving" => "正在获取播放地址...",
            "pending" => "正在准备媒体...",
            "unavailable" => FilmPlaybackError.Message(FilmJson.Text(snapshot?.Raw ?? default, "error"), "暂无可用片源"),
            "idle" => "待播放",
            _ => snapshot is null ? "加载中..." : StatusText
        };
    // Discrete search, address and native preparation phases, not download progress.
    public double PlayerStageProgress => ConnectionFailed ? 0 : PlayerStage switch
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
        var constraint = FilmJson.Member(snapshot?.Raw ?? default, "constraint");
        return PlayerBusy && FilmJson.Text(constraint, "action") == action &&
            (action == "switch" || action == "pan" &&
                PlayerProviderFamily(FilmJson.Text(constraint, "provider")) == PlayerProviderFamily(value) ||
             action == "quality" && FilmJson.Text(constraint, "quality") == value);
    }

    private static string PlayerProviderFamily(string provider) => provider switch
    {
        "quark_tv" => "quark", "uc_tv" => "uc",
        "yidong" or "tianyi" or "quark" or "uc" or "baidu" => provider, _ => ""
    };
    private static string PlayerPanName(string provider) => provider switch
    {
        "yidong" => "移动云盘", "tianyi" => "天翼云盘", "quark" or "quark_tv" => "夸克",
        "uc" or "uc_tv" => "UC", "baidu" => "百度网盘", "" => "未知", _ => provider
    };
    private static string PlayerQualityName(string quality) => quality switch
    {
        "4k_hdr" => "4K·HDR", "4k_fps" => "4K·60帧", "4k" => "4K", "1080p" => "1080P", "720p" => "720P",
        "" => "未知", _ => quality
    };
}
