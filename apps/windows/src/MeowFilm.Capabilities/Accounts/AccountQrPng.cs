// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace MeowFilm.Capabilities.Accounts;

/// <summary>Bound encoded data, chunks, dimensions and inflation BEFORE native image decoding.</summary>
internal static class AccountQrPng
{
    internal const int MaxBytes = 524288;
    internal static void Validate(byte[] bytes, CancellationToken token = default)
    {
        AccountRules.Require(bytes.Length is >= 57 and <= MaxBytes, "LIMIT");
        AccountRules.Require(bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "INVALID_RESPONSE");
        using var compressed = new MemoryStream();
        int at = 8, chunks = 0, maxInflated = 0;
        bool image = false, ended = false, dataEnded = false;
        while (at < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            AccountRules.Require(++chunks <= 256 && bytes.Length - at >= 12, "LIMIT");
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at, 4));
            AccountRules.Require(length <= bytes.Length - at - 12, "INVALID_RESPONSE");
            int n = (int)length;
            var chunk = bytes.AsSpan(at + 4, n + 4);
            uint crc = uint.MaxValue;
            foreach (byte b in chunk)
            {
                crc ^= b;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
            }
            AccountRules.Require(~crc == BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + n + 8, 4)), "INVALID_RESPONSE");
            string kind = Encoding.ASCII.GetString(bytes, at + 4, 4);
            AccountRules.Require(kind.All(char.IsAsciiLetter), "INVALID_RESPONSE");
            if (chunks == 1)
            {
                AccountRules.Require(kind == "IHDR" && n == 13, "INVALID_RESPONSE");
                uint w = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 8, 4));
                uint h = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 12, 4));
                AccountRules.Require(w is > 0 and <= 1024 && h is > 0 and <= 1024, "LIMIT");
                byte depth = bytes[at + 16], color = bytes[at + 17];
                AccountRules.Require(color switch {
                    0 => depth is 1 or 2 or 4 or 8 or 16, 2 or 4 or 6 => depth is 8 or 16,
                    3 => depth is 1 or 2 or 4 or 8, _ => false }, "INVALID_RESPONSE");
                AccountRules.Require(bytes[at + 18] == 0 && bytes[at + 19] == 0 && bytes[at + 20] <= 1, "INVALID_RESPONSE");
                // Includes the seven Adam7 passes/filter bytes, for the largest accepted pixel format.
                maxInflated = checked((int)(w * h * 8 + h * 7 + 1024));
            }
            else if (kind == "IDAT")
            {
                AccountRules.Require(!dataEnded && !ended, "INVALID_RESPONSE");
                image = true; compressed.Write(bytes, at + 8, n);
            }
            else if (kind == "IEND")
            {
                AccountRules.Require(image && n == 0 && at + 12 == bytes.Length, "INVALID_RESPONSE"); ended = true;
            }
            else
            {
                if (image) dataEnded = true;
                // No animations, embedded profiles/text/URLs or unknown critical chunks.
                AccountRules.Require(kind is "PLTE" or "tRNS" or "sRGB" or "gAMA" or "cHRM" or "pHYs", "INVALID_RESPONSE");
            }
            at += n + 12;
        }
        AccountRules.Require(ended, "INVALID_RESPONSE");
        compressed.Position = 0;
        try
        {
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true);
            byte[] buffer = new byte[8192]; int total = 0, read;
            while ((read = zlib.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested();
                total = checked(total + read);
                AccountRules.Require(total <= maxInflated, "LIMIT");
            }
            AccountRules.Require(total > 0, "INVALID_RESPONSE");
        }
        catch (InvalidDataException) { throw new MusicAccountException("INVALID_RESPONSE"); }
    }
}
