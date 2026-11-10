#include "video_internal.h"
#include <vlc_fourcc.h>
#include <libavutil/pixfmt.h>
#include <libavutil/mastering_display_metadata.h>
#include <libavutil/display.h>

static vlc_fourcc_t codec_fourcc(const AVCodecParameters *parameters)
{
    switch (parameters->codec_id) {
    case AV_CODEC_ID_H264: return VLC_CODEC_H264;
    case AV_CODEC_ID_HEVC: return VLC_CODEC_HEVC;
    case AV_CODEC_ID_AV1: return VLC_CODEC_AV1;
    case AV_CODEC_ID_VP8: return VLC_CODEC_VP8;
    case AV_CODEC_ID_VP9: return VLC_CODEC_VP9;
    case AV_CODEC_ID_MPEG1VIDEO: case AV_CODEC_ID_MPEG2VIDEO: return VLC_CODEC_MPGV;
    case AV_CODEC_ID_MPEG4: return VLC_CODEC_MP4V;
    case AV_CODEC_ID_H263: return VLC_CODEC_H263;
    case AV_CODEC_ID_WMV1: return VLC_CODEC_WMV1;
    case AV_CODEC_ID_WMV2: return VLC_CODEC_WMV2;
    case AV_CODEC_ID_WMV3: return VLC_CODEC_WMV3;
    case AV_CODEC_ID_VC1: return VLC_CODEC_VC1;
    case AV_CODEC_ID_THEORA: return VLC_CODEC_THEORA;
    case AV_CODEC_ID_MJPEG: return VLC_CODEC_MJPG;
    case AV_CODEC_ID_AAC: case AV_CODEC_ID_AAC_LATM: return VLC_CODEC_MP4A;
    case AV_CODEC_ID_MP2: case AV_CODEC_ID_MP3: return VLC_CODEC_MPGA;
    case AV_CODEC_ID_AC3: return VLC_CODEC_A52;
    case AV_CODEC_ID_EAC3: return VLC_CODEC_EAC3;
    case AV_CODEC_ID_DTS: return VLC_CODEC_DTS;
    case AV_CODEC_ID_TRUEHD: return VLC_CODEC_TRUEHD;
    case AV_CODEC_ID_FLAC: return VLC_CODEC_FLAC;
    case AV_CODEC_ID_OPUS: return VLC_CODEC_OPUS;
    case AV_CODEC_ID_VORBIS: return VLC_CODEC_VORBIS;
    case AV_CODEC_ID_ALAC: return VLC_CODEC_ALAC;
    case AV_CODEC_ID_WMAV1: return VLC_CODEC_WMA1;
    case AV_CODEC_ID_WMAV2: return VLC_CODEC_WMA2;
    case AV_CODEC_ID_WMAPRO: return VLC_CODEC_WMAP;
    case AV_CODEC_ID_PCM_U8: return VLC_CODEC_U8;
    case AV_CODEC_ID_PCM_S16LE: return VLC_CODEC_S16L;
    case AV_CODEC_ID_PCM_S16BE: return VLC_CODEC_S16B;
    case AV_CODEC_ID_PCM_S24LE: return VLC_CODEC_S24L;
    case AV_CODEC_ID_PCM_S24BE: return VLC_CODEC_S24B;
    case AV_CODEC_ID_PCM_S32LE: return VLC_CODEC_S32L;
    case AV_CODEC_ID_PCM_S32BE: return VLC_CODEC_S32B;
    case AV_CODEC_ID_PCM_F32LE: return VLC_CODEC_F32L;
    case AV_CODEC_ID_PCM_F64LE: return VLC_CODEC_F64L;
    case AV_CODEC_ID_TEXT: case AV_CODEC_ID_SUBRIP: return VLC_CODEC_SUBT;
    case AV_CODEC_ID_ASS: case AV_CODEC_ID_SSA: return VLC_CODEC_SSA;
    case AV_CODEC_ID_MOV_TEXT: return VLC_CODEC_TX3G;
    case AV_CODEC_ID_WEBVTT: return VLC_CODEC_WEBVTT;
    case AV_CODEC_ID_DVD_SUBTITLE: return VLC_CODEC_SPU;
    case AV_CODEC_ID_HDMV_PGS_SUBTITLE: return VLC_CODEC_BD_PG;
    case AV_CODEC_ID_RAWVIDEO:
        switch (parameters->format) {
        case AV_PIX_FMT_YUV420P: return VLC_CODEC_I420;
        case AV_PIX_FMT_YUV420P10LE: return VLC_CODEC_I420_10L;
        case AV_PIX_FMT_NV12: return VLC_CODEC_NV12;
        case AV_PIX_FMT_YUV422P: return VLC_CODEC_I422;
        case AV_PIX_FMT_YUV444P: return VLC_CODEC_I444;
        case AV_PIX_FMT_RGB24: return VLC_CODEC_RGB24;
        default: return 0;
        }
    default: return 0;
    }
}

static void video_parameters(video_format_t *video, const AVCodecParameters *parameters)
{
    video->i_width = video->i_visible_width = parameters->width;
    video->i_height = video->i_visible_height = parameters->height;
    video->i_bits_per_pixel = parameters->bits_per_raw_sample ? parameters->bits_per_raw_sample :
                                                              parameters->bits_per_coded_sample;
    video->b_color_range_full = parameters->color_range == AVCOL_RANGE_JPEG;
    switch (parameters->color_primaries) {
    case AVCOL_PRI_BT709: video->primaries = COLOR_PRIMARIES_BT709; break;
    case AVCOL_PRI_BT2020: video->primaries = COLOR_PRIMARIES_BT2020; break;
    case AVCOL_PRI_BT470BG: video->primaries = COLOR_PRIMARIES_BT601_625; break;
    case AVCOL_PRI_SMPTE170M: video->primaries = COLOR_PRIMARIES_BT601_525; break;
    default: break;
    }
    switch (parameters->color_trc) {
    case AVCOL_TRC_BT709: video->transfer = TRANSFER_FUNC_BT709; break;
    case AVCOL_TRC_SMPTE2084: video->transfer = TRANSFER_FUNC_SMPTE_ST2084; break;
    case AVCOL_TRC_ARIB_STD_B67: video->transfer = TRANSFER_FUNC_HLG; break;
    case AVCOL_TRC_IEC61966_2_1: video->transfer = TRANSFER_FUNC_SRGB; break;
    default: break;
    }
    switch (parameters->color_space) {
    case AVCOL_SPC_BT709: video->space = COLOR_SPACE_BT709; break;
    case AVCOL_SPC_BT2020_NCL: case AVCOL_SPC_BT2020_CL: video->space = COLOR_SPACE_BT2020; break;
    case AVCOL_SPC_BT470BG: case AVCOL_SPC_SMPTE170M: video->space = COLOR_SPACE_BT601; break;
    default: break;
    }
    const AVPacketSideData *side = av_packet_side_data_get(parameters->coded_side_data,
        parameters->nb_coded_side_data, AV_PKT_DATA_MASTERING_DISPLAY_METADATA);
    if (side && side->size >= sizeof(AVMasteringDisplayMetadata)) {
        const AVMasteringDisplayMetadata *data = (const void *)side->data;
        if (data->has_primaries) {
            for (unsigned i = 0; i < 3; i++) {
                unsigned source = (i + 1) % 3; /* VLC G,B,R; FFmpeg R,G,B. */
                for (unsigned n = 0; n < 2; n++)
                    video->mastering.primaries[i * 2 + n] =
                        av_rescale(data->display_primaries[source][n].num, 50000,
                                   data->display_primaries[source][n].den);
            }
            for (unsigned n = 0; n < 2; n++)
                video->mastering.white_point[n] =
                    av_rescale(data->white_point[n].num, 50000, data->white_point[n].den);
        }
        if (data->has_luminance) {
            video->mastering.max_luminance =
                av_rescale(data->max_luminance.num, 10000, data->max_luminance.den);
            video->mastering.min_luminance =
                av_rescale(data->min_luminance.num, 10000, data->min_luminance.den);
        }
    }
    side = av_packet_side_data_get(parameters->coded_side_data,
        parameters->nb_coded_side_data, AV_PKT_DATA_CONTENT_LIGHT_LEVEL);
    if (side && side->size >= sizeof(AVContentLightMetadata)) {
        const AVContentLightMetadata *data = (const void *)side->data;
        video->lighting.MaxCLL = data->MaxCLL;
        video->lighting.MaxFALL = data->MaxFALL;
    }
}

int mvk_add_tracks(demux_t *demux)
{
    demux_sys_t *sys = demux->p_sys;
    unsigned count = sys->format->nb_streams;
    if (count <= sys->track_count) return VLC_SUCCESS;
    mvk_track *tracks = realloc(sys->tracks, count * sizeof(*tracks));
    if (!tracks) return VLC_ENOMEM;
    sys->tracks = tracks;
    memset(tracks + sys->track_count, 0, (count - sys->track_count) * sizeof(*tracks));
    unsigned first = sys->track_count;
    sys->track_count = count; /* Cleanup must also own partially added ES on failure. */
    for (unsigned i = first; i < count; i++) {
        tracks[i].dts = VLC_TS_INVALID;
        const AVStream *stream = sys->format->streams[i];
        const AVCodecParameters *parameters = stream->codecpar;
        vlc_fourcc_t codec = codec_fourcc(parameters);
        if (!codec || stream->disposition & AV_DISPOSITION_ATTACHED_PIC) continue;
        int category = parameters->codec_type == AVMEDIA_TYPE_VIDEO ? VIDEO_ES :
                       parameters->codec_type == AVMEDIA_TYPE_AUDIO ? AUDIO_ES :
                       parameters->codec_type == AVMEDIA_TYPE_SUBTITLE ? SPU_ES : UNKNOWN_ES;
        if (category == UNKNOWN_ES) continue;
        es_format_t format;
        es_format_Init(&format, category, codec);
        format.i_id = i;
        format.i_group = 0;
        format.i_original_fourcc = parameters->codec_tag;
        format.i_bitrate = parameters->bit_rate;
        format.i_profile = parameters->profile;
        format.i_level = parameters->level;
        if (parameters->extradata_size > 0) {
            format.p_extra = malloc(parameters->extradata_size);
            if (!format.p_extra) { es_format_Clean(&format); return VLC_ENOMEM; }
            memcpy(format.p_extra, parameters->extradata, parameters->extradata_size);
            format.i_extra = parameters->extradata_size;
        }
        if (category == VIDEO_ES) {
            video_parameters(&format.video, parameters);
            AVRational ratio = parameters->sample_aspect_ratio.num ? parameters->sample_aspect_ratio :
                                                                      stream->sample_aspect_ratio;
            if (ratio.num > 0 && ratio.den > 0) {
                format.video.i_sar_num = ratio.num; format.video.i_sar_den = ratio.den;
            }
            AVRational rate = stream->avg_frame_rate.num ? stream->avg_frame_rate : stream->r_frame_rate;
            if (rate.num > 0 && rate.den > 0) {
                format.video.i_frame_rate = rate.num; format.video.i_frame_rate_base = rate.den;
            }
            if (parameters->codec_id == AV_CODEC_ID_H264 && parameters->extradata_size > 0 &&
                parameters->extradata[0] == 1) format.i_original_fourcc = VLC_FOURCC('a','v','c','1');
            if (parameters->codec_id == AV_CODEC_ID_HEVC && parameters->extradata_size > 0 &&
                parameters->extradata[0] == 1) format.i_original_fourcc = VLC_FOURCC('h','v','c','1');
        } else if (category == AUDIO_ES) {
            format.audio.i_channels = parameters->ch_layout.nb_channels;
            format.audio.i_rate = parameters->sample_rate;
            format.audio.i_bitspersample = parameters->bits_per_coded_sample;
            format.audio.i_blockalign = parameters->block_align;
            if (parameters->codec_id == AV_CODEC_ID_AAC_LATM) {
                format.i_original_fourcc = VLC_FOURCC('L','A','T','M'); format.b_packetized = false;
            } else if (parameters->codec_id == AV_CODEC_ID_AAC && !strcmp(sys->format->iformat->name, "aac")) {
                format.i_original_fourcc = VLC_FOURCC('A','D','T','S'); format.b_packetized = false;
            }
        }
        AVDictionaryEntry *language = av_dict_get(stream->metadata, "language", NULL, 0);
        if (language) format.psz_language = strdup(language->value);
        AVDictionaryEntry *title = av_dict_get(stream->metadata, "title", NULL, 0);
        if (title) format.psz_description = strdup(title->value);
        if (stream->disposition & AV_DISPOSITION_DEFAULT) format.i_priority = ES_PRIORITY_SELECTABLE_MIN + 1;
        tracks[i].es = es_out_Add(demux->out, &format);
        es_format_Clean(&format);
        if (!tracks[i].es) return VLC_EGENERIC;
        if (stream->disposition & AV_DISPOSITION_DEFAULT)
            es_out_Control(demux->out, ES_OUT_SET_ES_DEFAULT, tracks[i].es);
    }
    return VLC_SUCCESS;
}
void mvk_delete_tracks(demux_t *demux)
{
    demux_sys_t *sys = demux->p_sys;
    for (unsigned i = 0; i < sys->track_count; i++)
        if (sys->tracks[i].es) es_out_Del(demux->out, sys->tracks[i].es);
    free(sys->tracks);
    sys->tracks = NULL; sys->track_count = 0;
}

block_t *mvk_packet_block(const AVPacket *packet, const AVStream *stream, int64_t start)
{
    block_t *block = block_Alloc(packet->size);
    if (!block) return NULL;
    memcpy(block->p_buffer, packet->data, packet->size);
    block->i_dts = packet->dts == AV_NOPTS_VALUE ? VLC_TS_INVALID :
        av_rescale_q(packet->dts, stream->time_base, AV_TIME_BASE_Q) - start + VLC_TS_0;
    block->i_pts = packet->pts == AV_NOPTS_VALUE ? VLC_TS_INVALID :
        av_rescale_q(packet->pts, stream->time_base, AV_TIME_BASE_Q) - start + VLC_TS_0;
    block->i_length = packet->duration > 0 ?
        av_rescale_q(packet->duration, stream->time_base, AV_TIME_BASE_Q) : 0;
    if (packet->flags & AV_PKT_FLAG_KEY) block->i_flags |= BLOCK_FLAG_TYPE_I;
    if (packet->flags & AV_PKT_FLAG_CORRUPT) block->i_flags |= BLOCK_FLAG_CORRUPTED;
    return block;
}
