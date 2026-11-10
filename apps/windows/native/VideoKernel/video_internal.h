#ifndef MEOWFILM_VIDEO_INTERNAL_H
#define MEOWFILM_VIDEO_INTERNAL_H

#include "video_kernel.h"
#include <windows.h>
/* The external Windows SDK omits config.h's vlc_fixups.h declarations.
 * Match its opaque poll prototype for vlc_threads.h's unused inline wrapper. */
struct pollfd;
int poll(struct pollfd *, unsigned, int);
#include <vlc_common.h>
#include <vlc_demux.h>
#include <vlc_es.h>
#include <vlc_es_out.h>
#include <vlc_block.h>
#include <libavformat/avformat.h>
#include <libavcodec/codec_par.h>
#include <libavutil/opt.h>

typedef struct mvk_io mvk_io;
typedef struct mvk_owner {
    mvk_callbacks callbacks;
    char *uri;
    char token[33];
    int format;
    mvk_clear_key *keys;
    uint32_t key_count;
    uint8_t default_key[16];
    int has_default;
    volatile LONG cancelled;
    unsigned users;
    int closing;
    mvk_io *io;
    struct mvk_owner *next;
} mvk_owner;

typedef struct {
    es_out_id_t *es;
    int64_t dts;
} mvk_track;

struct demux_sys_t {
    mvk_owner *owner;
    AVFormatContext *format;
    AVIOContext *root;
    AVPacket *packet;
    mvk_track *tracks;
    unsigned track_count;
    int64_t start;
    int64_t time;
};

int mvk_interrupted(void *);
void mvk_failure(mvk_owner *, int);
int mvk_io_open(AVFormatContext *, AVIOContext **, const char *, int, AVDictionary **);
int mvk_io_close(AVFormatContext *, AVIOContext *);
void mvk_io_close_all(mvk_owner *);
int mvk_add_tracks(demux_t *);
void mvk_delete_tracks(demux_t *);
block_t *mvk_packet_block(const AVPacket *, const AVStream *, int64_t);

#endif
