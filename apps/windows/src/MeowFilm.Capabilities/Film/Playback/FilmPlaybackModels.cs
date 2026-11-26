using System.ComponentModel;
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
        "NO_VIDEO_SOURCES" => "暂无可用视频源",
        "TIMEOUT" or "MEDIA_UPSTREAM_TIMEOUT" or "MEDIA_PREFETCH_TIMEOUT" or PlaybackError.HttpDeadline => "请求超时，请重试",
        "CLOSED" => "服务已关闭，请重连",
        "DISCONNECTED" => "连接已断开，请重连",
        "STALE_SESSION" => "会话已失效，请重连",
        "INVALID_RESPONSE" => "响应无效，请重试",
        "UPSTREAM_FORMAT" => "上游数据格式错误",
        "NO_MATCH" => "未找到匹配片源",
        "PERMISSION_DENIED" => "没有访问权限",
        "AUTH_REQUIRED" => "请先登录账号",
        "AUTH_FAILED" => "认证失败，请检查凭据",
        PlaybackError.HttpUnauthorized => "媒体认证失败",
        "LOGIN_EXPIRED" => "账号登录已失效，请重新登录",
        "NOT_CONFIGURED" => "尚未完成配置",
        "CHALLENGE_REQUIRED" => "需要完成账号验证",
        "WEB_INTERACTION_REQUIRED" => "需要完成网页验证，请在数据源设置中检查 Cookie 或代理后重试",
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
        "CACHE_LOAD_FAILED" => "缓存读取失败",
        "STORAGE_CHANGED" => "本地数据已变更，请重试",
        "LIBRARY_UPDATE_FAILED" => "媒体库更新失败",
        "UNSUPPORTED_OPERATION" or "CAPABILITY_UNAVAILABLE" or "METHOD_NOT_FOUND" => "当前操作不可用",
        "PAN_FAILURE" => "网盘请求失败",
        "PAN_INTERCEPTION_UNSUPPORTED" => "网盘请求未能接管，请关闭对应网盘接管后重试",
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
        "MEDIA_MANIFEST_INVALID" => "媒体清单格式错误",
        "MEDIA_MANIFEST_UNSUPPORTED" => "媒体清单不受支持",
        "MEDIA_METHOD_NOT_ALLOWED" => "媒体请求方法不受支持",
        "MEDIA_MANIFEST_LIMIT" or "MEDIA_RESOURCE_LIMIT" => "媒体资源超出处理预算",
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
        PlaybackError.InvalidRate => "当前媒体不支持此倍速",
        PlaybackError.OpenFailed => "媒体打开失败",
        PlaybackError.PlaybackFailed => "媒体播放失败",
        PlaybackError.NativeUnavailable => "媒体引擎不可用",
        PlaybackError.HttpFormatUnsupported or PlaybackError.ReadFormatUnsupported or PlaybackError.UnsupportedUri
            or PlaybackError.UnsupportedAdaptive or PlaybackError.FilmFormatUnsupported => "媒体格式不受支持",
        PlaybackError.FilmEncryptionUnsupported => "当前媒体加密方式不受支持",
        PlaybackError.FilmDecodeFailed => "视频解码失败",
        PlaybackError.FilmAudioFailed => "音频输出失败",
        PlaybackError.FilmRenderFailed => "视频画面渲染失败",
        PlaybackError.FilmHdrDisplayUnsupported => "当前显示器不支持原生 HDR 输出",
        PlaybackError.FilmDoviUnsupported => "当前杜比视界格式不受支持",
        PlaybackError.FilmDeviceLost => "图形设备已重置，请重试",
        PlaybackError.ReadFailed => "媒体读取失败",
        PlaybackError.ReadSeekFailed or PlaybackError.InvalidSeek => "媒体跳转失败",
        PlaybackError.NotSeekable => "当前媒体不支持跳转进度",
        PlaybackError.ReadChanged => "媒体内容已变更，请重新取址",
        PlaybackError.HttpServerFailed or PlaybackError.HttpStatusFailed => "媒体服务请求失败",
        PlaybackError.HttpEncodingUnsupported => "媒体编码不受支持",
        PlaybackError.HttpInvalidRange or PlaybackError.HttpRangeRejected => "媒体范围读取失败",
        PlaybackError.InvalidTrack or PlaybackError.TrackMetadataInvalid => "媒体轨道信息无效",
        PlaybackError.TrackSelectionFailed => "切换媒体轨道失败",
        PlaybackError.CommandFailed => "播放控制操作失败",
        PlaybackError.UnsupportedHeaders or PlaybackError.InvalidHeaders => "媒体请求头不受支持",
        PlaybackError.InvalidResourceLease or PlaybackError.InvalidReadLease => "媒体资源租约无效",
        PlaybackError.DispatcherUnavailable or PlaybackError.WrongThread => "播放器线程状态异常",
        PlaybackError.Superseded => "播放已被新操作替代",
        _ => failure
    };
}

/// <summary>Only an actual native occurrence, never selected UI metadata or a prepared URL.</summary>
public sealed record FilmPlaybackMediaState(string? Identity, string Title, bool HasMedia, bool IsPlaying,
    double Position, double Duration);

public sealed record FilmTmdbRef(string Kind, int Id);
public sealed record FilmDoubanRef(string Kind, string Id);

public sealed record FilmSiteEntryRef(string SourceKey, string? InstallationId, string? SiteKey,
    JsonElement DetailId, string RawTitle = "", string Remark = "", string Poster = "")
{
    internal FilmSiteEntryRef Own() => this with
    {
        DetailId = DetailId.ValueKind == JsonValueKind.Undefined ? default : DetailId.Clone()
    };

    internal bool SameContent(FilmSiteEntryRef? other) => other is not null &&
        SourceKey == other.SourceKey && InstallationId == other.InstallationId && SiteKey == other.SiteKey &&
        RawTitle == other.RawTitle && Remark == other.Remark && Poster == other.Poster &&
        FilmJson.Equal(DetailId, other.DetailId);
}

/// <summary>Unified lightweight card entry shared across Home, Site Category, Search, Favorites, and History.</summary>
public sealed record FilmEntry(
    string TargetId,
    string Keyword,
    string Title,
    string Subtitle = "",
    string Poster = "",
    string Backdrop = "",
    string Score = "",
    string TextBadge = "",
    string SiteLabel = "",
    string? Kind = null,
    int? Year = null,
    int? Season = null,
    int? Episode = null,
    int? SeasonHint = null,
    FilmTmdbRef? Tmdb = null,
    FilmDoubanRef? Douban = null,
    FilmSiteEntryRef? SiteEntry = null)
{
    internal FilmEntry Own() => SiteEntry is null ? this : this with { SiteEntry = SiteEntry.Own() };

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(TargetId) || string.IsNullOrWhiteSpace(Keyword) || string.IsNullOrWhiteSpace(Title) ||
            Kind is not (null or "movie" or "tv") ||
            Tmdb is { } tmdb && (tmdb.Kind is not ("movie" or "tv") || tmdb.Id <= 0) ||
            Douban is { } douban && (douban.Kind is not ("movie" or "tv") || string.IsNullOrWhiteSpace(douban.Id)) ||
            SiteEntry is { } site && (string.IsNullOrWhiteSpace(site.SourceKey) || !FilmJson.IsIdentity(site.DetailId)))
            throw new FilmPlaybackException("INVALID_REQUEST");
    }

    internal bool SameContent(FilmEntry? other) => other is not null &&
        TargetId == other.TargetId && Keyword == other.Keyword && Title == other.Title &&
        Subtitle == other.Subtitle && Poster == other.Poster && Backdrop == other.Backdrop &&
        Score == other.Score && TextBadge == other.TextBadge && SiteLabel == other.SiteLabel &&
        Kind == other.Kind && Year == other.Year && Season == other.Season &&
        Episode == other.Episode && SeasonHint == other.SeasonHint &&
        Tmdb == other.Tmdb && Douban == other.Douban &&
        (SiteEntry is null ? other.SiteEntry is null : SiteEntry.SameContent(other.SiteEntry));

    internal object Wire() => new
    {
        targetId = TargetId,
        keyword = Keyword,
        title = Title,
        subtitle = Subtitle,
        poster = Poster,
        backdrop = Backdrop,
        score = Score,
        textBadge = TextBadge,
        siteLabel = SiteLabel,
        kind = Kind,
        year = Year,
        season = Season,
        episode = Episode,
        seasonHint = SeasonHint,
        tmdb = Tmdb is null ? null : new { kind = Tmdb.Kind, id = Tmdb.Id },
        douban = Douban is null ? null : new { kind = Douban.Kind, id = Douban.Id },
        siteEntry = SiteEntry is null ? null : new
        {
            sourceKey = SiteEntry.SourceKey,
            installationId = SiteEntry.InstallationId,
            siteKey = SiteEntry.SiteKey,
            detailId = SiteEntry.DetailId,
            rawTitle = SiteEntry.RawTitle,
            remark = SiteEntry.Remark,
            poster = SiteEntry.Poster
        }
    };

    internal static FilmEntry Read(JsonElement card)
    {
        if (card.ValueKind != JsonValueKind.Object) throw new FilmPlaybackException("INVALID_RESPONSE");
        string title = FilmSearchJson.Text(card, "title");
        string keyword = FilmSearchJson.Text(card, "keyword");
        if (keyword.Length == 0) keyword = title;
        string targetId = FilmSearchJson.Text(card, "targetId");
        string? kind = FilmJson.Text(card, "kind") is ("movie" or "tv") and var k ? k : null;

        FilmTmdbRef? tmdb = null;
        if (FilmJson.Member(card, "tmdb") is { ValueKind: JsonValueKind.Object } tmdbObj &&
            FilmJson.Text(tmdbObj, "kind") is ("movie" or "tv") and var tk &&
            FilmJson.Integer(tmdbObj, "id") is > 0 and var tid)
        {
            tmdb = new(tk, tid);
        }

        FilmDoubanRef? douban = null;
        if (FilmJson.Member(card, "douban") is { ValueKind: JsonValueKind.Object } doubanObj &&
            FilmJson.Text(doubanObj, "kind") is ("movie" or "tv") and var dk &&
            FilmJson.Text(doubanObj, "id") is { Length: > 0 } did)
        {
            douban = new(dk, did);
        }

        FilmSiteEntryRef? siteEntry = null;
        if (FilmJson.Member(card, "siteEntry") is { ValueKind: JsonValueKind.Object } siteObj &&
            FilmJson.Text(siteObj, "sourceKey") is { Length: > 0 } sourceKey &&
            FilmJson.Member(siteObj, "detailId") is var detailId && FilmJson.IsIdentity(detailId))
        {
            siteEntry = new(
                sourceKey,
                FilmJson.Text(siteObj, "installationId") is { Length: > 0 } inst ? inst : null,
                FilmJson.Text(siteObj, "siteKey") is { Length: > 0 } sk ? sk : null,
                detailId.Clone(),
                FilmSearchJson.Text(siteObj, "rawTitle"),
                FilmSearchJson.Text(siteObj, "remark"),
                FilmSearchJson.Text(siteObj, "poster"));
        }

        var entry = new FilmEntry(
            targetId,
            keyword,
            title,
            FilmSearchJson.Text(card, "subtitle"),
            FilmSearchJson.Text(card, "poster"),
            FilmSearchJson.Text(card, "backdrop"),
            FilmSearchJson.Text(card, "score"),
            FilmSearchJson.Text(card, "textBadge"),
            FilmSearchJson.Text(card, "siteLabel"),
            kind,
            FilmJson.Integer(card, "year"),
            FilmJson.Integer(card, "season"),
            FilmJson.Integer(card, "episode"),
            FilmJson.Integer(card, "seasonHint"),
            tmdb,
            douban,
            siteEntry);
        entry.Validate();
        return entry;
    }
}

internal static class FilmJson
{
    internal static readonly char[] InputWhitespace =
        "\u0009\u000a\u000b\u000c\u000d\u0020\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000\ufeff".ToCharArray();

    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static JsonElement Encode(object value) => JsonSerializer.SerializeToElement(value, Options);
    internal static JsonElement Member(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var member) ? member : default;
    internal static bool Equal(JsonElement left, JsonElement right) =>
        left.ValueKind == right.ValueKind &&
        (left.ValueKind == JsonValueKind.Undefined || JsonElement.DeepEquals(left, right));
    internal static JsonElement Copy(JsonElement value) =>
        value.ValueKind == JsonValueKind.Undefined ? default : value.Clone();
    internal static string Text(JsonElement value, string name) => String(Member(value, name));
    internal static string String(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! :
        value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "";
    internal static string RequiredText(JsonElement value, string name)
    {
        var member = Member(value, name);
        if (member.ValueKind != JsonValueKind.String || member.GetString()!.Length == 0)
            throw new FilmPlaybackException("INVALID_RESPONSE");
        return member.GetString()!;
    }
    internal static bool IsIdentity(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString()!.Length > 0
        : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) &&
            number is >= -9007199254740991 and <= 9007199254740991;
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
    internal static string Time(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(
        seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

    internal static string Score(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) &&
            double.IsFinite(number) && number > 0)
            return number.ToString("0.0", CultureInfo.InvariantCulture);
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
            double.IsFinite(parsed) && parsed > 0)
            return parsed.ToString("0.0", CultureInfo.InvariantCulture);
        return "";
    }
}

public sealed record FilmSelection(string Id, string Label, string Kind = "", int? Season = null, string? Provider = null);

internal sealed record FilmWorkSnapshot(
    string TargetId,
    string Keyword,
    string Kind,
    string Title,
    string Subtitle,
    string Poster,
    string Backdrop,
    string Logo,
    string Year,
    string Countries,
    string Duration,
    int? SeasonCount,
    int? EpisodeCount,
    IReadOnlyList<string> Genres,
    string Score,
    string ScoreProvider,
    string Directors,
    string Cast,
    string Summary,
    FilmTmdbRef? Tmdb,
    FilmDoubanRef? Douban,
    IReadOnlyList<FilmCardItem> RelatedCards)
{
    internal static FilmWorkSnapshot Read(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new FilmPlaybackException("INVALID_RESPONSE");
        string kind = FilmJson.Text(value, "kind") is "movie" ? "movie" : "tv";
        var genres = FilmJson.Array(value, "genres")
            .Select(FilmJson.String)
            .Where(g => g.Length > 0)
            .ToArray();
        string cast = string.Join(" / ", FilmJson.Array(value, "cast")
            .Select(person => FilmJson.Text(person, "name"))
            .Where(name => name.Length > 0));

        FilmTmdbRef? tmdb = null;
        if (FilmJson.Member(value, "tmdb") is { ValueKind: JsonValueKind.Object } tmdbObj &&
            FilmJson.Text(tmdbObj, "kind") is ("movie" or "tv") and var tk &&
            FilmJson.Integer(tmdbObj, "id") is > 0 and var tid)
            tmdb = new(tk, tid);

        FilmDoubanRef? douban = null;
        if (FilmJson.Member(value, "douban") is { ValueKind: JsonValueKind.Object } doubanObj &&
            FilmJson.Text(doubanObj, "kind") is ("movie" or "tv") and var dk &&
            FilmJson.Text(doubanObj, "id") is { Length: > 0 } did)
            douban = new(dk, did);

        var related = new List<FilmCardItem>();
        foreach (var cardJson in FilmJson.Array(value, "relatedCards"))
        {
            if (cardJson.ValueKind != JsonValueKind.Object) continue;
            try
            {
                string id = FilmJson.RequiredText(cardJson, "id");
                var entry = FilmEntry.Read(cardJson);
                related.Add(new FilmCardItem(id, entry.Title, entry.Poster, entry.Score)
                {
                    TextBadge = entry.TextBadge,
                    Subtitle = entry.Subtitle,
                    Entry = entry
                });
            }
            catch (FilmPlaybackException) { }
        }

        return new(
            FilmJson.Text(value, "targetId"),
            FilmJson.Text(value, "keyword"),
            kind,
            FilmJson.Text(value, "title"),
            FilmJson.Text(value, "subtitle"),
            FilmJson.Text(value, "poster"),
            FilmJson.Text(value, "backdrop"),
            FilmJson.Text(value, "logo"),
            FilmJson.Text(value, "year"),
            FilmJson.Text(value, "countries"),
            FilmJson.Text(value, "duration"),
            FilmJson.Integer(value, "seasonCount"),
            FilmJson.Integer(value, "episodeCount"),
            genres,
            FilmJson.Text(value, "score"),
            FilmJson.Text(value, "scoreProvider"),
            FilmJson.Text(value, "directors"),
            cast,
            FilmJson.Text(value, "summary"),
            tmdb,
            douban,
            related);
    }

    internal bool SameContent(FilmWorkSnapshot? other) => other is not null &&
        TargetId == other.TargetId && Keyword == other.Keyword && Kind == other.Kind &&
        Title == other.Title && Subtitle == other.Subtitle && Poster == other.Poster &&
        Backdrop == other.Backdrop && Logo == other.Logo && Year == other.Year &&
        Countries == other.Countries && Duration == other.Duration &&
        SeasonCount == other.SeasonCount && EpisodeCount == other.EpisodeCount &&
        Genres.SequenceEqual(other.Genres, StringComparer.Ordinal) &&
        Score == other.Score && ScoreProvider == other.ScoreProvider &&
        Directors == other.Directors && Cast == other.Cast && Summary == other.Summary &&
        Tmdb == other.Tmdb && Douban == other.Douban &&
        RelatedCards.Select(c => (c.Id, c.Title, c.Poster, c.Score))
            .SequenceEqual(other.RelatedCards.Select(c => (c.Id, c.Title, c.Poster, c.Score)));
}

internal sealed record FilmAttemptInfo(
    string Id,
    string CandidateId,
    bool PrepareOnly,
    double Position,
    double Duration,
    bool Paused,
    string Title,
    int? Season,
    int? Episode,
    int? GlobalEpisode,
    string EpisodeLabel,
    string SourceLabel,
    string Quality,
    string? Provider)
{
    internal static FilmAttemptInfo? Read(JsonElement value, bool isCurrent)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        string id = FilmJson.RequiredText(value, isCurrent ? "attemptId" : "id");
        double pos = FilmJson.Member(value, "position") is { ValueKind: JsonValueKind.Number } p && p.TryGetDouble(out double pv) ? pv : 0;
        double dur = isCurrent && FilmJson.Member(value, "duration") is { ValueKind: JsonValueKind.Number } d && d.TryGetDouble(out double dv) ? dv : 0;
        return new(
            id,
            FilmJson.Text(value, "candidateId"),
            !isCurrent && FilmJson.Bool(value, "prepareOnly"),
            pos,
            dur,
            FilmJson.Bool(value, "paused"),
            FilmJson.Text(value, "title"),
            FilmJson.Integer(value, "season"),
            FilmJson.Integer(value, "episode"),
            FilmJson.Integer(value, "globalEpisode"),
            FilmJson.Text(value, "episodeLabel"),
            FilmJson.Text(value, "sourceLabel"),
            FilmJson.Text(value, "quality"),
            FilmJson.Text(value, "provider") is { Length: > 0 } prov ? prov : null);
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
            FilmJson.Member(value, "work").ValueKind != JsonValueKind.Object ||
            FilmJson.Member(value, "sourceMenu").ValueKind != JsonValueKind.Object ||
            FilmJson.Member(value, "lineMenu").ValueKind != JsonValueKind.Object ||
            FilmJson.Member(value, "directory").ValueKind != JsonValueKind.Object ||
            FilmJson.Member(value, "player").ValueKind != JsonValueKind.Object)
            throw new FilmPlaybackException("INVALID_RESPONSE");

        Raw = value.Clone();
        SessionId = id;
        Sequence = sequence;
        TargetId = FilmJson.RequiredText(Raw, "targetId");
        Keyword = FilmJson.Text(Raw, "keyword");
        Stage = FilmJson.Text(Raw, "stage");
        Error = FilmJson.Text(Raw, "error");
        ErrorStage = FilmJson.Text(Raw, "errorStage");
        ReportError = FilmJson.Text(Raw, "reportError");
        NoSources = FilmJson.Bool(Raw, "noSources");
        RetryMediaAttemptId = FilmJson.Text(Raw, "retryMediaAttemptId");
        Work = FilmWorkSnapshot.Read(FilmJson.Member(Raw, "work"));

        var sm = FilmJson.Member(Raw, "sourceMenu");
        SourceSelectedId = FilmJson.Text(sm, "selectedId") is { Length: > 0 } sid ? sid : null;
        SourcePlaceholder = FilmJson.Text(sm, "placeholder") is { Length: > 0 } ph ? ph : "选择站点源";
        SourceDisabled = FilmJson.Bool(sm, "disabled");
        SourceCanLoadMore = FilmJson.Bool(sm, "canLoadMore");
        SourceLoadingMore = FilmJson.Bool(sm, "loadingMore");
        SourceOptions = FilmJson.Array(sm, "options").Select(opt => new FilmSelection(
            FilmJson.RequiredText(opt, "id"),
            FilmJson.Text(opt, "label"),
            Kind: FilmJson.Text(opt, "kind"))).ToArray();

        var lm = FilmJson.Member(Raw, "lineMenu");
        LineVisible = FilmJson.Bool(lm, "visible");
        LineSelectedId = FilmJson.Text(lm, "selectedId") is { Length: > 0 } lid ? lid : null;
        LineOptions = FilmJson.Array(lm, "options").Select(opt => new FilmSelection(
            FilmJson.RequiredText(opt, "id"),
            FilmJson.Text(opt, "label"),
            Provider: FilmJson.Text(opt, "provider") is { Length: > 0 } lp ? lp : null)).ToArray();

        var dir = FilmJson.Member(Raw, "directory");
        DirectoryMode = FilmJson.Text(dir, "mode") is { Length: > 0 } dm ? dm : "empty";
        DirectoryLoading = FilmJson.Bool(dir, "loading");
        DirectoryRefreshing = FilmJson.Bool(dir, "refreshing");
        DirectoryError = FilmJson.Text(dir, "error");
        DirectorySelectedSeason = FilmJson.Integer(dir, "selectedSeason");
        DirectoryCanLoadMore = FilmJson.Bool(dir, "canLoadMore");
        DirectoryLoadingMore = FilmJson.Bool(dir, "loadingMore");
        PreviewEpisodes = FilmJson.Bool(dir, "previewEpisodes");
        CanTogglePreview = FilmJson.Bool(dir, "canTogglePreview");
        DirectorySeasons = FilmJson.Array(dir, "seasons").Select(s =>
        {
            int seasonNum = FilmJson.Integer(s, "season") ?? 1;
            string label = FilmJson.Text(s, "label") is { Length: > 0 } sl ? sl : $"第{seasonNum}季";
            return new FilmSelection($"season:{seasonNum}", label, Kind: "season", Season: seasonNum);
        }).ToArray();
        DirectoryItems = FilmJson.Member(dir, "items");
        DirectoryRawItems = FilmJson.Member(dir, "rawItems");

        var pl = FilmJson.Member(Raw, "player");
        ShortVideo = FilmJson.Bool(pl, "shortVideo");
        CanPrevious = FilmJson.Bool(pl, "canPrevious");
        CanNext = FilmJson.Bool(pl, "canNext");
        CanSwitchSource = FilmJson.Bool(pl, "canSwitchSource");
        CanBlockSource = FilmJson.Bool(pl, "canBlockSource");
        ActiveSourceLabel = FilmJson.Text(pl, "activeSourceLabel");
        ActiveEpisodeLabel = FilmJson.Text(pl, "activeEpisodeLabel");
        ActiveQuality = FilmJson.Text(pl, "activeQuality");
        ActivePan = FilmJson.Text(pl, "activePan");
        var constraint = FilmJson.Member(pl, "constraint");
        ConstraintAction = FilmJson.Text(constraint, "action");
        ConstraintProvider = FilmJson.Text(constraint, "provider");
        ConstraintQuality = FilmJson.Text(constraint, "quality");
        AvailableQualities = FilmJson.Array(pl, "availableQualities").Select(q =>
            new FilmPlayerOption(FilmJson.RequiredText(q, "key"), FilmJson.Text(q, "label"), FilmJson.Bool(q, "active"))).ToArray();
        AvailablePans = FilmJson.Array(pl, "availablePans").Select(p =>
            new FilmPlayerOption(FilmJson.RequiredText(p, "provider"), FilmJson.Text(p, "label"), FilmJson.Bool(p, "active"))).ToArray();
        Attempt = FilmAttemptInfo.Read(FilmJson.Member(pl, "attempt"), isCurrent: false);
        Current = FilmAttemptInfo.Read(FilmJson.Member(pl, "current"), isCurrent: true);
    }

    internal JsonElement Raw { get; }
    internal Guid SessionId { get; }
    internal long Sequence { get; }
    internal string TargetId { get; }
    internal string Keyword { get; }
    internal string Stage { get; }
    internal string Error { get; }
    internal string ErrorStage { get; }
    internal string ReportError { get; }
    internal bool NoSources { get; }
    internal string RetryMediaAttemptId { get; }
    internal FilmWorkSnapshot Work { get; }

    internal string? SourceSelectedId { get; }
    internal string SourcePlaceholder { get; }
    internal bool SourceDisabled { get; }
    internal bool SourceCanLoadMore { get; }
    internal bool SourceLoadingMore { get; }
    internal IReadOnlyList<FilmSelection> SourceOptions { get; }

    internal bool LineVisible { get; }
    internal string? LineSelectedId { get; }
    internal IReadOnlyList<FilmSelection> LineOptions { get; }

    internal string DirectoryMode { get; }
    internal bool DirectoryLoading { get; }
    internal bool DirectoryRefreshing { get; }
    internal string DirectoryError { get; }
    internal int? DirectorySelectedSeason { get; }
    internal bool DirectoryCanLoadMore { get; }
    internal bool DirectoryLoadingMore { get; }
    internal bool PreviewEpisodes { get; }
    internal bool CanTogglePreview { get; }
    internal IReadOnlyList<FilmSelection> DirectorySeasons { get; }
    internal JsonElement DirectoryItems { get; }
    internal JsonElement DirectoryRawItems { get; }

    internal bool ShortVideo { get; }
    internal bool CanPrevious { get; }
    internal bool CanNext { get; }
    internal bool CanSwitchSource { get; }
    internal bool CanBlockSource { get; }
    internal string ActiveSourceLabel { get; }
    internal string ActiveEpisodeLabel { get; }
    internal string ActiveQuality { get; }
    internal string ActivePan { get; }
    internal string ConstraintAction { get; }
    internal string ConstraintProvider { get; }
    internal string ConstraintQuality { get; }
    internal IReadOnlyList<FilmPlayerOption> AvailableQualities { get; }
    internal IReadOnlyList<FilmPlayerOption> AvailablePans { get; }
    internal FilmAttemptInfo? Attempt { get; }
    internal FilmAttemptInfo? Current { get; }
    internal string AttemptId => Attempt?.Id ?? "";
    internal string CurrentId => Current?.Id ?? "";
}

public sealed record FilmCandidateItem(string Id, string Name, string Path, string Quality,
    string SourceLabel, string Image, string Overview) : INotifyPropertyChanged
{
    public string DisplayLabel { get; init; } = "";
    public bool IsSelected { get; private set; }
    internal void Select(bool value)
    {
        if (IsSelected == value) return;
        IsSelected = value;
        PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal bool SameDisplay(FilmCandidateItem other) =>
        Id == other.Id && Name == other.Name && Path == other.Path && Quality == other.Quality &&
        SourceLabel == other.SourceLabel && DisplayLabel == other.DisplayLabel &&
        Image == other.Image && Overview == other.Overview;

    internal static FilmCandidateItem Read(JsonElement row, string poster, string overview)
    {
        string id = FilmJson.RequiredText(row, "id");
        string name = FilmJson.Text(row, "fileName");
        if (name.Length == 0) name = FilmJson.Text(row, "title");
        string quality = FilmJson.Text(row, "quality");
        string sourceLabel = FilmJson.Text(row, "sourceLabel");
        string displayLabel = quality.Length > 0 && sourceLabel.Length > 0
            ? $"{quality} · {sourceLabel}"
            : quality.Length > 0 ? quality : sourceLabel.Length > 0 ? sourceLabel : "未知画质";
        return new(id, name, FilmJson.Text(row, "path"), quality, sourceLabel, poster, overview)
        {
            DisplayLabel = displayLabel
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
