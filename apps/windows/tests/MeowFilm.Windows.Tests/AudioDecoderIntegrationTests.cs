using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FFmpeg.AutoGen;
using MeowFilm.Media;
using static MeowFilm.Windows.Tests.AudioCoreFixtures;

namespace MeowFilm.Windows.Tests;

// Real native decoding, synthetic media and loopback only; no output device.
public static class AudioDecoderIntegrationTests
{
    private const string LogCase = "audio.integration.native-logs-and-errors-do-not-leak";
    private const string LogProbe = "MEOWFILM_AUDIO_LOG_PROBE";
    private const string Diagnostic = "http://127.0.0.1/synthetic-url-canary?fixture=only " +
        "C:\\synthetic-path-canary\\audio.flac Authorization: synthetic-header-canary\n";
    private static readonly IReadOnlyDictionary<string, string> Headers = new Dictionary<string, string>
    {
        ["Authorization"] = "Fixture synthetic-audio-only",
        ["Referer"] = "http://127.0.0.1/synthetic-referrer",
        ["X-Audio-Fixture"] = "synthetic-only"
    };

    public static IEnumerable<TestCase> Cases()
    {
        yield return new("audio.integration.http-headers-range-seek-real-pcm", HttpSeek);
        yield return new("audio.integration.http-seek-cancel-same-position-recovers-read", HttpSeekCancellationRecovery);
        yield return new("audio.integration.http-cancelled-read-avio-buffered-seek-recovers-pcm", HttpBufferedSeekRecovery);
        yield return new("audio.integration.http-nonrange-flac-plays-without-initial-seek", NonRange);
        yield return new("audio.integration.http-opening-cancel-waiting-headers", () => CancelOpening(true));
        yield return new("audio.integration.http-opening-cancel-waiting-body", () => CancelOpening(false));
        yield return new("audio.integration.http-active-cancel-disconnects-range-body", CancelReading);
        yield return new("audio.integration.http-invalid-content-range-rejected", () => InvalidHttp(false));
        yield return new("audio.integration.http-truncation-is-not-clean-eof", () => InvalidHttp(true));
        yield return new("audio.integration.flac-real-pcm-duration-seek-and-release", DecodeFlac);
        yield return new("audio.integration.flac-crc-error-is-not-clean-eof", CorruptFlac);
        yield return new(LogCase, NativeLogs);
    }

    private static async Task HttpSeek()
    {
        byte[] wave = WaveBytes(6, markers: true);
        await using var server = new Loopback((request, stream, token) =>
            Reply(stream, wave, request.Offset, true, token));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var decoder = await AudioDecoder.OpenAsync(server.Uri, Headers, deadline.Token);
        Near(decoder.DurationSeconds, 6, .01, "HTTP duration");
        CheckPcm(Drain(decoder, 6), wave);
        int requests = server.Requests.Count;
        decoder.Seek(3.25);
        float[] tail = Drain(decoder, 3);
        CheckPcm(tail, wave, 156000);
        Near(Frequency(tail), 990, 2, "seek discards earlier PCM");
        var afterSeek = server.Requests.ToArray().Skip(requests).ToArray();
        Check(afterSeek.Any(request => request.Offset > 0), "seek caused a real nonzero Range request");
        Check(afterSeek.All(request => request.Headers.GetValueOrDefault("If-Range") == "\"synthetic-v1\""),
            "seek keeps the HTTP representation validator");
    }

    private static async Task NonRange()
    {
        byte[] wave = WaveBytes(3), flac = FlacBytes(wave);
        await using var server = new Loopback((_, stream, token) => Reply(stream, flac, 0, false, token));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var decoder = await AudioDecoder.OpenAsync(server.Uri, Headers, deadline.Token);
        Near(decoder.DurationSeconds, 3, .01, "non-Range FLAC duration");
        CheckPcm(Drain(decoder, 3), wave);
        ErrorSync(() => decoder.Seek(1), PlaybackError.NotSeekable);
        Check(server.Requests.All(request => request.Offset == 0), "non-Range input never reopens at an offset");
    }

    private static async Task HttpSeekCancellationRecovery()
    {
        byte[] wave = WaveBytes(1);
        const int offset = 44 + 12000 * Channels * 2;
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        await using var server = new Loopback(async (request, stream, token) =>
        {
            if (Interlocked.Increment(ref requests) != 2)
            {
                await Reply(stream, wave, request.Offset, true, token);
                return;
            }
            blocked.TrySetResult();
            await Disconnected(stream, token);
            disconnected.TrySetResult();
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var input = new AudioDecoderInput(server.Uri, Headers, deadline.Token);
        input.Open(server.Uri);
        Check(input.CanSeek && input.Length == wave.Length, "initial HTTP response is open and seekable");
        using var cancel = new CancellationTokenSource();
        input.OperationToken = cancel.Token;
        Task seeking = Task.Run(() => input.Seek(offset, 0));
        try
        {
            await UntilBlocked(blocked.Task, seeking);
            Check(!seeking.IsCompleted, "seek waits for response headers before cancellation");
            cancel.Cancel();
            await Cancelled(() => seeking.WaitAsync(TimeSpan.FromSeconds(3)));
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
            input.OperationToken = default;
            Check(!input.Interrupted && input.LastErrorCode is null, "seek cancellation preserves the input lifetime");
            Check(server.Requests.Count == 2, "only the initial and cancelled requests ran");
            Check(input.Seek(offset, 0) == offset, "same-position seek recovers after cancellation");
            var exchanges = server.Requests.ToArray();
            Check(exchanges.Length == 3 && exchanges[1].Offset == offset && exchanges[2].Offset == offset,
                "same-position recovery opens a new Range request");
            Check(exchanges.Skip(1).All(request => request.Headers.GetValueOrDefault("If-Range") == "\"synthetic-v1\""),
                "cancelled and recovered seeks keep the representation validator");
            var bytes = new byte[4096];
            IntPtr buffer = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                int position = offset, count;
                while ((count = input.Read(buffer, bytes.Length)) != 0)
                {
                    Check(count > 0 && count <= bytes.Length && position + count <= wave.Length, "bounded recovered read");
                    Marshal.Copy(buffer, bytes, 0, count);
                    Check(bytes.AsSpan(0, count).SequenceEqual(wave.AsSpan(position, count)),
                        "recovered bytes match the requested source position");
                    position += count;
                }
                Check(position == wave.Length, "recovered response reads through clean EOF");
            }
            finally { Marshal.FreeHGlobal(buffer); }
            Check(!input.Interrupted && input.LastErrorCode is null, "recovery never marks the input failed");
        }
        finally
        {
            cancel.Cancel();
            input.Cancel();
            try { await seeking.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task HttpBufferedSeekRecovery()
    {
        byte[] wave = WaveBytes(10);
        const int prefix = 44 + 6 * SampleRate * Channels * 2;
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        await using var server = new Loopback(async (request, stream, token) =>
        {
            if (Interlocked.Increment(ref requests) != 1)
            {
                await Reply(stream, wave, request.Offset, true, token);
                return;
            }
            await ReplyHeader(stream, wave.Length, request.Offset, true, token);
            await stream.WriteAsync(wave.AsMemory(request.Offset, prefix - request.Offset), token);
            sent.TrySetResult();
            await Disconnected(stream, token);
            disconnected.TrySetResult();
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var decoder = await AudioDecoder.OpenAsync(server.Uri, Headers, deadline.Token);
        using var cancel = new CancellationTokenSource();
        Task reading = Task.Run(() =>
        {
            var pcm = new float[4096];
            while (decoder.Read(pcm, 0, pcm.Length, cancel.Token) > 0) { }
        });
        try
        {
            await sent.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Until(() => HttpReadBlocked(decoder, prefix), "real AVIO read awaits the stalled HTTP body");
            Check(!reading.IsCompleted, "cancel targets the actual blocking read");
            cancel.Cancel();
            await Cancelled(() => reading.WaitAsync(TimeSpan.FromSeconds(3)));
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(decoder.LastErrorCode is null && server.Requests.Count == 1,
                "single-read cancellation closes only the response, not the input lifetime");
            int frame = BufferedSeekFrame(decoder, prefix);
            double seconds = frame / (double)SampleRate;
            int firstFrame = (int)Math.Ceiling(seconds * SampleRate);
            decoder.Seek(seconds);
            Check(server.Requests.Count == 1, "actual FFmpeg seek stays inside AVIO's buffer without an HTTP seek callback");
            CheckPcm(Drain(decoder, 10), wave, firstFrame);
            var exchanges = server.Requests.ToArray();
            Check(exchanges.Length == 2 && exchanges[1].Offset == prefix &&
                exchanges[1].Headers.GetValueOrDefault("If-Range") == "\"synthetic-v1\"" &&
                decoder.LastErrorCode is null,
                "after buffered PCM, read resumes at the exact delivered byte boundary with its validator through EOF");
        }
        finally
        {
            cancel.Cancel();
            decoder.Cancel();
            try { await reading.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
        }
    }

    private static bool HttpReadBlocked(AudioDecoder decoder, long position)
    {
        var input = Field(decoder, "_input")!;
        var transport = Field(input, "_http")!;
        var readers = (System.Collections.IDictionary)Field(transport, "_readers")!;
        object reader = readers.Values.Cast<object>().Single();
        if ((long)Field(reader, "_position")! != position) return false;
        object gate = Field(reader, "_io")!;
        if (!Monitor.TryEnter(gate)) return true;
        Monitor.Exit(gate);
        return false;
    }

    private static unsafe int BufferedSeekFrame(AudioDecoder decoder, long position)
    {
        var io = (AVIOContext*)Pointer.Unbox(Field(decoder, "_avio")!);
        long retained = io->buf_end - io->buffer;
        Check(io->pos == position && retained > 8, "native AVIO retains real bytes before the cancelled read");
        long frame = (io->pos - retained / 2 - 44) / (Channels * 2);
        Check(44 + frame * Channels * 2 >= io->pos - retained &&
            44 + frame * Channels * 2 < io->pos, "seek target is measured inside the actual retained buffer");
        return checked((int)frame);
    }

    private static async Task CancelOpening(bool beforeHeaders)
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new Loopback(async (_, stream, token) =>
        {
            if (!beforeHeaders) await ReplyHeader(stream, 576044, 0, true, token);
            blocked.TrySetResult();
            await Disconnected(stream, token);
            disconnected.TrySetResult();
        });
        using var cancel = new CancellationTokenSource();
        Task<AudioDecoder> opening = AudioDecoder.OpenAsync(server.Uri, Headers, cancel.Token);
        try
        {
            await UntilBlocked(blocked.Task, opening);
            cancel.Cancel();
            await Cancelled(async () => { using var decoder = await opening.WaitAsync(TimeSpan.FromSeconds(3)); });
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            cancel.Cancel();
            await server.DisposeAsync();
            try { (await opening.WaitAsync(TimeSpan.FromSeconds(3))).Dispose(); }
            catch (OperationCanceledException) { }
            catch (PlaybackException) { }
        }
    }

    private static async Task CancelReading()
    {
        byte[] wave = WaveBytes(3);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int stall = 0;
        await using var server = new Loopback(async (request, stream, token) =>
        {
            if (Volatile.Read(ref stall) == 0) { await Reply(stream, wave, request.Offset, true, token); return; }
            await ReplyHeader(stream, wave.Length, request.Offset, true, token);
            blocked.TrySetResult();
            await Disconnected(stream, token);
            disconnected.TrySetResult();
        });
        using var decoder = await AudioDecoder.OpenAsync(server.Uri, Headers, default);
        CheckPcm(Drain(decoder, 3), wave);
        Volatile.Write(ref stall, 1);
        Task reading = Task.Run(() => { decoder.Seek(1.5); _ = Drain(decoder, 2); });
        try
        {
            await UntilBlocked(blocked.Task, reading);
            await Task.Run(decoder.Cancel).WaitAsync(TimeSpan.FromSeconds(1));
            await Cancelled(() => reading.WaitAsync(TimeSpan.FromSeconds(3)));
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            try { await server.DisposeAsync(); } // Unblock failed cancellation before joining.
            finally
            {
                decoder.Cancel();
                try { await reading.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (OperationCanceledException) { }
                catch (PlaybackException) { }
            }
        }
    }

    private static async Task InvalidHttp(bool truncated)
    {
        byte[] wave = WaveBytes(6);
        await using var server = new Loopback(async (request, stream, token) =>
        {
            await ReplyHeader(stream, wave.Length, request.Offset, true, token, invalidRange: !truncated);
            if (truncated) await stream.WriteAsync(wave.AsMemory(request.Offset, 1000), token);
        });
        await Error(async () =>
        {
            using var decoder = await AudioDecoder.OpenAsync(server.Uri, Headers, default);
            _ = Drain(decoder, 6);
        }, truncated ? PlaybackError.HttpTruncated : PlaybackError.HttpInvalidRange);
        Check(!server.Requests.IsEmpty, "rejection exercised an actual HTTP request");
    }

    private static async Task DecodeFlac()
    {
        byte[] wave = WaveBytes(3), flac = FlacBytes(wave);
        Check(flac.Length < wave.Length * .75, "fixture uses compressed residuals, not verbatim PCM");
        var lease = new Lease(flac);
        using (var decoder = await AudioDecoder.OpenReadAsync(lease, default))
        {
            Near(decoder.DurationSeconds, 3, .01, "FLAC STREAMINFO duration");
            CheckPcm(Drain(decoder, 3), wave);
            decoder.Seek(1.25);
            CheckPcm(Drain(decoder, 2), wave, 60000);
            decoder.Seek(0);
            CheckPcm(Drain(decoder, 3), wave);
        }
        lease.AssertReleased();
    }

    private static async Task CorruptFlac()
    {
        byte[] flac = FlacBytes(WaveBytes(3));
        flac[^1] ^= 1; // Last frame's CRC16, after many valid compressed frames.
        var lease = new Lease(flac);
        using (var decoder = await AudioDecoder.OpenReadAsync(lease, default))
        {
            var first = new float[4096];
            Check(decoder.Read(first, 0, first.Length) == first.Length, "valid compressed PCM precedes the damaged frame");
            ErrorSync(() => Drain(decoder, 3), PlaybackError.ReadFailed);
            Check(decoder.LastErrorCode == PlaybackError.ReadFailed, "recognized native CRC error remains a fixed code");
        }
        lease.AssertReleased();
    }

    private static void CheckPcm(float[] pcm, byte[] wave, int firstFrame = 0)
    {
        int offset = 44 + firstFrame * Channels * 2;
        Check(pcm.Length == (wave.Length - offset) / 2, "exact decoded sample count including tail");
        for (int i = 0; i < pcm.Length; i++)
            Check(pcm[i] == BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(offset + i * 2)) / 32768f,
                "lossless interleaved PCM equals the synthetic source");
    }

    private static async Task NativeLogs()
    {
        string? mode = Environment.GetEnvironmentVariable(LogProbe);
        if (mode is "control" or "suppressed")
        {
            if (mode == "suppressed")
            {
                await CorruptFlac();
                await InvalidHttp(false);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            EmitNativeDiagnostic(mode == "control");
            return;
        }
        var control = await RunLogProbe("control");
        Check(control.Contains("synthetic-url-canary") && control.Contains("synthetic-path-canary") &&
            control.Contains("synthetic-header-canary"), "positive control captures real native stderr");
        Check((await RunLogProbe("suppressed")).Length == 0, "no native stderr after rejected input or decoder disposal");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetLogCallback(IntPtr callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeLog(IntPtr context, int level, [MarshalAs(UnmanagedType.LPUTF8Str)] string message);

    private static void EmitNativeDiagnostic(bool control)
    {
        AudioNativeLibrary.EnsureLoaded();
        // The verified avutil mapping is already loaded. This temporary reference
        // lets a safe test assembly invoke its logging ABI without unsafe compilation.
        IntPtr library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "audio-kernel", "win-x64", "avutil-61.dll"));
        try
        {
            if (control)
                Marshal.GetDelegateForFunctionPointer<SetLogCallback>(NativeLibrary.GetExport(library, "av_log_set_callback"))(
                    NativeLibrary.GetExport(library, "av_log_default_callback"));
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_TRACE); // A callback must suppress logs even if the level changes.
            Marshal.GetDelegateForFunctionPointer<NativeLog>(NativeLibrary.GetExport(library, "av_log"))(
                IntPtr.Zero, ffmpeg.AV_LOG_ERROR, Diagnostic);
        }
        finally { NativeLibrary.Free(library); }
    }

    private static async Task<string> RunLogProbe(string mode)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--case");
        start.ArgumentList.Add(LogCase);
        start.Environment[LogProbe] = mode;
        using var process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Check(process.ExitCode == 0 && (await output).Contains("PASS " + LogCase, StringComparison.Ordinal),
                "isolated native logging probe completed successfully");
            return await error;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    private sealed record Request(int Offset, Dictionary<string, string> Headers);

    private sealed class Loopback : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Func<Request, NetworkStream, CancellationToken, Task> handler;
        private readonly ConcurrentBag<Task> connections = [];
        private readonly ConcurrentQueue<Exception> failures = [];
        private readonly Task accepting;
        private int disposed;
        internal ConcurrentQueue<Request> Requests { get; } = [];
        internal Uri Uri { get; }

        internal Loopback(Func<Request, NetworkStream, CancellationToken, Task> handler)
        {
            this.handler = handler;
            listener.Start();
            Uri = new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/synthetic-audio?fixture=only");
            accepting = Accept();
        }

        private async Task Accept()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                    connections.Add(Serve(await listener.AcceptTcpClientAsync(stop.Token)));
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
        }

        private async Task Serve(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            using (var close = stop.Token.Register(client.Dispose))
            {
                try
                {
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    Check(await reader.ReadLineAsync(stop.Token) == "GET /synthetic-audio?fixture=only HTTP/1.1",
                        "controlled GET target");
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 } line)
                    {
                        int colon = line.IndexOf(':');
                        Check(colon > 0, "valid HTTP header");
                        headers.Add(line[..colon], line[(colon + 1)..].Trim());
                    }
                    Check(Headers.All(pair => headers.GetValueOrDefault(pair.Key) == pair.Value),
                        "every request carries the supplied synthetic headers");
                    Check(headers.GetValueOrDefault("Accept-Encoding") == "identity", "controlled representation encoding");
                    string range = headers.GetValueOrDefault("Range", "");
                    Check(range.StartsWith("bytes=", StringComparison.Ordinal) && range.EndsWith('-'), "explicit open-ended Range");
                    var request = new Request(int.Parse(range[6..^1], CultureInfo.InvariantCulture), headers);
                    Requests.Enqueue(request);
                    await handler(request, stream, stop.Token);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
                catch (IOException) { /* HTTP seek/cancel deliberately closes the current body. */ }
                catch (Exception error) { failures.Enqueue(error); }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            stop.Cancel();
            listener.Stop();
            await accepting.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.WhenAll(connections).WaitAsync(TimeSpan.FromSeconds(3));
            stop.Dispose();
            if (failures.TryDequeue(out var error)) throw error;
        }
    }

    private static async Task Reply(NetworkStream stream, byte[] bytes, int offset, bool ranges, CancellationToken token)
    {
        await ReplyHeader(stream, bytes.Length, offset, ranges, token);
        await stream.WriteAsync(bytes.AsMemory(offset), token);
    }

    private static Task ReplyHeader(NetworkStream stream, int length, int offset, bool ranges, CancellationToken token,
        bool invalidRange = false)
    {
        string range = ranges ? $"Content-Range: bytes {offset + (invalidRange ? 1 : 0)}-{length - 1}/{length}\r\n" : "";
        string header = $"HTTP/1.1 {(ranges ? 206 : 200)} Fixture\r\nContent-Type: application/octet-stream\r\n" +
            $"Content-Length: {length - offset}\r\nETag: \"synthetic-v1\"\r\n{range}Connection: close\r\n\r\n";
        return stream.WriteAsync(Encoding.ASCII.GetBytes(header), token).AsTask();
    }

    private static async Task Disconnected(NetworkStream stream, CancellationToken token)
    {
        try { Check(await stream.ReadAsync(new byte[1], token) == 0, "client closed the blocked HTTP exchange"); }
        catch (IOException) { /* TCP reset also confirms real client-side disconnection. */ }
    }

    // Original minimal RFC 9639 fixture encoder: fixed-order-2 prediction,
    // one Rice partition (k=6), independent stereo, CRC8/CRC16 and PCM MD5.
    private static byte[] FlacBytes(byte[] wave)
    {
        const int block = 4800;
        int frames = (wave.Length - 44) / (Channels * 2);
        using var output = new MemoryStream();
        output.Write("fLaC"u8);
        output.Write([0x80, 0, 0, 34]);
        var info = new byte[34];
        BinaryPrimitives.WriteUInt16BigEndian(info, block);
        BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(2), block);
        BinaryPrimitives.WriteUInt64BigEndian(info.AsSpan(10),
            (ulong)SampleRate << 44 | (ulong)(Channels - 1) << 41 | 15UL << 36 | (uint)frames);
        MD5.HashData(wave.AsSpan(44)).CopyTo(info, 18);
        output.Write(info);
        for (int first = 0; first < frames; first += block)
        {
            byte[] header = [0xff, 0xf8, 0x70, 0x18, (byte)(first / block), (block - 1) >> 8, (block - 1) & 255];
            var bits = new Bits();
            foreach (byte value in header) bits.Write(value, 8);
            bits.Write(Crc(header, 8, 0x07), 8);
            for (int channel = 0; channel < Channels; channel++)
            {
                int Sample(int i) => BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(44 + ((first + i) * Channels + channel) * 2));
                bits.Write(0x14, 8); // Fixed predictor order 2, no wasted bits.
                bits.Write((uint)Sample(0), 16);
                bits.Write((uint)Sample(1), 16);
                bits.Write(0, 6); // Rice method 0, partition order 0.
                bits.Write(6, 4);
                for (int i = 2; i < block; i++)
                {
                    int residual = Sample(i) - 2 * Sample(i - 1) + Sample(i - 2);
                    uint folded = (uint)(residual < 0 ? -2 * residual - 1 : 2 * residual);
                    for (uint zeros = folded >> 6; zeros > 0; zeros--) bits.Write(0, 1);
                    bits.Write(1, 1);
                    bits.Write(folded, 6);
                }
            }
            byte[] frame = bits.Finish();
            output.Write(frame);
            ushort crc = Crc(frame, 16, 0x8005);
            output.WriteByte((byte)(crc >> 8));
            output.WriteByte((byte)crc);
        }
        return output.ToArray();
    }

    private static ushort Crc(ReadOnlySpan<byte> bytes, int width, int polynomial)
    {
        int crc = 0, mask = (1 << width) - 1;
        foreach (byte value in bytes)
        {
            crc ^= value << (width - 8);
            for (int bit = 0; bit < 8; bit++)
                crc = ((crc << 1) ^ ((crc & (1 << (width - 1))) != 0 ? polynomial : 0)) & mask;
        }
        return (ushort)crc;
    }

    private sealed class Bits
    {
        private readonly List<byte> bytes = [];
        private int value, used;
        internal void Write(uint number, int count)
        {
            for (int bit = count - 1; bit >= 0; bit--)
            {
                value = value << 1 | (int)(number >> bit & 1);
                if (++used != 8) continue;
                bytes.Add((byte)value);
                value = used = 0;
            }
        }
        internal byte[] Finish() { if (used > 0) Write(0, 8 - used); return bytes.ToArray(); }
    }
}
