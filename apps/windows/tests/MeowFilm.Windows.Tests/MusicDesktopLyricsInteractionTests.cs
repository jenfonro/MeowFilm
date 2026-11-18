using SystemParameters = System.Windows.SystemParameters;
using Colors = Microsoft.UI.Colors;
using Color = global::Windows.UI.Color;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Window = MeowFilm.Desktop.DesktopWindow;
using WindowState = MeowFilm.Desktop.DesktopWindowState;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class MusicDesktopLyricsInteractionTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.DesktopLyricsInteractionCases();
}

public static partial class MusicUiTests
{
    private const string DesktopHitTestCase = "desktop lyrics OS hit testing crosses a process boundary";
    private const string DesktopHitPipeVariable = "MEOWFILM_DESKTOP_LYRIC_HIT_PIPE";

    internal static IEnumerable<TestCase> DesktopLyricsInteractionCases()
    {
        yield return Case("desktop lyric toggles during settings load cancel stale opens without losing a later open request", () =>
            WithDesktopLyrics(async (f, _) =>
            {
                var settings = f.Calls.HoldNext("music.lyrics.desktop.settings.get");
                var canceledOpen = f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                await settings.Arrived.Task;
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                True(!f.Module.IsDesktopLyricsOpen, "the second toggle cancels the pending first open");
                var requestedOpen = f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                settings.Release();
                await Task.WhenAll(canceledOpen, requestedOpen);
                await Until(() => f.Module.DesktopLyricsWindow is { AppWindow.IsVisible: true });
                True(f.Module.IsDesktopLyricsOpen, "the later toggle opens after the shared settings read settles");
            }, withCurrent: false));

        yield return Case("shutdown during desktop lyric settings load cannot create a late window", () =>
            WithDesktopLyrics(async (f, _) =>
            {
                var settings = f.Calls.HoldNext("music.lyrics.desktop.settings.get");
                var opening = f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                await settings.Arrived.Task;
                var shutdown = f.Module.PrepareShutdownAsync();
                True(!f.Module.IsDesktopLyricsOpen, "shutdown seals an opening request before settings settle");
                settings.Release();
                await Task.WhenAll(opening, shutdown);
                True(!f.Module.IsDesktopLyricsOpen, "the stale settings reply cannot create a window after shutdown");
            }, withCurrent: false));

        yield return Case("desktop overlay is alpha zero outside hover and retains chrome while its menu is open", () =>
            WithDesktopLyrics(async (f, _) =>
            {
                await SaveDesktopLyricFixture(f);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                var window = f.Module.DesktopLyricsWindow!;
                await Drain();
                window.RefreshHover(new(-100000, -100000));
                var toolbar = (StackPanel)DesktopTestUi.WindowRoot(window).FindName("Toolbar");
                var backdrop = (Border)DesktopTestUi.WindowRoot(window).FindName("HoverSurface");
                True(window.SystemBackdrop is LyricsTransparentBackdrop && ((SolidColorBrush)((Border)window.Content).Background).Color.A == 0 &&
                    backdrop.Visibility == Visibility.Collapsed && toolbar.Visibility == Visibility.Collapsed,
                    "ordinary lyrics have no opaque or almost-transparent rectangle");
                Equal((byte)0, await DesktopAlpha(window, 4, 4));
                True(window.IsPointerObserverRunning, "only a visible unlocked window observes its transparent bounds");
                StopDesktopPointerObservation(window);
                DesktopHover(window);
                await Drain();
                DesktopTestUi.WindowRoot(window).UpdateLayout();
                Equal(Visibility.Visible, toolbar.Visibility);
                True(await DesktopAlpha(window, 4, 4) > 0, "hover paints the actual surface");
                await GeneralAppearanceTests.SavePreview((FrameworkElement)window.Content, "desktop-lyrics-hover",
                    (int)DesktopTestUi.WindowRoot(window).ActualWidth, (int)DesktopTestUi.WindowRoot(window).ActualHeight);
                var toolbarBounds = new Rect(toolbar.TransformToVisual(DesktopTestUi.WindowRoot(window)).TransformPoint(new()), new Size(toolbar.ActualWidth, toolbar.ActualHeight));
                True(Math.Abs(toolbarBounds.Left + toolbarBounds.Width / 2 - DesktopTestUi.WindowRoot(window).ActualWidth / 2) <= 1,
                    $"the complete toolbar is horizontally centered; toolbar={toolbarBounds}, window={DesktopTestUi.WindowRoot(window).ActualWidth}x{DesktopTestUi.WindowRoot(window).ActualHeight}");
                True(DesktopTestUi.WindowMinimumSize(window).Width >= toolbar.DesiredSize.Width,
                    $"the hidden toolbar's full desired width remains available: minimum={DesktopTestUi.WindowMinimumSize(window).Width}, toolbar={toolbar.DesiredSize.Width}");
                DesktopTestUi.ResizeWindow(window, DesktopTestUi.WindowMinimumSize(window).Width, DesktopTestUi.WindowSize(window).Height);
                DesktopTestUi.ResizeWindow(window, DesktopTestUi.WindowSize(window).Width, DesktopTestUi.WindowMinimumSize(window).Height);
                await Until(() => Math.Abs(DesktopTestUi.WindowRoot(window).ActualWidth - DesktopTestUi.WindowMinimumSize(window).Width) <= 1 &&
                    Math.Abs(DesktopTestUi.WindowRoot(window).ActualHeight - DesktopTestUi.WindowMinimumSize(window).Height) <= 1);
                DesktopTestUi.WindowRoot(window).UpdateLayout();
                var minimumToolbarBounds = new Rect(toolbar.TransformToVisual(DesktopTestUi.WindowRoot(window)).TransformPoint(new()), new Size(toolbar.ActualWidth, toolbar.ActualHeight));
                True(minimumToolbarBounds.Left >= 0 && minimumToolbarBounds.Right <= DesktopTestUi.WindowRoot(window).ActualWidth &&
                    toolbar.Children.OfType<Button>().All(button =>
                    {
                        var bounds = new Rect(button.TransformToVisual(DesktopTestUi.WindowRoot(window)).TransformPoint(new()), new Size(button.ActualWidth, button.ActualHeight));
                        return bounds.Left >= 0 && bounds.Right <= DesktopTestUi.WindowRoot(window).ActualWidth;
                    }),
                    $"the toolbar and all buttons remain inside the minimum window width: toolbar={minimumToolbarBounds}, window={DesktopTestUi.WindowRoot(window).ActualWidth}");
                var minimumLine = (MusicDesktopLyricLineView)DesktopTestUi.WindowRoot(window).FindName("TopLine");
                var minimumLineBounds = new Rect(minimumLine.TransformToVisual(DesktopTestUi.WindowRoot(window)).TransformPoint(new()), new Size(minimumLine.ActualWidth, minimumLine.ActualHeight));
                var minimumTranslation = (FrameworkElement)minimumLine.FindName("TranslationFrame");
                var minimumTranslationBounds = minimumTranslation.TransformToVisual(DesktopTestUi.WindowRoot(window))
                    .TransformBounds(new Rect(new Point(), new Size(minimumTranslation.ActualWidth, minimumTranslation.ActualHeight)));
                True(minimumLineBounds.Top >= 44 && minimumLineBounds.Bottom <= DesktopTestUi.WindowRoot(window).ActualHeight - 12 &&
                    DesktopVisual.IsVisible(minimumTranslation) && minimumTranslationBounds.Bottom <= DesktopTestUi.WindowRoot(window).ActualHeight - 12,
                    $"the complete translated lyric fits at the minimum height: line={minimumLineBounds}, translation={minimumTranslationBounds}");
                await GeneralAppearanceTests.SavePreview((FrameworkElement)window.Content, "desktop-lyrics-minimum",
                    (int)DesktopTestUi.WindowRoot(window).ActualWidth, (int)DesktopTestUi.WindowRoot(window).ActualHeight);
                var controls = toolbar.Children.OfType<Button>().Select(button => button.Name).ToArray();
                True(controls.SequenceEqual(new[] { "ActivateButton", "DelayButton", "AdvanceButton",
                    "PreviousButton", "PlaybackButton", "NextButton", "SettingsButton", "LockButton", "CloseButton" }),
                    "toolbar follows the confirmed action order");
                await AssertLyricTimingIconAsync((Button)DesktopTestUi.WindowRoot(window).FindName("DelayButton"), expectCircle: false);
                await AssertLyricTimingIconAsync((Button)DesktopTestUi.WindowRoot(window).FindName("AdvanceButton"), expectCircle: false);
                double audio = f.Module.PositionSeconds;
                Click((Button)DesktopTestUi.WindowRoot(window).FindName("DelayButton"));
                Equal(.5d, f.Module.LyricOffsetSeconds);
                Click((Button)DesktopTestUi.WindowRoot(window).FindName("AdvanceButton"));
                Equal(0d, f.Module.LyricOffsetSeconds);
                Equal(audio, f.Module.PositionSeconds);
                Click((Button)DesktopTestUi.WindowRoot(window).FindName("SettingsButton"));
                await Drain();
                var menu = DesktopOpenMenu(window);
                True(ReferenceEquals(menu.MenuFlyoutPresenterStyle, DesktopVisual.Resource(DesktopTestUi.WindowRoot(window), "MeowFilm.ContextMenu")),
                    "desktop settings use the shared context menu style");
                foreach (var item in menu.Items)
                    Equal(Microsoft.UI.Text.FontWeights.Normal, item.FontWeight);
                window.RefreshHover(new(-100000, -100000));
                Equal(Visibility.Visible, toolbar.Visibility);
                menu.Hide();
                await Drain();
                window.RefreshHover(new(-100000, -100000));
                Equal(Visibility.Collapsed, toolbar.Visibility);
                window.AppWindow.Hide();
                True(!window.IsPointerObserverRunning, "hidden lyrics release the pointer observer");
                window.Show(); await Drain();
                True(window.IsPointerObserverRunning, "visible lyrics resume their observer");
            }));

        yield return Case("desktop fixed tracks use source rows and append source translation to both lines", () =>
            WithDesktopLyrics(async (f, _) =>
            {
                await SaveDesktopLyricFixture(f);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                var singleHeight = DesktopTestUi.WindowRoot(f.Module.DesktopLyricsWindow!).ActualHeight;
                var initialBottom = DesktopTestUi.WindowPosition(f.Module.DesktopLyricsWindow!).Y + singleHeight;
                await f.Module.SetDesktopLyricLayoutAsync("double");
                var window = f.Module.DesktopLyricsWindow!;
                await Drain();
                await Until(() => DesktopTestUi.WindowRoot(window).ActualHeight > singleHeight && DesktopTestUi.WindowRoot(window).ActualHeight == DesktopTestUi.WindowSize(window).Height);
                DesktopTestUi.WindowRoot(window).UpdateLayout();
                Equal(initialBottom, DesktopTestUi.WindowPosition(window).Y + DesktopTestUi.WindowRoot(window).ActualHeight);
                double doubleHeight = DesktopTestUi.WindowRoot(window).ActualHeight;
                var top = (MusicDesktopLyricLineView)DesktopTestUi.WindowRoot(window).FindName("TopLine");
                var bottom = (MusicDesktopLyricLineView)DesktopTestUi.WindowRoot(window).FindName("BottomLine");
                True(ReferenceEquals(top.Row, f.Module.Lyrics[0]) && ReferenceEquals(bottom.Row, f.Module.Lyrics[1]),
                    "two tracks reference source rows instead of copying or replacing lyrics");
                Equal(HorizontalAlignment.Left, top.HorizontalContentAlignment);
                Equal(HorizontalAlignment.Right, bottom.HorizontalContentAlignment);
                True(DesktopVisual.IsVisible(((FrameworkElement)top.FindName("TranslationFrame"))) &&
                    DesktopVisual.IsVisible(((FrameworkElement)bottom.FindName("TranslationFrame"))), "each original owns its own translation");
                var firstSource = f.Module.Lyrics[0];
                var secondSource = f.Module.Lyrics[1];
                f.Module.Lyrics[1] = secondSource with { Translation = "" };
                await Drain();
                Equal(224d, DesktopTestUi.WindowMinimumSize(window).Height);
                f.Module.Lyrics[0] = firstSource with { Translation = "" };
                await Drain();
                Equal(180d, DesktopTestUi.WindowMinimumSize(window).Height);
                f.Module.Lyrics[1] = secondSource;
                await Drain();
                Equal(240d, DesktopTestUi.WindowMinimumSize(window).Height);
                f.Module.Lyrics[0] = firstSource;
                await Drain();
                Equal(240d, DesktopTestUi.WindowMinimumSize(window).Height);
                Equal(doubleHeight, DesktopTestUi.WindowRoot(window).ActualHeight);
                var bottomY = bottom.TransformToVisual(DesktopTestUi.WindowRoot(window)).TransformPoint(new()).Y;
                foreach (var line in new[] { top, bottom })
                {
                    var bounds = new Rect(line.TransformToVisual(DesktopTestUi.WindowRoot(window)).TransformPoint(new()), new Size(line.ActualWidth, line.ActualHeight));
                    True(bounds.Top >= 44 && bounds.Bottom <= DesktopTestUi.WindowRoot(window).ActualHeight,
                        "both originals and translations fit below the toolbar without clipping");
                    var translated = (FrameworkElement)line.FindName("TranslationFrame");
                    var translationBounds = translated.TransformToVisual(DesktopTestUi.WindowRoot(window)).TransformBounds(new Rect(new Point(), new Size(translated.ActualWidth, translated.ActualHeight)));
                    True(translationBounds.Bottom <= bounds.Bottom && translationBounds.Bottom <= DesktopTestUi.WindowRoot(window).ActualHeight - 12,
                        $"source translations fit their fixed track: row={bounds}, translation={translationBounds}, window={DesktopTestUi.WindowRoot(window).ActualHeight}");
                }
                await GeneralAppearanceTests.SavePreview((FrameworkElement)window.Content, "desktop-lyrics-double-translated",
                    (int)DesktopTestUi.WindowRoot(window).ActualWidth, (int)DesktopTestUi.WindowRoot(window).ActualHeight);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleTranslation); await Drain();
                True(!DesktopVisual.IsVisible(((FrameworkElement)top.FindName("TranslationFrame"))) &&
                    !DesktopVisual.IsVisible(((FrameworkElement)bottom.FindName("TranslationFrame"))), "the shared switch hides both translations");
                f.Playback.Tick(6); await Drain();
                True(top.Row is null && ReferenceEquals(bottom.Row, f.Module.Lyrics[1]),
                    "the final odd track stays bottom-right and no lyric is fabricated beyond the end");
                Equal(bottomY, bottom.TransformToVisual(DesktopTestUi.WindowRoot(window)).TransformPoint(new()).Y);
                Equal(Visibility.Collapsed, ((FrameworkElement)DesktopTestUi.WindowRoot(window).FindName("Status")).Visibility);
                await f.Module.SetDesktopLyricLayoutAsync("single"); await Drain();
                Equal(doubleHeight, DesktopTestUi.WindowRoot(window).ActualHeight);
                True(ReferenceEquals(top.Row, f.Module.Lyrics[1]) && !DesktopVisual.IsVisible(bottom),
                    "single mode presents the active original, not its translation or a pending track");
                f.Module.PreviewLyric(f.Module.Lyrics[0]);
                True(ReferenceEquals(top.Row, f.Module.Lyrics[1]), "desktop tracks do not follow the full player's preview");
            }));

        yield return Case("single desktop lyric line centers only visible source text and translation", () =>
            OnSta(async () =>
            {
                var line = new MusicDesktopLyricLineView
                {
                    Width = 700, Height = 100, HorizontalContentAlignment = HorizontalAlignment.Center,
                    Row = new(0, "当前原文", "当前译文"), ShowTranslation = true
                };
                var host = DesktopTestUi.CreateWindow(line, 760, 160);
                try
                {
                    host.Show(); await Drain(); line.UpdateLayout();
                    static void CheckVisibleBlock(MusicDesktopLyricLineView target, int count)
                    {
                        var boxes = Visuals(target).OfType<Viewbox>().Where(box => DesktopVisual.IsVisible(box)).ToArray();
                        Equal(count, boxes.Length);
                        Rect block = Rect.Empty;
                        foreach (var box in boxes)
                            block.Union(new Rect(box.TransformToVisual(target).TransformPoint(new()), new Size(box.ActualWidth, box.ActualHeight)));
                        True(Math.Abs(block.Top + block.Height / 2 - target.ActualHeight / 2) <= 1,
                            "visible source text is vertically centered without a reserved translation row");
                        True(Math.Abs(block.Left + block.Width / 2 - target.ActualWidth / 2) <= 1,
                            "single-line source text and translation are horizontally centered");
                    }

                    CheckVisibleBlock(line, 2);
                    line.ShowTranslation = false; await Drain(); line.UpdateLayout();
                    CheckVisibleBlock(line, 1);
                    line.ShowTranslation = true;
                    line.Row = line.Row! with { Translation = "" };
                    await Drain(); line.UpdateLayout();
                    CheckVisibleBlock(line, 1);
                    True(!DesktopVisual.IsVisible(((FrameworkElement)line.FindName("TranslationFrame"))),
                        "an enabled translation setting without source text creates no empty row");
                }
                finally { host.Close(); await Drain(); }
            }));

        yield return Case("desktop ordinary text never uses highlight and real words reuse the sampled lyric presenter", async () =>
        {
            var row = new MusicDesktopLyricLineView
            {
                Row = new(0, "普通原文 e\u0301 العربية 🙂", "源译文"),
                Normal = (new SolidColorBrush(Microsoft.UI.Colors.LightPink)), Highlight = (new SolidColorBrush(Microsoft.UI.Colors.Gold))
            };
            var list = new MusicLyricList
            {
                Position = 1, IsPlaying = false, PlaybackRate = 1,
                PresentationKey = "desktop-render-fixture", Width = 700, Height = 160,
                Background = (new SolidColorBrush(Microsoft.UI.Colors.Transparent)), BorderThickness = new(0),
                Template = DesktopPresenterTemplate()
            };
            list.Items.Add(row);
            var host = DesktopLyricOwner();
            try
            {
                host.Content = list; host.Show(); await Drain(); list.UpdateLayout();
                var plain = (MusicDesktopLyricTextView)row.FindName("PlainText");
                True(DesktopVisual.IsVisible(plain) && !DesktopVisual.IsVisible(((ItemsControl)row.FindName("Words"))),
                    "line-timed lyrics stay ordinary-colored and have no synthetic karaoke view");
                Equal(Colors.LightPink, ((SolidColorBrush)plain.Foreground).Color);
                row.Row = new(0, "真实逐字", "源译文", Words: [new("真实", 0, 2), new("逐字", 2, 2)]);
                await Drain(); list.UpdateLayout(); await Drain();
                var words = Visuals(row).OfType<MusicLyricWordView>().ToArray();
                Equal(2, words.Length);
                Equal(.5, words[0].WordProgress); Equal(0d, words[1].WordProgress);
                True(words.All(word => Equals(word.Foreground, row.Normal) && Equals(word.Highlight, row.Highlight) &&
                    word.Stroke is not null && word.StrokeThickness > 0), "normal highlight and outline remain separate roles");
                await words[0].Outline.RenderWork;
                var glyphs = words[0].Outline.Coverage;
                True(glyphs is { Length: > 0 }, "native hinted outline coverage was actually rasterized");
                row.Highlight = (new SolidColorBrush(Microsoft.UI.Colors.LightBlue)); row.Normal = (new SolidColorBrush(Microsoft.UI.Colors.White));
                await Drain();
                Equal(.5, words[0].WordProgress);
                True(ReferenceEquals(glyphs, words[0].Outline.Coverage), "changing colors retains the native glyph cache and progress");
                list.Position = 3; await Drain();
                Equal(1d, words[0].WordProgress); Equal(.5, words[1].WordProgress);
                await GeneralAppearanceTests.SavePreview(row, "desktop-lyrics-real-words", 700, 120);
                row.Row = new(0, new string('长', 100), "long source translation");
                row.Width = 660;
                await Drain(); list.UpdateLayout();
                True(row.ActualWidth <= 700 && DesktopVisual.IsVisible(plain) && !Visuals(row).OfType<MusicLyricWordView>().Any(),
                    "long ordinary lines scale without fake words or a stale highlighted layer");
                await GeneralAppearanceTests.SavePreview(row, "desktop-lyrics-long-ordinary", 700, 120);
                DesktopTheme.SetIsHighContrast(row, true);
                Render(row, 700, 120); await Drain();
                var ink = Visuals(plain).OfType<TextBlock>().Select(text => ((SolidColorBrush)text.Foreground).Color).ToArray();
                True(ink.Length > 0 && ink.All(color => color == new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.WindowText)),
                    "ordinary desktop text paints the system high-contrast ink rather than the saved pink or highlight");
            }
            finally { host.Close(); await Drain(); }
        });

        yield return Case("desktop palette cancellation discards drafts and a confirmed custom color saves", () =>
            WithDesktopLyrics(async (f, _) =>
            {
                await SaveDesktopLyricFixture(f);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                var window = f.Module.DesktopLyricsWindow!;
                string original = f.Module.DesktopLyricNormalColor;
                var editor = new MusicDesktopLyricColorWindow(f.Module, "normal")
                { XamlRoot = DesktopTestUi.WindowRoot(window).XamlRoot };
                var showing = editor.ShowAsync().AsTask();
                await Until(() => MusicTestUi.IsDialogOpen(editor)); await Drain();
                var selection = (Microsoft.UI.Xaml.Controls.ColorPicker)editor.FindName("Selection");
                selection.Color = Colors.White; await Drain();
                Equal(original, f.Module.DesktopLyricNormalColor);
                Click(Visuals(editor).OfType<Button>().Single(button => Equals(button.Content, "取消")));
                Equal(ContentDialogResult.None, await showing); await Drain();
                Equal(original, f.Module.DesktopLyricNormalColor);
                True(!MusicTestUi.IsDialogOpen(editor), "cancel releases the same-root native color dialog");
                editor = new(f.Module, "normal")
                { XamlRoot = DesktopTestUi.WindowRoot(window).XamlRoot };
                showing = editor.ShowAsync().AsTask();
                await Until(() => MusicTestUi.IsDialogOpen(editor)); await Drain();
                selection = (Microsoft.UI.Xaml.Controls.ColorPicker)editor.FindName("Selection");
                selection.Color = Color.FromArgb(255, 0x12, 0x34, 0x56);
                Equal(original, f.Module.DesktopLyricNormalColor);
                Click(Visuals(editor).OfType<Button>().Single(button => Equals(button.Content, "确定")));
                Equal(ContentDialogResult.Primary, await showing);
                True(!MusicTestUi.IsDialogOpen(editor), "successful save retires the native dialog");
                Equal("#123456", f.Module.DesktopLyricNormalColor);
                await Drain();
                Equal(Color.FromArgb(255, 0x12, 0x34, 0x56),
                    ((SolidColorBrush)((MusicDesktopLyricLineView)DesktopTestUi.WindowRoot(window).FindName("TopLine")).Normal).Color);
            }));

        yield return Case("desktop lock uses a separate recoverable HWND and closes every native resource", () =>
            WithDesktopLyrics(async (f, _) =>
            {
                await SaveDesktopLyricFixture(f);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                var window = f.Module.DesktopLyricsWindow!; await Drain();
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                var style = DesktopInteractionNative.GetWindowLongPtr(hwnd, -20);
                True((style & 0x08000080) == 0x08000080 && (style & 0x20) == 0,
                    "the transparent native HWND is noactivate and toolwindow, not click-through");
                DesktopHover(window);
                await Drain();
                DesktopTestUi.WindowRoot(window).UpdateLayout();
                var lockButton = (Button)DesktopTestUi.WindowRoot(window).FindName("LockButton");
                var lockStart = NativeDesktopCapture.ScreenPoint(lockButton, new());
                var lockEnd = NativeDesktopCapture.ScreenPoint(lockButton, new(lockButton.ActualWidth, lockButton.ActualHeight));
                window.SetLocked(true); await Drain();
                var unlock = window.UnlockWindow!;
                window.ApplyHighContrast(true); await Drain();
                True(DesktopTheme.GetIsHighContrast(DesktopTestUi.WindowRoot(window)) &&
                    DesktopTheme.GetIsHighContrast((Button)unlock.Content),
                    "runtime high contrast reaches both independent roots and their controls");
                Equal(new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.WindowText),
                    ((SolidColorBrush)((Button)unlock.Content).Foreground).Color);
                window.ApplyHighContrast(false); await Drain();
                nint unlockHandle = WinRT.Interop.WindowNative.GetWindowHandle(unlock);
                True(unlockHandle != 0 && unlockHandle != hwnd && DesktopInteractionNative.IsWindow(unlockHandle),
                    "unlock is a real independent top-level HWND");
                True((DesktopInteractionNative.GetWindowLongPtr(hwnd, -20) & 0x20) != 0 &&
                    (DesktopInteractionNative.GetWindowLongPtr(unlockHandle, -20) & 0x20) == 0,
                    "only the lyric HWND becomes click-through; style checks alone are not cross-process input evidence");
                True(!window.IsPointerObserverRunning && ((FrameworkElement)DesktopTestUi.WindowRoot(window).FindName("Toolbar")).Visibility == Visibility.Collapsed,
                    "locked lyrics stop observing hover and retire the normal toolbar");
                True(DesktopTestUi.WindowIsVisible(unlock), "the independent unlock entry remains visible while locked");
                await GeneralAppearanceTests.SavePreview((FrameworkElement)window.Content, "desktop-lyrics-locked",
                    (int)DesktopTestUi.WindowRoot(window).ActualWidth, (int)DesktopTestUi.WindowRoot(window).ActualHeight);
                await GeneralAppearanceTests.SavePreview((FrameworkElement)unlock.Content, "desktop-lyrics-unlock",
                    (int)DesktopTestUi.WindowRoot(unlock).ActualWidth, (int)DesktopTestUi.WindowRoot(unlock).ActualHeight);
                Equal((nint)3, DesktopInteractionNative.SendMessage(unlockHandle, 0x21, 0, 0));
                var state = window.CapturePresentationState();
                Equal(true, state.IsLocked);
                var windowOrigin = NativeDesktopCapture.ScreenPoint(DesktopTestUi.WindowRoot(window), new());
                DesktopTestUi.MoveWindow(window, DesktopTestUi.WindowPosition(window).X + (18), DesktopTestUi.WindowPosition(window).Y); DesktopTestUi.MoveWindow(window, DesktopTestUi.WindowPosition(window).X, DesktopTestUi.WindowPosition(window).Y + (21)); await Drain();
                var anchor = (FrameworkElement)DesktopTestUi.WindowRoot(window).FindName("UnlockAnchor");
                var point = NativeDesktopCapture.ScreenPoint(anchor, new());
                var movedOrigin = NativeDesktopCapture.ScreenPoint(DesktopTestUi.WindowRoot(window), new());
                double movedX = movedOrigin.X - windowOrigin.X;
                double movedY = movedOrigin.Y - windowOrigin.Y;
                True(MusicDesktopLyricsNative.GetWindowRect(unlockHandle, out var unlockBounds) &&
                    Math.Abs(unlockBounds.Left - point.X) <= 1 && Math.Abs(unlockBounds.Top - point.Y) <= 1,
                    "the independent unlock HWND follows physical placement, not an assumed screen-wide DPI");
                True(Math.Abs(unlockBounds.Left - lockStart.X - movedX) <= 1 &&
                    Math.Abs(unlockBounds.Top - lockStart.Y - movedY) <= 1 &&
                    Math.Abs(unlockBounds.Right - lockEnd.X - movedX) <= 1 &&
                    Math.Abs(unlockBounds.Bottom - lockEnd.Y - movedY) <= 1,
                    $"the unlock HWND occupies the centered toolbar's former lock-button hit target; unlock={unlockBounds}, lock={lockStart}..{lockEnd}, move={movedX},{movedY}");
                Click((Button)unlock.Content);
                True(!window.IsLyricsLocked && window.UnlockWindow is null && !DesktopInteractionNative.IsWindow(unlockHandle),
                    "the independent unlock action restores normal operation and releases its HWND");
                window.SetLocked(true); await Drain();
                unlockHandle = WinRT.Interop.WindowNative.GetWindowHandle(window.UnlockWindow!);
                window.Close(); await Drain();
                True(!DesktopInteractionNative.IsWindow(hwnd) && !DesktopInteractionNative.IsWindow(unlockHandle) &&
                    !window.IsPointerObserverRunning && DesktopTestUi.WindowRoot(window).DataContext is null, "close releases both HWNDs and the observer");
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics); await Drain();
                True(!f.Module.DesktopLyricsWindow!.IsLyricsLocked, "ordinary reopen does not persist locked state");
            }));

        yield return Case("desktop lyric corners and edges resize natively and retain the chosen size", () =>
            WithDesktopLyrics(async (f, _) =>
            {
                await SaveDesktopLyricFixture(f);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                var window = f.Module.DesktopLyricsWindow!;
                var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
                var style = DesktopInteractionNative.GetWindowLongPtr(handle, -16);
                True((style & 0x00040000) != 0, "the real lyrics HWND retains the native resize frame");
                True(MusicDesktopLyricsNative.GetWindowRect(handle, out var bounds), "the resize target has native bounds");
                static nint Packed(int x, int y) => (nint)(((y & 0xffff) << 16) | (x & 0xffff));
                var hits = new[]
                {
                    (bounds.Left + 1, bounds.Top + 1, 13), (bounds.Right - 1, bounds.Top + 1, 14),
                    (bounds.Left + 1, bounds.Bottom - 1, 16), (bounds.Right - 1, bounds.Bottom - 1, 17),
                    (bounds.Left + 1, (bounds.Top + bounds.Bottom) / 2, 10),
                    (bounds.Right - 1, (bounds.Top + bounds.Bottom) / 2, 11),
                    ((bounds.Left + bounds.Right) / 2, bounds.Top + 1, 12),
                    ((bounds.Left + bounds.Right) / 2, bounds.Bottom - 1, 15)
                };
                foreach (var (x, y, expected) in hits)
                    Equal((nint)expected, DesktopInteractionNative.SendMessage(handle, 0x0084, 0, Packed(x, y)));

                DesktopInteractionNative.SendMessage(handle, 0x0231, 0, 0);
                True(DesktopInteractionNative.SetWindowPos(handle, 0, bounds.Left, bounds.Top,
                    bounds.Right - bounds.Left + 80, bounds.Bottom - bounds.Top + 40, 0x10),
                    "a native resize can begin before its size gesture is cancelled");
                await Drain();
                True(DesktopInteractionNative.SetWindowPos(handle, 0, bounds.Left, bounds.Top,
                    bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0x10),
                    "cancelling a native resize restores its original bounds");
                await Drain();
                DesktopInteractionNative.SendMessage(handle, 0x0232, 0, 0);
                True(!window.CapturePresentationState().UserSized,
                    "a cancelled native resize does not become the user's preferred size");
                True(f.Module.DesktopLyricGeometry is null,
                    "a cancelled resize does not commit an intermediate window position or size");
                await f.Module.SetDesktopLyricLayoutAsync("double"); await Drain();
                Equal(240d, DesktopTestUi.WindowRoot(window).ActualHeight);
                nint infoAddress = Marshal.AllocHGlobal(Marshal.SizeOf<DesktopInteractionNative.MinMaxInfo>());
                try
                {
                    Marshal.StructureToPtr(new DesktopInteractionNative.MinMaxInfo(), infoAddress, false);
                    DesktopInteractionNative.SendMessage(handle, 0x0024, 0, infoAddress);
                    var limits = Marshal.PtrToStructure<DesktopInteractionNative.MinMaxInfo>(infoAddress);
                    uint dpi = MusicDesktopLyricsNative.GetDpiForWindow(handle);
                    True(dpi > 0 && limits.MinTrackSize.X == Math.Ceiling(DesktopTestUi.WindowMinimumSize(window).Width * dpi / 96d) &&
                        limits.MinTrackSize.Y == Math.Ceiling(DesktopTestUi.WindowMinimumSize(window).Height * dpi / 96d),
                        $"native resizing enforces complete toolbar and double translated rows: {limits.MinTrackSize.X}x{limits.MinTrackSize.Y}");
                }
                finally { Marshal.FreeHGlobal(infoAddress); }
                await f.Module.SetDesktopLyricLayoutAsync("single"); await Drain();
                Equal(240d, DesktopTestUi.WindowRoot(window).ActualHeight);

                double oldWidth = DesktopTestUi.WindowRoot(window).ActualWidth, oldHeight = DesktopTestUi.WindowRoot(window).ActualHeight;
                True(MusicDesktopLyricsNative.GetWindowRect(handle, out var resizeBounds),
                    "current native bounds reflect the content minimum before the next resize");
                DesktopInteractionNative.SendMessage(handle, 0x0231, 0, 0);
                True(DesktopInteractionNative.SetWindowPos(handle, 0, resizeBounds.Left, resizeBounds.Top,
                    resizeBounds.Right - resizeBounds.Left + 120, resizeBounds.Bottom - resizeBounds.Top + 60, 0x10),
                    "the owned window accepts a native width and height change");
                await Drain();
                DesktopInteractionNative.SendMessage(handle, 0x0232, 0, 0);
                True(DesktopTestUi.WindowRoot(window).ActualWidth > oldWidth + 50 && DesktopTestUi.WindowRoot(window).ActualHeight > oldHeight + 25,
                    "the native size change reaches the actual WinUI root in both dimensions");
                double resizedHeight = DesktopTestUi.WindowRoot(window).ActualHeight;
                await f.Module.SetDesktopLyricLayoutAsync("double"); await Drain();
                Equal(resizedHeight, DesktopTestUi.WindowRoot(window).ActualHeight);
                window.SetLocked(true); await Drain();
                True((DesktopInteractionNative.GetWindowLongPtr(handle, -20) & 0x20) != 0,
                    "resizing does not weaken locked click-through");
                window.SetLocked(false); await Drain();
                Equal(resizedHeight, DesktopTestUi.WindowRoot(window).ActualHeight);

                True(MusicDesktopLyricsNative.GetWindowRect(handle, out var beforeMove),
                    "the window has bounds before the move-only persistence check");
                var beforeMoveGeometry = window.CapturePresentationState().Geometry!.Value;
                DesktopInteractionNative.SendMessage(handle, 0x0231, 0, 0);
                True(DesktopInteractionNative.SetWindowPos(handle, 0, beforeMove.Left + 40, beforeMove.Top + 25,
                    beforeMove.Right - beforeMove.Left, beforeMove.Bottom - beforeMove.Top, 0x10),
                    "the native window accepts a move without a size change");
                await Drain();
                DesktopInteractionNative.SendMessage(handle, 0x0232, 0, 0);
                await f.Module.EnsureDesktopLyricSettingsAsync();
                var movedGeometry = window.CapturePresentationState().Geometry!.Value;
                True(movedGeometry.X > beforeMoveGeometry.X && movedGeometry.Y > beforeMoveGeometry.Y,
                    "a pure native move updates the persisted geometry");
                Equal(movedGeometry, f.Module.DesktopLyricGeometry!.Value);

                var savedGeometry = window.CapturePresentationState().Geometry!.Value;
                window.Close();
                await Until(() => !f.Module.IsDesktopLyricsOpen);
                await f.Module.EnsureDesktopLyricSettingsAsync();
                Equal(savedGeometry, f.Module.DesktopLyricGeometry!.Value);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                await Drain();
                var reopened = f.Module.DesktopLyricsWindow!;
                var reopenedGeometry = reopened.CapturePresentationState().Geometry!.Value;
                Equal(savedGeometry.Monitor, reopenedGeometry.Monitor);
                True(Math.Abs(savedGeometry.X - reopenedGeometry.X) < 1 &&
                    Math.Abs(savedGeometry.Y - reopenedGeometry.Y) < 1,
                    "the same display restores the saved logical offset");
                Equal(savedGeometry.Width, DesktopTestUi.WindowSize(reopened).Width);
                Equal(savedGeometry.Height, DesktopTestUi.WindowSize(reopened).Height);
                True(!reopened.IsLyricsLocked, "ordinary reopen retains size and placement but resets the lock");
            }));

        yield return Case("a missing saved display restores on the primary work area", () =>
            WithDesktopLyrics(async (f, _) =>
            {
                await f.Module.EnsureDesktopLyricSettingsAsync();
                await f.Module.SaveDesktopLyricGeometryAsync(new("missing-display", 2000000, 2000000, 760, 144));
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                await f.Module.EnsureDesktopLyricSettingsAsync();
                var window = f.Module.DesktopLyricsWindow!;
                var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
                var monitor = MusicDesktopLyricsNative.MonitorForWindow(handle);
                True(monitor.Primary && MusicDesktopLyricsNative.GetWindowRect(handle, out var bounds) &&
                    bounds.Left >= monitor.WorkLeft && bounds.Top >= monitor.WorkTop &&
                    bounds.Left < monitor.WorkRight && bounds.Top < monitor.WorkBottom,
                    "a disconnected saved display is restored within the primary work area");
                Equal(monitor.Device, f.Module.DesktopLyricGeometry!.Value.Monitor);
            }));

        yield return Case("a rapid reopen waits for a closing window's final geometry save", () =>
            WithDesktopLyrics(async (f, _) =>
            {
                await SaveDesktopLyricFixture(f, translated: false);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                var closing = f.Module.DesktopLyricsWindow!;
                DesktopTestUi.MoveWindow(closing, DesktopTestUi.WindowPosition(closing).X + (37), DesktopTestUi.WindowPosition(closing).Y);
                DesktopTestUi.MoveWindow(closing, DesktopTestUi.WindowPosition(closing).X, DesktopTestUi.WindowPosition(closing).Y + (19));
                var desiredGeometry = closing.CapturePresentationState().Geometry!.Value;

                var save = f.Calls.HoldNext("music.lyrics.desktop.settings.save");
                var close = f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                await save.Arrived.Task;
                True(closing.IsClosePending, "the first toggle begins its final asynchronous geometry save");
                True(!DesktopTestUi.WindowIsEnabled(closing) && !DesktopTestUi.WindowRoot(closing).IsHitTestVisible,
                    "the closing window cannot move after its final geometry has been captured");
                True(!close.IsCompleted && !f.Module.IsDesktopLyricsOpen && !f.Module.NeedsLyrics,
                    "closing waits for the accepted write after retiring its lyric consumer");
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                save.Release();
                await close;
                await Until(() => f.Module.DesktopLyricsWindow is { AppWindow.IsVisible: true } current &&
                    !ReferenceEquals(current, closing));

                Equal(desiredGeometry, f.Module.DesktopLyricGeometry!.Value);
                True(!DesktopTestUi.WindowIsVisible(closing), "the previous HWND closes before the requested replacement appears");
            }));

        yield return Case("shutdown cancellation cannot revive desktop lyrics already waiting to close", () =>
            WithDesktopLyrics(async (f, _) =>
            {
                await SaveDesktopLyricFixture(f, translated: false);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                var window = f.Module.DesktopLyricsWindow!;
                DesktopTestUi.MoveWindow(window, DesktopTestUi.WindowPosition(window).X + (31), DesktopTestUi.WindowPosition(window).Y);
                var geometry = window.CapturePresentationState().Geometry!.Value;
                var save = f.Calls.HoldNext("music.lyrics.desktop.settings.save");
                var close = f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                await save.Arrived.Task;
                True(!f.Module.IsDesktopLyricsOpen && !close.IsCompleted,
                    "the desktop consumer retires while its accepted geometry write is still pending");
                var shutdown = f.Module.PrepareShutdownAsync();
                save.Release();
                await Task.WhenAll(close, shutdown);
                await f.Module.CancelShutdownPreparationAsync();
                True(!f.Module.IsDesktopLyricsOpen && !DesktopTestUi.WindowIsVisible(window),
                    "cancelling shutdown does not restore a window whose close was already accepted");
                Equal(geometry, f.Module.DesktopLyricGeometry!.Value);
            }));

        yield return new(DesktopHitTestCase, () =>
            Environment.GetEnvironmentVariable(DesktopHitPipeVariable) is { } pipe
                ? OnSta(() => DesktopHitChild(pipe))
                : OnSta(() => WithDesktopLyrics(DesktopCrossProcessHits)));
    }

    private static async Task<byte> DesktopAlpha(MusicDesktopLyricsWindow window, int x, int y)
    {
        var frame = await DesktopTestUi.Render((FrameworkElement)window.Content);
        return frame.Pixel(x, y).A;
    }

    private static void DesktopHover(MusicDesktopLyricsWindow window)
    {
        True(MusicDesktopLyricsNative.GetWindowRect(WinRT.Interop.WindowNative.GetWindowHandle(window), out var rect), "native lyric bounds exist");
        window.RefreshHover(new(rect.Left + 2, rect.Top + 2));
    }

    private static void StopDesktopPointerObservation(MusicDesktopLyricsWindow window) =>
        ((Microsoft.UI.Dispatching.DispatcherQueueTimer)typeof(MusicDesktopLyricsWindow)
            .GetField("pointerObserver", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Stop();

    private static MenuFlyout DesktopOpenMenu(MusicDesktopLyricsWindow window) =>
        (MenuFlyout)typeof(MusicDesktopLyricsWindow).GetField("settingsMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static ControlTemplate DesktopPresenterTemplate()
    {
        const string xaml = """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:l="using:MeowFilm.Capabilities" TargetType="l:MusicLyricList"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><ItemsPresenter/></ControlTemplate>
            """;
        return (ControlTemplate)XamlReader.Load(xaml);
    }

    private static async Task DesktopCrossProcessHits(Fixture f, Window _)
    {
        await SaveDesktopLyricFixture(f);
        await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
        var window = f.Module.DesktopLyricsWindow!;
        DesktopTestUi.MoveWindow(window, SystemParameters.WorkArea.Left + 20, DesktopTestUi.WindowPosition(window).Y);
        DesktopTestUi.MoveWindow(window, DesktopTestUi.WindowPosition(window).X, SystemParameters.WorkArea.Bottom - DesktopTestUi.WindowSize(window).Height - 20);
        await Drain();
        StopDesktopPointerObservation(window);
        string pipeName = "meowfilm-lyric-hit-" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--case"); start.ArgumentList.Add(DesktopHitTestCase);
        start.Environment[DesktopHitPipeVariable] = pipeName;
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Desktop hit-test child not started");
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        try
        {
            await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            nint lower = (nint)long.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!, CultureInfo.InvariantCulture);
            True(MusicDesktopLyricsNative.GetWindowRect(WinRT.Interop.WindowNative.GetWindowHandle(window), out var rect), "lyrics have OS bounds");
            await writer.WriteLineAsync(FormattableString.Invariant($"place {rect.Left - 10} {rect.Top - 10} {rect.Right - rect.Left + 20} {rect.Bottom - rect.Top + 20}"));
            Equal("placed", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            DesktopInteractionNative.GetWindowThreadProcessId(lower, out uint pid);
            True(pid == child.Id && pid != Environment.ProcessId, "the target HWND belongs to the separate controlled process");
            True(DesktopInteractionNative.SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x13),
                "place our lyric HWND above our child without activating either");
            window.RefreshHover(new(-100000, -100000)); await Drain();
            DesktopInteractionNative.DwmFlush();
            var glyph = await DesktopOpaqueLyricPoint(window);
            var blank = NativeDesktopCapture.ScreenPoint(DesktopTestUi.WindowRoot(window), new(8, DesktopTestUi.WindowRoot(window).ActualHeight - 8));
            await DesktopHit(glyph, hwnd);
            await DesktopHit(blank, lower);
            DesktopHover(window); await Drain(); DesktopInteractionNative.DwmFlush();
            await DesktopHit(blank, hwnd);
            window.SetLocked(true); await Drain(); DesktopInteractionNative.DwmFlush();
            await DesktopHit(glyph, lower);
            await DesktopHit(blank, lower);
            var unlock = window.UnlockWindow!;
            var unlockHandle = WinRT.Interop.WindowNative.GetWindowHandle(unlock);
            var unlockPoint = NativeDesktopCapture.ScreenPoint(DesktopTestUi.WindowRoot(unlock), new(16, 16));
            await DesktopHit(unlockPoint, unlockHandle);
            var target = System.Windows.Automation.AutomationElement.FromHandle(lower).FindFirst(
                System.Windows.Automation.TreeScope.Element | System.Windows.Automation.TreeScope.Descendants,
                new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.AutomationIdProperty, "ControlledLyricHitTarget"))
                ?? throw new InvalidOperationException("Separate-process native target is not exposed to automation");
            ((System.Windows.Automation.InvokePattern)target.GetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern)).Invoke();
            await writer.WriteLineAsync("count");
            Equal("1", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Click((Button)unlock.Content); StopDesktopPointerObservation(window);
            await Drain(); DesktopHover(window); DesktopInteractionNative.DwmFlush();
            await DesktopHit(blank, hwnd);
            await writer.WriteLineAsync("close");
            Equal("closed", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Equal(0, child.ExitCode);
            Console.WriteLine("DESKTOP_LYRICS_OS_HIT_TEST other-process glyph+blank -> lower HWND; independent unlock -> unlock HWND; UI Automation action count=1; no keyboard or pointer input");
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            // Preserve child diagnostics without injecting a second test-runner summary.
            foreach (string line in ((await output) + (await errors)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Console.WriteLine("DESKTOP_HIT_CHILD " + line.TrimEnd('\r'));
        }
    }

    private static async Task DesktopHit(Point point, nint expected)
    {
        // Observe the actual native compositor frame, not just a dispatcher drain.
        var watch = Stopwatch.StartNew();
        nint actual;
        do
        {
            DesktopInteractionNative.DwmFlush();
            actual = DesktopInteractionNative.WindowFromPoint(DesktopPoint(point));
            if (actual == expected) return;
            await Task.Delay(16);
        } while (watch.Elapsed < TimeSpan.FromSeconds(8));
        throw new InvalidOperationException($"Desktop OS hit at {point}: expected HWND 0x{expected:X}, actual 0x{actual:X}");
    }

    private static async Task<Point> DesktopOpaqueLyricPoint(MusicDesktopLyricsWindow window)
    {
        var content = (FrameworkElement)window.Content;
        var bitmap = await DesktopTestUi.Render(content);
        int stride = bitmap.Width * 4;
        var pixels = bitmap.Pixels;
        for (int y = 48; y < bitmap.Height - 12; y++)
            for (int x = 22; x < bitmap.Width - 22; x++)
            {
                bool insideInk = true;
                for (int dy = -1; dy <= 1 && insideInk; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (pixels[(y + dy) * stride + (x + dx) * 4 + 3] < 220) { insideInk = false; break; }
                if (insideInk) return NativeDesktopCapture.ScreenPoint(content, new((x + .5) / bitmap.Scale, (y + .5) / bitmap.Scale));
            }
        throw new InvalidOperationException("No actual rendered lyric glyph was found");
    }

    private static DesktopInteractionNative.NativePoint DesktopPoint(Point point) =>
        new() { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) };

    private static async Task DesktopHitChild(string pipeName)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        int clicks = 0;
        var button = new Button { Content = "controlled lyric hit target", Background = new SolidColorBrush(Colors.White) };
        AutomationProperties.SetAutomationId(button, "ControlledLyricHitTarget");
        button.Click += (_, _) => clicks++;
        var target = DesktopTestUi.CreateWindow(button, 800, 300);
        target.Title = "meowfilm controlled lyric hit target";
        target.AppWindow.IsShownInSwitchers = false;
        if (target.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        { presenter.SetBorderAndTitleBar(false, false); presenter.IsResizable = false; presenter.IsAlwaysOnTop = true; }
        target.AppWindow.Move(new(-32000, -32000)); target.AppWindow.Show(false);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(target);
        try
        {
            await writer.WriteLineAsync(hwnd.ToInt64().ToString(CultureInfo.InvariantCulture));
            while (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) is { } command)
            {
                var parts = command.Split(' ');
                if (parts is ["place", var x, var y, var width, var height])
                {
                    True(DesktopInteractionNative.SetWindowPos(hwnd, -1, int.Parse(x, CultureInfo.InvariantCulture),
                        int.Parse(y, CultureInfo.InvariantCulture), int.Parse(width, CultureInfo.InvariantCulture),
                        int.Parse(height, CultureInfo.InvariantCulture), 0x10), "place our controlled target without activation");
                    await Drain(); DesktopInteractionNative.DwmFlush();
                    await writer.WriteLineAsync("placed");
                }
                else if (command == "count") { await Drain(); await writer.WriteLineAsync(clicks.ToString(CultureInfo.InvariantCulture)); }
                else if (command == "close") { await writer.WriteLineAsync("closed"); return; }
                else throw new InvalidOperationException("Unknown hit-test command");
            }
        }
        finally { target.Close(); }
    }

    private static class DesktopInteractionNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct NativePoint { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct MinMaxInfo
        {
            internal NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
        }
        [DllImport("user32.dll")]
        internal static extern nint WindowFromPoint(NativePoint point);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        internal static extern nint GetWindowLongPtr(nint hwnd, int index);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(nint hwnd);
        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        internal static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("dwmapi.dll")]
        internal static extern int DwmFlush();
    }
}
