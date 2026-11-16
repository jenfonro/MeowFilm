#ifndef MEOWFILM_FILM_RENDERER_H
#define MEOWFILM_FILM_RENDERER_H

#include <memory>
#include "film_kernel.h"
extern "C" {
#include <libavcodec/avcodec.h>
#include <libavutil/buffer.h>
}
struct AVStream;

/* All methods are called under the kernel output mutex. Frames are borrowed
 * only for the call. The stream remains alive until this renderer is released.
 * open creates the shared decoder/render device without a visible swapchain;
 * attach creates the presentation target at commit. Decoder and renderer use
 * the same immediate context and recursive device lock. Device loss requires
 * reopening the owner, including its decoders. */
class FilmRenderer {
public:
    FilmRenderer();
    ~FilmRenderer();
    int open(void *hwnd);
    void set_stream(const AVStream *stream);
    AVBufferRef *hardware_device() const; // Borrowed; decoder takes av_buffer_ref.
    // Null releases only the swapchain for a fenced owner handoff.
    int attach(void *hwnd);
    int inspect(const AVFrame *base, const AVFrame *enhancement);
    int present(const AVFrame *base, const AVFrame *enhancement, double aspect,
                double position);
    int subtitle_header(const AVCodecContext *codec);
    // Pass the decoder's current canvas after decoding (not the video size).
    int subtitle(const AVSubtitle &subtitle, double pts,
                 int canvas_width = 0, int canvas_height = 0);
    // Font attachments are copied and survive track replacement and seeking.
    int add_attachment(const char *name, const char *mime, const uint8_t *bytes, size_t size);
    void clear_subtitles();
    int native_hdr() const;
    int dolby_vision() const;
    uint64_t submitted_pictures() const;
private:
    struct Impl;
    std::unique_ptr<Impl> impl;
};
#endif
