using System.Globalization;
using System.IO;
using MeowFilm.Media;
using static MeowFilm.Windows.Tests.AudioCoreFixtures;

namespace MeowFilm.Windows.Tests;

// Actual FFmpeg decoding only. These cases never construct an output device.
public static class AudioCoreTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("audio.core.native-wav-resampling-stereo-eof", Decode);
        yield return new("audio.core.native-exact-eof-short-tail-and-duration", ExactEof);
        yield return new("audio.core.pcm-underrun-silence-excluded-from-clock", PcmUnderrunClock);
        yield return new("audio.core.pcm-short-tail-empty-eof-and-frame-boundary", PcmShortTail);
        yield return new("audio.core.native-seek-rewinds-pcm-and-eof", Seek);
        yield return new("audio.core.native-invalid-read-seek-rate", InvalidCommands);
        yield return new("audio.core.native-invalid-and-truncated-file", InvalidFiles);
        yield return new("audio.core.cache-real-tail-duration-rejection-and-release", CacheProbe);
        foreach (int tenth in Enumerable.Range(5, 26))
        {
            double rate = tenth / 10d;
            yield return new("audio.core.native-tempo-" + rate.ToString("0.0", CultureInfo.InvariantCulture) +
                "-sample-count-and-pitch", () => Tempo(rate));
        }
        foreach (string invalid in new[] { "unreadable", "unseekable", "writable", "negative-length",
                     "extent-mismatch", "null-content", "throwing-content" })
            yield return new("audio.core.lease-rejected-" + invalid + "-releases", () => RejectedLease(invalid));
        yield return new("audio.core.lease-real-short-reads-and-eof-release", ShortReads);
        yield return new("audio.core.lease-truncation-is-not-clean-eof", TruncatedLease);
        yield return new("audio.core.lease-precancelled-and-prerevoked-release", PreflightLifetime);
        yield return new("audio.core.lease-revocation-rejects-already-buffered-pcm", RevokeBuffered);
        yield return new("audio.core.lease-opening-cancel-closes-token-ignoring-read", CancelOpening);
        yield return new("audio.core.lease-active-cancel-closes-token-ignoring-read", () => BlockedRead(revoke: false));
        yield return new("audio.core.lease-active-revoke-closes-token-ignoring-read", () => BlockedRead(revoke: true));
    }

    private static async Task Decode()
    {
        using var wave = new Wave(1.5, sampleRate: 32000, channels: 1);
        AudioNativeLibrary.EnsureLoaded();
        AudioNativeLibrary.EnsureLoaded();
        using (var decoder = await AudioDecoder.OpenAsync(wave.Uri, NoHeaders, default))
        {
            Check(decoder.SampleRate == SampleRate && decoder.Channels == Channels, "canonical 48 kHz stereo float");
            Near(decoder.DurationSeconds, 1.5, .01, "duration from actual container");
            Near(decoder.Rate, 1, 0, "initial tempo");
            float[] pcm = Drain(decoder, 1.5);
            Near(pcm.Length / (double)(SampleRate * Channels), 1.5, .025, "resampler and filter tail drained");
            Near(Frequency(pcm), 440, 2, "mono resampling retains pitch");
            Check(pcm.Where((_, index) => index % Channels == 0)
                .Zip(pcm.Where((_, index) => index % Channels == 1))
                .All(pair => Math.Abs(pair.First - pair.Second) < .00001), "mono becomes equal stereo channels");
            var tail = new float[128];
            Check(decoder.Read(tail, 0, tail.Length) == 0 && decoder.Read(tail, 0, tail.Length) == 0 &&
                decoder.LastErrorCode is null, "stable clean EOF, not a decode failure");
        }
        wave.AssertReleased();
    }

    private static async Task Seek()
    {
        using var wave = new Wave(4, markers: true);
        using (var decoder = await AudioDecoder.OpenAsync(wave.Uri, NoHeaders, default))
        {
            Check(decoder.Read(new float[8192], 0, 8192) == 8192, "read ahead before seek");
            decoder.Seek(2.25);
            float[] after = Drain(decoder, 1.75);
            Near(after.Length / (double)(SampleRate * Channels), 1.75, .025, "seek returns the requested media position");
            Near(Frequency(after), 990, 2, "no old pre-seek PCM or filter history");
            decoder.Seek(.25); // Seek after EOF must reopen the actual decoder/filter drain.
            var before = new float[SampleRate / 2 * Channels];
            Check(decoder.Read(before, 0, before.Length) == before.Length, "backward seek after EOF produces PCM");
            Near(Frequency(before), 330, 2, "backward seek reaches the early frequency marker");
            decoder.Seek(3.75);
            Near(Drain(decoder, .25).Length / (double)(SampleRate * Channels), .25, .025, "near-tail seek sample extent");
        }
        wave.AssertReleased();
    }

    private static async Task ExactEof()
    {
        using var wave = new Wave(1);
        using (var decoder = await AudioDecoder.OpenAsync(wave.Uri, NoHeaders, default))
        {
            var pcm = Enumerable.Repeat(1234.5f, SampleRate * Channels + 4).ToArray();
            Near(decoder.DurationSeconds, 1, 1d / SampleRate, "known sample-exact WAV duration");
            Check(decoder.Read(pcm, 2, SampleRate * Channels) == SampleRate * Channels,
                "a read ending exactly at EOF returns all PCM, not a short read");
            Check(pcm[0] == 1234.5f && pcm[1] == 1234.5f && pcm[^1] == 1234.5f && pcm[^2] == 1234.5f,
                "exact EOF read respects the destination bounds");
            Array.Fill(pcm, 1234.5f);
            Check(decoder.Read(pcm, 2, 128) == 0 && decoder.Read(pcm, 2, 0) == 0 &&
                decoder.Read(pcm, 2, 128) == 0 && pcm.All(value => value == 1234.5f),
                "positive and zero-length reads at EOF stay empty and do not change the destination");
            decoder.Seek(.75);
            int tail = SampleRate * Channels / 4;
            Check(decoder.Read(pcm, 2, tail * 2) == tail &&
                pcm.AsSpan(2 + tail).IndexOfAnyExcept(1234.5f) == -1,
                "legal final short read returns only the remaining PCM without padding");
            decoder.Seek(decoder.DurationSeconds);
            Array.Fill(pcm, 1234.5f);
            Check(decoder.Read(pcm, 2, 128) == 0 && pcm.All(value => value == 1234.5f) &&
                decoder.LastErrorCode is null, "seek exactly to duration is clean empty EOF");
            Near(decoder.DurationSeconds, 1, 1d / SampleRate, "EOF and tail seek retain the media duration");
            Near(decoder.Rate, 1, 0, "EOF does not mutate the playback rate");
        }
        wave.AssertReleased();
    }

    private static Task PcmUnderrunClock()
    {
        var buffer = new AudioPcmBuffer(8, Channels);
        buffer.Write([1, -1, 2, -2], default);
        var first = Enumerable.Repeat(1234.5f, 12).ToArray();
        Check(buffer.Read(first) == first.Length && first.AsSpan(0, 4).SequenceEqual([1, -1, 2, -2]) &&
            first.AsSpan(4).IndexOfAnyExcept(0f) == -1, "active underrun supplies actual PCM then silence, not EOF");
        Near(buffer.MediaSecondsAt(.125), .125, 0, "real frame advances media time");
        Near(buffer.MediaSecondsAt(.25), .25, 0, "first silence boundary");
        Near(buffer.MediaSecondsAt(.5), .25, 0, "partially rendered underrun silence is excluded");
        Near(buffer.MediaSecondsAt(.75), .25, 0, "fully rendered silence is retired");
        Near(buffer.MediaSecondsAt(.75), .25, 0, "repeated observation does not subtract silence twice");
        buffer.Write([3, -3, 4, -4], default);
        var next = new float[8];
        Check(buffer.Read(next) == next.Length && next.AsSpan(0, 4).SequenceEqual([3, -3, 4, -4]) &&
            next.AsSpan(4).IndexOfAnyExcept(0f) == -1, "PCM resumes before a separate underrun interval");
        Near(buffer.MediaSecondsAt(.875), .375, 0, "resumed PCM advances after retired silence");
        Near(buffer.MediaSecondsAt(1), .5, 0, "second silence boundary");
        Near(buffer.MediaSecondsAt(1.125), .5, 0, "second partial interval is excluded independently");
        Near(buffer.MediaSecondsAt(1.25), .5, 0, "two complete silence intervals do not advance song time");
        Near(buffer.MediaSecondsAt(100), .5, 0, "clock cannot advance beyond submitted real PCM");
        return Task.CompletedTask;
    }

    private static Task PcmShortTail()
    {
        var buffer = new AudioPcmBuffer(8, Channels);
        buffer.Write([1, -1, 2, -2], default);
        buffer.Complete(null);
        var destination = Enumerable.Repeat(1234.5f, 9).ToArray();
        Check(buffer.Read(destination.AsSpan(0, 3)) == Channels &&
            destination.AsSpan(0, 2).SequenceEqual([1, -1]) &&
            destination.AsSpan(2).IndexOfAnyExcept(1234.5f) == -1 && !buffer.IsDrained,
            "odd-sized destination reads complete stereo frames only and retains the short tail");
        Array.Fill(destination, 1234.5f);
        Check(buffer.Read(destination) == Channels && destination.AsSpan(0, 2).SequenceEqual([2, -2]) &&
            destination.AsSpan(2).IndexOfAnyExcept(1234.5f) == -1 && buffer.IsDrained,
            "completed stream returns a real short read without silence padding or overwriting unused space");
        Array.Fill(destination, 1234.5f);
        Check(buffer.Read(destination) == 0 && buffer.Read(Span<float>.Empty) == 0 &&
            destination.All(value => value == 1234.5f), "drained PCM EOF is stable and never fabricates samples");
        var empty = new AudioPcmBuffer(8, Channels);
        empty.Complete(null);
        Check(empty.Ready.Task.IsCompletedSuccessfully && empty.IsDrained && empty.Read(destination) == 0 &&
            destination.All(value => value == 1234.5f), "empty completed PCM becomes ready EOF without an output device");
        return Task.CompletedTask;
    }

    private static async Task Tempo(double rate)
    {
        using var wave = new Wave(4);
        using (var decoder = await AudioDecoder.OpenAsync(wave.Uri, NoHeaders, default))
        {
            decoder.SetRate(rate);
            decoder.Seek(0); // Whole-file measurement starts at this tempo, not pre-open queued 1x PCM.
            Near(decoder.Rate, rate, .000001, "accepted native tempo");
            float[] pcm = Drain(decoder, 4 / rate);
            double rendered = pcm.Length / (double)(SampleRate * Channels), expected = 4 / rate;
            Near(rendered, expected, Math.Max(.06, expected * .025), "actual output sample count changes with tempo");
            Near(Frequency(pcm), 440, 3, "left pitch independent of playback rate");
            Near(Frequency(pcm, 1), 660, 3, "right pitch independent of playback rate");
            Check(decoder.LastErrorCode is null, "tempo reaches clean EOF");
            Console.WriteLine($"OBSERVED audio tempo={rate:R}; frames={pcm.Length / Channels}; " +
                $"seconds={rendered:R}; left-hz={Frequency(pcm):F3}; right-hz={Frequency(pcm, 1):F3}");
        }
        wave.AssertReleased();
    }

    private static async Task InvalidCommands()
    {
        using var wave = new Wave(1);
        using var decoder = await AudioDecoder.OpenAsync(wave.Uri, NoHeaders, default);
        foreach (double rate in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity, 0, .49, 3.01 })
            ErrorSync(() => decoder.SetRate(rate), PlaybackError.InvalidRate);
        foreach (double position in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity, -1, double.MaxValue })
            ErrorSync(() => decoder.Seek(position), PlaybackError.InvalidSeek);
        var buffer = new float[32];
        ErrorSync(() => decoder.Read(null!, 0, 2), PlaybackError.InvalidRequest);
        ErrorSync(() => decoder.Read(buffer, -1, 2), PlaybackError.InvalidRequest);
        ErrorSync(() => decoder.Read(buffer, 0, 33), PlaybackError.InvalidRequest);
        Check(decoder.Read(buffer, 0, 0) == 0 && decoder.Read(buffer, 0, 32) == 32 &&
            decoder.LastErrorCode is null, "rejected controls do not poison the accepted decoder");
        decoder.Dispose();
        decoder.Dispose();
        ErrorSync(() => decoder.Read(buffer, 0, 32), PlaybackError.Disposed);
        wave.AssertReleased();
    }

    private static async Task InvalidFiles()
    {
        using var invalid = new Wave(1);
        File.WriteAllBytes(invalid.FilePath, "This is not an audio container."u8.ToArray());
        await Error(async () =>
        {
            using var decoder = await AudioDecoder.OpenAsync(invalid.Uri, NoHeaders, default);
            _ = Drain(decoder, 1);
        }, PlaybackError.ReadFormatUnsupported, PlaybackError.ReadFailed, PlaybackError.OpenFailed);
        invalid.AssertReleased();
        using var truncated = new Wave(18);
        File.WriteAllBytes(truncated.FilePath, truncated.Bytes[..(44 + SampleRate * Channels * 2 + 1)]);
        await Error(async () =>
        {
            using var decoder = await AudioDecoder.OpenAsync(truncated.Uri, NoHeaders, default);
            _ = Drain(decoder, 18);
        }, PlaybackError.ReadTruncated, PlaybackError.ReadFailed, PlaybackError.ReadFormatUnsupported);
        truncated.AssertReleased();
    }

    private static async Task CacheProbe()
    {
        using var wave = new Wave(1);
        Near(await NativeAudioCacheProbe.ValidateAsync(wave.FilePath, 1, default), 1, .025, "real native tail validation");
        Near(await NativeAudioCacheProbe.ValidateAsync(wave.FilePath, 0, default), 1, .025, "unknown expected duration");
        Near(await NativeAudioCacheProbe.ValidateAsync(wave.FilePath, 11, default), 1, .025, "existing ten-second tolerance");
        await Error(() => NativeAudioCacheProbe.ValidateAsync(wave.FilePath, 11.01, default), PlaybackError.ReadFailed);
        foreach (double duration in new[] { -1d, double.NaN, double.PositiveInfinity })
            await Error(() => NativeAudioCacheProbe.ValidateAsync(wave.FilePath, duration, default), PlaybackError.InvalidRequest);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Cancelled(() => NativeAudioCacheProbe.ValidateAsync(wave.FilePath, 1, cancelled.Token));
        wave.AssertReleased();
        using var truncated = new Wave(18);
        File.WriteAllBytes(truncated.FilePath, truncated.Bytes[..(44 + SampleRate * Channels * 2 + 1)]);
        await Error(() => NativeAudioCacheProbe.ValidateAsync(truncated.FilePath, 18, default),
            PlaybackError.ReadFailed, PlaybackError.ReadTruncated, PlaybackError.ReadFormatUnsupported);
        truncated.AssertReleased();
        File.WriteAllBytes(truncated.FilePath, "not a cached audio file"u8.ToArray());
        await Error(() => NativeAudioCacheProbe.ValidateAsync(truncated.FilePath, 18, default),
            PlaybackError.ReadFailed, PlaybackError.ReadFormatUnsupported, PlaybackError.OpenFailed);
        truncated.AssertReleased();
    }

    private static async Task RejectedLease(string invalid)
    {
        var lease = new Lease(WaveBytes(1));
        switch (invalid)
        {
            case "unreadable": lease.Stream.Readable = false; break;
            case "unseekable": lease.Stream.Seekable = false; break;
            case "writable": lease.Stream.Writable = true; break;
            case "negative-length": lease.DeclaredLength = -1; break;
            case "extent-mismatch": lease.DeclaredLength--; break;
            case "null-content": lease.NullContent = true; break;
            case "throwing-content": lease.ThrowContent = true; break;
        }
        await Error(async () =>
        {
            using var decoder = await AudioDecoder.OpenReadAsync(lease, default);
        }, PlaybackError.InvalidReadLease);
        lease.AssertReleased();
        Check(lease.Stream.ReadCalls == 0, "invalid read lease rejected before any byte read");
    }

    private static async Task ShortReads()
    {
        var lease = new Lease(WaveBytes(1, sampleRate: 32000, channels: 1));
        lease.Stream.Chunk = 37;
        using (var decoder = await AudioDecoder.OpenReadAsync(lease, default))
        {
            Near(decoder.DurationSeconds, 1, .01, "short-read duration");
            float[] pcm = Drain(decoder, 1);
            Near(pcm.Length / (double)(SampleRate * Channels), 1, .025, "legal short reads decode completely");
            Near(Frequency(pcm), 440, 2, "short-read real PCM pitch");
            Check(lease.Stream.ReadCalls > 100 && lease.DisposeCalls == 0, "lease retained through actual EOF");
        }
        lease.AssertReleased();
    }

    private static async Task TruncatedLease()
    {
        var lease = new Lease(WaveBytes(3));
        lease.Stream.EofAt = 44 + SampleRate * Channels * 2;
        await Error(async () =>
        {
            using var decoder = await AudioDecoder.OpenReadAsync(lease, default);
            _ = Drain(decoder, 3);
        }, PlaybackError.ReadTruncated);
        lease.AssertReleased();
    }

    private static async Task PreflightLifetime()
    {
        var cancelledLease = new Lease(WaveBytes(1));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Cancelled(async () =>
        {
            using var decoder = await AudioDecoder.OpenReadAsync(cancelledLease, cancelled.Token);
        });
        cancelledLease.AssertReleased();
        Check(cancelledLease.Stream.ReadCalls == 0, "pre-cancelled request does not read");
        var revoked = new Lease(WaveBytes(1)); revoked.Revoke();
        await Error(async () =>
        {
            using var decoder = await AudioDecoder.OpenReadAsync(revoked, default);
        }, PlaybackError.ReadRevoked);
        revoked.AssertReleased();
        Check(revoked.Stream.ReadCalls == 0, "pre-revoked authorization does not read");
    }

    private static async Task RevokeBuffered()
    {
        var lease = new Lease(WaveBytes(3));
        using var decoder = await AudioDecoder.OpenReadAsync(lease, default);
        var buffer = Enumerable.Repeat(1234.5f, 1024).ToArray();
        lease.Revoke();
        ErrorSync(() => decoder.Read(buffer, 0, buffer.Length), PlaybackError.ReadRevoked);
        Check(buffer.All(value => value == 1234.5f) && decoder.Revoked.IsCancellationRequested &&
            decoder.LastErrorCode == PlaybackError.ReadRevoked, "no queued native PCM delivered after revocation");
        await lease.Released.Task.WaitAsync(TimeSpan.FromSeconds(3));
        decoder.Dispose();
        lease.AssertReleased();
    }

    private static async Task CancelOpening()
    {
        var lease = new Lease(WaveBytes(20)); lease.Stream.BlockReads();
        using var cancel = new CancellationTokenSource();
        Task<AudioDecoder> opening = AudioDecoder.OpenReadAsync(lease, cancel.Token);
        try
        {
            await UntilBlocked(lease.Stream.Blocked.Task, opening);
            cancel.Cancel();
            await lease.Released.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Cancelled(async () =>
            {
                using var decoder = await opening.WaitAsync(TimeSpan.FromSeconds(3));
            });
            lease.AssertReleased();
        }
        finally
        {
            lease.Stream.UnblockReads();
            cancel.Cancel();
            try { (await opening.WaitAsync(TimeSpan.FromSeconds(3))).Dispose(); }
            catch (OperationCanceledException) { }
            catch (PlaybackException) { }
        }
    }

    private static async Task BlockedRead(bool revoke)
    {
        var lease = new Lease(WaveBytes(20));
        using var decoder = await AudioDecoder.OpenReadAsync(lease, default);
        lease.Stream.BlockReads();
        Task<float[]> reading = Task.Run(() => Drain(decoder, 20));
        try
        {
            await UntilBlocked(lease.Stream.Blocked.Task, reading);
            if (revoke) lease.Revoke();
            else decoder.Cancel();
            await lease.Released.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (revoke)
            {
                await Error(() => reading.WaitAsync(TimeSpan.FromSeconds(3)), PlaybackError.ReadRevoked);
                Check(decoder.Revoked.IsCancellationRequested && decoder.LastErrorCode == PlaybackError.ReadRevoked,
                    "revoked blocked native input reports authorization loss");
            }
            else await Cancelled(() => reading.WaitAsync(TimeSpan.FromSeconds(3)));
            decoder.Dispose();
            lease.AssertReleased();
        }
        finally
        {
            lease.Stream.UnblockReads();
            decoder.Cancel();
            try { await reading.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
            catch (PlaybackException) { }
        }
    }
}
