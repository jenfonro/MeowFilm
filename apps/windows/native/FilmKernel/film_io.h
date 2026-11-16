#ifndef MEOWFILM_FILM_IO_H
#define MEOWFILM_FILM_IO_H
#include "film_kernel.h"
#include <windows.h>
#include <libavformat/avformat.h>
#include <libavutil/opt.h>

typedef struct mfk_io mfk_io;
typedef struct {
    mfk_callbacks callbacks;
    char *uri;
    int format;
    mfk_clear_key *keys;
    uint32_t key_count;
    uint8_t default_key[16];
    int has_default;
    volatile LONG cancelled;
    mfk_io *io;
} mfk_input;

#ifdef __cplusplus
extern "C" {
#endif
int mfk_interrupted(void *);
void mfk_failure(mfk_input *, int);
int mfk_io_open(AVFormatContext *, AVIOContext **, const char *, int, AVDictionary **);
int mfk_io_close(AVFormatContext *, AVIOContext *);
void mfk_io_close_all(mfk_input *);
int mfk_key_options(mfk_input *, AVDictionary **);
void mfk_clear_dictionary(AVDictionary **);
#ifdef __cplusplus
}
#endif
#endif
