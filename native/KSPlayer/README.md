# KSPlayer core-only source package

This local package exports the `KSPlayer` product/module. Its playback, decode,
audio, render, subtitle and track implementation comes from KSPlayer
`d0d063ab1f045735129b6617591ad2148d88d5db`; upstream controls, SwiftUI and demos
are not compilation inputs. It is not a wrapper around the former C decoder.

## Source preparation

From the repository root:

```sh
python3 -I -B scripts/prepare-ios-video-kernel.py --sources-only
python3 -I -B scripts/prepare-ios-video-kernel.py --target macos-x86_64
python3 -I -B scripts/prepare-ios-video-kernel.py --verify --target macos-x86_64
```

Select the locked iOS device/simulator target for those builds. The six FFmpeg
XCFramework inputs are generated from the locked FFmpeg 8.1.2 sources, not
downloaded binary releases. FFmpegKit `c32be9bfb628042737ad3ef622e930c5c7b15954`
is a source-only C/header bridge; none of its prebuilt dependencies is used.
`scripts/ios-video-kernel.json` locks all sources and patches.
`Provenance/source-manifest.json` and `patches/core-only.patch` reconstruct the
exact compiled package inputs from the verified upstream bytes.

## Product integration

- `KSOptions.inputFormat: String?` is an FFmpeg demuxer name, passed to
  `av_find_input_format`. Unknown names fail preparation. `"hls"` enables all
  segment extensions on that input only; it does not inspect URL suffixes.
- HTTP options/headers belong to each `KSOptions` instance. Response
  `Set-Cookie` is ignored by the local FFmpeg patch; explicit request Cookie is
  retained. No process-global proxy or Cookie state is installed by the core.
- `prepareToPlay()` does not start audio or the playback clock.
  `waitForFirstVideoFrame()` waits for real native displayed pixels or a Metal
  drawable presentation without playing audio. Task cancellation affects that
  wait only. `cancelPendingFrame()` cancels existing waits, not future ones.
- `seek(time:completion:)` completes on the main queue after the native call
  returns. A superseded/cancelled request receives its own `false` result.
  Fractional seconds are preserved and pending target positions are not exposed
  as actual output time. Frames and packets carry a seek generation.
- `cancelSeek()` interrupts both native seeking and subsequent input feeding,
  even if the native seek has already returned. It does not close the session
  or roll back automatically. The product pauses output and must explicitly
  seek again to rebuild feeding. Seek/flush invalidate old first-frame evidence.
- `onDisplayLayerChanged` reports replacement of the actual
  `AVSampleBufferDisplayLayer` so the product PiP owner can rebind.
- `shutdownAndWait()` is the terminal asynchronous drain operation: input and
  decoder executors, VideoToolbox callbacks and audio callbacks are joined.
  `shutdown()` by itself is not a drain signal.
- Decode/probe failures and AudioEngine start failure reach the player delegate
  as fixed-classification errors; raw media/network diagnostics are suppressed.
  The product owns AVAudioSession, Now Playing and PiP. The core never activates
  or configures those system sessions, nor creates its own PiP controller.

## Native HDR metadata

The native pixel-buffer path retains ten-bit storage and color attachments.
Mastering-display metadata is serialized in ST 2086 primary order and units,
with content-light levels attached separately. Partial or out-of-range
mastering metadata is omitted rather than invented or converted with a
trapping integer cast. `HDRMetadataCoreTests` verifies those bytes and failure
boundaries; decoded-fixture tests inspect real pixel-buffer attachments. These
checks do not establish a device's HDR display output.

## Dolby Vision native path

HEVC tracks with a Dolby Vision configuration use the core's whole-packet
VideoToolbox decoder even when `asynchronousDecompression` is false. The
compressed format uses `kCMVideoCodecType_DolbyVisionHEVC`, `hvcC` and a 24-byte
`dvcC`/`dvvC` payload serialized by the locked FFmpeg
`ff_isom_put_dvcc_dvvc`; the C structure itself is never used as an atom.
Annex B parameter sets use CoreMedia's HEVC configuration generator. Packet
conversion retains every NAL, including RPU and SEI. Decoded native pixel-buffer
metadata is passed to the system sample-buffer display path without requesting
a different destination dynamic range.

Enhancement layers/FEL and profile 7 composition are unsupported, not played as
a base-layer substitute. Invalid configuration, native session creation or
native decode failure is terminal for that track; neither the default FFmpeg
decoder nor an HDR base path is used as a fallback. Other single-layer HEVC
configurations are offered to the native decoder; this does not establish
profile/device support. Non-native display modes cannot substitute for DV
output.

`DolbyVisionCoreTests` covers configuration bytes, default routing, the
enhancement-layer rejection and NAL payload preservation. It does not decode a
real Dolby Vision sample or verify device display output.

## Validation boundary

Source reconstruction and Python preparation/cache regressions do not establish
Swift compilation or playback correctness. The parent integration runs the
actual Mac builds and playback tests. HDR/Dolby Vision paths are retained; no
claim of complete profile/device output validation follows from source import
or successful compilation. Third-party licenses and source notices are owned by
the repository's `LICENSES/` files, not duplicated here.
