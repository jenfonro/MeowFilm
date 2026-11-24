using System.ComponentModel;
using System.Text.Json;
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
    public string Pan => FilmSettingsLogic.FormatPanLabel(FilmJson.Text(Value, Manual ? "provider" : "lineId"));
    public string Season => FilmJson.Text(Value, "seasonHint");
    public string AutoDisable => FilmJson.Bool(Value, "autoDisable") ? "开启" : "关闭";
    public string State => $"{(FilmJson.Bool(Value, "enabled") ? "已启用" : "已停用")} · 连续失败{FilmJson.Integer(Value, "failureCount") ?? 0}次";
}

public sealed record FilmSettingGroup(string Id, string Title, IReadOnlyList<FilmSettingRow> Rows);

internal static class FilmSettingsLogic
{
    internal static IReadOnlyList<FilmSettingsView.MetadataRoute> DoubanDataRoutes { get; } =
    [
        new("direct", "无", FilmMetadataSettingsStore.DoubanDefaults.ApiBase),
        new("cors", "Cors Proxy By Zwei", FilmMetadataSettingsStore.DoubanDefaults.ApiBase, "https://ciao-cors.is-an.org/"),
        new("cdn-tx", "豆瓣 CDN By CMLiussss（腾讯云）", "https://m.douban.cmliussss.net"),
        new("cdn-ali", "豆瓣 CDN By CMLiussss（阿里云）", "https://m.douban.cmliussss.com"),
        new("custom", "自定义代理", FilmMetadataSettingsStore.DoubanDefaults.ApiBase)
    ];

    internal static IReadOnlyList<FilmSettingsView.MetadataRoute> DoubanImageRoutes { get; } =
    [
        new("direct", "无", FilmMetadataSettingsStore.DoubanDefaults.ImageBase),
        new("cdn-tx", "豆瓣 CDN By CMLiussss（腾讯云）", "https://img.doubanio.cmliussss.net"),
        new("cdn-ali", "豆瓣 CDN By CMLiussss（阿里云）", "https://img.doubanio.cmliussss.com"),
        new("custom", "自定义代理", FilmMetadataSettingsStore.DoubanDefaults.ImageBase)
    ];

    internal static string FormatPanLabel(string value) => value.Trim().ToLowerInvariant() switch
    {
        "yidong" => "移动云盘",
        "tianyi" => "天翼云盘",
        "quark" => "夸克网盘",
        "uc" => "UC 网盘",
        "baidu" => "百度网盘",
        _ => value
    };

    internal static string FormatOptionLabel(string id) => id switch
    {
        "quality" => "画质",
        "pan" => "网盘",
        "keyword" => "关键词",
        _ => FormatPanLabel(id)
    };

    internal static FilmSettingRow[] ReadLibraryRows(JsonElement library, string field)
    {
        if (FilmJson.Member(library, field).ValueKind != JsonValueKind.Array)
            throw new FilmPlaybackException("INVALID_RESPONSE");
        return FilmJson.Array(library, field).Select(value => new FilmSettingRow(
            FilmJson.Text(value, "title") is { Length: > 0 } title ? title : FilmJson.Text(value, "targetId"),
            value.Clone(), field == "manual")).ToArray();
    }

    internal static FilmSettingGroup[] GroupLibraryRows(IEnumerable<FilmSettingRow> values) => values
        .GroupBy(row => FilmJson.Text(row.Value, "targetId"))
        .Select(group => new FilmSettingGroup(group.Key, group.First().Title, group.ToArray())).ToArray();

    internal static string[] ParseKeywords(string value) =>
        value.Split([',', '，']).Select(word => word.Trim()).Where(word => word.Length > 0).ToArray();

    internal static (FilmPreferences Next, string Field) BuildRestorePreferences(FilmPreferences current, string group)
    {
        var defaults = FilmPreferences.Defaults;
        return group switch
        {
            "rule" => (current with { RuleOrder = defaults.RuleOrder }, "ruleOrder"),
            "pan" => (current with { PanOrder = defaults.PanOrder }, "panOrder"),
            "keywords" => (current with { Keywords = defaults.Keywords }, "keywords"),
            _ => (current with { SiteCleanKeywords = defaults.SiteCleanKeywords }, "siteCleanKeywords")
        };
    }

    internal static string ResolveDoubanDataRouteId(FilmDoubanSettings value) =>
        value.SearchBase == FilmMetadataSettingsStore.DoubanDefaults.SearchBase
            ? DoubanDataRoutes.FirstOrDefault(route => route.Id != "custom" &&
                route.Base == value.ApiBase && route.Proxy == value.ApiProxy)?.Id ?? "custom"
            : "custom";

    internal static string ResolveDoubanImageRouteId(FilmDoubanSettings value) =>
        DoubanImageRoutes.FirstOrDefault(route => route.Id != "custom" &&
            route.Base == value.ImageBase && route.Proxy == value.ImageProxy)?.Id ?? "custom";

    internal static Dictionary<string, object?> BuildTmdbPatch(string apiBase, string imageBase,
        string apiToken, string language, string region, bool includeAdult) => new()
        {
            ["apiBase"] = apiBase.Trim(),
            ["imageBase"] = imageBase.Trim(),
            ["apiKey"] = apiToken,
            ["accessToken"] = "",
            ["language"] = language,
            ["region"] = region,
            ["includeAdult"] = includeAdult
        };

    internal static Dictionary<string, object?> BuildDoubanPatch(FilmDoubanSettings? saved,
        FilmSettingsView.MetadataRoute? dataRoute, string customDataProxy,
        FilmSettingsView.MetadataRoute? imageRoute, string customImageProxy, string cookie)
    {
        var data = dataRoute ?? DoubanDataRoutes[0];
        var image = imageRoute ?? DoubanImageRoutes[0];
        var defaults = FilmMetadataSettingsStore.DoubanDefaults;
        string apiBase = data.Id == "custom"
            ? (saved?.ApiBase is { Length: > 0 } existingApi ? existingApi : defaults.ApiBase)
            : data.Base;
        string searchBase = data.Id == "custom"
            ? (saved?.SearchBase is { Length: > 0 } existingSearch ? existingSearch : defaults.SearchBase)
            : defaults.SearchBase;
        string imageBase = image.Id == "custom"
            ? (saved?.ImageBase is { Length: > 0 } existingImage ? existingImage : defaults.ImageBase)
            : image.Base;
        return new()
        {
            ["apiBase"] = apiBase,
            ["searchBase"] = searchBase,
            ["apiProxy"] = data.Id == "custom" ? customDataProxy.Trim() : data.Proxy,
            ["imageBase"] = imageBase,
            ["imageProxy"] = image.Id == "custom" ? customImageProxy.Trim() : image.Proxy,
            ["cookie"] = cookie
        };
    }

    internal static FilmVideoOptions ApplyVideoOption(FilmVideoOptions current, string option, bool enabled) => option switch
    {
        "enabled" => current with { Enabled = enabled },
        "home" => current with { HomeVisible = enabled },
        "search" => current with { SearchEnabled = enabled },
        _ => throw new InvalidOperationException("未知视频源选项")
    };

    internal static bool TryResolveVideoOrder(IReadOnlyList<string> order,
        IReadOnlyDictionary<string, FilmSourceKey> byId, out FilmSourceKey[] resolved)
    {
        resolved = [];
        if (order.Count != byId.Count || order.Distinct(StringComparer.Ordinal).Count() != byId.Count ||
            order.Any(id => !byId.ContainsKey(id)))
            return false;
        resolved = order.Select(id => byId[id]).ToArray();
        return true;
    }
}
