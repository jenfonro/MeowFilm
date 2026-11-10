using System.Buffers.Binary;
using System.Security.Cryptography;
using MeowFilm.Capabilities.Feedback;

namespace MeowFilm.Windows.Tests;

public static class NeteasePlaybackLogCodecTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("NCBL codec matches the shared independent cryptography fixture", IndependentFixture);
        yield return new("NCBL codec preserves the complete maximum-size container", MaximumSize);
        yield return new("NCBL codec handles empty bytes and frame boundaries", FrameBoundaries);
        yield return new("NCBL codec generates fresh nonces and wrapping keys", FreshRandomness);
        yield return new("NCBL codec rejects null and oversized inputs", InvalidInputs);
        yield return new("NCBL codec rejection-samples the unsigned wrapping key", RejectionSampling);
        yield return new("NCBL codec never substitutes insecure randomness", RandomFailure);
    }

    private static Task IndependentFixture()
    {
        // Synthetic cross-language fixture: Python cryptography ChaCha20 and pow(..., 65537, N).
        // The plaintext raw frame was round-tripped by standard libzstd, not a local codec decoder.
        var metadata = "{\"fixture\":1}"u8.ToArray();
        var records = Enumerable.Range(0, 65536).Select(i => (byte)i).ToArray();
        var draws = new[]
        {
            Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(),
            Enumerable.Range(16, 16).Select(i => (byte)i).ToArray()
        };
        int draw = 0;
        var output = NeteasePlaybackLogCodec.EncodeForTesting(metadata, records, buffer =>
        {
            Require(draw < draws.Length && buffer.Length == draws[draw].Length, "Fixture random draw");
            draws[draw++].CopyTo(buffer, 0);
        });
        Require(draw == 2, "One key and UUID for the complete payload");
        Require(Convert.ToHexStringLower(SHA256.HashData(output)) ==
            "b9f9fa0e856f9f471eceb56febd04e65a220f52a26e6714b34f03ec480a9bb45",
            "Complete shared Python fixture, including header counts and frame sequences");
        Require(output.AsSpan(26, 32).SequenceEqual(Convert.FromHexString(
            "dda20437ce173c34273cb03bffb85db8f3ce53bc2f1334a752303d26890094af")), "Public key wrapping fixture");
        Require(output.AsSpan(74, 13).SequenceEqual(Convert.FromHexString("daba5c8865dc9e78fbccc0c2b1")),
            "Encrypted metadata fixture");
        var frames = Frames(output);
        Require(Read32(output, 58) == frames[0].Sequence &&
            Read32(output, 62) == frames[^1].Sequence, "Header sequences bound the encoded frames");
        Require(Read32(output, 66) == output.Length - Read16(output, 8), "Total bytes include frame headers");
        var ciphertext = new List<byte>();
        int offset = Read16(output, 8);
        foreach (var frame in frames)
        {
            ciphertext.AddRange(output.AsSpan(offset + 6, frame.Length).ToArray());
            offset += frame.Length + 6;
        }
        Require(Convert.ToHexStringLower(SHA256.HashData(ciphertext.ToArray())) ==
            "4a055e6685394f6030c2a1410bd443e5a2b5e87e597f503f61f87abdb6ee0457",
            "All encrypted frames match independent fixture, including ChaCha restarts");
        Require(output.AsSpan(Read16(output, 8) + 6, 32).SequenceEqual(Convert.FromHexString(
            "32816c50960617d8be56dc3f0adb5b05e9806668770571a129ea6682ff79a58e")),
            "Standard Zstandard header encrypted with the body key");
        return Task.CompletedTask;
    }

    private static Task MaximumSize()
    {
        var metadata = Enumerable.Repeat((byte)0xff, 8192).ToArray();
        var records = Enumerable.Range(0, 65536).Select(i => (byte)i).ToArray();
        var output = NeteasePlaybackLogCodec.Encode(metadata, records);
        Require(output.AsSpan(0, 4).SequenceEqual("NCBL"u8), "NCBL magic");
        Require(Read32(output, 4) == 3, "Version 3");
        Require(Read16(output, 8) == 8266, "Header includes the metadata extension");
        Require(Read16(output, 70) == 0x4343 && Read16(output, 72) == 8192, "Metadata type and length");
        var frames = Frames(output);
        Require(Read32(output, 58) == 0 && Read32(output, 62) == 2 && Read32(output, 66) == 65566,
            "Fresh payload sequence range and framed byte count");
        Require(frames.Select(frame => frame.Length).SequenceEqual([32768, 32768, 12]), "No truncated records");
        Require(frames.Zip(frames.Skip(1)).All(pair => pair.Second.Sequence == pair.First.Sequence + 1),
            "Strictly increasing frame sequences");
        Require(output.Length == 8266 + 65548 + 18, "Complete framing and Zstandard overhead");
        Require(metadata.All(value => value == 0xff) &&
            records.Select((value, index) => value == (byte)index).All(value => value), "Inputs stay unchanged");
        return Task.CompletedTask;
    }

    private static Task FrameBoundaries()
    {
        foreach (int length in new[] { 0, 1, 32755, 32756, 32757, 65524, 65525, 65536 })
        {
            var output = NeteasePlaybackLogCodec.Encode([], new byte[length]);
            Require(Read16(output, 8) == 74 && Read16(output, 72) == 0, "Empty metadata is bytes, not JSON");
            var frames = Frames(output);
            Require(frames.Count == (length + 12 + 32767) / 32768, "Frame count at boundary");
            Require(frames.Sum(frame => frame.Length) == length + 12, "Standard raw Zstandard frame length");
            Require(frames.All(frame => frame.Length is > 0 and <= 32768), "Frame payload bounds");
        }
        return Task.CompletedTask;
    }

    private static Task FreshRandomness()
    {
        var first = NeteasePlaybackLogCodec.Encode([0xff], [0x80, 0x00]);
        var second = NeteasePlaybackLogCodec.Encode([0xff], [0x80, 0x00]);
        Require(!first.AsSpan(10, 16).SequenceEqual(second.AsSpan(10, 16)), "Fresh UUID/nonce per payload");
        Require(!first.AsSpan(26, 32).SequenceEqual(second.AsSpan(26, 32)), "Fresh wrapped key per payload");
        Require(!first.AsSpan().SequenceEqual(second), "No deterministic production ciphertext");
        return Task.CompletedTask;
    }

    private static Task InvalidInputs()
    {
        Throws<ArgumentNullException>(() => NeteasePlaybackLogCodec.Encode(null!, []));
        Throws<ArgumentNullException>(() => NeteasePlaybackLogCodec.Encode([], null!));
        Throws<ArgumentOutOfRangeException>(() => NeteasePlaybackLogCodec.Encode(new byte[8193], []));
        Throws<ArgumentOutOfRangeException>(() => NeteasePlaybackLogCodec.Encode([], new byte[65537]));
        return Task.CompletedTask;
    }

    private static Task RejectionSampling()
    {
        var modulus = Convert.FromHexString("fd90bd466ff9bc8a3fec2fbcf263b90d5c564879fa5d7aab89b31c1d5cb4139d");
        var largestKey = modulus.ToArray();
        largestKey[^1]--;
        var uuid = Enumerable.Range(16, 16).Select(i => (byte)i).ToArray();
        int draws = 0;
        var output = NeteasePlaybackLogCodec.EncodeForTesting([], [], buffer =>
        {
            byte[] source = draws++ switch
            {
                0 => modulus,
                1 => Enumerable.Repeat((byte)0xff, 32).ToArray(),
                2 => largestKey,
                3 => uuid,
                _ => throw new InvalidOperationException("Unexpected random draw")
            };
            Require(source.Length == buffer.Length, "Random buffer size");
            source.CopyTo(buffer, 0);
        });
        Require(draws == 4, "Reject values equal to or above the modulus");
        // (N - 1)^65537 mod N == N - 1, including its unsigned high bit.
        Require(output.AsSpan(26, 32).SequenceEqual(largestKey), "Unsigned 256-bit wrapping");
        Require(output.AsSpan(10, 16).SequenceEqual(uuid), "UUID draw is not reused as key material");
        var zero = NeteasePlaybackLogCodec.EncodeForTesting([], [], buffer => Array.Clear(buffer));
        Require(zero.AsSpan(26, 32).SequenceEqual(new byte[32]), "Wrapped values are padded to 32 bytes");
        return Task.CompletedTask;
    }

    private static Task RandomFailure()
    {
        Throws<CryptographicException>(() => NeteasePlaybackLogCodec.EncodeForTesting([], [],
            _ => throw new CryptographicException("Synthetic random source failure")));
        return Task.CompletedTask;
    }

    private static List<(int Length, uint Sequence)> Frames(byte[] output)
    {
        var result = new List<(int Length, uint Sequence)>();
        int offset = Read16(output, 8);
        while (offset < output.Length)
        {
            Require(offset + 6 <= output.Length, "Complete frame header");
            int length = Read16(output, offset);
            Require(offset + 6 + length <= output.Length, "Complete frame payload");
            result.Add((length, Read32(output, offset + 2)));
            offset += 6 + length;
        }
        Require(offset == output.Length, "No trailing bytes");
        return result;
    }

    private static ushort Read16(byte[] value, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(offset));

    private static uint Read32(byte[] value, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(offset));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
}
