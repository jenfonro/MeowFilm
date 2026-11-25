using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

internal sealed record MusicSettingReceiptDescriptor(string Label, string[] Keys);

internal static class MusicSettingReceiptKeys
{
    internal static readonly MusicSettingReceiptDescriptor OfficialSources =
        new("官方音源", ["native-official-sources:enabled"]);
    internal static readonly MusicSettingReceiptDescriptor MetadataPolicy =
        new("元数据", ["native-metadata-policy:platforms", "native-metadata-policy:enabled"]);
    internal static readonly MusicSettingReceiptDescriptor QualityPolicy =
        new("音质", [
            "native-quality-policy:request",
            "native-quality-policy:qualityOrder",
            "native-quality-policy:qualityEnabled",
            "native-quality-policy:spatialOrder",
            "native-quality-policy:spatialEnabled"
        ]);
    internal static readonly MusicSettingReceiptDescriptor SearchPolicy =
        new("搜索", [
            "native-search-policy:songs.order", "native-search-policy:songs.enabled",
            "native-search-policy:songs.pages", "native-search-policy:songs.concurrency", "native-search-policy:songs.cacheMinutes",
            "native-search-policy:playlists.order", "native-search-policy:playlists.enabled",
            "native-search-policy:playlists.pages", "native-search-policy:playlists.concurrency", "native-search-policy:playlists.cacheMinutes"
        ]);
    internal static readonly MusicSettingReceiptDescriptor Appearance =
        new("外观", [
            "native-appearance:playlistView", "native-appearance:playerTheme",
            "native-appearance:colorMode", "native-appearance:colorPalette"
        ]);
    internal static readonly MusicSettingReceiptDescriptor CatalogSource =
        new("首页来源", ["native-catalog-source:platform"]);
    internal static readonly MusicSettingReceiptDescriptor DeviceSettings =
        new("设备", ["native-device-settings:volume"]);
    internal static readonly MusicSettingReceiptDescriptor DesktopLyricsCore =
        new("桌面歌词", [
            "native-desktop-lyrics:layout", "native-desktop-lyrics:normalColor", "native-desktop-lyrics:highlightColor"
        ]);
    internal static readonly MusicSettingReceiptDescriptor DesktopLyricsWithGeometry =
        new("桌面歌词", [
            "native-desktop-lyrics:layout", "native-desktop-lyrics:normalColor",
            "native-desktop-lyrics:highlightColor", "native-desktop-lyrics:geometry"
        ]);

    internal static MusicSettingReceiptDescriptor DesktopLyrics(bool includeGeometry) =>
        includeGeometry ? DesktopLyricsWithGeometry : DesktopLyricsCore;
}

/// <summary>Tracks unconfirmed settings save receipts for desktop-owned notices.</summary>
internal sealed class MusicSettingReceiptTracker : IDesktopOwnedNoticeSource
{
    private sealed record SettingNotice(object Owner, long Order, string Label);
    private readonly Dictionary<string, SettingNotice> settingNotices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> confirmedSettings = new(StringComparer.Ordinal);
    private long settingSubmission;

    public event EventHandler? OwnedNoticesChanged;

    public IReadOnlyList<DesktopOwnedNotice> OwnedNotices => settingNotices.Values
        .OrderBy(notice => notice.Order).DistinctBy(notice => notice.Owner).Select(notice => new DesktopOwnedNotice(notice.Owner,
            notice.Label + "保存结果未确认")).ToArray();

    internal long NextSubmission() => ++settingSubmission;

    internal void Record(string key, string label, long submission, bool confirmed)
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
