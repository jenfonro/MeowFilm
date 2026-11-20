using FontWeights = Microsoft.UI.Text.FontWeights;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI.Text;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Markup;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class MusicLyricAnimationTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("timed words retain real whitespace advances on first measure and text replacement", async () =>
        {
            var samples = new[] { ("Thought ", FlowDirection.LeftToRight), ("  ", FlowDirection.LeftToRight),
                ("العربية ", FlowDirection.RightToLeft), ("中文", FlowDirection.LeftToRight) };
            var wrap = new MusicWrapPanel();
            var words = samples.Select(sample => new MusicLyricWordView
            {
                Word = new(sample.Item1, 0, 2), FontSize = 24, FlowDirection = sample.Item2,
                Foreground = MusicVisuals.Brush("#FFFFFF"), HorizontalAlignment = HorizontalAlignment.Left
            }).ToArray();
            foreach (var word in words) wrap.Children.Add(word);
            var initial = new InitialWordMeasure(wrap, words);
            using var host = new DesktopTestUi.Host(initial, 600, 320);
            await Drain(); initial.UpdateLayout();
            Check(initial.Widths is not null, "the actual wrap consumer measures each word before its first arrange");
            for (int index = 0; index < words.Length; index++)
                Verify(words[index], samples[index].Item1, initial.Widths![index]);
            var retainedGlyphs = Glyphs(words[0]).ToArray();
            foreach (string text in new[] { "Thought", "Thought  ", "中文", "Thought " })
            {
                words[0].Word = new(text, 0, 2);
                initial.UpdateLayout();
                Verify(words[0], text, words[0].DesiredSize.Width);
                Check(retainedGlyphs.SequenceEqual(Glyphs(words[0])), "text replacement retains the native TextBlocks");
            }

            static void Verify(MusicLyricWordView word, string text, double measured)
            {
                var glyph = Glyphs(word).First();
                Equal(text, glyph.Text);
                Equal(word.FlowDirection, glyph.FlowDirection);
                if (!char.IsWhiteSpace(text[^1]))
                {
                    Near(glyph.DesiredSize.Width, measured, .01, "text without trailing whitespace receives no synthetic spacing");
                    return;
                }
                var run = (Microsoft.UI.Xaml.Documents.Run)glyph.Inlines[0];
                var first = run.ContentStart.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);
                var last = run.ContentEnd.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Backward);
                double nativeAdvance = Math.Abs(last.X - first.X);
                Near(nativeAdvance, measured, 2.01, "first and replacement layouts reserve the native text advance within the TextBlock and parent layout rounding");
                if (text.All(char.IsWhiteSpace)) Check(measured > 0, "a whitespace-only timed word reserves its real advance");
            }
        });
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
                Check(word.WordProgress > initial + .015, "the compositor advances highlight without another media position");
            else Near(initial, word.WordProgress, .001, "reduced animation keeps the last real sample");
            Equal(size, word.DesiredSize);
            word.Foreground = MusicVisuals.Brush("#D3D3D3");
            word.Highlight = MusicVisuals.Brush("#FFFFFF");
            word.Stroke = MusicVisuals.Brush("#191919");
            word.StrokeThickness = 1.1;
            if (AnimationEnabled) Check(Animated(word), "changing row colors does not interrupt the running compositor clip");
            await Drain();
            Check(glyphs.SequenceEqual(Glyphs(word)), "changing row colors retains native text geometry");
            f.List.Position = 1.4;
            f.List.IsPlaying = false;
            await Drain();
            Near(1.4 / 8, word.WordProgress, .001, "pause uses the actual media position");
            Check(!Animated(word), "pause removes the word clock");
            word.Foreground = MusicVisuals.Brush("#D3D3D3"); await Drain();
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
            word.Word = new("连续高亮", 4, 8);
            await Drain();
            Near(.4, word.WordProgress, .001, "replacing only source timing recalibrates the retained word");
            Check(glyphs.SequenceEqual(Glyphs(word)), "timing-only replacement retains native text controls");
            Equal("连续高亮", FrameworkElementAutomationPeer.CreatePeerForElement(word)!.GetName());
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
            f.List.Visibility = Visibility.Collapsed;
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
                word.Foreground = MusicVisuals.Brush("#D3D3D3");
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
            var firstRows = Visuals(f.List).OfType<ListViewItem>().ToArray();
            Check(firstRows.Length > 0 && firstRows.Length < 64, "multiword rows remain virtualized");
            f.List.ScrollIntoView(lines[^1]);
            await Drain();
            var lastRows = Visuals(f.List).OfType<ListViewItem>().ToArray();
            Check(lastRows.Length > 0 && lastRows.Length < 64, "scrolling retains a bounded row cache");
            Check(lastRows.Any(firstRows.Contains), "multiword row containers are actually recycled");
            foreach (var word in f.Words)
                Check(!Animated(word) || f.Visible(word), "recycled cached words have no offscreen clocks");
            f.List.IsPlaying = false;
            f.List.Position = 4;
            await Drain();
            var beforeReflow = f.Words.Where(f.Visible).ToHashSet();
            var rowHeight = lastRows.Max(row => row.ActualHeight);
            f.Window.Resize(210, 220);
            await Drain();
            Check(Visuals(f.List).OfType<ListViewItem>().Any(row => row.ActualHeight > rowHeight),
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
            hidden.Visibility = Visibility.Collapsed;
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
        yield return Case("compositor scrolling keeps active clocks viewport bounded and cancellation settles", async () =>
        {
            using var f = Fixture.WithLines(MultiwordLines(), position: 1);
            await f.Ready();
            var scroll = Visuals(f.List).OfType<ScrollViewer>().First();
            var before = f.Words.Where(f.Visible).Select(word => word.Word).ToHashSet();
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            double target = scroll.VerticalOffset + scroll.ViewportHeight * .75;
            MusicLyricScroll.Move(scroll, target, () => completed.TrySetResult());
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Drain();
            Near(target, scroll.VerticalOffset, .5, "native scrolling reaches the requested offset");
            Check(f.Words.Where(f.Visible).Any(word => !before.Contains(word.Word)), "actual visible rows change");
            foreach (var word in f.Words)
                Equal(AnimationEnabled && f.Visible(word) && word.Word!.Time == .1, Animated(word));
            f.List.Position = 1.2; f.List.PlaybackRate = 2; await Drain();
            bool cancelledCompleted = false;
            MusicLyricScroll.Move(scroll, 0, () => cancelledCompleted = true);
            MusicLyricScroll.Stop(scroll);
            await Drain();
            double stopped = scroll.VerticalOffset; await Task.Delay(550); await Drain();
            Check(!cancelledCompleted, "cancelled scrolling cannot complete an obsolete preview");
            Near(stopped, scroll.VerticalOffset, .5, "cancellation stops physical movement");
            foreach (var word in f.Words)
                Check(!Animated(word) || f.Visible(word), "no hidden words keep animation clocks after cancellation");
            Equal(1.2, f.List.Position);
        });
        yield return Case("a queued stop event cannot complete a later native scroll before its destination", async () =>
        {
            using var f = new Fixture(Enumerable.Range(0, 36).Select(index => new MusicLyricWord("歌词 " + index, 0, 30)).ToArray(), position: 1);
            await f.Ready();
            var scroll = Visuals(f.List).OfType<ScrollViewer>().First();
            double target = Math.Min(400, scroll.ScrollableHeight);
            Check(target > scroll.ViewportHeight, "a short fixture crosses the native viewport boundary");
            MusicLyricScroll.Settle(scroll, 48);
            var settled = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool intermediate = false;
            scroll.ViewChanged += (_, args) => intermediate |= args.IsIntermediate && scroll.VerticalOffset > 48 && scroll.VerticalOffset < target;
            MusicLyricScroll.Move(scroll, target, () => settled.TrySetResult(scroll.VerticalOffset));
            Near(target, await settled.Task.WaitAsync(TimeSpan.FromSeconds(5)), .5,
                "only the successor's final destination owns completion");
            if (AnimationEnabled) Check(intermediate, "automatic follow moves through intermediate native offsets instead of settling in one jump");
            Check(!MusicLyricScroll.IsMoving(scroll), "the actual final event clears the motion owner");
        });
        yield return Case("recycled rows keep viewport bounded animation and newly visible words use the current sample", async () =>
        {
            var lyrics = Enumerable.Range(0, 128).Select(index => new MusicLyricWord("歌词 " + index, 0, 30)).ToArray();
            using var f = new Fixture(lyrics, position: 1);
            await f.Ready();
            var first = f.Words.First();
            var initialRows = Visuals(f.List).OfType<ListViewItem>().ToArray();
            Check(initialRows.Length > 0 && initialRows.Length < 64, "real virtualization bounds containers");
            foreach (var word in f.Words)
                Check(!Animated(word) || f.Visible(word), "cached offscreen rows have no word clock");
            f.List.ScrollIntoView(lyrics[^1]);
            await Drain();
            var rows = Visuals(f.List).OfType<ListViewItem>().ToArray();
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
            f.List.UpdateLayout();
            var small = word.DesiredSize;
            word.FontSize = 32;
            word.FlowDirection = FlowDirection.RightToLeft;
            f.List.UpdateLayout(); await Drain();
            Check(word.DesiredSize.Width > small.Width && word.DesiredSize.Height > small.Height,
                $"font and direction changes invalidate text metrics: before={small}, after={word.DesiredSize}, font={word.FontSize}, glyph={Glyphs(word).First().DesiredSize}");
            Near(.2, word.WordProgress, .001, "reformatting preserves the sampled progress");
            Check(Glyphs(word).All(text => text.FontWeight == FontWeights.Normal && text.FlowDirection == FlowDirection.RightToLeft),
                "native text keeps regular weight and the current direction");
            Equal("中文🙂 e\u0301 العربية", FrameworkElementAutomationPeer.CreatePeerForElement(word)!.GetName());
        });
        yield return Case("repeated center requests share completion and cancellation retires pending settlement", async () =>
        {
            using var f = Fixture.WithLines(MultiwordLines(), position: 1);
            await f.Ready();
            var scroll = Visuals(f.List).OfType<ScrollViewer>().First();
            var completions = 0;
            void Settled() => completions++;
            MusicLyricScroll.Move(scroll, 1500, Settled);
            MusicLyricScroll.Move(scroll, 1500, Settled);
            MusicLyricScroll.Move(scroll, 1500, Settled);
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (completions == 0)
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Native lyric scroll did not settle.");
                await Task.Delay(10); await Drain();
            }
            Equal(1, completions);
            Near(1500, scroll.VerticalOffset, .5, "same in-flight target completes once instead of restarting");
            int replacedCompletions = 0;
            MusicLyricScroll.Move(scroll, 100, () => replacedCompletions++);
            var replacement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            MusicLyricScroll.Move(scroll, 800, () => replacement.TrySetResult());
            await replacement.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(0, replacedCompletions);
            Near(800, scroll.VerticalOffset, .5, "retargeting completes only the actual latest native scroll");
            var completed = completions;
            MusicLyricScroll.Move(scroll, 300, Settled);
            MusicLyricScroll.Stop(scroll);
            await Task.Delay(550); await Drain();
            Equal(completed, completions);
            double stopped = scroll.VerticalOffset; await Task.Delay(150); await Drain();
            Near(stopped, scroll.VerticalOffset, .5, "cancellation removes the native scrolling motion");
        });
    }

    private static bool AnimationEnabled => MusicVisuals.Animate;
    private static TestCase Case(string name, Func<Task> run) =>
        new("music-lyric-animation: " + name, () => MusicQualityConsumerTests.Sta(run));

    private sealed class InitialWordMeasure : Panel
    {
        private readonly MusicWrapPanel wrap;
        private readonly MusicLyricWordView[] words;
        internal double[]? Widths { get; private set; }
        internal InitialWordMeasure(MusicWrapPanel wrap, MusicLyricWordView[] words)
        { this.wrap = wrap; this.words = words; Children.Add(wrap); }
        protected override Size MeasureOverride(Size availableSize)
        {
            wrap.Measure(availableSize);
            Widths ??= words.Select(word => word.DesiredSize.Width).ToArray();
            return wrap.DesiredSize;
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            wrap.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
            return finalSize;
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal MusicLyricList List { get; }
        internal DesktopWindow Window { get; }
        internal IEnumerable<MusicLyricWordView> Words => Visuals(List).OfType<MusicLyricWordView>();

        internal Fixture(MusicLyricWord[] words, double position) : this(words, WordTemplate(), position) { }

        internal static Fixture WithLines(MusicLyricWord[][] lines, double position) =>
            new(lines, LineTemplate(), position);

        private static DataTemplate WordTemplate() => (DataTemplate)Application.Current.Resources["LyricTestWord"];
        private static DataTemplate LineTemplate() => (DataTemplate)Application.Current.Resources["LyricTestLine"];

        private Fixture(System.Collections.IEnumerable items, DataTemplate template, double position)
        {
            List = new MusicLyricList
            {
                ItemsSource = items, ItemTemplate = template, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Position = position, IsPlaying = true, PlaybackRate = 1, PresentationKey = "occurrence:1",
                Background = MusicVisuals.Brush("#000000"), BorderThickness = new Thickness(0), Padding = new Thickness(0)
            };
            List.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load("<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsStackPanel CacheLength='1'/></ItemsPanelTemplate>");
            ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Disabled);
            Window = new DesktopWindow { Content = List }; Window.AttachRoot(); Window.Resize(420, 220); Window.Activate();
        }

        internal async Task Ready()
        {
            List.UpdateLayout(); await Drain();
            Check(List.IsLoaded && List.Visibility == Visibility.Visible && Words.Any(), "native HWND realizes lyric words");
        }

        internal bool Visible(MusicLyricWordView word)
        {
            if (word.Visibility != Visibility.Visible || word.ActualWidth <= 0 || word.ActualHeight <= 0 || !MusicVisuals.IsWithin(word, List)) return false;
            var bounds = word.TransformToVisual(List).TransformBounds(new Rect(0, 0, word.ActualWidth, word.ActualHeight));
            bounds.Intersect(new Rect(0, 0, List.ActualWidth, List.ActualHeight));
            return !bounds.IsEmpty;
        }

        public void Dispose() => Window.Close();
    }

    private static MusicLyricWord[][] MultiwordLines() => Enumerable.Range(0, 128)
        .Select(index => new MusicLyricWord[] {
            new($"行{index:D4} ", 0, .1), new("当前🙂 ", .1, 6),
            new("随后 e\u0301 ", 6.1, 6), new("العربية ", 12.1, 6)
        }).ToArray();

    private static bool Animated(MusicLyricWordView word) => word.IsHighlightAnimating;
    private static IEnumerable<TextBlock> Glyphs(MusicLyricWordView word) =>
        Visuals(word).OfType<TextBlock>().Where(text => text.Visibility == Visibility.Visible && text.Text.Length > 0);

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static Task Drain() => MusicTestUi.Drain();
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual)
    { Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; got {actual}"); }
    private static void Near(double expected, double actual, double tolerance, string message)
    { Check(Math.Abs(expected - actual) <= tolerance, $"{message}: expected {expected}; got {actual}"); }
}
