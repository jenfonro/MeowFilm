#ifndef MEOW_VIDEO_KERNEL_H
#define MEOW_VIDEO_KERNEL_H

#include <CoreMedia/CoreMedia.h>
#include <stdint.h>

CF_ASSUME_NONNULL_BEGIN

typedef struct MVKDecoder MVKDecoder;
typedef struct {
    double duration;
    int32_t video_codec;
    int32_t video_profile;
    int32_t bit_depth;
    int32_t width;
    int32_t height;
    int32_t has_audio;
    int32_t sample_rate;
    int32_t audio_channels;
    int32_t pixel_format;
    /* -1: no measured session, 0: software, 1: measured hardware. */
    int32_t hardware_decode;
} MVKMediaInfo;

enum {
    MVK_OK = 0, MVK_END = 1, MVK_INTERRUPTED = 2,
    MVK_CANCELLED = -1, MVK_OPEN_FAILED = -2, MVK_FORMAT_FAILED = -3,
    MVK_DECODE_FAILED = -4, MVK_SEEK_FAILED = -5, MVK_OUTPUT_FAILED = -6,
    MVK_TIMED_OUT = -7, MVK_TRANSPORT_FAILED = -8, MVK_DECRYPTION_FAILED = -9
};
enum { MVK_VIDEO = 1, MVK_AUDIO = 2 };

/* Construction is passive; open/read/seek are serialized by the caller.
 * Interrupt/cancel may be invoked concurrently. No function logs inputs,
 * dictionaries, FFmpeg error strings or packet contents. */
MVKDecoder * _Nullable mvk_create(void);
int32_t mvk_open(MVKDecoder *decoder, const char *url, const char *format,
                 const char *kid_keys);
int32_t mvk_read(MVKDecoder *decoder, CMSampleBufferRef _Nullable * _Nonnull sample,
                 int32_t *kind, double *end_time);
CMSampleBufferRef _Nullable mvk_copy_sample(MVKDecoder *decoder, int32_t *status,
                                           int32_t *kind, double *end_time) CF_RETURNS_RETAINED;
int32_t mvk_seek(MVKDecoder *decoder, double position);
void mvk_media_info(MVKDecoder *decoder, MVKMediaInfo *info);
void mvk_interrupt(MVKDecoder *decoder);
void mvk_cancel(MVKDecoder *decoder);
/* Caller joins every read/seek before destroy. Closes FFmpeg input, codecs,
 * resamplers, frames and the actual VideoToolbox session synchronously. */
void mvk_destroy(MVKDecoder * _Nullable decoder);
const char *mvk_codec_name(int32_t codec);

CF_ASSUME_NONNULL_END

#endif
