#!/usr/bin/env python3
"""Synthetic public test media only; requires a local ffmpeg fixture tool.

This tool is NOT linked/shipped in the product. Production FFmpeg is built from
the independent reviewed lock. No real URL, credential, movie or license data.
"""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import subprocess


def boxes(data, start=0, end=None):
    end = len(data) if end is None else end
    while start < end:
        size = int.from_bytes(data[start:start + 4], "big")
        assert size >= 8 and start + size <= end
        yield data[start + 4:start + 8], start, size
        start += size


def box(kind, data):
    return struct.pack(">I", len(data) + 8) + kind + data


def merge_kids(video, audio, output):
    """Combine two source-generated CENC tracks, preserving their KID/aux data."""
    v = video.read_bytes()
    a = audio.read_bytes()
    vt = {kind: v[start:start + size] for kind, start, size in boxes(v)}
    at = {kind: a[start:start + size] for kind, start, size in boxes(a)}
    prefix = vt[b"ftyp"] + vt[b"mdat"] + at[b"mdat"]
    vdata_start = next(start for kind, start, size in boxes(v) if kind == b"mdat")
    adata_start = next(start for kind, start, size in boxes(a) if kind == b"mdat")
    shifts = [len(vt[b"ftyp"]) - vdata_start,
              len(vt[b"ftyp"]) + len(vt[b"mdat"]) - adata_start]
    tracks = []
    mvhd = None
    for index, source in enumerate([vt[b"moov"], at[b"moov"]]):
        for kind, start, size in boxes(source, 8):
            child = bytearray(source[start:start + size])
            if kind == b"mvhd" and index == 0:
                child[-4:] = (3).to_bytes(4, "big"); mvhd = bytes(child)
            if kind != b"trak":
                continue
            def rewrite(data, start=0, end=None):
                for kind, offset, size in boxes(data, start, end):
                    if kind in [b"trak", b"mdia", b"minf", b"stbl"]:
                        rewrite(data, offset + 8, offset + size)
                    elif kind == b"tkhd":
                        id_offset = offset + (28 if data[offset + 8] == 1 else 20)
                        data[id_offset:id_offset + 4] = (index + 1).to_bytes(4, "big")
                    elif kind == b"stco":
                        count = int.from_bytes(data[offset + 12:offset + 16], "big")
                        for number in range(count):
                            pos = offset + 16 + number * 4
                            value = int.from_bytes(data[pos:pos + 4], "big") + shifts[index]
                            data[pos:pos + 4] = value.to_bytes(4, "big")
            rewrite(child)
            tracks.append(child)
    moov = bytearray(box(b"moov", mvhd + b"".join(tracks)))
    def fix_aux(start, end):
        children = list(boxes(moov, start, end))
        senc = next((offset for kind, offset, size in children if kind == b"senc"), None)
        for kind, offset, size in children:
            if kind in [b"trak", b"mdia", b"minf", b"stbl"]:
                fix_aux(offset + 8, offset + size)
            elif kind == b"saio":
                assert senc is not None and moov[offset + 8] == 0
                moov[offset + 16:offset + 20] = (len(prefix) + senc + 16).to_bytes(4, "big")
    fix_aux(8, len(moov))
    output.write_bytes(prefix + moov)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ffmpeg", required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent / "VideoKernelTests/Fixtures"
    root.mkdir(parents=True, exist_ok=True)
    commands = []
    def run(*values):
        command = [args.ffmpeg, "-nostdin", "-hide_banner", "-loglevel", "error", "-y", *map(str, values)]
        subprocess.run(command, cwd=root, check=True)
        commands.append(command[1:])
    run("-f", "lavfi", "-i", "testsrc2=size=96x64:rate=10:duration=3",
        "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=3",
        "-c:v", "libx264", "-threads", "2", "-pix_fmt", "yuv420p", "-g", "10",
        "-bf", "2", "-c:a", "aac", "-b:a", "48k", "-movflags", "+faststart", "ordinary.mp4")
    run("-i", "ordinary.mp4", "-c", "copy", "-hls_time", "1", "-hls_list_size", "0",
        "-hls_segment_filename", "hls-%d.ts", "variant.m3u8")
    (root / "master.m3u8").write_text('#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=160000,RESOLUTION=96x64\nvariant.m3u8\n')
    run("-i", "ordinary.mp4", "-c", "copy", "-f", "dash", "-seg_duration", "1",
        "-use_template", "1", "-use_timeline", "1", "ordinary.mpd")
    run("-f", "lavfi", "-i", "testsrc2=size=96x64:rate=5:duration=2",
        "-c:v", "libx265", "-threads", "2", "-pix_fmt", "yuv420p10le",
        "-x265-params", "pools=1:frame-threads=1:log-level=error:keyint=5",
        "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc",
        "-tag:v", "hvc1", "-movflags", "+faststart", "main10.mp4")
    run("-f", "lavfi", "-i", "testsrc2=size=96x64:rate=5:duration=2",
        "-c:v", "libx265", "-threads", "2", "-pix_fmt", "yuv420p10le",
        "-x265-params", "pools=1:frame-threads=1:log-level=error:keyint=5:"
        "master-display=G(8500,39850)B(6550,2300)R(35400,14600)WP(15635,16450)L(10000000,1):max-cll=1000,400",
        "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc",
        "-tag:v", "hvc1", "-movflags", "+faststart", "main10-hdr.mp4")
    # Public synthetic 128-bit test material, not an account/license.
    kid1, key1 = bytes(range(16)).hex(), bytes(range(16, 32)).hex()
    kid2, key2 = bytes(range(32, 48)).hex(), bytes(range(48, 64)).hex()
    run("-i", "ordinary.mp4", "-c", "copy", "-encryption_scheme", "cenc-aes-ctr",
        "-encryption_key", key1, "-encryption_kid", kid1, "cenc.mp4")
    run("-i", "ordinary.mp4", "-an", "-c", "copy", "-encryption_scheme", "cenc-aes-ctr",
        "-encryption_key", key1, "-encryption_kid", kid1, "cenc-video.mp4")
    run("-i", "ordinary.mp4", "-vn", "-c", "copy", "-encryption_scheme", "cenc-aes-ctr",
        "-encryption_key", key2, "-encryption_kid", kid2, "cenc-audio.mp4")
    merge_kids(root / "cenc-video.mp4", root / "cenc-audio.mp4", root / "cenc-multikid.mp4")
    # Front-loaded metadata allows these single-segment DASH representations
    # to be consumed through the demuxer's streaming custom AVIO.
    run("-i", "ordinary.mp4", "-an", "-c", "copy", "-movflags", "+faststart",
        "-encryption_scheme", "cenc-aes-ctr", "-encryption_key", key1,
        "-encryption_kid", kid1, "cenc-dash-video.mp4")
    run("-i", "ordinary.mp4", "-vn", "-c", "copy", "-movflags", "+faststart",
        "-encryption_scheme", "cenc-aes-ctr", "-encryption_key", key2,
        "-encryption_kid", kid2, "cenc-dash-audio.mp4")
    (root / "cenc.mpd").write_text(
        '<?xml version="1.0"?>\n'
        '<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" '
        'mediaPresentationDuration="PT3S" minBufferTime="PT1S" '
        'profiles="urn:mpeg:dash:profile:isoff-on-demand:2011">\n'
        '<Period duration="PT3S">\n'
        '<AdaptationSet contentType="video" mimeType="video/mp4">'
        '<ContentProtection schemeIdUri="urn:mpeg:dash:mp4protection:2011" value="cenc"/>'
        '<Representation id="0" codecs="avc1.64000a" bandwidth="60000" width="96" height="64">'
        '<BaseURL>cenc-dash-video.mp4</BaseURL></Representation></AdaptationSet>\n'
        '<AdaptationSet contentType="audio" mimeType="audio/mp4">'
        '<ContentProtection schemeIdUri="urn:mpeg:dash:mp4protection:2011" value="cenc"/>'
        '<Representation id="1" codecs="mp4a.40.2" bandwidth="48000" audioSamplingRate="48000">'
        '<BaseURL>cenc-dash-audio.mp4</BaseURL></Representation></AdaptationSet>\n'
        '</Period></MPD>\n')
    files = {file.name: hashlib.sha256(file.read_bytes()).hexdigest()
             for file in root.iterdir() if file.name != "manifest.json"}
    (root / "manifest.json").write_text(json.dumps({
        "generator": subprocess.check_output([args.ffmpeg, "-version"], text=True).splitlines()[0],
        "purpose": "synthetic decoder contract fixtures, not a production dependency",
        "commands": commands, "sha256": files
    }, indent=2) + "\n")


if __name__ == "__main__":
    main()
