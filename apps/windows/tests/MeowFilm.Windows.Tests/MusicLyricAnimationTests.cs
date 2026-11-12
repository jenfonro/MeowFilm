using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class MusicLyricAnimationTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("accepted playback advances between real samples and pauses at the real position", async () =>
        {
            using var f = new Fixture([new("连续高亮", 0, 8)], position: 1);
            await f.Ready();
            var word = f.Words.Single();
            var size = word.DesiredSize;
            var glyphs = Glyphs(word).ToArray();
            Check(glyphs.Length > 0, "native glyph geometry is present");
            var initial = word.WordProgress;
            await Task.Delay(240); await Drain();
            Equal(1d, f.List.Position);
            if (AnimationEnabled)
                Check(word.WordProgress > initial + .015, "WPF advances highlight without another media position");
            else Near(initial, word.WordProgress, .001, "reduced animation keeps the last real sample");
            Equal(size, word.DesiredSize);
            f.List.Position = 1.4;
            f.List.IsPlaying = false;
            await Drain();
            Near(1.4 / 8, word.WordProgress, .001, "pause uses the actual media position");
            Check(!Animated(word), "pause removes the word clock");
            word.Foreground = Brushes.LightGray; await Drain();
            Check(glyphs.SequenceEqual(Glyphs(word)), "position and color changes reuse the native glyph geometry");
            var paused = word.WordProgress;
            await Task.Delay(240); await Drain();
            Near(paused, word.WordProgress, .001, "paused text never drifts");
            f.List.IsPlaying = true;
            await Drain();
            await Task.Delay(240); await Drain();
            if (AnimationEnabled) Check(word.WordProgress > paused + .015, "resume starts from the paused sample");
            f.List.Position = .4;
            await Drain();
            Near(.05, word.WordProgress, .03, "backward seek while playing replaces the old animation");
            f.List.Position = 7.2;
            await Drain();
            Near(.9, word.WordProgress, .03, "forward seek while playing uses the new position");
            f.List.IsPlaying = false;
            await Drain();
            Near(.9, word.WordProgress, .001, "pause after seek removes extrapolation");
            f.List.Position = .4; await Drain();
            Near(.05, word.WordProgress, .001, "backward seek preserves pause");
            f.List.Position = 7.2;
            await Drain();
            Near(.9, word.WordProgress, .001, "forward seek preserves pause");
            Equal("连续高亮", UIElementAutomationPeer.CreatePeerForElement(word)!.GetName());
        });
        yield return Case("unknown rate identity changes hidden and unloaded words retire their clocks", async () =>
        {
            using var f = new Fixture([new("身份", 0, 8)], position: 1);
            await f.Ready();
            var word = f.Words.Single();
            f.List.PlaybackRate = null;
            await Drain();
            Near(.125, word.WordProgress, .001, "no accepted rate means no extrapolation");
            await Task.Delay(160); await Drain();
            Near(.125, word.WordProgress, .001, "the requested rate is never assumed");
            Check(!Animated(word), "unconfirmed rate removes the clock");
            f.List.PlaybackRate = 2;
            await Drain();
            var start = word.WordProgress;
            await Task.Delay(240); await Drain();
            if (AnimationEnabled)
                Check(word.WordProgress - start > .04 && word.WordProgress - start < .15,
                    "accepted 2x advances the word at the accepted media rate");
            f.List.PresentationKey = "";
            await Drain();
            Near(.125, word.WordProgress, .001, "no occurrence identity means no extrapolation");
            Check(!Animated(word), "retiring the occurrence removes its clock even with an accepted rate");
            f.List.PresentationKey = "occurrence:2";
            f.List.PlaybackRate = null;
            f.List.Position = 0;
            await Drain();
            Near(0, word.WordProgress, .001, "new occurrence does not retain old progress");
            Check(!Animated(word), "new occurrence waits for its rate receipt");
            f.List.PlaybackRate = 1;
            f.List.Position = 1;
            await Drain();
            f.List.Visibility = Visibility.Hidden;
            await Drain();
            var hidden = word.WordProgress;
            await Task.Delay(180); await Drain();
            Near(hidden, word.WordProgress, .001, "hidden geometry is stopped");
            Check(!Animated(word), "hidden list has no word clocks");
            f.List.Position = 4;
            f.List.Visibility = Visibility.Visible;
            await Drain();
            Near(.5, word.WordProgress, .03, "showing uses the latest real position");
            f.Window.Content = null;
            await Drain();
            var unloaded = word.WordProgress;
            await Task.Delay(180); await Drain();
            Near(unloaded, word.WordProgress, .001, "unloaded word stops changing");
            Check(!Animated(word), "unload detaches the clock");
            f.List.Position = 2;
            f.Window.Content = f.List;
            await Drain();
            Near(.25, word.WordProgress, .03, "reload reconstructs from the current sample");
        });
        yield return Case("only visible active words animate and boundaries hand over without media ticks", async () =>
        {
            using var f = Fixture.WithLines([[
                new("完成", 0, .1), new("当前", .1, 1), new("随后", 1.1, 2), new("未来", 20, 1)
            ]], position: .2);
            await f.Ready();
            var words = f.Words.ToArray();
            Equal(4, words.Length);
            Near(1, words[0].WordProgress, .001, "completed word remains fully highlighted");
            Near(0, words[2].WordProgress, .001, "future word remains unhighlighted");
            Equal(AnimationEnabled ? 1 : 0, words.Count(Animated));
            await Task.Delay(1100); await Drain();
            Equal(.2, f.List.Position);
            if (AnimationEnabled)
            {
                Near(1, words[1].WordProgress, .001, "first word finishes without a media sample");
                Check(words[2].WordProgress > 0 && words[2].WordProgress < 1, "the next boundary activates the next word");
                Check(!Animated(words[0]) && !Animated(words[1]) && Animated(words[2]) && !Animated(words[3]),
                    "only the word currently crossing its duration owns an animation");
            }
            f.List.IsPlaying = false; f.List.Position = 0; await Drain();
            Equal(0, words.Count(Animated));
        });
        yield return Case("multiword rendering preserves negative short and zero-duration source intervals", async () =>
        {
            using var f = Fixture.WithLines([[
                new("负偏移", -.1, .2), new("短词", .1, .02), new("零时长", .12, 0)
            ]], position: 0);
            f.List.IsPlaying = false;
            await f.Ready();
            var words = f.Words.ToArray();
            Equal(3, words.Length);
            Near(.5, words[0].WordProgress, .001, "negative source time remains part of the real word interval");
            Near(0, words[1].WordProgress, .001, "the short word waits for its actual start");
            Near(0, words[2].WordProgress, .001, "the zero-duration word waits for its actual boundary");
            f.List.Position = .11; await Drain();
            Near(1, words[0].WordProgress, .001, "the offset word completes at its original end");
            Near(.5, words[1].WordProgress, .001, "a 20ms source word is not expanded to 50ms");
            Near(0, words[2].WordProgress, .001, "the zero-duration word does not start early");
            f.List.Position = .12; await Drain();
            Near(1, words[1].WordProgress, .001, "the short word completes at the source boundary");
            Near(1, words[2].WordProgress, .001, "the zero-duration word completes exactly at its boundary");
            f.List.Position = .1; await Drain();
            Near(0, words[1].WordProgress, .001, "backward seek restores the short word to its actual start");
            Near(0, words[2].WordProgress, .001, "backward seek resets the instant word");
            Check(!words.Any(Animated), "paused source boundaries never extrapolate");
        });
        yield return Case("unbounded final real word highlights at its start and stays highlighted without an animation", async () =>
        {
            using var f = Fixture.WithLines([[new("前词", 0, 1), new("末词", 1, 0)]], position: 0);
            await f.Ready();
            var words = f.Words.ToArray();
            Near(0, words[1].WordProgress, .001, "the unbounded word waits for its real start");
            await Task.Delay(1200); await Drain();
            if (!AnimationEnabled) f.List.Position = 1;
            await Drain();
            Near(1, words[1].WordProgress, .001, "the real start immediately highlights the whole final word");
            Check(!Animated(words[1]), "no synthetic closing duration creates a word animation");
            f.List.Position = 179; await Drain();
            Near(1, words[1].WordProgress, .001, "the final word remains highlighted near the song end");
            f.List.IsPlaying = false; f.List.Position = .5; await Drain();
            Near(0, words[1].WordProgress, .001, "a paused seek before the real start clears the final word");
            f.List.Position = 1; await Drain();
            Near(1, words[1].WordProgress, .001, "a paused seek to the real start highlights without a clock");
            Check(!Animated(words[1]), "the unbounded word never owns an interpolation clock");
        });
        yield return Case("multiword cached pages apply batched samples without rebuilding native glyphs", async () =>
        {
            using var f = Fixture.WithLines(MultiwordLines(), position: 1);
            await f.Ready();
            var words = f.Words.ToArray();
            Check(words.Any(word => !f.Visible(word)), "the multiword fixture realizes an offscreen cache page");
            Check(words.Count(word => f.Visible(word) && word.Word!.Time == .1) > 1,
                "multiple real rows contain an active word");
            var shapes = words.Where(f.Visible).ToDictionary(word => word,
                word => (Size: word.DesiredSize, Glyphs: Glyphs(word).ToArray()));
            Check(shapes.Values.All(shape => shape.Glyphs.Length > 0), "visible words retain native glyph runs");
            var activeWords = words.Where(word => f.Visible(word) && word.Word!.Time == .1).ToArray();
            foreach (var sample in new[] { 1.2, 1.4, 1.6 })
            {
                await Task.Delay(200);
                f.List.Position = sample;
                if (AnimationEnabled)
                    Check(activeWords.All(Animated), "real playback samples retain running clips until render calibration");
                await Drain();
            }
            f.List.PlaybackRate = 2;
            f.List.Position = 2;
            if (AnimationEnabled)
                Check(activeWords.All(Animated), "accepted rate and position updates share clip replacement");
            f.List.IsPlaying = false;
            Check(!words.Any(Animated), "pause immediately retires every active clip in a binding batch");
            f.List.Position = 4;
            f.List.PlaybackRate = 1;
            f.List.SelectedIndex = 1;
            await Drain();
            foreach (var word in f.Words.Where(f.Visible))
            {
                Near(word.Word!.Time == 0 ? 1 : word.Word.Time == .1 ? .65 : 0,
                    word.WordProgress, .001, "the final paused sample reaches each word in a row");
                word.Foreground = Brushes.LightGray;
            }
            await Drain();
            foreach (var (word, shape) in shapes)
            {
                Equal(shape.Size, word.DesiredSize);
                Check(shape.Glyphs.SequenceEqual(Glyphs(word)),
                    "sample batches selection and color retain native text geometry");
            }
            f.List.Position = 1;
            f.List.PlaybackRate = 2;
            f.List.IsPlaying = true;
            await Drain();
            var active = f.Words.Where(word => f.Visible(word) && word.Word!.Time == .1)
                .ToDictionary(word => word, word => word.WordProgress);
            await Task.Delay(200); await Drain();
            foreach (var (word, progress) in active)
                if (AnimationEnabled)
                    Check(Animated(word) && word.WordProgress > progress + .04,
                        "each visible row advances at the accepted rate between real samples");
                else Near(progress, word.WordProgress, .001, "reduced animation retains the real sample");
            foreach (var word in f.Words)
                Check(!Animated(word) || f.Visible(word), "cached pages never acquire progress clocks");
            DesktopTheme.SetIsHighContrast(f.List, true);
            Check(!f.Words.Any(Animated), "high contrast immediately retires all multiword clocks");
            await Drain();
            foreach (var word in f.Words.Where(word => f.Visible(word) && word.Word!.Time == .1))
                Near(.15, word.WordProgress, .001, "high contrast returns to the actual media sample");
        });
        yield return Case("multiword cached pages refresh after scrolling reflow hiding and reload", async () =>
        {
            var lines = MultiwordLines();
            using var f = Fixture.WithLines(lines, position: 1);
            await f.Ready();
            var firstRows = Visuals(f.List).OfType<ListBoxItem>().ToArray();
            Check(firstRows.Length > 0 && firstRows.Length < 64, "multiword rows remain virtualized");
            f.List.ScrollIntoView(lines[^1]);
            await Drain();
            var lastRows = Visuals(f.List).OfType<ListBoxItem>().ToArray();
            Check(lastRows.Length > 0 && lastRows.Length < 64, "scrolling retains a bounded row cache");
            Check(lastRows.Any(firstRows.Contains), "multiword row containers are actually recycled");
            foreach (var word in f.Words)
                Check(!Animated(word) || f.Visible(word), "recycled cached words have no offscreen clocks");
            f.List.IsPlaying = false;
            f.List.Position = 4;
            await Drain();
            var beforeReflow = f.Words.Where(f.Visible).ToHashSet();
            var rowHeight = lastRows.Max(row => row.ActualHeight);
            f.Window.Width = 210;
            await Drain();
            Check(Visuals(f.List).OfType<ListBoxItem>().Any(row => row.ActualHeight > rowHeight),
                "the real wrap panel reflows multiword rows");
            Check(f.Words.Where(f.Visible).Any(beforeReflow.Contains), "reflow preserves realized word controls");
            foreach (var word in f.Words.Where(f.Visible))
                Near(word.Word!.Time == 0 ? 1 : word.Word.Time == .1 ? .65 : 0,
                    word.WordProgress, .001, "newly visible words receive the current paused sample after reflow");
            f.List.Position = 1;
            f.List.IsPlaying = true;
            await Drain();
            foreach (var word in f.Words)
                Equal(AnimationEnabled && f.Visible(word) && word.Word!.Time == .1, Animated(word));
            var hidden = f.Words.First(word => f.Visible(word) && word.Word!.Time == .1);
            hidden.Visibility = Visibility.Hidden;
            Check(!Animated(hidden), "hiding an individual word immediately retires its clock");
            await Drain();
            Check(!Animated(hidden), "viewport refresh leaves a hidden word stopped");
            hidden.Visibility = Visibility.Visible;
            await Drain();
            Equal(AnimationEnabled, Animated(hidden));
            f.List.PresentationKey = "";
            Check(!f.Words.Any(Animated), "identity retirement removes every visible row clock immediately");
            f.List.Position = 4;
            await Drain();
            foreach (var word in f.Words.Where(word => f.Visible(word) && word.Word!.Time == .1))
                Near(.65, word.WordProgress, .001, "retired identity uses only the latest real sample");
            f.List.PresentationKey = "occurrence:2";
            var unloading = f.Words.ToArray();
            f.Window.Content = null;
            await Drain();
            Check(!unloading.Any(Animated), "an unloaded list retains no multiword clocks");
            f.List.Position = 2;
            f.Window.Content = f.List;
            await Drain();
            foreach (var word in f.Words.Where(word => f.Visible(word) && word.Word!.Time == .1))
                Near(1.9 / 6, word.WordProgress, .03, "reload uses the current sample with fresh viewport membership");
            foreach (var word in f.Words)
                Check(!Animated(word) || f.Visible(word), "reload leaves cached pages stopped");
        });
        yield return Case("compositor motion retains swept clocks and retires them on completion or cancellation", async () =>
        {
            using var f = Fixture.WithLines(MultiwordLines(), position: 1);
            await f.Ready();
            var start = f.Words.Where(word => f.Visible(word) && word.Word!.Time == .1)
                .Select(word => word.Word!).ToHashSet<MusicLyricWord>(ReferenceEqualityComparer.Instance);
            var scroll = Visuals(f.List).OfType<ScrollViewer>().First();
            var offset = scroll.VerticalOffset;
            scroll.ScrollToVerticalOffset(offset + scroll.ViewportHeight * .75);
            f.Window.UpdateLayout(); await Drain();
            var end = f.Words.Where(word => f.Visible(word) && word.Word!.Time == .1)
                .Select(word => word.Word!).ToHashSet<MusicLyricWord>(ReferenceEqualityComparer.Instance);
            Check(start.Any(source => !end.Contains(source)) && end.Any(source => !start.Contains(source)),
                "the native offset changes actual visible rows");
            var swept = start.Concat(end).ToHashSet<MusicLyricWord>(ReferenceEqualityComparer.Instance);
            Check(swept.All(source => f.Words.Any(word => ReferenceEquals(word.Word, source))),
                "one cache page realizes the source words from both viewport endpoints");
            var presenter = Visuals(f.List).OfType<ItemsPresenter>().First();
            var from = scroll.VerticalOffset - offset;
            var translation = new TranslateTransform(0, from);
            presenter.RenderTransform = translation;
            f.List.BeginPresentationMotion(translation, from);
            await Drain();
            foreach (var word in f.Words)
                Equal(AnimationEnabled && swept.Contains(word.Word!), Animated(word));
            translation.Y = 0;
            translation.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(420)) { FillBehavior = FillBehavior.Stop });
            await Task.Delay(120);
            // A real layout pass during motion must keep both endpoints, even though
            // TransformToAncestor now includes an intermediate presentation offset.
            f.List.InvalidateArrange();
            f.List.Position = 1.2;
            f.List.PlaybackRate = 2;
            await Drain();
            foreach (var word in f.Words)
                Equal(AnimationEnabled && swept.Contains(word.Word!), Animated(word));
            await Task.Delay(360); await Drain();
            translation.BeginAnimation(TranslateTransform.YProperty, null);
            translation.Y = 0;
            f.List.EndPresentationMotion(translation);
            await Drain();
            foreach (var word in f.Words)
                Equal(AnimationEnabled && f.Visible(word) && word.Word!.Time == .1, Animated(word));
            Equal(1.2, f.List.Position);
            scroll.ScrollToVerticalOffset(offset);
            f.Window.UpdateLayout(); await Drain();
            var reverse = -from;
            translation.Y = reverse;
            f.List.BeginPresentationMotion(translation, reverse);
            await Drain();
            foreach (var word in f.Words)
                Equal(AnimationEnabled && swept.Contains(word.Word!), Animated(word));
            translation.Y = 0;
            translation.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(reverse, 0, TimeSpan.FromMilliseconds(420)));
            await Task.Delay(120);
            translation.BeginAnimation(TranslateTransform.YProperty, null);
            translation.Y = 0;
            f.List.EndPresentationMotion(translation);
            await Drain();
            foreach (var word in f.Words)
                Equal(AnimationEnabled && f.Visible(word) && word.Word!.Time == .1, Animated(word));
            Equal(1.2, f.List.Position);
        });
        yield return Case("recycled rows keep viewport bounded animation and newly visible words use the current sample", async () =>
        {
            var lyrics = Enumerable.Range(0, 1000).Select(index => new MusicLyricWord("歌词 " + index, 0, 30)).ToArray();
            using var f = new Fixture(lyrics, position: 1);
            await f.Ready();
            var first = f.Words.First();
            var initialRows = Visuals(f.List).OfType<ListBoxItem>().ToArray();
            Check(initialRows.Length > 0 && initialRows.Length < 64, "real virtualization bounds containers");
            foreach (var word in f.Words)
                Check(!Animated(word) || f.Visible(word), "cached offscreen rows have no word clock");
            f.List.ScrollIntoView(lyrics[^1]);
            await Drain();
            var rows = Visuals(f.List).OfType<ListBoxItem>().ToArray();
            Check(rows.Length > 0 && rows.Length < 64, "visiting the last row keeps virtualization enabled");
            Check(rows.Any(row => initialRows.Contains(row)), "actual containers are recycled");
            foreach (var word in f.Words)
                Check(!Animated(word) || f.Visible(word), "recycling does not animate offscreen words");
            if (!f.Visible(first)) Check(!Animated(first), "retired/offscreen first word has no clock");
            f.List.Position = 12;
            f.List.IsPlaying = false;
            await Drain();
            foreach (var word in f.Words.Where(f.Visible))
                Near(.4, word.WordProgress, .001, "newly realized text uses the latest sample");
        });
        yield return Case("high contrast stops interpolation and text metric changes retain accurate progress", async () =>
        {
            using var f = new Fixture([new("中文🙂 e\u0301 العربية", 0, 10)], position: 2);
            await f.Ready();
            var word = f.Words.Single();
            DesktopTheme.SetIsHighContrast(f.List, true);
            await Drain();
            Near(.2, word.WordProgress, .001, "high contrast uses the real sample");
            Check(!Animated(word), "high contrast does not animate text");
            var small = word.DesiredSize;
            word.FontSize = 32;
            word.FlowDirection = FlowDirection.RightToLeft;
            await Drain();
            Check(word.DesiredSize.Width > small.Width && word.DesiredSize.Height > small.Height,
                "font and direction changes invalidate text metrics");
            Near(.2, word.WordProgress, .001, "reformatting preserves the sampled progress");
            Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(word));
            Equal(TextRenderingMode.Auto, TextOptions.GetTextRenderingMode(word));
            Equal("中文🙂 e\u0301 العربية", UIElementAutomationPeer.CreatePeerForElement(word)!.GetName());
        });
        yield return Case("repeated center requests share completion and cancellation retires pending settlement", async () =>
        {
            using var f = Fixture.WithLines(MultiwordLines(), position: 1);
            await f.Ready();
            var scroll = Visuals(f.List).OfType<ScrollViewer>().First();
            var presenter = Visuals(f.List).OfType<ItemsPresenter>().First();
            var translation = new TranslateTransform();
            presenter.RenderTransform = translation;
            var completions = 0;
            void Settled() => completions++;
            MusicLyricScroll.Move(scroll, 1500, translation, f.List, Settled);
            await Task.Delay(200);
            MusicLyricScroll.Move(scroll, 1500, translation, f.List, Settled);
            await Task.Delay(200);
            MusicLyricScroll.Move(scroll, 1500, translation, f.List, Settled);
            await Task.Delay(150); await Drain();
            Equal(AnimationEnabled ? 1 : 3, completions);
            Near(1500, scroll.VerticalOffset, .5, "same target completes from its original start under 200ms requests");
            var completed = completions;
            MusicLyricScroll.Move(scroll, 300, translation, f.List, Settled);
            MusicLyricScroll.Stop(scroll);
            await Task.Delay(550); await Drain();
            Equal(completed, completions);
            Near(0, translation.Y, .001, "cancellation retires the presentation transform");
            Check(!translation.HasAnimatedProperties, "cancellation removes the automatic clock");
        });
    }

    private static bool AnimationEnabled => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
    private static TestCase Case(string name, Func<Task> run) =>
        new("music-lyric-animation: " + name, () => MusicQualityConsumerTests.Sta(run));

    private sealed class Fixture : IDisposable
    {
        internal MusicLyricList List { get; }
        internal Window Window { get; }
        internal IEnumerable<MusicLyricWordView> Words => Visuals(List).OfType<MusicLyricWordView>();

        internal Fixture(MusicLyricWord[] words, double position) : this(words, WordTemplate(), position) { }

        internal static Fixture WithLines(MusicLyricWord[][] lines, double position) =>
            new(lines, LineTemplate(), position);

        private static DataTemplate WordTemplate()
        {
            var factory = new FrameworkElementFactory(typeof(MusicLyricWordView));
            factory.SetBinding(MusicLyricWordView.WordProperty, new Binding());
            factory.SetValue(Control.FontSizeProperty, 20d);
            factory.SetValue(Control.FontWeightProperty, FontWeights.Normal);
            factory.SetValue(Control.ForegroundProperty, Brushes.Gray);
            factory.SetValue(MusicLyricWordView.HighlightProperty, Brushes.White);
            return new DataTemplate { VisualTree = factory };
        }

        private static DataTemplate LineTemplate()
        {
            var factory = new FrameworkElementFactory(typeof(ItemsControl));
            factory.SetBinding(ItemsControl.ItemsSourceProperty, new Binding());
            factory.SetValue(ItemsControl.ItemTemplateProperty, WordTemplate());
            factory.SetValue(ItemsControl.ItemsPanelProperty,
                new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel))));
            return new DataTemplate { VisualTree = factory };
        }

        private Fixture(System.Collections.IEnumerable items, DataTemplate template, double position)
        {
            List = new MusicLyricList
            {
                ItemsSource = items, ItemTemplate = template, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Position = position, IsPlaying = true, PlaybackRate = 1, PresentationKey = "occurrence:1",
                Background = Brushes.Black, BorderThickness = new Thickness(0), Padding = new Thickness(0)
            };
            TextOptions.SetTextFormattingMode(List, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(List, TextRenderingMode.Auto);
            ScrollViewer.SetCanContentScroll(List, true);
            ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Disabled);
            VirtualizingPanel.SetIsVirtualizing(List, true);
            VirtualizingPanel.SetVirtualizationMode(List, VirtualizationMode.Recycling);
            VirtualizingPanel.SetScrollUnit(List, ScrollUnit.Pixel);
            VirtualizingPanel.SetCacheLength(List, new VirtualizationCacheLength(1));
            VirtualizingPanel.SetCacheLengthUnit(List, VirtualizationCacheLengthUnit.Page);
            Window = new Window
            {
                Content = List, Width = 420, Height = 220, ShowInTaskbar = false,
                WindowStyle = WindowStyle.None, Left = -32000, Top = -32000
            };
            Window.Show();
        }

        internal async Task Ready()
        {
            Window.UpdateLayout(); await Drain();
            Check(List.IsLoaded && List.IsVisible && Words.Any(), "native HWND realizes lyric words");
        }

        internal bool Visible(MusicLyricWordView word) => word.IsVisible && word.IsArrangeValid &&
            word.RenderSize.Width > 0 && word.RenderSize.Height > 0 && List.IsAncestorOf(word) &&
            word.TransformToAncestor(List).TransformBounds(new Rect(word.RenderSize)).IntersectsWith(new Rect(List.RenderSize));

        public void Dispose() => Window.Close();
    }

    private static MusicLyricWord[][] MultiwordLines() => Enumerable.Range(0, 1000)
        .Select(index => new MusicLyricWord[] {
            new($"行{index:D4} ", 0, .1), new("当前🙂 ", .1, 6),
            new("随后 e\u0301 ", 6.1, 6), new("العربية ", 12.1, 6)
        }).ToArray();

    private static bool Animated(MusicLyricWordView word)
    {
        static bool HasClock(DrawingGroup group) => group.ClipGeometry?.HasAnimatedProperties == true ||
            group.Children.OfType<DrawingGroup>().Any(HasClock);
        return VisualTreeHelper.GetDrawing(word) is { } drawing && HasClock(drawing);
    }

    private static IEnumerable<GlyphRun> Glyphs(MusicLyricWordView word)
    {
        static IEnumerable<GlyphRun> Runs(Drawing drawing)
        {
            if (drawing is GlyphRunDrawing glyph) yield return glyph.GlyphRun;
            if (drawing is DrawingGroup group)
                foreach (var child in group.Children)
                    foreach (var run in Runs(child)) yield return run;
        }
        return VisualTreeHelper.GetDrawing(word) is { } drawing ? Runs(drawing) : [];
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static async Task Drain()
    {
        for (var i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual)
    { Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; got {actual}"); }
    private static void Near(double expected, double actual, double tolerance, string message)
    { Check(Math.Abs(expected - actual) <= tolerance, $"{message}: expected {expected}; got {actual}"); }
}
