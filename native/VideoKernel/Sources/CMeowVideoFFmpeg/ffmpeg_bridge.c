#include "CMeowVideoFFmpeg.h"
#include <ctype.h>
#include <errno.h>
#include <math.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static pthread_mutex_t g_mvk_log_mutex = PTHREAD_MUTEX_INITIALIZER;
static mvk_log_sink_fn g_mvk_log_sink = NULL;
static bool g_mvk_log_installed = false;

static bool mvk_starts_with_ci(const char *s, const char *prefix) {
    while (*prefix) {
        if (!*s || tolower((unsigned char)*s) != tolower((unsigned char)*prefix)) {
            return false;
        }
        s++;
        prefix++;
    }
    return true;
}

static size_t mvk_append_str(char *dst, size_t pos, size_t dst_size, const char *str) {
    while (*str && pos + 1 < dst_size) {
        dst[pos++] = *str++;
    }
    return pos;
}

static void mvk_redact_line(const char *src, char *dst, size_t dst_size) {
    if (!dst || dst_size == 0) return;
    if (!src) {
        dst[0] = '\0';
        return;
    }
    size_t out = 0;
    const char *p = src;
    while (*p && out + 1 < dst_size) {
        if (mvk_starts_with_ci(p, "http://") || mvk_starts_with_ci(p, "https://")) {
            out = mvk_append_str(dst, out, dst_size, "<redacted-url>");
            while (*p && *p != ' ' && *p != '\t' && *p != '\'' && *p != '"' &&
                   *p != '\r' && *p != '\n' && *p != ',' && *p != ')') {
                p++;
            }
            continue;
        }
        static const char *const kSensitiveHeaders[] = {
            "authorization:",
            "proxy-authorization:",
            "cookie:",
            "set-cookie:",
            "cenc_decryption_keys",
            "decryption_keys",
            "decryption_key"
        };
        bool matched_header = false;
        for (size_t i = 0; i < sizeof(kSensitiveHeaders) / sizeof(kSensitiveHeaders[0]); i++) {
            const char *hdr = kSensitiveHeaders[i];
            if (mvk_starts_with_ci(p, hdr)) {
                size_t hdr_len = strlen(hdr);
                for (size_t k = 0; k < hdr_len && out + 1 < dst_size; k++) {
                    dst[out++] = p[k];
                }
                p += hdr_len;
                out = mvk_append_str(dst, out, dst_size, " <redacted>");
                while (*p && *p != '\r' && *p != '\n') {
                    p++;
                }
                matched_header = true;
                break;
            }
        }
        if (matched_header) continue;
        dst[out++] = *p++;
    }
    dst[out] = '\0';
}

static void mvk_ffmpeg_log_callback(void *ptr, int level, const char *fmt, va_list vl) {
    if (level > AV_LOG_WARNING) return;
    pthread_mutex_lock(&g_mvk_log_mutex);
    mvk_log_sink_fn sink = g_mvk_log_sink;
    pthread_mutex_unlock(&g_mvk_log_mutex);
    if (!sink || !fmt) return;

    char raw[512];
    int print_prefix = 1;
    av_log_format_line2(ptr, level, fmt, vl, raw, (int)sizeof(raw), &print_prefix);
    size_t len = strlen(raw);
    while (len > 0 && (raw[len - 1] == '\n' || raw[len - 1] == '\r')) {
        raw[--len] = '\0';
    }
    if (len == 0) return;

    char clean[512];
    mvk_redact_line(raw, clean, sizeof(clean));
    sink(level, clean);
}

void mvk_ensure_log_installed(void) {
    pthread_mutex_lock(&g_mvk_log_mutex);
    if (!g_mvk_log_installed) {
        av_log_set_callback(mvk_ffmpeg_log_callback);
        g_mvk_log_installed = true;
    }
    pthread_mutex_unlock(&g_mvk_log_mutex);
}

void mvk_set_log_sink(mvk_log_sink_fn sink) {
    pthread_mutex_lock(&g_mvk_log_mutex);
    g_mvk_log_sink = sink;
    if (!g_mvk_log_installed) {
        av_log_set_callback(mvk_ffmpeg_log_callback);
        g_mvk_log_installed = true;
    }
    pthread_mutex_unlock(&g_mvk_log_mutex);
}

int64_t mvk_av_nopts_value(void) { return AV_NOPTS_VALUE; }
int mvk_averror_eof(void) { return AVERROR_EOF; }
int mvk_averror_eagain(void) { return AVERROR(EAGAIN); }
int mvk_averror_exit(void) { return AVERROR_EXIT; }
int mvk_averror_invaliddata(void) { return AVERROR_INVALIDDATA; }

bool mvk_is_http_error(int err) {
    return err == AVERROR_HTTP_BAD_REQUEST ||
           err == AVERROR_HTTP_UNAUTHORIZED ||
           err == AVERROR_HTTP_FORBIDDEN ||
           err == AVERROR_HTTP_NOT_FOUND ||
           err == AVERROR_HTTP_TOO_MANY_REQUESTS ||
           err == AVERROR_HTTP_OTHER_4XX ||
           err == AVERROR_HTTP_SERVER_ERROR;
}

bool mvk_is_network_error(int err) {
    if (mvk_is_http_error(err)) return true;
    return err == AVERROR(EIO) ||
           err == AVERROR(ETIMEDOUT) ||
           err == AVERROR(ECONNRESET) ||
           err == AVERROR(ECONNREFUSED) ||
           err == AVERROR(ENETUNREACH) ||
           err == AVERROR(EHOSTUNREACH) ||
           err == AVERROR(EPIPE) ||
           err == AVERROR(ENOTCONN);
}

void mvk_secure_zero(void *ptr, size_t len) {
    if (!ptr || len == 0) return;
    volatile unsigned char *p = (volatile unsigned char *)ptr;
    while (len--) *p++ = 0;
}

void mvk_zero_dict_and_free(AVDictionary **dict) {
    if (!dict || !*dict) return;
    AVDictionaryEntry *entry = NULL;
    while ((entry = av_dict_get(*dict, "", entry, AV_DICT_IGNORE_SUFFIX)) != NULL) {
        if (entry->value) {
            mvk_secure_zero(entry->value, strlen(entry->value));
        }
    }
    av_dict_free(dict);
}

static uint16_t mvk_scale_chroma(AVRational r) {
    if (r.den <= 0) return 0;
    double v = av_q2d(r) * 50000.0;
    if (!isfinite(v) || v < 0.0) return 0;
    if (v > 65535.0) return 65535;
    return (uint16_t)llround(v);
}

static uint32_t mvk_scale_luma(AVRational r) {
    if (r.den <= 0) return 0;
    double v = av_q2d(r) * 10000.0;
    if (!isfinite(v) || v < 0.0) return 0;
    if (v > (double)UINT32_MAX) return UINT32_MAX;
    return (uint32_t)llround(v);
}

static void mvk_fill_mdm(mvk_hdr_metadata *hdr, const AVMasteringDisplayMetadata *mdm) {
    if (!hdr || !mdm || !mdm->has_primaries || !mdm->has_luminance) return;
    hdr->has_mastering_display = true;
    // SMPTE ST 2086 order: G, B, R (FFmpeg display_primaries is R=0, G=1, B=2)
    hdr->display_primaries_x[0] = mvk_scale_chroma(mdm->display_primaries[1][0]);
    hdr->display_primaries_y[0] = mvk_scale_chroma(mdm->display_primaries[1][1]);
    hdr->display_primaries_x[1] = mvk_scale_chroma(mdm->display_primaries[2][0]);
    hdr->display_primaries_y[1] = mvk_scale_chroma(mdm->display_primaries[2][1]);
    hdr->display_primaries_x[2] = mvk_scale_chroma(mdm->display_primaries[0][0]);
    hdr->display_primaries_y[2] = mvk_scale_chroma(mdm->display_primaries[0][1]);
    hdr->white_point_x = mvk_scale_chroma(mdm->white_point[0]);
    hdr->white_point_y = mvk_scale_chroma(mdm->white_point[1]);
    hdr->max_display_luminance = mvk_scale_luma(mdm->max_luminance);
    hdr->min_display_luminance = mvk_scale_luma(mdm->min_luminance);
}

static void mvk_fill_cll(mvk_hdr_metadata *hdr, const AVContentLightMetadata *cll) {
    if (!hdr || !cll || (cll->MaxCLL == 0 && cll->MaxFALL == 0)) return;
    hdr->has_content_light = true;
    hdr->max_cll = (uint16_t)(cll->MaxCLL > 65535 ? 65535 : cll->MaxCLL);
    hdr->max_fall = (uint16_t)(cll->MaxFALL > 65535 ? 65535 : cll->MaxFALL);
}

mvk_stream_geometry mvk_inspect_video_stream(const AVStream *stream) {
    mvk_stream_geometry g;
    memset(&g, 0, sizeof(g));
    g.bit_depth = 8;
    g.dovi_profile = -1;
    if (!stream || !stream->codecpar) return g;

    const AVCodecParameters *par = stream->codecpar;
    const AVPixFmtDescriptor *desc = av_pix_fmt_desc_get((enum AVPixelFormat)par->format);
    if (desc && desc->nb_components > 0 && desc->comp[0].depth > 0) {
        g.bit_depth = desc->comp[0].depth;
    } else if (par->bits_per_raw_sample > 0) {
        g.bit_depth = par->bits_per_raw_sample;
    }

    const AVPacketSideData *rot_sd = av_packet_side_data_get(
        par->coded_side_data, par->nb_coded_side_data, AV_PKT_DATA_DISPLAYMATRIX);
    if (rot_sd && rot_sd->size >= 9 * sizeof(int32_t)) {
        int32_t copy[9];
        memcpy(copy, rot_sd->data, sizeof(copy));
        int64_t det = (int64_t)copy[0] * (int64_t)copy[4] - (int64_t)copy[1] * (int64_t)copy[3];
        if (det < 0) {
            g.hflip = true;
            // Remove reflection before extracting rotation so pure horizontal flip stays 0 deg.
            av_display_matrix_flip(copy, 1, 0);
        }
        double theta = av_display_rotation_get(copy);
        if (isfinite(theta)) {
            int deg = (int)llround(-theta) % 360;
            if (deg < 0) deg += 360;
            if (deg >= 45 && deg < 135) g.rotation_degrees = 90;
            else if (deg >= 135 && deg < 225) g.rotation_degrees = 180;
            else if (deg >= 225 && deg < 315) g.rotation_degrees = 270;
            else g.rotation_degrees = 0;
        }
    }

    const AVPacketSideData *dovi_sd = av_packet_side_data_get(
        par->coded_side_data, par->nb_coded_side_data, AV_PKT_DATA_DOVI_CONF);
    if (dovi_sd && dovi_sd->size >= sizeof(AVDOVIDecoderConfigurationRecord)) {
        const AVDOVIDecoderConfigurationRecord *dovi =
            (const AVDOVIDecoderConfigurationRecord *)dovi_sd->data;
        g.dovi_profile = (int)dovi->dv_profile;
    }

    const AVPacketSideData *mdm_sd = av_packet_side_data_get(
        par->coded_side_data, par->nb_coded_side_data, AV_PKT_DATA_MASTERING_DISPLAY_METADATA);
    if (mdm_sd && mdm_sd->size >= sizeof(AVMasteringDisplayMetadata)) {
        mvk_fill_mdm(&g.hdr, (const AVMasteringDisplayMetadata *)mdm_sd->data);
    }

    const AVPacketSideData *cll_sd = av_packet_side_data_get(
        par->coded_side_data, par->nb_coded_side_data, AV_PKT_DATA_CONTENT_LIGHT_LEVEL);
    if (cll_sd && cll_sd->size >= sizeof(AVContentLightMetadata)) {
        mvk_fill_cll(&g.hdr, (const AVContentLightMetadata *)cll_sd->data);
    }

    return g;
}

mvk_hdr_metadata mvk_inspect_frame_hdr(const AVFrame *frame) {
    mvk_hdr_metadata hdr;
    memset(&hdr, 0, sizeof(hdr));
    if (!frame) return hdr;

    const AVFrameSideData *mdm_sd = av_frame_get_side_data(frame, AV_FRAME_DATA_MASTERING_DISPLAY_METADATA);
    if (mdm_sd && mdm_sd->size >= sizeof(AVMasteringDisplayMetadata)) {
        mvk_fill_mdm(&hdr, (const AVMasteringDisplayMetadata *)mdm_sd->data);
    }
    const AVFrameSideData *cll_sd = av_frame_get_side_data(frame, AV_FRAME_DATA_CONTENT_LIGHT_LEVEL);
    if (cll_sd && cll_sd->size >= sizeof(AVContentLightMetadata)) {
        mvk_fill_cll(&hdr, (const AVContentLightMetadata *)cll_sd->data);
    }
    return hdr;
}

SwrContext *mvk_swr_create_stereo_float(const AVCodecParameters *par, int out_sample_rate) {
    if (!par || par->sample_rate <= 0 || out_sample_rate <= 0) return NULL;
    AVChannelLayout out_layout = {0};
    av_channel_layout_default(&out_layout, 2);
    AVChannelLayout in_layout = {0};
    if (par->ch_layout.nb_channels > 0) {
        if (av_channel_layout_copy(&in_layout, &par->ch_layout) < 0) {
            av_channel_layout_uninit(&out_layout);
            return NULL;
        }
    } else {
        av_channel_layout_default(&in_layout, 2);
    }
    SwrContext *swr = NULL;
    int rc = swr_alloc_set_opts2(&swr,
                                 &out_layout, AV_SAMPLE_FMT_FLT, out_sample_rate,
                                 &in_layout, (enum AVSampleFormat)par->format, par->sample_rate,
                                 0, NULL);
    av_channel_layout_uninit(&in_layout);
    av_channel_layout_uninit(&out_layout);
    if (rc < 0 || !swr) {
        swr_free(&swr);
        return NULL;
    }
    if (swr_init(swr) < 0) {
        swr_free(&swr);
        return NULL;
    }
    return swr;
}

SwrContext *mvk_swr_create_from_frame(const AVFrame *frame, int out_sample_rate) {
    if (!frame || frame->sample_rate <= 0 || out_sample_rate <= 0) return NULL;
    AVChannelLayout out_layout = {0};
    av_channel_layout_default(&out_layout, 2);
    AVChannelLayout in_layout = {0};
    if (frame->ch_layout.nb_channels > 0) {
        if (av_channel_layout_copy(&in_layout, &frame->ch_layout) < 0) {
            av_channel_layout_uninit(&out_layout);
            return NULL;
        }
    } else {
        av_channel_layout_default(&in_layout, 2);
    }
    SwrContext *swr = NULL;
    int rc = swr_alloc_set_opts2(&swr,
                                 &out_layout, AV_SAMPLE_FMT_FLT, out_sample_rate,
                                 &in_layout, (enum AVSampleFormat)frame->format, frame->sample_rate,
                                 0, NULL);
    av_channel_layout_uninit(&in_layout);
    av_channel_layout_uninit(&out_layout);
    if (rc < 0 || !swr) {
        swr_free(&swr);
        return NULL;
    }
    if (swr_init(swr) < 0) {
        swr_free(&swr);
        return NULL;
    }
    return swr;
}

int mvk_swr_convert_frame(SwrContext *swr, float *out_interleaved, int max_out_frames, const AVFrame *frame) {
    if (!swr || !out_interleaved || max_out_frames <= 0) return 0;
    uint8_t *out_planes[1] = { (uint8_t *)out_interleaved };
    if (!frame) {
        return swr_convert(swr, out_planes, max_out_frames, NULL, 0);
    }
    return swr_convert(swr, out_planes, max_out_frames,
                       (const uint8_t **)frame->extended_data, frame->nb_samples);
}

bool mvk_copy_plane_rows(uint8_t *dst, size_t dst_stride, size_t dst_height,
                         const uint8_t *src, int src_stride, size_t row_bytes, size_t rows) {
    if (!dst || !src || row_bytes == 0 || rows == 0) return false;
    if (rows > dst_height || row_bytes > dst_stride) return false;
    if (src_stride == 0) return false;
    size_t abs_src_stride = (size_t)(src_stride < 0 ? -src_stride : src_stride);
    if (row_bytes > abs_src_stride) return false;
    for (size_t y = 0; y < rows; y++) {
        const uint8_t *srow = src_stride > 0
            ? src + y * abs_src_stride
            : src + (rows - 1 - y) * abs_src_stride;
        memcpy(dst + y * dst_stride, srow, row_bytes);
    }
    return true;
}

struct mvk_audio_ring {
    float *buffer;
    size_t capacity; // power of two (even, interleaved stereo samples)
    size_t mask;
    _Atomic size_t write_pos;       // single-producer monotonic sample counter
    _Atomic size_t read_pos;        // single-consumer monotonic sample counter (or stopped)
    _Atomic uint64_t flush_epoch;   // producer increments to request consumer flush
    _Atomic size_t flush_target;    // write_pos snapshot at flush time
    uint64_t seen_flush_epoch;      // consumer-only
    _Atomic int32_t playing;        // atomic playback state
    _Atomic float target_gain;      // atomic target gain
    float current_gain;             // consumer-only smoothed gain
    _Atomic uint64_t underruns;     // consumer increments
};

mvk_audio_ring *mvk_audio_ring_create(size_t capacity_samples) {
    size_t cap = 4096;
    while (cap < capacity_samples && cap < (1u << 24)) cap <<= 1;
    mvk_audio_ring *ring = (mvk_audio_ring *)calloc(1, sizeof(mvk_audio_ring));
    if (!ring) return NULL;
    ring->buffer = (float *)calloc(cap, sizeof(float));
    if (!ring->buffer) {
        free(ring);
        return NULL;
    }
    ring->capacity = cap;
    ring->mask = cap - 1;
    atomic_init(&ring->write_pos, 0);
    atomic_init(&ring->read_pos, 0);
    atomic_init(&ring->flush_epoch, 0);
    atomic_init(&ring->flush_target, 0);
    ring->seen_flush_epoch = 0;
    atomic_init(&ring->playing, 0);
    atomic_init(&ring->target_gain, 0.0f);
    ring->current_gain = 0.0f;
    atomic_init(&ring->underruns, 0);
    return ring;
}

void mvk_audio_ring_destroy(mvk_audio_ring *ring) {
    if (!ring) return;
    free(ring->buffer);
    free(ring);
}

void mvk_audio_ring_clear_stopped(mvk_audio_ring *ring) {
    if (!ring) return;
    size_t w = atomic_load_explicit(&ring->write_pos, memory_order_acquire);
    atomic_store_explicit(&ring->flush_target, w, memory_order_relaxed);
    atomic_store_explicit(&ring->read_pos, w, memory_order_release);
    ring->seen_flush_epoch = atomic_load_explicit(&ring->flush_epoch, memory_order_relaxed);
    ring->current_gain = 0.0f;
}

void mvk_audio_ring_request_flush(mvk_audio_ring *ring) {
    if (!ring) return;
    size_t w = atomic_load_explicit(&ring->write_pos, memory_order_acquire);
    atomic_store_explicit(&ring->flush_target, w, memory_order_release);
    atomic_fetch_add_explicit(&ring->flush_epoch, 1, memory_order_release);
}

void mvk_audio_ring_set_playing(mvk_audio_ring *ring, bool playing) {
    if (!ring) return;
    atomic_store_explicit(&ring->playing, playing ? 1 : 0, memory_order_release);
}

void mvk_audio_ring_set_target_gain(mvk_audio_ring *ring, float gain) {
    if (!ring) return;
    atomic_store_explicit(&ring->target_gain, gain, memory_order_relaxed);
}

uint64_t mvk_audio_ring_underruns(mvk_audio_ring *ring) {
    if (!ring) return 0;
    return atomic_load_explicit(&ring->underruns, memory_order_relaxed);
}

size_t mvk_audio_ring_write_pos(mvk_audio_ring *ring) {
    if (!ring) return 0;
    return atomic_load_explicit(&ring->write_pos, memory_order_acquire);
}

size_t mvk_audio_ring_effective_read_pos(mvk_audio_ring *ring) {
    if (!ring) return 0;
    // Load r and ft BEFORE w with acquire ordering so w >= r and w >= ft always hold
    // by release-acquire transitivity (prevents unsigned underflow on concurrent advances).
    size_t r = atomic_load_explicit(&ring->read_pos, memory_order_acquire);
    size_t ft = atomic_load_explicit(&ring->flush_target, memory_order_acquire);
    size_t w = atomic_load_explicit(&ring->write_pos, memory_order_acquire);
    return ((size_t)(w - ft) < (size_t)(w - r)) ? ft : r;
}

size_t mvk_audio_ring_available_read(mvk_audio_ring *ring) {
    if (!ring) return 0;
    size_t r = atomic_load_explicit(&ring->read_pos, memory_order_acquire);
    size_t ft = atomic_load_explicit(&ring->flush_target, memory_order_acquire);
    size_t w = atomic_load_explicit(&ring->write_pos, memory_order_acquire);
    size_t eff_r = ((size_t)(w - ft) < (size_t)(w - r)) ? ft : r;
    return w - eff_r;
}

size_t mvk_audio_ring_write(mvk_audio_ring *ring, const float *src, size_t sample_count) {
    if (!ring || !src || sample_count < 2) return 0;
    size_t aligned = sample_count & ~(size_t)1;
    size_t w = atomic_load_explicit(&ring->write_pos, memory_order_relaxed);
    size_t r = atomic_load_explicit(&ring->read_pos, memory_order_acquire);
    size_t used = w - r;
    if (used >= ring->capacity) return 0;
    size_t avail = (ring->capacity - used) & ~(size_t)1;
    size_t n = aligned < avail ? aligned : avail;
    if (n == 0) return 0;
    size_t idx = w & ring->mask;
    size_t first = ring->capacity - idx;
    if (first > n) first = n;
    memcpy(ring->buffer + idx, src, first * sizeof(float));
    if (n > first) {
        memcpy(ring->buffer, src + first, (n - first) * sizeof(float));
    }
    atomic_store_explicit(&ring->write_pos, w + n, memory_order_release);
    return n;
}

static size_t mvk_audio_ring_read_consumer(mvk_audio_ring *ring, float *dst, size_t sample_count) {
    size_t aligned = sample_count & ~(size_t)1;
    size_t r = atomic_load_explicit(&ring->read_pos, memory_order_relaxed);
    size_t w = atomic_load_explicit(&ring->write_pos, memory_order_acquire);
    size_t avail = (w - r) & ~(size_t)1;
    size_t n = aligned < avail ? aligned : avail;
    if (n == 0) return 0;
    size_t idx = r & ring->mask;
    size_t first = ring->capacity - idx;
    if (first > n) first = n;
    memcpy(dst, ring->buffer + idx, first * sizeof(float));
    if (n > first) {
        memcpy(dst + first, ring->buffer, (n - first) * sizeof(float));
    }
    atomic_store_explicit(&ring->read_pos, r + n, memory_order_release);
    return n;
}

void mvk_audio_ring_render_planar_stereo(mvk_audio_ring *ring,
                                         float *left,
                                         float *right,
                                         size_t frames,
                                         float ramp_step) {
    if (!ring || !left || !right || frames == 0) return;

    uint64_t ep = atomic_load_explicit(&ring->flush_epoch, memory_order_acquire);
    if (ep != ring->seen_flush_epoch) {
        size_t ft = atomic_load_explicit(&ring->flush_target, memory_order_acquire);
        size_t r = atomic_load_explicit(&ring->read_pos, memory_order_relaxed);
        if ((size_t)(ft - r) <= ring->capacity) {
            atomic_store_explicit(&ring->read_pos, ft, memory_order_release);
        }
        ring->seen_flush_epoch = ep;
        ring->current_gain = 0.0f;
    }

    if (atomic_load_explicit(&ring->playing, memory_order_acquire) == 0) {
        memset(left, 0, frames * sizeof(float));
        memset(right, 0, frames * sizeof(float));
        ring->current_gain = 0.0f;
        return;
    }

    float gain = ring->current_gain;
    float target = atomic_load_explicit(&ring->target_gain, memory_order_relaxed);
    float temp[1024]; // 512 stereo frames on C stack — zero heap allocation in CoreAudio RT callback.
    size_t done = 0;

    while (done < frames) {
        size_t chunk = frames - done;
        if (chunk > 512) chunk = 512;
        size_t read_samples = mvk_audio_ring_read_consumer(ring, temp, chunk * 2);
        size_t read_frames = read_samples / 2;
        for (size_t i = 0; i < read_frames; i++) {
            if (gain < target) {
                gain += ramp_step;
                if (gain > target) gain = target;
            } else if (gain > target) {
                gain -= ramp_step;
                if (gain < target) gain = target;
            }
            left[done + i] = temp[i * 2] * gain;
            right[done + i] = temp[i * 2 + 1] * gain;
        }
        if (read_frames < chunk) {
            size_t rem = frames - (done + read_frames);
            memset(left + done + read_frames, 0, rem * sizeof(float));
            memset(right + done + read_frames, 0, rem * sizeof(float));
            atomic_fetch_add_explicit(&ring->underruns, 1, memory_order_relaxed);
            gain = 0.0f;
            break;
        }
        done += read_frames;
    }
    ring->current_gain = gain;
}
