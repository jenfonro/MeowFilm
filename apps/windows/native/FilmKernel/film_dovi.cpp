/* NAL 63 extraction is adapted from FFmpeg libavcodec/bsf/dovi_split.c,
 * commit 0834db516348d253b2c51f969188501d324c698d, LGPL-2.1-or-later.
 * Source SHA-256: 00da865a8d3dbd6b7d993c0121554201525fe5d4480ac8a39f2ab161a4a1714a.
 * Only the NAL extraction mechanism is used, not the master BSF/API.
 * Decoder and metadata APIs target FFmpeg 8.1.2 (38b88335f99e76ed89ff3c93f877fdefce736c13)
 * with the adjacent ffmpeg-dovi.patch. No other player's code is used. */
#include "film_dovi.h"
#include <climits>
#include <cmath>
#include <cstring>
#include <deque>
#include <map>
#include <utility>
extern "C" {
#include <libavformat/avformat.h>
#include <libavutil/dovi_meta.h>
#include <libavutil/intreadwrite.h>
#include <libavutil/mathematics.h>
#include <libavutil/mem.h>
}

#ifndef FF_MEOWFILM_DOVI
#error FilmDovi requires the locked FFmpeg 8.1.2 ffmpeg-dovi.patch
#endif

namespace {
constexpr size_t max_frames = 32; // HEVC DPB + frame threading, not a time-based timeout.
constexpr size_t max_prefix = 1024 * 1024;
constexpr size_t max_packet = INT_MAX - AV_INPUT_BUFFER_PADDING_SIZE;

bool header(const uint8_t *nal, size_t size)
{
    return size >= 2 && !(nal[0] & 0x80) && (nal[1] & 7);
}
unsigned type(const uint8_t *nal) { return (nal[0] >> 1) & 63; }
unsigned layer(const uint8_t *nal) { return ((nal[0] & 1) << 5) | (nal[1] >> 3); }

bool append(std::vector<uint8_t> &out, const uint8_t *nal, size_t size)
{
    if (size > max_packet - 4 || out.size() > max_packet - 4 - size) return false;
    static const uint8_t prefix[] = {0, 0, 0, 1};
    out.insert(out.end(), prefix, prefix + 4);
    out.insert(out.end(), nal, nal + size);
    return true;
}

template<class Visit>
bool walk(const uint8_t *data, size_t size, int length, Visit visit)
{
    if ((!data && size) || length < 0 || length > 4) return false;
    if (!size) return true;
    if (length) {
        size_t offset = 0;
        while (offset < size) {
            if (size - offset < static_cast<size_t>(length)) return false;
            uint32_t count = 0;
            for (int i = 0; i < length; i++) count = (count << 8) | data[offset++];
            if (count > size - offset || !header(data + offset, count) ||
                !visit(data + offset, count)) return false;
            offset += count;
        }
        return true;
    }
    auto prefix = [&](size_t from) {
        for (; size - from >= 3; from++)
            if (!data[from] && !data[from + 1] && data[from + 2] == 1) return from;
        return size;
    };
    size_t start = prefix(0);
    if (start == size) return false;
    for (size_t i = 0; i < start; i++)
        if (data[i]) return false; // Only leading_zero_8bits may precede a start code.
    while (start < size) {
        const size_t payload = start + 3;
        const size_t next = prefix(payload);
        size_t end = next;
        while (end > payload && !data[end - 1]) end--; // Annex B trailing_zero_8bits.
        if (!header(data + payload, end - payload) ||
            !visit(data + payload, end - payload)) return false;
        start = next;
    }
    return true;
}

// Fast non-fatal probe for raw HEVC streams without container DOVI_CONF:
// checks whether NAL 62 (RPU) or NAL 63 (EL) is present without rejecting
// ordinary HEVC packets that carry padding, corruption or non-DV extensions.
bool probe_has_dovi_nal(const uint8_t *data, size_t size, int length)
{
    if (!data || !size || length < 0 || length > 4) return false;
    bool found = false;
    if (length) {
        size_t offset = 0;
        while (offset + static_cast<size_t>(length) <= size) {
            uint32_t count = 0;
            for (int i = 0; i < length; i++) count = (count << 8) | data[offset++];
            if (count > size - offset) break;
            if (count >= 2 && header(data + offset, count)) {
                const unsigned kind = type(data + offset);
                if (kind == 62 || kind == 63) { found = true; break; }
            }
            offset += count;
        }
        return found;
    }
    size_t i = 0;
    while (i + 5 <= size) {
        if (!data[i] && !data[i + 1] && data[i + 2] == 1) {
            const uint8_t *nal = data + i + 3;
            if (header(nal, size - (i + 3))) {
                const unsigned kind = type(nal);
                if (kind == 62 || kind == 63) { found = true; break; }
            }
            i += 3;
        } else {
            ++i;
        }
    }
    return found;
}

bool select_el(const uint8_t *nal, size_t size, std::vector<uint8_t> &out,
               bool enhancement_only)
{
    if (type(nal) == 63) {
        if (layer(nal) || size < 4) return false;
        nal += 2; size -= 2;
        if (!header(nal, size) || layer(nal) || type(nal) >= 62) return false;
    } else if (!enhancement_only) {
        return true;
    } else if (layer(nal) || type(nal) >= 62) {
        return false;
    }
    return append(out, nal, size);
}
}

bool FilmDoviNal::split(const uint8_t *data, size_t size, int length, Packet &output)
{
    Packet parsed;
    unsigned base_starts = 0, el_starts = 0;
    bool valid = walk(data, size, length, [&](const uint8_t *nal, size_t bytes) {
        const unsigned kind = type(nal);
        if (kind == 62) {
            // Accept legal temporal_id >= 0 (header() already ensures temporal_id_plus1 >= 1).
            if (layer(nal) || bytes <= 2 || parsed.rpu) return false;
            parsed.rpu = true;
        } else if (kind == 63) {
            if (!select_el(nal, bytes, parsed.enhancement, false)) return false;
            if (type(nal + 2) <= 31) {
                if (bytes < 5) return false;
                parsed.enhancement_picture = true;
                el_starts += (nal[4] >> 7);
            }
        } else if (kind <= 31 && !layer(nal)) {
            if (bytes < 3) return false;
            parsed.base_picture = true;
            base_starts += (nal[2] >> 7);
        }
        return true;
    });
    if (!valid || (parsed.base_picture && base_starts != 1) ||
        (parsed.enhancement_picture && el_starts != 1)) return false;
    output = std::move(parsed);
    return true;
}

bool FilmDoviNal::configuration(const uint8_t *data, size_t size, int &length,
                               std::vector<uint8_t> &output, bool enhancement_only)
{
    if (!data && size) return false;
    std::vector<uint8_t> parsed;
    int parsed_length = 0;
    const bool hvcc = size >= 23 &&
        (data[0] == 1 || (data[0] == 0 && (data[1] || data[2] > 1)));
    if (hvcc) {
        parsed_length = (data[21] & 3) + 1;
        size_t offset = 23;
        for (unsigned array = 0; array < data[22]; array++) {
            if (size - offset < 3) return false;
            const unsigned kind = data[offset++] & 63;
            const unsigned count = AV_RB16(data + offset); offset += 2;
            for (unsigned i = 0; i < count; i++) {
                if (size - offset < 2) return false;
                const unsigned bytes = AV_RB16(data + offset); offset += 2;
                if (bytes > size - offset || !header(data + offset, bytes) ||
                    type(data + offset) != kind ||
                    !select_el(data + offset, bytes, parsed, enhancement_only)) return false;
                offset += bytes;
            }
        }
        if (offset != size) return false;
    } else if (!walk(data, size, 0, [&](const uint8_t *nal, size_t bytes) {
        return select_el(nal, bytes, parsed, enhancement_only);
    })) {
        return false;
    }
    length = parsed_length;
    output = std::move(parsed);
    return true;
}

struct FilmDoviInput::Impl {
    struct PacketDelete { void operator()(AVPacket *packet) const { av_packet_free(&packet); } };
    using Packet = std::unique_ptr<AVPacket, PacketDelete>;
    AVCodecParameters *parameters = avcodec_parameters_alloc();
    int base_index = -1, enhancement_index = -1, base_length = 0, el_length = 0;
    AVRational base_time{0, 1}, el_time{0, 1};
    std::map<int64_t, Packet> bases, enhancements;
    std::deque<int64_t> decode_order;
    size_t bytes = 0;
    static constexpr size_t byte_limit = 16 * 1024 * 1024;
    ~Impl() { avcodec_parameters_free(&parameters); }
    static size_t cost(const AVPacket *packet)
    {
        if (packet->size < 0 || static_cast<size_t>(packet->size) > byte_limit) return byte_limit + 1;
        size_t size = sizeof(AVPacket) + packet->size;
        for (int i = 0; i < packet->side_data_elems; i++) {
            if (packet->side_data[i].size > byte_limit || size > byte_limit - packet->side_data[i].size)
                return byte_limit + 1;
            size += packet->side_data[i].size;
        }
        return size;
    }
};

FilmDoviInput::FilmDoviInput() : impl(std::make_unique<Impl>()) { }
FilmDoviInput::~FilmDoviInput() = default;
FilmDoviInput::FilmDoviInput(FilmDoviInput &&) noexcept = default;
FilmDoviInput &FilmDoviInput::operator=(FilmDoviInput &&) noexcept = default;

int FilmDoviInput::open(const AVFormatContext *format, int preferred_video)
{
    impl = std::make_unique<Impl>();
    auto &p = *impl;
    if (!format || preferred_video < 0 || static_cast<unsigned>(preferred_video) >= format->nb_streams)
        return MFK_INVALID_REQUEST;
    if (!p.parameters) return MFK_DECODE_FAILED;
    p.base_index = preferred_video;
    for (unsigned i = 0; i < format->nb_streams; i++) {
        const auto *par = format->streams[i]->codecpar;
        const auto *sd = av_packet_side_data_get(par->coded_side_data,
            par->nb_coded_side_data, AV_PKT_DATA_DOVI_CONF);
        if (!sd) continue;
        if (sd->size < sizeof(AVDOVIDecoderConfigurationRecord)) return MFK_DOVI_UNSUPPORTED;
        const auto *config = reinterpret_cast<const AVDOVIDecoderConfigurationRecord *>(sd->data);
        if (config->bl_present_flag || !config->el_present_flag) continue;
        if (par->codec_id != AV_CODEC_ID_HEVC || config->dv_profile != 7 ||
            !config->rpu_present_flag || p.enhancement_index >= 0) return MFK_DOVI_UNSUPPORTED;
        p.enhancement_index = static_cast<int>(i);
    }
    if (p.enhancement_index >= 0) {
        auto is_p7_base_candidate = [&](unsigned i, bool &explicit_bl) {
            explicit_bl = false;
            const auto *par = format->streams[i]->codecpar;
            if (static_cast<int>(i) == p.enhancement_index || par->codec_type != AVMEDIA_TYPE_VIDEO ||
                par->codec_id != AV_CODEC_ID_HEVC) return false;
            const auto *sd = av_packet_side_data_get(par->coded_side_data,
                par->nb_coded_side_data, AV_PKT_DATA_DOVI_CONF);
            if (sd) {
                if (sd->size < sizeof(AVDOVIDecoderConfigurationRecord)) return false;
                const auto *config = reinterpret_cast<const AVDOVIDecoderConfigurationRecord *>(sd->data);
                if (config->dv_profile != 7 || !config->bl_present_flag || config->el_present_flag)
                    return false;
                explicit_bl = true;
            }
            return true;
        };
        int unique_explicit = -1;
        int explicit_count = 0;
        int unique_any = -1;
        int any_count = 0;
        for (unsigned i = 0; i < format->nb_streams; i++) {
            bool is_explicit = false;
            if (!is_p7_base_candidate(i, is_explicit)) continue;
            if (is_explicit) { unique_explicit = static_cast<int>(i); ++explicit_count; }
            unique_any = static_cast<int>(i); ++any_count;
        }
        if (preferred_video == p.enhancement_index) {
            // Caller landed on the EL stream; resolve the paired P7 base layer.
            int chosen = -1;
            if (explicit_count == 1) chosen = unique_explicit;
            else if (explicit_count == 0 && any_count == 1) chosen = unique_any;
            else return MFK_DOVI_UNSUPPORTED;
            p.base_index = chosen;
        } else {
            bool preferred_explicit = false;
            const bool preferred_is_candidate =
                is_p7_base_candidate(static_cast<unsigned>(preferred_video), preferred_explicit);
            const bool pair_with_el = preferred_is_candidate &&
                (preferred_explicit || (explicit_count == 0 && any_count == 1));
            p.base_index = preferred_video;
            if (!pair_with_el) {
                // Respect explicit selection of another non-P7-BL video track by dropping EL.
                p.enhancement_index = -1;
            }
        }
    }
    const auto *base = format->streams[p.base_index];
    if (avcodec_parameters_copy(p.parameters, base->codecpar) < 0) return MFK_DECODE_FAILED;
    p.base_time = base->time_base;
    if (p.enhancement_index < 0) return MFK_OK;
    const auto *el = format->streams[p.enhancement_index];
    p.el_time = el->time_base;
    std::vector<uint8_t> unused;
    if (base->codecpar->extradata_size < 0 || el->codecpar->extradata_size < 0 ||
        base->codecpar->extradata_size > static_cast<int>(max_prefix) ||
        el->codecpar->extradata_size > static_cast<int>(max_prefix) ||
        p.base_time.num <= 0 || p.base_time.den <= 0 || p.el_time.num <= 0 || p.el_time.den <= 0 ||
        !FilmDoviNal::configuration(base->codecpar->extradata, base->codecpar->extradata_size,
                                    p.base_length, unused) ||
        !FilmDoviNal::configuration(el->codecpar->extradata, el->codecpar->extradata_size,
                                    p.el_length, unused, true)) return MFK_DOVI_UNSUPPORTED;
    const auto *dv = av_packet_side_data_get(el->codecpar->coded_side_data,
        el->codecpar->nb_coded_side_data, AV_PKT_DATA_DOVI_CONF);
    auto *copy = av_packet_side_data_new(&p.parameters->coded_side_data,
        &p.parameters->nb_coded_side_data, AV_PKT_DATA_DOVI_CONF, dv->size, 0);
    if (!copy) return MFK_DECODE_FAILED;
    memcpy(copy->data, dv->data, dv->size);
    reinterpret_cast<AVDOVIDecoderConfigurationRecord *>(copy->data)->bl_present_flag = 1;
    if (el->codecpar->extradata && el->codecpar->extradata_size > 0) {
        auto *hevc = av_packet_side_data_new(&p.parameters->coded_side_data,
            &p.parameters->nb_coded_side_data, AV_PKT_DATA_HEVC_CONF, el->codecpar->extradata_size, 0);
        if (!hevc) return MFK_DECODE_FAILED;
        memcpy(hevc->data, el->codecpar->extradata, el->codecpar->extradata_size);
    }
    return MFK_OK;
}

int FilmDoviInput::base_index() const { return impl->base_index; }
int FilmDoviInput::enhancement_index() const { return impl->enhancement_index; }
const AVCodecParameters *FilmDoviInput::parameters() const { return impl->parameters; }

int FilmDoviInput::send(const AVPacket *packet)
{
    auto &p = *impl;
    if (!packet || p.enhancement_index < 0) return MFK_INVALID_REQUEST;
    const bool base = packet->stream_index == p.base_index;
    if (!base && packet->stream_index != p.enhancement_index) return MFK_INVALID_REQUEST;
    int64_t pts = packet->pts;
    const auto time = base ? p.base_time : p.el_time;
    if (pts == AV_NOPTS_VALUE || (packet->flags & AV_PKT_FLAG_CORRUPT)) return MFK_DOVI_UNSUPPORTED;
    if (!base) {
        pts = av_rescale_q(packet->pts, time, p.base_time);
        if (av_compare_ts(pts, p.base_time, packet->pts, time)) return MFK_DOVI_UNSUPPORTED;
    }
    auto &packets = base ? p.bases : p.enhancements;
    const size_t size = Impl::cost(packet);
    if (packets.count(pts) || size > Impl::byte_limit ||
        p.bytes > Impl::byte_limit - size) return MFK_DOVI_UNSUPPORTED;
    Impl::Packet copy(av_packet_clone(packet));
    if (!copy) return MFK_DECODE_FAILED;
    packets.emplace(pts, std::move(copy));
    if (base) p.decode_order.push_back(pts);
    p.bytes += size;
    return MFK_OK;
}

int FilmDoviInput::take(AVPacket **packet)
{
    auto &p = *impl;
    if (!packet) return MFK_INVALID_REQUEST;
    *packet = nullptr;
    if (p.decode_order.empty()) return MFK_OK;
    const int64_t pts = p.decode_order.front();
    auto el = p.enhancements.find(pts);
    if (el == p.enhancements.end()) return MFK_OK;
    auto base = p.bases.find(pts);
    const auto *bl = base->second.get(), *enhancement = el->second.get();
    int bl_length = p.base_length, el_length = p.el_length;
    size_t size = 0;
    std::vector<uint8_t> unused;
    auto *extra = av_packet_get_side_data(bl, AV_PKT_DATA_NEW_EXTRADATA, &size);
    if (extra && !FilmDoviNal::configuration(extra, size, bl_length, unused))
        return MFK_DOVI_UNSUPPORTED;
    auto *el_extra = av_packet_get_side_data(enhancement, AV_PKT_DATA_NEW_EXTRADATA, &size);
    const size_t el_extra_size = size;
    if (el_extra && !FilmDoviNal::configuration(el_extra, size, el_length, unused, true))
        return MFK_DOVI_UNSUPPORTED;
    std::vector<uint8_t> merged;
    bool rpu = false;
    auto add = [&](const uint8_t *nal, size_t length) {
        const size_t prefix = bl_length ? bl_length : 4;
        if (length > max_packet - prefix || merged.size() > max_packet - prefix - length ||
            (bl_length && length >= (UINT64_C(1) << (8 * bl_length)))) return false;
        if (bl_length) {
            for (int n = bl_length - 1; n >= 0; n--) merged.push_back((length >> (8 * n)) & 255);
        } else {
            merged.insert(merged.end(), {0, 0, 0, 1});
        }
        merged.insert(merged.end(), nal, nal + length);
        return true;
    };
    bool valid = walk(bl->data, bl->size, bl_length, [&](const uint8_t *nal, size_t length) {
        if (type(nal) == 62 || type(nal) == 63) return false;
        return add(nal, length);
    });
    valid &= walk(enhancement->data, enhancement->size, el_length,
        [&](const uint8_t *nal, size_t length) {
            if (layer(nal) || type(nal) == 63) return false;
            if (type(nal) == 62) {
                if (rpu) return false;
                rpu = true;
                return add(nal, length);
            }
            std::vector<uint8_t> wrapped{126, 1}; // Single-track NAL63 EL encapsulation.
            wrapped.insert(wrapped.end(), nal, nal + length);
            return add(wrapped.data(), wrapped.size());
        });
    FilmDoviNal::Packet parsed;
    if (!valid || !rpu || !FilmDoviNal::split(merged.data(), merged.size(), bl_length, parsed) ||
        !parsed.base_picture || !parsed.enhancement_picture) return MFK_DOVI_UNSUPPORTED;
    Impl::Packet output(av_packet_alloc());
    if (!output || av_new_packet(output.get(), static_cast<int>(merged.size())) < 0 ||
        av_packet_copy_props(output.get(), bl) < 0) return MFK_DECODE_FAILED;
    memcpy(output->data, merged.data(), merged.size());
    output->stream_index = p.base_index;
    output->time_base = p.base_time;
    if (el_extra && el_extra_size > 0) {
        auto *copy = av_packet_new_side_data(output.get(), AV_PKT_DATA_HEVC_CONF, el_extra_size);
        if (!copy) return MFK_DECODE_FAILED;
        memcpy(copy, el_extra, el_extra_size);
    }
    p.base_length = bl_length; p.el_length = el_length;
    p.bytes -= Impl::cost(bl) + Impl::cost(enhancement);
    p.bases.erase(base); p.enhancements.erase(el); p.decode_order.pop_front();
    *packet = output.release();
    return MFK_OK;
}

int FilmDoviInput::finish() const
{
    return impl->bases.empty() && impl->enhancements.empty() ? MFK_OK : MFK_DOVI_UNSUPPORTED;
}
void FilmDoviInput::flush()
{
    impl->bases.clear(); impl->enhancements.clear(); impl->decode_order.clear(); impl->bytes = 0;
}

namespace {
enum class Residual { None, Required, Invalid };

template<class T>
bool inside(size_t offset, size_t size)
{
    return offset >= sizeof(AVDOVIMetadata) && offset <= size &&
        size - offset >= sizeof(T) && offset % alignof(T) == 0;
}

Residual residual(const AVFrame *base, bool expected)
{
    if (!base) return Residual::Invalid;
    const auto *side = av_frame_get_side_data(base, AV_FRAME_DATA_DOVI_METADATA);
    if (!side)
        return expected || av_frame_get_side_data(base, AV_FRAME_DATA_DOVI_RPU_BUFFER) ?
            Residual::Invalid : Residual::None;
    if (!side->data || side->size < sizeof(AVDOVIMetadata)) return Residual::Invalid;
    const auto *data = reinterpret_cast<const AVDOVIMetadata *>(side->data);
    if (!inside<AVDOVIRpuDataHeader>(data->header_offset, side->size) ||
        !inside<AVDOVIDataMapping>(data->mapping_offset, side->size) ||
        !inside<AVDOVIColorMetadata>(data->color_offset, side->size) ||
        data->num_ext_blocks < 0 || data->num_ext_blocks > AV_DOVI_MAX_EXT_BLOCKS)
        return Residual::Invalid;
    if (data->num_ext_blocks &&
        (!inside<AVDOVIDmData>(data->ext_block_offset, side->size) ||
         data->ext_block_size < sizeof(AVDOVIDmData) ||
         data->ext_block_size % alignof(AVDOVIDmData) != 0 ||
         static_cast<size_t>(data->num_ext_blocks) >
             (side->size - data->ext_block_offset) / data->ext_block_size))
        return Residual::Invalid;
    const auto *hdr = av_dovi_get_header(data);
    const auto *mapping = av_dovi_get_mapping(data);
    if (hdr->coef_log2_denom > 32 || hdr->disable_residual_flag > 1)
        return Residual::Invalid;
    if (hdr->disable_residual_flag) return Residual::None;
    if (mapping->nlq_method_idc != AV_DOVI_NLQ_LINEAR_DZ) return Residual::Invalid;
    for (const auto &nlq : mapping->nlq)
        if (nlq.nlq_offset || nlq.vdr_in_max != (UINT64_C(1) << hdr->coef_log2_denom) ||
            nlq.linear_deadzone_slope || nlq.linear_deadzone_threshold)
            return Residual::Required;
    return Residual::None;
}

bool configuration_flags(const uint8_t *data, size_t size, bool &dv, bool &el)
{
    if (!data || size < sizeof(AVDOVIDecoderConfigurationRecord)) return false;
    const auto *config = reinterpret_cast<const AVDOVIDecoderConfigurationRecord *>(data);
    if (config->el_present_flag &&
        (config->dv_profile != 7 || !config->bl_present_flag || !config->rpu_present_flag))
        return false;
    dv = config->rpu_present_flag || config->dv_profile == 7;
    el = config->el_present_flag;
    return true;
}
}

struct FilmDovi::Impl {
    AVCodecContext *decoder = nullptr;
    AVRational time_base{0, 1};
    int nal_length = 0, failure = MFK_OK;
    bool opened = false, hevc = false, expect_dv = false, expect_el = false;
    bool draining = false, eof = false;
    int64_t last_output = AV_NOPTS_VALUE, retired = AV_NOPTS_VALUE;
    int64_t discard = INT64_MIN;
    std::vector<uint8_t> extra, prefix;
    std::map<int64_t, AVFrame *> frames;
    ~Impl()
    {
        clear();
        avcodec_free_context(&decoder);
    }
    void clear()
    {
        for (auto &[pts, frame] : frames) av_frame_free(&frame);
        frames.clear();
    }
    int fail(int code = MFK_DOVI_UNSUPPORTED) { failure = code; return code; }
    int start()
    {
        if (decoder) return MFK_OK;
        const auto *codec = avcodec_find_decoder_by_name("hevc");
        if (!codec || !(decoder = avcodec_alloc_context3(codec))) return fail();
        decoder->pkt_timebase = time_base;
        decoder->thread_count = 2;
        decoder->err_recognition = AV_EF_CRCCHECK | AV_EF_CAREFUL | AV_EF_EXPLODE;
        if (!extra.empty()) {
            decoder->extradata = static_cast<uint8_t *>(av_mallocz(extra.size() + AV_INPUT_BUFFER_PADDING_SIZE));
            if (!decoder->extradata) return fail(MFK_DECODE_FAILED);
            memcpy(decoder->extradata, extra.data(), extra.size());
            decoder->extradata_size = static_cast<int>(extra.size());
        }
        return avcodec_open2(decoder, codec, nullptr) < 0 ? fail() : MFK_OK;
    }
    int receive()
    {
        if (failure || !decoder || eof) return failure;
        while (frames.size() < max_frames) {
            AVFrame *frame = av_frame_alloc();
            if (!frame) return fail(MFK_DECODE_FAILED);
            int result = avcodec_receive_frame(decoder, frame);
            if (result == AVERROR(EAGAIN) || result == AVERROR_EOF) {
                av_frame_free(&frame);
                eof = result == AVERROR_EOF;
                return MFK_OK;
            }
            const int64_t pts = frame->pts;
            if (result < 0 || pts == AV_NOPTS_VALUE || frame->decode_error_flags ||
                (frame->flags & AV_FRAME_FLAG_CORRUPT) ||
                (last_output != AV_NOPTS_VALUE && pts <= last_output)) {
                av_frame_free(&frame); return fail();
            }
            last_output = pts;
            if (pts < discard || (retired != AV_NOPTS_VALUE && pts <= retired)) {
                av_frame_free(&frame); continue;
            }
            frame->time_base = time_base;
            frames.emplace(pts, frame);
        }
        return MFK_OK;
    }
    AVFrame *pop(int64_t pts)
    {
        auto found = frames.find(pts);
        if (found == frames.end()) return nullptr;
        AVFrame *frame = found->second;
        frames.erase(found);
        return frame;
    }
};

FilmDovi::FilmDovi() : impl(std::make_unique<Impl>()) { }
FilmDovi::~FilmDovi() = default;
FilmDovi::FilmDovi(FilmDovi &&) noexcept = default;
FilmDovi &FilmDovi::operator=(FilmDovi &&) noexcept = default;

int FilmDovi::open(const AVCodecParameters *parameters, AVRational time_base)
{
    impl = std::make_unique<Impl>();
    auto &p = *impl;
    if (!parameters || time_base.num <= 0 || time_base.den <= 0)
        return p.fail(MFK_INVALID_REQUEST);
    p.time_base = time_base;
    p.hevc = parameters->codec_id == AV_CODEC_ID_HEVC;
    const auto *side = av_packet_side_data_get(parameters->coded_side_data,
        parameters->nb_coded_side_data, AV_PKT_DATA_DOVI_CONF);
    if (side && !configuration_flags(side->data, side->size, p.expect_dv, p.expect_el))
        return p.fail();
    if (p.expect_el && !p.hevc) return p.fail();
    if (p.hevc) {
        const auto *el_side = av_packet_side_data_get(parameters->coded_side_data,
            parameters->nb_coded_side_data, AV_PKT_DATA_HEVC_CONF);
        const bool declared_dv = p.expect_dv || p.expect_el || el_side != nullptr;
        if (parameters->extradata_size < 0) {
            if (declared_dv) return p.fail();
        } else if (parameters->extradata_size > 0) {
            if (!FilmDoviNal::configuration(parameters->extradata, parameters->extradata_size,
                                            p.nal_length, p.extra)) {
                // Non-DV HEVC streams with unusual extradata must not be rejected as DV.
                if (declared_dv) return p.fail();
                p.nal_length = 0;
                p.extra.clear();
            }
        }
        if (el_side) {
            int length;
            if (!FilmDoviNal::configuration(el_side->data, el_side->size, length, p.extra, true))
                return p.fail();
            p.expect_el = p.expect_dv = true;
        }
        if (!p.extra.empty()) p.expect_el = p.expect_dv = true;
    }
    p.opened = true;
    return MFK_OK;
}

int FilmDovi::send(const AVPacket *packet)
{
    auto &p = *impl;
    if (p.failure) return p.failure;
    if (!p.opened) return p.fail(MFK_INVALID_REQUEST);
    if (p.receive() != MFK_OK) return p.failure;
    if (p.frames.size() == max_frames) return AVERROR(EAGAIN);
    if (!packet) {
        if (!p.decoder) { p.draining = p.eof = true; return MFK_OK; }
        if (!p.draining) {
            int result = avcodec_send_packet(p.decoder, nullptr);
            if (result == AVERROR(EAGAIN)) {
                if (p.receive() != MFK_OK) return p.failure;
                if (p.frames.size() == max_frames) return AVERROR(EAGAIN);
                result = avcodec_send_packet(p.decoder, nullptr);
            }
            if (result < 0 && result != AVERROR_EOF) return p.fail();
            p.draining = true;
        }
        return p.receive();
    }
    if (p.draining) return p.fail(MFK_INVALID_REQUEST);
    if (!p.hevc) return MFK_OK;

    int length = p.nal_length;
    bool dv = p.expect_dv, el = p.expect_el;
    size_t side_size = 0;
    auto *dovi_side = av_packet_get_side_data(packet, AV_PKT_DATA_DOVI_CONF, &side_size);
    if (dovi_side && !configuration_flags(dovi_side, side_size, dv, el)) return p.fail();
    auto *hvce_side = av_packet_get_side_data(packet, AV_PKT_DATA_HEVC_CONF, &side_size);
    if (hvce_side) { dv = el = true; }

    // Ordinary non-DV HEVC bypasses strict DV splitting unless an actual DV
    // configuration or NAL 62/63 is encountered in this packet.
    if (!dv && !el) {
        if (packet->flags & AV_PKT_FLAG_CORRUPT) return MFK_OK;
        auto *new_extra = av_packet_get_side_data(packet, AV_PKT_DATA_NEW_EXTRADATA, &side_size);
        if (new_extra) {
            std::vector<uint8_t> probed;
            if (FilmDoviNal::configuration(new_extra, side_size, length, probed)) {
                p.nal_length = length;
                if (!probed.empty()) { dv = el = true; }
            }
        }
        if (!dv && !el && !probe_has_dovi_nal(packet->data, packet->size > 0 ? static_cast<size_t>(packet->size) : 0, length))
            return MFK_OK;
    }

    if (packet->size < 0 || (!packet->data && packet->size) || (packet->flags & AV_PKT_FLAG_CORRUPT))
        return p.fail();
    std::vector<uint8_t> bytes = p.prefix, changed;
    auto *side = av_packet_get_side_data(packet, AV_PKT_DATA_NEW_EXTRADATA, &side_size);
    if (side) {
        if (!FilmDoviNal::configuration(side, side_size, length, changed)) return p.fail();
        bytes.insert(bytes.end(), changed.begin(), changed.end());
    }
    side = av_packet_get_side_data(packet, AV_PKT_DATA_HEVC_CONF, &side_size);
    if (side) {
        int el_length;
        if (!FilmDoviNal::configuration(side, side_size, el_length, changed, true)) return p.fail();
        bytes.insert(bytes.end(), changed.begin(), changed.end());
        el = dv = true;
    }
    FilmDoviNal::Packet parsed;
    if (!FilmDoviNal::split(packet->data, packet->size, length, parsed)) return p.fail();
    dv |= parsed.rpu || !parsed.enhancement.empty();
    el |= !parsed.enhancement.empty();
    if (parsed.base_picture && dv && !parsed.rpu) return p.fail();
    if (bytes.size() > max_packet - parsed.enhancement.size()) return p.fail();
    bytes.insert(bytes.end(), parsed.enhancement.begin(), parsed.enhancement.end());
    if (!parsed.enhancement_picture) {
        if (bytes.size() > max_prefix) return p.fail();
        p.prefix = std::move(bytes);
    } else {
        if (packet->pts == AV_NOPTS_VALUE || bytes.size() > max_packet) return p.fail();
        if (p.start() != MFK_OK) return p.failure;
        AVPacket *copy = av_packet_alloc();
        if (!copy || av_new_packet(copy, static_cast<int>(bytes.size())) < 0) {
            av_packet_free(&copy); return p.fail(MFK_DECODE_FAILED);
        }
        memcpy(copy->data, bytes.data(), bytes.size());
        copy->pts = packet->pts; copy->dts = packet->dts;
        copy->duration = packet->duration; copy->flags = packet->flags;
        copy->time_base = p.time_base;
        int result = avcodec_send_packet(p.decoder, copy);
        if (result == AVERROR(EAGAIN)) {
            if (p.receive() != MFK_OK) { av_packet_free(&copy); return p.failure; }
            if (p.frames.size() == max_frames) {
                av_packet_free(&copy); return AVERROR(EAGAIN);
            }
            result = avcodec_send_packet(p.decoder, copy);
        }
        av_packet_free(&copy);
        if (result < 0) return p.fail();
        p.prefix.clear();
    }
    p.nal_length = length;
    p.expect_dv = dv; p.expect_el = el;
    return p.receive();
}

int FilmDovi::pair(const AVFrame *base, AVFrame **enhancement)
{
    auto &p = *impl;
    if (!enhancement) return p.fail(MFK_INVALID_REQUEST);
    *enhancement = nullptr;
    if (p.failure) return p.failure;
    if (!p.opened || !base) return p.fail(MFK_INVALID_REQUEST);
    const Residual needed = residual(base, p.expect_dv);
    if (needed == Residual::Invalid) return p.fail();
    if (!p.expect_dv && !p.expect_el && needed == Residual::None) return MFK_OK;
    int64_t pts = base->pts;
    if (pts == AV_NOPTS_VALUE) return p.fail();
    if (base->time_base.num > 0 && base->time_base.den > 0) {
        pts = av_rescale_q(base->pts, base->time_base, p.time_base);
        if (av_compare_ts(pts, p.time_base, base->pts, base->time_base)) return p.fail();
    }
    if (pts < p.discard || (p.retired != AV_NOPTS_VALUE && pts <= p.retired))
        return p.fail(MFK_INVALID_REQUEST);
    if (p.receive() != MFK_OK) return p.failure;
    AVFrame *frame = p.pop(pts);
    if (needed == Residual::Required && !frame) {
        p.expect_el = true;
        return p.eof || (p.last_output != AV_NOPTS_VALUE && p.last_output >= pts) ?
            p.fail() : AVERROR(EAGAIN);
    }
    p.retired = pts;
    for (auto it = p.frames.begin(); it != p.frames.end() && it->first < pts;) {
        av_frame_free(&it->second); it = p.frames.erase(it);
    }
    if (needed == Residual::Required) *enhancement = frame;
    else av_frame_free(&frame);
    return MFK_OK;
}

int FilmDovi::discard_before(int64_t pts)
{
    auto &p = *impl;
    if (p.failure) return p.failure;
    if (!p.opened || pts == AV_NOPTS_VALUE || pts < p.discard)
        return p.fail(MFK_INVALID_REQUEST);
    p.discard = pts;
    for (auto it = p.frames.begin(); it != p.frames.end() && it->first < pts;) {
        av_frame_free(&it->second); it = p.frames.erase(it);
    }
    return p.receive();
}

void FilmDovi::flush()
{
    auto &p = *impl;
    if (p.decoder) avcodec_flush_buffers(p.decoder);
    p.clear(); p.prefix.clear();
    p.draining = p.eof = false;
    p.last_output = p.retired = AV_NOPTS_VALUE;
    p.discard = INT64_MIN;
    if (p.opened) p.failure = MFK_OK;
}
