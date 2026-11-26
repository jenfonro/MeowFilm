using MeowFilm.Capabilities.Library;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

internal readonly record struct MusicDesktopLyricsGeometry(
    string Monitor, double X, double Y, double Width, double Height);

public sealed partial class MusicSettingsController
{
    private long desktopLyricRevision;
    private bool desktopLyricLoaded, desktopLyricBusy;
    private int desktopLyricPending;
    private Task desktopLyricWork = Task.CompletedTask;
    private MusicDesktopLyricsGeometry? desktopLyricGeometryRequest;
    private int desktopLyricGeometryWrites;
    private string desktopLyricLayout = "single";
    private string desktopLyricNormalColor = "#FFB5C5", desktopLyricHighlightColor = "#FFF2A8";
    private MusicDesktopLyricsGeometry? desktopLyricGeometry;

    public string DesktopLyricLayout => desktopLyricLayout;
    public string DesktopLyricNormalColor => desktopLyricNormalColor;
    public string DesktopLyricHighlightColor => desktopLyricHighlightColor;
    public bool CanEditDesktopLyricSettings => CanEdit && !disposed && desktopLyricLoaded && !desktopLyricBusy;
    internal MusicDesktopLyricsGeometry? DesktopLyricGeometry => desktopLyricGeometry;
    internal bool DesktopLyricSettingsLoaded => desktopLyricLoaded;

    private void BeginDesktopLyricWork()
    {
        desktopLyricPending++;
        desktopLyricBusy = true;
        Changed(nameof(CanEditDesktopLyricSettings));
    }

    private void EndDesktopLyricWork()
    {
        desktopLyricBusy = --desktopLyricPending > 0;
        Changed(nameof(CanEditDesktopLyricSettings));
    }

    private void AcceptDesktopLyrics(MusicDesktopLyricsRecord settings, bool preserveGeometryRequest = false)
    {
        MusicDesktopLyricsGeometry? nextGeometry = settings.Geometry is { } saved
            ? new(saved.Monitor, saved.X, saved.Y, saved.Width, saved.Height)
            : null;
        string nextLayout = settings.Layout;
        string nextNormal = AppearanceColors.TryParse(settings.NormalColor, out var normal) ? AppearanceColors.Hex(normal) : settings.NormalColor;
        string nextHighlight = AppearanceColors.TryParse(settings.HighlightColor, out var highlight) ? AppearanceColors.Hex(highlight) : settings.HighlightColor;
        bool layoutChanged = desktopLyricLayout != nextLayout,
            normalChanged = desktopLyricNormalColor != nextNormal,
            highlightChanged = desktopLyricHighlightColor != nextHighlight,
            geometryChanged = desktopLyricGeometry != nextGeometry;
        desktopLyricRevision = settings.Revision;
        desktopLyricLayout = nextLayout;
        desktopLyricNormalColor = nextNormal;
        desktopLyricHighlightColor = nextHighlight;
        desktopLyricGeometry = nextGeometry;
        if (!preserveGeometryRequest && desktopLyricGeometryWrites == 0)
            desktopLyricGeometryRequest = nextGeometry;
        desktopLyricLoaded = true;
        if (layoutChanged) Changed(nameof(DesktopLyricLayout));
        if (normalChanged) Changed(nameof(DesktopLyricNormalColor));
        if (highlightChanged) Changed(nameof(DesktopLyricHighlightColor));
        if (geometryChanged) Changed(nameof(DesktopLyricGeometry));
        Changed(nameof(CanEditDesktopLyricSettings));
    }

    internal Task EnsureDesktopLyricSettingsAsync()
    {
        if (desktopLyricLoaded || desktopLyricBusy) return desktopLyricWork;
        if (stopping || disposed) return Task.CompletedTask;
        BeginDesktopLyricWork();
        return desktopLyricWork = RunAsync(async () =>
        {
            try
            {
                var settings = await application.Library.GetDesktopLyricsAsync(lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested();
                AcceptDesktopLyrics(settings);
            }
            catch
            {
                desktopLyricLoaded = false;
                if (!stopping && !disposed) await Notify("桌面歌词设置读取失败");
            }
            finally { EndDesktopLyricWork(); }
        });
    }

    public Task SetDesktopLyricLayoutAsync(string nextLayout)
    {
        if (!CanEditDesktopLyricSettings) return Task.CompletedTask;
        if (nextLayout is not ("single" or "double")) return Notify("桌面歌词设置无效");
        if (nextLayout == desktopLyricLayout) return Task.CompletedTask;
        return SaveDesktopLyricsAsync(nextLayout, desktopLyricNormalColor, desktopLyricHighlightColor, null, saveGeometry: false);
    }

    public Task SetDesktopLyricColorAsync(string role, string value)
    {
        if (!CanEditDesktopLyricSettings) return Task.CompletedTask;
        if (role is not ("normal" or "highlight") || !AppearanceColors.TryParse(value, out var color))
            return Notify("桌面歌词设置无效");
        string nextNormal = role == "normal" ? AppearanceColors.Hex(color) : desktopLyricNormalColor,
            nextHighlight = role == "highlight" ? AppearanceColors.Hex(color) : desktopLyricHighlightColor;
        if (nextNormal == desktopLyricNormalColor && nextHighlight == desktopLyricHighlightColor) return Task.CompletedTask;
        return SaveDesktopLyricsAsync(desktopLyricLayout, nextNormal, nextHighlight, null, saveGeometry: false);
    }

    internal Task SaveDesktopLyricGeometryAsync(MusicDesktopLyricsGeometry nextGeometry)
    {
        if (!desktopLyricLoaded || stopping || disposed) return Task.CompletedTask;
        if (desktopLyricGeometryRequest == nextGeometry) return desktopLyricWork;
        desktopLyricGeometryRequest = nextGeometry;
        desktopLyricGeometryWrites++;
        return SaveDesktopLyricsAsync(null, null, null, nextGeometry, saveGeometry: true);
    }

    private Task SaveDesktopLyricsAsync(string? nextLayoutArg, string? nextNormalArg, string? nextHighlightArg,
        MusicDesktopLyricsGeometry? nextGeometryArg, bool saveGeometry)
    {
        if (!desktopLyricLoaded || stopping || disposed) return Task.CompletedTask;
        BeginDesktopLyricWork();
        return desktopLyricWork = RunAsync(async () =>
        {
            try
            {
                string nextLayout = nextLayoutArg ?? desktopLyricLayout,
                    nextNormal = nextNormalArg ?? desktopLyricNormalColor,
                    nextHighlight = nextHighlightArg ?? desktopLyricHighlightColor;
                MusicDesktopLyricsGeometry? nextGeometry = saveGeometry ? nextGeometryArg : desktopLyricGeometry;
                MusicDesktopLyricsGeometryRecord? nextGeometryRecord = nextGeometry is { } value
                    ? new MusicDesktopLyricsGeometryRecord(value.Monitor, value.X, value.Y, value.Width, value.Height)
                    : null;
                try
                {
                    var saved = await application.Library.SaveDesktopLyricsAsync(desktopLyricRevision, nextLayout, nextNormal,
                        nextHighlight, nextGeometryRecord, nextGeometryRecord is not null, lifetime.Token);
                    AcceptDesktopLyrics(saved, preserveGeometryRequest: desktopLyricGeometryWrites > 0);
                }
                catch (Exception error) when (ErrorCode(error) is "STORAGE_CONFLICT" or "REVISION_CONFLICT")
                {
                    var current = await application.Library.GetDesktopLyricsAsync(lifetime.Token);
                    AcceptDesktopLyrics(current, preserveGeometryRequest: desktopLyricGeometryWrites > 0);
                    bool requestedValuesChanged = nextLayoutArg is not null && desktopLyricLayout != nextLayout ||
                        nextNormalArg is not null && desktopLyricNormalColor != nextNormal ||
                        nextHighlightArg is not null && desktopLyricHighlightColor != nextHighlight ||
                        saveGeometry && desktopLyricGeometry != nextGeometry;
                    if (requestedValuesChanged)
                        await Notify(ErrorCode(error) == "REVISION_CONFLICT" ? "桌面歌词设置已更新" :
                            saveGeometry ? "桌面歌词位置和大小保存失败" : "桌面歌词设置保存失败");
                }
            }
            catch
            {
                desktopLyricLoaded = false;
                if ((!stopping || saveGeometry) && !disposed)
                    await Notify(saveGeometry ? "桌面歌词位置和大小保存失败" : "桌面歌词设置保存失败");
            }
            finally
            {
                if (saveGeometry && --desktopLyricGeometryWrites == 0)
                    desktopLyricGeometryRequest = desktopLyricGeometry;
                EndDesktopLyricWork();
            }
        }, publishStatus: !saveGeometry);
    }
}
