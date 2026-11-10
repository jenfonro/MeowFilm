using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MeowFilm.Windows.Tests;

// Standard synthetic fragmented MP4: Motion JPEG video and PCM audio, optionally
// full-sample CENC with distinct KIDs. No external encoder, account or media URL.
internal static class FFmpegVideoFixtures
{
    internal const int Seconds = 24, FramesPerSecond = 10;
    internal const string VideoKid = "00112233445566778899aabbccddeeff";
    internal const string AudioKid = "102132435465768798a9bacbdcedfe0f";
    internal const string VideoKey = "ffeeddccbbaa99887766554433221100";
    internal const string AudioKey = "0123456789abcdef0123456789abcdef";
    private static readonly byte[] Iv = new byte[16];

    internal static byte[] VideoInit(bool encrypted = false) => Init(true, encrypted);
    internal static byte[] AudioInit(bool encrypted = false) => Init(false, encrypted);
    private static byte[] Init(bool video, bool encrypted)
    {
        uint scale = video ? FramesPerSecond : 8000u, id = video ? 1u : 2u;
        byte[] sample = video
            ? Box(encrypted ? "encv" : "mjpg", new byte[6], U16(1), new byte[16], U16(160), U16(90),
                U32(72 << 16), U32(72 << 16), U32(0), U16(1), new byte[32], U16(24), U16(0xffff),
                encrypted ? Protection("mjpg", VideoKid) : [])
            : Box(encrypted ? "enca" : "sowt", new byte[6], U16(1), new byte[8], U16(1), U16(16),
                new byte[4], U32(8000u << 16), encrypted ? Protection("sowt", AudioKid) : []);
        return Join(Box("ftyp", Text("isom"), U32(0), Text("isomiso6mp41")),
            Box("moov", Full("mvhd", 0, new byte[8], U32(1000), U32(Seconds * 1000), U32(0x10000),
                    U16(0x100), new byte[10], Matrix(), new byte[24], U32(3)),
                Box("trak", Full("tkhd", 7, new byte[8], U32(id), U32(0), U32(Seconds * 1000),
                        new byte[8], U16(0), U16(0), U16(video ? 0u : 0x100u), U16(0), Matrix(),
                        U32(video ? 160u << 16 : 0), U32(video ? 90u << 16 : 0)),
                    Box("mdia", Full("mdhd", 0, new byte[8], U32(scale), U32(scale * Seconds), U16(0x55c4), U16(0)),
                        Full("hdlr", 0, U32(0), Text(video ? "vide" : "soun"), new byte[12], Text("Synthetic\0")),
                        Box("minf", video ? Full("vmhd", 1, new byte[8]) : Full("smhd", 0, new byte[4]),
                            Box("dinf", Full("dref", 0, U32(1), Full("url ", 1))),
                            Box("stbl", Full("stsd", 0, U32(1), sample), Full("stts", 0, U32(0)),
                                Full("stsc", 0, U32(0)), Full("stsz", 0, U32(0), U32(0)), Full("stco", 0, U32(0)))))),
                Box("mvex", Full("trex", 0, U32(id), U32(1), U32(video ? 1u : 8000u), U32(0), U32(0)))));
    }
    private static byte[] Protection(string original, string kid) =>
        Box("sinf", Box("frma", Text(original)), Full("schm", 0, Text("cenc"), U32(0x10000)),
            Box("schi", Full("tenc", 0x1000000, new byte[] { 0, 0, 1, 0 },
                Convert.FromHexString(kid), new byte[] { 16 }, Iv)));

    internal static byte[] VideoFragment(int second, bool encrypted = false)
    {
        var frames = Enumerable.Range(0, FramesPerSecond).Select(frame => Jpeg(second * FramesPerSecond + frame)).ToArray();
        return Fragment(1, second, FramesPerSecond, frames, encrypted ? VideoKey : null);
    }
    internal static byte[] AudioFragment(int second, bool encrypted = false)
    {
        var samples = new byte[16000];
        for (int i = 0; i < 8000; i++)
            BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(i * 2), (short)(Math.Sin(i * .2) * 1000));
        return Fragment(2, second, 8000, [samples], encrypted ? AudioKey : null);
    }
    private static byte[] Fragment(uint track, int second, uint scale, byte[][] samples, string? key)
    {
        var data = key is null ? samples : samples.Select(sample => Ctr(sample, key)).ToArray();
        byte[] Make(uint offset) => Box("moof", Full("mfhd", 0, U32((uint)second + 1)),
            Box("traf", Full("tfhd", 0x20000, U32(track)),
                Full("tfdt", 0x1000000, U32(0), U32((uint)second * scale)),
                Full("trun", 0x301, U32((uint)samples.Length), U32(offset),
                    Join(data.Select(sample => Join(U32(scale / (uint)samples.Length), U32((uint)sample.Length))).ToArray()))));
        byte[] moof = Make(0);
        return Join(Make((uint)moof.Length + 8), Box("mdat", Join(data)));
    }
    private static byte[] Jpeg(int index)
    {
        var pixels = new byte[160 * 90 * 3];
        for (int i = 0; i < pixels.Length; i += 3)
        { pixels[i] = (byte)(index % 128 + 32); pixels[i + 1] = 50; pixels[i + 2] = 180; }
        var bitmap = BitmapSource.Create(160, 90, 96, 96, PixelFormats.Rgb24, null, pixels, 160 * 3);
        var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }
    private static byte[] Ctr(byte[] bytes, string hexKey)
    {
        using var aes = Aes.Create();
        aes.Key = Convert.FromHexString(hexKey);
        var counter = new byte[16]; var stream = new byte[16]; var output = new byte[bytes.Length];
        for (int offset = 0; offset < bytes.Length; offset += 16)
        {
            aes.EncryptEcb(counter, stream, PaddingMode.None);
            for (int i = 0; i < Math.Min(16, bytes.Length - offset); i++) output[offset + i] = (byte)(bytes[offset + i] ^ stream[i]);
            for (int i = 15; i >= 0 && ++counter[i] == 0; i--) { }
        }
        return output;
    }
    internal static byte[] Aes128(byte[] bytes)
    {
        using var aes = Aes.Create(); aes.Key = Convert.FromHexString(VideoKey);
        return aes.EncryptCbc(bytes, new byte[16], PaddingMode.PKCS7);
    }
    internal static string Dash()
    {
        string Adapt(bool video)
        {
            string kind = video ? "video" : "audio", codecs = video ? "jpeg" : "sowt";
            return $"""
                <AdaptationSet contentType="{kind}" mimeType="{kind}/mp4" codecs="{codecs}">
                  <SegmentTemplate timescale="1" duration="1" startNumber="0" initialization="{kind}-init" media="{kind}-$Number$"/>
                  <Representation id="{kind}" bandwidth="128000" />
                </AdaptationSet>
                """;
        }
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT{Seconds}S" minBufferTime="PT1S">
              <Period duration="PT{Seconds}S"><BaseURL>parts/</BaseURL>{Adapt(true)}{Adapt(false)}</Period>
            </MPD>
            """;
    }
    internal static byte[] Progressive() => Join(VideoInit(), Join(Enumerable.Range(0, Seconds).Select(i => VideoFragment(i)).ToArray()));
    internal static byte[] Main10TransportStream(byte[] annexB)
    {
        var starts = new List<int>();
        for (int i = 0; i + 3 < annexB.Length; i++)
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1) starts.Add(i);
        starts.Add(annexB.Length);
        var pictures = new List<byte[]>(); using var picture = new MemoryStream(); bool hasVcl = false;
        for (int i = 0; i + 1 < starts.Count; i++)
        {
            var unit = annexB.AsSpan(starts[i], starts[i + 1] - starts[i]);
            bool vcl = unit.Length > 5 && ((unit[3] >> 1) & 63) <= 31;
            if (vcl && (unit[5] & 128) != 0 && hasVcl)
            { pictures.Add(picture.ToArray()); picture.SetLength(0); hasVcl = false; }
            picture.Write(unit); hasVcl |= vcl;
        }
        if (hasVcl) pictures.Add(picture.ToArray());
        if (pictures.Count == 0) throw new InvalidOperationException("Main10 test sample has no coded picture.");
        using var output = new MemoryStream();
        int continuity = 0;
        void Packet(int pid, byte[] payload, long? pcr = null)
        {
            for (int offset = 0; offset < payload.Length;)
            {
                bool first = offset == 0, clock = first && pcr is not null;
                int count = Math.Min(payload.Length - offset, clock ? 176 : 184);
                var packet = Enumerable.Repeat((byte)255, 188).ToArray();
                packet[0] = 0x47; packet[1] = (byte)((pid >> 8) | (first ? 0x40 : 0)); packet[2] = (byte)pid;
                bool adaptation = count < 184;
                packet[3] = (byte)((adaptation ? 0x30 : 0x10) | (pid == 0x101 ? continuity++ & 15 : 0));
                int at = 4;
                if (adaptation)
                {
                    int size = 183 - count; packet[at++] = (byte)size;
                    if (size > 0) packet[at++] = clock ? (byte)0x10 : (byte)0;
                    if (clock)
                    {
                        long stamp = pcr!.Value;
                        packet[at++] = (byte)(stamp >> 25); packet[at++] = (byte)(stamp >> 17);
                        packet[at++] = (byte)(stamp >> 9); packet[at++] = (byte)(stamp >> 1);
                        packet[at++] = (byte)((stamp & 1) << 7 | 0x7e); packet[at++] = 0;
                    }
                    at = 5 + size;
                }
                payload.AsSpan(offset, count).CopyTo(packet.AsSpan(at)); output.Write(packet); offset += count;
            }
        }
        byte[] Section(byte[] data)
        {
            uint crc = uint.MaxValue;
            foreach (byte value in data)
            {
                crc ^= (uint)value << 24;
                for (int bit = 0; bit < 8; bit++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04c11db7 : crc << 1;
            }
            return Join([0], data, U32(crc));
        }
        Packet(0, Section([0, 0xb0, 13, 0, 1, 0xc1, 0, 0, 0, 1, 0xe1, 0]));
        Packet(0x100, Section([2, 0xb0, 18, 0, 1, 0xc1, 0, 0, 0xe1, 1, 0xf0, 0, 0x24, 0xe1, 1, 0xf0, 0]));
        // Repeat complete coded sequences to give real vout enough lifetime. Only
        // the test container's explicit 25fps timeline is synthetic; no decode/transcode.
        for (int frame = 0; frame < Seconds * 25; frame++)
        {
            long pts = 90000 + (long)frame * 3600;
            byte[] stamp = [(byte)(0x21 | (pts >> 29) & 0x0e), (byte)(pts >> 22),
                (byte)((pts >> 14) & 0xfe | 1), (byte)(pts >> 7), (byte)((pts << 1) & 0xfe | 1)];
            Packet(0x101, Join([0, 0, 1, 0xe0, 0, 0, 0x80, 0x80, 5], stamp, pictures[frame % pictures.Count]), pts);
        }
        return output.ToArray();
    }
    internal static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);
    private static byte[] Matrix() => Join(U32(0x10000), U32(0), U32(0), U32(0), U32(0x10000), U32(0), U32(0), U32(0), U32(0x40000000));
    private static byte[] U16(uint value) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)value); return b; }
    private static byte[] U32(uint value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); return b; }
    private static byte[] Box(string type, params byte[][] data) { var body = Join(data); return Join(U32((uint)body.Length + 8), Text(type), body); }
    private static byte[] Full(string type, uint flags, params byte[][] data) => Box(type, U32(flags), Join(data));
    private static byte[] Join(params byte[][] arrays)
    { var all = new byte[arrays.Sum(array => array.Length)]; int at = 0; foreach (var a in arrays) { a.CopyTo(all, at); at += a.Length; } return all; }
}
