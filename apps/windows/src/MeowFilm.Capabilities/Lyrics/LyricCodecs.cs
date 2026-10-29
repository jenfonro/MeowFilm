// SPDX-License-Identifier: GPL-3.0-only
// Native bounded QQ QRC / KuGou KRC decode; fixed source attribution in LICENSES/NOTICE.md.
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Lyrics;

internal static class LyricCodecs
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static string Qrc(string value, CancellationToken token)
    {
        if (value.Length == 0) return "";
        token.ThrowIfCancellationRequested();
        string decoded;
        if (value.TrimStart().StartsWith('<') || L.Timed(value)) decoded = value;
        else
        {
            L.Require(value.Length <= 2 * L.MaxDecodedBytes, "LIMIT");
            L.Require(value.Length % 16 == 0 && L.Match(value, "^[0-9a-fA-F]+$"), "INVALID_RESPONSE");
            var bytes = Convert.FromHexString(value);
            QrcCipher.Decrypt(bytes, token);
            decoded = Inflate(bytes, padding: true, token);
        }
        // Extract text without an XML parser, DTD, entity resolver, DOM or browser.
        if (decoded.TrimStart().StartsWith('<'))
        {
            L.Require(!decoded.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) &&
                !decoded.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase), "INVALID_RESPONSE");
            var fields = L.Regex("\\bLyricContent\\s*=\\s*([\"'])([\\s\\S]*?)\\1").Matches(decoded);
            L.Require(fields.Count == 1, "INVALID_RESPONSE");
            decoded = L.Regex("&(amp|lt|gt|quot|apos|#\\d+|#[xX][\\da-fA-F]+);").Replace(fields[0].Groups[2].Value, m =>
            {
                var name = m.Groups[1].Value;
                if (name[0] != '#') return name switch { "amp" => "&", "lt" => "<", "gt" => ">", "quot" => "\"", _ => "'" };
                var hex = name.StartsWith("#x", StringComparison.OrdinalIgnoreCase);
                return int.TryParse(name[(hex ? 2 : 1)..], hex ? NumberStyles.HexNumber : NumberStyles.None,
                    CultureInfo.InvariantCulture, out var n) && n is > 0 and <= 0x10ffff && n is not (>= 0xd800 and <= 0xdfff)
                    ? char.ConvertFromUtf32(n) : m.Value;
            });
        }
        L.Require(decoded.Length <= L.MaxSegment, "LIMIT");
        return decoded;
    }
    internal static string Krc(string value, CancellationToken token)
    {
        L.Require(value.Length <= 4 * ((L.MaxDecodedBytes + 2) / 3), "LIMIT");
        L.Require(value.Length % 4 == 0 && L.Match(value, "^[A-Za-z0-9+/]*={0,2}$"), "INVALID_RESPONSE");
        byte[] data;
        try { data = Convert.FromBase64String(value); }
        catch (FormatException) { throw new MusicPlatformException("INVALID_RESPONSE"); }
        L.Require(data.Length is >= 10 and <= L.MaxDecodedBytes && data.AsSpan(0, 4).SequenceEqual("krc1"u8), "INVALID_RESPONSE");
        ReadOnlySpan<byte> key = [64, 71, 97, 119, 94, 50, 116, 71, 81, 54, 49, 45, 206, 210, 110, 105];
        var compressed = data.AsSpan(4).ToArray();
        for (var i = 0; i < compressed.Length; i++)
        { if (i % 4096 == 0) token.ThrowIfCancellationRequested(); compressed[i] ^= key[i % 16]; }
        return Inflate(compressed, padding: false, token);
    }
    private static string Inflate(byte[] bytes, bool padding, CancellationToken token)
    {
        L.Require(bytes.Length >= 6 && (bytes[0] & 15) == 8 && (bytes[0] >> 4) <= 7 &&
            (bytes[1] & 32) == 0 && ((bytes[0] << 8) + bytes[1]) % 31 == 0, "INVALID_RESPONSE");
        try
        {
            // A single-byte input boundary prevents inflater read-ahead hiding a
            // truncated trailer, trailing payload or a second compressed stream.
            using var input = new ExactInput(bytes, token);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            uint a = 1, b = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var n = zlib.Read(buffer);
                if (n == 0) break;
                L.Require(output.Length + n <= L.MaxDecodedBytes, "LIMIT");
                foreach (var value in buffer.AsSpan(0, n)) { a = (a + value) % 65521; b = (b + a) % 65521; }
                output.Write(buffer, 0, n);
            }
            token.ThrowIfCancellationRequested();
            int consumed = input.Consumed, remaining = bytes.Length - consumed;
            L.Require(consumed >= 6 && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(consumed - 4, 4)) == (b << 16 | a), "INVALID_RESPONSE");
            L.Require(remaining == 0 || padding && remaining <= 7 && bytes.AsSpan(consumed).IndexOfAnyExcept((byte)0) < 0, "INVALID_RESPONSE");
            return Utf8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        }
        catch (Exception e) when (e is InvalidDataException or IOException or DecoderFallbackException)
        { throw new MusicPlatformException("INVALID_RESPONSE"); }
    }
    private sealed class ExactInput(byte[] bytes, CancellationToken token) : Stream
    {
        internal int Consumed { get; private set; }
        public override int Read(Span<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            if (Consumed == bytes.Length || buffer.Length == 0) return 0;
            buffer[0] = bytes[Consumed++]; return 1;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => Consumed; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
