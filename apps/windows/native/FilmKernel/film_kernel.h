#ifndef MEOWFILM_FILM_KERNEL_H
#define MEOWFILM_FILM_KERNEL_H

#include <stdint.h>

#ifdef _WIN32
#define MFK_EXPORT __declspec(dllexport)
#define MFK_CALL __cdecl
#else
#define MFK_EXPORT
#define MFK_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define MFK_ABI 3u
#define MFK_IO_BUFFER 65536

enum mfk_log_level {
    MFK_LOG_ERROR = 1,
    MFK_LOG_WARN = 2,
    MFK_LOG_INFO = 3,
    MFK_LOG_DEBUG = 4
};

/* Synchronous callbacks on kernel threads. The caller retains the callbacks
 * until release returns; no exception may cross this boundary.
 * log is optional and may be NULL. */
typedef struct {
    uint32_t size, abi;
    void *opaque;
    int (MFK_CALL *open)(void *, const char *, int64_t, int64_t,
                        uint64_t *, int64_t *, int *, uint32_t *);
    int (MFK_CALL *location)(void *, uint64_t, char *, uint32_t);
    int (MFK_CALL *read)(void *, uint64_t, uint8_t *, int);
    int64_t (MFK_CALL *seek)(void *, uint64_t, int64_t, int);
    void (MFK_CALL *close)(void *, uint64_t);
    int (MFK_CALL *interrupted)(void *);
    void (MFK_CALL *failed)(void *, int);
    void (MFK_CALL *log)(void *, int level, const char *component, const char *message);
} mfk_callbacks;

typedef struct { uint8_t kid[16], key[16]; } mfk_clear_key;
enum mfk_format { MFK_AUTO, MFK_PROGRESSIVE, MFK_HLS, MFK_DASH };
enum mfk_state {
    MFK_NEW, MFK_PREPARED, MFK_PLAYING, MFK_PAUSED, MFK_BUFFERING,
    MFK_ENDED, MFK_FAILED
};
enum mfk_error {
    MFK_OK, MFK_OPEN_FAILED, MFK_FORMAT_UNSUPPORTED, MFK_ENCRYPTION_UNSUPPORTED,
    MFK_READ_FAILED, MFK_SEEK_FAILED, MFK_DECODE_FAILED, MFK_AUDIO_FAILED,
    MFK_RENDER_FAILED, MFK_HDR_DISPLAY_UNSUPPORTED, MFK_DOVI_UNSUPPORTED,
    MFK_CANCELLED, MFK_INVALID_REQUEST, MFK_DEVICE_LOST
};
enum mfk_track_kind { MFK_VIDEO = 1, MFK_AUDIO = 2, MFK_SUBTITLE = 3 };

typedef struct {
    uint32_t size, abi;
    int32_t state, error;
    double position, duration, buffered_until;
    int32_t width, height;
    int32_t video_track, audio_track, subtitle_track;
    int32_t native_hdr, tone_mapped, dolby_vision, hardware_decoding;
    int32_t seekable, muted, audio_degraded;
    uint64_t submitted_pictures;
} mfk_snapshot;

typedef struct {
    uint32_t size, abi;
    uint64_t dropped_frames;
    uint64_t audio_underruns;
    uint64_t audio_reconnects;
    uint64_t subtitle_errors;
    double av_sync_diff;
    double render_fps;
    int32_t video_packet_queue;
    int32_t audio_packet_queue;
    int32_t subtitle_packet_queue;
    int32_t picture_queue;
    int32_t hardware_fallback;
    int32_t audio_degraded;
    int32_t tone_mapped;
    int32_t display_hdr_enabled;
    char decode_fallback_reason[64];
    char last_native_error[128];
} mfk_diagnostics;

typedef struct {
    uint32_t size;
    int32_t index, kind, selected;
    char codec[32], language[32], title[128];
} mfk_track;

typedef struct {
    uint32_t size;
    int32_t index;
    double start, end;
    char title[256];
} mfk_chapter;

/* The UI owns layout and visibility. Dimensions are physical pixels (zero
 * suspends presentation); scale is SwapChainPanel.CompositionScaleX/Y.
 * display_window is a top-level HWND used only for read-only display
 * capability queries, never a picture target. Updates may use null only while
 * visible is zero; attach always requires a valid display_window.
 * The kernel copies this structure during the call. */
typedef struct {
    uint32_t size, abi;
    void *display_window;
    int32_t width, height;
    float scale_x, scale_y;
    int32_t visible;
    int32_t reserved;
} mfk_surface;

/* URI, headers and keys never appear in a public lookup token or log. create
 * copies key bytes. Each owner has independent IO, decode and output state. */
MFK_EXPORT int MFK_CALL mfk_create(const mfk_callbacks *, const char *uri, int format,
    const mfk_clear_key *, uint32_t count, const uint8_t *default_key, void **owner);
/* prepare is silent and blocking. cancel interrupts it. An optional top-level
 * HWND selects the decoder/render adapter; null selects the default adapter. */
MFK_EXPORT int MFK_CALL mfk_prepare(void *owner, double position, void *display_window);
/* Creates a composition swapchain (SDR or FP16/scRGB HDR according to source
 * and read-only display HDR state) without presenting a frame. swapchain is
 * required and receives an AddRef'd IDXGISwapChain1 on success; the caller
 * releases its reference after UI-thread SetSwapChain. Reattaching an attached
 * owner is invalid: surface updates preserve the existing COM identity.
 * A null surface drains/releases the target, retaining decoder/clock/picture.
 * The UI must SetSwapChain(null) before detach or owner release. */
MFK_EXPORT int MFK_CALL mfk_attach(void *owner, const mfk_surface *, void **swapchain);
/* Resize/scale/visibility/monitor/HDR changes preserve the swapchain COM
 * identity. After mounting an already committed owner, call this to repaint
 * its retained frame. A newly prepared owner starts only after SetSwapChain. */
MFK_EXPORT int MFK_CALL mfk_surface_update(void *owner, const mfk_surface *);
MFK_EXPORT int MFK_CALL mfk_start(void *owner, int paused);
MFK_EXPORT int MFK_CALL mfk_pause(void *owner, int paused);
MFK_EXPORT int MFK_CALL mfk_seek(void *owner, double position);
MFK_EXPORT int MFK_CALL mfk_rate(void *owner, double rate);
MFK_EXPORT int MFK_CALL mfk_volume(void *owner, double volume);
MFK_EXPORT int MFK_CALL mfk_mute(void *owner, int muted);
MFK_EXPORT int MFK_CALL mfk_aspect(void *owner, double ratio);
MFK_EXPORT int MFK_CALL mfk_select_track(void *owner, int kind, int index);
MFK_EXPORT int MFK_CALL mfk_snapshot_read(void *owner, mfk_snapshot *);
MFK_EXPORT int MFK_CALL mfk_diagnostics_read(void *owner, mfk_diagnostics *);
MFK_EXPORT int MFK_CALL mfk_track_count(void *owner);
MFK_EXPORT int MFK_CALL mfk_track_read(void *owner, int ordinal, mfk_track *);
MFK_EXPORT int MFK_CALL mfk_chapter_count(void *owner);
MFK_EXPORT int MFK_CALL mfk_chapter_read(void *owner, int ordinal, mfk_chapter *);
MFK_EXPORT void MFK_CALL mfk_cancel(void *owner);
/* Cancels, joins all actual workers, drains GPU/HTTP references, then releases.
 * Never free caller callbacks, transport or a resource lease before it returns. */
MFK_EXPORT void MFK_CALL mfk_release(void *owner);
MFK_EXPORT const char *MFK_CALL mfk_build_identity(void);

#ifdef __cplusplus
}
#endif
#endif
