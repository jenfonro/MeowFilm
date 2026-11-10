#ifndef MEOWFILM_VIDEO_KERNEL_H
#define MEOWFILM_VIDEO_KERNEL_H

#include <stdint.h>

#ifdef _WIN32
#define MVK_EXPORT __declspec(dllexport)
#define MVK_CALL __cdecl
#else
#define MVK_EXPORT
#define MVK_CALL
#endif

#define MVK_ABI 2u
#define MVK_IO_BUFFER 65536

/* Every callback is synchronous on libVLC's input thread. No exception may cross
 * this ABI. Handles identify owned HTTP reads, never an address authorization. */
typedef struct {
    uint32_t size;
    uint32_t abi;
    void *opaque;
    int (MVK_CALL *open)(void *, const char *, int64_t, int64_t,
                         uint64_t *, int64_t *, int *, uint32_t *);
    /* open returns the final location's UTF-8 size including NUL. Copying that
     * location reads the existing handle and never repeats the HTTP request. */
    int (MVK_CALL *location)(void *, uint64_t, char *, uint32_t);
    int (MVK_CALL *read)(void *, uint64_t, uint8_t *, int);
    int64_t (MVK_CALL *seek)(void *, uint64_t, int64_t, int);
    void (MVK_CALL *close)(void *, uint64_t);
    int (MVK_CALL *interrupted)(void *);
    void (MVK_CALL *failed)(void *, int);
} mvk_callbacks;

typedef struct {
    uint8_t kid[16];
    uint8_t key[16];
} mvk_clear_key;

enum mvk_format { MVK_AUTO, MVK_PROGRESSIVE, MVK_HLS, MVK_DASH };
enum mvk_error { MVK_OPEN_FAILED = 1, MVK_FORMAT_UNSUPPORTED, MVK_ENCRYPTION_UNSUPPORTED,
                 MVK_READ_FAILED, MVK_SEEK_FAILED };

/* The returned owner is private process-local memory. token is an unpredictable
 * 128-bit lookup identity for the MRL; neither URI nor key enters that MRL. */
MVK_EXPORT int MVK_CALL mvk_create(const mvk_callbacks *, const char *uri, int format,
                                  const mvk_clear_key *, uint32_t key_count,
                                  const uint8_t *default_key, const char *token, void **owner);
MVK_EXPORT void MVK_CALL mvk_cancel(void *owner);
/* Call after cancelling host IO and stopping/disposal of the associated player.
 * Waits for all plugin users; never frees a callback or key while in use. */
MVK_EXPORT void MVK_CALL mvk_release(void *owner);
MVK_EXPORT const char *MVK_CALL mvk_build_identity(void);

#endif
