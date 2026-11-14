using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeowFilm.Core.Runtime;
using MeowFilm.Media;

namespace MeowFilm.Capabilities;

public sealed class FilmPlaybackException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

internal static class FilmPlaybackError
{
    internal static string Message(Exception error, string failure) => Message(error switch
    {
        FilmPlaybackException film => film.Code,
        BridgeException bridge => bridge.Code,
        PlaybackException media => media.Code,
        _ => ""
    }, failure);

    internal static string Message(string code, string failure) => code switch
    {
        "TIMEOUT" or "MEDIA_UPSTREAM_TIMEOUT" or "MEDIA_PREFETCH_TIMEOUT" or PlaybackError.HttpDeadline => "请求超时，请重试",
        "CLOSED" => "服务已关闭，请重连",
        "DISCONNECTED" => "连接已断开，请重连",
        "STALE_SESSION" => "会话已失效，请重连",
        "INVALID_RESPONSE" => "响应无效，请重试",
        "UPSTREAM_FORMAT" => "上游数据格式错误",
        "NO_MATCH" => "未找到匹配片源",
        "PERMISSION_DENIED" => "没有访问权限",
        "AUTH_REQUIRED" => "请先登录账号",
        "AUTH_FAILED" => "账号认证失败，请重新登录",
        PlaybackError.HttpUnauthorized => "媒体认证失败",
        "LOGIN_EXPIRED" => "账号登录已失效，请重新登录",
        "NOT_CONFIGURED" => "尚未完成配置",
        "CHALLENGE_REQUIRED" => "需要完成账号验证",
        "WEB_INTERACTION_REQUIRED" => "需要网页交互",
        "STALE_ACCOUNT" => "账号已变更，请重试",
        "STALE_SOURCE" => "来源已变更，请重新选择",
        "SOURCE_DISABLED" => "来源已停用，请切换来源",
        "SOURCE_NOT_FOUND" => "来源不存在，请切换来源",
        "STALE_METADATA" => "影片资料已变更，请重试",
        "NO_EPISODE_MAPPING" => "未找到对应集数",
        "NETWORK_ERROR" or PlaybackError.HttpNetworkFailed => "网络连接失败，请重试",
        PlaybackError.HttpTlsFailed => "安全连接失败，请重试",
        "UPSTREAM_HTTP" or "UPSTREAM_ERROR" or "MEDIA_UPSTREAM_ERROR" => "上游服务请求失败",
        "NOT_FOUND" => "请求的内容不存在",
        "MEDIA_NOT_FOUND" or PlaybackError.HttpNotFound => "媒体资源不存在",
        "INVALID_REQUEST" => "请求无效",
        "INVALID_DETAIL" or "INVALID_RESULT" => "来源数据无效",
        "INVALID_PREFERENCES" or "INVALID_RULES" => "播放设置无效",
        "STORAGE_ERROR" => "本地存储失败",
        "CORRUPT_STORAGE" => "本地数据已损坏",
        "COMMIT_UNKNOWN" => "保存结果未确认，请核对",
        "CACHE_LOAD_FAILED" => "缓存读取失败",
        "STORAGE_CHANGED" => "本地数据已变更，请重试",
        "LIBRARY_UPDATE_FAILED" => "媒体库更新失败",
        "UNSUPPORTED_OPERATION" or "CAPABILITY_UNAVAILABLE" or "METHOD_NOT_FOUND" => "当前操作不可用",
        "PAN_FAILURE" => "网盘请求失败",
        "SOURCE_FAILURE" => "来源请求失败",
        "CAT_FAILURE" or "SCRIPT_FAILURE" => "脚本执行失败",
        "SCRIPT_PROTOCOL_ERROR" => "脚本响应格式错误",
        "CANCELLED" or "MEDIA_CANCELLED" => "操作已取消",
        "MEDIA_BUSY" => "媒体资源正忙，请重试",
        "MEDIA_UNAVAILABLE" => "媒体暂不可用",
        "MEDIA_CLOSED" or PlaybackError.Disposed => "媒体已关闭，请重试",
        "MEDIA_CLOSE_FAILED" or PlaybackError.ReleaseFailed => "媒体关闭失败",
        "MEDIA_EMPTY_RESPONSE" => "媒体响应为空",
        "MEDIA_INVALID_DESCRIPTOR" or PlaybackError.InvalidRequest => "媒体请求无效",
        "MEDIA_LISTEN_FAILED" => "媒体服务启动失败",
        "MEDIA_MANIFEST_INVALID" or PlaybackError.AdaptiveInvalid => "媒体清单格式错误",
        "MEDIA_MANIFEST_UNSUPPORTED" or PlaybackError.AdaptiveUnsupported => "媒体清单不受支持",
        "MEDIA_METHOD_NOT_ALLOWED" => "媒体请求方法不受支持",
        "MEDIA_MANIFEST_LIMIT" or "MEDIA_RESOURCE_LIMIT" or PlaybackError.AdaptiveBudget => "媒体资源超出处理预算",
        "MEDIA_DRM_UNSUPPORTED" or PlaybackError.AdaptiveDrmUnsupported => "当前媒体加密方式不受支持",
        "MEDIA_PREFETCH_DISABLED" => "媒体预缓存未启用",
        "MEDIA_PREFETCH_HTTP_ERROR" => "媒体预缓存请求失败",
        "MEDIA_PREFETCH_LIMIT" => "媒体预缓存超出处理预算",
        "MEDIA_PREFETCH_UNSUPPORTED" => "当前媒体不支持预缓存",
        "MEDIA_RANGE_UNSUPPORTED" => "媒体不支持所需范围读取",
        "MEDIA_REDIRECT_ERROR" or PlaybackError.HttpRedirectFailed => "媒体重定向失败",
        "MEDIA_RESOURCE_CHANGED" or PlaybackError.HttpRepresentationChanged => "媒体资源已变更，请重新取址",
        PlaybackError.HttpExpired => "媒体地址已过期，请重新取址",
        PlaybackError.HttpThrottled => "媒体请求过于频繁，请稍后重试",
        PlaybackError.HttpTruncated or PlaybackError.ReadTruncated => "媒体响应不完整",
        PlaybackError.ResourceRevoked or PlaybackError.ReadRevoked => "媒体授权已失效",
        PlaybackError.RateUnsupported => "当前媒体不支持此倍速",
        _ => failure
    };
}

/// <summary>Only an actual native occurrence, never selected UI metadata or a prepared URL.</summary>
public sealed record FilmPlaybackMediaState(string? Identity, string Title, bool HasMedia, bool IsPlaying,
    double Position, double Duration);

/// <summary>Exact search/detail identity. Display titles never supply an identity or episode coordinate.</summary>
public sealed record FilmPlaybackTarget(string Id, string Kind, string Title,
    string? MetadataProvider = null, string? MetadataId = null, int? Year = null,
    int? Season = null, int? Episode = null, int? GlobalEpisode = null, JsonElement? Source = null,
    JsonElement? Members = null)
{
    internal object Wire() => new
    {
        id = Id, kind = Kind, title = Title, metadataProvider = MetadataProvider, metadataId = MetadataId,
        year = Year, season = Season, episode = Episode, globalEpisode = GlobalEpisode, source = Source, members = Members
    };

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Title) ||
            Kind is not ("movie" or "tv" or "source" or "group") ||
            MetadataProvider is not (null or "tmdb" or "douban") ||
            MetadataProvider is not null && (string.IsNullOrEmpty(MetadataId) || MetadataId.Any(character => !char.IsAsciiDigit(character))) ||
            Season is < 0 || Episode is < 0 || GlobalEpisode is < 1 ||
            (Season == 0 || Episode == 0) && GlobalEpisode is not null ||
            Kind == "movie" && (Season is not null || Episode is not null || GlobalEpisode is not null) ||
            Kind == "source" && (Source is not { ValueKind: JsonValueKind.Object } source ||
                FilmJson.Text(source, "key").Length == 0 || !FilmJson.IsIdentity(FilmJson.Member(source, "detailId"))) ||
            Kind == "group" && (MetadataProvider is not null || Members is not { ValueKind: JsonValueKind.Array } members ||
                members.EnumerateArray().Any(member =>
                    FilmJson.Text(member, "sourceKey").Length == 0 || !FilmJson.IsIdentity(FilmJson.Member(member, "detailId"))) ||
                Source is { } selected && (selected.ValueKind != JsonValueKind.Object ||
                    FilmJson.Text(selected, "key").Length == 0 || !FilmJson.IsIdentity(FilmJson.Member(selected, "detailId")))))
            throw new FilmPlaybackException("INVALID_REQUEST");
        // Public snapshots may contain Node's unresolved Douban wait group.
        // Initial waiting reentry omits resume; a revoked Douban resume is sent
        // intact so Node rejects it instead of starting a new seed lookup.
    }
}

/// <summary>Native handoff preserves the original query, loaded results/pages and raw metadata, without another cache.</summary>
public sealed record FilmPlaybackRequest(FilmPlaybackTarget Target,
    IReadOnlyList<JsonElement>? Sources = null, string? Query = null,
    JsonElement? Results = null, JsonElement? Search = null, JsonElement? Metadata = null,
    JsonElement? CacheEntries = null, JsonElement? ResultHints = null, JsonElement? SearchReference = null,
    string? EntrySurface = null, FilmPlaybackTarget? ResumeTarget = null)
{
    // Only the producer of the original source results/cache may bind this proof.
    // Navigation persistence intentionally loses it: process-local authority is not JSON.
    [JsonIgnore]
    public RuntimeConnection? DataOwner { get; init; }

    internal FilmPlaybackRequest Own() => this with
    {
        Target = Target with { Source = Target.Source?.Clone(), Members = Target.Members?.Clone() },
        ResumeTarget = ResumeTarget is { } resume
            ? resume with { Source = resume.Source?.Clone(), Members = resume.Members?.Clone() } : null,
        Sources = Sources?.Select(value => value.Clone()).ToArray(),
        Results = Results?.Clone(), Search = Search?.Clone(), Metadata = Metadata?.Clone(), CacheEntries = CacheEntries?.Clone(),
        ResultHints = ResultHints?.Clone(), SearchReference = SearchReference?.Clone()
    };
}

internal static class FilmJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static JsonElement Encode(object value) => JsonSerializer.SerializeToElement(value, Options);
    internal static JsonElement Member(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var member) ? member : default;
    internal static string Text(JsonElement value, string name) => String(Member(value, name));
    internal static string String(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! :
        value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "";
    internal static string RequiredText(JsonElement value, string name)
    {
        var member = Member(value, name);
        if (member.ValueKind != JsonValueKind.String || member.GetString()!.Length == 0)
            throw new FilmPlaybackException("INVALID_RESPONSE");
        string result = member.GetString()!;
        return result;
    }
    internal static bool IsIdentity(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString()!.Length > 0
        : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) &&
            number is >= -9007199254740991 and <= 9007199254740991;
    internal static JsonElement Identity(JsonElement value, string name, bool required = false)
    {
        var member = Member(value, name);
        if (!required && member.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return default;
        if (!IsIdentity(member)) throw new FilmPlaybackException("INVALID_RESPONSE");
        return member.Clone();
    }
    internal static JsonElement[] Array(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
    internal static JsonElement[] Array(JsonElement value, string name) => Array(Member(value, name));
    internal static bool Bool(JsonElement value, string name) => Member(value, name).ValueKind == JsonValueKind.True;
    internal static int? Integer(JsonElement value, string name) =>
        Member(value, name) is { ValueKind: JsonValueKind.Number } member && member.TryGetInt32(out int number) ? number : null;
    internal static double Number(JsonElement value, string name)
    {
        var member = Member(value, name);
        if (member.ValueKind != JsonValueKind.Number || !member.TryGetDouble(out double number) ||
            !double.IsFinite(number) || number < 0) throw new FilmPlaybackException("INVALID_RESPONSE");
        return number;
    }
    internal static string Error(JsonElement value, string name)
    {
        string code = Text(value, name);
        return code.Length == 0 ? "" : FilmPlaybackError.Message(code, "加载失败");
    }
    internal static string Names(JsonElement value) => string.Join(" / ", Array(value)
        .Select(item => item.ValueKind == JsonValueKind.String ? String(item) : Text(item, "name"))
        .Where(name => name.Length > 0));
    internal static string Time(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(
        seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

    internal static FilmPlaybackTarget Target(JsonElement value)
    {
        var source = Member(value, "source");
        return new(RequiredText(value, "id"), RequiredText(value, "kind"), RequiredText(value, "title"),
            Text(value, "metadataProvider") is { Length: > 0 } provider ? provider : null,
            Text(value, "metadataId") is { Length: > 0 } id ? id : null,
            Integer(value, "year"), Integer(value, "season"), Integer(value, "episode"), Integer(value, "globalEpisode"),
            source.ValueKind == JsonValueKind.Object ? source : null,
            Member(value, "members").ValueKind == JsonValueKind.Array ? Member(value, "members") : null);
    }
}

internal sealed class FilmPlaybackSnapshot
{
    internal FilmPlaybackSnapshot(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !Guid.TryParse(FilmJson.RequiredText(value, "sessionId"), out var id) ||
            FilmJson.Member(value, "sequence").ValueKind != JsonValueKind.Number ||
            !FilmJson.Member(value, "sequence").TryGetInt64(out long sequence) || sequence < 0 ||
            FilmJson.Member(value, "entryTarget").ValueKind != JsonValueKind.Object ||
            FilmJson.Member(value, "sources").ValueKind != JsonValueKind.Array ||
            FilmJson.Member(value, "episodes").ValueKind != JsonValueKind.Array ||
            FilmJson.Member(value, "candidates").ValueKind != JsonValueKind.Array ||
            FilmJson.Member(value, "browsing").ValueKind != JsonValueKind.Object)
            throw new FilmPlaybackException("INVALID_RESPONSE");
        Raw = value.Clone();
        SessionId = id;
        Sequence = sequence;
        Target = FilmJson.Target(FilmJson.Member(Raw, "target"));
        Target.Validate();
        EntryTarget = FilmJson.Target(FilmJson.Member(Raw, "entryTarget"));
        EntryTarget.Validate();
        string metadataSourceId = FilmJson.Text(Raw, "metadataSourceId");
        SelectedMetadataSource = metadataSourceId.Length > 0
            ? FilmJson.Array(Raw, "metadataSources").FirstOrDefault(row => FilmJson.Text(row, "id") == metadataSourceId) : default;
        string contentKind = FilmJson.Text(SelectedMetadataSource, "kind");
        ContentKind = contentKind is "movie" or "tv" ? contentKind : Target.Kind;
        MetadataCache = FilmMetadataCache.Read(FilmJson.Member(Raw, "metadataCache"));
        MetadataSupplement = FilmMetadataSupplement.Read(FilmJson.Member(Raw, "metadataSupplement"));
    }
    internal JsonElement Raw { get; }
    internal Guid SessionId { get; }
    internal long Sequence { get; }
    internal FilmPlaybackTarget Target { get; }
    internal FilmPlaybackTarget EntryTarget { get; }
    internal JsonElement SelectedMetadataSource { get; }
    internal string ContentKind { get; }
    internal string EntrySurface => FilmJson.Text(Raw, "entrySurface");
    internal FilmMetadataCache? MetadataCache { get; }
    internal FilmMetadataSupplement? MetadataSupplement { get; }
    internal JsonElement Metadata => FilmJson.Member(Raw, "metadata");
    internal JsonElement Browsing => FilmJson.Member(Raw, "browsing");
    internal JsonElement Attempt => FilmJson.Member(Raw, "attempt");
    internal JsonElement Current => FilmJson.Member(Raw, "current");
    internal string AttemptId => FilmJson.Text(Attempt, "id");
    internal string CurrentId => FilmJson.Text(Current, "attemptId");
}

public sealed record FilmSelection(string? Id, string Label, int? Season = null, JsonElement? WireId = null,
    string? MetadataSourceId = null, string? Provider = null);

public sealed record FilmCandidateItem(string Id, string Name, string Path, string Quality,
    string? Provider, string SourceKey, JsonElement DetailId, JsonElement LineId,
    int? Season = null, int? Episode = null, int? GlobalEpisode = null)
{
    public string DisplayLabel { get; init; } = "";
    public bool IsSelected { get; init; }
    public string Image { get; init; } = "";
    public string Overview { get; init; } = "";
    public JsonElement Resolution { get; init; }
    public JsonElement Fps { get; init; }
    public string Codec { get; init; } = "";
    private static string QualityLabel(string quality) => quality switch
    {
        "4k_hdr" => "4K HDR", "4k_fps" => "4K 60帧", "4k" => "4K", "1080p" => "1080P", "720p" => "720P",
        "" => "未知画质", _ => quality
    };
    internal static FilmCandidateItem Read(JsonElement value)
    {
        var source = FilmJson.Member(value, "source");
        return new(FilmJson.RequiredText(value, "id"), FilmJson.Text(value, "name"), FilmJson.Text(value, "path"),
            FilmJson.Text(value, "quality"), FilmJson.Text(value, "provider") is { Length: > 0 } provider ? provider : null,
            FilmJson.RequiredText(source, "key"), FilmJson.Identity(source, "detailId", required: true), FilmJson.Identity(value, "lineId"),
            FilmJson.Integer(value, "season"), FilmJson.Integer(value, "episode"), FilmJson.Integer(value, "globalEpisode"))
        {
            DisplayLabel = QualityLabel(FilmJson.Text(value, "quality")) + " · " + FilmJson.Text(source, "name"),
            Resolution = FilmJson.Member(value, "resolution"),
            Fps = FilmJson.Member(value, "fps"),
            Codec = FilmJson.Text(value, "codec")
        };
    }
}

internal sealed record FilmMediaReceipt(string AttemptId, string ResourceId, Uri Address, Uri? MediaOrigin,
    string Title, double Position, bool Paused, JsonElement Format, JsonElement Drm, JsonElement Scope)
{
    public override string ToString() => nameof(FilmMediaReceipt);
    internal static FilmMediaReceipt Read(JsonElement value)
    {
        if (!Uri.TryCreate(FilmJson.RequiredText(value, "url"), UriKind.Absolute, out var address))
            throw new FilmPlaybackException("INVALID_RESPONSE");
        Uri? origin = null;
        var mediaOrigin = FilmJson.Member(value, "mediaOrigin");
        if (mediaOrigin.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) &&
            !Uri.TryCreate(FilmJson.String(mediaOrigin), UriKind.Absolute, out origin))
            throw new FilmPlaybackException("INVALID_RESPONSE");
        if (FilmJson.Member(value, "paused").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new FilmPlaybackException("INVALID_RESPONSE");
        return new(FilmJson.RequiredText(value, "attemptId"), FilmJson.RequiredText(value, "id"), address, origin,
            FilmJson.Text(value, "title"), FilmJson.Number(value, "position"), FilmJson.Bool(value, "paused"),
            FilmJson.Member(value, "format"), FilmJson.Member(value, "drm"), FilmJson.Member(value, "scope"));
    }
}
