using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        yield return Case("desktop overlay is alpha zero outside hover and retains chrome while its menu is open", () =>
            WithDesktopLyrics(async (f, _) =>
            {
                await SaveDesktopLyricFixture(f);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                var window = f.Module.DesktopLyricsWindow!;
                await Drain();
                window.RefreshHover(new(-100000, -100000));
                var toolbar = (StackPanel)window.FindName("Toolbar");
                var backdrop = (Border)window.FindName("HoverSurface");
                True(window.AllowsTransparency && ((SolidColorBrush)window.Background).Color.A == 0 &&
                    backdrop.Visibility == Visibility.Collapsed && toolbar.Visibility == Visibility.Collapsed,
                    "ordinary lyrics have no opaque or almost-transparent rectangle");
                Equal((byte)0, DesktopAlpha(window, 4, 4));
                True(window.IsPointerObserverRunning, "only a visible unlocked window observes its transparent bounds");
                StopDesktopPointerObservation(window);
                DesktopHover(window);
                await Drain();
                window.UpdateLayout();
                Equal(Visibility.Visible, toolbar.Visibility);
                True(DesktopAlpha(window, 4, 4) > 0, "hover paints the actual surface");
                GeneralAppearanceTests.SavePreview((FrameworkElement)window.Content, "desktop-lyrics-hover",
                    (int)window.ActualWidth, (int)window.ActualHeight);
                var toolbarBounds = new Rect(toolbar.TranslatePoint(new(), window), toolbar.RenderSize);
                True(Math.Abs(toolbarBounds.Left + toolbarBounds.Width / 2 - window.ActualWidth / 2) <= 1,
                    $"the complete toolbar is horizontally centered; toolbar={toolbarBounds}, window={window.ActualWidth}x{window.ActualHeight}");
                var controls = toolbar.Children.OfType<Button>().Select(button => button.Name).ToArray();
                True(controls.SequenceEqual(new[] { "ActivateButton", "DelayButton", "AdvanceButton",
                    "PreviousButton", "PlaybackButton", "NextButton", "SettingsButton", "LockButton", "CloseButton" }),
                    "toolbar follows the confirmed action order");
                await AssertLyricTimingIconAsync((Button)window.FindName("DelayButton"), expectCircle: false);
                await AssertLyricTimingIconAsync((Button)window.FindName("AdvanceButton"), expectCircle: false);
                double audio = f.Module.PositionSeconds;
                Click((Button)window.FindName("DelayButton"));
                Equal(.5d, f.Module.LyricOffsetSeconds);
                Click((Button)window.FindName("AdvanceButton"));
                Equal(0d, f.Module.LyricOffsetSeconds);
                Equal(audio, f.Module.PositionSeconds);
                Click((Button)window.FindName("SettingsButton"));
                await Drain();
                var menu = DesktopOpenMenu(window);
                True(ReferenceEquals(menu.Style, window.FindResource("MeowFilm.ContextMenu")),
                    "desktop settings use the shared context menu style");
                foreach (var item in menu.Items.OfType<MenuItem>())
                    True(ReferenceEquals(item.Style, window.FindResource("MeowFilm.MenuItem")),
                        "desktop settings use shared menu item templates");
                window.RefreshHover(new(-100000, -100000));
                Equal(Visibility.Visible, toolbar.Visibility);
                menu.IsOpen = false;
                await Drain();
                window.RefreshHover(new(-100000, -100000));
                Equal(Visibility.Collapsed, toolbar.Visibility);
                window.Hide();
                True(!window.IsPointerObserverRunning, "hidden lyrics release the pointer observer");
                window.Show(); await Drain();
                True(window.IsPointerObserverRunning, "visible lyrics resume their observer");
            }));

        yield return Case("desktop fixed tracks use source rows and append source translation to both lines", () =>
            WithDesktopLyrics(async (f, _) =>
            {
                await SaveDesktopLyricFixture(f);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                var singleHeight = f.Module.DesktopLyricsWindow!.ActualHeight;
                var initialBottom = f.Module.DesktopLyricsWindow.Top + singleHeight;
                await f.Module.SetDesktopLyricLayoutAsync("double");
                var window = f.Module.DesktopLyricsWindow!;
                await Drain();
                await Until(() => window.ActualHeight > singleHeight && window.ActualHeight == window.Height);
                window.UpdateLayout();
                Equal(initialBottom, window.Top + window.ActualHeight);
                var top = (MusicDesktopLyricLineView)window.FindName("TopLine");
                var bottom = (MusicDesktopLyricLineView)window.FindName("BottomLine");
                True(ReferenceEquals(top.Row, f.Module.Lyrics[0]) && ReferenceEquals(bottom.Row, f.Module.Lyrics[1]),
                    "two tracks reference source rows instead of copying or replacing lyrics");
                Equal(HorizontalAlignment.Left, top.HorizontalContentAlignment);
                Equal(HorizontalAlignment.Right, bottom.HorizontalContentAlignment);
                True(((FrameworkElement)top.FindName("TranslationFrame")).IsVisible &&
                    ((FrameworkElement)bottom.FindName("TranslationFrame")).IsVisible, "each original owns its own translation");
                var bottomY = bottom.TranslatePoint(new(), window).Y;
                foreach (var line in new[] { top, bottom })
                {
                    var bounds = new Rect(line.TranslatePoint(new(), window), line.RenderSize);
                    True(bounds.Top >= 44 && bounds.Bottom <= window.ActualHeight,
                        "both originals and translations fit below the toolbar without clipping");
                    var translated = (FrameworkElement)line.FindName("TranslationFrame");
                    var translationBounds = translated.TransformToAncestor(window).TransformBounds(new Rect(translated.RenderSize));
                    True(translationBounds.Bottom <= bounds.Bottom && translationBounds.Bottom <= window.ActualHeight - 12,
                        $"source translations fit their fixed track: row={bounds}, translation={translationBounds}, window={window.ActualHeight}");
                }
                GeneralAppearanceTests.SavePreview((FrameworkElement)window.Content, "desktop-lyrics-double-translated",
                    (int)window.ActualWidth, (int)window.ActualHeight);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleTranslation); await Drain();
                True(!((FrameworkElement)top.FindName("TranslationFrame")).IsVisible &&
                    !((FrameworkElement)bottom.FindName("TranslationFrame")).IsVisible, "the shared switch hides both translations");
                f.Playback.Tick(6); await Drain();
                True(top.Row is null && ReferenceEquals(bottom.Row, f.Module.Lyrics[1]),
                    "the final odd track stays bottom-right and no lyric is fabricated beyond the end");
                Equal(bottomY, bottom.TranslatePoint(new(), window).Y);
                Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("Status")).Visibility);
                await f.Module.SetDesktopLyricLayoutAsync("single"); await Drain();
                await Until(() => window.ActualHeight == singleHeight);
                True(ReferenceEquals(top.Row, f.Module.Lyrics[1]) && !bottom.IsVisible,
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
                var host = new Window
                {
                    Content = line, Width = 760, Height = 160, Left = -32000, Top = -32000,
                    WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                    ShowActivated = false, ShowInTaskbar = false
                };
                try
                {
                    host.Show(); await Drain(); line.UpdateLayout();
                    static void CheckVisibleBlock(MusicDesktopLyricLineView target, int count)
                    {
                        var boxes = Visuals(target).OfType<Viewbox>().Where(box => box.IsVisible).ToArray();
                        Equal(count, boxes.Length);
                        Rect block = Rect.Empty;
                        foreach (var box in boxes)
                            block.Union(new Rect(box.TranslatePoint(new(), target), box.RenderSize));
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
                    True(!((FrameworkElement)line.FindName("TranslationFrame")).IsVisible,
                        "an enabled translation setting without source text creates no empty row");
                }
                finally { host.Close(); await Drain(); }
            }));

        yield return Case("desktop ordinary text never uses highlight and real words reuse the sampled lyric presenter", async () =>
        {
            var row = new MusicDesktopLyricLineView
            {
                Row = new(0, "普通原文 e\u0301 العربية 🙂", "源译文"),
                Normal = Brushes.LightPink, Highlight = Brushes.Gold
            };
            var list = new MusicLyricList
            {
                Position = 1, IsPlaying = false, PlaybackRate = 1,
                PresentationKey = "desktop-render-fixture", Width = 700, Height = 160,
                Background = Brushes.Transparent, BorderThickness = new(0),
                Template = DesktopPresenterTemplate()
            };
            list.Items.Add(row);
            var host = DesktopLyricOwner();
            try
            {
                host.Content = list; host.Show(); await Drain(); list.UpdateLayout();
                var plain = (MusicDesktopLyricTextView)row.FindName("PlainText");
                True(plain.IsVisible && !((ItemsControl)row.FindName("Words")).IsVisible,
                    "line-timed lyrics stay ordinary-colored and have no synthetic karaoke view");
                Equal(Brushes.LightPink, plain.Foreground);
                row.Row = new(0, "真实逐字", "源译文", Words: [new("真实", 0, 2), new("逐字", 2, 2)]);
                await Drain(); list.UpdateLayout(); await Drain();
                var words = Visuals(row).OfType<MusicLyricWordView>().ToArray();
                Equal(2, words.Length);
                Equal(.5, words[0].WordProgress); Equal(0d, words[1].WordProgress);
                True(words.All(word => Equals(word.Foreground, row.Normal) && Equals(word.Highlight, row.Highlight) &&
                    word.Stroke is not null && word.StrokeThickness > 0), "normal highlight and outline remain separate roles");
                var glyphs = DesktopGlyphs(words[0]).ToArray();
                row.Highlight = Brushes.LightBlue; row.Normal = Brushes.White;
                await Drain();
                Equal(.5, words[0].WordProgress);
                True(glyphs.SequenceEqual(DesktopGlyphs(words[0])), "changing colors retains the native glyph cache and progress");
                list.Position = 3; await Drain();
                Equal(1d, words[0].WordProgress); Equal(.5, words[1].WordProgress);
                GeneralAppearanceTests.SavePreview(row, "desktop-lyrics-real-words", 700, 120);
                row.Row = new(0, new string('长', 100), "long source translation");
                row.Width = 660;
                await Drain(); list.UpdateLayout();
                True(row.ActualWidth <= 700 && plain.IsVisible && !Visuals(row).OfType<MusicLyricWordView>().Any(),
                    "long ordinary lines scale without fake words or a stale highlighted layer");
                GeneralAppearanceTests.SavePreview(row, "desktop-lyrics-long-ordinary", 700, 120);
                DesktopTheme.SetIsHighContrast(row, true);
                Render(row, 700, 120); await Drain();
                var ink = DesktopGlyphLayers(plain).Select(glyph => ((SolidColorBrush)glyph.ForegroundBrush).Color).ToArray();
                True(ink.Length > 0 && ink.All(color => color == SystemColors.WindowTextColor),
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
                { Owner = window, ShowActivated = false, Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual };
                editor.Show(); await Drain();
                var selection = (ColorSelection)editor.FindName("Selection");
                var choices = (ComboBox)selection.FindName("Choices");
                choices.SelectedValue = "#FFFFFF"; await Drain();
                Equal(original, f.Module.DesktopLyricNormalColor);
                editor.Close(); await Drain();
                Equal(original, f.Module.DesktopLyricNormalColor);
                True(!window.OwnedWindows.OfType<MusicDesktopLyricColorWindow>().Any(), "cancel releases its owned focusable HWND");
                editor = new(f.Module, "normal")
                { Owner = window, ShowActivated = false, Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual };
                editor.Show(); await Drain();
                selection = (ColorSelection)editor.FindName("Selection");
                choices = (ComboBox)selection.FindName("Choices");
                choices.SelectedValue = "custom"; await Drain();
                var picker = (ColorPicker)selection.FindName("Custom");
                var hex = (TextBox)picker.FindName("SavedHex");
                hex.Text = "#123456";
                // Exercise the shared picker's commit event without injecting global keyboard input.
                typeof(ColorPicker).GetMethod("CommitHex", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(picker, null);
                Equal(original, f.Module.DesktopLyricNormalColor);
                Click((Button)editor.FindName("SaveButton"));
                await Until(() => !editor.IsVisible);
                Equal("#123456", f.Module.DesktopLyricNormalColor);
                await Drain();
                Equal(Color.FromRgb(0x12, 0x34, 0x56),
                    ((SolidColorBrush)((MusicDesktopLyricLineView)window.FindName("TopLine")).Normal).Color);
            }));

        yield return Case("desktop lock uses a separate recoverable HWND and closes every native resource", () =>
            WithDesktopLyrics(async (f, _) =>
            {
                await SaveDesktopLyricFixture(f);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                var window = f.Module.DesktopLyricsWindow!; await Drain();
                var hwnd = new WindowInteropHelper(window).Handle;
                var style = DesktopInteractionNative.GetWindowLongPtr(hwnd, -20);
                True((style & 0x08080080) == 0x08080080 && (style & 0x20) == 0,
                    "the ordinary WPF HWND is layered noactivate and toolwindow, not click-through");
                DesktopHover(window);
                await Drain();
                window.UpdateLayout();
                var lockButton = (Button)window.FindName("LockButton");
                var lockStart = lockButton.PointToScreen(new());
                var lockEnd = lockButton.PointToScreen(new(lockButton.ActualWidth, lockButton.ActualHeight));
                window.SetLocked(true); await Drain();
                var unlock = window.UnlockWindow!;
                window.ApplyHighContrast(true); await Drain();
                True(DesktopTheme.GetIsHighContrast(window) && DesktopTheme.GetIsHighContrast(unlock) &&
                    DesktopTheme.GetIsHighContrast((Button)unlock.Content),
                    "runtime high contrast reaches both independent roots and their controls");
                Equal(SystemColors.WindowTextBrush, ((Button)unlock.Content).Foreground);
                window.ApplyHighContrast(false); await Drain();
                nint unlockHandle = new WindowInteropHelper(unlock).Handle;
                True(unlockHandle != 0 && unlockHandle != hwnd && DesktopInteractionNative.IsWindow(unlockHandle),
                    "unlock is a real independent top-level HWND");
                True((DesktopInteractionNative.GetWindowLongPtr(hwnd, -20) & 0x20) != 0 &&
                    (DesktopInteractionNative.GetWindowLongPtr(unlockHandle, -20) & 0x20) == 0,
                    "only the lyric HWND becomes click-through; style checks alone are not cross-process input evidence");
                True(!window.IsPointerObserverRunning && ((FrameworkElement)window.FindName("Toolbar")).Visibility == Visibility.Collapsed,
                    "locked lyrics stop observing hover and retire the normal toolbar");
                True(unlock.IsVisible, "the independent unlock entry remains visible while locked");
                GeneralAppearanceTests.SavePreview((FrameworkElement)window.Content, "desktop-lyrics-locked",
                    (int)window.ActualWidth, (int)window.ActualHeight);
                GeneralAppearanceTests.SavePreview((FrameworkElement)unlock.Content, "desktop-lyrics-unlock",
                    (int)unlock.ActualWidth, (int)unlock.ActualHeight);
                Equal((nint)3, DesktopInteractionNative.SendMessage(unlockHandle, 0x21, 0, 0));
                var state = window.CapturePresentationState();
                Equal(true, state.IsLocked);
                var windowOrigin = window.PointToScreen(new());
                window.Left += 18; window.Top += 21; await Drain();
                var anchor = (FrameworkElement)window.FindName("UnlockAnchor");
                var point = anchor.PointToScreen(new());
                var movedOrigin = window.PointToScreen(new());
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
                unlockHandle = new WindowInteropHelper(window.UnlockWindow!).Handle;
                window.Close(); await Drain();
                True(!DesktopInteractionNative.IsWindow(hwnd) && !DesktopInteractionNative.IsWindow(unlockHandle) &&
                    !window.IsPointerObserverRunning && window.DataContext is null, "close releases both HWNDs and the observer");
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics); await Drain();
                True(!f.Module.DesktopLyricsWindow!.IsLyricsLocked, "ordinary reopen does not persist locked state");
            }));

        yield return Case("desktop lyric corners and edges resize natively and retain the chosen size", () =>
            WithDesktopLyrics(async (f, _) =>
            {
                await SaveDesktopLyricFixture(f);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                var window = f.Module.DesktopLyricsWindow!;
                var handle = new WindowInteropHelper(window).Handle;
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
                await f.Module.SetDesktopLyricLayoutAsync("double"); await Drain();
                Equal(240d, window.ActualHeight);
                await f.Module.SetDesktopLyricLayoutAsync("single"); await Drain();
                Equal(144d, window.ActualHeight);

                double oldWidth = window.ActualWidth, oldHeight = window.ActualHeight;
                DesktopInteractionNative.SendMessage(handle, 0x0231, 0, 0);
                True(DesktopInteractionNative.SetWindowPos(handle, 0, bounds.Left, bounds.Top,
                    bounds.Right - bounds.Left + 120, bounds.Bottom - bounds.Top + 60, 0x10),
                    "the owned window accepts a native width and height change");
                await Drain();
                DesktopInteractionNative.SendMessage(handle, 0x0232, 0, 0);
                True(window.ActualWidth > oldWidth + 50 && window.ActualHeight > oldHeight + 25,
                    "the native size change reaches WPF in both dimensions");
                double resizedHeight = window.ActualHeight;
                await f.Module.SetDesktopLyricLayoutAsync("double"); await Drain();
                Equal(resizedHeight, window.ActualHeight);
                window.SetLocked(true); await Drain();
                True((DesktopInteractionNative.GetWindowLongPtr(handle, -20) & 0x20) != 0,
                    "resizing does not weaken locked click-through");
                window.SetLocked(false); await Drain();
                Equal(resizedHeight, window.ActualHeight);
            }));

        yield return new(DesktopHitTestCase, () =>
            Environment.GetEnvironmentVariable(DesktopHitPipeVariable) is { } pipe
                ? OnSta(() => DesktopHitChild(pipe))
                : OnSta(() => WithDesktopLyrics(DesktopCrossProcessHits)));
    }

    private static byte DesktopAlpha(MusicDesktopLyricsWindow window, int x, int y)
    {
        var bitmap = DesktopBitmap((FrameworkElement)window.Content);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[3];
    }

    private static RenderTargetBitmap DesktopBitmap(FrameworkElement content)
    {
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),
            (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        return bitmap;
    }

    private static void DesktopHover(MusicDesktopLyricsWindow window)
    {
        True(MusicDesktopLyricsNative.GetWindowRect(new WindowInteropHelper(window).Handle, out var rect), "native lyric bounds exist");
        window.RefreshHover(new(rect.Left + 2, rect.Top + 2));
    }

    private static void StopDesktopPointerObservation(MusicDesktopLyricsWindow window) =>
        ((System.Windows.Threading.DispatcherTimer)typeof(MusicDesktopLyricsWindow)
            .GetField("pointerObserver", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Stop();

    private static ContextMenu DesktopOpenMenu(MusicDesktopLyricsWindow window) =>
        (ContextMenu)typeof(MusicDesktopLyricsWindow).GetField("settingsMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static ControlTemplate DesktopPresenterTemplate()
    {
        const string xaml = """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:l="clr-namespace:MeowFilm.Capabilities;assembly=MeowFilm.Capabilities" TargetType="{x:Type l:MusicLyricList}"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><ItemsPresenter/></ControlTemplate>
            """;
        return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private static IEnumerable<GlyphRun> DesktopGlyphs(Visual view) => DesktopGlyphLayers(view).Select(glyph => glyph.GlyphRun);

    private static IEnumerable<GlyphRunDrawing> DesktopGlyphLayers(Visual view)
    {
        var drawing = VisualTreeHelper.GetDrawing(view);
        return drawing is null ? [] : Glyphs(drawing);
        static IEnumerable<GlyphRunDrawing> Glyphs(Drawing value)
        {
            if (value is GlyphRunDrawing glyph) yield return glyph;
            if (value is DrawingGroup group)
                foreach (var child in group.Children)
                    foreach (var item in Glyphs(child)) yield return item;
        }
    }

    private static async Task DesktopCrossProcessHits(Fixture f, Window _)
    {
        await SaveDesktopLyricFixture(f);
        await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
        var window = f.Module.DesktopLyricsWindow!;
        window.Left = SystemParameters.WorkArea.Left + 20;
        window.Top = SystemParameters.WorkArea.Bottom - window.Height - 20;
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
            True(MusicDesktopLyricsNative.GetWindowRect(new WindowInteropHelper(window).Handle, out var rect), "lyrics have OS bounds");
            await writer.WriteLineAsync(FormattableString.Invariant($"place {rect.Left - 10} {rect.Top - 10} {rect.Right - rect.Left + 20} {rect.Bottom - rect.Top + 20}"));
            Equal("placed", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            var hwnd = new WindowInteropHelper(window).Handle;
            DesktopInteractionNative.GetWindowThreadProcessId(lower, out uint pid);
            True(pid == child.Id && pid != Environment.ProcessId, "the target HWND belongs to the separate controlled process");
            True(DesktopInteractionNative.SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x13),
                "place our lyric HWND above our child without activating either");
            window.RefreshHover(new(-100000, -100000)); await Drain();
            DesktopInteractionNative.DwmFlush();
            var glyph = DesktopOpaqueLyricPoint(window);
            var blank = window.PointToScreen(new(8, window.ActualHeight - 8));
            await DesktopHit(glyph, hwnd);
            await DesktopHit(blank, lower);
            DesktopHover(window); await Drain(); DesktopInteractionNative.DwmFlush();
            await DesktopHit(blank, hwnd);
            window.SetLocked(true); await Drain(); DesktopInteractionNative.DwmFlush();
            await DesktopHit(glyph, lower);
            await DesktopHit(blank, lower);
            var unlock = window.UnlockWindow!;
            var unlockHandle = new WindowInteropHelper(unlock).Handle;
            var unlockPoint = unlock.PointToScreen(new(16, 16));
            await DesktopHit(unlockPoint, unlockHandle);
            var local = DesktopPoint(glyph);
            True(DesktopInteractionNative.ScreenToClient(lower, ref local), "convert the observed target's own coordinates");
            nint packed = (nint)((local.Y << 16) | (local.X & 0xffff));
            DesktopInteractionNative.SendMessage(lower, 0x201, 1, packed);
            DesktopInteractionNative.SendMessage(lower, 0x202, 0, packed);
            await writer.WriteLineAsync("count");
            Equal("1", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Click((Button)unlock.Content); StopDesktopPointerObservation(window);
            await Drain(); DesktopHover(window); DesktopInteractionNative.DwmFlush();
            await DesktopHit(blank, hwnd);
            await writer.WriteLineAsync("close");
            Equal("closed", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Equal(0, child.ExitCode);
            Console.WriteLine("DESKTOP_LYRICS_OS_HIT_TEST other-process glyph+blank -> lower HWND; independent unlock -> unlock HWND; directed message count=1; no global input or foreground activation");
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
        // WPF's layered surface reaches the OS on its compositor frame, not on a dispatcher drain.
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

    private static Point DesktopOpaqueLyricPoint(MusicDesktopLyricsWindow window)
    {
        var content = (FrameworkElement)window.Content;
        var bitmap = DesktopBitmap(content);
        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, stride, 0);
        for (int y = 48; y < bitmap.PixelHeight - 12; y++)
            for (int x = 22; x < bitmap.PixelWidth - 22; x++)
            {
                bool insideInk = true;
                for (int dy = -1; dy <= 1 && insideInk; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (pixels[(y + dy) * stride + (x + dx) * 4 + 3] < 220) { insideInk = false; break; }
                if (insideInk) return content.PointToScreen(new(x + .5, y + .5));
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
        var target = new Window
        {
            Width = 800, Height = 300, Left = -32000, Top = -32000, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowActivated = false, ShowInTaskbar = false, Topmost = true,
            Background = Brushes.White, Title = "meowfilm controlled lyric hit target"
        };
        target.Show();
        var hwnd = new WindowInteropHelper(target).Handle;
        var source = HwndSource.FromHwnd(hwnd)!;
        int clicks = 0;
        nint Hook(nint handle, int message, nint wParam, nint lParam, ref bool handled)
        {
            if (message == 0x21) { handled = true; return 3; }
            if (message == 0x202) clicks++;
            return 0;
        }
        source.AddHook(Hook);
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
                else if (command == "count") await writer.WriteLineAsync(clicks.ToString(CultureInfo.InvariantCulture));
                else if (command == "close") { await writer.WriteLineAsync("closed"); return; }
                else throw new InvalidOperationException("Unknown hit-test command");
            }
        }
        finally { if (!source.IsDisposed) source.RemoveHook(Hook); target.Close(); }
    }

    private static class DesktopInteractionNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct NativePoint { internal int X, Y; }
        [DllImport("user32.dll")]
        internal static extern nint WindowFromPoint(NativePoint point);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        internal static extern nint GetWindowLongPtr(nint hwnd, int index);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(nint hwnd);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ScreenToClient(nint hwnd, ref NativePoint point);
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
