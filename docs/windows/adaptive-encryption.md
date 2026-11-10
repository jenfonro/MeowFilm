# HLS AES-128 — Windows native playback

## Supported inputs

This document describes identity AES-128 HLS VOD and the bounded
[live HLS profile](live-hls.md) in the existing `AdaptiveMediaSession` path.
The separate film typed-input path uses the pinned FFmpeg private demux bridge
for legitimate ClearKey; see [native media playback](media-playback.md).
It does not broaden the supported inputs or trust boundary listed below.

Supported inputs:

- supported media/master playlists;
- MPEG-TS and the supported fragmented MP4 profile;
- explicit 128-bit IVs and media-sequence-derived IVs;
- key rotation, `METHOD=NONE`, and encrypted initialization sections with an
  explicit IV;
- full-segment byte ranges already accepted by the native adaptive reader.

Unsupported inputs fail before native trust:

- low-latency playlists and live profiles outside the supported bounds;
- `SAMPLE-AES`, non-identity key formats, DRM, and protected MP4;
- malformed keys, IVs, padding, ciphertext sizes, nested playlists in decrypted
  payloads, unsupported non-HTTP key URLs, and
  initialization sections that require an implicit IV.

## Ownership and security

The adaptive session owns manifest parsing, key retrieval, decryption, validation,
the existing local representation service, cancellation, and key-buffer cleanup. LibVLC receives
only validated plaintext resources; it never receives key bytes or key URLs.

Key and media requests use the same bounded HTTP policy:

- explicit caller headers follow the input's redirects and child requests without origin stripping;
- no domain/IP classification, loopback-origin rule or URL-permission graph blocks HTTP(S) input;
- URI/HTTP/container/range parsing, cancellation and representation consistency remain enforced;
- response cookies are not stored or replayed, and keys/headers/addresses do not enter logs.

Keys must be exactly 16 bytes. AES-CBC uses PKCS#7 padding and the full media
sequence value when deriving an implicit IV. Key buffers are zeroed after admitted
readers drain. This is best-effort ownership cleanup, not a forensic erasure claim.

## Streaming and cancellation

Decryption is streamed and bounded. The reader does not accumulate a complete
segment or media file. Plaintext range requests restart CBC from the segment IV
and skip within the decrypted stream; ciphertext and plaintext offsets are never
interchanged.

Cancellation, replacement, stop, and disposal revoke the current generation,
drain admitted I/O, release the native session, and prevent old callbacks from
retiring a newer playback generation. A rejected input cannot publish media state.
