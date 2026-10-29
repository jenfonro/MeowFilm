using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace MeowFilm.Media;

// The graph owns keys; native URLs, rebuilt playlists and archive metadata never do.
internal sealed class AdaptiveKey(AdaptiveResource resource)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private byte[]? bytes;
    private int references = 1;
    internal string Identity => (resource.OriginalCredentials ? "1:" : "0:") + resource.OriginalUri.AbsoluteUri;
    internal AdaptiveKey Retain() { Interlocked.Increment(ref references); return this; }
    internal void SetIndependent() => resource.Independent = true;
    internal async Task<byte[]> GetAsync(AdaptiveHttpTransport transport, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (bytes is not null) return bytes;
            using var response = await transport.OpenAsync(resource, 0, null, token).ConfigureAwait(false);
            if (response.Length != 16) throw new PlaybackException(PlaybackError.HttpFormatUnsupported);
            var value = new byte[16];
            try
            {
                int at = 0;
                while (at < value.Length) at += await response.ReadAsync(value.AsMemory(at)).ConfigureAwait(false);
                await response.VerifyEndAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                bytes = value;
                return value;
            }
            catch { CryptographicOperations.ZeroMemory(value); throw; }
        }
        finally { gate.Release(); }
    }
    internal void Clear()
    {
        if (Interlocked.Decrement(ref references) != 0) return;
        if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        bytes = null; gate.Dispose(); resource.Release();
    }
}

internal sealed record AdaptiveEncryption(AdaptiveKey Key, byte[] IV);

internal sealed partial class AdaptiveHttpTransport
{
    internal readonly record struct PayloadInfo(long Length, long Total, string? ETag, string? EncryptionBinding = null);

    // A 16/32-byte tail probe establishes the exact plaintext length without buffering a segment.
    // Strong ETag + correct Range is mandatory for this encrypted profile, including online playback.
    internal async Task<PayloadInfo> ProbePayloadAsync(AdaptiveResource resource, CancellationToken token)
    {
        if (resource.Encryption is not { } encryption)
        {
            using var plain = await OpenAsync(resource, 0, null, token).ConfigureAwait(false);
            return new(plain.Length, plain.Total, plain.ETag);
        }
        byte[] key = await encryption.Key.GetAsync(this, token).ConfigureAwait(false);
        long cipherLength;
        string tag;
        using (var headers = await OpenAsync(resource, 0, null, token).ConfigureAwait(false))
        {
            cipherLength = headers.Length;
            tag = headers.ETag ?? throw new PlaybackException(PlaybackError.HttpRepresentationChanged);
            if (cipherLength < 16 || cipherLength % 16 != 0) throw Format();
        }
        byte[] tail = new byte[(int)Math.Min(cipherLength, 32)];
        byte[] last = new byte[16];
        byte[] binding = new byte[32];
        try
        {
            using var response = await OpenAsync(resource, cipherLength - tail.Length, cipherLength - 1, token).ConfigureAwait(false);
            int at = 0;
            while (at < tail.Length) at += await response.ReadAsync(tail.AsMemory(at)).ConfigureAwait(false);
            await response.VerifyEndAsync().ConfigureAwait(false);
            using var aes = Aes.Create();
            aes.Key = key;
            aes.DecryptCbc(tail.AsSpan(tail.Length - 16), tail.Length == 32 ? tail.AsSpan(0, 16) : encryption.IV,
                last, PaddingMode.None);
            int padding = last[15];
            if (padding is < 1 or > 16) throw Format();
            for (int i = 16 - padding; i < 16; i++) if (last[i] != padding) throw Format();
            long length = cipherLength - padding;
            long maximum = resource.Kind == AdaptiveResourceKind.Mp4Init ? AdaptiveManifest.MaximumBytes : 64L * 1024 * 1024;
            if (length <= 0 || length > maximum) throw Format();
            key.CopyTo(binding, 0); encryption.IV.CopyTo(binding, 16);
            // Only a one-way binding survives in the artifact validator. No key, IV or key URL is serialized.
            return new(length, cipherLength, tag, Convert.ToHexStringLower(SHA256.HashData(binding)));
        }
        catch (CryptographicException) { throw Format(); }
        finally
        {
            CryptographicOperations.ZeroMemory(tail); CryptographicOperations.ZeroMemory(last);
            CryptographicOperations.ZeroMemory(binding);
        }
    }

    internal async Task<Response> OpenPayloadAsync(AdaptiveResource resource, long start, long? end, CancellationToken token)
    {
        if (resource.Encryption is not { } encryption)
            return await OpenAsync(resource, start, end, token).ConfigureAwait(false);
        var info = await ProbePayloadAsync(resource, token).ConfigureAwait(false);
        if (start < 0 || start >= info.Length || end < start || end >= info.Length)
            throw new PlaybackException(PlaybackError.HttpInvalidRange);
        Response? raw = null;
        DecryptedStream? stream = null;
        try
        {
            raw = await OpenAsync(resource, 0, null, token).ConfigureAwait(false);
            if (raw.Length != info.Total || raw.ETag != info.ETag)
                throw new PlaybackException(PlaybackError.HttpRepresentationChanged);
            stream = new(raw, await encryption.Key.GetAsync(this, token).ConfigureAwait(false), encryption.IV, info.Length);
            raw = null; // stream owns the response, its gates and cancellation.
            await stream.SkipAsync(start, token).ConfigureAwait(false);
            long count = (end ?? info.Length - 1) - start + 1;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Headers.ETag = EntityTagHeaderValue.Parse(info.ETag!);
            var result = new Response(response, stream, count, info.Length, start, _options.ReadTimeout, _lifetime, token, null);
            stream = null; return result;
        }
        finally { stream?.Dispose(); raw?.Dispose(); }
    }
    private static PlaybackException Format() => new(PlaybackError.HttpFormatUnsupported);

    private sealed class DecryptedStream : Stream
    {
        private readonly Response raw;
        private readonly Aes aes;
        private readonly ICryptoTransform transform;
        private readonly CryptoStream crypto;
        private readonly long length;
        private readonly object ownership = new();
        private long position;
        private int disposed, reading;
        private bool cryptoReleased;
        internal DecryptedStream(Response raw, byte[] key, byte[] iv, long length)
        {
            this.raw = raw; this.length = length;
            aes = Aes.Create(); aes.Key = key; aes.IV = iv; aes.Padding = PaddingMode.PKCS7; aes.Mode = CipherMode.CBC;
            transform = aes.CreateDecryptor();
            crypto = new(new CipherStream(raw), transform, CryptoStreamMode.Read);
        }
        internal async Task SkipAsync(long count, CancellationToken token)
        {
            if (count == 0) return;
            var buffer = new byte[65536];
            try
            {
                while (position < count)
                    if (await ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count - position)), token).ConfigureAwait(false) == 0)
                        throw Format();
            }
            finally { CryptographicOperations.ZeroMemory(buffer); }
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            lock (ownership)
            {
                if (disposed != 0) throw new ObjectDisposedException(nameof(DecryptedStream));
                if (buffer.IsEmpty || position == length) return 0;
                reading++;
            }
            using var abort = token.Register(raw.Dispose);
            try
            {
                token.ThrowIfCancellationRequested();
                int n = await crypto.ReadAsync(buffer[..(int)Math.Min(Math.Min(buffer.Length, 65536), length - position)], token).ConfigureAwait(false);
                if (n == 0) throw Format();
                position += n;
                if (position == length)
                {
                    // Force PKCS#7 finalization even when the caller asks for exactly the plaintext length.
                    var extra = new byte[1];
                    try
                    {
                        if (await crypto.ReadAsync(extra, token).ConfigureAwait(false) != 0) throw Format();
                        await raw.VerifyEndAsync().ConfigureAwait(false);
                    }
                    finally { CryptographicOperations.ZeroMemory(extra); }
                }
                token.ThrowIfCancellationRequested(); return n;
            }
            catch (CryptographicException) { throw Format(); }
            finally
            {
                lock (ownership)
                {
                    reading--;
                    if (disposed != 0 && reading == 0) ReleaseCryptography();
                }
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
            {
                // Cancellation aborts IO immediately but must not dispose an AES transform
                // concurrently with its admitted read. The owning handler/reader awaits that read.
                try { raw.Dispose(); }
                finally { lock (ownership) if (reading == 0) ReleaseCryptography(); }
            }
            base.Dispose(disposing);
        }
        private void ReleaseCryptography()
        {
            if (cryptoReleased) return;
            cryptoReleased = true;
            try { crypto.Dispose(); }
            finally { try { transform.Dispose(); } finally { aes.Dispose(); } }
        }
        public override bool CanRead => disposed == 0;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class CipherStream(Response response) : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            using var abort = token.Register(response.Dispose);
            token.ThrowIfCancellationRequested();
            return await response.ReadAsync(buffer).ConfigureAwait(false);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => response.Length;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        // CryptoStream finalizes a partially-read stream on Dispose. A read-only HTTP
        // adapter has no writable buffers; flush is a no-op, not a cancellation failure.
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
