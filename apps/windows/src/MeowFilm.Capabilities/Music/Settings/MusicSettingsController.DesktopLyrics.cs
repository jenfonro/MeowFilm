using MeowFilm.Capabilities.Library;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

internal readonly record struct MusicDesktopLyricsGeometry(
    string Monitor, double X, double Y, double Width, double Height);

public sealed partial class MusicSettingsController
{
    private long desktopLyricSettingsRevision;
    private bool desktopLyricSettingsLoaded, desktopLyricSettingsBusy;
    private int desktopLyricSettingsPending;
    private Task desktopLyricSettingsWork = Task.CompletedTask;
    private MusicDesktopLyricsGeometry? desktopLyricGeometryRequest;
    private int desktopLyricGeometryWrites;
    private string desktopLyricLayout = "single";
    private string desktopLyricNormalColor = "#FFB5C5", desktopLyricHighlightColor = "#FFF2A8";
    private MusicDesktopLyricsGeometry? desktopLyricGeometry;

    public string DesktopLyricLayout => desktopLyricLayout;
    public string DesktopLyricNormalColor => desktopLyricNormalColor;
    public string DesktopLyricHighlightColor => desktopLyricHighlightColor;
    public bool CanEditDesktopLyricSettings => CanEdit && !disposed &&
        desktopLyricSettingsLoaded && !desktopLyricSettingsBusy;

    internal void DesktopLyricSettingsBindings() => Changed(nameof(CanEditDesktopLyricSettings));
    internal MusicDesktopLyricsGeometry? DesktopLyricGeometry => desktopLyricGeometry;
    internal bool DesktopLyricSettingsLoaded => desktopLyricSettingsLoaded;

    private void BeginDesktopLyricSettingsWork()
    {
        desktopLyricSettingsPending++;
        desktopLyricSettingsBusy = true;
        DesktopLyricSettingsBindings();
    }

    private void EndDesktopLyricSettingsWork()
    {
        desktopLyricSettingsBusy = --desktopLyricSettingsPending > 0;
        DesktopLyricSettingsBindings();
    }

    private void AcceptDesktopLyricSettings(MusicDesktopLyricsRecord settings, bool preserveGeometryRequest = false)
    {
        long revision = settings.Revision;
        string layout = settings.Layout,
            normalColor = settings.NormalColor,
            highlightColor = settings.HighlightColor;
        MusicDesktopLyricsGeometry? geometry = null;
        if (settings.Geometry is { } saved)
        {
            MusicDto.Require(saved.Monitor.Length is > 0 and <= 32 && double.IsFinite(saved.X) && double.IsFinite(saved.Y) &&
                double.IsFinite(saved.Width) && saved.Width > 0 && double.IsFinite(saved.Height) && saved.Height > 0);
            geometry = new(saved.Monitor, saved.X, saved.Y, saved.Width, saved.Height);
        }
        MusicDto.Require(layout is "single" or "double");
        MusicDto.Require(AppearanceColors.TryParse(normalColor, out var normal));
        MusicDto.Require(AppearanceColors.TryParse(highlightColor, out var highlight));
        normalColor = AppearanceColors.Hex(normal);
        highlightColor = AppearanceColors.Hex(highlight);
        bool layoutChanged = desktopLyricLayout != layout,
            normalChanged = desktopLyricNormalColor != normalColor,
            highlightChanged = desktopLyricHighlightColor != highlightColor,
            geometryChanged = desktopLyricGeometry != geometry;
        desktopLyricSettingsRevision = revision;
        desktopLyricLayout = layout;
        desktopLyricNormalColor = normalColor;
        desktopLyricHighlightColor = highlightColor;
        desktopLyricGeometry = geometry;
        if (!preserveGeometryRequest && desktopLyricGeometryWrites == 0)
            desktopLyricGeometryRequest = geometry;
        desktopLyricSettingsLoaded = true;
        if (layoutChanged)
        {
            Changed(nameof(DesktopLyricLayout));
            lyricsPort.DesktopLyricBindings();
        }
        if (normalChanged) Changed(nameof(DesktopLyricNormalColor));
        if (highlightChanged) Changed(nameof(DesktopLyricHighlightColor));
        if (geometryChanged) Changed(nameof(DesktopLyricGeometry));
        DesktopLyricSettingsBindings();
    }

    internal Task EnsureDesktopLyricSettingsAsync()
    {
        if (desktopLyricSettingsLoaded) return desktopLyricSettingsWork;
        if (desktopLyricSettingsBusy) return desktopLyricSettingsWork;
        if (stopping || disposed) return Task.CompletedTask;
        BeginDesktopLyricSettingsWork();
        return desktopLyricSettingsWork = RunAsync(async () =>
        {
            try
            {
                var settings = await RunSettingReceiptAsync(MusicSettingReceiptKeys.DesktopLyricsWithGeometry,
                    ct => application.GetDesktopLyricsAsync(ct), lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested();
                AcceptDesktopLyricSettings(settings);
            }
            catch
            {
                desktopLyricSettingsLoaded = false;
                if (!stopping && !disposed) await Notify("桌面歌词设置读取失败");
            }
            finally
            {
                EndDesktopLyricSettingsWork();
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
        => SaveDesktopLyricSettingsAsync(layout, normalColor, highlightColor, null, saveGeometry: false);

    internal Task SaveDesktopLyricGeometryAsync(MusicDesktopLyricsGeometry geometry)
    {
        if (!desktopLyricSettingsLoaded || stopping || disposed) return Task.CompletedTask;
        if (desktopLyricGeometryRequest == geometry) return desktopLyricSettingsWork;
        desktopLyricGeometryRequest = geometry;
        desktopLyricGeometryWrites++;
        return SaveDesktopLyricSettingsAsync(null, null, null, geometry, saveGeometry: true);
    }

    private Task SaveDesktopLyricSettingsAsync(string? layout, string? normalColor, string? highlightColor,
        MusicDesktopLyricsGeometry? geometry, bool saveGeometry)
    {
        if (!desktopLyricSettingsLoaded || stopping || disposed) return Task.CompletedTask;
        BeginDesktopLyricSettingsWork();
        return desktopLyricSettingsWork = RunAsync(async () =>
        {
            try
            {
                string nextLayout = layout ?? desktopLyricLayout,
                    nextNormal = normalColor ?? desktopLyricNormalColor,
                    nextHighlight = highlightColor ?? desktopLyricHighlightColor;
                MusicDesktopLyricsGeometry? nextGeometry = saveGeometry ? geometry : desktopLyricGeometry;
                MusicDesktopLyricsGeometryRecord? nextGeometryRecord = nextGeometry is { } value
                    ? new MusicDesktopLyricsGeometryRecord(value.Monitor, value.X, value.Y, value.Width, value.Height)
                    : null;
                var descriptor = MusicSettingReceiptKeys.DesktopLyrics(nextGeometryRecord is not null);
                try
                {
                    var saved = await RunSettingReceiptAsync(descriptor,
                        ct => application.SaveDesktopLyricsAsync(desktopLyricSettingsRevision, nextLayout, nextNormal,
                            nextHighlight, nextGeometryRecord, nextGeometryRecord is not null, ct), lifetime.Token);
                    AcceptDesktopLyricSettings(saved, preserveGeometryRequest: desktopLyricGeometryWrites > 0);
                }
                catch (MusicOperationException error) when (error.Code is
                    "COMMIT_UNKNOWN" or "STORAGE_CONFLICT" or "PENDING_WRITE" or "REVISION_CONFLICT")
                {
                    var current = await RunSettingReceiptAsync(MusicSettingReceiptKeys.DesktopLyricsWithGeometry,
                        ct => application.GetDesktopLyricsAsync(ct), lifetime.Token);
                    AcceptDesktopLyricSettings(current, preserveGeometryRequest: desktopLyricGeometryWrites > 0);
                    bool requestedValuesChanged = layout is not null && desktopLyricLayout != nextLayout ||
                        normalColor is not null && desktopLyricNormalColor != nextNormal ||
                        highlightColor is not null && desktopLyricHighlightColor != nextHighlight ||
                        saveGeometry && desktopLyricGeometry != nextGeometry;
                    if (requestedValuesChanged)
                        await Notify(error.Code == "REVISION_CONFLICT" ? "桌面歌词设置已更新" :
                            saveGeometry ? "桌面歌词位置和大小保存失败" : "桌面歌词设置保存失败");
                }
            }
            catch
            {
                desktopLyricSettingsLoaded = false;
                if ((!stopping || saveGeometry) && !disposed)
                    await Notify(saveGeometry ? "桌面歌词位置和大小保存失败" : "桌面歌词设置保存失败");
            }
            finally
            {
                if (saveGeometry && --desktopLyricGeometryWrites == 0)
                    desktopLyricGeometryRequest = desktopLyricGeometry;
                EndDesktopLyricSettingsWork();
            }
        }, publishStatus: !saveGeometry);
    }
}
