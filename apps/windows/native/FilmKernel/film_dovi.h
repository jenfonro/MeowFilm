#ifndef MEOWFILM_FILM_DOVI_H
#define MEOWFILM_FILM_DOVI_H

#include <cstddef>
#include <memory>
#include <vector>
#include "film_kernel.h"
extern "C" {
#include <libavcodec/avcodec.h>
}
struct AVFormatContext;

namespace FilmDoviNal {
struct Packet {
    std::vector<uint8_t> enhancement; // Annex B, without the outer NAL 63 header.
    bool base_picture = false, enhancement_picture = false, rpu = false;
};
// Transactional: a malformed NAL leaves output unchanged. length is 0 for
// Annex B, or 1..4 for length-prefixed packets. No RBSP unescaping is performed.
bool split(const uint8_t *data, size_t size, int length, Packet &output);
// hvcC or Annex B. enhancement_only is for the demuxer's hvcE side data, never
// for BL extradata: BL parameter sets must not initialize the EL decoder.
bool configuration(const uint8_t *data, size_t size, int &length,
                   std::vector<uint8_t> &output, bool enhancement_only = false);
}

/* Resolves a P7 BL/EL track pair using DOVI configuration flags, normalizing
 * dual-track access units to the single-track NAL63 contract below. Ordinary
 * non-DV or single-track inputs pass through unchanged. Serialized by caller. */
class FilmDoviInput {
public:
    FilmDoviInput();
    ~FilmDoviInput();
    FilmDoviInput(FilmDoviInput &&) noexcept;
    FilmDoviInput &operator=(FilmDoviInput &&) noexcept;
    int open(const AVFormatContext *format, int preferred_video);
    int base_index() const;
    int enhancement_index() const; // -1 for ordinary/single-track input.
    const AVCodecParameters *parameters() const;
    int send(const AVPacket *packet); // Borrows/clones a BL or EL packet.
    int take(AVPacket **packet); // Owned merged packet, nullptr if awaiting its pair.
    int finish() const; // EOF with unmatched access units is not BL fallback.
    void flush();
private:
    struct Impl;
    std::unique_ptr<Impl> impl;
};

/* Retains and decodes Profile 7's enhancement layer, pairs it by PTS with
 * reordered base frames, and clears both decode and pairing state on seek.
 * Ordinary non-DV HEVC streams bypass strict DV validation cleanly.
 * Calls are serialized by the video decode worker. Requires ffmpeg-dovi.patch. */
class FilmDovi {
public:
    FilmDovi();
    ~FilmDovi();
    FilmDovi(FilmDovi &&) noexcept;
    FilmDovi &operator=(FilmDovi &&) noexcept;
    int open(const AVCodecParameters *parameters, AVRational time_base);
    // MFK_OK consumes the packet; AVERROR(EAGAIN) does NOT consume it. Release
    // queued base/EL pairs and retry the SAME packet (nullptr requests drain).
    int send(const AVPacket *packet);
    // Validates RPU/metadata, uses integer PTS, and returns an owned EL only if
    // residuals need it. MFK_OK retires base; EAGAIN means more input needed.
    int pair(const AVFrame *base, AVFrame **enhancement);
    // Absolute stream ticks, inclusive lower bound. Releases preroll ELs.
    int discard_before(int64_t pts);
    void flush();
private:
    struct Impl;
    std::unique_ptr<Impl> impl;
};
#endif
