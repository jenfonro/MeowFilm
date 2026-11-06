using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MeowFilm.Media;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static class MusicPlaybackRateTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.PlaybackRateCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> PlaybackRateCases()
    {
        yield return Case("native music rate reaches the actual shared libVLC input and survives a new local track", async () =>
        {
            await using var native = new NativePlayback(nativeLibraryPath: Environment.GetEnvironmentVariable("MEOWFILM_TEST_VLC_ROOT"),
                audioOutput: NativeAudioOutput.Dummy);
            await using var f = await Fixture.Create(native: native);
            await f.PlayableCollection();
            foreach (string path in System.IO.Directory.GetFiles(f.Root, "*.wav")) WriteRateWave(path);
            await f.Module.PlaySelectionAsync();
            await Until(() => native.IsPlaying);
            string? old = native.Identity;
            await f.Module.ChangePlaybackRateAsync(1.7);
            True(Math.Abs((await native.ReadTracksAsync())!.Rate - 1.7) < 0.001, "native SetRate/readback");
            True(Math.Abs(f.Module.AcceptedPlaybackRate!.Value - 1.7) < 0.001, "actual wrapper receipt");
            await f.Module.StepAsync(true);
            await Until(() => native.IsPlaying);
            True(native.Identity != old, "next occurrence");
            True(Math.Abs((await native.ReadTracksAsync())!.Rate - 1.7) < 0.001, "same shared engine restores rate");
            True(!f.Module.EffectivePlaybackRateVerified, "dummy output is not measured system audio");
            Equal(0, f.Runtime.Calls.Count);
        });
        yield return Case("native music rate is optional and never claims an unimplemented engine command", async () =>
        {
            await using var f = await Fixture.Create();
            await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            True(!f.Module.CanChangePlaybackRate, "unsupported port disabled");
            await f.Module.ChangePlaybackRateAsync(1.5);
            Equal(1d, f.Module.PlaybackRate); Equal(null, f.Module.AcceptedPlaybackRate);
            True(!f.Module.EffectivePlaybackRateVerified, "no effective-output claim");
        });
        yield return Case("native music rate changes use exact input scope and survive next previous and pause", async () =>
        {
            var output = new RateProbe();
            await using var f = await Fixture.Create(output: output);
            await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            True(f.Module.CanChangePlaybackRate, "current supported input");
            await f.Module.ChangePlaybackRateAsync(1.7);
            Equal(1.7, f.Module.PlaybackRate); Equal(1.7, f.Module.AcceptedPlaybackRate);
            output.Start(); await f.Module.TogglePlaybackAsync();
            Equal(1.7, f.Module.PlaybackRate);
            await f.Module.StepAsync(true);
            Equal(1.7, output.Rate); Equal(1.7, f.Module.AcceptedPlaybackRate);
            await f.Module.StepAsync(false);
            Equal(1.7, output.Rate); Equal(1.7, f.Module.PlaybackRate);
            True(output.Writes.All(write => write.Scope.OwnerId == "music" && write.Scope.Generation > 0),
                "only exact native scopes");
            Equal(0, f.Runtime.Calls.Count);
        });
        yield return Case("native music rate bounds and unsupported acknowledgments retain prior intent", async () =>
        {
            var output = new RateProbe();
            await using var f = await Fixture.Create(output: output);
            await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, 0.4, 3.1, 1.25 })
            {
                int before = output.Writes.Count;
                await f.Module.ChangePlaybackRateAsync(invalid);
                Equal(before, output.Writes.Count); Equal(1d, f.Module.PlaybackRate);
                True(f.Module.Status.Length > 0, "invalid rate error");
            }
            await f.Module.ChangePlaybackRateAsync(0.5); Equal(0.5, output.Rate);
            await f.Module.ChangePlaybackRateAsync(3); Equal(3d, output.Rate);
            output.Reject = true;
            await f.Module.ChangePlaybackRateAsync(1.3);
            Equal(3d, f.Module.PlaybackRate); True(f.Module.Status.Length > 0, "native refusal visible");
        });
        yield return Case("native music rate mismatched receipt never advances the displayed rate", async () =>
        {
            var output = new RateProbe();
            await using var f = await Fixture.Create(output: output);
            await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            output.WrongRate = true;
            await f.Module.ChangePlaybackRateAsync(1.4);
            Equal(1d, f.Module.PlaybackRate);
            True(f.Module.Status.Length > 0, "engine readback is not the requested value");
            output.WrongRate = false; output.WrongScope = true;
            await f.Module.ChangePlaybackRateAsync(1.6);
            Equal(1d, f.Module.PlaybackRate);
        });
        yield return Case("native music rate old song read cannot change a replacement domain input", async () =>
        {
            var output = new RateProbe();
            await using var f = await Fixture.Create(output: output);
            await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            output.HoldRead = true;
            int before = output.Writes.Count;
            var change = f.Module.ChangePlaybackRateAsync(1.8);
            await output.Entered.Task;
            await output.OpenAsync("film", "new-film", new Uri("https://example.invalid/video"), "Film", default);
            output.Release.TrySetResult();
            await change;
            Equal(before, output.Writes.Count); Equal(1d, f.Module.PlaybackRate);
            True(!f.Module.CanChangePlaybackRate, "not the owning domain");
            Equal(null, f.Module.AcceptedPlaybackRate);
        });
        yield return Case("native music rate shutdown waits for physical command and ignores its late success", async () =>
        {
            var output = new RateProbe();
            await using var f = await Fixture.Create(output: output);
            await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            output.HoldWrite = true;
            var change = f.Module.ChangePlaybackRateAsync(1.8);
            await output.Entered.Task;
            var prepare = f.Module.PrepareShutdownAsync();
            True(!prepare.IsCompleted && !f.Module.CanChangePlaybackRate, "sealed before physical completion");
            output.Release.TrySetResult();
            await Task.WhenAll(change, prepare);
            Equal(1d, f.Module.PlaybackRate);
            int opens = output.Opens.Count;
            await f.Module.CancelShutdownPreparationAsync();
            await Drain(); await Until(() => !f.Module.Busy);
            Equal(opens, output.Opens.Count); Equal(1d, output.Rate);
            True(output.HasMedia && f.Module.CanChangePlaybackRate,
                "surviving product-owned local input reconciles intent without reopening media");
            Render(f.Module.PlayerView, 860, 105);
        });
        yield return Case("native music rate failure on next song keeps the song and publishes an error", async () =>
        {
            var output = new RateProbe();
            await using var f = await Fixture.Create(output: output);
            await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            await f.Module.ChangePlaybackRateAsync(2.1);
            var old = output.Identity;
            output.Reject = true;
            await f.Module.StepAsync(true);
            True(output.Identity != old && output.HasMedia, "next song not silently discarded");
            Equal(2.1, f.Module.PlaybackRate);
            Equal(null, f.Module.AcceptedPlaybackRate);
            True(f.Module.Status.Length > 0, "unsupported rate not silent");
        });
        yield return Case("native music rate actual WPF popup exposes tenths and reset without explanatory text", async () =>
        {
            var output = new RateProbe();
            await using var f = await Fixture.Create(output: output);
            await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var player = (MusicPlayerView)f.Module.PlayerView;
            var button = (Button)player.FindName("MoreButton");
            Equal("更多", AutomationProperties.GetName(button));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var rate = (MenuItem)button.ContextMenu.Items[0];
            True(rate.Header.ToString()!.StartsWith("播放倍速"), "rate is a more-menu action");
            var slider = (Slider)player.FindName("RateSlider");
            Equal(0.5, slider.Minimum); Equal(3d, slider.Maximum); Equal(0.1, slider.TickFrequency);
            True(slider.IsSnapToTickEnabled, "keyboard/mouse tenths");
            await f.Module.ChangePlaybackRateAsync(1.3); await Drain();
            True(button.ToolTip is null, "the footer has no explanatory hover hint");
            Equal(1.3, slider.Value);
            True(!f.Module.EffectivePlaybackRateVerified, "requested != measured");
        });
    }

    private static void WriteRateWave(string path)
    {
        const int sampleRate = 22050, frames = sampleRate * 12, length = frames * 2;
        using var writer = new System.IO.BinaryWriter(System.IO.File.Create(path));
        void Four(string text) => writer.Write(System.Text.Encoding.ASCII.GetBytes(text));
        Four("RIFF"); writer.Write(36 + length); Four("WAVE"); Four("fmt "); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate); writer.Write(sampleRate * 2);
        writer.Write((short)2); writer.Write((short)16); Four("data"); writer.Write(length); writer.Write(new byte[length]);
    }

    private sealed class RateProbe : PlaybackProbe, IMusicPlaybackRate
    {
        private int seen;
        internal double Rate = 1;
        internal bool Reject, WrongRate, WrongScope, HoldRead, HoldWrite;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<(NativePlaybackScope Scope, double Rate)> Writes = [];
        private NativePlaybackScope CurrentScope => new(OwnerId!, Identity!, Opens.Count);
        public async Task<MusicPlaybackRate?> ReadRateAsync(CancellationToken token = default)
        {
            var scope = CurrentScope;
            if (seen != Opens.Count) { seen = Opens.Count; Rate = 1; }
            if (HoldRead) { HoldRead = false; Entered.TrySetResult(); await Release.Task; }
            token.ThrowIfCancellationRequested();
            return HasMedia ? new(scope, Rate) : null;
        }
        public async Task<MusicPlaybackRate> SetRateAsync(NativePlaybackScope scope, double rate, CancellationToken token = default)
        {
            if (HoldWrite) { HoldWrite = false; Entered.TrySetResult(); await Release.Task; }
            token.ThrowIfCancellationRequested();
            if (scope != CurrentScope) throw new MusicOperationException(PlaybackError.Superseded);
            if (Reject) throw new MusicOperationException(PlaybackError.RateUnsupported);
            Rate = WrongRate ? 1 : rate; Writes.Add((scope, rate));
            return new(WrongScope ? scope with { Generation = scope.Generation + 1 } : scope, Rate);
        }
    }
}
