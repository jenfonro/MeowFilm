// SPDX-License-Identifier: GPL-3.0-only
// Complete-file checks adapted from fixed iWebPlus audio-file-cache.js, Apache-2.0.
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MeowFilm.Capabilities.PlaybackCache;

internal static class AudioCacheStructure
{
    internal sealed record AudioFacts(string Codec, int SampleRate, int BitDepth, int Channels, int Bitrate)
    {
        // Encoding properties, never proof of original mastering or spatial output.
        internal string Quality => Codec is "flac" or "pcm"
            ? BitDepth > 16 || SampleRate > 48000 ? "flac24bit" : "flac"
            : Codec == "mp3" && Bitrate > 0 ? Bitrate >= 256000 ? "320k" : Bitrate >= 160000 ? "192k" : "128k" : "";
    }
    internal static void Validate(Stream file, CancellationToken token) => Inspect(file, token);
    internal static AudioFacts? Inspect(Stream file, CancellationToken token)
    {
        long length = file.Length;
        byte[] Read(long at, int count)
        {
            token.ThrowIfCancellationRequested();
            if (at < 0 || count < 0 || at > length - count) throw new InvalidDataException("CACHE_INCOMPLETE");
            file.Position = at; var bytes = new byte[count]; file.ReadExactly(bytes); return bytes;
        }
        string Tag(long at, int count) => Encoding.ASCII.GetString(Read(at, count));
        uint U32(long at, bool little = false) => little ? BinaryPrimitives.ReadUInt32LittleEndian(Read(at, 4)) :
            BinaryPrimitives.ReadUInt32BigEndian(Read(at, 4));
        void Require(bool valid) { if (!valid) throw new InvalidDataException("CACHE_INCOMPLETE"); }
        Require(length >= 12);
        long start = 0;
        for (int tags = 0; Tag(start, 3) == "ID3"; tags++)
        {
            var header = Read(start, 10); Require(tags < 8 && header[3] is >= 2 and <= 4);
            long size = 0; for (int i = 6; i < 10; i++) { Require(header[i] < 128); size = size * 128 + header[i]; }
            start += 10 + size + (header[3] == 4 && (header[5] & 16) != 0 ? 10 : 0);
            Require(start < length - 4);
        }
        if (Tag(0, 4) == "RIFF")
        {
            Require(Tag(8, 4) == "WAVE" && U32(4, true) + 8L == length);
            long p = 12; bool data = false, pcm = false;
            int rate = 0, depth = 0, channels = 0, bitrate = 0;
            while (p + 8 <= length)
            {
                uint size = U32(p + 4, true); Require(p + 8 + size <= length);
                string type = Tag(p, 4);
                if (type == "fmt ")
                {
                    Require(size >= 16); var format = Read(p + 8, 16);
                    ushort code = BinaryPrimitives.ReadUInt16LittleEndian(format);
                    pcm = code is 1 or 3;
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2));
                    rate = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4)));
                    depth = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14));
                    bitrate = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(8)) * 8);
                    if (code == 0xfffe)
                    {
                        Require(size >= 40);
                        var extension = Read(p + 24, 24);
                        Require(BinaryPrimitives.ReadUInt16LittleEndian(extension) >= 22);
                        var subFormat = new Guid(extension.AsSpan(8, 16));
                        pcm = subFormat == new Guid("00000001-0000-0010-8000-00aa00389b71") ||
                            subFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
                        int validBits = BinaryPrimitives.ReadUInt16LittleEndian(extension.AsSpan(2));
                        Require(validBits <= depth);
                        if (validBits > 0) depth = validBits;
                    }
                    Require(BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2)) > 0 &&
                        BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4)) > 0 &&
                        BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(12)) > 0);
                }
                if (type == "data") data = size > 0;
                p += 8L + size + (size & 1);
            }
            Require(p == length && pcm && data && depth is >= 8 and <= 64);
            return new("pcm", rate, depth, channels, bitrate);
        }
        if (Tag(start, 4) == "fLaC")
        {
            long p = start + 4; bool last = false, streamInfo = false;
            int rate = 0, depth = 0, channels = 0;
            while (!last && p + 4 <= length)
            {
                var h = Read(p, 4); last = (h[0] & 128) != 0;
                int size = h[1] * 65536 + h[2] * 256 + h[3];
                Require(p + 4 + size <= length);
                if ((h[0] & 127) == 0)
                {
                    Require(size == 34 && !streamInfo && p == start + 4); streamInfo = true;
                    ulong packed = BinaryPrimitives.ReadUInt64BigEndian(Read(p + 14, 8));
                    rate = (int)(packed >> 44);
                    channels = (int)((packed >> 41) & 7) + 1;
                    depth = (int)((packed >> 36) & 31) + 1;
                    Require(rate > 0 && depth is >= 4 and <= 32);
                }
                p += 4 + size;
            }
            Require(last && streamInfo && p + 6 < length);
            var frame = Read(p, 2); Require(frame[0] == 255 && (frame[1] & 252) == 248);
            return new("flac", rate, depth, channels, 0); // Native decoding is still required.
        }
        if (Tag(4, 4) == "ftyp")
        {
            bool movie = false, payload = false; int work = 0;
            void Boxes(long begin, long end, int depth, bool entries = false)
            {
                Require(depth < 16); long p = begin;
                while (p + 8 <= end)
                {
                    Require(++work <= 100000);
                    long size = U32(p), header = 8; string type = Tag(p + 4, 4);
                    if (size == 1) { size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(Read(p + 8, 8))); header = 16; }
                    if (size == 0) size = end - p;
                    Require(size >= header && size <= end - p);
                    Require(type is not ("pssh" or "sinf" or "enca" or "encv" or "drms" or "drmi" or "moof" or "mvex"));
                    if (type == "moov") movie = true;
                    if (type == "mdat" && size > header) payload = true;
                    if (type is "moov" or "trak" or "mdia" or "minf" or "stbl") Boxes(p + header, p + size, depth + 1);
                    if (type == "stsd") Boxes(p + header + 8, p + size, depth + 1, true);
                    if (entries && type is "mp4a" or "alac")
                    {
                        ushort version = BinaryPrimitives.ReadUInt16BigEndian(Read(p + header + 8, 2));
                        Require(version <= 1); Boxes(p + header + (version == 1 ? 44 : 28), p + size, depth + 1);
                    }
                    p += size;
                }
                Require(p == end);
            }
            Boxes(0, length, 0); Require(movie && payload); return null;
        }
        long end = length;
        if (end - start >= 128 && Tag(end - 128, 3) == "TAG") end -= 128;
        if (end - start >= 32 && Tag(end - 32, 8) == "APETAGEX")
        {
            long footer = end - 32;
            uint size = U32(footer + 12, true), count = U32(footer + 16, true), flags = U32(footer + 20, true);
            long first = end - size;
            Require(U32(footer + 8, true) == 2000 && size >= 32 && first >= start &&
                (flags & 0x7ffffffe) == 0 && U32(footer + 24) == 0 && U32(footer + 28) == 0);
            long p = first;
            for (long item = 0; item < count; item++)
            {
                Require(p + 8 <= footer);
                uint valueLength = U32(p, true), itemFlags = U32(p + 4, true);
                Require((itemFlags & ~7u) == 0 && (itemFlags & 6) != 6);
                p += 8; long key = p;
                while (p < footer)
                {
                    byte ch = Read(p, 1)[0]; if (ch == 0) break;
                    Require(ch is >= 32 and <= 126 && ch != 61 && p - key < 255); p++;
                }
                Require(p - key >= 2 && p < footer && p + 1 + valueLength <= footer);
                p += 1 + valueLength;
            }
            Require(p == footer); end = first;
            if ((flags & 0x80000000) != 0)
            {
                end -= 32;
                Require(end >= start && Tag(end, 8) == "APETAGEX" && U32(end + 8, true) == 2000 &&
                    U32(end + 12, true) == size && U32(end + 16, true) == count &&
                    U32(end + 20, true) == flags + 0x20000000 && U32(end + 24) == 0 && U32(end + 28) == 0);
            }
        }
        long position = start; int frames = 0, sampleRate = 0, channelCount = 0;
        double seconds = 0;
        while (position + 4 <= end)
        {
            var h = Read(position, 4);
            int version = (h[1] >> 3) & 3, index = h[2] >> 4, rateIndex = (h[2] >> 2) & 3;
            Require(h[0] == 255 && (h[1] & 224) == 224 && version != 1 && ((h[1] >> 1) & 3) == 1 &&
                index is > 0 and < 15 && rateIndex < 3);
            int rate = new[] { 44100, 48000, 32000 }[rateIndex] / (version == 3 ? 1 : version == 2 ? 2 : 4);
            if (sampleRate == 0) sampleRate = rate; Require(sampleRate == rate);
            int channels = (h[3] >> 6) == 3 ? 1 : 2;
            if (channelCount == 0) channelCount = channels;
            Require(channelCount == channels);
            int bitrate = (version == 3 ? new[] { 0,32,40,48,56,64,80,96,112,128,160,192,224,256,320 } :
                new[] { 0,8,16,24,32,40,48,56,64,80,96,112,128,144,160 })[index] * 1000;
            int size = (version == 3 ? 144 : 72) * bitrate / rate + ((h[2] >> 1) & 1);
            Require(position + size <= end); position += size; frames++;
            seconds += (version == 3 ? 1152d : 576d) / rate;
        }
        Require(position == end && frames >= 3);
        return new("mp3", sampleRate, 0, channelCount, (int)Math.Round((end - start) * 8 / seconds));
    }
}
