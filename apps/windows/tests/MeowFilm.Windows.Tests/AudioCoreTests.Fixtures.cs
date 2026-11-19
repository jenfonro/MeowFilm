using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using MeowFilm.Threading;
using Microsoft.UI.Dispatching;
using MeowFilm.Media;

namespace MeowFilm.Windows.Tests;

internal static class AudioCoreFixtures
{
    internal const int SampleRate = 48000, Channels = 2;
    internal static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    internal static object? Field(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(instance);

    internal static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Audio core: " + message);
    }

    internal static void Near(double actual, double expected, double tolerance, string message) =>
        Check(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance,
            $"{message}; expected={expected:R}; actual={actual:R}; tolerance={tolerance:R}");

    internal static async Task Error(Func<Task> action, params string[] codes)
    {
        try { await action(); }
        catch (PlaybackException error)
        {
            Check(codes.Contains(error.Code) && error.Message == error.Code && error.InnerException is null,
                "fixed error code: " + error.Code);
            return;
        }
        throw new InvalidOperationException("Expected audio playback error: " + string.Join(',', codes));
    }

    internal static void ErrorSync(Action action, params string[] codes)
    {
        try { action(); }
        catch (PlaybackException error)
        {
            Check(codes.Contains(error.Code) && error.Message == error.Code && error.InnerException is null,
                "fixed synchronous error code: " + error.Code);
            return;
        }
        throw new InvalidOperationException("Expected synchronous audio error: " + string.Join(',', codes));
    }

    internal static async Task Cancelled(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected audio cancellation");
    }

    internal static async Task Until(Func<bool> condition, string stage, double seconds = 8)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed.TotalSeconds > seconds) throw new TimeoutException("Audio core: " + stage);
            await Task.Delay(15);
        }
    }

    internal static async Task UntilBlocked(Task blocked, Task operation)
    {
        Task completed = await Task.WhenAny(blocked, operation).WaitAsync(TimeSpan.FromSeconds(3));
        if (completed == operation && !blocked.IsCompletedSuccessfully)
        {
            await operation; // Preserve the real open/read failure instead of replacing it with a timeout.
            throw new InvalidOperationException("Audio operation ended before reaching the blocking stream");
        }
        await blocked;
    }

    internal static Task Sta(Func<Task> body) => OwnerTestQueue.RunAsync(body, TimeSpan.FromSeconds(40));

    internal static NativeAudioPlayback Dummy(Func<CancellationToken, Task<IDisposable>>? acquireOutput = null) =>
        new(OwnerDispatcher.Current, acquireOutput, NativeAudioOutput.Dummy);

    internal sealed class Admission : IDisposable
    {
        internal int DisposeCalls;
        public void Dispose() => DisposeCalls++;
    }

    internal static async Task Start(NativeAudioPlayback player, Func<Task> open)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler onStarted = (_, _) => started.TrySetResult();
        EventHandler onFailed = (_, _) => started.TrySetException(
            new InvalidOperationException("Audio fixture failed: " + player.LastErrorCode));
        player.Started += onStarted;
        player.Failed += onFailed;
        try
        {
            await open();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(8));
        }
        finally { player.Started -= onStarted; player.Failed -= onFailed; }
    }

    internal static float[] Drain(AudioDecoder decoder, double maximumSeconds)
    {
        var samples = new List<float>();
        var buffer = new float[4100];
        var watch = Stopwatch.StartNew();
        while (true)
        {
            Array.Fill(buffer, 1234.5f);
            int count = decoder.Read(buffer, 2, 4096);
            Check(count >= 0 && count <= 4096 && count % Channels == 0, "bounded complete stereo frames");
            Check(buffer[0] == 1234.5f && buffer[1] == 1234.5f &&
                buffer.AsSpan(2 + count).IndexOfAnyExcept(1234.5f) == -1, "read respects offset and count");
            if (count == 0) break;
            float[] decoded = buffer.AsSpan(2, count).ToArray();
            Check(decoded.All(float.IsFinite), "finite decoded PCM");
            samples.AddRange(decoded);
            Check(samples.Count <= (maximumSeconds + 1) * SampleRate * Channels &&
                watch.Elapsed < TimeSpan.FromSeconds(10), "bounded actual decoder drain");
        }
        return samples.ToArray();
    }

    internal static double Frequency(float[] samples, int channel = 0)
    {
        int frames = samples.Length / Channels;
        int trim = Math.Min(SampleRate / 4, frames / 10);
        double first = 0, last = 0;
        int crossings = 0;
        double energy = 0;
        for (int frame = trim + 1; frame < frames - trim; frame++)
        {
            float before = samples[(frame - 1) * Channels + channel], after = samples[frame * Channels + channel];
            energy += after * after;
            if (before <= 0 && after > 0)
            {
                double at = frame - 1 - before / (double)(after - before);
                if (crossings++ == 0) first = at;
                last = at;
            }
        }
        Check(crossings > 30 && energy / Math.Max(1, frames - 2 * trim) > .001,
            "frequency measured from non-silent real PCM");
        return (crossings - 1) * SampleRate / (last - first);
    }

    internal static byte[] WaveBytes(double seconds, int sampleRate = SampleRate, int channels = Channels,
        bool silent = false, bool markers = false)
    {
        int frames = checked((int)(sampleRate * seconds)), bytes = checked(frames * channels * 2);
        using var stream = new MemoryStream(bytes + 44);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8); writer.Write(bytes + 36); writer.Write("WAVE"u8);
        writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)channels);
        writer.Write(sampleRate); writer.Write(sampleRate * channels * 2);
        writer.Write((short)(channels * 2)); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(bytes);
        for (int frame = 0; frame < frames; frame++)
            for (int channel = 0; channel < channels; channel++)
            {
                double time = frame / (double)sampleRate;
                double frequency = markers ? time < 1 ? 330 : time < 2 ? 660 : 990 : channel == 0 ? 440 : 660;
                writer.Write(silent ? (short)0 : (short)(8192 * Math.Sin(2 * Math.PI * frequency * time)));
            }
        return stream.ToArray();
    }

    internal sealed class Wave : IDisposable
    {
        private readonly string root = TestWorkspace.DataPath("audio-core-" + Guid.NewGuid().ToString("N"));
        internal string FilePath { get; }
        internal Uri Uri => new(FilePath);
        internal byte[] Bytes { get; }

        internal Wave(double seconds, bool silent = false, int sampleRate = SampleRate,
            int channels = Channels, bool markers = false)
        {
            Bytes = WaveBytes(seconds, sampleRate, channels, silent, markers);
            Directory.CreateDirectory(root);
            FilePath = Path.Combine(root, "generated.wav");
            File.WriteAllBytes(FilePath, Bytes);
        }

        internal bool IsReleased
        {
            get
            {
                try
                {
                    using var exclusive = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    return exclusive.Length > 0;
                }
                catch (IOException) { return false; }
            }
        }

        internal void AssertReleased() => Check(IsReleased, "generated file handle can be acquired exclusively");

        public void Dispose()
        {
            File.Delete(FilePath);
            Directory.Delete(root, recursive: false);
        }
    }

    internal sealed class Lease : INativeMediaReadLease
    {
        private readonly object lifetime = new();
        private readonly CancellationTokenSource revoked = new();
        private readonly CancellationToken token;
        internal readonly ReadStream Stream;
        internal readonly TaskCompletionSource ReleaseEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? DisposeGate;
        internal long DeclaredLength;
        internal bool NullContent, ThrowContent;
        internal int DisposeCalls;
        public Stream Content => ThrowContent ? throw new IOException("synthetic-audio-read-canary")
            : NullContent ? null! : Stream;
        public long Length => DeclaredLength;
        public CancellationToken Revoked => token;

        internal Lease(byte[] bytes)
        {
            Stream = new(bytes);
            DeclaredLength = bytes.Length;
            token = revoked.Token;
        }

        internal void Revoke() { lock (lifetime) revoked.Cancel(); }

        internal void AssertReleased()
        {
            Check(Volatile.Read(ref DisposeCalls) == 1 && Stream.CloseCalls == 1 &&
                Released.Task.IsCompletedSuccessfully, "transferred lease and stream released exactly once");
        }

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCalls);
            ReleaseEntered.TrySetResult();
            if (DisposeGate is not null) await DisposeGate.ConfigureAwait(false);
            Stream.Dispose();
            lock (lifetime) revoked.Dispose();
            Released.TrySetResult();
        }
    }

    internal sealed class ReadStream(byte[] bytes) : Stream
    {
        private long position;
        private volatile bool closed, block;
        private readonly TaskCompletionSource unblock = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Readable = true, Seekable = true, Writable;
        internal bool HonorReadCancellation;
        internal int Chunk = int.MaxValue, ReadCalls, CloseCalls;
        internal long EofAt = long.MaxValue;
        public override bool CanRead => Readable && !closed;
        public override bool CanSeek => Seekable && !closed;
        public override bool CanWrite => Writable;
        public override long Length => bytes.Length;
        public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
        internal void BlockReads() => block = true;
        internal void UnblockReads() { block = false; unblock.TrySetResult(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ReadCalls);
            if (block)
            {
                Blocked.TrySetResult();
                if (HonorReadCancellation) await unblock.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                else await unblock.Task.ConfigureAwait(false); // Lease disposal must also unblock token-ignoring inputs.
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Copy(buffer.Span);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Interlocked.Increment(ref ReadCalls);
            if (block) { Blocked.TrySetResult(); unblock.Task.GetAwaiter().GetResult(); }
            return Copy(buffer.AsSpan(offset, count));
        }

        private int Copy(Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            int count = (int)Math.Max(0, Math.Min(Math.Min(destination.Length, Chunk),
                Math.Min(bytes.Length, EofAt) - position));
            bytes.AsSpan((int)position, count).CopyTo(destination);
            position += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            long target = origin switch
            {
                SeekOrigin.Begin => offset, SeekOrigin.Current => position + offset,
                SeekOrigin.End => bytes.Length + offset, _ => -1
            };
            if (target < 0 || target > bytes.Length) throw new IOException("synthetic seek out of range");
            return position = target;
        }

        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Interlocked.Increment(ref CloseCalls); closed = true; UnblockReads(); }
            base.Dispose(disposing);
        }
    }
}
