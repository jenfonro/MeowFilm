using System.Runtime.InteropServices;
using MeowFilm.Core.Debug;
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
    private int deviceInvalidatedSignaled;
    private bool disposed;
    private Task? renderRetirement;
    internal EffectChain Effects { get; }
    internal string? ActiveDeviceId { get; private set; }
    internal double BufferedSeconds => buffer.BufferedSeconds;
    internal bool IsBuffering => buffer.IsBuffering;
    internal long UnderrunCount => buffer.UnderrunCount;
    internal event Action<Exception?>? Stopped;
    internal event Action<string?>? DeviceInvalidated;
    internal event Action<bool>? BufferingChanged;

    internal AudioOutputSession(AudioDecoder decoder)
    {
        this.decoder = decoder;
        buffer = new(decoder.SampleRate, decoder.Channels, decoder.CheckAuthorization);
        buffer.BufferingChanged += state => BufferingChanged?.Invoke(state);
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
            ActiveDeviceId = device.ID;
            player = new WasapiPlayerBuilder().WithDevice(device).WithLatency(100)
                .WithCategory(AudioStreamCategory.Media).WithMmcssThreadPriority("Audio").Build();
            player.Init(new SampleToWaveProvider(Effects));
            try
            {
                device.AudioSessionManager.AudioSessionControl.DisplayName = "meowfilm";
                if (Environment.ProcessPath is { } path)
                    device.AudioSessionManager.AudioSessionControl.IconPath = path;
            }
            catch (Exception ex)
            {
                if (DebugLog.IsEnabled)
                    DebugLog.Warn(DebugCategories.MusicPlayback, "audiokernel", "session_label.failed",
                        new { deviceId = ActiveDeviceId }, error: ex);
            }
            player.PlaybackStopped += PlaybackStopped;
        }
        catch (Exception ex)
        {
            if (AudioEndpointMonitor.IsDeviceInvalidation(ex))
                throw new PlaybackException(PlaybackError.AudioDeviceUnavailable);
            throw new PlaybackException(PlaybackError.NativeUnavailable);
        }
    }

    private void PlaybackStopped(object? sender, StoppedEventArgs args) => ReportStopped(args.Exception);

    private void ReportStopped(Exception? error)
    {
        buffer.CompleteRenderWaiter(error);
        if (AudioEndpointMonitor.IsDeviceInvalidation(error))
        {
            NotifyDeviceInvalidated();
            return;
        }
        Stopped?.Invoke(error);
    }

    private void NotifyDeviceInvalidated()
    {
        if (Interlocked.Exchange(ref deviceInvalidatedSignaled, 1) == 0)
            DeviceInvalidated?.Invoke(ActiveDeviceId);
    }

    internal double DevicePositionSeconds
    {
        get
        {
            var active = player;
            if (active is null) return observedDevicePosition;
            try
            {
                double clock = active.GetPosition() / (double)active.OutputWaveFormat.AverageBytesPerSecond;
                // WasapiPlayer reports zero after its native thread has stopped.
                // Never rewind a failed occurrence while recovery captures position.
                return observedDevicePosition = Math.Max(observedDevicePosition, clock);
            }
            catch (Exception ex) when (ex is COMException or ObjectDisposedException)
            {
                if (AudioEndpointMonitor.IsDeviceInvalidation(ex))
                    NotifyDeviceInvalidated();
                return observedDevicePosition;
            }
        }
    }

    internal double MediaPositionSeconds => buffer.MediaSecondsAt(DevicePositionSeconds);
    internal bool IsDrained => buffer.IsDrained;

    internal int Volume
    {
        set
        {
            var active = player;
            if (active is null) return;
            try { active.Volume = Math.Clamp(value, 0, 100) / 100f; }
            catch (Exception ex) when (ex is COMException or ObjectDisposedException)
            {
                if (AudioEndpointMonitor.IsDeviceInvalidation(ex)) NotifyDeviceInvalidated();
            }
        }
    }

    internal async Task PlayAsync(CancellationToken token)
    {
        var active = player ?? throw new PlaybackException(PlaybackError.NativeUnavailable);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        buffer.ArmRenderWaiter(started);
        try
        {
            active.Play();
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException)
        {
            buffer.CompleteRenderWaiter(canceled: true);
            if (AudioEndpointMonitor.IsDeviceInvalidation(ex))
            {
                NotifyDeviceInvalidated();
                throw new PlaybackException(PlaybackError.AudioDeviceUnavailable);
            }
            throw new PlaybackException(PlaybackError.PlaybackFailed);
        }
        try
        {
            await started.Task.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            buffer.CompleteRenderWaiter(canceled: true);
            throw;
        }
        catch (COMException ex) when (AudioEndpointMonitor.IsDeviceInvalidation(ex))
        {
            NotifyDeviceInvalidated();
            throw new PlaybackException(PlaybackError.AudioDeviceUnavailable);
        }
    }

    internal void Pause()
    {
        var active = player;
        if (active is null) return;
        try { active.Pause(); }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException)
        {
            if (AudioEndpointMonitor.IsDeviceInvalidation(ex)) NotifyDeviceInvalidated();
        }
    }

    internal Task StopRenderingAsync() => renderRetirement ??= StopRenderingCoreAsync();

    private async Task StopRenderingCoreAsync()
    {
        buffer.CompleteRenderWaiter(canceled: true);
        var active = player;
        player = null;
        if (active is not null)
        {
            active.PlaybackStopped -= PlaybackStopped;
            try { await active.DisposeAsync(); }
            catch (Exception ex) when (ex is COMException or ObjectDisposedException) { }
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
                device = null;
                production.Dispose();
            }
        }
    }
}

internal sealed class AudioPcmBuffer(int sampleRate, int channels, Action? validateRead = null) : ISampleProvider
{
    private readonly object gate = new();
    private readonly float[] samples = new float[sampleRate * channels * 2];
    private readonly int refillWatermark = sampleRate * channels * 3 / 2;
    private readonly List<(long Start, long Count)> silence = [];
    private int silenceHead;
    private int head, count;
    private long submittedFrames, retiredSilence, underrunCount;
    private bool complete, producerWaiting, buffering, readySignaled;
    private Exception? failure;
    private TaskCompletionSource? renderWaiter;
    internal event Action<bool>? BufferingChanged;
    internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    internal bool IsDrained { get { lock (gate) return complete && count == 0; } }
    internal double BufferedSeconds { get { lock (gate) return count / (double)(sampleRate * channels); } }
    internal bool IsBuffering { get { lock (gate) return buffering; } }
    internal long UnderrunCount { get { lock (gate) return underrunCount; } }

    internal void ArmRenderWaiter(TaskCompletionSource waiter) => Volatile.Write(ref renderWaiter, waiter);

    internal void CompleteRenderWaiter(Exception? error = null, bool canceled = false)
    {
        var waiter = Interlocked.Exchange(ref renderWaiter, null);
        if (waiter is null) return;
        if (canceled) waiter.TrySetCanceled();
        else if (error is not null) waiter.TrySetException(error);
        else waiter.TrySetResult();
    }

    internal void Write(ReadOnlySpan<float> data, CancellationToken token)
    {
        bool? bufferingUpdate = null;
        lock (gate)
        {
            while (!data.IsEmpty)
            {
                token.ThrowIfCancellationRequested();
                while (count == samples.Length)
                {
                    producerWaiting = true;
                    try { Monitor.Wait(gate); }
                    finally { producerWaiting = false; }
                    token.ThrowIfCancellationRequested();
                }
                int tail = (head + count) % samples.Length;
                int size = Math.Min(data.Length, Math.Min(samples.Length - count, samples.Length - tail));
                data[..size].CopyTo(samples.AsSpan(tail));
                data = data[size..];
                count += size;
                if (count >= sampleRate * channels / 10)
                {
                    if (!readySignaled)
                    {
                        readySignaled = true;
                        Ready.TrySetResult();
                    }
                    if (buffering)
                    {
                        buffering = false;
                        bufferingUpdate = false;
                    }
                }
            }
        }
        if (bufferingUpdate is { } state) BufferingChanged?.Invoke(state);
    }

    internal void Wake() { lock (gate) Monitor.PulseAll(gate); }

    internal void Complete(Exception? error)
    {
        bool clearedBuffering = false;
        lock (gate)
        {
            complete = true;
            failure = error;
            if (buffering)
            {
                buffering = false;
                clearedBuffering = true;
            }
            if (error is not null) Ready.TrySetException(error);
            else Ready.TrySetResult();
            Monitor.PulseAll(gate);
        }
        if (clearedBuffering) BufferingChanged?.Invoke(false);
    }

    public int Read(Span<float> destination)
    {
        validateRead?.Invoke();
        bool? bufferingUpdate = null;
        int returned;
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
            returned = complete ? copied : wanted;
            if (returned > copied)
            {
                destination[copied..returned].Clear();
                long silenceStart = submittedFrames + copied / channels;
                long silenceFrames = (returned - copied) / channels;
                if (silence.Count > silenceHead &&
                    silence[^1].Start + silence[^1].Count == silenceStart)
                {
                    var last = silence[^1];
                    silence[^1] = (last.Start, last.Count + silenceFrames);
                }
                else
                {
                    silence.Add((silenceStart, silenceFrames));
                }
                underrunCount++;
                if (!buffering)
                {
                    buffering = true;
                    bufferingUpdate = true;
                }
            }
            else if (buffering && copied == wanted)
            {
                buffering = false;
                bufferingUpdate = false;
            }
            submittedFrames += returned / channels;
            if (producerWaiting && count <= refillWatermark)
                Monitor.Pulse(gate);
        }
        if (returned > 0 && Volatile.Read(ref renderWaiter) is not null)
            Interlocked.Exchange(ref renderWaiter, null)?.TrySetResult();
        if (bufferingUpdate is { } state) BufferingChanged?.Invoke(state);
        return returned;
    }

    internal double MediaSecondsAt(double deviceSeconds)
    {
        lock (gate)
        {
            long frame = Math.Clamp((long)(Math.Max(0, deviceSeconds) * sampleRate), 0, submittedFrames);
            while (silenceHead < silence.Count && silence[silenceHead].Start + silence[silenceHead].Count <= frame)
            {
                retiredSilence += silence[silenceHead].Count;
                silenceHead++;
            }
            if (silenceHead >= 32 && silenceHead * 2 >= silence.Count)
            {
                silence.RemoveRange(0, silenceHead);
                silenceHead = 0;
            }
            long omitted = retiredSilence;
            if (silenceHead < silence.Count && frame > silence[silenceHead].Start)
                omitted += frame - silence[silenceHead].Start;
            return Math.Max(0, frame - omitted) / (double)sampleRate;
        }
    }
}
