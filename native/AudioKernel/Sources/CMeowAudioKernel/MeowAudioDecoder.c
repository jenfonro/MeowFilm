#include "MeowAudioKernel.h"

#include <errno.h>
#include <limits.h>
#include <math.h>
#include <pthread.h>
#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>

#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/channel_layout.h>
#include <libavutil/log.h>
#include <libavutil/mem.h>
#include <libavutil/opt.h>
#include <libswresample/swresample.h>

enum { CANCELLED = -1, INVALID_MEDIA = -2, INPUT_FAILED = -3, UNSUPPORTED = -4, NO_MEMORY = -5 };
enum { IO_BYTES = 65536, MAX_PCM_FRAMES = 262144, MAX_SAMPLE_RATE = 384000 };

typedef struct NestedIO {
    AVIOContext *io;
    struct NestedIO *next;
} NestedIO;

struct MeowAudioDecoder {
    MeowAudioInput input;
    MeowAudioInfo info;
    atomic_int cancelled;
    int input_error;
    int nested_error;
    char *loopback_root;
    AVIOContext *io;
    NestedIO *nested;
    AVFormatContext *format;
    AVCodecContext *codec;
    AVPacket *packet;
    AVFrame *frame;
    SwrContext *swr;
    AVChannelLayout input_layout;
    int input_rate, input_format, stream;
    AVRational time_base;
    int64_t start_timestamp;
    double pcm_position, drop_before;
    int position_known, require_timestamp, packet_ready, drain_sent, codec_ended, ended;
    float *pcm_left;
    float *pcm_right;
    int pcm_capacity, pcm_offset, pcm_count;
    int pcm_block_align, packet_tail_count;
    uint8_t packet_tail[512];
};

static pthread_once_t log_once = PTHREAD_ONCE_INIT;

static void discard_log(void *context, int level, const char *format, va_list arguments)
{
    (void)context; (void)level; (void)format; (void)arguments;
}

static void install_discard_log(void)
{
    /* Confined to this two-level dynamic FFmpeg image; installed once process-wide. */
    av_log_set_callback(discard_log);
    av_log_set_level(AV_LOG_QUIET);
}

static int interrupted(void *opaque)
{
    MeowAudioDecoder *d = opaque;
    return atomic_load_explicit(&d->cancelled, memory_order_acquire) ||
        (d->input.interrupted && d->input.interrupted(d->input.opaque));
}

static int failure(MeowAudioDecoder *d, int code)
{
    if (interrupted(d) || code == AVERROR_EXIT || d->input_error == CANCELLED) return CANCELLED;
    if (d->input_error) return d->input_error;
    if (code == AVERROR(ENOMEM)) return NO_MEMORY;
    if (code == AVERROR_DECODER_NOT_FOUND || code == AVERROR_DEMUXER_NOT_FOUND ||
        code == AVERROR_STREAM_NOT_FOUND || code == AVERROR_PROTOCOL_NOT_FOUND ||
        code == AVERROR(ENOSYS)) return UNSUPPORTED;
    if (code == AVERROR_INVALIDDATA || code == AVERROR_EOF || code == AVERROR(EINVAL)) return INVALID_MEDIA;
    return INPUT_FAILED;
}

static int callback_error(MeowAudioDecoder *d, int code)
{
    d->input_error = code >= NO_MEMORY && code <= CANCELLED ? code : INPUT_FAILED;
    if (d->input_error == CANCELLED) return AVERROR_EXIT;
    if (d->input_error == NO_MEMORY) return AVERROR(ENOMEM);
    if (d->input_error == INVALID_MEDIA) return AVERROR_INVALIDDATA;
    if (d->input_error == UNSUPPORTED) return AVERROR(ENOSYS);
    return AVERROR(EIO);
}

static int read_input(void *opaque, uint8_t *buffer, int count)
{
    MeowAudioDecoder *d = opaque;
    if (interrupted(d)) return AVERROR_EXIT;
    int result = d->input.read(d->input.opaque, buffer, count);
    /* Do not discard successful bytes if cancel raced with the input callback:
     * FFmpeg must retain them in AVIO before the worker acknowledges cancel. */
    if (result > count) return callback_error(d, INPUT_FAILED);
    if (result < 0) return callback_error(d, result);
    if (!result && interrupted(d)) return AVERROR_EXIT;
    return result ? result : AVERROR_EOF;
}

static int64_t seek_input(void *opaque, int64_t offset, int whence)
{
    MeowAudioDecoder *d = opaque;
    if (interrupted(d)) return AVERROR_EXIT;
    if (!d->input.seek) return AVERROR(ENOSYS);
    int base = whence & ~AVSEEK_FORCE;
    if (base != SEEK_SET && base != SEEK_CUR && base != SEEK_END && base != AVSEEK_SIZE)
        return AVERROR(EINVAL);
    int64_t result = d->input.seek(d->input.opaque, offset, whence);
    /* Unsupported capability probes are not terminal input errors. */
    if (result < 0) {
        if (result == CANCELLED) return callback_error(d, CANCELLED);
        if (base == AVSEEK_SIZE || result == UNSUPPORTED) return AVERROR(ENOSYS);
        return callback_error(d, result >= NO_MEMORY && result <= CANCELLED ? (int)result : INPUT_FAILED);
    }
    return result;
}

static int allowed_url(MeowAudioDecoder *d, const char *url)
{
    if (!d->loopback_root || !url) return 0;
    if (!strncmp(url, "crypto+", 7) || !strncmp(url, "crypto:", 7)) url += 7;
    size_t length = strlen(d->loopback_root);
    return !strncmp(url, d->loopback_root, length);
}

static int open_nested(AVFormatContext *format, AVIOContext **io, const char *url,
    int flags, AVDictionary **options)
{
    MeowAudioDecoder *d = format->opaque;
    if (interrupted(d)) return AVERROR_EXIT;
    if (flags != AVIO_FLAG_READ || !allowed_url(d, url))
        return callback_error(d, UNSUPPORTED);
    AVDictionary *bounded = NULL;
    int result = av_dict_copy(&bounded, options ? *options : NULL, 0);
    if (result >= 0) result = av_dict_set(&bounded, "rw_timeout", "15000000", 0);
    if (result >= 0) result = av_dict_set(&bounded, "timeout", "15000000", 0);
    if (result >= 0) result = av_dict_set(&bounded, "reconnect", "0", 0);
    if (result >= 0) result = av_dict_set(&bounded, "multiple_requests", "0", 0);
    NestedIO *entry = NULL;
    if (result >= 0) {
        entry = av_mallocz(sizeof(*entry));
        if (!entry) result = AVERROR(ENOMEM);
    }
    if (result >= 0) result = avio_open2(io, url, flags, &format->interrupt_callback, &bounded);
    if (result >= 0) {
        entry->io = *io;
        entry->next = d->nested;
        d->nested = entry;
    } else {
        if (!interrupted(d) && result != AVERROR_EXIT && !d->nested_error)
            d->nested_error = result;
        av_free(entry);
    }
    av_dict_free(&bounded);
    return result;
}

static int close_nested(AVFormatContext *format, AVIOContext *io)
{
    MeowAudioDecoder *d = format->opaque;
    if (io && io->error < 0 && io->error != AVERROR_EOF && io->error != AVERROR_EXIT &&
        !interrupted(d) && !d->nested_error) {
        d->nested_error = io->error;
    }
    NestedIO **entry = &d->nested;
    while (*entry && (*entry)->io != io) entry = &(*entry)->next;
    if (*entry) {
        NestedIO *removed = *entry;
        *entry = removed->next;
        av_free(removed);
    }
    return avio_close(io);
}

static int nested_io_error(MeowAudioDecoder *d)
{
    for (NestedIO *entry = d->nested; entry; entry = entry->next) {
        if (entry->io && entry->io->error < 0 && entry->io->error != AVERROR_EOF)
            return entry->io->error;
    }
    return d->nested_error;
}

MeowAudioDecoder *meow_audio_decoder_create(MeowAudioInput input, int sample_rate,
    const char *format_hint, const char *loopback_root, const char *primary_url, int *error)
{
    int result = INVALID_MEDIA;
    if (error) *error = result;
    if (!input.read || sample_rate <= 0 || sample_rate > MAX_SAMPLE_RATE) return NULL;
    pthread_once(&log_once, install_discard_log);
    MeowAudioDecoder *d = calloc(1, sizeof(*d));
    if (!d) { if (error) *error = NO_MEMORY; return NULL; }
    d->input = input;
    atomic_init(&d->cancelled, 0);
    d->info = (MeowAudioInfo){ .duration = NAN, .sample_rate = sample_rate, .channels = 2 };
    if (loopback_root && *loopback_root) {
        d->loopback_root = av_strdup(loopback_root);
        if (!d->loopback_root) { result = NO_MEMORY; goto fail; }
    }
    if (primary_url && !allowed_url(d, primary_url)) { result = UNSUPPORTED; goto fail; }
    d->format = avformat_alloc_context();
    uint8_t *buffer = av_malloc(IO_BYTES);
    if (!d->format || !buffer) { av_free(buffer); result = NO_MEMORY; goto fail; }
    d->io = avio_alloc_context(buffer, IO_BYTES, 0, d, read_input, NULL, input.seek ? seek_input : NULL);
    if (!d->io) { av_free(buffer); result = NO_MEMORY; goto fail; }
    if (input.seek) {
        int64_t current = input.seek(input.opaque, 0, SEEK_CUR);
        if (current >= 0) {
            d->io->seekable = AVIO_SEEKABLE_NORMAL;
        } else if (current != UNSUPPORTED) {
            result = current >= NO_MEMORY && current <= CANCELLED ? (int)current : INPUT_FAILED;
            goto fail;
        }
    }
    d->info.seekable = !!(d->io->seekable & AVIO_SEEKABLE_NORMAL);
    d->format->pb = d->io;
    d->format->opaque = d;
    d->format->flags |= AVFMT_FLAG_CUSTOM_IO;
    d->format->interrupt_callback = (AVIOInterruptCB){ interrupted, d };
    d->format->io_open = open_nested;
    d->format->io_close2 = close_nested;
    d->format->probesize = 4 * 1024 * 1024;
    d->format->max_analyze_duration = 5 * AV_TIME_BASE;
    d->format->max_streams = 64;
    d->format->error_recognition = AV_EF_CRCCHECK | AV_EF_BITSTREAM | AV_EF_BUFFER | AV_EF_EXPLODE;
    const AVInputFormat *hint = NULL;
    if (format_hint && *format_hint) {
        hint = av_find_input_format(format_hint);
        if (!hint) { result = UNSUPPORTED; goto fail; }
    }
    AVDictionary *options = NULL;
    int rc = av_dict_set(&options, "protocol_whitelist", d->loopback_root ? "http,tcp,crypto" : "none", 0);
    if (rc >= 0) rc = av_dict_set(&options, "http_persistent", "0", 0);
    if (rc >= 0) rc = av_dict_set(&options, "http_multiple", "0", 0);
    if (rc >= 0) rc = av_dict_set(&options, "allowed_extensions", "ALL", 0);
    if (rc >= 0) rc = av_dict_set(&options, "extension_picky", "0", 0);
    if (rc >= 0) rc = avformat_open_input(&d->format,
        primary_url ? primary_url : d->loopback_root ? d->loopback_root : "meow-audio-input", hint, &options);
    av_dict_free(&options);
    if (rc < 0) { result = failure(d, rc); goto fail; }
    rc = avformat_find_stream_info(d->format, NULL);
    if (rc < 0) { result = failure(d, rc); goto fail; }
    const AVCodec *codec = NULL;
    rc = av_find_best_stream(d->format, AVMEDIA_TYPE_AUDIO, -1, -1, &codec, 0);
    if (rc < 0 || !codec) { result = rc < 0 ? failure(d, rc) : UNSUPPORTED; goto fail; }
    d->stream = rc;
    AVStream *stream = d->format->streams[d->stream];
    d->time_base = stream->time_base;
    if (d->time_base.num <= 0 || d->time_base.den <= 0) { result = INVALID_MEDIA; goto fail; }
    d->start_timestamp = stream->start_time != AV_NOPTS_VALUE ? stream->start_time :
        d->format->start_time != AV_NOPTS_VALUE
            ? av_rescale_q(d->format->start_time, AV_TIME_BASE_Q, d->time_base) : 0;
    if (stream->duration != AV_NOPTS_VALUE && stream->duration > 0)
        d->info.duration = stream->duration * av_q2d(d->time_base);
    else if (d->format->duration != AV_NOPTS_VALUE && d->format->duration > 0)
        d->info.duration = (double)d->format->duration / AV_TIME_BASE;
    for (unsigned i = 0; i < d->format->nb_streams; ++i)
        if ((int)i != d->stream) d->format->streams[i]->discard = AVDISCARD_ALL;
    d->codec = avcodec_alloc_context3(codec);
    d->packet = av_packet_alloc();
    d->frame = av_frame_alloc();
    if (!d->codec || !d->packet || !d->frame) { result = NO_MEMORY; goto fail; }
    rc = avcodec_parameters_to_context(d->codec, stream->codecpar);
    d->codec->pkt_timebase = d->time_base;
    d->codec->thread_count = 1;
    d->codec->max_samples = (int64_t)MAX_PCM_FRAMES * 64;
    d->codec->err_recognition = d->format->error_recognition;
    if (rc >= 0) rc = avcodec_open2(d->codec, codec, NULL);
    if (rc < 0) { result = failure(d, rc); goto fail; }
    d->info.source_sample_rate = d->codec->sample_rate;
    d->info.source_channels = d->codec->ch_layout.nb_channels;
    if (!strncmp(codec->name, "pcm_", 4) && !strstr(codec->name, "_planar")) {
        int bits = av_get_exact_bits_per_sample(codec->id);
        int channels = d->codec->ch_layout.nb_channels;
        if (bits > 0 && bits % 8 == 0 && channels > 0 && channels <= 64) {
            int align = bits / 8 * channels;
            if (align <= (int)sizeof(d->packet_tail)) d->pcm_block_align = align;
        }
    }
    if (interrupted(d)) { result = CANCELLED; goto fail; }
    if (error) *error = 0;
    return d;
fail:
    if (error) *error = result;
    meow_audio_decoder_destroy(d);
    return NULL;
}

static int decode_frame(MeowAudioDecoder *d)
{
    for (;;) {
        if (interrupted(d)) return CANCELLED;
        int rc = avcodec_receive_frame(d->codec, d->frame);
        if (rc >= 0) return 1;
        if (rc == AVERROR_EOF) { d->codec_ended = 1; return 0; }
        if (rc != AVERROR(EAGAIN)) return failure(d, rc);
        if (d->drain_sent) return INVALID_MEDIA;
        if (!d->packet_ready) {
            for (;;) {
                if (interrupted(d)) return CANCELLED;
                rc = av_read_frame(d->format, d->packet);
                if (rc == AVERROR_EOF) {
                    if (d->io->error < 0 && d->io->error != AVERROR_EOF) return failure(d, d->io->error);
                    int nerr = nested_io_error(d);
                    if (nerr < 0) return failure(d, nerr);
                    if (interrupted(d)) return CANCELLED;
                    if (d->packet_tail_count) return INVALID_MEDIA;
                    rc = avcodec_send_packet(d->codec, NULL);
                    if (rc < 0) return failure(d, rc);
                    d->drain_sent = 1;
                    break;
                }
                if (rc < 0) return failure(d, rc);
                if (d->packet->stream_index == d->stream) { d->packet_ready = 1; break; }
                av_packet_unref(d->packet);
            }
        }
        if (d->drain_sent) continue;
        if (interrupted(d)) return CANCELLED;
        if (d->pcm_block_align > 1) {
            int prefix = d->packet_tail_count;
            if (prefix) {
                rc = av_grow_packet(d->packet, prefix);
                if (rc < 0) return failure(d, rc);
                memmove(d->packet->data + prefix, d->packet->data, (size_t)d->packet->size - prefix);
                memcpy(d->packet->data, d->packet_tail, (size_t)prefix);
            }
            int tail = d->packet->size % d->pcm_block_align;
            if (tail) {
                rc = av_packet_make_writable(d->packet);
                if (rc < 0) return failure(d, rc);
                memcpy(d->packet_tail, d->packet->data + d->packet->size - tail, (size_t)tail);
                av_shrink_packet(d->packet, d->packet->size - tail);
            }
            d->packet_tail_count = tail;
            if (!d->packet->size) {
                av_packet_unref(d->packet);
                d->packet_ready = 0;
                continue;
            }
        }
        rc = avcodec_send_packet(d->codec, d->packet);
        if (rc < 0) return failure(d, rc);
        av_packet_unref(d->packet);
        d->packet_ready = 0;
    }
}

static int convert_frame(MeowAudioDecoder *d, AVFrame *frame)
{
    int rc;
    if (frame) {
        if (frame->nb_samples <= 0 || frame->nb_samples > MAX_PCM_FRAMES ||
            frame->sample_rate <= 0 || frame->sample_rate > MAX_SAMPLE_RATE ||
            frame->ch_layout.nb_channels > 64 || !av_channel_layout_check(&frame->ch_layout) ||
            (frame->flags & AV_FRAME_FLAG_CORRUPT) || frame->decode_error_flags) return INVALID_MEDIA;
        if (!d->swr) {
            AVChannelLayout stereo = AV_CHANNEL_LAYOUT_STEREO;
            rc = av_channel_layout_copy(&d->input_layout, &frame->ch_layout);
            if (rc < 0) return failure(d, rc);
            d->input_rate = frame->sample_rate;
            d->input_format = frame->format;
            rc = swr_alloc_set_opts2(&d->swr, &stereo, AV_SAMPLE_FMT_FLTP, d->info.sample_rate,
                &frame->ch_layout, frame->format, frame->sample_rate, 0, NULL);
            if (rc >= 0) rc = swr_init(d->swr);
            if (rc < 0) return failure(d, rc);
        }
        if (d->input_rate != frame->sample_rate || d->input_format != frame->format ||
            av_channel_layout_compare(&d->input_layout, &frame->ch_layout)) return UNSUPPORTED;
        int64_t timestamp = frame->best_effort_timestamp != AV_NOPTS_VALUE
            ? frame->best_effort_timestamp : frame->pts;
        if (timestamp != AV_NOPTS_VALUE) {
            int64_t delay_base = (int64_t)d->input_rate * d->info.sample_rate;
            d->pcm_position = ((double)timestamp - (double)d->start_timestamp) *
                av_q2d(d->time_base) * d->info.sample_rate -
                (double)swr_get_delay(d->swr, delay_base) / d->input_rate;
            d->position_known = 1;
        } else if (!d->position_known) {
            if (d->require_timestamp) return UNSUPPORTED;
            d->pcm_position = 0;
            d->position_known = 1;
        }
    }
    if (!d->swr) return 0;
    int capacity = swr_get_out_samples(d->swr, frame ? frame->nb_samples : 0);
    if (capacity < 0) return failure(d, capacity);
    if (capacity > MAX_PCM_FRAMES) return UNSUPPORTED;
    if (capacity < 1) capacity = 1;
    if (capacity > d->pcm_capacity) {
        float *left = av_malloc((size_t)capacity * sizeof(float));
        if (!left) return NO_MEMORY;
        float *right = av_malloc((size_t)capacity * sizeof(float));
        if (!right) { av_free(left); return NO_MEMORY; }
        av_free(d->pcm_left);
        av_free(d->pcm_right);
        d->pcm_left = left;
        d->pcm_right = right;
        d->pcm_capacity = capacity;
    }
    uint8_t *output[2] = { (uint8_t *)d->pcm_left, (uint8_t *)d->pcm_right };
    rc = swr_convert(d->swr, output, capacity,
        frame ? (const uint8_t **)frame->extended_data : NULL, frame ? frame->nb_samples : 0);
    if (rc < 0) return failure(d, rc);
    if (frame) {
        if (d->info.decoded_source_frames > INT64_MAX - frame->nb_samples) return INVALID_MEDIA;
        d->info.source_sample_rate = frame->sample_rate;
        d->info.source_channels = frame->ch_layout.nb_channels;
        d->info.decoded_source_frames += frame->nb_samples;
    }
    double delta = d->drop_before - d->pcm_position;
    int skip = delta > 0 ? (delta >= rc ? rc : (int)ceil(delta)) : 0;
    d->pcm_offset = skip;
    d->pcm_count = rc - skip;
    return rc;
}

int meow_audio_decoder_info(MeowAudioDecoder *d, MeowAudioInfo *info)
{
    if (!d || !info) return INVALID_MEDIA;
    *info = d->info;
    info->complete = d->ended;
    return 0;
}

int meow_audio_decoder_read(MeowAudioDecoder *d, float *left, float *right,
    int maximum_frames, double *position)
{
    if (!d || maximum_frames <= 0 || maximum_frames > MAX_PCM_FRAMES ||
        ((left == NULL) != (right == NULL))) return INVALID_MEDIA;
    if (interrupted(d)) return CANCELLED;
    if (d->input_error) return d->input_error;
    while (!d->pcm_count && !d->ended) {
        d->pcm_position += d->pcm_offset;
        d->pcm_offset = 0;
        int rc = d->codec_ended ? 0 : decode_frame(d);
        if (rc < 0) return rc;
        int converted = convert_frame(d, rc ? d->frame : NULL);
        if (rc) av_frame_unref(d->frame);
        if (converted < 0) return converted;
        if (d->codec_ended && converted == 0) d->ended = 1;
        if (interrupted(d)) return CANCELLED;
    }
    if (interrupted(d)) return CANCELLED;
    if (!d->pcm_count) return 0;
    int frames = d->pcm_count < maximum_frames ? d->pcm_count : maximum_frames;
    const float *src_left = d->pcm_left + d->pcm_offset;
    const float *src_right = d->pcm_right + d->pcm_offset;
    for (int i = 0; i < frames; ++i) {
        if (!isfinite(src_left[i]) || !isfinite(src_right[i])) return INVALID_MEDIA;
    }
    if (position) *position = (d->pcm_position + d->pcm_offset) / d->info.sample_rate;
    if (left && right) {
        memcpy(left, src_left, (size_t)frames * sizeof(float));
        memcpy(right, src_right, (size_t)frames * sizeof(float));
    }
    d->pcm_offset += frames;
    d->pcm_count -= frames;
    return frames;
}

int meow_audio_decoder_seek(MeowAudioDecoder *d, double position)
{
    if (!d || !isfinite(position) || position < 0 ||
        position >= (double)INT64_MAX / d->info.sample_rate) return INVALID_MEDIA;
    if (interrupted(d)) return CANCELLED;
    if (!d->info.seekable) return UNSUPPORTED;
    int64_t sample = (int64_t)ceil(position * d->info.sample_rate);
    int64_t offset = av_rescale_q(sample, (AVRational){1, d->info.sample_rate}, d->time_base);
    if (offset == INT64_MIN || offset == INT64_MAX ||
        (d->start_timestamp > 0 && offset > INT64_MAX - d->start_timestamp)) return INVALID_MEDIA;
    d->pcm_offset = d->pcm_count = 0;
    d->info.decoded_source_frames = 0;
    d->nested_error = 0;
    av_packet_unref(d->packet);
    av_frame_unref(d->frame);
    d->packet_ready = 0;
    d->packet_tail_count = 0;
    if (d->io) {
        d->io->error = 0;
        d->io->eof_reached = 0;
    }
    int rc = av_seek_frame(d->format, d->stream, d->start_timestamp + offset, AVSEEK_FLAG_BACKWARD);
    avcodec_flush_buffers(d->codec);
    swr_free(&d->swr);
    av_channel_layout_uninit(&d->input_layout);
    d->position_known = 0;
    d->require_timestamp = sample > 0;
    d->drop_before = position * d->info.sample_rate;
    d->drain_sent = d->codec_ended = d->ended = 0;
    if (rc < 0) return failure(d, rc);
    return interrupted(d) ? CANCELLED : 0;
}

void meow_audio_decoder_cancel(MeowAudioDecoder *d)
{
    if (d) atomic_store_explicit(&d->cancelled, 1, memory_order_release);
}

void meow_audio_decoder_resume(MeowAudioDecoder *d)
{
    if (!d) return;
    if (d->input_error == CANCELLED) d->input_error = 0;
    if (d->nested_error == AVERROR_EXIT || d->nested_error == AVERROR(EINTR)) d->nested_error = 0;
    if (d->io && d->io->error == AVERROR_EXIT) {
        d->io->error = 0;
        d->io->eof_reached = 0;
    }
    for (NestedIO *entry = d->nested; entry; entry = entry->next) {
        if (entry->io->error == AVERROR_EXIT || entry->io->error == AVERROR(EINTR)) {
            entry->io->error = 0;
            entry->io->eof_reached = 0;
        }
    }
    atomic_store_explicit(&d->cancelled, 0, memory_order_release);
}

void meow_audio_decoder_destroy(MeowAudioDecoder *d)
{
    if (!d) return;
    meow_audio_decoder_cancel(d);
    avformat_close_input(&d->format);
    while (d->nested) close_nested(&(AVFormatContext){ .opaque = d }, d->nested->io);
    if (d->io) av_freep(&d->io->buffer);
    avio_context_free(&d->io);
    avcodec_free_context(&d->codec);
    av_packet_free(&d->packet);
    av_frame_free(&d->frame);
    swr_free(&d->swr);
    av_channel_layout_uninit(&d->input_layout);
    av_free(d->pcm_left);
    av_free(d->pcm_right);
    av_free(d->loopback_root);
    free(d);
}
