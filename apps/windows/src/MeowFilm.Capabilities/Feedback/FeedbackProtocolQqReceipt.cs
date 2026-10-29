// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace MeowFilm.Capabilities.Feedback;

internal static class FeedbackProtocolQqReceipt
{
    internal static void Verify(byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        FeedbackProtocolRules.Require(bytes.Length <= 65536, "LIMIT");
        FeedbackProtocolRules.Require(bytes.Length > 11 && bytes.AsSpan(0, 5).IndexOfAnyExcept((byte)0) == -1, "UPSTREAM_UNCONFIRMED");
        try
        {
            // One-byte reads prevent an inflater's read-ahead from hiding extra trailing members.
            using var input = new ExactInput(bytes);
            using var output = new MemoryStream();
            using (var zlib = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true))
            {
                var buffer = new byte[2048];
                int read;
                while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    FeedbackProtocolRules.Require(output.Length + read <= 65536, "LIMIT");
                    output.Write(buffer, 0, read);
                }
            }
            token.ThrowIfCancellationRequested();
            FeedbackProtocolRules.Require(input.Position == input.Length && output.Length > 0, "UPSTREAM_UNCONFIRMED");
            // Explicit Adler32 also rejects a truncated footer that some stream APIs treat as EOF.
            byte[] body = output.ToArray();
            uint a = 1, b = 0;
            foreach (byte value in body) { a = (a + value) % 65521; b = (b + a) % 65521; }
            uint checksum = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(bytes.Length - 4));
            FeedbackProtocolRules.Require(checksum == (b << 16 | a), "UPSTREAM_UNCONFIRMED");
            string text = new UTF8Encoding(false, true).GetString(body);
            using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            FeedbackProtocolRules.Require(json.RootElement.ValueKind == JsonValueKind.Object, "UPSTREAM_UNCONFIRMED");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in json.RootElement.EnumerateObject())
                FeedbackProtocolRules.Require(names.Add(property.Name), "UPSTREAM_UNCONFIRMED");
            FeedbackProtocolRules.Require(json.RootElement.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.Number && code.TryGetInt64(out long n) && n == 0 &&
                code.GetRawText().IndexOfAny(['.', 'e', 'E']) < 0, "UPSTREAM_UNCONFIRMED");
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or DecoderFallbackException or IOException)
        { throw new MusicFeedbackException("UPSTREAM_UNCONFIRMED"); }
        token.ThrowIfCancellationRequested();
    }

    private sealed class ExactInput(byte[] bytes) : MemoryStream(bytes, 5, bytes.Length - 5, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1)]);
    }
}
