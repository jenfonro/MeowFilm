using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

// Visual instances and their resources belong to the presentation session, not to
// a content page. Reading/restoring application state never constructs a view.
internal sealed class MusicPresentation
{
    private readonly MusicCapability module;
    private MusicView? page;
    private MusicSettingsView? settings;
    private MusicAppearanceView? appearance;
    private MusicHomeSourceSettingsView? homeSourceSettings;
    private MusicCacheSettingsView? cacheSettings;
    private MusicPlayerView? player;
    private MusicPlayerDetailView? detail;
    private MusicQueueView? queue;
    private Brush playerSample = Brushes.DimGray;
    private double scroll;
    private bool restorePending;

    internal MusicPresentation(MusicCapability module)
    {
        this.module = module;
        Dispatcher = Dispatcher.CurrentDispatcher;
        CreatePlaylistAction = new("创建歌单", () => Page.OpenCreatePlaylistDialog(),
            () => module.CanEdit && page?.IsPlaylistDialogOpen != true);
    }

    internal Dispatcher Dispatcher { get; }
    internal MusicView Page => page ??= new(module);
    internal MusicSettingsView Settings => settings ??= new(module);
    internal MusicAppearanceView Appearance => appearance ??= new(module);
    internal MusicHomeSourceSettingsView HomeSourceSettings => homeSourceSettings ??= new(module);
    internal MusicCacheSettingsView CacheSettings => cacheSettings ??= new(module);
    internal MusicPlayerView Player => player ??= new(module);
    internal MusicPlayerDetailView Detail => detail ??= new(module);
    internal MusicQueueView Queue => queue ??= new(module);
    internal bool IsSettingsActive => settings is { IsLoaded: true, IsVisible: true };
    internal bool IsPageActive => page is { IsLoaded: true, IsVisible: true };
    internal DesktopNavigationGroupAction CreatePlaylistAction { get; }
    internal Brush PlayerBackground { get; private set; } = MusicPlayerArtwork.Backdrop(Brushes.DimGray);
    internal Brush PlayerProgress { get; private set; } = MusicPlayerArtwork.Progress(Brushes.DimGray);

    internal double CaptureScroll() => page?.CaptureScroll() ?? scroll;
    internal void RestoreScroll(double value)
    {
        scroll = value;
        restorePending = true;
        if (IsPageActive)
        {
            page!.RestoreScroll(value);
            restorePending = false;
        }
    }
    internal void PageLoaded()
    {
        if (!restorePending || page is null) return;
        page.RestoreScroll(scroll);
        restorePending = false;
    }
    internal void DeactivatePlayer() => player?.Deactivate();
    internal void DeactivateDetail() => detail?.Deactivate();
    internal void ResetSearchPresentation() => page?.ResetSearchPresentation();

    internal void RefreshAppearance(Brush sampled, bool animate = true)
    {
        playerSample = sampled;
        var palette = module.PlayerPalette(sampled);
        var next = (GradientBrush)MusicPlayerArtwork.Backdrop(palette);
        var prior = PlayerBackground as GradientBrush;
        if (animate && (page?.IsVisible == true || player?.IsVisible == true || detail?.IsVisible == true) &&
            SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast &&
            prior is not null && prior.GradientStops.Count == next.GradientStops.Count)
        {
            next = next.Clone();
            for (int i = 0; i < next.GradientStops.Count; i++)
                next.GradientStops[i].BeginAnimation(GradientStop.ColorProperty,
                    new ColorAnimation(prior.GradientStops[i].Color, next.GradientStops[i].Color,
                        TimeSpan.FromMilliseconds(300)) { FillBehavior = FillBehavior.Stop });
        }
        PlayerBackground = next;
        PlayerProgress = MusicPlayerArtwork.Progress(palette);
        module.RefreshPlayerBackground();
    }
    internal void ReapplyAppearance() => RefreshAppearance(playerSample);
}
