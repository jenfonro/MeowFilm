using MeowFilm.Capabilities.Library;
using MeowFilm.Desktop;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Capabilities;

public sealed record MusicAppearancePalette(string Key, string Name, string Color)
{
    public static IReadOnlyList<MusicAppearancePalette> Options { get; } = Array.AsReadOnly(new[]
    {
        new MusicAppearancePalette("lilac", "星紫", "#D7D1F7"),
        new MusicAppearancePalette("coral", "桃雾", "#EFCFD0"),
        new MusicAppearancePalette("mint", "薄荷", "#E1FCED"),
        new MusicAppearancePalette("silver", "银霜", "#DAD8D9"),
        new MusicAppearancePalette("lemon", "暖柠", "#FCF7D7"),
        new MusicAppearancePalette("rose", "蔷薇", "#F0D0E7"),
        new MusicAppearancePalette("periwinkle", "雾蓝", "#DAE1FB"),
        new MusicAppearancePalette("peach", "杏橙", "#F6E7D4"),
        new MusicAppearancePalette("sky", "晴空", "#D7E8FA")
    });

    public static Brush Brush(string key)
    {
        var value = ColorValue(key);
        AppearanceColors.TryParse(value, out var color);
        return new SolidColorBrush(color);
    }
    public static bool IsValid(string key) =>
        Options.Any(p => p.Key == key) || AppearanceColors.TryParse(key, out _);
    public static string ColorValue(string key) =>
        Options.FirstOrDefault(p => p.Key == key)?.Color ??
        (AppearanceColors.TryParse(key, out var color) ? AppearanceColors.Hex(color) : Options[0].Color);
}

/// <summary>
/// Standalone domain handler for music player color palette and playlist view preferences.
/// </summary>
internal sealed class MusicAppearanceSettings(MusicRuntimeContext ctx, Action<string> notify)
{
    private long revision, generation;
    private bool loaded, busy;
    private Task work = Task.CompletedTask;
    private string savedPlaylistView = "grid", savedColorMode = "song", savedColorPalette = "lilac";
    private string status = "";

    private bool IsCurrent(long currentGeneration) =>
        currentGeneration == generation && !ctx.Stopping && !ctx.Disposed;

    internal MusicCatalogChoice[] ColorModes { get; } =
        [new("song", "跟随歌曲"), new("fixed", "固定配色")];
    internal MusicCatalogChoice[] PlaylistViews { get; } =
        [new("grid", "封面网格"), new("list", "封面列表")];
    internal string ColorMode => savedColorMode;
    internal string ColorPalette => savedColorPalette;
    internal string PlaylistView => savedPlaylistView;
    internal bool PlaylistListView => savedPlaylistView == "list";
    internal bool CanEditFixed => CanEdit && ColorMode == "fixed";
    internal bool CanEdit => ctx.CanEdit && loaded && !busy;
    internal string Status => status;

    internal Brush PlayerPalette(Brush cover) => savedColorMode == "fixed"
        ? MusicAppearancePalette.Brush(savedColorPalette) : cover;

    internal void Retire()
    {
        generation++;
        loaded = false;
    }

    internal void SetReadFailed()
    {
        status = "外观设置读取失败";
        NotifyBindings();
    }

    internal void NotifyBindings()
    {
        notify(nameof(MusicSettingsController.CanEditAppearance));
        notify(nameof(MusicSettingsController.CanEditFixedAppearance));
        notify(nameof(MusicSettingsController.AppearanceStatus));
        notify(nameof(MusicSettingsController.AppearanceColorMode));
        notify(nameof(MusicSettingsController.AppearanceColorPalette));
        notify(nameof(MusicSettingsController.AppearancePlaylistView));
    }

    private void Accept(MusicAppearanceRecord value)
    {
        revision = value.Revision;
        savedPlaylistView = value.PlaylistView;
        savedColorMode = value.ColorMode;
        savedColorPalette = value.ColorPalette;
        loaded = true;
        status = "";
        notify(nameof(MusicSettingsController.PlaylistListView));
        NotifyBindings();
        ctx.ReapplyAppearance();
    }

    internal async Task ReadAsync(CancellationToken token)
    {
        long currentGeneration = generation;
        var settings = await ctx.RunSettingReceiptAsync(MusicSettingReceiptKeys.Appearance,
            ct => ctx.Application.GetAppearanceAsync(ct), token);
        token.ThrowIfCancellationRequested();
        if (IsCurrent(currentGeneration)) Accept(settings);
    }

    internal async Task EnsureAsync()
    {
        long currentGeneration = generation;
        await work;
        if (IsCurrent(currentGeneration) && !loaded) await ReloadAsync();
    }

    internal Task ReloadAsync()
    {
        if (busy || ctx.Stopping || ctx.Disposed) return Task.CompletedTask;
        busy = true;
        long currentGeneration = generation;
        NotifyBindings();
        return work = ctx.RunAsync(async () =>
        {
            try { await ReadAsync(ctx.Lifetime.Token); }
            catch { if (IsCurrent(currentGeneration)) status = "外观设置读取失败"; }
            finally { busy = false; if (IsCurrent(currentGeneration)) NotifyBindings(); }
        });
    }

    private bool Matches(string playlistView, string colorMode, string colorPalette) =>
        savedPlaylistView == playlistView && savedColorMode == colorMode &&
        savedColorPalette == colorPalette;

    internal Task SetAsync(string setting, string value)
    {
        if (!CanEdit) return Task.CompletedTask;
        string playlistView = savedPlaylistView, colorMode = savedColorMode, colorPalette = savedColorPalette;
        switch (setting)
        {
            case "playlistView" when PlaylistViews.Any(option => option.Key == value):
                playlistView = value;
                break;
            case "colorMode" when ColorModes.Any(option => option.Key == value):
                colorMode = value;
                break;
            case "colorPalette" when MusicAppearancePalette.IsValid(value):
                colorPalette = AppearanceColors.TryParse(value, out var color) ? AppearanceColors.Hex(color) : value;
                break;
            default:
                return Task.CompletedTask;
        }
        if (playlistView == savedPlaylistView && colorMode == savedColorMode &&
            colorPalette == savedColorPalette) return Task.CompletedTask;
        long expectedRevision = revision;
        busy = true;
        long currentGeneration = generation;
        NotifyBindings();
        return work = ctx.RunAsync(async () =>
        {
            try
            {
                var saved = await ctx.RunSettingReceiptAsync(MusicSettingReceiptKeys.Appearance,
                    ct => ctx.Application.SaveAppearanceAsync(expectedRevision, playlistView, "starlight", colorMode, colorPalette, ct),
                    ctx.Lifetime.Token);
                if (IsCurrent(currentGeneration)) Accept(saved);
            }
            catch (MusicOperationException error) when (error.Code is
                "COMMIT_UNKNOWN" or "STORAGE_CONFLICT" or "PENDING_WRITE" or "REVISION_CONFLICT")
            {
                if (!IsCurrent(currentGeneration)) return;
                try
                {
                    var current = await ctx.RunSettingReceiptAsync(MusicSettingReceiptKeys.Appearance,
                        ct => ctx.Application.GetAppearanceAsync(ct), ctx.Lifetime.Token);
                    if (!IsCurrent(currentGeneration)) return;
                    Accept(current);
                    if (!Matches(playlistView, colorMode, colorPalette))
                        status = error.Code == "REVISION_CONFLICT"
                            ? "外观设置已更新"
                            : "外观设置保存失败";
                }
                catch
                {
                    if (!IsCurrent(currentGeneration)) return;
                    loaded = false;
                    status = "外观设置保存失败";
                }
            }
            catch
            {
                if (!IsCurrent(currentGeneration)) return;
                loaded = false;
                status = "外观设置保存失败";
            }
            finally
            {
                busy = false;
                if (IsCurrent(currentGeneration)) NotifyBindings();
            }
        });
    }
}
