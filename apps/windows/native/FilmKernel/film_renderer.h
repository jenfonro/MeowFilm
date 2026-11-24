#ifndef MEOWFILM_FILM_RENDERER_H
#define MEOWFILM_FILM_RENDERER_H

#include <functional>
#include <memory>
#include <string>
#include "film_kernel.h"
extern "C" {
#include <libavcodec/avcodec.h>
#include <libavutil/buffer.h>
}
struct AVStream;

/* All methods are called under the kernel output mutex. Frames are borrowed
 * only for the call. The stream remains alive until this renderer is released.
 * Strictly read-only detection of Windows display HDR state (never forces HDR
 * on). HDR/DoVi sources output native FP16 scRGB when display HDR is enabled,
 * and seamlessly tone-map via libplacebo to SDR when display HDR is off or on
 * SDR monitors. Pure SDR sources use standard 8-bit SDR swapchains. */
class FilmRenderer {
public:
    using LogFn = std::function<void(int level, const char *component, const char *message)>;
    struct InspectInfo {
        int native_hdr = 0;
        int tone_mapped = 0;
        int display_hdr_enabled = 0;
        int dolby_vision = 0;
        int display_width = 0;
        int display_height = 0;
    };

    FilmRenderer();
    ~FilmRenderer();
    int open(void *display_window, LogFn logger = {});
    void set_stream(const AVStream *stream);
    AVBufferRef *hardware_device() const;
    int attach(const mfk_surface *surface, void **swapchain);
    int surface_update(const mfk_surface &surface);
    int inspect(const AVFrame *base, const AVFrame *enhancement, InspectInfo *info = nullptr);
    int present(const AVFrame *base, const AVFrame *enhancement, double aspect,
                double position);
    int subtitle_header(const AVCodecContext *codec);
    // Subtitle errors are isolated and never terminate video playback.
    int subtitle(const AVSubtitle &subtitle, double pts, double stream_start,
                 int canvas_width = 0, int canvas_height = 0);
    // Font attachments are batched; call finish_attachments() once after adding all.
    void add_attachment(const char *name, const char *mime, const uint8_t *bytes, size_t size);
    void finish_attachments();
    void clear_subtitles();

    int native_hdr() const;
    int tone_mapped() const;
    int display_hdr_enabled() const;
    int dolby_vision() const;
    int display_width() const;
    int display_height() const;
    uint64_t submitted_pictures() const;
    uint64_t subtitle_errors() const;
    std::string transfer_fallback_reason() const;
private:
    struct Impl;
    std::unique_ptr<Impl> impl;
};
#endif
