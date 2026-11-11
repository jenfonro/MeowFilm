using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace MeowFilm.Capabilities.Feedback;

internal static class NeteasePlaybackLogCodec
{
    private const int MetadataLimit = 8 * 1024;
    private const int RecordsLimit = 64 * 1024;
    private const int FrameLimit = 32768;
    private static readonly BigInteger Modulus = new(Convert.FromHexString(
        "fd90bd466ff9bc8a3fec2fbcf263b90d5c564879fa5d7aab89b31c1d5cb4139d"),
        isUnsigned: true, isBigEndian: true);

    internal static byte[] Encode(byte[] metadata, byte[] records) =>
        EncodeForTesting(metadata, records, buffer => RandomNumberGenerator.Fill(buffer));

    // No mutable global random source: only this internal test seam accepts deterministic draws.
    internal static byte[] EncodeForTesting(byte[] metadata, byte[] records, Action<byte[]> fillRandom)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(records);
        if (metadata.Length > MetadataLimit) throw new ArgumentOutOfRangeException(nameof(metadata));
        if (records.Length > RecordsLimit) throw new ArgumentOutOfRangeException(nameof(records));

        var key = new byte[32];
        BigInteger value;
        do
        {
            fillRandom(key);
            value = new BigInteger(key, isUnsigned: true, isBigEndian: true);
        } while (value >= Modulus);
        var uuid = new byte[16];
        fillRandom(uuid);
        var wrapped = WrapKey(value);
        var compressed = ZstandardRawFrame(records);
        int frameCount = (compressed.Length + FrameLimit - 1) / FrameLimit;
        int headerLength = 74 + metadata.Length;
        var output = new byte[headerLength + compressed.Length + frameCount * 6];
        "NCBL"u8.CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(8), (ushort)headerLength);
        uuid.CopyTo(output, 10);
        wrapped.CopyTo(output, 26);
        // Fresh payloads use file-local sequences. Native totalBytes counts frame headers too.
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(58), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(62), (uint)(frameCount - 1));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(66), (uint)(compressed.Length + frameCount * 6));
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(70), 0x4343);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(72), (ushort)metadata.Length);
        Encrypt(metadata, output.AsSpan(74, metadata.Length), wrapped, uuid);

        int destination = headerLength;
        for (int offset = 0; offset < compressed.Length; offset += FrameLimit)
        {
            int length = Math.Min(FrameLimit, compressed.Length - offset);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(destination), (ushort)length);
            uint sequence = (uint)(offset / FrameLimit);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(destination + 2), sequence);
            // The native container restarts the same ChaCha stream for every frame.
            Encrypt(compressed.AsSpan(offset, length), output.AsSpan(destination + 6, length), key, uuid);
            destination += 6 + length;
        }
        CryptographicOperations.ZeroMemory(key);
        return output;
    }

    private static byte[] WrapKey(BigInteger key)
    {
        var wrapped = BigInteger.ModPow(key, 65537, Modulus).ToByteArray(isUnsigned: true, isBigEndian: true);
        var output = new byte[32];
        wrapped.CopyTo(output, output.Length - wrapped.Length);
        return output;
    }

    private static byte[] ZstandardRawFrame(byte[] records)
    {
        // Single-segment Zstandard frame with a four-byte content size and one final raw block.
        // The records resource limit keeps this below Zstandard's 128 KiB block limit.
        var output = new byte[records.Length + 12];
        BinaryPrimitives.WriteUInt32LittleEndian(output, 0xfd2fb528);
        output[4] = 0xa0;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(5), (uint)records.Length);
        uint blockHeader = ((uint)records.Length << 3) | 1;
        output[9] = (byte)blockHeader;
        output[10] = (byte)(blockHeader >> 8);
        output[11] = (byte)(blockHeader >> 16);
        records.CopyTo(output, 12);
        return output;
    }

    private static void Encrypt(ReadOnlySpan<byte> input, Span<byte> output,
        ReadOnlySpan<byte> key, ReadOnlySpan<byte> uuid)
    {
        Span<uint> state = stackalloc uint[16];
        state[0] = 0x61707865; state[1] = 0x3320646e;
        state[2] = 0x79622d32; state[3] = 0x6b206574;
        for (int i = 0; i < 8; i++)
            state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(i * 4)..]);
        state[12] = BinaryPrimitives.ReadUInt32LittleEndian(uuid[12..]) >> 2;
        for (int i = 0; i < 3; i++)
            state[13 + i] = BinaryPrimitives.ReadUInt32LittleEndian(uuid[(i * 4)..]);

        Span<uint> working = stackalloc uint[16];
        Span<byte> block = stackalloc byte[64];
        for (int offset = 0; offset < input.Length; offset += 64)
        {
            state.CopyTo(working);
            for (int round = 0; round < 10; round++)
            {
                QuarterRound(working, 0, 4, 8, 12);
                QuarterRound(working, 1, 5, 9, 13);
                QuarterRound(working, 2, 6, 10, 14);
                QuarterRound(working, 3, 7, 11, 15);
                QuarterRound(working, 0, 5, 10, 15);
                QuarterRound(working, 1, 6, 11, 12);
                QuarterRound(working, 2, 7, 8, 13);
                QuarterRound(working, 3, 4, 9, 14);
            }
            for (int i = 0; i < 16; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(block[(i * 4)..], unchecked(working[i] + state[i]));
            int count = Math.Min(64, input.Length - offset);
            for (int i = 0; i < count; i++)
                output[offset + i] = (byte)(input[offset + i] ^ block[i]);
            state[12] = unchecked(state[12] + 1);
        }
    }

    private static void QuarterRound(Span<uint> state, int a, int b, int c, int d)
    {
        unchecked
        {
            state[a] += state[b]; state[d] = BitOperations.RotateLeft(state[d] ^ state[a], 16);
            state[c] += state[d]; state[b] = BitOperations.RotateLeft(state[b] ^ state[c], 12);
            state[a] += state[b]; state[d] = BitOperations.RotateLeft(state[d] ^ state[a], 8);
            state[c] += state[d]; state[b] = BitOperations.RotateLeft(state[b] ^ state[c], 7);
        }
    }
}
