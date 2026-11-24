using System.Runtime.InteropServices;
using MeowFilm.Core.Debug;
using NAudio.CoreAudioApi;
using NAudio.Effects;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeowFilm.Media;

// Producer decodes and runs DSP on an AboveNormal thread; WASAPI initializes and renders in MTA.
internal sealed class AudioOutputSession : IAsyncDisposable
{
    private readonly AudioDecoder decoder;
    private readonly AudioPcmBuffer buffer;
    private readonly DecoderSource source;
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
    internal double MediaPositionSeconds => buffer.MediaSecondsAt(DevicePositionSeconds);
    internal bool IsDrained => buffer.IsDrained;
    internal event Action<Exception?>? Stopped;
    internal event Action<string?>? DeviceInvalidated;
    internal event Action<bool>? BufferingChanged;

    internal AudioOutputSession(AudioDecoder decoder)
    {
        this.decoder = decoder;
        buffer = new(decoder.SampleRate, decoder.Channels, decoder.CheckAuthorization);
        buffer.BufferingChanged += state => BufferingChanged?.Invoke(state);
        source = new(decoder, production.Token);
        Effects = new(source);
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
                source.EndOfStream = false;
                int count = Effects.Read(samples);
                if (count > 0) buffer.Write(samples.AsSpan(0, count), production.Token);
                if (count == 0 || source.EndOfStream) { buffer.Complete(null); return; }
            }
        }
        catch (OperationCanceledException) when (production.IsCancellationRequested) { }
        catch (Exception error) { buffer.Complete(error); }
        finally { producerDone.TrySetResult(); }
    }

    internal async Task PrepareAsync(CancellationToken token)
    {
        await buffer.Ready.Task.WaitAsync(token).ConfigureAwait(false);
        // Always initialize MMDevice and WasapiPlayer on a ThreadPool MTA worker so COM interfaces
        // never bind to the WinUI STA apartment or marshal across the UI message pump during scroll.
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var devices = new MMDeviceEnumerator();
                device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                ActiveDeviceId = device.ID;
                player = new WasapiPlayerBuilder().WithDevice(device).WithLatency(160)
                    .WithCategory(AudioStreamCategory.Media).WithMmcssThreadPriority("Audio").Build();
                player.Init(new SampleToWaveProvider(buffer));
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
        }, token).ConfigureAwait(false);
    }

    private void PlaybackStopped(object? sender, StoppedEventArgs args)
    {
        buffer.CompleteRenderWaiter(args.Exception);
        if (AudioEndpointMonitor.IsDeviceInvalidation(args.Exception)) { NotifyDeviceInvalidated(); return; }
        Stopped?.Invoke(args.Exception);
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
            if (player is not { } active) return observedDevicePosition;
            try
            {
                double clock = active.GetPosition() / (double)active.OutputWaveFormat.AverageBytesPerSecond;
                return observedDevicePosition = Math.Max(observedDevicePosition, clock);
            }
            catch (Exception ex) when (ex is COMException or ObjectDisposedException)
            {
                if (AudioEndpointMonitor.IsDeviceInvalidation(ex)) NotifyDeviceInvalidated();
                return observedDevicePosition;
            }
        }
    }

    internal int Volume
    {
        set
        {
            if (player is not { } active) return;
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
        try { active.Play(); }
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
        try { await started.Task.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { buffer.CompleteRenderWaiter(canceled: true); throw; }
        catch (COMException ex) when (AudioEndpointMonitor.IsDeviceInvalidation(ex))
        {
            NotifyDeviceInvalidated();
            throw new PlaybackException(PlaybackError.AudioDeviceUnavailable);
        }
    }

    internal void Pause()
    {
        if (player is not { } active) return;
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
        if (Interlocked.Exchange(ref player, null) is { } active)
        {
            active.PlaybackStopped -= PlaybackStopped;
            try { await active.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is COMException or ObjectDisposedException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        production.Cancel();
        buffer.Wake();
        try { await StopRenderingAsync().ConfigureAwait(false); }
        finally
        {
            try { await producerDone.Task.ConfigureAwait(false); }
            finally { Effects.Reset(); device?.Dispose(); device = null; production.Dispose(); }
        }
    }

    private sealed class DecoderSource(AudioDecoder decoder, CancellationToken token) : ISampleProvider
    {
        private readonly float[] scratch = new float[4096];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(decoder.SampleRate, decoder.Channels);
        internal bool EndOfStream;
        public int Read(Span<float> destination)
        {
            int copied = 0;
            while (copied < destination.Length)
            {
                int read = decoder.Read(scratch, 0, Math.Min(destination.Length - copied, scratch.Length), token);
                if (read == 0) { EndOfStream = true; break; }
                scratch.AsSpan(0, read).CopyTo(destination[copied..]);
                copied += read;
            }
            return copied;
        }
    }
}

internal sealed class AudioPcmBuffer(int sampleRate, int channels, Action? validateRead = null) : ISampleProvider
{
    private readonly object gate = new();
    private readonly float[] samples = new float[sampleRate * channels * 2];
    private readonly int refillWatermark = sampleRate * channels * 3 / 2;
    private readonly List<(long Start, long Count)> silence = [];
    private int silenceHead, head, count;
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
        if (Interlocked.Exchange(ref renderWaiter, null) is not { } waiter) return;
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
                    try { Monitor.Wait(gate); } finally { producerWaiting = false; }
                    token.ThrowIfCancellationRequested();
                }
                int tail = (head + count) % samples.Length;
                int size = Math.Min(data.Length, Math.Min(samples.Length - count, samples.Length - tail));
                data[..size].CopyTo(samples.AsSpan(tail));
                data = data[size..];
                count += size;
                if (count >= sampleRate * channels / 10)
                {
                    if (!readySignaled) { readySignaled = true; Ready.TrySetResult(); }
                    if (buffering) { buffering = false; bufferingUpdate = false; }
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
            complete = true; failure = error;
            if (buffering) { buffering = false; clearedBuffering = true; }
            if (error is not null) Ready.TrySetException(error); else Ready.TrySetResult();
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
                long silenceStart = submittedFrames + copied / channels, silenceFrames = (returned - copied) / channels;
                if (silence.Count > silenceHead && silence[^1].Start + silence[^1].Count == silenceStart)
                    silence[^1] = (silence[^1].Start, silence[^1].Count + silenceFrames);
                else silence.Add((silenceStart, silenceFrames));
                underrunCount++;
                if (!buffering) { buffering = true; bufferingUpdate = true; }
            }
            else if (buffering && copied == wanted) { buffering = false; bufferingUpdate = false; }
            submittedFrames += returned / channels;
            if (producerWaiting && count <= refillWatermark) Monitor.Pulse(gate);
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
                retiredSilence += silence[silenceHead++].Count;
            if (silenceHead >= 32 && silenceHead * 2 >= silence.Count) { silence.RemoveRange(0, silenceHead); silenceHead = 0; }
            long omitted = retiredSilence;
            if (silenceHead < silence.Count && frame > silence[silenceHead].Start)
                omitted += frame - silence[silenceHead].Start;
            return Math.Max(0, frame - omitted) / (double)sampleRate;
        }
    }
}
