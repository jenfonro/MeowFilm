using NAudio.CoreAudioApi;
using NAudio.Effects;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeowFilm.Media;

// A bounded producer keeps network/decoder work off the WASAPI render thread.
// The clock ledger excludes underrun silence from the logical song position.
internal sealed class AudioOutputSession : IAsyncDisposable
{
    private readonly AudioDecoder decoder;
    private readonly AudioPcmBuffer buffer;
    private readonly CancellationTokenSource production = new();
    private readonly TaskCompletionSource producerDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread producerThread;
    private WasapiPlayer? player;
    private MMDevice? device;
    private double observedDevicePosition;
    private bool disposed;
    private Task? renderRetirement;
    private TaskCompletionSource? rendered;
    internal EffectChain Effects { get; }
    internal event Action<Exception?>? Stopped;

    internal AudioOutputSession(AudioDecoder decoder)
    {
        this.decoder = decoder;
        buffer = new(decoder.SampleRate, decoder.Channels, decoder.CheckAuthorization);
        buffer.Rendered += () => Volatile.Read(ref rendered)?.TrySetResult();
        Effects = new(buffer);
        // Dedicated AboveNormal background thread isolates PCM decoding from ThreadPool bursts
        // during window drag, UI layout, or concurrent artwork decoding (prevents audio stutter).
        producerThread = new Thread(Produce)
        {
            Name = "MeowFilm.AudioDecode",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        producerThread.Start();
    }

    private void Produce()
    {
        var samples = new float[4096];
        try
        {
            while (!production.IsCancellationRequested)
            {
                int count = decoder.Read(samples, 0, samples.Length, production.Token);
                if (count == 0) { buffer.Complete(null); return; }
                buffer.Write(samples.AsSpan(0, count), production.Token);
            }
        }
        catch (OperationCanceledException) when (production.IsCancellationRequested) { }
        catch (Exception error) { buffer.Complete(error); }
        finally { producerDone.TrySetResult(); }
    }

    internal async Task PrepareAsync(CancellationToken token)
    {
        await buffer.Ready.Task.WaitAsync(token);
        token.ThrowIfCancellationRequested();
        try
        {
            using var devices = new MMDeviceEnumerator();
            device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            player = new WasapiPlayerBuilder().WithDevice(device).WithLatency(100)
                .WithCategory(AudioStreamCategory.Media).WithMmcssThreadPriority("Audio").Build();
            player.Init(new SampleToWaveProvider(Effects));
            device.AudioSessionManager.AudioSessionControl.DisplayName = "meowfilm";
            if (Environment.ProcessPath is { } path)
                device.AudioSessionManager.AudioSessionControl.IconPath = path;
            player.PlaybackStopped += PlaybackStopped;
        }
        catch
        {
            throw new PlaybackException(PlaybackError.NativeUnavailable);
        }
    }

    private void PlaybackStopped(object? sender, StoppedEventArgs args) => ReportStopped(args.Exception);
    private void ReportStopped(Exception? error)
    {
        if (error is not null) Volatile.Read(ref rendered)?.TrySetException(error);
        Stopped?.Invoke(error);
    }
    internal double DevicePositionSeconds
    {
        get
        {
            var active = player;
            if (active is null) return observedDevicePosition;
            double clock = active.GetPosition() / (double)active.OutputWaveFormat.AverageBytesPerSecond;
            // WasapiPlayer reports zero after its native thread has stopped.
            // Never rewind a failed occurrence while recovery captures position.
            return observedDevicePosition = Math.Max(observedDevicePosition, clock);
        }
    }
    internal double MediaPositionSeconds => buffer.MediaSecondsAt(DevicePositionSeconds);
    internal bool IsDrained => buffer.IsDrained;

    internal int Volume
    {
        set { if (player is not null) player.Volume = Math.Clamp(value, 0, 100) / 100f; }
    }

    internal async Task PlayAsync(CancellationToken token)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref rendered, started);
        if (player is not null) player.Play();
        else throw new PlaybackException(PlaybackError.NativeUnavailable);
        await started.Task.WaitAsync(token);
    }

    internal void Pause() => player?.Pause();

    internal Task StopRenderingAsync() => renderRetirement ??= StopRenderingCoreAsync();
    private async Task StopRenderingCoreAsync()
    {
        Volatile.Read(ref rendered)?.TrySetCanceled();
        var active = player;
        player = null;
        if (active is not null)
        {
            active.PlaybackStopped -= PlaybackStopped;
            await active.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        production.Cancel();
        buffer.Wake();
        try { await StopRenderingAsync(); }
        finally
        {
            try { await producerDone.Task; }
            finally
            {
                Effects.Reset();
                device?.Dispose();
                production.Dispose();
            }
        }
    }
}

internal sealed class AudioPcmBuffer(int sampleRate, int channels, Action? validateRead = null) : ISampleProvider
{
    private readonly object gate = new();
    private readonly float[] samples = new float[sampleRate * channels * 2];
    private readonly Queue<(long Start, long Count)> silence = new();
    private int head, count;
    private long submittedFrames, retiredSilence;
    private bool complete;
    private Exception? failure;
    internal event Action? Rendered;
    internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    internal bool IsDrained { get { lock (gate) return complete && count == 0; } }

    internal void Write(ReadOnlySpan<float> data, CancellationToken token)
    {
        lock (gate)
        {
            while (!data.IsEmpty)
            {
                token.ThrowIfCancellationRequested();
                while (count == samples.Length)
                {
                    Monitor.Wait(gate);
                    token.ThrowIfCancellationRequested();
                }
                int tail = (head + count) % samples.Length;
                int size = Math.Min(data.Length, Math.Min(samples.Length - count, samples.Length - tail));
                data[..size].CopyTo(samples.AsSpan(tail));
                data = data[size..];
                count += size;
                if (count >= sampleRate * channels / 10) Ready.TrySetResult();
            }
        }
    }

    internal void Wake() { lock (gate) Monitor.PulseAll(gate); }
    internal void Complete(Exception? error)
    {
        lock (gate)
        {
            complete = true;
            failure = error;
            if (error is not null) Ready.TrySetException(error);
            else Ready.TrySetResult();
            Monitor.PulseAll(gate);
        }
    }

    public int Read(Span<float> destination)
    {
        validateRead?.Invoke();
        lock (gate)
        {
            if (failure is not null) throw failure;
            int copied = 0, wanted = destination.Length / channels * channels;
            while (copied < wanted && count > 0)
            {
                int size = Math.Min(wanted - copied, Math.Min(count, samples.Length - head));
                samples.AsSpan(head, size).CopyTo(destination[copied..]);
                head = (head + size) % samples.Length;
                copied += size;
                count -= size;
            }
            int returned = complete ? copied : wanted;
            if (returned > copied)
            {
                destination[copied..returned].Clear();
                silence.Enqueue((submittedFrames + copied / channels, (returned - copied) / channels));
            }
            submittedFrames += returned / channels;
            validateRead?.Invoke();
            Rendered?.Invoke();
            Monitor.PulseAll(gate);
            return returned;
        }
    }

    internal double MediaSecondsAt(double deviceSeconds)
    {
        lock (gate)
        {
            long frame = Math.Clamp((long)(Math.Max(0, deviceSeconds) * sampleRate), 0, submittedFrames);
            while (silence.TryPeek(out var interval) && interval.Start + interval.Count <= frame)
            {
                retiredSilence += interval.Count;
                silence.Dequeue();
            }
            long omitted = retiredSilence;
            if (silence.TryPeek(out var partial) && frame > partial.Start) omitted += frame - partial.Start;
            return Math.Max(0, frame - omitted) / (double)sampleRate;
        }
    }
}
