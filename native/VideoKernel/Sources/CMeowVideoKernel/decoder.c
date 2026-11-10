/*
 * Meowfilm native FFmpeg 8 adapter. Media mechanisms adapted from KSPlayer
 * FFmpegDecode.swift / Resample.swift (kintan/kingslay and contributors,
 * GPL-3.0-only). Uses public FFmpeg APIs, not FFmpegKit private structures.
 */
#include "MeowVideoKernel.h"
#include <libavformat/avformat.h>
#include <libavcodec/avcodec.h>
#include <libavutil/avutil.h>
#include <libavutil/dict.h>
#include <libavutil/hwcontext.h>
#include <libavutil/hwcontext_videotoolbox.h>
#include <libavutil/pixdesc.h>
#include <libavutil/time.h>
#include <libavutil/mastering_display_metadata.h>
#include <libswresample/swresample.h>
#include <libswscale/swscale.h>
#include <AudioToolbox/AudioToolbox.h>
#include <CoreVideo/CoreVideo.h>
#include <VideoToolbox/VideoToolbox.h>
#include <stdatomic.h>
#include <pthread.h>
#include <math.h>
#include <string.h>
#include <errno.h>
#include <stdlib.h>

/* This reviewed, same-source patch carries the owning session's measurement
 * with its decoded frame. No private FFmpeg layout crosses this ABI. */
extern int av_meow_videotoolbox_frame_hardware_decode(const AVFrame *frame);

typedef struct {
    AVCodecContext *codec;
    AVRational timebase;
    int index;
    double next_pts;
    int drain_sent;
} MVKTrack;

struct MVKDecoder {
    AVFormatContext *format;
    MVKTrack video;
    MVKTrack audio;
    AVPacket *packet;
    AVFrame *frame;
    struct SwsContext *scale;
    struct SwrContext *resample;
    AVChannelLayout audio_layout;
    enum AVSampleFormat audio_format;
    int audio_rate;
    CVPixelBufferPoolRef pixel_pool;
    int pool_width, pool_height;
    OSType pool_format;
    atomic_int cancelled;
    atomic_int interrupted;
    atomic_llong deadline;
    int receiving;
    int packet_pending;
    int at_end;
    int encrypted;
    double origin;
    double discard_before;
    MVKMediaInfo info;
};

static pthread_once_t initialization = PTHREAD_ONCE_INIT;

static void discard_log(void *context, int level, const char *format, va_list arguments) {
    (void)context; (void)level; (void)format; (void)arguments;
}

static void initialize(void) {
    /* FFmpeg can log URLs, cookies, dictionary values and KIDs even at error
     * level. Do not expose its raw callback anywhere in this product. */
    av_log_set_callback(discard_log);
    av_log_set_level(AV_LOG_QUIET);
    avformat_network_init();
}

static int interrupt_io(void *opaque) {
    MVKDecoder *decoder = opaque;
    if (atomic_load(&decoder->cancelled) || atomic_load(&decoder->interrupted)) return 1;
    long long deadline = atomic_load(&decoder->deadline);
    return deadline && av_gettime_relative() >= deadline;
}

static int result(MVKDecoder *decoder, int code, int fallback) {
    if (atomic_load(&decoder->cancelled)) return MVK_CANCELLED;
    if (atomic_load(&decoder->interrupted)) return MVK_INTERRUPTED;
    long long deadline = atomic_load(&decoder->deadline);
    if (code == AVERROR(ETIMEDOUT) || (deadline && av_gettime_relative() >= deadline)) return MVK_TIMED_OUT;
    if (code == AVERROR(EIO) || code == AVERROR(ECONNRESET) || code == AVERROR(ECONNREFUSED)
        || code == AVERROR_HTTP_UNAUTHORIZED || code == AVERROR_HTTP_FORBIDDEN
        || code == AVERROR_HTTP_NOT_FOUND || code == AVERROR_HTTP_SERVER_ERROR) return MVK_TRANSPORT_FAILED;
    /* Locked MOV get_key_from_kid returns -1 (EPERM) for an absent KID.
     * Invalid encrypted sample data is also a decryption failure; AES-CTR
     * provides no authentication with which to distinguish a wrong key. */
    return decoder->encrypted && (code == AVERROR_INVALIDDATA || code == AVERROR(EPERM))
        ? MVK_DECRYPTION_FAILED : fallback;
}

static int open_io(AVFormatContext *context, AVIOContext **io, const char *url,
                   int flags, AVDictionary **options) {
    av_dict_set(options, "rw_timeout", "15000000", 0);
    av_dict_set(options, "tls_verify", "1", 0);
    av_dict_set(options, "reconnect", "0", 0);
    av_dict_set(options, "reconnect_on_network_error", "0", 0);
    return avio_open2(io, url, flags, &context->interrupt_callback, options);
}

static enum AVPixelFormat video_format(AVCodecContext *codec, const enum AVPixelFormat *formats) {
    if (codec->hw_device_ctx) {
        for (const enum AVPixelFormat *format = formats; *format != AV_PIX_FMT_NONE; format++) {
            if (*format == AV_PIX_FMT_VIDEOTOOLBOX) return *format;
        }
    }
    /* A decoder may reject VT for a particular profile/size. Preserve the
     * native software depth instead of switching media or transcoding it. */
    for (const enum AVPixelFormat *format = formats; *format != AV_PIX_FMT_NONE; format++) {
        const AVPixFmtDescriptor *descriptor = av_pix_fmt_desc_get(*format);
        if (descriptor && !(descriptor->flags & AV_PIX_FMT_FLAG_HWACCEL)) return *format;
    }
    return AV_PIX_FMT_NONE;
}

static int open_track(MVKDecoder *decoder, MVKTrack *track, enum AVMediaType type) {
    const AVCodec *codec = NULL;
    int index = av_find_best_stream(decoder->format, type, -1, -1, &codec, 0);
    track->index = index;
    if (index < 0) return type == AVMEDIA_TYPE_AUDIO && index == AVERROR_STREAM_NOT_FOUND
        ? MVK_OK : MVK_FORMAT_FAILED;
    AVStream *stream = decoder->format->streams[index];
    track->codec = avcodec_alloc_context3(codec);
    if (!track->codec) return MVK_DECODE_FAILED;
    if (avcodec_parameters_to_context(track->codec, stream->codecpar) < 0) return MVK_DECODE_FAILED;
    track->timebase = stream->time_base;
    track->codec->pkt_timebase = track->timebase;
    track->codec->thread_count = 2;
    track->codec->err_recognition = AV_EF_CRCCHECK | AV_EF_BITSTREAM | AV_EF_EXPLODE;
    if (type == AVMEDIA_TYPE_VIDEO) {
        track->codec->get_format = video_format;
        for (int i = 0;; i++) {
            const AVCodecHWConfig *config = avcodec_get_hw_config(codec, i);
            if (!config) break;
            if (config->device_type == AV_HWDEVICE_TYPE_VIDEOTOOLBOX
                && (config->methods & AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX)) {
                av_hwdevice_ctx_create(&track->codec->hw_device_ctx, AV_HWDEVICE_TYPE_VIDEOTOOLBOX,
                                       NULL, NULL, 0);
                break;
            }
        }
    }
    if (avcodec_open2(track->codec, codec, NULL) < 0) return MVK_DECODE_FAILED;
    track->next_pts = 0;
    return MVK_OK;
}

MVKDecoder *mvk_create(void) {
    pthread_once(&initialization, initialize);
    MVKDecoder *decoder = av_mallocz(sizeof(*decoder));
    if (!decoder) return NULL;
    atomic_init(&decoder->cancelled, 0);
    atomic_init(&decoder->interrupted, 0);
    atomic_init(&decoder->deadline, 0);
    decoder->video.index = decoder->audio.index = -1;
    decoder->info.hardware_decode = -1;
    decoder->info.duration = NAN;
    decoder->packet = av_packet_alloc();
    decoder->frame = av_frame_alloc();
    if (!decoder->packet || !decoder->frame) { mvk_destroy(decoder); return NULL; }
    return decoder;
}

int32_t mvk_open(MVKDecoder *decoder, const char *url, const char *format,
                 const char *kid_keys) {
    if (atomic_load(&decoder->cancelled)) return MVK_CANCELLED;
    if (decoder->format) return MVK_OPEN_FAILED;
    atomic_store(&decoder->deadline, av_gettime_relative() + 15000000);
    decoder->format = avformat_alloc_context();
    if (!decoder->format) return MVK_OPEN_FAILED;
    decoder->format->interrupt_callback = (AVIOInterruptCB){ interrupt_io, decoder };
    decoder->format->io_open = open_io;
    decoder->format->flags |= AVFMT_FLAG_GENPTS;
    AVDictionary *options = NULL;
    av_dict_set(&options, "rw_timeout", "15000000", 0);
    av_dict_set(&options, "tls_verify", "1", 0);
    av_dict_set(&options, "reconnect", "0", 0);
    /* Signed URLs and manifest references need not have an extension.
     * The demuxer/probe validates the actual media content. */
    av_dict_set(&options, "extension_picky", "0", 0);
    if (kid_keys && kid_keys[0]) {
        decoder->encrypted = 1;
        av_dict_set(&options, "cenc_decryption_keys", kid_keys, 0);
        av_dict_set(&options, "decryption_keys", kid_keys, 0);
    }
    const AVInputFormat *input = format && format[0] ? av_find_input_format(format) : NULL;
    int status = avformat_open_input(&decoder->format, url, input, &options);
    av_dict_free(&options);
    if (status < 0) return result(decoder, status,
        status == AVERROR_INVALIDDATA ? MVK_FORMAT_FAILED : MVK_OPEN_FAILED);
    status = avformat_find_stream_info(decoder->format, NULL);
    if (decoder->format->pb && decoder->format->pb->error < 0
        && decoder->format->pb->error != AVERROR_EOF)
        status = decoder->format->pb->error;
    if (status < 0) return result(decoder, status, MVK_FORMAT_FAILED);
    if (decoder->format->start_time != AV_NOPTS_VALUE)
        decoder->origin = decoder->format->start_time / (double)AV_TIME_BASE;
    int opened = open_track(decoder, &decoder->video, AVMEDIA_TYPE_VIDEO);
    if (opened != MVK_OK) return opened;
    opened = open_track(decoder, &decoder->audio, AVMEDIA_TYPE_AUDIO);
    if (opened != MVK_OK) return opened;
    decoder->info.duration = decoder->format->duration > 0
        ? decoder->format->duration / (double)AV_TIME_BASE : NAN;
    AVCodecParameters *parameters = decoder->format->streams[decoder->video.index]->codecpar;
    decoder->info.video_codec = parameters->codec_id;
    decoder->info.video_profile = parameters->profile;
    decoder->info.bit_depth = parameters->bits_per_raw_sample;
    decoder->info.width = parameters->width; decoder->info.height = parameters->height;
    decoder->info.has_audio = decoder->audio.codec != NULL;
    atomic_store(&decoder->deadline, 0);
    return MVK_OK;
}

static void attach_hdr(CVPixelBufferRef pixel, AVFrame *frame) {
    av_vt_pixbuf_set_attachments(NULL, pixel, frame);
    AVFrameSideData *data = av_frame_get_side_data(frame, AV_FRAME_DATA_MASTERING_DISPLAY_METADATA);
    AVMasteringDisplayMetadata *metadata = data ? (AVMasteringDisplayMetadata *)data->data : NULL;
    if (metadata && metadata->has_primaries && metadata->has_luminance) {
        uint16_t chromaticity[8];
        /* ISO/IEC 23008-2 D.2.28 SEI uses G,B,R; AVFrame metadata is R,G,B.
         * See the locked FFmpeg HEVC SEI writer, not the MP4 mdcv atom order. */
        const int order[3] = { 1, 2, 0 };
        for (int i = 0; i < 3; i++) for (int j = 0; j < 2; j++)
            chromaticity[i * 2 + j] = CFSwapInt16HostToBig((uint16_t)llround(av_q2d(metadata->display_primaries[order[i]][j]) * 50000));
        for (int j = 0; j < 2; j++)
            chromaticity[6 + j] = CFSwapInt16HostToBig((uint16_t)llround(av_q2d(metadata->white_point[j]) * 50000));
        uint32_t luminance[2] = {
            CFSwapInt32HostToBig((uint32_t)llround(av_q2d(metadata->max_luminance) * 10000)),
            CFSwapInt32HostToBig((uint32_t)llround(av_q2d(metadata->min_luminance) * 10000))
        };
        uint8_t bytes[24]; memcpy(bytes, chromaticity, 16); memcpy(bytes + 16, luminance, 8);
        CFDataRef value = CFDataCreate(kCFAllocatorDefault, bytes, sizeof(bytes));
        CVBufferSetAttachment(pixel, kCVImageBufferMasteringDisplayColorVolumeKey, value, kCVAttachmentMode_ShouldPropagate);
        CFRelease(value);
    } else CVBufferRemoveAttachment(pixel, kCVImageBufferMasteringDisplayColorVolumeKey);
    data = av_frame_get_side_data(frame, AV_FRAME_DATA_CONTENT_LIGHT_LEVEL);
    if (data) {
        AVContentLightMetadata *metadata = (AVContentLightMetadata *)data->data;
        uint16_t bytes[2] = { CFSwapInt16HostToBig(metadata->MaxCLL), CFSwapInt16HostToBig(metadata->MaxFALL) };
        CFDataRef value = CFDataCreate(kCFAllocatorDefault, (uint8_t *)bytes, sizeof(bytes));
        CVBufferSetAttachment(pixel, kCVImageBufferContentLightLevelInfoKey, value, kCVAttachmentMode_ShouldPropagate);
        CFRelease(value);
    } else CVBufferRemoveAttachment(pixel, kCVImageBufferContentLightLevelInfoKey);
}

static int video_sample(MVKDecoder *decoder, AVFrame *frame, double pts, double duration,
                        CMSampleBufferRef *sample) {
    CVPixelBufferRef pixel = NULL;
    enum AVPixelFormat format = frame->format;
    const AVPixFmtDescriptor *descriptor = av_pix_fmt_desc_get(format);
    if (format == AV_PIX_FMT_VIDEOTOOLBOX) {
        pixel = (CVPixelBufferRef)frame->data[3];
        if (!pixel) return MVK_OUTPUT_FAILED;
        CVPixelBufferRetain(pixel);
        decoder->info.hardware_decode = av_meow_videotoolbox_frame_hardware_decode(frame);
        if (frame->hw_frames_ctx) {
            AVHWFramesContext *hardware = (AVHWFramesContext *)frame->hw_frames_ctx->data;
            descriptor = av_pix_fmt_desc_get(hardware->sw_format);
        }
    } else {
        decoder->info.hardware_decode = 0;
        int depth = descriptor ? descriptor->comp[0].depth : decoder->info.bit_depth;
        enum AVPixelFormat destination = depth > 8 ? AV_PIX_FMT_P010LE : AV_PIX_FMT_NV12;
        OSType type = av_map_videotoolbox_format_from_pixfmt2(destination, frame->color_range == AVCOL_RANGE_JPEG);
        if (!type) return MVK_OUTPUT_FAILED;
        if (!decoder->pixel_pool || decoder->pool_width != frame->width
            || decoder->pool_height != frame->height || decoder->pool_format != type) {
            if (decoder->pixel_pool) CVPixelBufferPoolRelease(decoder->pixel_pool);
            CFMutableDictionaryRef attributes = CFDictionaryCreateMutable(kCFAllocatorDefault, 0,
                &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
            CFNumberRef width = CFNumberCreate(NULL, kCFNumberIntType, &frame->width);
            CFNumberRef height = CFNumberCreate(NULL, kCFNumberIntType, &frame->height);
            CFNumberRef pixel_type = CFNumberCreate(NULL, kCFNumberSInt32Type, &type);
            CFDictionaryRef surface = CFDictionaryCreate(NULL, NULL, NULL, 0,
                &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
            CFDictionarySetValue(attributes, kCVPixelBufferWidthKey, width);
            CFDictionarySetValue(attributes, kCVPixelBufferHeightKey, height);
            CFDictionarySetValue(attributes, kCVPixelBufferPixelFormatTypeKey, pixel_type);
            CFDictionarySetValue(attributes, kCVPixelBufferIOSurfacePropertiesKey, surface);
            CFDictionarySetValue(attributes, kCVPixelBufferMetalCompatibilityKey, kCFBooleanTrue);
            CVReturn status = CVPixelBufferPoolCreate(NULL, NULL, attributes, &decoder->pixel_pool);
            CFRelease(width); CFRelease(height); CFRelease(pixel_type); CFRelease(surface); CFRelease(attributes);
            if (status != kCVReturnSuccess) return MVK_OUTPUT_FAILED;
            decoder->pool_width = frame->width; decoder->pool_height = frame->height; decoder->pool_format = type;
        }
        if (CVPixelBufferPoolCreatePixelBuffer(NULL, decoder->pixel_pool, &pixel) != kCVReturnSuccess)
            return MVK_OUTPUT_FAILED;
        decoder->scale = sws_getCachedContext(decoder->scale, frame->width, frame->height, format,
            frame->width, frame->height, destination, SWS_FAST_BILINEAR, NULL, NULL, NULL);
        if (!decoder->scale) { CVPixelBufferRelease(pixel); return MVK_OUTPUT_FAILED; }
        int matrix = frame->colorspace == AVCOL_SPC_BT2020_NCL ? SWS_CS_BT2020 :
            frame->colorspace == AVCOL_SPC_BT709 ? SWS_CS_ITU709 : SWS_CS_DEFAULT;
        const int *coefficients = sws_getCoefficients(matrix);
        int full = frame->color_range == AVCOL_RANGE_JPEG;
        sws_setColorspaceDetails(decoder->scale, coefficients, full, coefficients, full, 0, 1 << 16, 1 << 16);
        CVPixelBufferLockBaseAddress(pixel, 0);
        uint8_t *planes[4] = { CVPixelBufferGetBaseAddressOfPlane(pixel, 0), CVPixelBufferGetBaseAddressOfPlane(pixel, 1), NULL, NULL };
        int strides[4] = { (int)CVPixelBufferGetBytesPerRowOfPlane(pixel, 0),
                           (int)CVPixelBufferGetBytesPerRowOfPlane(pixel, 1), 0, 0 };
        int converted = sws_scale(decoder->scale, (const uint8_t *const *)frame->data, frame->linesize,
                                  0, frame->height, planes, strides);
        CVPixelBufferUnlockBaseAddress(pixel, 0);
        if (converted < 0) { CVPixelBufferRelease(pixel); return MVK_OUTPUT_FAILED; }
    }
    attach_hdr(pixel, frame);
    descriptor = av_pix_fmt_desc_get(av_map_videotoolbox_format_to_pixfmt(CVPixelBufferGetPixelFormatType(pixel)));
    decoder->info.bit_depth = descriptor ? descriptor->comp[0].depth : decoder->info.bit_depth;
    decoder->info.width = frame->width; decoder->info.height = frame->height;
    decoder->info.pixel_format = CVPixelBufferGetPixelFormatType(pixel);
    CMVideoFormatDescriptionRef description = NULL;
    OSStatus status = CMVideoFormatDescriptionCreateForImageBuffer(NULL, pixel, &description);
    if (status == noErr) {
        CMSampleTimingInfo timing = { CMTimeMakeWithSeconds(duration, 1000000),
            CMTimeMakeWithSeconds(pts, 1000000), kCMTimeInvalid };
        status = CMSampleBufferCreateReadyWithImageBuffer(NULL, pixel, description, &timing, sample);
    }
    if (description) CFRelease(description);
    CVPixelBufferRelease(pixel);
    return status == noErr ? MVK_OK : MVK_OUTPUT_FAILED;
}

static int audio_sample(MVKDecoder *decoder, AVFrame *frame, double *pts, double *duration,
                        CMSampleBufferRef *sample) {
    if (!decoder->resample || decoder->audio_rate != frame->sample_rate
        || decoder->audio_format != frame->format
        || av_channel_layout_compare(&decoder->audio_layout, &frame->ch_layout)) {
        swr_free(&decoder->resample);
        av_channel_layout_uninit(&decoder->audio_layout);
        AVChannelLayout output = AV_CHANNEL_LAYOUT_STEREO;
        int status = swr_alloc_set_opts2(&decoder->resample, &output, AV_SAMPLE_FMT_FLT, frame->sample_rate,
            &frame->ch_layout, frame->format, frame->sample_rate, 0, NULL);
        if (status < 0 || swr_init(decoder->resample) < 0) return MVK_OUTPUT_FAILED;
        av_channel_layout_copy(&decoder->audio_layout, &frame->ch_layout);
        decoder->audio_format = frame->format; decoder->audio_rate = frame->sample_rate;
    }
    int capacity = swr_get_out_samples(decoder->resample, frame->nb_samples);
    uint8_t *bytes = av_malloc((size_t)capacity * 2 * sizeof(float));
    if (!bytes) return MVK_OUTPUT_FAILED;
    int frames = swr_convert(decoder->resample, &bytes, capacity,
                             (const uint8_t **)frame->extended_data, frame->nb_samples);
    if (frames < 0) { av_free(bytes); return MVK_OUTPUT_FAILED; }
    int skip = *pts < decoder->discard_before
        ? (int)fmin(frames, ceil((decoder->discard_before - *pts) * frame->sample_rate)) : 0;
    frames -= skip;
    *pts += skip / (double)frame->sample_rate;
    *duration = frames / (double)frame->sample_rate;
    if (!frames) { av_free(bytes); return MVK_INTERRUPTED; }
    AudioStreamBasicDescription asbd = {
        .mSampleRate = frame->sample_rate, .mFormatID = kAudioFormatLinearPCM,
        .mFormatFlags = kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
        .mBytesPerPacket = 8, .mFramesPerPacket = 1, .mBytesPerFrame = 8,
        .mChannelsPerFrame = 2, .mBitsPerChannel = 32
    };
    CMAudioFormatDescriptionRef description = NULL;
    CMBlockBufferRef block = NULL;
    OSStatus status = CMAudioFormatDescriptionCreate(NULL, &asbd, 0, NULL, 0, NULL, NULL, &description);
    size_t length = (size_t)frames * 8;
    if (status == noErr) status = CMBlockBufferCreateWithMemoryBlock(NULL, NULL, length,
        kCFAllocatorDefault, NULL, 0, length, 0, &block);
    if (status == noErr) status = CMBlockBufferReplaceDataBytes(bytes + (size_t)skip * 8, block, 0, length);
    if (status == noErr) status = CMAudioSampleBufferCreateReadyWithPacketDescriptions(NULL, block,
        description, frames, CMTimeMakeWithSeconds(*pts, 1000000), NULL, sample);
    if (description) CFRelease(description);
    if (block) CFRelease(block);
    av_free(bytes);
    decoder->info.sample_rate = frame->sample_rate; decoder->info.audio_channels = 2;
    return status == noErr ? MVK_OK : MVK_OUTPUT_FAILED;
}

static int frame_sample(MVKDecoder *decoder, MVKTrack *track, int kind,
                        CMSampleBufferRef *sample, double *end_time) {
    AVFrame *frame = decoder->frame;
    int64_t timestamp = frame->best_effort_timestamp;
    if (timestamp == AV_NOPTS_VALUE) timestamp = frame->pts;
    double pts = timestamp == AV_NOPTS_VALUE ? track->next_pts : timestamp * av_q2d(track->timebase) - decoder->origin;
    double duration = frame->duration > 0 ? frame->duration * av_q2d(track->timebase) :
        kind == MVK_AUDIO ? frame->nb_samples / (double)frame->sample_rate : 0;
    if (kind == MVK_VIDEO && duration <= 0) {
        AVRational rate = av_guess_frame_rate(decoder->format, decoder->format->streams[track->index], frame);
        duration = rate.num > 0 && rate.den > 0 ? av_q2d(av_inv_q(rate)) : 1.0 / 30;
    }
    track->next_pts = pts + duration;
    if (kind == MVK_VIDEO && pts + duration < decoder->discard_before) return MVK_INTERRUPTED;
    if (kind == MVK_VIDEO && pts < decoder->discard_before) pts = decoder->discard_before;
    if (pts < 0) pts = 0;
    int status = kind == MVK_VIDEO ? video_sample(decoder, frame, pts, duration, sample)
        : audio_sample(decoder, frame, &pts, &duration, sample);
    *end_time = pts + duration;
    return status;
}

int32_t mvk_read(MVKDecoder *decoder, CMSampleBufferRef *sample, int32_t *kind, double *end_time) {
    *sample = NULL;
    atomic_store(&decoder->deadline, av_gettime_relative() + 15000000);
    for (;;) {
        if (atomic_load(&decoder->cancelled)) return MVK_CANCELLED;
        if (atomic_load(&decoder->interrupted)) return MVK_INTERRUPTED;
        if (interrupt_io(decoder)) return MVK_TIMED_OUT;
        MVKTrack *track = decoder->receiving == MVK_VIDEO ? &decoder->video :
            decoder->receiving == MVK_AUDIO ? &decoder->audio : NULL;
        if (track) {
            int status = avcodec_receive_frame(track->codec, decoder->frame);
            if (status == 0) {
                *kind = decoder->receiving;
                int converted = frame_sample(decoder, track, *kind, sample, end_time);
                av_frame_unref(decoder->frame);
                if (converted == MVK_INTERRUPTED) continue; // pre-seek frame, not cancellation
                if (converted == MVK_OK) atomic_store(&decoder->deadline, 0);
                return converted;
            }
            if (status != AVERROR_EOF && status != AVERROR(EAGAIN))
                return result(decoder, status, MVK_DECODE_FAILED);
            decoder->receiving = 0;
        }
        if (decoder->packet_pending) {
            MVKTrack *target = decoder->packet->stream_index == decoder->video.index ? &decoder->video : &decoder->audio;
            int status = avcodec_send_packet(target->codec, decoder->packet);
            decoder->receiving = target == &decoder->video ? MVK_VIDEO : MVK_AUDIO;
            if (status == AVERROR(EAGAIN)) continue;
            decoder->packet_pending = 0; av_packet_unref(decoder->packet);
            if (status < 0) return result(decoder, status, MVK_DECODE_FAILED);
            continue;
        }
        if (decoder->at_end) {
            MVKTrack *target = !decoder->video.drain_sent ? &decoder->video :
                decoder->audio.codec && !decoder->audio.drain_sent ? &decoder->audio : NULL;
            if (!target) { atomic_store(&decoder->deadline, 0); return MVK_END; }
            int status = avcodec_send_packet(target->codec, NULL);
            if (status < 0 && status != AVERROR_EOF && status != AVERROR(EAGAIN))
                return result(decoder, status, MVK_DECODE_FAILED);
            if (status != AVERROR(EAGAIN)) target->drain_sent = 1;
            decoder->receiving = target == &decoder->video ? MVK_VIDEO : MVK_AUDIO;
            continue;
        }
        int status = av_read_frame(decoder->format, decoder->packet);
        if (decoder->format->pb && decoder->format->pb->error < 0
            && decoder->format->pb->error != AVERROR_EOF) {
            av_packet_unref(decoder->packet);
            return result(decoder, decoder->format->pb->error, MVK_TRANSPORT_FAILED);
        }
        if (status == AVERROR_EOF) { decoder->at_end = 1; continue; }
        if (status < 0) return result(decoder, status, MVK_TRANSPORT_FAILED);
        /* MOV/DASH attaches this public side data when it could not decrypt.
         * Never feed ciphertext to a codec and mistake a concealed frame for
         * successful ClearKey output. No commercial CDM path is implied. */
        size_t encryption_size = 0;
        if (av_packet_get_side_data(decoder->packet, AV_PKT_DATA_ENCRYPTION_INFO, &encryption_size)) {
            av_packet_unref(decoder->packet);
            return MVK_DECRYPTION_FAILED;
        }
        if (decoder->packet->stream_index == decoder->video.index
            || (decoder->audio.codec && decoder->packet->stream_index == decoder->audio.index)) {
            decoder->packet_pending = 1;
        } else av_packet_unref(decoder->packet);
    }
}

CMSampleBufferRef mvk_copy_sample(MVKDecoder *decoder, int32_t *status, int32_t *kind, double *end_time) {
    CMSampleBufferRef sample = NULL;
    *status = mvk_read(decoder, &sample, kind, end_time);
    return sample;
}

int32_t mvk_seek(MVKDecoder *decoder, double position) {
    if (atomic_load(&decoder->cancelled)) return MVK_CANCELLED;
    atomic_store(&decoder->interrupted, 0);
    atomic_store(&decoder->deadline, av_gettime_relative() + 15000000);
    if (decoder->format->pb) {
        /* An interrupted read must not leave AVIO's error/eof latch active. */
        decoder->format->pb->error = 0; decoder->format->pb->eof_reached = 0;
    }
    int64_t time = llround((position + decoder->origin) * AV_TIME_BASE);
    int status = avformat_seek_file(decoder->format, -1, INT64_MIN, time, time, AVSEEK_FLAG_BACKWARD);
    if (status < 0) return result(decoder, status, MVK_SEEK_FAILED);
    avcodec_flush_buffers(decoder->video.codec);
    if (decoder->audio.codec) avcodec_flush_buffers(decoder->audio.codec);
    swr_free(&decoder->resample);
    av_packet_unref(decoder->packet); av_frame_unref(decoder->frame);
    decoder->packet_pending = 0; decoder->receiving = 0; decoder->at_end = 0;
    decoder->video.drain_sent = decoder->audio.drain_sent = 0;
    decoder->video.next_pts = decoder->audio.next_pts = position;
    decoder->discard_before = position;
    atomic_store(&decoder->deadline, 0);
    return MVK_OK;
}

void mvk_media_info(MVKDecoder *decoder, MVKMediaInfo *info) { *info = decoder->info; }
void mvk_interrupt(MVKDecoder *decoder) { atomic_store(&decoder->interrupted, 1); }
void mvk_cancel(MVKDecoder *decoder) { atomic_store(&decoder->cancelled, 1); }

void mvk_destroy(MVKDecoder *decoder) {
    if (!decoder) return;
    avformat_close_input(&decoder->format);
    avcodec_free_context(&decoder->video.codec); avcodec_free_context(&decoder->audio.codec);
    av_packet_free(&decoder->packet); av_frame_free(&decoder->frame);
    sws_freeContext(decoder->scale); swr_free(&decoder->resample);
    av_channel_layout_uninit(&decoder->audio_layout);
    if (decoder->pixel_pool) CVPixelBufferPoolRelease(decoder->pixel_pool);
    memset(decoder, 0, sizeof(*decoder)); av_free(decoder);
}

const char *mvk_codec_name(int32_t codec) { return avcodec_get_name(codec); }
