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

#define MFK_ABI 1u
#define MFK_IO_BUFFER 65536

/* Synchronous callbacks on kernel threads. The caller retains the callbacks
 * until release returns; no exception may cross this boundary. */
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
} mfk_callbacks;

typedef struct { uint8_t kid[16], key[16]; } mfk_clear_key;
enum mfk_format { MFK_AUTO, MFK_PROGRESSIVE, MFK_HLS, MFK_DASH };
enum mfk_state {
    MFK_NEW, MFK_PREPARED, MFK_PLAYING, MFK_PAUSED, MFK_BUFFERING,
    MFK_ENDED, MFK_FAILED, MFK_STOPPED
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
    int32_t width, height, audio_track, subtitle_track;
    int32_t native_hdr, dolby_vision, hardware_decoding, seekable;
    uint64_t submitted_pictures;
} mfk_snapshot;

typedef struct {
    uint32_t size;
    int32_t index, kind, selected;
    char codec[32], language[32], title[128];
} mfk_track;

/* URI, headers and keys never appear in a public lookup token or log. create
 * copies key bytes. Each owner has independent IO, decode and output state. */
MFK_EXPORT int MFK_CALL mfk_create(const mfk_callbacks *, const char *uri, int format,
    const mfk_clear_key *, uint32_t count, const uint8_t *default_key, void **owner);
/* prepare is silent and blocking. cancel interrupts it. HWND is the intended
 * native picture target, not a WPF bitmap or an upstream player control. */
MFK_EXPORT int MFK_CALL mfk_prepare(void *owner, double position, void *hwnd);
/* A null target releases the swapchain, retaining decoder/clock/last picture.
 * Only one committed owner may hold a flip swapchain for a picture HWND. */
MFK_EXPORT int MFK_CALL mfk_attach(void *owner, void *hwnd);
MFK_EXPORT int MFK_CALL mfk_start(void *owner, int paused);
MFK_EXPORT int MFK_CALL mfk_pause(void *owner, int paused);
MFK_EXPORT int MFK_CALL mfk_seek(void *owner, double position);
MFK_EXPORT int MFK_CALL mfk_rate(void *owner, double rate);
MFK_EXPORT int MFK_CALL mfk_volume(void *owner, double volume);
MFK_EXPORT int MFK_CALL mfk_aspect(void *owner, double ratio);
MFK_EXPORT int MFK_CALL mfk_select_track(void *owner, int kind, int index);
MFK_EXPORT int MFK_CALL mfk_snapshot_read(void *owner, mfk_snapshot *);
MFK_EXPORT int MFK_CALL mfk_track_count(void *owner);
MFK_EXPORT int MFK_CALL mfk_track_read(void *owner, int ordinal, mfk_track *);
MFK_EXPORT void MFK_CALL mfk_cancel(void *owner);
/* Cancels, joins all actual workers, drains GPU/HTTP references, then releases.
 * Never free caller callbacks, transport or a resource lease before it returns. */
MFK_EXPORT void MFK_CALL mfk_release(void *owner);
MFK_EXPORT const char *MFK_CALL mfk_build_identity(void);

#ifdef __cplusplus
}
#endif
#endif
