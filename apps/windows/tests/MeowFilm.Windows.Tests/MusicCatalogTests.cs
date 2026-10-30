using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.Core.Runtime;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Platform.Storage;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;
using System.Windows.Media.Imaging;

namespace MeowFilm.Windows.Tests;

// Real WPF + Native application/catalog/platform protocols + DPAPI. Synthetic
// HttpMessageHandler replaces upstream; only explicit authored LX needs Node.
public static class MusicCatalogTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("entry presentation contract separates preview overlay animated and static behavior", async () =>
        {
            var preview = new MusicCatalogCard("playlist", "歌单", "真实预览", "", "playlist", "wy", ActionId: "1",
                Presentation: MusicCatalogEntryPresentation.Preview);
            var overlay = new MusicCatalogCard("similar", "相似歌曲", "遮罩播放", "", "similar", "wy", ActionId: "similar",
                Presentation: MusicCatalogEntryPresentation.Overlay);
            var animated = new MusicCatalogCard("fm", "私人漫游", "动态入口", "", "fm", "wy", ActionId: "fm",
                Presentation: MusicCatalogEntryPresentation.Animated);
            var card = new MusicCatalogCard("artist-entry", "相似艺人", "静态入口", "",
                "unsupported", "wy", Available: false, ErrorCode: "UNSUPPORTED",
                Presentation: MusicCatalogEntryPresentation.Static);
            True(preview.IsPreviewEntry && preview.CanRequestPreview && !preview.IsOverlayEntry && !preview.IsAnimatedEntry,
                "preview contract");
            True(overlay.IsOverlayEntry && !overlay.IsPreviewEntry && !overlay.IsAnimatedEntry && !overlay.CanRequestPreview,
                "overlay contract");
            True(animated.IsAnimatedEntry && !animated.IsPreviewEntry && !animated.IsOverlayEntry && !animated.CanRequestPreview,
                "animated contract");
            True(!card.IsPreviewEntry && !card.IsOverlayEntry && !card.IsAnimatedEntry && !card.CanRequestPreview,
                "static contract");
            var previewEntry = new EntryCard { DataContext = preview };
            var previewRequests = 0; previewEntry.PreviewRequested += (_, _) => previewRequests++;
            previewEntry.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            Equal(1, previewRequests);
            foreach (var passive in new[] { overlay, animated, card })
            {
                var passiveEntry = new EntryCard { DataContext = passive };
                var requests = 0; passiveEntry.PreviewRequested += (_, _) => requests++;
                passiveEntry.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
                Equal(0, requests);
            }
            var entry = new EntryCard { DataContext = card };
            var window = new Window { Content = entry, Width = 152, Height = 200, WindowStyle = WindowStyle.None,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                var surface = (Grid)entry.FindName("Surface");
                True(entry.IsEnabled && surface.IsEnabled, "unsupported action must not disable the visual hover surface");
                True(!((Button)entry.FindName("OpenButton")).IsEnabled && !((Button)entry.FindName("PlayButton")).IsEnabled,
                    "unsupported open and play remain denied; artwork retry is a separate control");
                entry.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
                Equal(0, entry.PreviewTitles.Count);
                CardMotion.Transition(surface, true, false); await Drain();
                var caption = Descendants<StackPanel>(entry).Single(p => p.RenderTransform is TranslateTransform);
                Equal(0d, ((TranslateTransform)caption.RenderTransform).Y);
                CardMotion.Transition(surface, false, false); await Drain();
                Equal(0d, ((TranslateTransform)caption.RenderTransform).Y);
                Equal(Visibility.Collapsed, ((Button)entry.FindName("PlayButton")).Visibility);
            }
            finally { window.Close(); }
        });
        yield return Case("catalog card types derive distinct colors from one artwork sample", () =>
        {
            var raw = new SolidColorBrush(Color.FromRgb(83, 74, 85));
            var entry = new MusicCatalogPaletteConverter { Role = MusicCatalogPaletteRole.Entry };
            var footer = new MusicCatalogPaletteConverter { Role = MusicCatalogPaletteRole.CoverFooter };
            SolidColorBrush Entry(MusicCatalogEntryPresentation presentation) =>
                (SolidColorBrush)entry.Convert([raw, presentation], typeof(Brush), null!,
                    System.Globalization.CultureInfo.InvariantCulture);
            var preview = Entry(MusicCatalogEntryPresentation.Preview);
            var overlay = Entry(MusicCatalogEntryPresentation.Overlay);
            var animated = Entry(MusicCatalogEntryPresentation.Animated);
            var cover = (SolidColorBrush)footer.Convert([raw], typeof(Brush), null!,
                System.Globalization.CultureInfo.InvariantCulture);
            Equal(Color.FromRgb(83, 74, 85), preview.Color);
            Equal(Color.FromRgb(78, 62, 62), overlay.Color);
            Equal(overlay.Color, animated.Color);
            Equal(Color.FromRgb(65, 58, 66), cover.Color);
            True(preview.IsFrozen && overlay.IsFrozen && animated.IsFrozen && cover.IsFrozen,
                "catalog palette results are immutable");
            True(preview.Color != overlay.Color && preview.Color != cover.Color && overlay.Color != cover.Color,
                "preview, overlay entry and ordinary cover own separate palette policies");

            var daily = new SolidColorBrush(Color.FromRgb(169, 149, 142));
            Equal(daily.Color, ((SolidColorBrush)entry.Convert(
                [daily, MusicCatalogEntryPresentation.Preview], typeof(Brush), null!,
                System.Globalization.CultureInfo.InvariantCulture)).Color);
            return Task.CompletedTask;
        });
        yield return Case("home headings use system typography and unclipped refresh without extra options", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default);
            var view = new MusicCatalogView { DataContext = f.Module };
            using var source = new HwndSource(new HwndSourceParameters("catalog-system-typography-fixture")
            {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
                PositionX = -32000, PositionY = -32000, Width = 900, Height = 700
            });
            try
            {
                source.RootVisual = view;
                view.Measure(new(900, 700)); view.Arrange(new Rect(0, 0, 900, 700)); view.UpdateLayout(); await Drain();
                var buttons = Descendants<Button>(view).Where(b => b.IsVisible).ToArray();
                var channelMore = (System.Windows.Controls.Primitives.Popup)view.FindName("ChannelMorePopup");
                Equal(System.Windows.Controls.Primitives.PopupAnimation.None, channelMore.PopupAnimation);
                Equal(System.Windows.Controls.Primitives.PlacementMode.Custom, channelMore.Placement);
                Equal(572d, ((Border)channelMore.Child).Width);
                True(view.FindName("ChannelMoreClip") is RectangleGeometry,
                    "more channels use a top-centred clip reveal instead of popup scaling");
                var refreshes = buttons.Where(b => AutomationProperties.GetName(b) == "刷新推荐").ToArray();
                True(refreshes.Length > 0, "real headings have refresh controls");
                foreach (var refresh in refreshes)
                {
                    Equal(24d, refresh.ActualHeight); Equal(0d, refresh.MinHeight);
                    Equal(Color.FromRgb(139,144,150), ((SolidColorBrush)refresh.Foreground).Color);
                    True(Descendants<System.Windows.Shapes.Path>(refresh).Any(), "vector refresh, not clipped font glyph");
                    var canvas = Descendants<Canvas>(refresh).Single();
                    True(canvas.TranslatePoint(new Point(), refresh).Y >= 0 && canvas.ActualHeight <= refresh.ActualHeight,
                        "complete icon fits within actual button bounds");
                }
                var body = SystemFonts.MessageFontFamily;
                var title = Descendants<TextBlock>(view).First(text => text.IsVisible &&
                    !string.IsNullOrWhiteSpace(text.Text) && text.Style == view.FindResource("Catalog.SectionTitle"));
                Equal(body, title.FontFamily); Equal(18d, title.FontSize);
                Equal(FontWeights.SemiBold, title.FontWeight);
                var playlist = Descendants<TextBlock>(view).First(text => text.IsVisible &&
                    !string.IsNullOrWhiteSpace(text.Text) && text.Style == view.FindResource("Catalog.PlaylistTitle"));
                Equal(body, playlist.FontFamily); Equal(13d, playlist.FontSize);
                Equal(FontWeights.Normal, playlist.FontWeight); Equal(20d, playlist.LineHeight);
                Equal(Color.FromRgb(75,79,88), ((SolidColorBrush)playlist.Foreground).Color);
                var catalogText = Descendants<TextBlock>(view).ToArray();
                AssertGrayscaleText(catalogText, "actual catalog headings and captions");
                foreach (var text in new[] { title, playlist })
                foreach (var mode in new[] { TextRenderingMode.ClearType, TextRenderingMode.Aliased })
                {
                    var originalMode = TextOptions.GetTextRenderingMode(text);
                    text.SetCurrentValue(TextOptions.TextRenderingModeProperty, mode);
                    try
                    {
                        Equal(mode, TextOptions.GetTextRenderingMode(text));
                        Equal(TextRenderingMode.Grayscale, TextOptions.GetTextRenderingMode(view));
                        var rejected = false;
                        try { AssertGrayscaleText(catalogText, "actual catalog headings and captions"); }
                        catch (InvalidOperationException) { rejected = true; }
                        True(rejected, $"the text-node contract must reject {mode} on '{text.Text}' even with a grayscale parent");
                    }
                    finally { text.SetCurrentValue(TextOptions.TextRenderingModeProperty, originalMode); }
                    AssertGrayscaleText(catalogText, "actual catalog headings and captions");
                }
            }
            finally { source.RootVisual = null; await Drain(); }
        });
        yield return Case("home text follows the system font and entry captions stay pixel aligned after resize", async () =>
        {
            var card = new MusicCatalogCard("500", "每日推荐", "推荐歌曲", "", "playlist", "wy", ActionId: "500",
                Presentation: MusicCatalogEntryPresentation.Preview);
            var view = new MusicCatalogView();
            var entry = new EntryCard { DataContext = card, PreviewTitles = ["第一首", "第二首", "第三首"] };
            view.Content = entry;
            using var source = new HwndSource(new HwndSourceParameters("entry-system-typography-fixture")
            {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
                PositionX = -32000, PositionY = -32000, Width = 240, Height = 300
            });
            void Layout()
            { view.Measure(new(240, 300)); view.Arrange(new Rect(0, 0, 240, 300)); view.UpdateLayout(); }
            try
            {
                source.RootVisual = view; Layout(); await Drain();
                Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(view));
                Equal(TextRenderingMode.Grayscale, TextOptions.GetTextRenderingMode(entry));
                Equal(SystemFonts.MessageFontFamily, view.FontFamily);
                Equal(SystemFonts.MessageFontFamily, entry.FontFamily);
                True(view.UseLayoutRounding && entry.UseLayoutRounding && entry.SnapsToDevicePixels,
                    "text and cover layout align to the current display pixels");
                var alternate = new FontFamily("Arial");
                view.Resources[SystemFonts.MessageFontFamilyKey] = alternate;
                await Drain();
                Equal(alternate, view.FontFamily); Equal(alternate, entry.FontFamily);
                var surface = (Grid)entry.FindName("Surface");
                var caption = (StackPanel)entry.FindName("Caption");
                var subtitle = Descendants<TextBlock>(caption).First();
                Equal(alternate, subtitle.FontFamily);
                var entryText = Descendants<TextBlock>(entry).ToArray();
                Equal(3, entry.PreviewTitles.Count);
                True(entry.PreviewTitles.All(preview => entryText.Any(text => text.Text == preview)),
                    "entry typography covers the actual generated preview text");
                True(((Button)entry.FindName("OpenButton")).FocusVisualStyle is not null,
                    "entry actions keep the shared rounded keyboard focus");
                foreach (var width in new[] { 152.4, 180.8, 200.6 })
                {
                    entry.Width = width;
                    Layout(); await Drain();
                    CardMotion.Transition(surface, false, false);
                    await Drain();
                    AssertGrayscaleText(Descendants<TextBlock>(entry), "resting entry title caption and preview");
                    Equal(0d, ((TranslateTransform)caption.RenderTransform).Y);
                    var y = caption.TranslatePoint(new Point(), surface).Y;
                    var pixelY = y * VisualTreeHelper.GetDpi(surface).DpiScaleY;
                    True(Math.Abs(pixelY - Math.Round(pixelY)) < .01, "caption baseline starts on a display pixel");
                    var cover = Descendants<ArtworkImage>(surface).Single();
                    True(y >= cover.ActualHeight && y + subtitle.ActualHeight <= surface.ActualHeight,
                        "the resting caption fits in its footer after resize");
                    var clip = (RectangleGeometry)surface.Clip;
                    True(clip.RadiusX > 0 && clip.RadiusY > 0, "the complete entry has rounded corners");
                    Equal(surface.RenderSize, clip.Rect.Size);
                    CardMotion.Transition(surface, true, false);
                    await Drain();
                    AssertGrayscaleText(Descendants<TextBlock>(entry), "raised entry title caption and preview");
                    True(((TranslateTransform)caption.RenderTransform).Y < 0, "preview rises over its artwork");
                    var raisedY = caption.TranslatePoint(new Point(), surface).Y * VisualTreeHelper.GetDpi(surface).DpiScaleY;
                    True(Math.Abs(raisedY - Math.Round(raisedY)) < .01, "settled preview also starts on a display pixel");
                    var layoutClip = LayoutInformation.GetLayoutClip(caption);
                    True(layoutClip is null || layoutClip.Bounds.Height >= caption.DesiredSize.Height,
                        "all preview rows remain outside the footer's layout constraint");
                }
            }
            finally { source.RootVisual = null; await Drain(); }
        });
        yield return Case("catalog system typography uses effective grayscale text and paints neutral glyphs at bitmap DPI", () =>
        {
            var view = new MusicCatalogView();
            var host = new Grid { Background = Brushes.White };
            var text = new TextBlock { Text = "二次元赛高", Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0) };
            host.Children.Add(text); view.Content = host;
            Equal(SystemFonts.MessageFontFamily, view.FontFamily);
            Equal(TextRenderingMode.Grayscale, TextOptions.GetTextRenderingMode(view));
            foreach (var role in new[] { "Catalog.SectionTitle", "Catalog.SongTitle", "Catalog.PlaylistTitle" })
            {
                text.Style = (Style)view.FindResource(role);
                view.Measure(new(200, 52)); view.Arrange(new Rect(0, 0, 200, 52)); view.UpdateLayout();
                Equal(SystemFonts.MessageFontFamily, text.FontFamily);
                AssertGrayscaleText([text], role);
                // RenderTargetBitmap has no HWND: scaled neutral pixels prove glyph drawing,
                // not OS subpixel rendering at a monitor's DPI.
                foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
                {
                    var bitmap = new RenderTargetBitmap((int)(200 * scale), (int)(52 * scale),
                        96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bitmap.Render(view);
                    var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                    bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
                    var ink = 0;
                    for (var index = 0; index < bytes.Length; index += 4)
                    {
                        True(Math.Abs(bytes[index] - bytes[index + 1]) <= 1 &&
                            Math.Abs(bytes[index + 1] - bytes[index + 2]) <= 1,
                            "offscreen neutral glyph pixels keep matching RGB channels");
                        if (bytes[index + 3] > 240 && bytes[index] < 200) ink++;
                    }
                    True(ink > 50, "offscreen drawing evidence covers actual painted glyphs, not empty output");
                }
            }
            return Task.CompletedTask;
        });
        yield return Case("home controls execute through automation patterns on a hidden nonactivating source", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Configure(new { detailLimit = 3, total = 3, homePages = 2 });
            await f.SetupSource();
            await f.SearchPolicy("wy", playlists: true);
            f.Module.CatalogSearchKind = "歌单";
            var shell = new ShellWindow([f.Module], () => Task.CompletedTask);
            shell.PrepareForRender();
            var root = (Border)shell.Content; shell.Content = null;
            root.Resources.MergedDictionaries.Add(shell.Resources);
            root.DataContext = shell.DataContext;
            root.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Window.Background)) { Source = shell });
            TextElement.SetFontFamily(root, shell.FontFamily); TextElement.SetFontSize(root, shell.FontSize);
            TextElement.SetForeground(root, shell.Foreground);
            TextOptions.SetTextFormattingMode(root, TextOptions.GetTextFormattingMode(shell));
            TextOptions.SetTextRenderingMode(root, TextOptions.GetTextRenderingMode(shell));
            root.UseLayoutRounding = true; root.SnapsToDevicePixels = true;
            var parameters = new HwndSourceParameters("catalog-headless-control-fixture")
            {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
                PositionX = -32000, PositionY = -32000, Width = 1110, Height = 800
            };
            using var source = new HwndSource(parameters);
            void Layout()
            { root.Measure(new(1110, 800)); root.Arrange(new Rect(0, 0, 1110, 800)); root.UpdateLayout(); }
            void Invoke(Button button)
            {
                True(button.IsEnabled, "headless automation invokes only enabled controls");
                var peer = UIElementAutomationPeer.CreatePeerForElement(button)
                    ?? throw new InvalidOperationException("Missing button automation peer");
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            }
            try
            {
                source.RootVisual = root; Layout();
                Equal(SystemFonts.MessageFontFamily, shell.FontFamily);
                Equal(SystemFonts.MessageFontFamily, ((Control)f.Module.SettingsView).FontFamily);
                Equal(SystemFonts.MessageFontFamily, ((Control)f.Module.PlayerView).FontFamily);
                Equal(TextRenderingMode.Grayscale, TextOptions.GetTextRenderingMode(f.Module.SettingsView));
                Equal(TextRenderingMode.Grayscale, TextOptions.GetTextRenderingMode(f.Module.PlayerView));
                var detail = f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
                var queue = f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayQueue);
                Equal(SystemFonts.MessageFontFamily, ((Control)detail).FontFamily);
                Equal(SystemFonts.MessageFontFamily, ((Control)queue).FontFamily);
                Equal(TextRenderingMode.Grayscale, TextOptions.GetTextRenderingMode(detail));
                Equal(TextRenderingMode.Grayscale, TextOptions.GetTextRenderingMode(queue));
                shell.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                await Until(() => f.Module.IsCatalogHome && f.Client.Count("music.catalog.home") == 1 &&
                    !f.Module.Busy && ((ListBox)shell.FindName("NavigationList")).SelectedItem is
                        ShellNavigationEntry { Route: "catalog-home" }, "headless home startup did not settle");
                Layout(); await Drain();
                Equal(IntPtr.Zero, new WindowInteropHelper(shell).Handle);
                True((parameters.WindowStyle & 0x10000000) == 0 && (parameters.ExtendedWindowStyle & 0x08000000) != 0,
                    "the isolated presentation source is neither visible nor activatable");
                var catalog = (MusicCatalogView)f.Module.View.FindName("CatalogView");
                var search = (TextBox)shell.FindName("SearchBox");
                var searchPeer = UIElementAutomationPeer.CreatePeerForElement(search)!;
                var value = (IValueProvider)searchPeer.GetPattern(PatternInterface.Value);
                var searchButton = (Button)shell.FindName("SearchButton");
                var back = (Button)shell.FindName("BackButton");
                var player = f.Module.PlayerView;
                value.SetValue("   "); Invoke(searchButton); await Drain();
                Equal(0, f.Client.Count("music.search.start"));
                var refresh = Descendants<Button>(catalog).First(button =>
                    button.IsVisible && AutomationProperties.GetName(button) == "刷新推荐");
                Invoke(refresh);
                await Until(() => f.Client.Count("music.catalog.home") == 2 && !f.Module.Busy,
                    "headless refresh did not settle");
                Layout(); await Drain();
                var entry = Descendants<EntryCard>(catalog).First(control =>
                    control.DataContext is MusicCatalogCard { CanRequestPreview: true });
                var surface = (Grid)entry.FindName("Surface");
                entry.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
                await Until(() => entry.PreviewTitles.Count == 3, "headless preview did not settle");
                CardMotion.Transition(surface, true, false); await Drain();
                Equal(1, f.Client.Count("music.catalog.playlist"));
                True(Descendants<TextBlock>(entry).Any(text => text.Text.StartsWith("1 合成曲", StringComparison.Ordinal)),
                    "preview controls publish the actual native preview receipt");
                Invoke((Button)entry.FindName("PlayButton"));
                await Until(() => f.Playback.Opens.Count == 1 && !f.Module.Busy, "headless play did not settle");
                Layout(); await Drain();
                var playingTitle = Descendants<TextBlock>(player).Single(text => text.Text == f.Module.CurrentTitle);
                var playingArtist = Descendants<TextBlock>(player).Single(text => text.Text == f.Module.CurrentArtist);
                AssertGrayscaleText([playingTitle, playingArtist], "actual playing title and artist");
                Equal(1, f.Client.Count("music.catalog.playlist.prepare"));
                Equal(1, f.Client.Count("music.catalog.playlist.commit"));
                True(f.Module.IsCatalogHome, "the entry play action does not navigate");
                entry.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
                CardMotion.Transition(surface, false, false);
                value.SetValue("  云歌单  "); Invoke(searchButton);
                await Until(() => f.Client.Count("music.search.start") == 1 && f.Module.IsAggregateSearch,
                    "headless search did not settle");
                await WaitForSearch(f.Module); Layout();
                Equal("云歌单", f.Client.Last("music.search.start").GetProperty("keyword").GetString());
                Equal("playlists", f.Client.Last("music.search.start").GetProperty("kind").GetString());
                True(f.Module.CatalogSections.SelectMany(section => section.Items).Any(card => card.CanOpen),
                    "the real search handler publishes actionable native results");
                Invoke(back);
                await Until(() => f.Module.IsCatalogHome && !f.Module.Busy, "headless search back did not settle");
                Layout(); await Drain();
                True(ReferenceEquals(player, ((ContentControl)shell.FindName("PlayerHost")).Content),
                    "search and back retain the same player controls");
                entry = Descendants<EntryCard>(catalog).First(control =>
                    control.DataContext is MusicCatalogCard { CanRequestPreview: true });
                Invoke((Button)entry.FindName("OpenButton"));
                await Until(() => f.Module.IsCatalogPlaylist && f.Module.Songs.Count == 3 && !f.Module.Busy,
                    "headless entry open did not settle");
                Equal(1, f.Playback.Opens.Count);
                Invoke(back);
                await Until(() => f.Module.IsCatalogHome && !f.Module.Busy, "headless detail back did not settle");
                Layout(); await Drain();
                var scroll = (ScrollViewer)catalog.FindName("CatalogScroll");
                var pages = f.Client.Count("music.catalog.home");
                scroll.ScrollToEnd(); Layout(); await Drain();
                Equal(pages, f.Client.Count("music.catalog.home"));
                scroll.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.PageDown)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                await Until(() => f.Client.Count("music.catalog.home") == pages + 1 && !f.Module.Busy,
                    "headless page intent did not settle");
                True(!f.Module.CanCatalogNext, "the direct page intent stops at the native continuation boundary");
                foreach (var size in new[] { new Size(860, 600), new Size(1110, 800), new Size(1440, 900) })
                {
                    root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
                    var content = (Grid)shell.FindName("ContentRegion");
                    var footer = (Border)shell.FindName("PlayerFrame");
                    True(content.TranslatePoint(new Point(0, content.ActualHeight), root).Y <=
                        footer.TranslatePoint(new Point(), root).Y + .5, "the footer does not cover the content");
                    var hit = root.InputHitTest(searchButton.TranslatePoint(new Point(16, 16), root));
                    True(hit is DependencyObject target && Descendants<DependencyObject>(searchButton).Contains(target),
                        "the search icon occupies its actual input hit target");
                    var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var pixels = new byte[(int)size.Width * (int)size.Height * 4];
                    bitmap.CopyPixels(pixels, (int)size.Width * 4, 0);
                    True(pixels.Where((_, index) => index % 4 == 3).Count(alpha => alpha > 240) >
                        size.Width * size.Height * .8, "the hidden source paints real opaque app content");
                }
                True(Mouse.Captured is null, "the headless interaction sequence never captures the pointer");
                Equal(IntPtr.Zero, new WindowInteropHelper(shell).Handle);
                Console.WriteLine("Headless home evidence: hidden NOACTIVATE source; Value/Invoke providers; " +
                    "refresh, preview, play, search, back, open and paging; no product Window.Show or native input.");
            }
            finally
            {
                source.RootVisual = null;
                shell.Close(); await shell.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(8)); await Drain();
            }
        });
        yield return Case("each catalog section selects exactly one presenter", async () =>
        {
            var view = new MusicCatalogView();
            var presenterStyle = (Style)view.FindResource("Catalog.SectionPresenter");
            var song = SongRow.Read(MusicSongFixture.Element(
                MusicSongFixture.Online("wy", "1", "歌曲", "歌手", "专辑", 180)));
            var playlist = new MusicCatalogCard("p", "歌单", "", "", "playlist", "wy", ActionId: "p");
            var artist = new MusicCatalogCard("a", "歌手", "单曲：1", "", "artist", "wy", ActionId: "a");
            var ranking = playlist with
            {
                Preview = [new MusicRankPreview("歌曲", "歌手", "", 1, 0)]
            };
            var cases = new[]
            {
                (new MusicCatalogSection("歌单", [playlist], Type: "playlists"), "Catalog.Section.Shelf", MusicCatalogSectionPresentation.Shelf),
                (new MusicCatalogSection("榜单推荐", [ranking], Type: "rankings", Layout: "ONLY_COVER"), "Catalog.Section.Shelf", MusicCatalogSectionPresentation.RankingCovers),
                (new MusicCatalogSection("精选榜", [playlist], Type: "rankings", Layout: "ONLY_COVER"), "Catalog.Section.Shelf", MusicCatalogSectionPresentation.RankingCovers),
                (new MusicCatalogSection("曲风榜", [playlist], Type: "rankings", Layout: "ONLY_COVER"), "Catalog.Section.Shelf", MusicCatalogSectionPresentation.RankingCovers),
                (new MusicCatalogSection("官方榜", [playlist], Type: "rankings", Layout: "ranked-songs"), "Catalog.Section.Rankings", MusicCatalogSectionPresentation.RankingPreviews),
                (new MusicCatalogSection("会员榜", [playlist], Type: "rankings", Layout: "vip-ranked-songs"), "Catalog.Section.Rankings", MusicCatalogSectionPresentation.RankingPreviews),
                (new MusicCatalogSection("官方榜数据", [ranking], Type: "rankings"), "Catalog.Section.Rankings", MusicCatalogSectionPresentation.RankingPreviews),
                (new MusicCatalogSection("歌曲", [new MusicCatalogCard("s", "歌曲", "歌手", "", "song", "wy", song)]), "Catalog.Section.Songs", MusicCatalogSectionPresentation.Songs),
                (new MusicCatalogSection("", [artist], Type: "artists", Layout: "artist-grid"), "Catalog.Section.Artists", MusicCatalogSectionPresentation.Artists)
            };
            foreach (var (section, resource, presentation) in cases)
            {
                Equal(presentation, section.Presentation);
                if (presentation == MusicCatalogSectionPresentation.RankingCovers)
                    True(!section.IsPlaylistSection, section.Title + " must not fall into playlist rows");
                var presenter = new ContentControl { DataContext = section, Content = section, Style = presenterStyle };
                var window = new Window { Content = presenter, Width = 900, Height = 300, WindowStyle = WindowStyle.None,
                    Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
                try
                {
                    window.Show(); window.UpdateLayout(); await Drain();
                    True(ReferenceEquals(view.FindResource(resource), presenter.ContentTemplate),
                        section.Title + " chooses one presenter");
                }
                finally { window.Close(); }
            }
        });
        yield return Case("channel chrome owns direct tabs filters and no duplicate platform selector", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-channels", default);
            await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(card => card.ActionId == "feature"));
            var view = new MusicCatalogView { DataContext = f.Module };
            var window = new Window
            {
                Content = view, Width = 1042, Height = 700, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false
            };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                var header = (DockPanel)view.FindName("ChannelHeader");
                Equal(Visibility.Visible, header.Visibility);
                Equal(new Thickness(40, 0, 40, 12), header.Margin);
                True(!Descendants<ComboBox>(view).Any(combo => combo.IsVisible &&
                    AutomationProperties.GetName(combo) == "内容平台"),
                    "channel chrome has no duplicate platform selector");
                var direct = Descendants<Button>(header)
                    .Where(button => button.Tag is MusicCatalogCard).ToArray();
                Equal("feature,playlist,chart,artist,vip,euro,djR,huayu,qingge,acg", string.Join(",", direct.Select(button =>
                    ((MusicCatalogCard)button.Tag).ActionId)));
                Equal("精选,歌单广场,排行榜,歌手,VIP,欧美,电音,华语,情歌,二次元", string.Join(",", direct.Select(button =>
                    ((MusicCatalogCard)button.Tag).Title)));
                var selected = direct.Single(button => ((MusicCatalogCard)button.Tag).ActionId == "feature");
                var underline = Descendants<Border>(selected)
                    .Single(border => border.Name == "SelectionUnderline");
                Equal(16d, underline.ActualWidth); Equal(3d, underline.ActualHeight);
                Equal(((SolidColorBrush)view.FindResource("MeowFilm.Brush.Accent")).Color,
                    ((SolidColorBrush)underline.Background).Color);

                await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(card => card.ActionId == "rock"));
                await f.Module.CatalogTagAsync(f.Module.CatalogTags.Single(tag => tag.Id == "30"));
                window.UpdateLayout(); await Drain();
                var filters = (ItemsControl)view.FindName("ChannelFilters");
                Equal(Visibility.Visible, filters.Visibility);
                Equal(new Thickness(40, 0, 40, 16), filters.Margin);
                var tagButtons = Descendants<Button>(filters)
                    .Where(button => button.Tag is MusicCatalogTag).ToArray();
                Equal(2, tagButtons.Length);
                True(tagButtons.All(button => button.ActualHeight == 32 && button.ActualWidth >= 62),
                    "channel filters use the measured pill geometry");
                var selectedTag = f.Module.SelectedCatalogTag ??
                    throw new InvalidOperationException("channel selected tag");
                var selectedPill = tagButtons.Single(button =>
                    ((MusicCatalogTag)button.Tag).Id == selectedTag.Id);
                Equal(((SolidColorBrush)view.FindResource("MeowFilm.Brush.AccentSoft")).Color,
                    ((SolidColorBrush)selectedPill.Background).Color);
                Equal(new CornerRadius(16), Descendants<Border>(selectedPill)
                    .Single(border => border.Name == "ChannelFilterPill").CornerRadius);
            }
            finally { window.Close(); }
        });
        yield return Case("VIP route owns one three-card row and clears it outside the retained page", async () =>
        {
            await using var f = await Fixture.Create(vipAccount: true);
            await f.Module.NavigateAsync("catalog-channels", default);
            await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(card => card.ActionId == "vip"));
            var vip = f.Vip ?? throw new InvalidOperationException("VIP fixture");
            Equal(0, vip.Reads);

            var page = (MusicView)f.Module.View;
            var window = new Window
            {
                Content = page, Width = 1042, Height = 700, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false
            };
            try
            {
                window.Show();
                await Until(() => f.Module.VipPage.HasCards && vip.Reads == 1,
                    "VIP cards did not activate after the page loaded");
                window.UpdateLayout(); await Drain();

                var catalog = (MusicCatalogView)page.FindName("CatalogView");
                var row = (Grid)catalog.FindName("VipCardRow");
                var account = (Border)catalog.FindName("VipAccountTile");
                var sign = (Border)catalog.FindName("VipSignCard");
                var banner = (Button)catalog.FindName("VipBannerButton");
                Equal(Visibility.Visible, row.Visibility);
                Equal(960d, row.ActualWidth); Equal(184d, row.ActualHeight);
                Equal(new Thickness(40, 0, 40, 32), row.Margin);
                Equal("228,16,228,16,472", string.Join(",", row.ColumnDefinitions.Select(column =>
                    column.ActualWidth.ToString("0", System.Globalization.CultureInfo.InvariantCulture))));
                True(ReferenceEquals(Ancestor<ScrollViewer>(row), catalog.CatalogScroll),
                    "VIP cards belong to the same vertical transition and retention surface as the sections");
                Equal(Visibility.Visible, account.Visibility); Equal(228d, account.ActualWidth);
                Equal(Visibility.Visible, sign.Visibility); Equal(228d, sign.ActualWidth);
                Equal(Visibility.Visible, banner.Visibility); Equal(472d, banner.ActualWidth);
                var gradient = (LinearGradientBrush)account.Background;
                Equal(Color.FromRgb(33, 27, 27), gradient.GradientStops[0].Color);
                Equal(Color.FromRgb(91, 48, 48), gradient.GradientStops[1].Color);
                True(Descendants<TextBlock>(account).Any(text => text.Text == "NinonZQ"), "real account name is visible");
                True(Descendants<TextBlock>(account).Any(text => text.Text.StartsWith("SVIP · ", StringComparison.Ordinal)),
                    "real tier and expiry are visible");
                True(!Descendants<Button>(account).Any(button => Ancestor<ArtworkImage>(button) is null),
                    "no unconnected upgrade or marketing action");
                True(Descendants<TextBlock>(sign).Any(text => text.Text == "10月黑胶乐签") &&
                    Descendants<TextBlock>(sign).Any(text => text.Text == "第2天，得3成长值"),
                    "sign title and subtitle are native data");
                Equal(3, f.Module.VipPage.Sign!.Days.Count);
                Equal("https://img.invalid/sign-bg.jpg",
                    Descendants<ArtworkImage>(sign).First().Address);
                Equal("https://img.invalid/banner.jpg",
                    Descendants<ArtworkImage>(banner).Single().Address);
                Equal(1, vip.Reads);

                var signButton = Descendants<Button>(sign).Single(button =>
                    AutomationProperties.GetName(button) == "黑胶乐签打卡");
                signButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => vip.Signs == 1 && f.Module.VipPage.SignedToday &&
                    !signButton.IsEnabled && Equals(signButton.Content, "已打卡"),
                    "VIP sign did not publish its single refreshed sign branch");
                Equal(1, vip.Reads);

                banner.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => f.Module.IsCatalogPlaylist,
                    "VIP playlist banner did not navigate through the catalog owner");
                await f.Module.NavigateAsync("catalog-channel/wy/vip", default);
                await Until(() => f.Module.VipPage.HasCards && vip.Reads == 2,
                    "returning to VIP did not perform one page read");

                await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(item => item.ActionId == "feature"));
                True(!f.Module.IsVipChannel,
                    "channel chrome remains navigable after the VIP projection changes");
                await Until(() => !f.Module.VipPage.HasCards,
                    "VIP cards did not clear after leaving their route");
                window.UpdateLayout();
                Equal(Visibility.Collapsed, row.Visibility);

                await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(item => item.ActionId == "vip"));
                await Until(() => f.Module.VipPage.HasCards && vip.Reads == 3,
                    "VIP cards did not reactivate after returning to their route");
                vip.Invalidate();
                await Until(() => f.Module.VipPage.HasCards && vip.Reads == 4,
                    "VIP cards did not reload once after invalidation");
            }
            finally
            {
                window.Close();
                await Until(() => !f.Module.VipPage.HasCards,
                    "VIP cards did not clear after the page unloaded");
            }
        });
        yield return Case("VIP song banner uses the shared native song playback action", async () =>
        {
            await using var f = await Fixture.Create(vipAccount: true);
            var vip = f.Vip ?? throw new InvalidOperationException("VIP fixture");
            await f.Configure(new { total = 1 });
            vip.SetBanner("SONG", "1001");
            await f.SetupSource();
            await f.Module.NavigateAsync("catalog-channels", default);
            await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(card => card.ActionId == "vip"));
            var page = (MusicView)f.Module.View;
            var window = new Window
            {
                Content = page, Width = 1042, Height = 700, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false
            };
            try
            {
                window.Show();
                await Until(() => f.Module.VipPage.CanOpenBanner && vip.Reads == 1);
                await f.Module.OpenVipBannerAsync();
                await Until(() => f.Client.Count("music.catalog.song") == 1 &&
                    f.Playback.Opens.Count == 1, "VIP song banner did not use shared playback");
                var queue = await f.Rpc("music.queue.get", new { limit = 1 });
                Equal(1, queue.GetProperty("total").GetInt32());
                Equal("1001", queue.GetProperty("current").GetProperty("song")
                    .GetProperty("source_data").GetProperty("musicId").GetString());
            }
            finally { window.Close(); }
        });
        yield return Case("VIP song banner ignores a late song receipt after its card is invalidated", async () =>
        {
            await using var f = await Fixture.Create(vipAccount: true);
            var vip = f.Vip ?? throw new InvalidOperationException("VIP fixture");
            await f.Configure(new { total = 1 });
            vip.SetBanner("SONG", "1001");
            await f.SetupSource();
            await f.Module.NavigateAsync("catalog-channels", default);
            await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(card =>
                card.ActionId == "vip"));
            var page = (MusicView)f.Module.View;
            var window = new Window
            {
                Content = page, Width = 1042, Height = 700, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false
            };
            Held? hold = null;
            try
            {
                window.Show();
                await Until(() => f.Module.VipPage.CanOpenBanner && vip.Reads == 1);
                hold = f.Client.Hold("music.catalog.song");
                var opening = f.Module.OpenVipBannerAsync();
                await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));

                vip.Invalidate();
                await Until(() => vip.Reads == 2 && f.Module.VipPage.CanOpenBanner,
                    "VIP invalidation did not publish a replacement card");
                hold.Release();
                await opening;

                Equal(1, f.Client.Count("music.catalog.song"));
                Equal(0, f.Playback.Opens.Count);
                Equal(0, (await f.Rpc("music.queue.get", new { }))
                    .GetProperty("total").GetInt32());
            }
            finally
            {
                hold?.Release();
                window.Close();
            }
        });
        yield return Case("artist card is a 184 DIP hit target and opens one native round detail surface", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Configure(new { artistPages = 3 });
            await f.Module.NavigateAsync("catalog-channels", default);
            await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(card => card.ActionId == "artist"));
            await f.Module.CatalogTagAsync(f.Module.CatalogTags.Single(tag => tag.Group == "area" && tag.Name == "欧美"));
            await f.Module.CatalogPageAsync(1);
            Equal("第 2 页", f.Module.CatalogPageText);
            Equal(80, f.Module.CatalogSections.Single().Items.Count);
            var selectedTag = f.Module.SelectedCatalogTag?.Id ?? throw new InvalidOperationException("artist selected tag");
            var channelCalls = f.Client.Count("music.catalog.channel");
            var card = f.Module.CatalogSections.Single().Items.First();
            True(card.Kind == "artist" && card.CanOpen, "artist card must expose the native detail action");

            var page = (MusicView)f.Module.View;
            var window = new Window
            {
                Content = page, Width = 1042, Height = 700, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false
            };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                var catalog = (MusicCatalogView)page.FindName("CatalogView");
                var scroll = catalog.CatalogScroll;
                True(scroll.ScrollableHeight > 0, "artist fixture must expose vertical scroll");
                scroll.ScrollToVerticalOffset(Math.Min(240, scroll.ScrollableHeight));
                window.UpdateLayout(); await Drain();
                var savedOffset = scroll.VerticalOffset;

                var button = Descendants<Button>(catalog).Single(candidate => ReferenceEquals(candidate.Tag, card));
                Equal(184d, button.ActualWidth);
                Equal(184d, Descendants<Grid>(button).Single(grid => CardClip.GetRadius(grid) == 92).ActualWidth);
                Equal(card.Title, AutomationProperties.GetName(button));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => f.Module.IsCatalogArtist && !f.Module.Busy);

                True(f.Module.IsCatalogDetailPage && f.Module.IsContentDetailPage && f.Module.ShowsSongTable &&
                    !f.Module.IsCatalogContent, "artist detail must own the shared native song surface");
                Equal(card.Title, f.Module.Heading);
                Equal("热门歌曲", f.Module.DetailSectionTitle);
                Equal("3", f.Module.DetailCountText);
                Equal("https://img.invalid/artist-" + card.ActionId + ".jpg", f.Module.DetailArtwork);
                Equal(3, f.Module.Songs.Count);
                Equal(1, f.Client.Count("music.catalog.artist"));
                window.UpdateLayout(); await Drain();
                var artwork = (Border)page.FindName("DetailArtworkFrame");
                Equal(new CornerRadius(76), artwork.CornerRadius);
                Equal(76d, CardClip.GetRadius(artwork));
                True(artwork.Clip is RectangleGeometry { RadiusX: 76, RadiusY: 76 },
                    "artist artwork content must be physically clipped to the round frame");
                True(Descendants<TextBlock>(page).Any(text => text.IsVisible && text.Text == "热门歌曲"),
                    "artist detail must label the real hot-song section");

                await f.SetupSource();
                await f.Module.PlayContentDetailAsync();
                Equal(3, (await f.Rpc("music.queue.get", new { limit = 1 })).GetProperty("total").GetInt32());
                Equal(1, f.Client.Count("music.catalog.artist"));

                await f.Module.NavigateAsync("catalog-channel/wy/artist", default);
                window.UpdateLayout(); await Drain();
                Equal(channelCalls, f.Client.Count("music.catalog.channel"));
                Equal("第 2 页", f.Module.CatalogPageText);
                Equal(selectedTag, f.Module.SelectedCatalogTag?.Id);
                Equal(80, f.Module.CatalogSections.Single().Items.Count);
                True(Math.Abs(scroll.VerticalOffset - savedOffset) < 1,
                    $"artist channel vertical position restored ({savedOffset} -> {scroll.VerticalOffset})");
            }
            finally { window.Close(); }
        });
        yield return Case("retired artist receipt cannot replace the page opened after it", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-channels", default);
            await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(card => card.ActionId == "artist"));
            var card = f.Module.CatalogSections.Single().Items.First();
            var held = f.Client.Hold("music.catalog.artist");
            var pending = f.Module.OpenCatalogCardAsync(card);
            await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Module.NavigateAsync("catalog-home", default);
            await held.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            held.Release();
            await pending;
            await Drain();
            True(f.Module.IsCatalogHome && !f.Module.IsCatalogArtist, "late artist receipt replaced the current home");
            Equal("发现音乐", f.Module.Heading);
            Equal(1, f.Client.Count("music.catalog.artist"));
        });
        yield return Case("ranking cover sections use square artwork cards and shelf-owned navigation scope", async () =>
        {
            var view = new MusicCatalogView();
            var cards = Enumerable.Range(1, 7)
                .Select(i => new MusicCatalogCard(i.ToString(), "榜" + i, "不应显示", "", "playlist", "wy", ActionId: i.ToString()))
                .ToArray();
            var section = new MusicCatalogSection("曲风榜", cards, Type: "rankings", Layout: "ONLY_COVER");
            var shelf = (Shelf)((DataTemplate)view.FindResource("Catalog.Section.Shelf")).LoadContent();
            shelf.DataContext = section;
            view.Content = shelf;
            var window = new Window { Content = view, Width = 960, Height = 260, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                Equal(MusicCatalogSectionPresentation.RankingCovers, section.Presentation);
                True(ReferenceEquals(view.FindResource("Catalog.RankCoverCard"), shelf.CardTemplate),
                    "ranking covers own a dedicated card template");
                Equal(120d, shelf.MinimumCardWidth);
                True(shelf.CardWidth - 16 is >= 123 and <= 124,
                    "seven official square cards retain the measured 16 pixel gap at a 960 pixel content width");
                var card = Descendants<Button>(shelf).First(button => button.Tag is MusicCatalogCard);
                True(Math.Abs(card.ActualWidth - card.ActualHeight) < .5,
                    $"ranking artwork card is square ({card.ActualWidth} × {card.ActualHeight})");
                var visibleText = Descendants<TextBlock>(card).Where(text => text.IsVisible).ToArray();
                Equal(1, visibleText.Length); Equal("榜1", visibleText[0].Text);
                var next = (Button)shelf.FindName("Next");
                var hover = next.Style.Triggers.OfType<DataTrigger>().Single(trigger =>
                    trigger.Binding is System.Windows.Data.Binding binding && binding.Path.Path == "IsMouseOver");
                Equal("ShelfRoot", ((System.Windows.Data.Binding)hover.Binding).ElementName);
            }
            finally { window.Close(); }
        });
        yield return Case("shelf arrows occupy gutters page both ways and hide without overflow", async () =>
        {
            var view = new MusicCatalogView();
            var cards = Enumerable.Range(1,18).Select(i => new MusicCatalogCard(i.ToString(), "歌单"+i, "", "", "playlist", "wy")).ToArray();
            var section = new MusicCatalogSection("推荐歌单", cards);
            var shelf = new Shelf { MinimumCardWidth = 124, CardTemplate = (DataTemplate)view.FindResource("Catalog.PlaylistCard"),
                DataContext = section, Margin = new Thickness(40) };
            view.Content = shelf;
            var window = new Window { Content = view, Width = 906, Height = 320, WindowStyle = WindowStyle.None,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                var rail = shelf.ScrollHost ?? throw new InvalidOperationException("shelf scroll host");
                var previous = (Button)shelf.FindName("Previous"); var next = (Button)shelf.FindName("Next");
                True(rail.ScrollableWidth > 0 && next.IsEnabled && !previous.IsEnabled, "overflow right, left boundary disabled");
                Equal(Visibility.Visible, previous.Visibility); Equal(Visibility.Visible, next.Visibility);
                Equal(0d, next.Opacity); True(!next.IsHitTestVisible, "hidden controls do not intercept pointer");
                True(previous.TranslatePoint(new Point(previous.ActualWidth,0), rail).X <= 0, "left arrow outside card viewport");
                True(next.TranslatePoint(new Point(),rail).X >= rail.ActualWidth, "right arrow outside card viewport");
                next.Focus(); await Drain(); Equal(1d, next.Opacity);
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Drain();
                await Until(() => section.HorizontalOffset > 0 && Math.Abs(section.HorizontalOffset - rail.HorizontalOffset) < .5);
                True(previous.IsEnabled, "next moves real content");
                previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Drain();
                await Until(() => rail.HorizontalOffset < .5 && section.HorizontalOffset < .5);
                var source = PresentationSource.FromVisual(shelf) ?? throw new InvalidOperationException("shelf presentation source");
                var end = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.End)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                shelf.RaiseEvent(end);
                True(end.Handled, "End is handled by the shelf");
                await Until(() => Math.Abs(rail.HorizontalOffset - rail.ScrollableWidth) < .5);
                var home = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Home)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                shelf.RaiseEvent(home);
                True(home.Handled, "Home is handled by the shelf");
                await Until(() => rail.HorizontalOffset < .5 && section.HorizontalOffset < .5);
                shelf.DataContext = new MusicCatalogSection("推荐歌单", cards.Take(2).ToArray());
                window.UpdateLayout(); await Drain();
                await Until(() => next.Visibility == Visibility.Collapsed && previous.Visibility == Visibility.Collapsed);
            }
            finally { window.Close(); }
        });
        yield return Case("shelf virtualizes restores settled offset and never persists an in-between frame", async () =>
        {
            var view = new MusicCatalogView();
            var cards = Enumerable.Range(1, 1000)
                .Select(i => new MusicCatalogCard(i.ToString(), "歌单" + i, "", "", "playlist", "wy")).ToArray();
            var first = new MusicCatalogSection("推荐歌单", cards);
            var second = new MusicCatalogSection("其他歌单", cards.Take(40).ToArray());
            var shelf = new Shelf
            {
                MinimumCardWidth = 124,
                CardTemplate = (DataTemplate)view.FindResource("Catalog.PlaylistCard"),
                DataContext = first,
                Margin = new Thickness(40)
            };
            view.Content = shelf;
            var window = new Window
            {
                Content = view, Width = 906, Height = 320, WindowStyle = WindowStyle.None,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false
            };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                var rail = shelf.ScrollHost ?? throw new InvalidOperationException("shelf scroll host");
                var items = (ListBox)shelf.FindName("ItemsHost");
                var realized = Enumerable.Range(0, cards.Length)
                    .Count(index => items.ItemContainerGenerator.ContainerFromIndex(index) is not null);
                True(realized > 0 && realized < cards.Length / 4,
                    "realized containers remain bounded by viewport/cache rather than total items");

                var next = (Button)shelf.FindName("Next");
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => rail.HorizontalOffset > 1);
                shelf.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
                shelf.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 1, MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
                await Until(() => first.HorizontalOffset > 0 && Math.Abs(first.HorizontalOffset - rail.HorizontalOffset) < .5);
                True(first.HorizontalOffset >= shelf.CardWidth,
                    "a click without drag does not interrupt an in-flight native snap");
                var saved = first.HorizontalOffset;
                shelf.DataContext = second; window.UpdateLayout();
                await Until(() => rail.HorizontalOffset < .5);
                shelf.DataContext = first; window.UpdateLayout();
                await Until(() => Math.Abs(rail.HorizontalOffset - saved) < .5);

                rail.ScrollToHorizontalOffset(saved + shelf.CardWidth * .37); window.UpdateLayout();
                var inBetween = rail.HorizontalOffset;
                var settled = Math.Clamp(Math.Round(inBetween / shelf.CardWidth) * shelf.CardWidth, 0, rail.ScrollableWidth);
                shelf.DataContext = second; window.UpdateLayout();
                await Until(() => Math.Abs(first.HorizontalOffset - settled) < .5 && rail.HorizontalOffset < .5);
                True(Math.Abs(first.HorizontalOffset - inBetween) > 1,
                    "a context switch commits the legal snap target, not a transient physical frame");
                shelf.DataContext = first; window.UpdateLayout();
                await Until(() => Math.Abs(rail.HorizontalOffset - settled) < .5);
                saved = settled;

                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => rail.HorizontalOffset > saved + 1);
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Width = 790; window.UpdateLayout();
                await Until(() => Math.Abs(first.HorizontalOffset - rail.HorizontalOffset) < .5 &&
                    Math.Abs(rail.HorizontalOffset / Math.Max(1, shelf.CardWidth) -
                        Math.Round(rail.HorizontalOffset / Math.Max(1, shelf.CardWidth))) < .01);
                rail.ScrollToHorizontalOffset(Math.Min(rail.ScrollableWidth, rail.HorizontalOffset + 17));
                window.UpdateLayout();
                var pendingUnloadOffset = rail.HorizontalOffset;
                var pendingUnloadTarget = Math.Clamp(
                    Math.Round(pendingUnloadOffset / shelf.CardWidth) * shelf.CardWidth, 0, rail.ScrollableWidth);
                view.Content = null; await Drain();
                True(Math.Abs(first.HorizontalOffset - pendingUnloadTarget) < .5,
                    "unload commits the settled target before discarding the timer");
                True(Math.Abs(first.HorizontalOffset - pendingUnloadOffset) > 1,
                    "unload never persists an in-between animation or scrolling frame");
            }
            finally { window.Close(); }
        });
        yield return Case("entry hover variants animate independently and reset on unload", async () =>
        {
            var playlist = new MusicCatalogCard("1", "入口", "真实说明", "", "playlist", "wy", ActionId: "1",
                Presentation: MusicCatalogEntryPresentation.Preview);
            var overlay = playlist with { Kind = "similar", ActionId = "similar", Presentation = MusicCatalogEntryPresentation.Overlay };
            var fm = playlist with { Kind = "fm", ActionId = "fm", Presentation = MusicCatalogEntryPresentation.Animated };
            var staticCard = playlist with { Kind = "unsupported", ActionId = "unsupported", Available = false,
                Presentation = MusicCatalogEntryPresentation.Static };
            True(playlist.IsPreviewEntry && overlay.IsOverlayEntry && fm.IsAnimatedEntry &&
                !staticCard.IsPreviewEntry && !staticCard.IsOverlayEntry && !staticCard.IsAnimatedEntry,
                "entry semantic variants");
            True(!((playlist with { Available = false }).CanPlay), "unavailable entry cannot play");
            var view = new MusicCatalogView();
            var entry = (EntryCard)((DataTemplate)view.FindResource("Catalog.Entry")).LoadContent();
            entry.DataContext = playlist; entry.PreviewTitles = ["1 第一首", "2 第二首", "3 第三首"];
            entry.Measure(new Size(168, 300)); entry.Arrange(new Rect(0, 0, 168, 200)); entry.UpdateLayout();
            var surface = (Grid)entry.FindName("Surface");
            CardMotion.Transition(surface, true, false); await Drain();
            Equal(1d, CardMotion.GetProgress(surface)); Equal(200d, entry.ActualHeight);
            var shift = Descendants<StackPanel>(entry).Single(p => p.RenderTransform is TranslateTransform);
            Equal(86d, shift.TranslatePoint(new Point(), surface).Y);
            entry.DataContext = overlay; CardMotion.Transition(surface, true, false); await Drain();
            Equal(158d, shift.TranslatePoint(new Point(), surface).Y);
            Equal(58d, ((Button)entry.FindName("PlayButton")).Margin.Bottom);
            entry.DataContext = fm; CardMotion.Transition(surface, true, false); await Drain();
            Equal(158d, shift.TranslatePoint(new Point(), surface).Y);
            Equal(58d, ((Button)entry.FindName("PlayButton")).Margin.Bottom);
            entry.DataContext = staticCard; CardMotion.Transition(surface, true, false); await Drain();
            Equal(Visibility.Collapsed, ((Button)entry.FindName("PlayButton")).Visibility);
            CardMotion.Transition(surface, false, false);
            CardMotion.Transition(surface, true, true); await Task.Delay(60); await Drain();
            True(CardMotion.GetProgress(surface) is > 0 and < 1, "real intermediate animation, not instant switch");
            surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Equal(0d, CardMotion.GetProgress(surface));
        });
        yield return Case("entry preview reads only three songs caches within page and never changes queue", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Configure(new { detailLimit = 3 });
            await f.Module.NavigateAsync("catalog-home", default);
            var card = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.CanRequestPreview);
            JsonElement previewReply = default;
            f.Client.Transform("music.catalog.playlist", reply => { previewReply = reply; return reply; });
            var titles = await f.Module.ReadCatalogCardPreviewAsync(card, default);
            True(previewReply.ValueKind == JsonValueKind.Object && previewReply.GetProperty("ok").GetBoolean(),
                "synthetic preview response: " + previewReply);
            Equal(3, titles.Count); True(titles[0].StartsWith("1 ", StringComparison.Ordinal), "ordered real titles");
            Equal(3, f.Client.Last("music.catalog.playlist").GetProperty("limit").GetInt32());
            var calls = f.Client.Count("music.catalog.playlist");
            True(titles.SequenceEqual(await f.Module.ReadCatalogCardPreviewAsync(card, default)), "same-page preview cache");
            Equal(calls, f.Client.Count("music.catalog.playlist"));
            Equal(0, f.Client.Count("music.catalog.playlist.prepare")); Equal(0, f.Client.Count("music.queue.replace")); Equal(0, f.Playback.Opens.Count);
            await f.Module.NavigateAsync("favorites", default);
            Equal(0, (await f.Module.ReadCatalogCardPreviewAsync(card, default)).Count);
            Equal(calls, f.Client.Count("music.catalog.playlist"));
        });
        yield return Case("concurrent entry preview waiters share one request and cancel independently", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Configure(new { detailLimit = 3 });
            await f.Module.NavigateAsync("catalog-home", default);
            var card = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.CanRequestPreview);
            var hold = f.Client.Hold("music.catalog.playlist");
            using var cancellation = new CancellationTokenSource();
            var cancelled = f.Module.ReadCatalogCardPreviewAsync(card, cancellation.Token);
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var survivor = f.Module.ReadCatalogCardPreviewAsync(card, default);
            cancellation.Cancel();
            Equal(0, (await cancelled).Count);
            True(!survivor.IsCompleted, "one cancelled preview waiter cancelled shared physical work");
            True(!hold.Cancelled.Task.IsCompleted, "one cancelled preview waiter signalled shared cancellation");
            Equal(1, f.Client.Count("music.catalog.playlist"));
            hold.Release();
            Equal(3, (await survivor).Count);
            Equal(1, f.Client.Count("music.catalog.playlist"));
        });
        yield return Case("last entry preview waiter cancellation retires publication and shutdown drains physical request", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Configure(new { detailLimit = 3 });
            await f.Module.NavigateAsync("catalog-home", default);
            var card = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.CanRequestPreview);
            var hold = f.Client.Hold("music.catalog.playlist");
            using var cancellation = new CancellationTokenSource();
            var preview = f.Module.ReadCatalogCardPreviewAsync(card, cancellation.Token);
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Equal(0, (await preview).Count);
            await hold.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var shutdown = f.Module.PrepareShutdownAsync();
            await Task.Delay(35);
            True(!shutdown.IsCompleted, "shutdown returned before cancelled preview request physically settled");
            hold.Release();
            await shutdown;
        });
        yield return Case("page generation cancels physical preview and blocks late publication", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Configure(new { detailLimit = 3 });
            await f.Module.NavigateAsync("catalog-home", default);
            var card = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.CanRequestPreview);
            var hold = f.Client.Hold("music.catalog.playlist");
            var pending = f.Module.ReadCatalogCardPreviewAsync(card, default);
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Module.NavigateAsync("favorites", default);
            await hold.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(35);
            True(!pending.IsCompleted, "page generation detached a non-cooperative preview request");
            hold.Release(); Equal(0, (await pending).Count);
            Equal(0, f.Playback.Opens.Count);
            await f.Module.NavigateAsync("catalog-home", default);
            var current = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.CanRequestPreview);
            Equal(3, (await f.Module.ReadCatalogCardPreviewAsync(current, default)).Count);
            Equal(2, f.Client.Count("music.catalog.playlist"));
        });
        yield return Case("cached catalog page switch advances generation and cancels preview", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Configure(new { detailLimit = 3 });
            await f.Module.NavigateAsync("catalog-home", default);
            await f.Module.CatalogPageAsync(1);
            var card = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.CanRequestPreview);
            var hold = f.Client.Hold("music.catalog.playlist");
            var pending = f.Module.ReadCatalogCardPreviewAsync(card, default);
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Module.CatalogPageAsync(-1);
            await hold.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(35);
            True(!pending.IsCompleted, "cached page switch detached physical preview work");
            hold.Release(); Equal(0, (await pending).Count);
            var current = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.CanRequestPreview);
            Equal(3, (await f.Module.ReadCatalogCardPreviewAsync(current, default)).Count);
            Equal(2, f.Client.Count("music.catalog.playlist"));
        });
        yield return Case("aggregate playlist search cards stay static and never request entry previews", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Configure(new { searchPlaylistTotal = 61 });
            await f.Module.NavigateAsync("catalog-home", default);
            await f.SearchPolicy("wy", playlists: true);
            f.Module.CatalogSearchKind = "歌单";
            await f.Module.SearchAsync("云歌单", default);
            await WaitForSearch(f.Module);
            True(f.Module.CanAggregateMore, "fixture search must expose another page");
            var card = f.Module.CatalogSections.Single().Items.First();
            Equal(MusicCatalogEntryPresentation.Static, card.Presentation);
            True(!card.CanRequestPreview, "aggregate search is not an entry preview surface");
            var calls = f.Client.Count("music.catalog.playlist");
            Equal(0, (await f.Module.ReadCatalogCardPreviewAsync(card, default)).Count);
            Equal(calls, f.Client.Count("music.catalog.playlist"));
            await f.Module.MoreAggregateSearchAsync();
            await WaitForSearch(f.Module);
            var current = f.Module.CatalogSections.Single().Items.First();
            True(!ReferenceEquals(card, current), "aggregate snapshot did not replace visible card identity");
            Equal(MusicCatalogEntryPresentation.Static, current.Presentation);
            Equal(0, (await f.Module.ReadCatalogCardPreviewAsync(current, default)).Count);
            Equal(calls, f.Client.Count("music.catalog.playlist"));
        });
        yield return Case("loaded entry hover publishes real native preview titles into WPF", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Configure(new { detailLimit = 3 });
            await f.Module.NavigateAsync("catalog-home", default);
            var card = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.CanRequestPreview);
            var view = new MusicCatalogView { DataContext = f.Module };
            var entry = (EntryCard)((DataTemplate)view.FindResource("Catalog.Entry")).LoadContent();
            entry.DataContext = card; view.Content = entry;
            var window = new Window { Content = view, Width = 168, Height = 230, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                entry.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
                for (var i = 0; i < 100 && entry.PreviewTitles.Count == 0; i++) { await Task.Delay(10); await Drain(); }
                Equal(1, f.Client.Count("music.catalog.playlist"));
                Equal(3, entry.PreviewTitles.Count);
                True(Descendants<TextBlock>(entry).Any(t => t.Text.StartsWith("1 合成曲", StringComparison.Ordinal)), "real preview text rendered");
                var caption = Descendants<StackPanel>(entry).Single(p => p.RenderTransform is TranslateTransform);
                var clip = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(caption);
                True(clip is null || clip.Bounds.Height >= caption.DesiredSize.Height, "expanded preview is not clipped to the collapsed caption's 48px layout slot");
            }
            finally { window.Close(); }
        });
        yield return Case("unavailable hover preview does not retry on each entry or request FM", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Configure(new { detailLimit = 3 });
            await f.Module.NavigateAsync("catalog-home", default);
            var card = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.CanRequestPreview);
            f.Client.Transform("music.catalog.playlist", _ => JsonSerializer.SerializeToElement(new { ok = false, error = new { code = "UNAVAILABLE" } }));
            Equal(0, (await f.Module.ReadCatalogCardPreviewAsync(card, default)).Count);
            var calls = f.Client.Count("music.catalog.playlist");
            Equal(0, (await f.Module.ReadCatalogCardPreviewAsync(card, default)).Count);
            Equal(calls, f.Client.Count("music.catalog.playlist"));
            Equal(0, (await f.Module.ReadCatalogCardPreviewAsync(card with
                { Kind = "fm", Presentation = MusicCatalogEntryPresentation.Animated }, default)).Count);
            Equal(0, f.Client.Count("music.catalog.fm")); Equal(0, f.Playback.Opens.Count);
        });
        yield return Case("home card play uses full native playlist once without opening its page", async () =>
        {
            await using var f = await Fixture.Create(); await f.SetupSource();
            await f.Module.NavigateAsync("catalog-home", default);
            var card = f.Module.CatalogSections.SelectMany(s => s.Items).First(c => c.Kind == "playlist");
            var navigations = 0; f.Module.NavigationRequested += (_, _) => navigations++;
            var view = new MusicCatalogView { DataContext = f.Module };
            var tile = (FrameworkElement)((DataTemplate)view.FindResource("Catalog.PlaylistCard")).LoadContent();
            tile.DataContext = card; view.Content = tile;
            ((Button)tile).Background = Brushes.Magenta;
            tile.Measure(new Size(140, 200)); tile.Arrange(new Rect(0, 0, 140, 172)); tile.UpdateLayout();
            True(Descendants<Border>(tile).First().Background is SolidColorBrush { Color.A: 0 },
                "generic button hover brush must not tint the playlist caption");
            var play = Descendants<Button>(tile).Single(b => ReferenceEquals(b.Style, view.FindResource("Catalog.CoverPlay")));
            play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 500 && f.Playback.Opens.Count == 0; i++) { await Task.Delay(10); await Drain(); }
            Equal(1, f.Client.Count("music.catalog.playlist.prepare")); Equal(1, f.Client.Count("music.catalog.playlist.commit"));
            Equal(1, f.Playback.Opens.Count); Equal(0, navigations);
            True(f.Module.IsCatalogHome, "hover play does not navigate to a collection");
            await f.Module.PlayCatalogCardAsync(card with { Available = false });
            Equal(1, f.Client.Count("music.catalog.playlist.commit"));
        });
        yield return Case("home loads pages only from current owned scroll gestures", async () =>
        {
            await using var f = await Fixture.Create();
            var window = new Window { Content = f.Module.View, Width = 906, Height = 220,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            try
            {
            await f.Configure(new { homePages = 3 });
            await f.Module.NavigateAsync("catalog-home", default);
            var view = f.Module.View;
            view.UpdateLayout();
            await Drain();
            var catalog = (MusicCatalogView)view.FindName("CatalogView");
            var scroll = (ScrollViewer)catalog.FindName("CatalogScroll");
            True(catalog.IsVisible, "catalog is visible for gesture admission");
            True(scroll.ScrollableHeight > 280, "fixture has a non-near-end scroll range");
            True(f.Module.CanCatalogNext, "public home has a valid next cursor");
            Equal(1, f.Client.Count("music.catalog.home"));
            True(!Descendants<Button>(catalog).Any(b => Equals(b.Content, "更多")), "no bottom global more button");
            var tracked = f.Module.CatalogSections.First(section => section.Items.Count > 0);
            tracked.HorizontalOffset = 137;
            scroll.ScrollToTop(); await Drain();
            scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120)
                { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            await Drain();
            Equal(1, f.Client.Count("music.catalog.home"));
            scroll.ScrollToEnd(); await Drain(); scroll.UpdateLayout();
            Equal(1, f.Client.Count("music.catalog.home")); // Expired intent cannot admit later layout/restore scrolling.
            scroll.ScrollToTop(); await Drain();
            var bar = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
            var scrollThumb = Descendants<Thumb>(bar).Single();
            True(Mouse.Capture(scrollThumb), "catalog scrollbar thumb captures the pointer");
            try
            {
                scroll.ScrollToEnd();
                await Until(() => f.Client.Count("music.catalog.home") == 2 && !f.Module.Busy);
            }
            finally { Mouse.Capture(null); }
            scroll.UpdateLayout();
            Equal(2, f.Client.Count("music.catalog.home"));
            scroll.ScrollToEnd(); await Drain();
            double before = scroll.VerticalOffset;
            scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120)
                { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            for (int i = 0; i < 200 && (f.Client.Count("music.catalog.home") < 3 || f.Module.Busy); i++)
            { await Task.Delay(10); await Drain(); }
            await Drain(); scroll.UpdateLayout();
            Equal(3, f.Client.Count("music.catalog.home"));
            True(scroll.VerticalOffset >= before - 1, "append does not reset to top");
            Equal(137d, f.Module.CatalogSections.Single(section => section.Id == tracked.Id).HorizontalOffset);
            await Drain();
            Equal(3, f.Client.Count("music.catalog.home")); // No self-triggering request loop.
            }
            finally { Mouse.Capture(null); window.Content = null; window.Close(); }
        });
        yield return Case("wheel over a horizontal card shelf scrolls the outer page", async () =>
        {
            var shelf = new Shelf { Height = 200 };
            var content = new StackPanel();
            content.Children.Add(shelf); content.Children.Add(new Border { Height = 1600 });
            var outer = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var window = new Window
            {
                Content = outer, Width = 826, Height = 400, WindowStyle = WindowStyle.None,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false
            };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                shelf.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, -120)
                    { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent });
                await Drain(); outer.UpdateLayout();
                True(outer.VerticalOffset > 0, "outer page moves without pointing at whitespace");
                Equal(0d, (shelf.ScrollHost ?? throw new InvalidOperationException("shelf scroll host")).HorizontalOffset);
            }
            finally { window.Close(); }
        });
        yield return Case("library sync cannot cancel the first pending recommendation navigation", async () =>
        {
            await using var f = await Fixture.Create();
            var hold = f.Client.Hold("music.catalog.home");
            var pending = f.Module.NavigateAsync("catalog-home", default);
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(object? sender, PropertyChangedEventArgs e)
            { if (e.PropertyName == nameof(MusicCapability.CountText)) published.TrySetResult(); }
            f.Module.PropertyChanged += Changed;
            try
            {
                f.Client.NotifyLibrarySync();
                await published.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Drain();
                True(!pending.IsCompleted, "background library refresh must not retire the in-flight catalog route");
                hold.Release(); await pending;
                True(f.Module.IsCatalogHome, "first recommendation owns the visible page");
                True(f.Module.CatalogSections.Count > 0, "recommendation content is published");
            }
            finally { f.Module.PropertyChanged -= Changed; hold.Release(); await pending; }
        });
        yield return Case("desktop catalog preserves real ranking previews and responsive section types", () =>
        {
            var reply = JsonSerializer.SerializeToElement(new { platform = "wy", sections = new[] {
                new { id = "rank", title = "榜单", type = "rankings", layout = "ranked-songs", items = new[] {
                    new { id = "123", name = "测试榜", kind = "ranking", cover = "", updateFrequency = "每日更新",
                        action = new { type = "playlist", id = "123" },
                        preview = new[] { new { title = "测试歌曲", artist = "测试歌手", cover = "", rank = 1, lastRank = 3 } } } } } } });
            var section = MusicCatalogDto.Read(reply, "wy", "home", "推荐").Single();
            True(section.IsRankingPreviewSection && !section.IsRankingCoverSection && !section.IsSongSection,
                "ranked-songs layout owns the preview presentation");
            Equal("ranked-songs", section.Layout); Equal("每日更新", section.Items[0].UpdateFrequency);
            Equal("↑", section.Items[0].PreviewSongs.Single().Movement);
            True(section.Items[0].CanOpen, "ranking opens its actual playlist action");
            var columns = new ColumnsConverter();
            Equal(2, columns.Convert(1000d, typeof(int), null!, System.Globalization.CultureInfo.InvariantCulture));
            Equal(2, columns.Convert(1490d, typeof(int), null!, System.Globalization.CultureInfo.InvariantCulture));
            var shelf = new Shelf { MinimumCardWidth = 124, DataContext = section with { Type = "playlists" } };
            shelf.Measure(new Size(1000, 300)); shelf.Arrange(new Rect(0, 0, 1000, 300)); shelf.UpdateLayout();
            True(shelf.CardWidth is >= 140 and <= 146, "ordinary covers use seven columns at 1000 pixels");
            var view = new MusicCatalogView();
            var card = (FrameworkElement)((DataTemplate)view.FindResource("Catalog.CoverCard")).LoadContent();
            card.DataContext = section.Items[0];
            card.Measure(new Size(168, 300)); card.Arrange(new Rect(0, 0, 168, 300)); card.UpdateLayout();
            True(!Descendants<TextBlock>(card).Any(t =>
                t.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == "ErrorText"),
                "business errors must not be inserted in card layout");
            Equal("ErrorText", card.GetBindingExpression(FrameworkElement.ToolTipProperty)?.ParentBinding.Path?.Path);
            return Task.CompletedTask;
        });
        yield return Case("entry presentation DTO is strict while ordinary cards remain static", async () =>
        {
            var ordinaryReply = JsonSerializer.SerializeToElement(new { platform = "wy", sections = new[] {
                new { id = "section", title = "普通栏目", type = "playlists", items = new[] {
                    new { id = "1", name = "普通歌单", kind = "playlist", presentation = "preview",
                        action = new { type = "playlist", id = "1" } } } } } });
            Equal(MusicCatalogEntryPresentation.Static,
                MusicCatalogDto.Read(ordinaryReply, "wy", "home", "").Single().Items.Single().Presentation);

            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default);
            var nested = f.Module.CatalogSections.Single(section => section.IsEntrySection).Items.Single(card => card.Song is not null);
            Equal(MusicCatalogEntryPresentation.Static, nested.Presentation);
            var song = nested.Song!.Raw;

            void Rejected(JsonElement reply)
            {
                try { _ = MusicCatalogDto.Read(reply, "wy", "home", ""); }
                catch (MusicOperationException error) when (error.Code == "INVALID_RESPONSE") { return; }
                throw new InvalidOperationException("entry presentation contract accepted an invalid DTO");
            }
            Rejected(JsonSerializer.SerializeToElement(new { platform = "wy", entries = new[] {
                new { id = "1", name = "缺失展示类型", action = new { type = "playlist", id = "1" } } } }));
            Rejected(JsonSerializer.SerializeToElement(new { platform = "wy", entries = new[] {
                new { song, presentation = "Preview" } } }));
            var valid = JsonSerializer.SerializeToElement(new { platform = "wy", entries = new[] {
                new { song, presentation = "static" } } });
            Equal(MusicCatalogEntryPresentation.Static,
                MusicCatalogDto.Read(valid, "wy", "home", "").Single().Items.Single().Presentation);
        });
        yield return Case("home nested songs and unavailable actions use actual provider DTO", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default);
            var cards = f.Module.CatalogSections.SelectMany(s => s.Items).ToArray();
            var song = cards.Single(c => c.Song is not null);
            Equal("1001", song.Song!.Raw.GetProperty("source_data").GetProperty("musicId").GetString());
            True(song.Id != song.ActionId && song.ActionId == "1001", "domain identity not upstream locator");
            var daily = cards.Single(c => c.Kind == "daily");
            True(!daily.CanOpen && daily.ErrorText.Length > 0, "unavailable business error, not fake clickable");
            var count = f.Client.Log.Count;
            await f.Module.OpenCatalogCardAsync(daily);
            Equal(count, f.Client.Log.Count);
            Equal("playlist", cards.Single(c => c.Kind == "playlist").Kind);
        });
        yield return Case("channel descriptors expose implemented featured cube artists and VIP pages", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-channels", default);
            var cards = f.Module.CatalogSections.Single().Items;
            True(cards.Single(c => c.Id == "feature").CanOpen, "featured is channel descriptor kind");
            True(cards.Single(c => c.Id == "rock").CanOpen, "cube is channel descriptor kind");
            True(cards.Single(c => c.Id == "artist").CanOpen, "artist directory has a native owner");
            True(cards.Single(c => c.Id == "vip").CanOpen, "VIP page has a native owner");
            Equal("feature,playlist,chart,artist,vip,euro,djR,huayu,qingge,acg",
                string.Join(",", f.Module.VisibleChannelTabs.Select(card => card.ActionId)));
            Equal("china,yingshi,rock,music_top_tab_jpop,paobu,music_top_tab_game,yueyu,music_top_tab_sunoai,old_hat,music_top_tab_dance,hFlow,music_top_tab_world,music_top_tab_dj,rnb,chuxing,music_tab_drive,music_top_tab_tipsy,rap,xiangcun,music_top_tab_french,sleep,music_top_tab_light_music,classic,music_top_tab_children,HiFi,sir,ballad,music_top_tab_latin,music_tab_pioneer,funk,landiao,yueqi,study,yinyueju,music_top_tab_sport",
                string.Join(",", f.Module.MoreChannelTabs.Select(card => card.ActionId)));
            await f.Module.OpenCatalogCardAsync(cards.Single(c => c.Id == "rock"));
            Equal("MUSIC_ROCK", f.Client.Last("music.catalog.channel").GetProperty("pageCode").GetString());
            Equal("摇滚", f.Module.Heading); // provider publishes descriptor title, not protocol title
            Equal(2, f.Module.CatalogTags.Count);
            True(f.Module.CatalogSections.SelectMany(s => s.Items).Count(c => c.Song is not null) == 2, "carousel nested song + feed song");
        });
        yield return Case("artist channel preserves three independent filters and combined selection token", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-channels", default);
            await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(card => card.ActionId == "artist"));
            Equal("歌手", f.Module.Heading);
            Equal("area,type,initial", string.Join(",", f.Module.CatalogTagGroups.Select(group => group.Id)));
            Equal("6,4,28", string.Join(",", f.Module.CatalogTagGroups.Select(group => group.Items.Count)));
            True(f.Module.CatalogTagGroups.All(group => group.Items.Count(tag => tag.Selected) == 1),
                "every artist filter group has one selected option");
            Equal("-1:-1:-1,-1:-1:-1,-1:-1:-1", string.Join(",",
                f.Module.CatalogTagGroups.Select(group => group.Items.Single(tag => tag.Selected).Id)));
            var section = f.Module.CatalogSections.Single();
            True(section.IsArtistSection && section.Items.Count == 40, "artist response owns the artist grid");
            True(section.Items.All(card => card.Kind == "artist" && card.Title.Length > 0),
                "artist cards preserve their native shape");

            await f.Module.CatalogTagAsync(f.Module.CatalogTags.Single(tag =>
                tag.Group == "area" && tag.Id == "96:-1:-1"));
            Equal("96:-1:-1", f.Client.Last("music.catalog.channel").GetProperty("tag").GetString());
            await f.Module.CatalogTagAsync(f.Module.CatalogTags.Single(tag =>
                tag.Group == "type" && tag.Id == "96:2:-1"));
            Equal("96:2:-1", f.Client.Last("music.catalog.channel").GetProperty("tag").GetString());
            await f.Module.CatalogTagAsync(f.Module.CatalogTags.Single(tag =>
                tag.Group == "initial" && tag.Id == "96:2:65"));
            Equal("96:2:65", f.Client.Last("music.catalog.channel").GetProperty("tag").GetString());
            True(f.Module.CatalogTagGroups.All(group =>
                group.Items.Single(tag => tag.Selected).Id == "96:2:65"),
                "all three selected options retain the current combined token");
        });
        yield return Case("artist channel continuation appends one bounded native page", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Configure(new { artistPages = 2 });
            await f.Module.NavigateAsync("catalog-channels", default);
            await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(card => card.ActionId == "artist"));
            Equal(40, f.Module.CatalogSections.Single().Items.Count);
            True(f.Module.CanCatalogNext, "first artist page exposes its continuation");
            await f.Module.CatalogPageAsync(1);
            Equal(80, f.Module.CatalogSections.Single().Items.Count);
            True(!f.Module.CanCatalogNext, "second artist page terminates pagination");
            var request = f.Client.Last("music.catalog.channel");
            Equal("-1:-1:-1", request.GetProperty("tag").GetString());
            Equal(40, request.GetProperty("continuation").GetProperty("offset").GetInt32());
            Equal("第 2 页", f.Module.CatalogPageText);
        });
        yield return Case("VIP channel publishes native recommendations and only explicit VIP rankings", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-channels", default);
            await f.Module.OpenCatalogCardAsync(f.Module.ChannelTabs.Single(card => card.ActionId == "vip"));
            Equal("VIP", f.Module.Heading);
            Equal(2, f.Module.CatalogSections.Count);
            var recommendations = f.Module.CatalogSections[0];
            True(recommendations.IsSongSection, "VIP recommendations use the song presenter");
            Equal("黑胶VIP 每日推荐", recommendations.Title);
            Equal(2, recommendations.Items.Count);
            var rankings = f.Module.CatalogSections[1];
            True(rankings.IsRankingPreviewSection, "VIP charts use the ranking presenter");
            Equal("黑胶VIP 排行榜", rankings.Title);
            Equal("黑胶VIP 热歌榜,黑胶SVIP 飙升榜", string.Join(",", rankings.Items.Select(card => card.Title)));
            True(rankings.Items.All(card => card.PreviewSongs.Count == 1),
                "VIP ranking cards keep their native previews");
        });
        yield return Case("cube tag switch resets cursor and preserves whole feed continuation", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-channel/wy/rock", default);
            await f.Module.CatalogTagAsync(f.Module.CatalogTags.Single(t => t.Id == "30"));
            var first = f.Client.Last("music.catalog.channel");
            True(!first.TryGetProperty("continuation", out _), "tag switches from first page");
            await f.Module.CatalogPageAsync(1);
            var p = f.Client.Last("music.catalog.channel");
            Equal("30", p.GetProperty("tag").GetString());
            var cursor = p.GetProperty("continuation");
            foreach (var key in new[] { "offset", "ruleId", "pointIds", "algTagIds", "tag", "scope", "target" })
                True(cursor.TryGetProperty(key, out _), "whole cursor retains " + key);
            Equal(3, f.Module.CatalogSections.SelectMany(s => s.Items).Count(c => c.Song is not null));
            True(!f.Module.CanCatalogNext, "feed final page");
        });
        yield return Case("failed tag request keeps prior successful tags selection and content", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-channel/wy/rock", default);
            await f.Module.CatalogTagAsync(f.Module.CatalogTags.Single(t => t.Id == "30"));
            var before = f.Module.CatalogSections.ToArray();
            await f.Configure(new { tagFailure = true });
            await f.Module.CatalogTagAsync(f.Module.CatalogTags.Single(t => t.Id == "31"));
            Equal("30", f.Module.SelectedCatalogTag?.Id);
            True(before.SequenceEqual(f.Module.CatalogSections), "failed filter retains data");
            Equal("平台服务暂不可用", f.Module.Status);
        });
        yield return Case("refresh failure keeps home and sends refresh true first page", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default);
            await f.Module.CatalogPageAsync(1);
            var before = f.Module.CatalogSections.ToArray();
            await f.Configure(new { homeFailure = 503 });
            await f.Module.RefreshAsync(); // exercise owner's public refresh dispatch
            var p = f.Client.Last("music.catalog.home");
            True(p.GetProperty("refresh").GetBoolean(), "refresh propagated");
            True(!p.TryGetProperty("continuation", out _), "refresh resets cursor");
            True(before.SequenceEqual(f.Module.CatalogSections), "failure retains previous successful merged page");
            Equal("第 2 页", f.Module.CatalogPageText);
        });
        yield return Case("playlist next page appends with snapshot and bounded page size then refresh resets", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            Equal(100, f.Module.Songs.Count);
            await f.Module.CatalogPageAsync(1);
            var p = f.Client.Last("music.catalog.playlist");
            Equal(2, p.GetProperty("page").GetInt32()); Equal(100, p.GetProperty("limit").GetInt32());
            Equal(64, p.GetProperty("snapshot").GetString()!.Length);
            Equal(200, f.Module.Songs.Count);
            Equal("1001", f.Module.Songs[0].Raw.GetProperty("source_data").GetProperty("musicId").GetString());
            Equal("1101", f.Module.Songs[100].Raw.GetProperty("source_data").GetProperty("musicId").GetString());
            var calls = f.Client.Count("music.catalog.playlist");
            await f.Module.NavigateAsync("catalog-home", default);
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            Equal(calls, f.Client.Count("music.catalog.playlist"));
            Equal(200, f.Module.Songs.Count);
            Equal("第 2 页", f.Module.CatalogPageText);
            True(!f.Module.CanCatalogPrevious, "the accumulated song surface does not expose a page-back control");
            await f.Module.LoadMoreCatalogPlaylistAsync();
            Equal(205, f.Module.Songs.Count);
            True(!f.Module.CanLoadMoreCatalogPlaylist, "the final page terminates scroll pagination");
            calls = f.Client.Count("music.catalog.playlist");
            await f.Module.LoadMoreCatalogPlaylistAsync();
            Equal(calls, f.Client.Count("music.catalog.playlist"));
            await f.Configure(new { nextDetail = 0 });
            await f.Module.RefreshCatalogAsync();
            p = f.Client.Last("music.catalog.playlist");
            Equal(1, p.GetProperty("page").GetInt32()); True(p.GetProperty("refresh").GetBoolean(), "playlist refresh");
            True(!p.TryGetProperty("snapshot", out _), "fresh first page has no old snapshot");
        });
        yield return Case("snapshot changed response cannot replace current native page", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            var first = f.Module.Songs[0];
            f.Client.Transform("music.catalog.playlist", value => Edit(value, node => node["snapshot"] = new string('b', 64)));
            await f.Module.CatalogPageAsync(1);
            True(ReferenceEquals(first, f.Module.Songs[0]), "snapshot changed keeps old data");
            Equal("第 1 页", f.Module.CatalogPageText); Equal("内容已变更，请刷新", f.Module.Status);
        });
        yield return Case("rapid tag A B A suppresses old B receipt and restores A selection", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-channel/wy/rock", default);
            await f.Module.CatalogTagAsync(f.Module.CatalogTags.Single(t => t.Id == "30"));
            var hold = f.Client.Hold("music.catalog.channel");
            var old = f.Module.CatalogTagAsync(f.Module.CatalogTags.Single(t => t.Id == "31"));
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Module.CatalogTagAsync(f.Module.CatalogTags.Single(t => t.Id == "30"));
            hold.Release(); await old;
            Equal("30", f.Module.SelectedCatalogTag?.Id);
            Equal("30", f.Client.Last("music.catalog.channel").GetProperty("tag").GetString());
        });
        yield return Case("repeated cursor is rejected even if opaque fields differ", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default);
            var before = f.Module.CatalogSections.ToArray();
            f.Client.Transform("music.catalog.home", value => Edit(value, node =>
            {
                node["more"] = true;
                node["continuation"] = JsonSerializer.SerializeToNode(new { cursor = "1", scope = "different", target = "wy:home" });
            }));
            await f.Module.CatalogPageAsync(1);
            True(before.SequenceEqual(f.Module.CatalogSections), "stalled page cannot replace old content");
            Equal("分页未前进", f.Module.Status);
        });
        yield return Case("page 100 with more is rejected; page 101 never sent", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Configure(new { homePages = 101 });
            await f.Module.NavigateAsync("catalog-home", default);
            for (int i = 0; i < 99; i++) await f.Module.CatalogPageAsync(1);
            Equal("第 99 页", f.Module.CatalogPageText);
            Equal("内容超过数量限制", f.Module.Status);
            Equal(100, f.Client.Count("music.catalog.home"));
        });
        yield return Case("cancelled and retired responses cannot republish private display or cache", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default);
            var hold = f.Client.Hold("music.catalog.home");
            var pending = f.Module.RefreshCatalogAsync(); await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Module.RetireCatalogSession();
            await pending; hold.Release(); await Drain();
            Equal(0, f.Module.CatalogSections.Count); Equal(0, f.Module.Songs.Count);
            var reads = f.Client.Count("music.catalog.home");
            await f.Module.NavigateAsync("catalog-home", default);
            Equal(reads + 1, f.Client.Count("music.catalog.home"));
        });
        yield return Case("late A after B cannot replace current route", async () =>
        {
            await using var f = await Fixture.Create();
            var hold = f.Client.Hold("music.catalog.home");
            var pending = f.Module.NavigateAsync("catalog-home", default); await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Module.NavigateAsync("catalog-channels", default);
            hold.Release(); await pending;
            Equal("音乐频道", f.Module.Heading); Equal(45, f.Module.ChannelTabs.Count);
        });
        yield return Case("auth failure clears private display while transient failure does not", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default);
            await f.Configure(new { homeFailure = 401 });
            await f.Module.RefreshCatalogAsync();
            Equal(0, f.Module.CatalogSections.Count); Equal(0, f.Module.Songs.Count);
            Equal("当前内容需要平台登录", f.Module.Status);
        });
        yield return Case("search playlist DTO without kind is actionable and search uses page <=100", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default);
            await f.SearchPolicy("wy", playlists: true);
            f.Module.CatalogSearchKind = "歌单";
            await f.Module.SearchAsync("云歌单", default);
            await WaitForSearch(f.Module);
            var card = f.Module.CatalogSections.Single().Items.Single();
            True(card.CanOpen && card.Kind == "playlist" && card.ActionId == "500", "search playlist DTO is not an unknown resource");
            Equal("playlists", f.Client.Last("music.search.start").GetProperty("kind").GetString());
            True(f.Module.SearchProgress.All(p => p.Page is >= 0 and <= 100), "bounded aggregate provider page");
        });
        yield return Case("platform playlist uses the one playlist detail surface", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            True(f.Module.IsContentDetailPage && f.Module.ShowsSongTable && !f.Module.IsCatalogContent,
                "platform playlist leaves the generic catalog surface");
            Equal("完整云歌单", f.Module.Heading);
            Equal("205", f.Module.DetailCountText);
            Equal("https://img.invalid/playlist.jpg", f.Module.DetailArtwork);
            True(f.Module.CanLoadMoreCatalogPlaylist, "playlist continues from the song surface");
            var page = (MusicView)f.Module.View;
            var window = new Window { Content = page, Width = 900, Height = 700, WindowStyle = WindowStyle.None,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                var grid = (DataGrid)page.FindName("SongsGrid");
                Equal(Visibility.Visible, grid.Visibility);
                Equal(Visibility.Collapsed, ((MusicCatalogView)page.FindName("CatalogView")).Visibility);
                True(((MeowFilm.Desktop.IDesktopHeaderSource)page).Header.Slot is null,
                    "playlist detail does not publish the home-only create control");
                var visibleLabels = Descendants<Button>(page).Where(button => button.IsVisible)
                    .Select(button => AutomationProperties.GetName(button) is { Length: > 0 } name
                        ? name : button.Content?.ToString() ?? "").ToArray();
                True(visibleLabels.Any(label => label.Contains("播放全部", StringComparison.Ordinal)), "playlist primary action");
                True(!visibleLabels.Any(label => label is "上一页" or "下一页"), "playlist has no page-switch toolbar");
                True(!Descendants<TextBlock>(page).Any(text => text.IsVisible &&
                    (text.Text == "评论" || text.Text == "收藏者")),
                    "only the song section is present");
                var bar = Descendants<ScrollBar>(grid).Single(candidate =>
                    candidate.Orientation == Orientation.Vertical && candidate.IsVisible &&
                    candidate.Maximum > candidate.Minimum);
                var scroll = Ancestor<ScrollViewer>(bar) ??
                    throw new InvalidOperationException("playlist scrollbar has no owning scroll viewer");
                scroll.ScrollToEnd(); await Drain(); scroll.UpdateLayout();
                Equal(1, f.Client.Count("music.catalog.playlist"));
                grid.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                for (var i = 0; i < 200 && (f.Client.Count("music.catalog.playlist") < 2 || f.Module.Busy); i++)
                { await Task.Delay(10); await Drain(); }
                Equal(2, f.Client.Count("music.catalog.playlist"));
                Equal(200, f.Module.Songs.Count);
                await Drain();
                Equal(2, f.Client.Count("music.catalog.playlist"));
                window.UpdateLayout(); scroll.ScrollToTop(); await Drain();
                var resizeThumb = Descendants<Thumb>(grid)
                    .FirstOrDefault(candidate => candidate.IsVisible && candidate.ActualWidth > 0 &&
                        candidate.ActualHeight > 0 && Ancestor<ScrollBar>(candidate) is null);
                True(resizeThumb is not null && Mouse.Capture(resizeThumb),
                    "playlist column resize thumb captures the pointer");
                try
                {
                    scroll.ScrollToEnd();
                    await Drain();
                }
                finally { Mouse.Capture(null); }
                Equal(2, f.Client.Count("music.catalog.playlist"));
                scroll.ScrollToTop(); await Drain();
                var scrollThumb = Descendants<Thumb>(bar).Single();
                var scrollTrack = Ancestor<Track>(scrollThumb) ??
                    throw new InvalidOperationException("playlist scrollbar thumb has no track");
                scrollTrack.IsEnabled = true; // The offscreen host cannot synthesize the real mouse-over state.
                True(Mouse.Capture(scrollThumb), "playlist scrollbar thumb captures the pointer");
                try
                {
                    scroll.ScrollToEnd();
                    await Until(() => f.Client.Count("music.catalog.playlist") == 3 &&
                        f.Module.Songs.Count == 205 && !f.Module.Busy);
                }
                finally
                {
                    Mouse.Capture(null);
                    scrollTrack.ClearValue(UIElement.IsEnabledProperty);
                }
                Equal(3, f.Client.Count("music.catalog.playlist"));
                Equal(205, f.Module.Songs.Count);
                True(!f.Module.CanLoadMoreCatalogPlaylist, "final five songs terminate inline pagination");
                await Drain();
                Equal(3, f.Client.Count("music.catalog.playlist"));
            }
            finally { Mouse.Capture(null); window.Close(); }
        });
        foreach (var platform in new[] { "tx", "kg" })
        {
            yield return Case("actual " + platform + " provider song DTO and full cloud save", async () =>
            {
                await using var f = await Fixture.Create();
                await f.Module.NavigateAsync("catalog-home", default);
                await f.SearchPolicy(platform, playlists: false);
                f.Module.SelectedCatalogPlatform = f.Module.CatalogPlatforms.Single(p => p.Key == platform);
                await f.Module.SearchAsync("歌曲", default);
                await WaitForSearch(f.Module);
                var card = f.Module.CatalogSections.Single().Items.Single();
                True(card.CanOpen && card.Song is not null, "real provider normalized song is actionable");
                Equal(platform, card.Song!.Raw.GetProperty("source_data").GetProperty("source").GetString());
                var id = platform == "kg" ? "global-test" : "500";
                await f.Module.NavigateAsync("catalog-playlist/" + platform + "/" + id, default);
                await f.Module.SaveCatalogPlaylistAsync();
                Equal(205, f.Module.Playlists.Single(p => p.Kind == "playlist").Count);
            });
        }
        yield return Case("back restores successful merged view without new requests", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default); await f.Module.CatalogPageAsync(1);
            var before = f.Module.CatalogSections.ToArray(); var calls = f.Client.Count("music.catalog.home");
            await f.Module.NavigateAsync("catalog-channels", default);
            await f.Module.NavigateAsync("catalog-home", default);
            Equal(calls, f.Client.Count("music.catalog.home")); Equal("第 2 页", f.Module.CatalogPageText);
            True(before.SequenceEqual(f.Module.CatalogSections), "same successful state restored");
        });
        yield return Case("native save complete 205 song playlist through Native application and DPAPI", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            await f.Configure(new { nextDetail = 0 });
            await f.Module.SaveCatalogPlaylistAsync();
            var list = f.Module.Playlists.Single(p => p.Kind == "playlist");
            Equal(205, list.Count);
            var tail = await f.Rpc("music.library.detail", new { id = list.Id, offset = 200, limit = 100 });
            Equal(5, tail.GetProperty("songs").GetArrayLength());
            True(f.Client.Log.Where(c => c.Method.StartsWith("music.catalog.playlist.", StringComparison.Ordinal))
                .All(c => c.Parameters.GetRawText().Length < 1024), "token control only, not oversized song JSON");
            Equal("已保存到本地歌单", f.Module.Status);
        });
        yield return Case("empty cloud list saves locally and play-all remains disabled", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Configure(new { total = 0 });
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            True(f.Module.CanCatalogAction && !f.Module.CanPlayCatalogPlaylist, "empty save is allowed, absent playback is not");
            await f.Module.SaveCatalogPlaylistAsync();
            Equal(0, f.Module.Playlists.Single(p => p.Kind == "playlist").Count);
        });
        yield return Case("native full playlist selected song 204 replaces complete queue and resolves chosen recording", async () =>
        {
            await using var f = await Fixture.Create();
            await f.SetupSource();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            await f.Module.CatalogPageAsync(1); await f.Module.CatalogPageAsync(1);
            var card = f.Module.CatalogSections.Single().Items[204];
            await f.Configure(new { nextDetail = 0 });
            await f.Module.OpenCatalogCardAsync(card);
            var q = await f.Rpc("music.queue.get", new { limit = 1 });
            Equal(205, q.GetProperty("total").GetInt32());
            Equal("1205", q.GetProperty("current").GetProperty("song").GetProperty("source_data").GetProperty("musicId").GetString());
            Equal(1, f.Playback.Opens.Count);
            Equal("https://media.invalid/1205", f.Playback.Opens.Single().AbsoluteUri);
            Equal(0, f.Client.Count("music.history.recordPlayed"));
        });
        yield return Case("new selection cancels full-list load without queue replacement or auto play", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            await f.Configure(new { nextDetail = 0 });
            var hold = f.Client.Hold("music.catalog.playlist.prepare");
            var loading = f.Module.PlayCatalogPlaylistAsync(); await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Module.SelectedSong = f.Module.Songs[1]; // real public user-intent hook
            hold.Release(); await loading;
            Equal(0, f.Client.Count("music.catalog.playlist.commit"));
            Equal(0, f.Playback.Opens.Count);
            Equal(0, (await f.Rpc("music.queue.get", new { })).GetProperty("total").GetInt32());
        });
        yield return Case("cancel full-list save before commit creates no partial collection", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            await f.Configure(new { nextDetail = 0 });
            var hold = f.Client.Hold("music.catalog.playlist.prepare");
            var loading = f.Module.SaveCatalogPlaylistAsync(); await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Module.CancelCatalog(); await Task.Delay(35); await Drain();
            True(!loading.IsCompleted, "cancelled prepare abandoned its successful token receipt");
            hold.Release(); await loading;
            Equal(0, f.Client.Count("music.catalog.playlist.commit"));
            Equal(1, f.Client.Count("music.catalog.playlist.discard"));
            Equal(1, (await f.Rpc("music.library.list", new { })).GetProperty("total").GetInt32());
        });
        yield return Case("manual pause intent cancels old playlist load even with no current media", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            await f.Configure(new { nextDetail = 0 });
            var hold = f.Client.Hold("music.catalog.playlist.prepare");
            var loading = f.Module.PlayCatalogPlaylistAsync(); await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Module.TogglePlaybackAsync();
            hold.Release(); await loading;
            Equal(0, f.Client.Count("music.catalog.playlist.commit")); Equal(0, f.Playback.Opens.Count);
        });
        yield return Case("cancel after admitted playlist commit waits receipt and reconciles without autoplay", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            await f.Configure(new { nextDetail = 0 });
            var hold = f.Client.Hold("music.catalog.playlist.commit");
            var loading = f.Module.PlayCatalogPlaylistAsync(); await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Module.CancelCatalog(); await Task.Delay(35); await Drain();
            True(!loading.IsCompleted, "admitted queue commit was reported cancelled before its receipt settled");
            hold.Release(); await loading;
            Equal(1, f.Client.Count("music.catalog.playlist.commit"));
            Equal(1, f.Client.Count("music.catalog.playlist.discard"));
            Equal(205, (await f.Rpc("music.queue.get", new { })).GetProperty("total").GetInt32());
            Equal(0, f.Playback.Opens.Count);
        });
        yield return Case("queue changed during prepare is caught by real management CAS", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            await f.Configure(new { nextDetail = 0 });
            var hold = f.Client.Hold("music.catalog.playlist.prepare");
            var loading = f.Module.PlayCatalogPlaylistAsync(); await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // A second component calls the native application without a UI intent hook.
            await f.Rpc("music.queue.clear", new { });
            hold.Release(); await loading;
            Equal(1, f.Client.Count("music.catalog.playlist.commit"));
            Equal(0, (await f.Rpc("music.queue.get", new { })).GetProperty("total").GetInt32());
            Equal(0, f.Playback.Opens.Count); Equal("内容已变更，请刷新", f.Module.Status);
        });
        yield return Case("anonymous home hides login-only cards but keeps real public content", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-home", default);
            var view = new MusicCatalogView { DataContext = f.Module };
            var window = new Window { Content = view, Width = 900, Height = 650, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false };
            try
            {
            window.Show(); window.UpdateLayout(); await Drain();
            var buttons = Descendants<Button>(view).ToArray();
            True(!buttons.Any(b => b.Tag is MusicCatalogCard { Kind: "daily" }), "login-only entry not rendered");
            True(!Descendants<TextBlock>(view).Any(t => t.Text == "当前内容需要平台登录"), "no login warning tiles");
            True(f.Module.CatalogSections.SelectMany(s => s.Items).Any(c => c.Kind == "daily" && !c.CanOpen), "capability and action denial remain intact");
            True(buttons.Any(b => b.Tag is MusicCatalogCard { Song: not null } && b.IsEnabled), "nested song actual WPF card enabled");
            }
            finally { window.Close(); }
        });
        yield return Case("song template loads its real cover and uses reference typography", async () =>
        {
            const string address = "https://art.example.test/song.jpg";
            var view = new MusicCatalogView();
            var row = new MusicCatalogCard("1", "测试歌曲 Song", "测试歌手 / Artist", address,
                "song", "wy", new SongRow(JsonSerializer.SerializeToElement(new { songId = "1", title = "测试歌曲 Song" })), ActionId: "1");
            var content = (FrameworkElement)((DataTemplate)view.FindResource("Catalog.SongRow")).LoadContent();
            content.DataContext = row; view.Content = content;
            var loader = new CatalogArtworkLoader(); ArtworkScope.SetLoader(view, loader);
            var window = new Window { Content = view, Width = 430, Height = 130, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                Equal(1, loader.Addresses.Count); Equal(address, loader.Addresses[0]);
                var image = Descendants<ArtworkImage>(view).Single();
                Equal(50d, image.ActualWidth); Equal(50d, image.ActualHeight);
                True(Descendants<Image>(image).Single().Source is not null, "real artwork control displays acquired pixels");
                var title = Descendants<TextBlock>(view).Single(t => t.Text == row.Title);
                var artist = Descendants<TextBlock>(view).Single(t => t.Text == row.Subtitle);
                AssertGrayscaleText([title, artist], "actual catalog song title and artist");
                Equal(SystemFonts.MessageFontFamily, title.FontFamily);
                Equal(14d, title.FontSize); Equal(20d, title.LineHeight);
                Equal(12d, artist.FontSize); Equal(18d, artist.LineHeight); Equal(2d, artist.Margin.Top);
                Equal(84d, content.ActualHeight); Equal(new CornerRadius(12), ((Border)content).CornerRadius);
                Equal(72d, Descendants<StackPanel>(view).Single(p => p.Children.OfType<Button>().Count() == 2)
                    .Children.OfType<Button>().Sum(b => b.Width));
            }
            finally { window.Close(); }
        });
        yield return Case("reference home templates use measured entry playlist and banner geometry", () =>
        {
            var row = new MusicCatalogCard("1", "入口标题", "真实描述", "", "playlist", "wy", ActionId: "1");
            var view = new MusicCatalogView();
            foreach (var (type, width, height) in new[] { ("Catalog.Entry", 168d, 200d), ("Catalog.PlaylistCard", 140d, 172d), ("Catalog.Banner", 282d, 146.72d) })
            {
                var card = (FrameworkElement)((DataTemplate)view.FindResource(type)).LoadContent();
                card.DataContext = row; card.Measure(new Size(width, double.PositiveInfinity));
                card.Arrange(new Rect(0, 0, width, card.DesiredSize.Height)); card.UpdateLayout();
                True(Math.Abs(card.ActualHeight - height) <= 1, type + " reference height " + card.ActualHeight);
            }
            foreach (var (type, expected) in new[] { ("entries", 168.4), ("playlists", 140.333333), ("banner", 282d) })
            {
                var shelf = new Shelf { MinimumCardWidth = 124, DataContext = new MusicCatalogSection("", [row], Type: type) };
                shelf.Measure(new Size(826, 300)); shelf.Arrange(new Rect(0, 0, 826, 300)); shelf.UpdateLayout();
                True(Math.Abs(shelf.CardWidth - expected) < .01, type + " reference pitch");
            }
            var anonymous = new MusicCatalogSection("", [row, row with { Id = "private", Available = false, ErrorCode = "AUTH_REQUIRED" }]);
            Equal(1, anonymous.WithoutLoginOnlyCards().Items.Count);
            Equal(2, (anonymous with { Items = [row, row with { Id = "private", Available = true }] }).WithoutLoginOnlyCards().Items.Count);
            return Task.CompletedTask;
        });
    }
    private sealed class CatalogArtworkLoader : IArtworkLoader
    {
        internal readonly List<string> Addresses = [];
        public Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default)
        { Addresses.Add(address); return Task.FromResult<IArtworkLease>(new CatalogArtworkLease()); }
    }
    private sealed class CatalogArtworkLease : IArtworkLease
    {
        public BitmapSource Image { get; } = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 90, 120, 180, 255 }, 4);
        public void Dispose() { }
    }
    private static TestCase Case(string name, Func<Task> body) => new("music-catalog: " + name, () => OnSta(body));
    private static JsonElement Edit(JsonElement value, Action<JsonObject> apply)
    {
        var node = JsonNode.Parse(value.GetRawText())!.AsObject(); apply(node); return JsonSerializer.SerializeToElement(node);
    }
    private static void Equal<T>(T expected, T actual, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Catalog assertion line {line}: expected {expected}, actual {actual}"); }
    private static void True(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }
    private static void AssertGrayscaleText(IEnumerable<TextBlock> nodes, string label)
    {
        var text = nodes.Where(node => !string.IsNullOrWhiteSpace(node.Text)).ToArray();
        True(text.Length > 0, label + " must include actual text nodes");
        foreach (var node in text)
        {
            var mode = TextOptions.GetTextRenderingMode(node);
            True(mode == TextRenderingMode.Grayscale,
                $"{label}: '{node.Text}' must have effective Grayscale TextRenderingMode, actual {mode}");
        }
    }
    private static async Task Until(Func<bool> predicate, string failure = "Catalog UI condition did not settle")
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException(failure);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (!predicate()) await Task.Delay(1);
        }
        await Drain();
    }
    private static async Task Drain() { for (int i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static async Task WaitForSearch(MusicCapability module)
    {
        for (int i = 0; i < 500; i++)
        {
            if (module.CatalogSections.SelectMany(s => s.Items).Any() && module.SearchProgress.All(p => !p.Loading)) return;
            await Task.Delay(10); await Drain();
        }
        throw new InvalidOperationException("Native aggregate search did not settle");
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) yield return found;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static T? Ancestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T found) return found;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }
    private sealed class Held
    {
        public readonly TaskCompletionSource Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => released.TrySetResult();
        public async Task<JsonElement> Wait(JsonElement value, CancellationToken token)
        {
            using var registration = token.Register(() => Cancelled.TrySetResult());
            Arrived.TrySetResult(); await released.Task; return value;
        }
    }
    private sealed record Invocation(string Method, JsonElement Parameters);
    private sealed class VipApplication(IMusicApplication application) : MusicApplicationForwarder(application)
    {
        private const long Generation = 7;
        private string bannerType = "PLAYLIST", bannerId = "500";
        private bool signed;
        internal int Reads, Signs;

        public override event Action? VipPageInvalidated;

        public override Task<MusicVipPageSnapshot> ReadVipPageAsync(
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult(Snapshot());
        }

        public override Task<MusicVipSignReceipt> SignVipPageAsync(long expectedGeneration,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Equal(Generation, expectedGeneration);
            Signs++;
            signed = true;
            return Task.FromResult(new MusicVipSignReceipt(Generation, Sign()));
        }

        internal void SetBanner(string type, string id)
        {
            bannerType = type;
            bannerId = id;
        }

        internal void Invalidate() => VipPageInvalidated?.Invoke();

        private MusicVipPageSnapshot Snapshot()
        {
            var expiry = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds();
            return new(Generation,
                new(Generation, "NinonZQ", "https://img.invalid/avatar.jpg",
                    "SVIP · " + DateTimeOffset.FromUnixTimeMilliseconds(expiry)
                        .ToLocalTime().ToString("yyyy.M.d")),
                Sign(),
                new("https://img.invalid/banner.jpg", bannerType, bannerId));
        }

        private MusicVipSignCardData Sign() => new(
            "10月黑胶乐签", "第2天，得3成长值", false, false, signed ? "已打卡" : "打卡",
            "https://img.invalid/sign-bg.jpg",
            [
                new("25日", true, "", 1, false),
                new("26日", true, "", 2, false),
                new("今天", signed, "", signed ? 3 : 0, true)
            ]);
    }

    private sealed class Client(IMusicApplication application) : MusicApplicationForwarder(application)
    {
        public override event Action? LibrarySyncChanged;
        public void NotifyLibrarySync() => LibrarySyncChanged?.Invoke();
        public override event Action<JsonElement>? SearchChanged
        {
            add => Inner.SearchChanged += value;
            remove => Inner.SearchChanged -= value;
        }
        private readonly List<(string Method, Func<JsonElement, CancellationToken, Task<JsonElement>> Apply)> rules = [];
        private readonly List<Held> holds = [];
        public readonly List<Invocation> Log = [];
        public int Count(string method) => Log.Count(c => c.Method == method);
        public JsonElement Last(string method) => Log.Last(c => c.Method == method).Parameters;
        public Held Hold(string method)
        {
            var held = new Held(); holds.Add(held); rules.Add((method, held.Wait)); return held;
        }
        public void Transform(string method, Func<JsonElement, JsonElement> change) =>
            rules.Add((method, (value, _) => Task.FromResult(change(value))));
        public void ReleaseAll() { foreach (var h in holds) h.Release(); }
        public override async Task<JsonElement> ExecuteAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            Log.Add(new(method, JsonSerializer.SerializeToElement(parameters ?? new { })));
            var index = rules.FindIndex(r => r.Method == method);
            Func<JsonElement, CancellationToken, Task<JsonElement>>? transform = null;
            if (index >= 0) { transform = rules[index].Apply; rules.RemoveAt(index); }
            var result = await Inner.ExecuteAsync(method, parameters ?? new { }, cancellationToken);
            return transform is null ? result : await transform(result, cancellationToken); // deliberately delayed old receipt
        }
    }
    private sealed class Output : IMusicPlayback
    {
        public string? OwnerId { get; private set; }
        public string? Identity { get; private set; }
        public bool HasMedia { get; private set; }
        public bool IsPlaying => false;
        public double PositionSeconds => 0;
        public double DurationSeconds => 180;
        public int Volume { get; set; } = 80;
        public List<Uri> Opens { get; } = [];
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public event EventHandler? Started { add { } remove { } }
        public event EventHandler? Ended { add { } remove { } }
        public event EventHandler? Failed { add { } remove { } }
        public Task OpenAsync(string owner, string identity, Uri uri, string title, CancellationToken token)
        { token.ThrowIfCancellationRequested(); OwnerId = owner; Identity = identity; HasMedia = true; Opens.Add(uri); return Task.CompletedTask; }
        public Task PauseAsync() => Task.CompletedTask;
        public Task ResumeAsync() => Task.CompletedTask;
        public Task StopAsync() { HasMedia = false; return Task.CompletedTask; }
        public Task SeekAsync(double seconds) => Task.CompletedTask;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DpapiDocumentStore store;
        private readonly NativeMusicScriptTestRuntime runtime = new();
        private readonly CatalogTransport transport = new();
        public Client Client { get; }
        public Output Playback { get; } = new();
        public MusicCapability Module { get; }
        public VipApplication? Vip { get; }
        private Fixture(DpapiDocumentStore store, bool vipAccount)
        {
            this.store = store;
            var native = new MusicNativeApplication(store, runtime, transport);
            runtime.Register(native);
            IMusicApplication application = native;
            if (vipAccount) application = Vip = new(native);
            Client = new(application); Module = new(Client, store, Playback);
        }
        public static async Task<Fixture> Create(bool vipAccount = false)
        {
            var data = Environment.GetEnvironmentVariable("MEOWFILM_TEST_DATA_ROOT") ?? throw new InvalidOperationException("Isolated test data required");
            var dir = Path.Combine(data, "catalog-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            var store = new DpapiDocumentStore(dir, ["music.library.v1", "music.accounts.v1", "music.files.v1"]);
            var f = new Fixture(store, vipAccount);
            try
            {
                await f.Module.InitializeAsync(default);
                True(!f.runtime.HasStarted, "ordinary native initialization is script-free");
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async Task<JsonElement> Rpc(string method, object parameters)
        {
            var result = await Client.ExecuteAsync(method, parameters);
            True(result.GetProperty("ok").GetBoolean(), "fixture native command failure " + method + ":" + result.GetRawText());
            return result;
        }
        public async Task SearchPolicy(string platform, bool playlists)
        {
            foreach (var row in Module.SongSearch.Platforms.ToArray())
                await Module.SetSearchPlatformEnabledAsync("songs", row, !playlists && row.Platform == platform);
            foreach (var row in Module.PlaylistSearch.Platforms.ToArray())
                await Module.SetSearchPlatformEnabledAsync("playlists", row, playlists && row.Platform == platform);
            await Module.SetSearchModeAsync(playlists ? "playlists" : "songs", "aggregate");
        }
        public Task Configure(object state)
        {
            foreach (var pair in JsonSerializer.SerializeToNode(state)!.AsObject())
                transport.State[pair.Key] = pair.Value?.DeepClone();
            return Task.CompletedTask;
        }
        public async Task SetupSource()
        {
            const string source = """
                lx.on(lx.EVENT_NAMES.request, async ({info}) => 'https://media.invalid/' + info.musicInfo.musicId);
                lx.send(lx.EVENT_NAMES.inited,{status:true,sources:{wy:{name:'合成',type:'music',actions:['musicUrl'],qualitys:['320k']}}});
                """;
            var bytes = System.Text.Encoding.UTF8.GetBytes(source);
            var upload = await Rpc("music.transfer.begin", new { kind = "script", basename = "catalog-fixture.js", length = bytes.Length,
                sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) });
            var token = upload.GetProperty("token").GetString()!;
            await Rpc("music.transfer.chunk", new { token, offset = 0, data = Convert.ToBase64String(bytes) });
            var result = await Rpc("music.sources.install", new { token, name = "合成源", version = "1", permissions = new { } });
            var installationId = result.GetProperty("item").GetProperty("installationId").GetString();
            await Rpc("music.sources.enable", new { installationId });
            await Module.RefreshOnlineAsync();
            Module.SelectedSource = Module.Sources.Single(); await Module.LoadSourceAsync(Module.SelectedSource);
            await Module.UseSourcePlatformAsync("wy");
        }
        public async ValueTask DisposeAsync()
        {
            Client.ReleaseAll();
            try { await Module.DisposeAsync(); }
            finally
            {
                await runtime.DisposeAsync(); await store.DisposeAsync();
            }
        }
    }
    private sealed class CatalogTransport : HttpMessageHandler
    {
        internal readonly JsonObject State = new();
        private int artistReads;
        private int Number(string key, int fallback = 0) => State[key]?.GetValue<int>() ?? fallback;
        private static object Wy(int id) => new { id = id.ToString(), name = "合成曲 " + id,
            ar = new[] { new { name = "合成艺人" } }, al = new { name = "合成专辑" }, dt = 185000 };
        private static object Tx(int id) => new { mid = "mid" + id, id = id.ToString(), type = 0, title = "合成曲 " + id,
            singer = new[] { new { name = "合成艺人" } }, album = new { name = "合成专辑", mid = "albumM1" }, interval = 185, file = new { } };
        private static object Kg(int id) => new { FileHash = id.ToString("X").PadLeft(32, '0'), FileName = "合成艺人 - 合成曲 " + id,
            SingerName = "合成艺人", AlbumName = "合成专辑", AlbumID = "31", MixSongID = id.ToString(), Duration = 185 };
        private static object Channel(string code, string name, string pageCode = "") => new
        {
            code,
            name,
            url = pageCode.Length == 0 ? "" : "orpheus://rnpage?component=cube-renderer-rn&page=" + pageCode
        };
        private static object[] Channels() =>
        [
            Channel("chart", "排行榜"), Channel("vip", "VIP"), Channel("playlist", "歌单"),
            Channel("huayu", "华语", "MUSIC_HUAYU"), Channel("qingge", "情歌", "MUSIC_QINGGE"),
            Channel("euro", "欧美", "MUSIC_EURO"), Channel("djR", "电音", "MUSIC_DJR"),
            Channel("yingshi", "OST", "MUSIC_OST"), Channel("rap", "说唱", "MUSIC_RAP"),
            Channel("music_top_tab_dj", "慢摇DJ", "MUSIC_SLOW_DJ"), Channel("music_top_tab_sport", "运动", "MUSIC_SPORT"),
            Channel("china", "国风", "MUSIC_CHINA"), Channel("yueyu", "粤语", "MUSIC_CANTONESE"),
            Channel("rnb", "R&B", "MUSIC_RNB"), Channel("ballad", "民谣", "MUSIC_BALLAD"),
            Channel("rock", "摇滚", "MUSIC_ROCK"), Channel("paobu", "跑步", "MUSIC_RUNNING"),
            Channel("music_top_tab_acg", "二次元", "MUSIC_ACG"), Channel("yueqi", "乐器", "MUSIC_INSTRUMENT"),
            Channel("chuxing", "出行", "MUSIC_TRAVEL"), Channel("hFlow", "K-Pop", "MUSIC_KPOP"),
            Channel("music_tab_drive", "驾车", "MUSIC_DRIVE"), Channel("classic", "古典", "MUSIC_CLASSIC"),
            Channel("study", "专注", "MUSIC_STUDY"), Channel("sir", "爵士", "MUSIC_JAZZ"),
            Channel("music_tab_pioneer", "先锋", "MUSIC_PIONEER"), Channel("xiangcun", "乡村乐", "MUSIC_COUNTRY"),
            Channel("funk", "放克", "MUSIC_FUNK"), Channel("landiao", "蓝调", "MUSIC_BLUES"),
            Channel("music_top_tab_children", "儿歌", "MUSIC_CHILDREN"), Channel("music_top_tab_game", "游戏", "MUSIC_GAME"),
            Channel("music_top_tab_light_music", "轻音乐", "MUSIC_LIGHT"), Channel("music_top_tab_jpop", "日语", "MUSIC_JPOP"),
            Channel("HiFi", "高音质", "MUSIC_HIFI"), Channel("music_top_tab_french", "法语", "MUSIC_FRENCH"),
            Channel("music_top_tab_dance", "舞蹈", "MUSIC_DANCE"), Channel("music_top_tab_latin", "拉丁", "MUSIC_LATIN"),
            Channel("music_top_tab_sunoai", "AI", "MUSIC_AI"), Channel("music_top_tab_world", "全球", "MUSIC_WORLD"),
            Channel("yinyueju", "音乐剧", "MUSIC_MUSICAL"), Channel("music_top_tab_tipsy", "放松", "MUSIC_RELAX"),
            Channel("sleep", "助眠", "MUSIC_SLEEP"), Channel("old_hat", "经典", "MUSIC_OLD"),
            Channel("feature", "精选"), Channel("newsong", "新歌"), Channel("origin", "原创", "MUSIC_ORIGIN")
        ];
        private static Dictionary<string, string> Form(string text) => text.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
        private static async Task<JsonObject> Eapi(HttpRequestMessage request)
        {
            var form = Form(await request.Content!.ReadAsStringAsync());
            using var aes = Aes.Create(); aes.Key = "e82ckenh8dichen8"u8.ToArray();
            var parts = Encoding.UTF8.GetString(aes.DecryptEcb(Convert.FromHexString(form["params"]), PaddingMode.PKCS7)).Split("-36cd479b6b5-");
            True(parts.Length == 3 && parts[2] == Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes("nobody" + parts[0] + "use" + parts[1] + "md5forencrypt"))),
                "production EAPI request checksum");
            return JsonNode.Parse(parts[1])!.AsObject();
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage Json(object body, int status = 200) => new((HttpStatusCode)status)
            { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            var uri = request.RequestUri!; var path = uri.AbsolutePath; int total = Number("total", 205);
            if (uri.Host == "u.y.qq.com")
            {
                var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["req_0"]!["param"]!;
                if (payload["query"] is not null) return Json(new { code = 0, req_0 = new { code = 0, data = new {
                    meta = new { sum = 1, nextpage = -1 }, body = new { item_song = new[] { Tx(1001) } } } } });
                int first = payload["song_begin"]!.GetValue<int>(), count = Math.Min(payload["song_num"]!.GetValue<int>(), Math.Max(0, total - first));
                return Json(new { code = 0, req_0 = new { code = 0, data = new {
                    dirinfo = new { tid = payload["disstid"]!.ToString(), title = "完整云歌单" }, songlist_size = count,
                    total_song_num = total, hasmore = first + count < total ? 1 : 0, songlist = Enumerable.Range(1001 + first, count).Select(Tx) } } });
            }
            if (uri.Host == "gateway.kugou.com")
            {
                var p = Form(uri.Query);
                if (path.Contains("/search/", StringComparison.Ordinal)) return Json(new { status = 1, error_code = 0, data = new { total = 1, lists = new[] { Kg(1001) } } });
                int first = int.Parse(p["begin_idx"]), count = Math.Min(int.Parse(p.GetValueOrDefault("pagesize", p.GetValueOrDefault("page_size", "100"))), Math.Max(0, total - first));
                return Json(new { status = 1, error_code = 0, data = new {
                    list_info = new { global_collection_id = p["global_collection_id"], name = "完整云歌单", count = total },
                    songs = Enumerable.Range(1001 + first, count).Select(Kg) } });
            }
            True(uri.Host is "music.163.com" or "interfacepc.music.163.com", "no unregistered upstream");
            if (path.Contains("/playlist/detail", StringComparison.Ordinal))
            {
                var p = await Eapi(request);
                return Json(new { code = 200, playlist = new { id = p["id"]!.ToString(), name = "完整云歌单",
                    coverImgUrl = "https://img.invalid/playlist.jpg", trackCount = total,
                    trackIds = Enumerable.Range(1001, total).Select(id => new { id = id.ToString() }), updateTime = 1 } });
            }
            if (path.Contains("/song/detail", StringComparison.Ordinal))
            {
                var form = Form(await request.Content!.ReadAsStringAsync());
                True(form["encSecKey"].Length == 256, "production WEAPI key remains encrypted");
                int first = Number("nextDetail"), count = Math.Min(Number("detailLimit", 100), total - first);
                State["nextDetail"] = first + count >= total ? 0 : first + count;
                return Json(new { code = 200, songs = Enumerable.Range(1001 + first, count).Select(Wy) });
            }
            if (path.Contains("/cloudsearch/", StringComparison.Ordinal))
            {
                var p = await Eapi(request);
                int first = p["offset"]?.GetValue<int>() ?? 0;
                int playlistTotal = Number("searchPlaylistTotal", 1);
                int count = Math.Min(p["limit"]?.GetValue<int>() ?? 30, Math.Max(0, playlistTotal - first));
                return Json(new { code = 200, result = new
                {
                    playlistCount = playlistTotal,
                    playlists = Enumerable.Range(500 + first, count)
                        .Select(id => new { id = id.ToString(), name = "完整云歌单", trackCount = 205 })
                } });
            }
            if (path.EndsWith("/position/show/resource", StringComparison.Ordinal))
                return Json(new { code = 200, data = new { generalizedSceneShow = new { generalizedMap = new { nowChannelItems = Channels() } } } });
            if (path.StartsWith("/weapi/v1/artist/", StringComparison.Ordinal) &&
                !path.EndsWith("/list", StringComparison.Ordinal))
            {
                var id = int.Parse(path[(path.LastIndexOf('/') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                return Json(new
                {
                    code = 200,
                    artist = new
                    {
                        id = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        name = "合成歌手 " + id,
                        picUrl = "https://img.invalid/artist-" + id + ".jpg"
                    },
                    hotSongs = Enumerable.Range(4101, 3).Select(Wy)
                });
            }
            if (path.EndsWith("/artist/list", StringComparison.Ordinal))
            {
                int page = Interlocked.Increment(ref artistReads) - 1;
                int pages = Math.Max(1, Number("artistPages", 1));
                return Json(new
                {
                    code = 200,
                    artists = Enumerable.Range(1 + page * 40, 40).Select(id => new
                    {
                        id = id.ToString(),
                        name = "合成歌手 " + id,
                        picUrl = "https://img.invalid/artist-" + id + ".jpg",
                        musicSize = 100 + id
                    }),
                    more = page + 1 < pages
                });
            }
            if (path.EndsWith("/vipnewcenter/app/viptab/recommend/song/list", StringComparison.Ordinal))
                return Json(new { code = 200, data = new { title = "黑胶VIP 每日推荐",
                    songInfoList = new[] { Wy(3001), Wy(3002) } } });
            if (path.EndsWith("/toplist/detail/v2", StringComparison.Ordinal))
                return Json(new { code = 200, data = new[] { new { name = "会员榜", categoryCode = "vip",
                    frontDisplayType = "ranked-songs", list = new object[] {
                        new { targetType = "PLAYLIST", resourceId = "700", name = "黑胶VIP 热歌榜", canPlay = true,
                            rankItems = new[] { new { trackId = "3001", songName = "会员歌曲", artistName = "会员歌手", rank = 1, lastRank = 2 } } },
                        new { targetType = "PLAYLIST", resourceId = "701", name = "黑胶SVIP 飙升榜", canPlay = true,
                            rankItems = new[] { new { trackId = "3002", songName = "会员新歌", artistName = "会员歌手", rank = 1, lastRank = 0 } } },
                        new { targetType = "PLAYLIST", resourceId = "702", name = "普通热歌榜", canPlay = true,
                            rankItems = new[] { new { trackId = "1001", songName = "普通歌曲", artistName = "普通歌手", rank = 1, lastRank = 1 } } }
                    } } } });
            if (path.EndsWith("/cube/render/page/protocol", StringComparison.Ordinal))
                return Json(new { code = 200, data = new { pageProtocol = new { title = "摇滚频道", pages = new object[] {
                    new { id = "feed", componentName = "Container", props = new { songList = new { ruleId = "11", pointId = new[] { new { id = "22" } }, title = "频道歌曲" } } },
                    new { id = "banner", componentName = "Container", props = new { carousel = new[] { new { title = "嵌套歌曲", action = "orpheus://song/1001" } } } } } } } });
            if (path.EndsWith("/tag/tab/tags", StringComparison.Ordinal))
                return Json(new { code = 200, data = new { algTagInfos = new[] { new { algTagId = "30", algTagName = "风格一" }, new { algTagId = "31", algTagName = "风格二" } } } });
            if (path.EndsWith("/tag/tab/alg/recommends", StringComparison.Ordinal))
            {
                var p = await Eapi(request); int offset = p["offset"]!.GetValue<int>();
                var tag = p["algTagIds"]?.ToString() ?? "";
                if (State["tagFailure"]?.GetValue<bool>() == true && tag == "31") return Json(new { }, 503);
                return Json(new { code = 200, songs = new[] { Wy(2001 + offset + (int.TryParse(tag, out int n) ? n : 0)) }, hasMore = offset == 0 });
            }
            if (path.EndsWith("/rcmd/resource/show", StringComparison.Ordinal))
            {
                if (Number("homeFailure") != 0) return Json(new { }, Number("homeFailure"));
                var p = await Eapi(request); int cursor = p["cursor"]?.GetValue<int>() ?? 0;
                var entries = new List<object>();
                if (Number("detailLimit") > 0)
                    entries.Add(new { resourceType = "playlist", resourceId = (600 + cursor).ToString(), name = "入口歌单" });
                entries.Add(new { resourceType = "dailysongs", name = "每日推荐" });
                entries.Add(new { resourceType = "song", resourceExtInfo = new { songData = Wy(1001 + cursor) }, name = "嵌套歌曲" });
                return Json(new { code = 200, data = new { cursor = (cursor + 1).ToString(), hasMore = cursor < Number("homePages", 2) - 1,
                    blockCodeOrderList = "[\"PLAYLIST\"]", blocks = new object[] {
                        new { positionCode = "PLAYLIST", uiElement = new { subTitle = new { title = "推荐歌单" } },
                            creatives = new[] { new { resources = new[] { new { resourceType = "playlist", resourceId = (500 + cursor).ToString(), name = "合成歌单" } } } } },
                        new { positionCode = "ENTRANCE", creatives = new[] { new { resources = entries.ToArray() } } } } } });
            }
            throw new InvalidOperationException("Unexpected synthetic catalog request");
        }
    }
    private static async Task OnSta(Func<Task> action)
    {
        var completed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                Exception? error = null;
                try { await action(); await Drain(); } catch (Exception e) { error = e; }
                finally { completed.TrySetResult(error); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            try { Dispatcher.Run(); } finally { stopped.TrySetResult(); }
        }) { IsBackground = true, Name = "music-catalog-test" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        var failure = await completed.Task.WaitAsync(TimeSpan.FromSeconds(90));
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
