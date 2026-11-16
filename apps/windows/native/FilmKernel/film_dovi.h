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

/* Resolves the single unambiguous P7 BL/EL track pair using DOVI configuration
 * flags, not track order. Normalizes dual-track access units to the single-track
 * NAL63 contract below. Pending packets are bounded and retain BL decode order;
 * BL/EL timestamps must match exactly. Serialized by the decode owner. */
class FilmDoviInput {
public:
    FilmDoviInput();
    ~FilmDoviInput();
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
 * Calls are serialized by the decode owner. Requires ffmpeg-dovi.patch. */
class FilmDovi {
public:
    FilmDovi();
    ~FilmDovi();
    int open(const AVCodecParameters *parameters, AVRational time_base);
    // MFK_OK consumes the packet; AVERROR(EAGAIN) does NOT consume it. Release
    // queued base/EL pairs and retry the SAME packet (nullptr requests drain).
    int send(const AVPacket *packet);
    AVFrame *take(double pts); // Absolute seconds; owned frame, not a readiness test.
    bool required(const AVFrame *base) const;
    bool enabled() const;
    // Preferred rendering boundary: validates RPU/metadata, uses integer PTS,
    // and returns an owned EL only if residuals need it. MFK_OK retires base;
    // EAGAIN means more decode input is needed; a missing EL after decoder EOF
    // or after its output PTS has passed base is MFK_DOVI_UNSUPPORTED.
    // Pair bases in presentation order; never render a later base past EAGAIN.
    int pair(const AVFrame *base, AVFrame **enhancement);
    int status() const; // Latched error, including errors encountered by take().
    // Absolute stream ticks, inclusive lower bound. Use the first retained BL's
    // PTS, not the seek target (a retained frame can cover that target).
    // Releases preroll ELs even if the caller discards the corresponding BLs.
    int discard_before(int64_t pts);
    void flush();
private:
    struct Impl;
    std::unique_ptr<Impl> impl;
};
#endif
