using System.Text.Json;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;

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

public sealed partial class MusicSettingsController
{
    private long appearanceRevision, appearanceGeneration;
    private bool appearanceLoaded, appearanceBusy;
    private Task appearanceWork = Task.CompletedTask;
    private bool AppearanceCurrent(long generation) =>
        generation == appearanceGeneration && !stopping && !disposed;

    internal void RetireAppearanceSettings()
    {
        appearanceGeneration++;
        appearanceLoaded = false;
    }

    private string savedPlaylistView = "grid", savedColorMode = "song", savedColorPalette = "lilac";
    private string appearanceStatus = "";
    public MusicCatalogChoice[] ColorModes { get; } =
        [new("song", "跟随歌曲"), new("fixed", "固定配色")];
    public MusicCatalogChoice[] PlaylistViews { get; } =
        [new("grid", "封面网格"), new("list", "封面列表")];
    public string AppearanceColorMode => savedColorMode;
    public string AppearanceColorPalette => savedColorPalette;
    public string AppearancePlaylistView => savedPlaylistView;
    public bool PlaylistListView => savedPlaylistView == "list";
    public bool CanEditFixedAppearance => CanEditAppearance && AppearanceColorMode == "fixed";
    public bool CanEditAppearance => CanEdit && appearanceLoaded && !appearanceBusy;
    public string AppearanceStatus => appearanceStatus;

    internal Brush PlayerPalette(Brush cover) => savedColorMode == "fixed"
        ? MusicAppearancePalette.Brush(savedColorPalette) : cover;

    internal void SetAppearanceReadFailed()
    {
        appearanceStatus = "外观设置读取失败";
        AppearanceBindings();
    }

    internal void AppearanceBindings()
    {
        Changed(nameof(CanEditAppearance));
        Changed(nameof(CanEditFixedAppearance));
        Changed(nameof(AppearanceStatus));
        Changed(nameof(AppearanceColorMode));
        Changed(nameof(AppearanceColorPalette));
        Changed(nameof(AppearancePlaylistView));
    }

    private void AcceptAppearance(JsonElement reply)
    {
        var value = reply.GetProperty("settings");
        appearanceRevision = MusicDto.Integer(value, "revision");
        savedPlaylistView = MusicDto.Text(value, "playlistView");
        savedColorMode = MusicDto.Text(value, "colorMode");
        savedColorPalette = MusicDto.Text(value, "colorPalette");
        appearanceLoaded = true;
        appearanceStatus = "";
        Changed(nameof(PlaylistListView));
        AppearanceBindings();
        ctx.ReapplyAppearance();
    }

    internal async Task ReadAppearance(CancellationToken token)
    {
        long generation = appearanceGeneration;
        var reply = await CallSettledReceipt("music.appearance.get", null, token);
        token.ThrowIfCancellationRequested();
        if (AppearanceCurrent(generation)) AcceptAppearance(reply);
    }

    internal async Task EnsureAppearanceAsync()
    {
        long generation = appearanceGeneration;
        await appearanceWork;
        if (AppearanceCurrent(generation) && !appearanceLoaded) await ReloadAppearanceAsync();
    }

    internal Task ReloadAppearanceAsync()
    {
        if (appearanceBusy || stopping || disposed) return Task.CompletedTask;
        appearanceBusy = true;
        long generation = appearanceGeneration;
        AppearanceBindings();
        return appearanceWork = RunAsync(async () =>
        {
            try { await ReadAppearance(lifetime.Token); }
            catch { if (AppearanceCurrent(generation)) appearanceStatus = "外观设置读取失败"; }
            finally { appearanceBusy = false; if (AppearanceCurrent(generation)) AppearanceBindings(); }
        });
    }

    private bool AppearanceMatches(string playlistView, string colorMode, string colorPalette) =>
        savedPlaylistView == playlistView && savedColorMode == colorMode &&
        savedColorPalette == colorPalette;

    public Task SetAppearanceAsync(string setting, string value)
    {
        if (!CanEditAppearance) return Task.CompletedTask;
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
        var draft = new
        {
            ifRevision = appearanceRevision,
            playerTheme = "starlight",
            playlistView,
            colorMode,
            colorPalette
        };
        appearanceBusy = true;
        long generation = appearanceGeneration;
        AppearanceBindings();
        return appearanceWork = RunAsync(async () =>
        {
            try
            {
                var reply = await CallSettledReceipt("music.appearance.save", draft, lifetime.Token);
                if (AppearanceCurrent(generation)) AcceptAppearance(reply);
            }
            catch (MusicOperationException error) when (error.Code is
                "COMMIT_UNKNOWN" or "STORAGE_CONFLICT" or "PENDING_WRITE" or "REVISION_CONFLICT")
            {
                if (!AppearanceCurrent(generation)) return;
                try
                {
                    var reply = await CallSettledReceipt("music.appearance.get", null, lifetime.Token);
                    if (!AppearanceCurrent(generation)) return;
                    AcceptAppearance(reply);
                    if (!AppearanceMatches(playlistView, colorMode, colorPalette))
                        appearanceStatus = error.Code == "REVISION_CONFLICT"
                            ? "外观设置已更新"
                            : "外观设置保存失败";
                }
                catch
                {
                    if (!AppearanceCurrent(generation)) return;
                    appearanceLoaded = false;
                    appearanceStatus = "外观设置保存失败";
                }
            }
            catch
            {
                if (!AppearanceCurrent(generation)) return;
                appearanceLoaded = false;
                appearanceStatus = "外观设置保存失败";
            }
            finally
            {
                appearanceBusy = false;
                if (AppearanceCurrent(generation)) AppearanceBindings();
            }
        });
    }
}
