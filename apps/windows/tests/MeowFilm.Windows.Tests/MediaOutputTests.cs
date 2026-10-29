using System.Text;
using System.IO;
using System.Windows.Threading;
using MeowFilm.Media;

namespace MeowFilm.Windows.Tests;

public static class MediaOutputTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("media.output.real-generated-y4m-display-counter-once-and-clear", () => Sta(Video));
        yield return new("media.output.audio-playing-never-fakes-video-output", () => Sta(Audio));
    }
    private static string Root()
    {
        string root = TestWorkspace.DataPath("meowfilm-video-observation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }
    private static async Task Video()
    {
        string root = Root(), file = Path.Combine(root, "generated.y4m");
        WriteVideo(file);
        try
        {
            await using var playback = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            var observed = new TaskCompletionSource<VideoOutputObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
            int events = 0;
            playback.VideoOutputObserved += (_, value) => { events++; observed.TrySetResult(value); };
            Check(playback.ObservedVideoOutput is null);
            await playback.OpenAsync("film", "synthetic-y4m", new Uri(file), "合成视频");
            var value = await observed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Check(value.OwnerId == "film" && value.Identity == "synthetic-y4m" && value.Generation > 0);
            Check(value.DisplayedPictures > 0 && value.Evidence == "libvlc-displayed-pictures");
            Check(playback.ObservedVideoOutput == value);
            await Task.Delay(500); Check(events == 1);
            await playback.StopAsync(); Check(playback.ObservedVideoOutput is null);
            await Task.Delay(300); Check(events == 1);
            Console.WriteLine("OBSERVED generated Y4M -> libVLC vout display counter; hidden owned HWND, not physical-display/HDR/hardware evidence");
        }
        finally { File.Delete(file); Directory.Delete(root); }
    }
    private static async Task Audio()
    {
        string root = Root(), file = Path.Combine(root, "generated.wav");
        using (var writer = new BinaryWriter(File.Create(file)))
        {
            const int samples = 24000, bytes = samples * 2;
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + bytes);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000);
            writer.Write((short)2); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(bytes);
            for (int i = 0; i < samples; i++) writer.Write((short)0);
        }
        try
        {
            await using var playback = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            int events = 0;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            playback.Started += (_, _) => started.TrySetResult();
            playback.VideoOutputObserved += (_, _) => events++;
            await playback.OpenAsync("music", "synthetic-wave", new Uri(file), "合成音频");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(8)); await Task.Delay(500);
            Check(events == 0 && playback.ObservedVideoOutput is null);
            await playback.StopAsync();
        }
        finally { File.Delete(file); Directory.Delete(root); }
    }
    private static void WriteVideo(string path)
    {
        using var stream = File.Create(path);
        stream.Write(Encoding.ASCII.GetBytes("YUV4MPEG2 W160 H90 F25:1 Ip A1:1 C420jpeg\n"));
        var frame = new byte[160 * 90 * 3 / 2];
        frame.AsSpan(160 * 90).Fill(128);
        for (int index = 0; index < 150; index++)
        {
            frame.AsSpan(0, 160 * 90).Fill((byte)(32 + index % 160));
            stream.Write("FRAME\n"u8); stream.Write(frame);
        }
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Video observation assertion"); }
    private static async Task Sta(Func<Task> body)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.InvokeAsync(async () =>
            {
                try { await body(); completed.TrySetResult(); }
                catch (Exception error) { completed.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(35));
        Check(thread.Join(3000));
    }
}
