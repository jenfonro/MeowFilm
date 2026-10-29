namespace MeowFilm.Media;

// Fixed binary demux candidates only. Never trust a filename, Content-Type, title,
// URL suffix, ID3 metadata or content supplied as libVLC options. ",none" forbids
// fallback to playlist/adaptive auto-detection when the chosen demux rejects data.
internal static class OfflineMediaFormat
{
    internal static string Probe(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8))
        {
            if (bytes.Slice(8, 4).SequenceEqual("WAVE"u8)) return "wav";
            if (bytes.Slice(8, 4).SequenceEqual("AVI "u8)) return "avi";
        }
        if (bytes.Length >= 12 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8)) return "mp4";
        if (bytes.StartsWith("fLaC"u8)) return "flac";
        if (bytes.StartsWith("OggS"u8)) return "ogg";
        if (bytes.StartsWith(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 })) return "mkv";
        if (bytes.StartsWith("ID3"u8)) return "mpga";
        if (bytes.Length >= 4 && bytes[0] == 0xff)
        {
            if ((bytes[1] & 0xf6) == 0xf0) return "mp4a";
            if ((bytes[1] & 0xe0) == 0xe0 && (bytes[1] & 6) != 0) return "mpga";
        }
        throw new PlaybackException(PlaybackError.ReadFormatUnsupported);
    }
}
