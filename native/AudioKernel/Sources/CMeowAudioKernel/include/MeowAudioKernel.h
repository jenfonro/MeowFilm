#ifndef MEOW_AUDIO_KERNEL_H
#define MEOW_AUDIO_KERNEL_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct MeowAudioDecoder MeowAudioDecoder;
typedef struct MeowAudioInput {
    void *opaque;
    int (*read)(void *, uint8_t *, int);
    int64_t (*seek)(void *, int64_t, int);
    int (*interrupted)(void *);
} MeowAudioInput;
typedef struct MeowAudioInfo {
    double duration;
    int sample_rate;
    int channels;
    int seekable;
    int source_sample_rate;
    int source_channels;
    int64_t decoded_source_frames;
    int complete;
} MeowAudioInfo;

#define MEOW_AUDIO_API __attribute__((visibility("default")))

/* Only cancel may run concurrently. The input owner wakes blocked callbacks;
 * resume/destroy run on the serial worker after the cancelled read has ended.
 * read: positive frames, 0 EOF, -1 cancelled, -2 invalid media, -3 input failed,
 * -4 unsupported, -5 out of memory. Callbacks use these same negative codes.
 * format_hint is a demuxer name (or NULL). loopback_root is NULL or a canonical
 * http://127.0.0.1:port/path/ root, not the URL of the primary playlist.
 * Callback storage must outlive destroy. Output is stereo interleaved float;
 * position is the media time of the first returned frame. Unknown duration: NaN.
 * Info distinguishes source samples from output PCM; its actual decoded source
 * frame count resets on seek and complete requires codec/resampler EOF.
 */
MEOW_AUDIO_API MeowAudioDecoder *meow_audio_decoder_create(MeowAudioInput input, int sample_rate,
    const char *format_hint, const char *loopback_root, const char *primary_url, int *error);
MEOW_AUDIO_API int meow_audio_decoder_info(MeowAudioDecoder *, MeowAudioInfo *);
MEOW_AUDIO_API int meow_audio_decoder_read(MeowAudioDecoder *, float *interleaved, int maximum_frames, double *position);
MEOW_AUDIO_API int meow_audio_decoder_seek(MeowAudioDecoder *, double position);
MEOW_AUDIO_API void meow_audio_decoder_cancel(MeowAudioDecoder *);
MEOW_AUDIO_API void meow_audio_decoder_resume(MeowAudioDecoder *);
MEOW_AUDIO_API void meow_audio_decoder_destroy(MeowAudioDecoder *);

#undef MEOW_AUDIO_API

#ifdef __cplusplus
}
#endif
#endif
