using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;
using System.Windows.Threading;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;

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
    private Brush playerSample = MusicVisuals.Brush("#696969");
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
    private T WithArtwork<T>(T view) where T : FrameworkElement
    { ArtworkScope.SetLoader(view, module.ArtworkLoader); return view; }
    internal MusicView Page => page ??= WithArtwork(new MusicView(module));
    internal MusicSettingsView Settings => settings ??= WithArtwork(new MusicSettingsView(module));
    internal MusicAppearanceView Appearance => appearance ??= WithArtwork(new MusicAppearanceView(module));
    internal MusicHomeSourceSettingsView HomeSourceSettings => homeSourceSettings ??= WithArtwork(new MusicHomeSourceSettingsView(module));
    internal MusicCacheSettingsView CacheSettings => cacheSettings ??= WithArtwork(new MusicCacheSettingsView(module));
    internal MusicPlayerView Player => player ??= WithArtwork(new MusicPlayerView(module));
    internal MusicPlayerDetailView Detail => detail ??= WithArtwork(new MusicPlayerDetailView(module));
    internal MusicQueueView Queue => queue ??= WithArtwork(new MusicQueueView(module));
    internal bool IsSettingsActive => settings is { IsLoaded: true, Visibility: Visibility.Visible };
    internal bool IsPageActive => page is { IsLoaded: true, Visibility: Visibility.Visible };
    internal DesktopNavigationGroupAction CreatePlaylistAction { get; }
    internal Brush PlayerBackground { get; private set; } = MusicPlayerArtwork.Backdrop(MusicVisuals.Brush("#696969"));
    internal Brush PlayerProgress { get; private set; } = MusicPlayerArtwork.Progress(MusicVisuals.Brush("#696969"));

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
    private Color[]? backdropColors;
    private Storyboard? backdropAnimation;

    internal void RefreshAppearance(Brush sampled, bool animate = true)
    {
        playerSample = sampled;
        var palette = module.PlayerPalette(sampled);
        var next = (RadialGradientBrush)MusicPlayerArtwork.Backdrop(palette);
        var prior = PlayerBackground as RadialGradientBrush;
        var colors = next.GradientStops.Select(stop => stop.Color).ToArray();
        bool colorsChanged = !(backdropColors ?? prior?.GradientStops.Select(stop => stop.Color).ToArray() ?? [])
            .SequenceEqual(colors);
        if (colorsChanged)
        {
            var visibleColors = prior?.GradientStops.Select(stop => stop.Color).ToArray();
            backdropAnimation?.Stop(); backdropAnimation = null;
            backdropColors = colors;
            PlayerBackground = next;
            if (animate && (page is not null && DesktopVisual.IsVisible(page) ||
                player is not null && DesktopVisual.IsVisible(player) || detail is not null && DesktopVisual.IsVisible(detail)) &&
                MusicVisuals.Animate &&
                visibleColors is not null && visibleColors.Length == colors.Length)
            {
                var storyboard = new Storyboard();
                for (int i = 0; i < next.GradientStops.Count; i++)
                {
                    var animation = new ColorAnimation { From = visibleColors[i], To = colors[i],
                        Duration = TimeSpan.FromMilliseconds(300), EnableDependentAnimation = true };
                    Storyboard.SetTarget(animation, next.GradientStops[i]); Storyboard.SetTargetProperty(animation, "Color");
                    storyboard.Children.Add(animation);
                }
                backdropAnimation = storyboard;
                storyboard.Completed += (_, _) =>
                { if (ReferenceEquals(backdropAnimation, storyboard)) backdropAnimation = null; };
                storyboard.Begin();
            }
        }
        PlayerProgress = MusicPlayerArtwork.Progress(palette);
        module.RefreshPlayerBackground();
    }
    internal void ReapplyAppearance() => RefreshAppearance(playerSample);
}
