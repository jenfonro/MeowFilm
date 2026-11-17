#!/usr/bin/env python3
"""Local ffprobe/container/hash checks, not Apple playback or route validation."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ffprobe", required=True)
    args = parser.parse_args()
    tests = Path(__file__).resolve().parent.parent
    repository = tests.parent.parent.parent
    fixtures = tests / "Fixtures"
    manifest = json.loads((fixtures / "manifest.json").read_text())
    for name, expected in manifest["sha256"].items():
        assert sha256(fixtures / name) == expected, name
    for name, expected in manifest["inputs"].items():
        assert sha256(repository / name) == expected, name

    def probe(path, *extra):
        command = [args.ffprobe, "-v", "error", *extra, "-of", "json", str(path)]
        return json.loads(subprocess.check_output(command, text=True))

    entries = "format=format_name,duration:stream=codec_name,codec_type,duration,start_time"
    report = {"scope": "local synthetic fixture checks only; Apple execution pending",
              "ffprobe": subprocess.check_output([args.ffprobe, "-version"], text=True).splitlines()[0],
              "ffprobeSHA256": sha256(Path(args.ffprobe)),
              "fixtures": {}}
    for stem, audio in [("rate-audio-video", True), ("rate-video-only", False)]:
        path = fixtures / "HLS" / f"{stem}.m3u8"
        lines = path.read_text().splitlines()
        assert lines[0] == "#EXTM3U"
        assert "#EXT-X-PLAYLIST-TYPE:VOD" in lines and "#EXT-X-ENDLIST" in lines
        assert "#EXT-X-TARGETDURATION:4" in lines and "#EXT-X-INDEPENDENT-SEGMENTS" in lines
        assert sum(float(line.split(":")[1].rstrip(",")) for line in lines
                   if line.startswith("#EXTINF:")) == 24
        segments = [line for line in lines if line and not line.startswith("#")]
        assert segments == [f"{stem}-{index:03d}.ts" for index in range(6)]
        info = probe(path, "-show_entries", entries)
        assert info["format"]["format_name"] == "hls"
        assert float(info["format"]["duration"]) == 24
        assert {s["codec_name"] for s in info["streams"]} == ({"h264", "aac"} if audio else {"h264"})
        segment_info = []
        for index, segment in enumerate(segments):
            segment_path = fixtures / "HLS" / segment
            data = segment_path.read_bytes()
            assert len(data) % 188 == 0 and all(data[n] == 0x47 for n in range(0, len(data), 188)), segment
            metadata = probe(segment_path, "-show_entries", entries)
            assert metadata["format"]["format_name"] == "mpegts", segment
            assert {s["codec_name"] for s in metadata["streams"]} == ({"h264", "aac"} if audio else {"h264"})
            video = next(s for s in metadata["streams"] if s["codec_type"] == "video")
            assert float(video["duration"]) == 4, segment
            assert abs(float(video["start_time"]) - (1.6 + index * 4)) < 0.001, segment
            frames = probe(segment_path, "-select_streams", "v:0", "-read_intervals", "%+#20",
                           "-show_entries", "frame=key_frame,best_effort_timestamp_time")["frames"]
            assert frames[0]["key_frame"] == 1, segment
            segment_info.append({"name": segment, "videoStart": video["start_time"],
                                 "videoDuration": video["duration"], "firstFrameKeyframe": True})
        report["fixtures"][f"HLS/{stem}.m3u8"] = {"container": "hls", "duration": 24,
                                                "segments": segment_info}
    for name, codec, duration in [("software-mpeg4-video.mkv", "mpeg4", 24),
                                  ("software-video.mkv", "ffv1", 12)]:
        info = probe(fixtures / name, "-show_entries", entries)
        assert "matroska" in info["format"]["format_name"]
        assert float(info["format"]["duration"]) == duration
        assert len(info["streams"]) == 1
        assert info["streams"][0]["codec_name"] == codec
        report["fixtures"][name] = {"container": "matroska", "codec": codec, "duration": duration}
    report["verifiedHashes"] = manifest["sha256"]
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
