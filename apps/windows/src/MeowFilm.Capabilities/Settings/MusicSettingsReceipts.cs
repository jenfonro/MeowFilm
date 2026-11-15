using System.Text.Json;
using MeowFilm.Desktop;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability : IDesktopOwnedNoticeSource
{
    private sealed record SettingNotice(object Owner, long Order, string Label);
    private readonly Dictionary<string, SettingNotice> settingNotices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> confirmedSettings = new(StringComparer.Ordinal);
    private long settingSubmission;
    public event EventHandler? OwnedNoticesChanged;
    public IReadOnlyList<DesktopOwnedNotice> OwnedNotices => settingNotices.Values
        .OrderBy(notice => notice.Order).DistinctBy(notice => notice.Owner).Select(notice => new DesktopOwnedNotice(notice.Owner,
            notice.Label + "保存结果未确认")).ToArray();

    // These keys name fields actually written by the native library commands.
    // The dictionaries belong to this capability's stable application/store scope;
    // account publication and settings page generations are not storage scopes.
    private static IReadOnlyList<string> SettingKeys(string method, object? parameters)
    {
        (string area, string[] fields) setting = method switch
        {
            "music.settings.save" => ("online-settings", ["lyrics"]),
            "music.officialSources.settings.save" => ("native-official-sources", ["enabled"]),
            "music.metadata.settings.save" => ("native-metadata-policy", ["platforms", "enabled"]),
            "music.quality.settings.save" => ("native-quality-policy",
                ["request", "qualityOrder", "qualityEnabled", "spatialOrder", "spatialEnabled"]),
            "music.search.settings.save" => ("native-search-policy",
                ["songs.order", "songs.enabled", "songs.pages", "songs.concurrency", "songs.cacheMinutes",
                 "playlists.order", "playlists.enabled", "playlists.pages", "playlists.concurrency", "playlists.cacheMinutes"]),
            "music.appearance.save" => ("native-appearance", ["playlistView", "playerTheme", "colorMode", "colorPalette"]),
            "music.catalogSource.settings.save" => ("native-catalog-source", ["platform"]),
            "music.device.settings.save" => ("native-device-settings", ["volume"]),
            "music.lyrics.desktop.settings.save" => ("native-desktop-lyrics", ["layout", "normalColor", "highlightColor"]),
            _ => ("", [])
        };
        var (area, fields) = setting;
        if (method == "music.lyrics.desktop.settings.save")
        {
            var input = parameters is JsonElement json ? json : JsonSerializer.SerializeToElement(parameters);
            if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("geometry", out _))
                fields = [.. fields, "geometry"];
        }
        return fields.Select(field => MusicLibraryOwner.DocumentNamespace + ":" + area + ":" + field).ToArray();
    }

    private static string? SettingLabel(string method) => method switch
    {
        "music.officialSources.settings.save" => "官方音源",
        "music.metadata.settings.save" => "元数据",
        "music.quality.settings.save" => "音质",
        "music.search.settings.save" => "搜索",
        "music.appearance.save" => "外观",
        "music.catalogSource.settings.save" => "首页来源",
        "music.device.settings.save" => "设备",
        "music.lyrics.desktop.settings.save" => "桌面歌词",
        "music.settings.save" => "在线播放",
        _ => null
    };
    private void SettingReceipt(string key, string label, long submission, bool confirmed)
    {
        if (confirmed)
        {
            confirmedSettings[key] = Math.Max(confirmedSettings.GetValueOrDefault(key), submission);
            if (!settingNotices.TryGetValue(key, out var notice) || notice.Order > submission) return;
            settingNotices.Remove(key);
        }
        else
        {
            if (confirmedSettings.GetValueOrDefault(key) >= submission ||
                settingNotices.TryGetValue(key, out var newer) && newer.Order > submission) return;
            var owner = settingNotices.GetValueOrDefault(key)?.Owner ??
                settingNotices.Values.FirstOrDefault(notice => notice.Label == label)?.Owner ?? new object();
            settingNotices[key] = new(owner, submission, label);
        }
        OwnedNoticesChanged?.Invoke(this, EventArgs.Empty);
    }
}
