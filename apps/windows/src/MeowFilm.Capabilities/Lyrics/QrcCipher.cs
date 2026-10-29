// SPDX-FileCopyrightText: Copyright (c) 2024 沉默の金 <cmzj@cmzj.org>
// SPDX-License-Identifier: GPL-3.0-only
// Native translation of QQMusicApi 27861e51 algorithms/tripledes.py.
// Preserves its nonstandard S-box entries, byte layout and PC-2 offset (-27),
// NOT interchangeable with system DES/3DES. Public QRC format key, not credentials.
// Local changes: compact permutation tables, bounded spans and cancellation.
using System.Buffers.Binary;

namespace MeowFilm.Capabilities.Lyrics;

internal static class QrcCipher
{
    private static readonly byte[][] Sbox = [
        [14,4,13,1,2,15,11,8,3,10,6,12,5,9,0,7,0,15,7,4,14,2,13,1,10,6,12,11,9,5,3,8,4,1,14,8,13,6,2,11,15,12,9,7,3,10,5,0,15,12,8,2,4,9,1,7,5,11,3,14,10,0,6,13],
        [15,1,8,14,6,11,3,4,9,7,2,13,12,0,5,10,3,13,4,7,15,2,8,15,12,0,1,10,6,9,11,5,0,14,7,11,10,4,13,1,5,8,12,6,9,3,2,15,13,8,10,1,3,15,4,2,11,6,7,12,0,5,14,9],
        [10,0,9,14,6,3,15,5,1,13,12,7,11,4,2,8,13,7,0,9,3,4,6,10,2,8,5,14,12,11,15,1,13,6,4,9,8,15,3,0,11,1,2,12,5,10,14,7,1,10,13,0,6,9,8,7,4,15,14,3,11,5,2,12],
        [7,13,14,3,0,6,9,10,1,2,8,5,11,12,4,15,13,8,11,5,6,15,0,3,4,7,2,12,1,10,14,9,10,6,9,0,12,11,7,13,15,1,3,14,5,2,8,4,3,15,0,6,10,10,13,8,9,4,5,11,12,7,2,14],
        [2,12,4,1,7,10,11,6,8,5,3,15,13,0,14,9,14,11,2,12,4,7,13,1,5,0,15,10,3,9,8,6,4,2,1,11,10,13,7,8,15,9,12,5,6,3,0,14,11,8,12,7,1,14,2,13,6,15,0,9,10,4,5,3],
        [12,1,10,15,9,2,6,8,0,13,3,4,14,7,5,11,10,15,4,2,7,12,9,5,6,1,13,14,0,11,3,8,9,14,15,5,2,8,12,3,7,0,4,10,1,13,11,6,4,3,2,12,9,5,15,10,11,14,1,7,6,0,8,13],
        [4,11,2,14,15,0,8,13,3,12,9,7,5,10,6,1,13,0,11,7,4,9,1,10,14,3,5,12,2,15,8,6,1,4,11,13,12,3,7,14,10,15,6,8,0,5,9,2,6,11,13,8,1,4,10,7,9,5,0,15,14,2,3,12],
        [13,2,8,4,6,15,11,1,10,9,3,14,5,0,12,7,1,15,13,8,10,3,7,4,12,5,6,11,0,14,9,2,7,11,4,1,9,12,14,2,0,6,10,13,15,3,5,8,2,1,14,7,4,10,8,13,15,12,9,0,3,5,6,11],

    ];
    private static readonly int[] Order = [4,5,6,7,0,1,2,3];
    private static readonly int[] C = [56,48,40,32,24,16,8,0,57,49,41,33,25,17,9,1,58,50,42,34,26,18,10,2,59,51,43,35];
    private static readonly int[] D = [62,54,46,38,30,22,14,6,61,53,45,37,29,21,13,5,60,52,44,36,28,20,12,4,27,19,11,3];
    private static readonly int[] Compression = [13,16,10,23,0,4,2,27,14,5,20,9,22,18,11,3,25,7,15,6,26,19,12,1,40,51,30,36,46,54,29,39,50,44,32,47,43,48,38,55,33,52,45,41,49,35,28,31];
    private static readonly int[] Shifts = [1,1,2,2,2,2,2,2,1,2,2,2,2,2,2,1];
    private static readonly int[] Expansion = [32,1,2,3,4,5,4,5,6,7,8,9,8,9,10,11,12,13,12,13,14,15,16,17,16,17,18,19,20,21,20,21,22,23,24,25,24,25,26,27,28,29,28,29,30,31,32,1];
    private static readonly int[] Permutation = [16,25,12,11,3,20,4,15,31,17,9,6,27,14,1,22,30,24,8,18,0,5,29,23,13,19,2,26,10,21,28,7];
    private static readonly ulong[][] Keys = [Schedule("!@#)(NHL"u8, true), Schedule("123ZXC!@"u8, false), Schedule("!@#)(*$%"u8, true)];
    private static ulong[] Schedule(ReadOnlySpan<byte> key, bool decrypt)
    {
        uint v0 = BinaryPrimitives.ReadUInt32LittleEndian(key), v1 = BinaryPrimitives.ReadUInt32LittleEndian(key[4..]);
        uint Pick(int[] order)
        {
            uint result = 0;
            for (int i = 0; i < 28; i++) { int bit = order[i]; result |= ((bit < 32 ? v0 >> (31 - bit) : v1 >> (63 - bit)) & 1) << (31 - i); }
            return result;
        }
        uint c = Pick(C), d = Pick(D); var keys = new ulong[16];
        for (int i = 0; i < 16; i++)
        {
            c = ((c << Shifts[i]) | (c >> (28 - Shifts[i]))) & 0xfffffff0;
            d = ((d << Shifts[i]) | (d >> (28 - Shifts[i]))) & 0xfffffff0;
            ulong subkey = 0;
            for (int j = 0; j < 48; j++) subkey = subkey << 1 | (j < 24 ? c >> (31 - Compression[j]) : d >> (31 - (Compression[j] - 27))) & 1;
            keys[decrypt ? 15 - i : i] = subkey;
        }
        return keys;
    }
    private static uint F(uint state, ulong key)
    {
        ulong expanded = 0;
        foreach (var bit in Expansion) expanded = expanded << 1 | (state >> (32 - bit)) & 1;
        expanded ^= key;
        uint value = 0;
        for (int i = 0; i < 8; i++)
        {
            int six = (int)(expanded >> (42 - 6 * i)) & 63;
            int index = (six & 32) | (six & 31) >> 1 | (six & 1) << 4;
            value = value << 4 | Sbox[i][index];
        }
        uint result = 0;
        foreach (var bit in Permutation) result = result << 1 | (value >> bit) & 1;
        return result;
    }
    internal static void Decrypt(Span<byte> data, CancellationToken token)
    {
        L.Require(data.Length > 0 && data.Length <= L.MaxDecodedBytes && data.Length % 8 == 0, "INVALID_RESPONSE");
        for (int offset = 0; offset < data.Length; offset += 8)
        {
            if (offset % 512 == 0) token.ThrowIfCancellationRequested();
            var block = data.Slice(offset, 8);
            foreach (var keys in Keys)
            {
                uint left = 0, right = 0;
                for (int group = 0; group < 4; group++) foreach (var index in Order)
                { left = left << 1 | (uint)(block[index] >> (6 - group * 2)) & 1; right = right << 1 | (uint)(block[index] >> (7 - group * 2)) & 1; }
                for (int i = 0; i < 15; i++) (left, right) = (right, F(right, keys[i]) ^ left);
                left ^= F(right, keys[15]);
                block.Clear();
                for (int group = 0; group < 4; group++) for (int i = 0; i < 8; i++)
                {
                    int bit = 31 - group * 8 - i;
                    block[Order[i]] |= (byte)((left >> bit & 1) << (6 - group * 2) | (right >> bit & 1) << (7 - group * 2));
                }
            }
        }
        token.ThrowIfCancellationRequested();
    }
}
