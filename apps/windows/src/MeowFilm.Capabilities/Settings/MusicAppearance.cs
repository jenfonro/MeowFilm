using System.Text.Json;
using System.Windows.Media;

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
        var color = Options.FirstOrDefault(p => p.Key == key) ?? Options[0];
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color.Color));
        brush.Freeze();
        return brush;
    }
}

public sealed partial class MusicCapability
{
    private long appearanceRevision;
    private bool appearanceLoaded, appearanceBusy;
    private string savedPlaylistView = "grid", savedColorMode = "song", savedColorPalette = "lilac";
    private string appearanceStatus = "";
    public IReadOnlyList<MusicAppearancePalette> ColorPalettes => MusicAppearancePalette.Options;
    public MusicCatalogChoice[] ColorModes { get; } =
        [new("song", "跟随歌曲"), new("fixed", "固定配色")];
    public MusicCatalogChoice[] PlaylistViews { get; } =
        [new("grid", "封面网格"), new("list", "封面列表")];
    public string AppearanceColorMode => savedColorMode;
    public string AppearanceColorPalette => savedColorPalette;
    public string AppearancePlaylistView => savedPlaylistView;
    public bool PlaylistListView => savedPlaylistView == "list";
    public bool CanEditAppearance => CanEdit && appearanceLoaded && !appearanceBusy;
    public string AppearanceStatus => appearanceStatus;
    internal Brush PlayerPalette(Brush cover) => savedColorMode == "fixed"
        ? MusicAppearancePalette.Brush(savedColorPalette) : cover;

    internal void AppearanceBindings()
    {
        Changed(nameof(CanEditAppearance));
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
        page.ReapplyAppearance();
    }

    private async Task ReadAppearance(CancellationToken token)
    {
        var reply = await QualityCall("music.appearance.get", null, token);
        token.ThrowIfCancellationRequested();
        AcceptAppearance(reply);
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
            case "colorPalette" when ColorPalettes.Any(option => option.Key == value):
                colorPalette = value;
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
        AppearanceBindings();
        return RunAsync(async () =>
        {
            try
            {
                AcceptAppearance(await QualityCall("music.appearance.save", draft, lifetime.Token));
            }
            catch (MusicOperationException error) when (error.Code is
                "COMMIT_UNKNOWN" or "STORAGE_CONFLICT" or "PENDING_WRITE" or "REVISION_CONFLICT")
            {
                try
                {
                    AcceptAppearance(await QualityCall("music.appearance.get", null, lifetime.Token));
                    if (!AppearanceMatches(playlistView, colorMode, colorPalette))
                        appearanceStatus = error.Code == "REVISION_CONFLICT"
                            ? "外观设置已更新"
                            : "外观设置保存失败";
                }
                catch
                {
                    appearanceLoaded = false;
                    appearanceStatus = "外观设置保存失败";
                }
            }
            catch
            {
                appearanceLoaded = false;
                appearanceStatus = "外观设置保存失败";
            }
            finally
            {
                appearanceBusy = false;
                AppearanceBindings();
            }
        });
    }
}
