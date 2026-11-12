using System.Text.Json;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private long desktopLyricSettingsRevision;
    private bool desktopLyricSettingsLoaded, desktopLyricSettingsBusy;
    private Task desktopLyricSettingsWork = Task.CompletedTask;
    private string desktopLyricLayout = "single";
    private string desktopLyricNormalColor = "#FFB5C5", desktopLyricHighlightColor = "#FFF2A8";

    public string DesktopLyricLayout => desktopLyricLayout;
    public string DesktopLyricNormalColor => desktopLyricNormalColor;
    public string DesktopLyricHighlightColor => desktopLyricHighlightColor;
    public bool CanEditDesktopLyricSettings => CanEdit && !disposed &&
        desktopLyricSettingsLoaded && !desktopLyricSettingsBusy;

    internal void DesktopLyricSettingsBindings() => Changed(nameof(CanEditDesktopLyricSettings));

    private void AcceptDesktopLyricSettings(JsonElement reply)
    {
        var settings = reply.GetProperty("settings");
        MusicDto.Require(MusicDto.Integer(settings, "schema") == 1);
        long revision = MusicDto.Integer(settings, "revision");
        string layout = MusicDto.Text(settings, "layout"),
            normalColor = MusicDto.Text(settings, "normalColor"),
            highlightColor = MusicDto.Text(settings, "highlightColor");
        MusicDto.Require(layout is "single" or "double");
        MusicDto.Require(AppearanceColors.TryParse(normalColor, out var normal));
        MusicDto.Require(AppearanceColors.TryParse(highlightColor, out var highlight));
        normalColor = AppearanceColors.Hex(normal);
        highlightColor = AppearanceColors.Hex(highlight);
        bool layoutChanged = desktopLyricLayout != layout,
            normalChanged = desktopLyricNormalColor != normalColor,
            highlightChanged = desktopLyricHighlightColor != highlightColor;
        desktopLyricSettingsRevision = revision;
        desktopLyricLayout = layout;
        desktopLyricNormalColor = normalColor;
        desktopLyricHighlightColor = highlightColor;
        desktopLyricSettingsLoaded = true;
        if (layoutChanged)
        {
            Changed(nameof(DesktopLyricLayout));
            DesktopLyricBindings();
        }
        if (normalChanged) Changed(nameof(DesktopLyricNormalColor));
        if (highlightChanged) Changed(nameof(DesktopLyricHighlightColor));
        DesktopLyricSettingsBindings();
    }

    internal Task EnsureDesktopLyricSettingsAsync()
    {
        if (desktopLyricSettingsBusy) return desktopLyricSettingsWork;
        if (desktopLyricSettingsLoaded || stopping || disposed) return Task.CompletedTask;
        desktopLyricSettingsBusy = true;
        DesktopLyricSettingsBindings();
        return desktopLyricSettingsWork = RunAsync(async () =>
        {
            try
            {
                var reply = await QualityCall("music.lyrics.desktop.settings.get", null, lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested();
                AcceptDesktopLyricSettings(reply);
            }
            catch
            {
                desktopLyricSettingsLoaded = false;
                if (!stopping && !disposed) await Notify("桌面歌词设置读取失败");
            }
            finally
            {
                desktopLyricSettingsBusy = false;
                DesktopLyricSettingsBindings();
            }
        });
    }

    public Task SetDesktopLyricLayoutAsync(string layout)
    {
        if (!CanEditDesktopLyricSettings) return Task.CompletedTask;
        if (layout is not ("single" or "double")) return Notify("桌面歌词设置无效");
        if (layout == desktopLyricLayout) return Task.CompletedTask;
        return SaveDesktopLyricSettingsAsync(layout, desktopLyricNormalColor, desktopLyricHighlightColor);
    }

    public Task SetDesktopLyricColorAsync(string role, string value)
    {
        if (!CanEditDesktopLyricSettings) return Task.CompletedTask;
        if (role is not ("normal" or "highlight") || !AppearanceColors.TryParse(value, out var color))
            return Notify("桌面歌词设置无效");
        string normal = role == "normal" ? AppearanceColors.Hex(color) : desktopLyricNormalColor,
            highlight = role == "highlight" ? AppearanceColors.Hex(color) : desktopLyricHighlightColor;
        if (normal == desktopLyricNormalColor && highlight == desktopLyricHighlightColor) return Task.CompletedTask;
        return SaveDesktopLyricSettingsAsync(desktopLyricLayout, normal, highlight);
    }

    private Task SaveDesktopLyricSettingsAsync(string layout, string normalColor, string highlightColor)
    {
        var draft = new { ifRevision = desktopLyricSettingsRevision, layout, normalColor, highlightColor };
        desktopLyricSettingsBusy = true;
        DesktopLyricSettingsBindings();
        return desktopLyricSettingsWork = RunAsync(async () =>
        {
            try
            {
                try
                {
                    AcceptDesktopLyricSettings(await QualityCall("music.lyrics.desktop.settings.save", draft, lifetime.Token));
                }
                catch (MusicOperationException error) when (error.Code is
                    "COMMIT_UNKNOWN" or "STORAGE_CONFLICT" or "PENDING_WRITE" or "REVISION_CONFLICT")
                {
                    AcceptDesktopLyricSettings(await QualityCall("music.lyrics.desktop.settings.get", null, lifetime.Token));
                    if (desktopLyricLayout != layout || desktopLyricNormalColor != normalColor ||
                        desktopLyricHighlightColor != highlightColor)
                        await Notify(error.Code == "REVISION_CONFLICT" ? "桌面歌词设置已更新" : "桌面歌词设置保存失败");
                }
            }
            catch
            {
                desktopLyricSettingsLoaded = false;
                if (!stopping && !disposed) await Notify("桌面歌词设置保存失败");
            }
            finally
            {
                desktopLyricSettingsBusy = false;
                DesktopLyricSettingsBindings();
            }
        });
    }
}
