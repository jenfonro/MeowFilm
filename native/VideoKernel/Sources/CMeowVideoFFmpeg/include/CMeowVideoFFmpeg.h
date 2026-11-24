#pragma once

#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>
#include <libavformat/avformat.h>
#include <libavformat/avio.h>
#include <libavcodec/avcodec.h>
#include <libavutil/avutil.h>
#include <libavutil/opt.h>
#include <libavutil/dict.h>
#include <libavutil/display.h>
#include <libavutil/mastering_display_metadata.h>
#include <libavutil/dovi_meta.h>
#include <libavutil/pixdesc.h>
#include <libavutil/imgutils.h>
#include <libavutil/channel_layout.h>
#include <libavutil/error.h>
#include <libswresample/swresample.h>
#include <libswscale/swscale.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef void (*mvk_log_sink_fn)(int level, const char *message);

int64_t mvk_av_nopts_value(void);
int mvk_averror_eof(void);
int mvk_averror_eagain(void);
int mvk_averror_exit(void);
int mvk_averror_invaliddata(void);
bool mvk_is_http_error(int err);
bool mvk_is_network_error(int err);

void mvk_secure_zero(void *ptr, size_t len);
void mvk_zero_dict_and_free(AVDictionary **dict);

void mvk_ensure_log_installed(void);
void mvk_set_log_sink(mvk_log_sink_fn sink);

typedef struct mvk_hdr_metadata {
    bool has_mastering_display;
    uint16_t display_primaries_x[3];
    uint16_t display_primaries_y[3];
    uint16_t white_point_x;
    uint16_t white_point_y;
    uint32_t max_display_luminance;
    uint32_t min_display_luminance;
    bool has_content_light;
    uint16_t max_cll;
    uint16_t max_fall;
} mvk_hdr_metadata;

typedef struct mvk_stream_geometry {
    int rotation_degrees;
    bool hflip;
    bool vflip;
    int bit_depth;
    int dovi_profile; // -1 if none
    mvk_hdr_metadata hdr;
} mvk_stream_geometry;

mvk_stream_geometry mvk_inspect_video_stream(const AVStream *stream);
mvk_hdr_metadata mvk_inspect_frame_hdr(const AVFrame *frame);

SwrContext *mvk_swr_create_stereo_float(const AVCodecParameters *par, int out_sample_rate);
SwrContext *mvk_swr_create_from_frame(const AVFrame *frame, int out_sample_rate);
int mvk_swr_convert_frame(SwrContext *swr, float *out_interleaved, int max_out_frames, const AVFrame *frame);

bool mvk_copy_plane_rows(uint8_t *dst, size_t dst_stride, size_t dst_height,
                         const uint8_t *src, int src_stride, size_t row_bytes, size_t rows);

typedef struct mvk_audio_ring mvk_audio_ring;
mvk_audio_ring *mvk_audio_ring_create(size_t capacity_samples);
void mvk_audio_ring_destroy(mvk_audio_ring *ring);
void mvk_audio_ring_clear_stopped(mvk_audio_ring *ring);
void mvk_audio_ring_request_flush(mvk_audio_ring *ring);
void mvk_audio_ring_set_playing(mvk_audio_ring *ring, bool playing);
void mvk_audio_ring_set_target_gain(mvk_audio_ring *ring, float gain);
uint64_t mvk_audio_ring_underruns(mvk_audio_ring *ring);
size_t mvk_audio_ring_available_read(mvk_audio_ring *ring);
size_t mvk_audio_ring_write(mvk_audio_ring *ring, const float *src, size_t sample_count);
size_t mvk_audio_ring_write_pos(mvk_audio_ring *ring);
size_t mvk_audio_ring_effective_read_pos(mvk_audio_ring *ring);
void mvk_audio_ring_render_planar_stereo(mvk_audio_ring *ring,
                                         float *left,
                                         float *right,
                                         size_t frames,
                                         float ramp_step);

#ifdef __cplusplus
}
#endif
