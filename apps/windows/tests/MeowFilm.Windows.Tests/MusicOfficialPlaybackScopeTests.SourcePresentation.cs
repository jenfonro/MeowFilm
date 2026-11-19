using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Window = MeowFilm.Desktop.DesktopWindow;
using WindowState = MeowFilm.Desktop.DesktopWindowState;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MeowFilm.Threading;
using Microsoft.UI.Dispatching;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static partial class MusicOfficialPlaybackScopeTests
{
    private static IEnumerable<TestCase> SourcePresentationCases()
    {
        yield return Case("source panel shows accepted quality and source without requests and updates after track change", async () =>
        {
            await using var f = await Scene.Create();
            await f.Play(Song("first"), Song("next"));
            using var surface = new PlayerSurface(f.Module);
            int resolves = f.App.Resolutions.Count, plans = f.App.Plans.Count;
            await surface.OpenSources();
            var panel = (Border)surface.SourceDialog.Content;
            var view = (MusicPlayerView)f.Module.PlayerView;
            Check(((TextBlock)view.FindName("CurrentSourceSymbol")).Text == "SQ" &&
                ((TextBlock)view.FindName("CurrentSourceQuality")).Text == "无损" &&
                ((TextBlock)view.FindName("CurrentSourceDescription")).Text == "网易云音乐",
                "the current block presents the accepted quality and provider, not the track title or requested tier");
            Check(f.Module.SourceChoices.Where(choice => choice.Key != "auto" && choice.Selectable)
                .All(choice => choice.QualitySymbol == "SQ"));
            Check(f.App.Resolutions.Count == resolves && f.App.Plans.Count == plans && f.App.Completions.Count == 0,
                "presenting current quality and known rows must not search");
            Check(ReferenceEquals(((Button)view.FindName("AutomaticSourceButton")).Tag, f.Module.AutomaticSourceChoice),
                "automatic selection remains a real action with the same source choice identity");
            Check(ReferenceEquals(panel.Background, DesktopVisual.Resource(panel, "MeowFilm.Brush.FloatingSurface")) &&
                panel.CornerRadius == new CornerRadius(20));
            Check(panel.ActualHeight < 440, "short source lists do not retain a fixed tall blank dialog");
            await SourcePreview(panel, "context-source-known");

            f.App.MediaQuality = "320k";
            await f.SetQuality("320k");
            await f.Module.StepAsync(true);
            Check(!surface.SourceDialog.IsOpen, "a new track retires the previous source panel");
            await surface.OpenSources();
            Check(((TextBlock)view.FindName("CurrentSourceSymbol")).Text == "HQ" &&
                ((TextBlock)view.FindName("CurrentSourceQuality")).Text == "高音质",
                "the source badge and text publish the replacement receipt together");
        });
        yield return Case("source panel does not claim a quality badge for an unverified receipt", async () =>
        {
            await using var f = await Scene.Create();
            f.App.VerifiedQuality = false;
            await f.Play(Song("unverified"));
            using var surface = new PlayerSurface(f.Module);
            await surface.OpenSources();
            var view = (MusicPlayerView)f.Module.PlayerView;
            Check(f.Module.CurrentSourceQuality == "音质未知" && f.Module.CurrentSourceSymbol.Length == 0);
            Check(((TextBlock)view.FindName("CurrentSourceSymbol")).Text.Length == 0 &&
                ((TextBlock)view.FindName("CurrentSourceQuality")).Text == "音质未知");
            Check(f.Module.SourceChoices.Where(choice => choice.Key != "auto")
                .All(choice => choice.QualitySymbol.Length == 0),
                "requested lossless does not become an asserted SQ badge");
        });
        yield return Case("source panel empty loading and long-list layouts retain actions and contrast", async () =>
        {
            await using var f = await Scene.Create();
            await f.Play(Song("layout"));
            using var surface = new PlayerSurface(f.Module);
            await surface.OpenSources();
            var panel = (Border)surface.SourceDialog.Content;
            var view = (MusicPlayerView)f.Module.PlayerView;
            // Presentation-only data exercises an empty projection and a long list.
            // Live request, cancellation and receipt behavior is covered by the surrounding cases.
            var automatic = new MusicSourceChoice("auto", "自动选择", "", "", "", true, true);
            var rows = new ObservableCollection<MusicSourceChoice> { automatic };
            object Data(bool loading, bool confirmed) => new
            {
                CurrentSourceQuality = "高解析度", CurrentSourceSymbol = "HR",
                CurrentSourceDescription = "酷我 · 测试音源", SourceChoices = rows,
                HasConfirmedSourceChoices = confirmed, IsLoadingSources = loading, HasEmptySources = !confirmed && !loading,
                AutomaticSourceChoice = automatic, CanSwitchSource = true, CanLoadMoreSources = true
            };
            panel.DataContext = Data(false, false);
            await DesktopTestUi.DrainAsync();
            panel.UpdateLayout();
            var empty = (TextBlock)view.FindName("SourceEmptyState");
            var scroll = (ScrollViewer)view.FindName("SourceChoicesScroll");
            var more = (Button)view.FindName("LoadMoreSourcesButton");
            var auto = (Button)view.FindName("AutomaticSourceButton");
            Check(DesktopVisual.IsVisible(empty) && empty.Text == "暂无已确认可切换音源");
            Check(panel.ActualHeight < 360 && scroll.ActualHeight >= 80 && DesktopVisual.IsVisible(more) && DesktopVisual.IsVisible(auto));
            Check(auto.TransformToVisual(more).TransformPoint(new(auto.ActualWidth, 0)).X <= 0,
                "automatic selection and load more have separate hit targets");
            await SourcePreview(panel, "context-source-empty");
            panel.DataContext = Data(true, false);
            await DesktopTestUi.DrainAsync();
            Check(!DesktopVisual.IsVisible(empty), "an active search must not simultaneously claim a settled empty result");

            for (int i = 0; i < 20; i++)
                rows.Add(new("source-" + i, "测试音源 " + i, "酷我", "高解析度", i == 19 ? "不可用" : "可用",
                    false, i != 19) { QualitySymbol = i == 19 ? "" : "HR" });
            panel.DataContext = Data(false, true);
            await DesktopTestUi.DrainAsync();
            panel.UpdateLayout();
            Check(!DesktopVisual.IsVisible(empty) && scroll.ActualHeight <= 300 && scroll.ScrollableHeight > 0 &&
                more.TransformToVisual(panel).TransformPoint(new()).Y >= scroll.TransformToVisual(panel).TransformPoint(new(0, scroll.ActualHeight)).Y,
                "long lists scroll without covering the footer actions");
            Check(more.TransformToVisual(panel).TransformPoint(new(more.ActualWidth, more.ActualHeight)).Y <= panel.ActualHeight &&
                auto.TransformToVisual(panel).TransformPoint(new(auto.ActualWidth, auto.ActualHeight)).Y <= panel.ActualHeight,
                "both long-list footer actions remain fully inside the visible panel");
            await SourcePreview(panel, "context-source-list");
            DesktopTheme.SetIsHighContrast(panel, true);
            await DesktopTestUi.DrainAsync();
            Check(((SolidColorBrush)panel.Background).Color == new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.Window) && panel.Shadow is null);
            var unavailable = PlayerVisuals(panel).OfType<Button>()
                .Single(button => button.Tag is MusicSourceChoice { Key: "source-19" });
            Check(!unavailable.IsEnabled && ((SolidColorBrush)unavailable.Foreground).Color == new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.GrayText));
            Check(PlayerVisuals(unavailable).OfType<TextBlock>().All(text => SameBrush(text.Foreground, unavailable.Foreground)),
                "unavailable candidate text remains legible in system high contrast");
            panel.ClearValue(FrameworkElement.DataContextProperty);
        });
    }

    private static async Task SourcePreview(Border panel, string name)
    {
        if (Environment.GetEnvironmentVariable("MEOWFILM_SETTINGS_PREVIEW_ROOT") is not { } root) return;
        TestWorkspace.ValidatePath(TestWorkspace.DataRoot, root);
        System.IO.Directory.CreateDirectory(root);
        // Preserve the live native allocation while capturing each DPI raster.
        foreach (int dpi in new[] { 96, 144, 192 })
            await DesktopTestUi.Save(await DesktopTestUi.Render(panel, dpi / 96d),
                System.IO.Path.Combine(root, $"{name}-{dpi}.png"));
    }
}
