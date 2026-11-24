using MeowFilm.Capabilities.Library;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

internal readonly record struct MusicDesktopLyricsGeometry(
    string Monitor, double X, double Y, double Width, double Height);

/// <summary>
/// Standalone domain handler for desktop lyrics layout, color, and window geometry persistence.
/// </summary>
internal sealed class MusicDesktopLyricsSettings(MusicRuntimeContext ctx, Action<string> notify)
{
    private long revision;
    private bool loaded, busy;
    private int pending;
    private Task work = Task.CompletedTask;
    private MusicDesktopLyricsGeometry? geometryRequest;
    private int geometryWrites;
    private string layout = "single";
    private string normalColor = "#FFB5C5", highlightColor = "#FFF2A8";
    private MusicDesktopLyricsGeometry? geometry;

    internal event Action? LayoutChanged;

    internal string Layout => layout;
    internal string NormalColor => normalColor;
    internal string HighlightColor => highlightColor;
    internal bool CanEdit => ctx.CanEdit && !ctx.Disposed && loaded && !busy;
    internal MusicDesktopLyricsGeometry? Geometry => geometry;
    internal bool Loaded => loaded;

    internal void NotifyBindings() => notify(nameof(MusicSettingsController.CanEditDesktopLyricSettings));

    private void BeginWork()
    {
        pending++;
        busy = true;
        NotifyBindings();
    }

    private void EndWork()
    {
        busy = --pending > 0;
        NotifyBindings();
    }

    private void Accept(MusicDesktopLyricsRecord settings, bool preserveGeometryRequest = false)
    {
        long nextRevision = settings.Revision;
        string nextLayout = settings.Layout,
            nextNormal = settings.NormalColor,
            nextHighlight = settings.HighlightColor;
        MusicDesktopLyricsGeometry? nextGeometry = null;
        if (settings.Geometry is { } saved)
        {
            MusicDto.Require(saved.Monitor.Length is > 0 and <= 32 && double.IsFinite(saved.X) && double.IsFinite(saved.Y) &&
                double.IsFinite(saved.Width) && saved.Width > 0 && double.IsFinite(saved.Height) && saved.Height > 0);
            nextGeometry = new(saved.Monitor, saved.X, saved.Y, saved.Width, saved.Height);
        }
        MusicDto.Require(nextLayout is "single" or "double");
        MusicDto.Require(AppearanceColors.TryParse(nextNormal, out var normal));
        MusicDto.Require(AppearanceColors.TryParse(nextHighlight, out var highlight));
        nextNormal = AppearanceColors.Hex(normal);
        nextHighlight = AppearanceColors.Hex(highlight);
        bool layoutChanged = layout != nextLayout,
            normalChanged = normalColor != nextNormal,
            highlightChanged = highlightColor != nextHighlight,
            geometryChanged = geometry != nextGeometry;
        revision = nextRevision;
        layout = nextLayout;
        normalColor = nextNormal;
        highlightColor = nextHighlight;
        geometry = nextGeometry;
        if (!preserveGeometryRequest && geometryWrites == 0)
            geometryRequest = nextGeometry;
        loaded = true;
        if (layoutChanged)
        {
            notify(nameof(MusicSettingsController.DesktopLyricLayout));
            LayoutChanged?.Invoke();
        }
        if (normalChanged) notify(nameof(MusicSettingsController.DesktopLyricNormalColor));
        if (highlightChanged) notify(nameof(MusicSettingsController.DesktopLyricHighlightColor));
        if (geometryChanged) notify(nameof(MusicSettingsController.DesktopLyricGeometry));
        NotifyBindings();
    }

    internal Task EnsureAsync()
    {
        if (loaded || busy) return work;
        if (ctx.Stopping || ctx.Disposed) return Task.CompletedTask;
        BeginWork();
        return work = ctx.RunAsync(async () =>
        {
            try
            {
                var settings = await ctx.RunSettingReceiptAsync(MusicSettingReceiptKeys.DesktopLyricsWithGeometry,
                    ct => ctx.Application.GetDesktopLyricsAsync(ct), ctx.Lifetime.Token);
                ctx.Lifetime.Token.ThrowIfCancellationRequested();
                Accept(settings);
            }
            catch
            {
                loaded = false;
                if (!ctx.Stopping && !ctx.Disposed) await ctx.Notify("桌面歌词设置读取失败");
            }
            finally
            {
                EndWork();
            }
        });
    }

    internal Task SetLayoutAsync(string nextLayout)
    {
        if (!CanEdit) return Task.CompletedTask;
        if (nextLayout is not ("single" or "double")) return ctx.Notify("桌面歌词设置无效");
        if (nextLayout == layout) return Task.CompletedTask;
        return SaveAsync(nextLayout, normalColor, highlightColor, null, saveGeometry: false);
    }

    internal Task SetColorAsync(string role, string value)
    {
        if (!CanEdit) return Task.CompletedTask;
        if (role is not ("normal" or "highlight") || !AppearanceColors.TryParse(value, out var color))
            return ctx.Notify("桌面歌词设置无效");
        string nextNormal = role == "normal" ? AppearanceColors.Hex(color) : normalColor,
            nextHighlight = role == "highlight" ? AppearanceColors.Hex(color) : highlightColor;
        if (nextNormal == normalColor && nextHighlight == highlightColor) return Task.CompletedTask;
        return SaveAsync(layout, nextNormal, nextHighlight, null, saveGeometry: false);
    }

    internal Task SaveGeometryAsync(MusicDesktopLyricsGeometry nextGeometry)
    {
        if (!loaded || ctx.Stopping || ctx.Disposed) return Task.CompletedTask;
        if (geometryRequest == nextGeometry) return work;
        geometryRequest = nextGeometry;
        geometryWrites++;
        return SaveAsync(null, null, null, nextGeometry, saveGeometry: true);
    }

    private Task SaveAsync(string? nextLayoutArg, string? nextNormalArg, string? nextHighlightArg,
        MusicDesktopLyricsGeometry? nextGeometryArg, bool saveGeometry)
    {
        if (!loaded || ctx.Stopping || ctx.Disposed) return Task.CompletedTask;
        BeginWork();
        return work = ctx.RunAsync(async () =>
        {
            try
            {
                string nextLayout = nextLayoutArg ?? layout,
                    nextNormal = nextNormalArg ?? normalColor,
                    nextHighlight = nextHighlightArg ?? highlightColor;
                MusicDesktopLyricsGeometry? nextGeometry = saveGeometry ? nextGeometryArg : geometry;
                MusicDesktopLyricsGeometryRecord? nextGeometryRecord = nextGeometry is { } value
                    ? new MusicDesktopLyricsGeometryRecord(value.Monitor, value.X, value.Y, value.Width, value.Height)
                    : null;
                var descriptor = MusicSettingReceiptKeys.DesktopLyrics(nextGeometryRecord is not null);
                try
                {
                    var saved = await ctx.RunSettingReceiptAsync(descriptor,
                        ct => ctx.Application.SaveDesktopLyricsAsync(revision, nextLayout, nextNormal,
                            nextHighlight, nextGeometryRecord, nextGeometryRecord is not null, ct), ctx.Lifetime.Token);
                    Accept(saved, preserveGeometryRequest: geometryWrites > 0);
                }
                catch (MusicOperationException error) when (error.Code is
                    "COMMIT_UNKNOWN" or "STORAGE_CONFLICT" or "PENDING_WRITE" or "REVISION_CONFLICT")
                {
                    var current = await ctx.RunSettingReceiptAsync(MusicSettingReceiptKeys.DesktopLyricsWithGeometry,
                        ct => ctx.Application.GetDesktopLyricsAsync(ct), ctx.Lifetime.Token);
                    Accept(current, preserveGeometryRequest: geometryWrites > 0);
                    bool requestedValuesChanged = nextLayoutArg is not null && layout != nextLayout ||
                        nextNormalArg is not null && normalColor != nextNormal ||
                        nextHighlightArg is not null && highlightColor != nextHighlight ||
                        saveGeometry && geometry != nextGeometry;
                    if (requestedValuesChanged)
                        await ctx.Notify(error.Code == "REVISION_CONFLICT" ? "桌面歌词设置已更新" :
                            saveGeometry ? "桌面歌词位置和大小保存失败" : "桌面歌词设置保存失败");
                }
            }
            catch
            {
                loaded = false;
                if ((!ctx.Stopping || saveGeometry) && !ctx.Disposed)
                    await ctx.Notify(saveGeometry ? "桌面歌词位置和大小保存失败" : "桌面歌词设置保存失败");
            }
            finally
            {
                if (saveGeometry && --geometryWrites == 0)
                    geometryRequest = geometry;
                EndWork();
            }
        }, publishStatus: !saveGeometry);
    }
}
