# Windows private demux bridge

`NativePlayback` and pinned libVLC 3.0.24 remain the only playback/decode/render
owner. This access-demux plugin feeds FFmpeg 8.1.2 packets and codec parameters
to VLC `es_out`. It is not a decoder, transcoder, remux stream or HTTP server.

The plugin's private MRL contains only an unpredictable registered owner token.
The original address and ClearKey bytes cross `video_kernel.h` in process-local
memory. Registration identifies the resource owner, not URL authorization.
Every root, child and redirect uses native HTTP without origin, address-class or
URL-graph admission; an owned resource lease supplies cancellation and exact release.
`io_open`/`io_close2` and custom AVIO use only the public FFmpeg ABI.
The private callback ABI is version 2. `open` returns the final URL's UTF-8 size
including NUL; `location` copies it from the already-open handle into an exact
native allocation. This has no fixed URL-length gate and does not repeat HTTP.

FFmpeg is built without protocols/network, programs, decoders or encoders.
The locked source adaptation recognizes HTTP scheme names without enabling a
network protocol, preserves custom-AVIO redirect locations and disables XML
external entity access. HLS HTTP keepalive/prefetch is disabled inside FFmpeg;
native HttpClient remains responsible for connection reuse. HLS AES-128 CBC is
streamed through the same controlled IO; legitimate CENC keys are handed only
to the private MOV/DASH demux options.

The synchronous demux runs on VLC's input thread. Cancel interrupts managed IO
before native Stop joins that thread. Close frees all AVIO, and owner release
waits for plugin users before wiping keys or freeing callback memory. Time seek
uses `avformat_seek_file`/`avformat_flush` and VLC `ES_OUT_RESET_PCR` (which
flushes decoder queues); there is no byte-seek fallback masquerading as time.

Run `scripts/windows/build-video-kernel.ps1` on Windows. The Media project builds
and consumes its normal incremental output. Source archives, source adaptations,
SDK ABI, architecture, configuration and source fingerprints are recorded in the
receipt and native/managed build identity. The DLL is copied through MSBuild,
not by a developer's manual installation.

The toolchain's static `libwinpthread.a` supplies FFmpeg's `clock_gettime` and
`nanosleep`; its exact bytes are bound into the identity and receipt. It is not
an additional shipped DLL. Preserve its mingw-w64 winpthreads MIT and Lockless
BSD notices alongside the GCC runtime exception and MinGW CRT notices.

Sources and redistribution inputs are pinned in `video-kernel.lock.json`.
FFmpeg, libxml2 and VLC SDK licenses, native object files/static archives and
build receipt stay in the build cache for the main App license/relink packaging
owner. This block does not modify the application's license index. Real HDR,
hardware decode and commercial DRM/CDM support are not inferred from demux or
the library versions.
