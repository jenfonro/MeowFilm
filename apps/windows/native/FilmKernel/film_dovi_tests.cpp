/* Standalone test executable; link film_dovi.cpp and the patched, locked
 * FFmpeg 8.1.2 avcodec/avformat/avutil. No Windows/UI/GPU or external encoder
 * dependency. An optional dual-track P7 MP4 supplies a real RPU and a short
 * BL/EL prefix through the production input assembler. No whole-movie output
 * or actual HDR display is validated here. */
#include "film_dovi.h"
#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <fstream>
#include <iterator>
extern "C" {
#include <libavformat/avformat.h>
#include <libavutil/dovi_meta.h>
#include <libavutil/intreadwrite.h>
#include <libavutil/mem.h>
}

#define CHECK(condition) do { if (!(condition)) { \
    std::fprintf(stderr, "%s:%d: %s\n", __FILE__, __LINE__, #condition); \
    std::abort(); \
} } while (0)

namespace {
using Bytes = std::vector<uint8_t>;
struct FrameFree { void operator()(AVFrame *f) const { av_frame_free(&f); } };
struct PacketFree { void operator()(AVPacket *p) const { av_packet_free(&p); } };
struct ParametersFree { void operator()(AVCodecParameters *p) const { avcodec_parameters_free(&p); } };
using Frame = std::unique_ptr<AVFrame, FrameFree>;
using Packet = std::unique_ptr<AVPacket, PacketFree>;
using Parameters = std::unique_ptr<AVCodecParameters, ParametersFree>;
constexpr AVRational time_base{1, 24000};
constexpr int64_t step = 1001;

Bytes hex(const char *text)
{
    Bytes out;
    for (; *text; text += 2) {
        CHECK(text[1]);
        char pair[] = {text[0], text[1], 0};
        out.push_back(static_cast<uint8_t>(std::strtoul(pair, nullptr, 16)));
    }
    return out;
}
Bytes pack(const std::vector<Bytes> &nals, int length = 0)
{
    Bytes out;
    for (const auto &nal : nals) {
        if (!length) out.insert(out.end(), {0, 0, 0, 1});
        else {
            CHECK(length >= 1 && length <= 4);
            for (int i = length - 1; i >= 0; i--)
                out.push_back(static_cast<uint8_t>(nal.size() >> (8 * i)));
        }
        out.insert(out.end(), nal.begin(), nal.end());
    }
    return out;
}
std::vector<Bytes> unpack(const AVPacket *packet, int length)
{
    CHECK(length >= 1 && length <= 4 && packet->size >= 0);
    std::vector<Bytes> out;
    const size_t bytes = static_cast<size_t>(packet->size);
    for (size_t offset = 0; offset < bytes;) {
        CHECK(bytes - offset >= static_cast<size_t>(length));
        uint32_t size = 0;
        for (int i = 0; i < length; i++) size = (size << 8) | packet->data[offset++];
        CHECK(size >= 2 && size <= bytes - offset);
        out.emplace_back(packet->data + offset, packet->data + offset + size);
        offset += size;
    }
    return out;
}
Bytes wrap(Bytes nal)
{
    nal.insert(nal.begin(), {0x7e, 1});
    return nal;
}
Bytes hvcc(const std::vector<Bytes> &nals, int length = 4)
{
    Bytes out(23);
    out[0] = 1; out[21] = static_cast<uint8_t>(length - 1);
    out[22] = static_cast<uint8_t>(nals.size());
    for (const auto &nal : nals) {
        out.insert(out.end(), {static_cast<uint8_t>((nal[0] >> 1) & 63), 0, 1,
                              static_cast<uint8_t>(nal.size() >> 8),
                              static_cast<uint8_t>(nal.size())});
        out.insert(out.end(), nal.begin(), nal.end());
    }
    return out;
}
Packet packet(const Bytes &bytes, int64_t pts, int64_t dts)
{
    Packet out(av_packet_alloc());
    CHECK(out && av_new_packet(out.get(), static_cast<int>(bytes.size())) == 0);
    memcpy(out->data, bytes.data(), bytes.size());
    out->pts = pts; out->dts = dts; out->duration = step;
    return out;
}
void side(AVCodecParameters *par, AVPacketSideDataType type, const Bytes &bytes)
{
    auto *data = av_packet_side_data_new(&par->coded_side_data, &par->nb_coded_side_data,
                                         type, bytes.size(), 0);
    CHECK(data);
    memcpy(data->data, bytes.data(), bytes.size());
}
Parameters parameters(bool config = false)
{
    Parameters par(avcodec_parameters_alloc());
    CHECK(par);
    par->codec_type = AVMEDIA_TYPE_VIDEO; par->codec_id = AV_CODEC_ID_HEVC;
    if (config) {
        size_t size;
        auto *cfg = av_dovi_alloc(&size); CHECK(cfg);
        cfg->dv_profile = 7; cfg->rpu_present_flag = cfg->bl_present_flag = cfg->el_present_flag = 1;
        side(par.get(), AV_PKT_DATA_DOVI_CONF, Bytes(reinterpret_cast<uint8_t *>(cfg),
                                                   reinterpret_cast<uint8_t *>(cfg) + size));
        av_free(cfg);
    }
    return par;
}
void extra(AVCodecParameters *par, const Bytes &bytes)
{
    par->extradata = static_cast<uint8_t *>(av_mallocz(bytes.size() + AV_INPUT_BUFFER_PADDING_SIZE));
    CHECK(par->extradata);
    memcpy(par->extradata, bytes.data(), bytes.size());
    par->extradata_size = static_cast<int>(bytes.size());
}
Frame base(int64_t pts, bool fel)
{
    Frame frame(av_frame_alloc()); CHECK(frame);
    frame->pts = pts; frame->time_base = time_base;
    size_t size;
    auto *data = av_dovi_metadata_alloc(&size); CHECK(data);
    auto *hdr = av_dovi_get_header(data);
    hdr->coef_log2_denom = 13;
    hdr->bl_bit_depth = hdr->el_bit_depth = 10; hdr->vdr_bit_depth = 12;
    auto *mapping = av_dovi_get_mapping(data);
    mapping->nlq_method_idc = AV_DOVI_NLQ_LINEAR_DZ;
    for (auto &nlq : mapping->nlq) nlq.vdr_in_max = UINT64_C(1) << 13;
    if (fel) mapping->nlq[0].linear_deadzone_slope = 1;
    auto *sd = av_frame_new_side_data(frame.get(), AV_FRAME_DATA_DOVI_METADATA, size);
    CHECK(sd); memcpy(sd->data, data, size); av_free(data);
    return frame;
}

void test_nals()
{
    const Bytes bl{0x26, 1, 0x80}, el{0x28, 1, 0x80, 0, 0, 3, 1, 0x80};
    const Bytes rpu{0x7c, 1, 0x19, 0x80};
    for (int length = 0; length <= 4; length++) {
        const Bytes bytes = pack({bl, rpu, wrap(el)}, length);
        FilmDoviNal::Packet parsed;
        CHECK(FilmDoviNal::split(bytes.data(), bytes.size(), length, parsed));
        CHECK(parsed.base_picture && parsed.enhancement_picture && parsed.rpu);
        CHECK(parsed.enhancement == pack({el})); // EBSP escape preserved verbatim.
    }
    Bytes bytes = pack({wrap(el)});
    bytes.insert(bytes.begin(), 0); bytes.insert(bytes.end(), {0, 0});
    FilmDoviNal::Packet parsed;
    CHECK(FilmDoviNal::split(bytes.data(), bytes.size(), 0, parsed));
    CHECK(parsed.enhancement == pack({el}));
    bytes = {0, 0, 1, 0x7e, 1, 0x28, 1, 0x80}; // Three-byte start code.
    CHECK(FilmDoviNal::split(bytes.data(), bytes.size(), 0, parsed));
    CHECK(parsed.enhancement == pack({Bytes{0x28, 1, 0x80}}));
    const std::vector<Bytes> invalid = {
        {0x7e, 1, 0x28, 1, 0x80}, // No Annex B prefix.
        {0, 0, 1}, {0, 0, 1, 0x7e, 1}, {0, 0, 1, 0x7e, 1, 0x28},
        pack({Bytes{0x7e, 1, 0xa8, 1, 0x80}}), // Inner forbidden bit.
        pack({Bytes{0x7e, 1, 0x28, 0, 0x80}}), // Invalid temporal ID.
        pack({Bytes{0x7e, 1, 0x28, 9, 0x80}}), // Not a standalone EL stream.
        pack({wrap(el), wrap(el)}), // Two pictures cannot share one packet PTS.
        pack({rpu, rpu}), pack({Bytes{0x26, 1, 0x40}}), // Incomplete AU.
    };
    for (const auto &bad : invalid) {
        FilmDoviNal::Packet out; out.enhancement = {42}; out.rpu = true;
        CHECK(!FilmDoviNal::split(bad.data(), bad.size(), 0, out));
        CHECK(out.enhancement == Bytes{42} && out.rpu); // No partial commit.
    }
    for (int length = 1; length <= 4; length++) {
        auto bad = pack({wrap(el)}, length); bad.pop_back();
        CHECK(!FilmDoviNal::split(bad.data(), bad.size(), length, parsed));
        bad.assign(static_cast<size_t>(length), 0);
        CHECK(!FilmDoviNal::split(bad.data(), bad.size(), length, parsed));
    }
    CHECK(!FilmDoviNal::split(nullptr, 1, 0, parsed));
    CHECK(!FilmDoviNal::split(nullptr, 0, 5, parsed));
}

void test_configuration()
{
    const Bytes blps{0x42, 1, 0x80}, elps{0x42, 1, 0x91, 0, 0, 3, 1, 0x80};
    for (int length = 1; length <= 4; length++) {
        auto bytes = hvcc({blps, wrap(elps)}, length);
        int out_length = -1; Bytes out;
        CHECK(FilmDoviNal::configuration(bytes.data(), bytes.size(), out_length, out));
        CHECK(out_length == length && out == pack({elps}));
        bytes = hvcc({elps}, length);
        CHECK(FilmDoviNal::configuration(bytes.data(), bytes.size(), out_length, out, true));
        CHECK(out_length == length && out == pack({elps}));
        CHECK(FilmDoviNal::configuration(bytes.data(), bytes.size(), out_length, out));
        CHECK(out.empty()); // BL PSs are not a substitute for EL PSs.
    }
    int length = -1; Bytes out;
    auto bytes = pack({blps, wrap(elps)});
    CHECK(FilmDoviNal::configuration(bytes.data(), bytes.size(), length, out));
    CHECK(length == 0 && out == pack({elps}));
    bytes = hvcc({wrap(elps)}); bytes[0] = 0; bytes[1] = 1;
    CHECK(FilmDoviNal::configuration(bytes.data(), bytes.size(), length, out));
    bytes = hvcc({wrap(elps)});
    for (size_t size = 1; size < bytes.size(); size++) {
        int unchanged = 9; Bytes retained{42};
        CHECK(!FilmDoviNal::configuration(bytes.data(), size, unchanged, retained));
        CHECK(unchanged == 9 && retained == Bytes{42});
    }
    bytes.push_back(0); CHECK(!FilmDoviNal::configuration(bytes.data(), bytes.size(), length, out));
}

void test_metadata()
{
    FilmDovi dovi;
    auto par = parameters(true);
    CHECK(dovi.open(par.get(), time_base) == MFK_OK && dovi.enabled());
    auto mel = base(0, false), fel = base(0, true);
    CHECK(!dovi.required(mel.get()) && dovi.required(fel.get()));
    AVFrame *owned = nullptr;
    CHECK(dovi.pair(mel.get(), &owned) == MFK_OK && !owned);
    dovi.flush();
    CHECK(dovi.pair(fel.get(), &owned) == AVERROR(EAGAIN) && !owned);
    CHECK(dovi.send(nullptr) == MFK_OK);
    CHECK(dovi.pair(fel.get(), &owned) == MFK_DOVI_UNSUPPORTED && !owned);
    dovi.flush();
    auto *data = reinterpret_cast<AVDOVIMetadata *>(
        av_frame_get_side_data(mel.get(), AV_FRAME_DATA_DOVI_METADATA)->data);
    auto *hdr = av_dovi_get_header(data);
    hdr->disable_residual_flag = 1;
    CHECK(!dovi.required(mel.get()));
    hdr->disable_residual_flag = 0; hdr->coef_log2_denom = 64;
    CHECK(dovi.required(mel.get()));
    CHECK(dovi.pair(mel.get(), &owned) == MFK_DOVI_UNSUPPORTED);
    dovi.flush();
    hdr->coef_log2_denom = 13; data->mapping_offset = SIZE_MAX;
    CHECK(dovi.pair(mel.get(), &owned) == MFK_DOVI_UNSUPPORTED);
    for (bool configured : {false, true}) {
        par = parameters(configured);
        CHECK(dovi.open(par.get(), time_base) == MFK_OK);
        Frame absent(av_frame_alloc()); CHECK(absent); absent->pts = 0;
        if (!configured) {
            auto rpu = packet(pack({Bytes{0x7c, 1, 0x19, 0x80}}), 0, 0);
            CHECK(dovi.send(rpu.get()) == MFK_OK); // No container config, still detected.
        }
        CHECK(dovi.required(absent.get()));
        CHECK(dovi.pair(absent.get(), &owned) == MFK_DOVI_UNSUPPORTED);
    }
    par = parameters();
    CHECK(dovi.open(par.get(), time_base) == MFK_OK && !dovi.enabled());
    Frame ordinary(av_frame_alloc()); CHECK(ordinary);
    CHECK(dovi.pair(ordinary.get(), &owned) == MFK_OK && !owned);
    par = parameters(true);
    CHECK(dovi.open(par.get(), time_base) == MFK_OK);
    auto without_rpu = packet(pack({Bytes{0x26, 1, 0x80}}), 0, 0);
    CHECK(dovi.send(without_rpu.get()) == MFK_DOVI_UNSUPPORTED);
    CHECK(dovi.open(par.get(), AVRational{0, 1}) == MFK_INVALID_REQUEST);
    CHECK(dovi.status() == MFK_INVALID_REQUEST);
}

/* Self-generated 64x64 10-bit HEVC, eight changing gray frames, x265 3.5,
 * ultrafast / bframes=2 / b-adapt=0 / keyint=64 / no-scenecut / no-info.
 * Decode order 0,3,2,1,6,5,4,7 exercises real B-frame PTS reordering.
 * These are NOT authored DV pictures/RPUs: NAL 63 wrapping and synthetic
 * metadata exercise the helper independently of an optional real DV fixture. */
struct Sample { int pts; std::initializer_list<const char *> nals; };
const Sample samples[] = {
    {0, {"40010c01ffff02200000030090000003000003001e959009", "42010102200000030090000003000003001ea0208104d965644a4c2e0100000303e900005dc008", "4401c073c089", "2801ac76071fff8db56a47"}},
    {3, {"0201d0195e20e4220cc4d273cbcc"}},
    {2, {"0201e04497820410204dc1c6d768"}},
    {1, {"0001e024ffe89029809ec8"}},
    {6, {"0201d03097d620ec220cc99273cbcc"}},
    {5, {"0201e0a225d7820410204dc1c6d768"}},
    {4, {"0001e086fffa240a609ec8"}},
    {7, {"0201d038bf5e20e4220b449cf2f3"}},
};
std::vector<Bytes> sample_nals(const Sample &sample, bool wrapped, bool no_ps = false)
{
    std::vector<Bytes> out;
    for (const auto *text : sample.nals) {
        auto nal = hex(text);
        if (no_ps && ((nal[0] >> 1) & 63) >= 32) continue;
        out.push_back(wrapped ? wrap(nal) : nal);
    }
    return out;
}
std::vector<Bytes> parameter_sets()
{
    auto out = sample_nals(samples[0], false);
    out.pop_back();
    return out;
}

void test_dual_track_input()
{
    AVFormatContext *format = avformat_alloc_context(); CHECK(format);
    // Deliberately put EL first: association must use config flags, not order.
    auto *el = avformat_new_stream(format, nullptr);
    auto *bl = avformat_new_stream(format, nullptr);
    CHECK(el && bl);
    auto el_par = parameters(true), bl_par = parameters();
    const auto *sd = av_packet_side_data_get(el_par->coded_side_data,
        el_par->nb_coded_side_data, AV_PKT_DATA_DOVI_CONF);
    reinterpret_cast<AVDOVIDecoderConfigurationRecord *>(sd->data)->bl_present_flag = 0;
    extra(el_par.get(), hvcc(parameter_sets(), 2));
    extra(bl_par.get(), hvcc(parameter_sets(), 4));
    CHECK(avcodec_parameters_copy(el->codecpar, el_par.get()) == 0);
    CHECK(avcodec_parameters_copy(bl->codecpar, bl_par.get()) == 0);
    bl->time_base = time_base; el->time_base = {1, 48000};
    FilmDoviInput input;
    CHECK(input.open(format, el->index) == MFK_OK);
    CHECK(input.base_index() == bl->index && input.enhancement_index() == el->index);
    const auto *config = av_packet_side_data_get(input.parameters()->coded_side_data,
        input.parameters()->nb_coded_side_data, AV_PKT_DATA_DOVI_CONF);
    CHECK(config && reinterpret_cast<AVDOVIDecoderConfigurationRecord *>(config->data)->bl_present_flag);
    auto send = [&](int n, bool base_track) {
        const auto &sample = samples[n % 8];
        auto nals = sample_nals(sample, false);
        if (!base_track) nals.push_back(Bytes{124, 1, 25, 128}); // Structurally valid, not a real RPU.
        auto pkt = packet(pack(nals, base_track ? 4 : 2), (n / 8 * 8 + sample.pts) * step * (base_track ? 1 : 2),
                          (n - 2) * step * (base_track ? 1 : 2));
        pkt->stream_index = base_track ? bl->index : el->index;
        return input.send(pkt.get());
    };
    // EL may arrive in another order; output must retain BL's decode order.
    for (int n : {2, 0, 3, 1}) CHECK(send(n, false) == MFK_OK);
    for (int n = 0; n < 4; n++) CHECK(send(n, true) == MFK_OK);
    for (int n = 0; n < 4; n++) {
        AVPacket *raw = nullptr;
        CHECK(input.take(&raw) == MFK_OK); Packet merged(raw);
        CHECK(merged && merged->pts == samples[n].pts * step &&
              merged->dts == (n - 2) * step && merged->stream_index == bl->index);
        FilmDoviNal::Packet parsed;
        CHECK(FilmDoviNal::split(merged->data, merged->size, 4, parsed));
        CHECK(parsed.base_picture && parsed.enhancement_picture && parsed.rpu);
    }
    CHECK(input.finish() == MFK_OK);
    CHECK(send(0, true) == MFK_OK);
    CHECK(send(0, true) == MFK_DOVI_UNSUPPORTED);
    CHECK(input.finish() == MFK_DOVI_UNSUPPORTED);
    input.flush(); CHECK(input.finish() == MFK_OK);
    // MOV's file-offset interleaving can deliver more than 32 compressed BL
    // packets before EL. It must not be rejected as decoded DPB exhaustion.
    for (int n = 0; n < 64; n++) CHECK(send(n, true) == MFK_OK);
    for (int n = 0; n < 64; n++) {
        CHECK(send(n, false) == MFK_OK);
        AVPacket *raw = nullptr;
        CHECK(input.take(&raw) == MFK_OK); Packet merged(raw);
        CHECK(merged && merged->pts == (n / 8 * 8 + samples[n % 8].pts) * step &&
              merged->dts == (n - 2) * step);
    }
    CHECK(input.finish() == MFK_OK);
    auto mismatched = packet(pack(sample_nals(samples[0], false), 2), 1, 0);
    mismatched->stream_index = el->index;
    CHECK(input.send(mismatched.get()) == MFK_DOVI_UNSUPPORTED); // Not exactly representable in BL ticks.
    auto *another = avformat_new_stream(format, nullptr); CHECK(another);
    CHECK(avcodec_parameters_copy(another->codecpar, bl->codecpar) == 0);
    another->time_base = time_base;
    CHECK(input.open(format, bl->index) == MFK_DOVI_UNSUPPORTED);
    avformat_free_context(format);
}

void test_decode_and_seek()
{
    for (int mode = 0; mode < 7; mode++) {
        auto par = parameters(mode != 0);
        if (mode == 1 || mode == 5) {
            extra(par.get(), hvcc(parameter_sets()));
            if (mode == 1) side(par.get(), AV_PKT_DATA_HEVC_CONF, hvcc(parameter_sets()));
        } else if (mode == 2 || mode == 4) {
            auto ps = parameter_sets();
            for (auto &nal : ps) nal = wrap(nal);
            extra(par.get(), mode == 4 ? hvcc(ps) : pack(ps));
        }
        FilmDovi dovi;
        CHECK(dovi.open(par.get(), time_base) == MFK_OK);
        for (int round = 0; round < 2; round++) {
            const int64_t origin = round ? -900900 : INT64_C(9000000001);
            if (round) dovi.flush();
            CHECK(dovi.discard_before(origin + 2 * step) == MFK_OK);
            if (mode == 3) {
                auto ps = parameter_sets();
                for (auto &nal : ps) nal = wrap(nal);
                auto preamble = packet(pack(ps), AV_NOPTS_VALUE, AV_NOPTS_VALUE);
                CHECK(dovi.send(preamble.get()) == MFK_OK); // Separate inband PS packet.
            }
            for (int i = 0; i < 8; i++) {
                const auto &s = samples[i];
                auto pkt = packet(pack(sample_nals(s, true, mode != 0),
                                       mode == 1 || mode >= 4 ? 4 : 0),
                                  origin + s.pts * step, origin + (i - 2) * step);
                if (i == 0 && mode >= 5) {
                    auto ps = parameter_sets();
                    if (mode == 6) for (auto &nal : ps) nal = wrap(nal);
                    const Bytes config = hvcc(ps);
                    auto *data = av_packet_new_side_data(pkt.get(), mode == 5 ?
                        AV_PKT_DATA_HEVC_CONF : AV_PKT_DATA_NEW_EXTRADATA, config.size());
                    CHECK(data); memcpy(data, config.data(), config.size());
                }
                CHECK(dovi.send(pkt.get()) == MFK_OK);
            }
            CHECK(dovi.send(nullptr) == MFK_OK);
            CHECK(dovi.enabled());
            CHECK(!dovi.take(origin * av_q2d(time_base))); // Preroll was released.
            for (int i = 2; i < 8; i++) {
                auto bl = base(origin + i * step, true);
                AVFrame *raw = nullptr;
                CHECK(dovi.pair(bl.get(), &raw) == MFK_OK);
                Frame el(raw);
                CHECK(el && el->pts == bl->pts && el->width == 64 && el->height == 64);
                CHECK(el->format == AV_PIX_FMT_YUV420P10LE);
            }
            CHECK(dovi.status() == MFK_OK);
        }
        dovi.flush();
        CHECK(!dovi.take(0) && dovi.status() == MFK_OK);
        // Flush while delayed frames exist; no old-generation frame may reappear.
        auto pkt = packet(pack(sample_nals(samples[0], true), mode == 1 || mode >= 4 ? 4 : 0), 0, -2 * step);
        CHECK(dovi.send(pkt.get()) == MFK_OK);
        dovi.flush();
        CHECK(dovi.send(nullptr) == MFK_OK && !dovi.take(0));
    }
}

void test_backpressure()
{
    FilmDovi dovi;
    auto par = parameters();
    CHECK(dovi.open(par.get(), time_base) == MFK_OK);
    int accepted = 0, paired = 0, blocked = 0;
    for (int i = 0; i < 80; i++) {
        const int gop = i / 8;
        const auto &s = samples[i % 8];
        auto pkt = packet(pack(sample_nals(s, true)), (gop * 8 + s.pts) * step, (i - 2) * step);
        int result = dovi.send(pkt.get());
        while (result == AVERROR(EAGAIN)) {
            blocked++;
            auto bl = base(paired * step, true);
            AVFrame *raw = nullptr;
            CHECK(dovi.pair(bl.get(), &raw) == MFK_OK); Frame el(raw); CHECK(el);
            paired++;
            result = dovi.send(pkt.get()); // Exact same unconsumed packet.
        }
        CHECK(result == MFK_OK);
        accepted++;
    }
    CHECK(blocked > 0 && accepted == 80);
    while (dovi.send(nullptr) == AVERROR(EAGAIN)) {
        auto bl = base(paired++ * step, true);
        AVFrame *raw = nullptr;
        CHECK(dovi.pair(bl.get(), &raw) == MFK_OK); Frame el(raw); CHECK(el);
    }
    for (; paired < accepted; paired++) {
        auto bl = base(paired * step, true);
        AVFrame *raw = nullptr;
        CHECK(dovi.pair(bl.get(), &raw) == MFK_OK); Frame el(raw); CHECK(el);
    }
    CHECK(dovi.status() == MFK_OK);
    // The decoder has actually passed this PTS, not merely waited a timeout.
    auto missing = base(accepted * step, true);
    AVFrame *raw = nullptr;
    CHECK(dovi.pair(missing.get(), &raw) == MFK_DOVI_UNSUPPORTED && !raw);
}

void test_missing_pts_and_el()
{
    auto par = parameters();
    FilmDovi dovi;
    CHECK(dovi.open(par.get(), time_base) == MFK_OK);
    auto pkt = packet(pack(sample_nals(samples[0], true)), AV_NOPTS_VALUE, 0);
    CHECK(dovi.send(pkt.get()) == MFK_DOVI_UNSUPPORTED);
    CHECK(dovi.open(par.get(), time_base) == MFK_OK);
    for (int i = 0; i < 8; i++) {
        auto &s = samples[i];
        pkt = packet(pack(sample_nals(s, true)), (10 + s.pts) * step, i * step);
        CHECK(dovi.send(pkt.get()) == MFK_OK);
    }
    CHECK(dovi.send(nullptr) == MFK_OK);
    auto missing = base(0, true);
    AVFrame *raw = nullptr;
    CHECK(dovi.pair(missing.get(), &raw) == MFK_DOVI_UNSUPPORTED && !raw);
}

struct Memory {
    Bytes bytes;
    size_t offset = 0;
    static int read(void *opaque, uint8_t *out, int size)
    {
        auto &self = *static_cast<Memory *>(opaque);
        size_t count = std::min(static_cast<size_t>(size), self.bytes.size() - self.offset);
        if (!count) return AVERROR_EOF;
        memcpy(out, self.bytes.data() + self.offset, count); self.offset += count;
        return static_cast<int>(count);
    }
    static int64_t seek(void *opaque, int64_t pos, int whence)
    {
        auto &self = *static_cast<Memory *>(opaque);
        if (whence == AVSEEK_SIZE) return static_cast<int64_t>(self.bytes.size());
        if (whence == SEEK_CUR) pos += static_cast<int64_t>(self.offset);
        else if (whence == SEEK_END) pos += static_cast<int64_t>(self.bytes.size());
        else if (whence != SEEK_SET) return AVERROR(EINVAL);
        if (pos < 0 || static_cast<uint64_t>(pos) > self.bytes.size()) return AVERROR(EINVAL);
        self.offset = static_cast<size_t>(pos); return pos;
    }
};

void test_rpu_failure()
{
    for (const auto &rpu : {Bytes{0x7c, 1, 0x19}, // Truncated RPU.
                           Bytes{0x7c, 1, 0x19, 1, 1, 1, 0x80}}) { // Unsupported type 0.
        auto par = parameters(true);
        auto *codec = avcodec_find_decoder_by_name("hevc"); CHECK(codec);
        auto *decoder = avcodec_alloc_context3(codec); CHECK(decoder);
        CHECK(avcodec_parameters_to_context(decoder, par.get()) == 0);
        decoder->thread_count = 1; decoder->pkt_timebase = time_base;
        CHECK(avcodec_open2(decoder, codec, nullptr) == 0);
        auto nals = sample_nals(samples[0], false);
        nals.push_back(rpu); // Followed by EL: not silently ignored.
        auto el = sample_nals(samples[0], true);
        nals.insert(nals.end(), el.begin(), el.end());
        auto pkt = packet(pack(nals), 0, -2 * step);
        int result = avcodec_send_packet(decoder, pkt.get());
        if (result >= 0) {
            Frame frame(av_frame_alloc()); CHECK(frame);
            result = avcodec_receive_frame(decoder, frame.get());
        }
        CHECK(result < 0 && result != AVERROR(EAGAIN) && result != AVERROR_EOF);
        avcodec_free_context(&decoder);
    }
}

Bytes box(const char *kind, Bytes payload)
{
    Bytes out(8);
    AV_WB32(out.data(), payload.size() + 8);
    memcpy(out.data() + 4, kind, 4);
    out.insert(out.end(), payload.begin(), payload.end());
    return out;
}
void join(Bytes &out, const Bytes &part) { out.insert(out.end(), part.begin(), part.end()); }

void test_hvce_demux()
{
    for (bool el_first : {false, true}) {
        auto ps = parameter_sets();
        const auto bl_config = hvcc(ps);
        ps[2].back() ^= 1; // Distinguishable EL record; demuxing must not mix them.
        const auto el_config = hvcc(ps);
        auto bl = box("hvcC", bl_config), el = box("hvcE", el_config);
        Bytes entry(78);
        AV_WB16(entry.data() + 6, 1);
        AV_WB16(entry.data() + 24, 64); AV_WB16(entry.data() + 26, 64);
        AV_WB16(entry.data() + 40, 1); AV_WB16(entry.data() + 74, 24);
        join(entry, el_first ? el : bl); join(entry, el_first ? bl : el);
        Bytes stsd(8); AV_WB32(stsd.data() + 4, 1); join(stsd, box("hev1", entry));
        Bytes hdlr(24); memcpy(hdlr.data() + 8, "vide", 4);
        Bytes mdhd(24); AV_WB32(mdhd.data() + 12, 24000);
        Bytes mdia = box("mdhd", mdhd); join(mdia, box("hdlr", hdlr));
        join(mdia, box("minf", box("stbl", box("stsd", stsd))));
        Bytes trak = box("tkhd", Bytes(84)); join(trak, box("mdia", mdia));
        Memory input{box("moov", box("trak", trak))};
        auto *io = avio_alloc_context(static_cast<uint8_t *>(av_malloc(4096)), 4096,
                                      0, &input, Memory::read, nullptr, Memory::seek);
        CHECK(io);
        AVFormatContext *format = avformat_alloc_context(); CHECK(format);
        format->pb = io; format->flags |= AVFMT_FLAG_CUSTOM_IO;
        CHECK(avformat_open_input(&format, "fixture.mp4", av_find_input_format("mov"), nullptr) == 0);
        CHECK(format->nb_streams == 1);
        auto *par = format->streams[0]->codecpar;
        CHECK(par->extradata_size == static_cast<int>(bl_config.size()));
        CHECK(memcmp(par->extradata, bl_config.data(), bl_config.size()) == 0);
        const auto *sd = av_packet_side_data_get(par->coded_side_data, par->nb_coded_side_data,
                                                AV_PKT_DATA_HEVC_CONF);
        CHECK(sd && sd->size == el_config.size());
        CHECK(memcmp(sd->data, el_config.data(), el_config.size()) == 0);
        avformat_close_input(&format);
        av_freep(&io->buffer); avio_context_free(&io);
    }
}

void test_real_rpu(const char *path)
{
    std::ifstream file(path, std::ios::binary); CHECK(file.good());
    Memory input{Bytes(std::istreambuf_iterator<char>(file), {})};
    auto *buffer = static_cast<uint8_t *>(av_malloc(4096)); CHECK(buffer);
    auto *io = avio_alloc_context(buffer, 4096, 0, &input, Memory::read, nullptr, Memory::seek);
    CHECK(io);
    AVFormatContext *format = avformat_alloc_context(); CHECK(format);
    format->pb = io; format->flags |= AVFMT_FLAG_CUSTOM_IO;
    CHECK(avformat_open_input(&format, "fixture.mp4", av_find_input_format("mov"), nullptr) == 0);
    CHECK(format->nb_streams > 0);
    // FATE mov/dovi-p7.mp4 has separate BL/EL tracks (not this helper's input
    // contract). Borrow only its actual RPU, then deliberately place it BEFORE
    // NAL 63 in our single-track AU. Never claim this adds dual-track support.
    Bytes rpu;
    Packet pkt(av_packet_alloc()); CHECK(pkt);
    while (rpu.empty() && av_read_frame(format, pkt.get()) >= 0) {
        auto *par = format->streams[pkt->stream_index]->codecpar;
        const auto *cfg = av_packet_side_data_get(par->coded_side_data, par->nb_coded_side_data,
                                                 AV_PKT_DATA_DOVI_CONF);
        if (par->codec_id == AV_CODEC_ID_HEVC && cfg &&
            cfg->size >= sizeof(AVDOVIDecoderConfigurationRecord) &&
            reinterpret_cast<const AVDOVIDecoderConfigurationRecord *>(cfg->data)->dv_profile == 7) {
            CHECK(par->extradata_size >= 23 && par->extradata[0] == 1);
            const int length = (par->extradata[21] & 3) + 1;
            for (auto &nal : unpack(pkt.get(), length))
                if (((nal[0] >> 1) & 63) == 62) rpu = std::move(nal);
        }
        av_packet_unref(pkt.get());
    }
    CHECK(!rpu.empty());
    avformat_close_input(&format);
    av_freep(&io->buffer); avio_context_free(&io);
    auto par = parameters(true);
    FilmDovi dovi; CHECK(dovi.open(par.get(), time_base) == MFK_OK);
    auto *codec = avcodec_find_decoder_by_name("hevc"); CHECK(codec);
    auto *decoder = avcodec_alloc_context3(codec); CHECK(decoder);
    CHECK(avcodec_parameters_to_context(decoder, par.get()) == 0);
    decoder->pkt_timebase = time_base; decoder->thread_count = 1;
    CHECK(avcodec_open2(decoder, codec, nullptr) == 0);
    std::deque<Frame> waiting;
    int pictures = 0, residuals = 0, metadata = 0;
    auto receive = [&] {
        for (;;) {
            Frame frame(av_frame_alloc()); CHECK(frame);
            int result = avcodec_receive_frame(decoder, frame.get());
            if (result == AVERROR(EAGAIN) || result == AVERROR_EOF) break;
            CHECK(result == 0);
            waiting.push_back(std::move(frame));
        }
    };
    auto pair = [&] {
        while (!waiting.empty()) {
            auto *bl = waiting.front().get();
            AVFrame *raw = nullptr;
            int result = dovi.pair(bl, &raw);
            if (result == AVERROR(EAGAIN)) break;
            CHECK(result == MFK_OK);
            Frame el(raw);
            const auto *parsed = av_frame_get_side_data(bl, AV_FRAME_DATA_DOVI_METADATA);
            const auto *encoded = av_frame_get_side_data(bl, AV_FRAME_DATA_DOVI_RPU_BUFFER);
            CHECK(parsed && encoded);
            size_t payload = rpu.size();
            while (payload > 2 && !rpu[payload - 1]) payload--;
            CHECK(encoded->size >= payload - 2 &&
                  memcmp(encoded->data, rpu.data() + 2, payload - 2) == 0);
            metadata++;
            residuals += el != nullptr;
            pictures++; waiting.pop_front();
        }
    };
    for (int i = 0; i < 8; i++) {
        const auto &s = samples[i];
        auto nals = sample_nals(s, false);
        Bytes padded = rpu;
        if (i % 3 == 1) while (!padded.back()) padded.pop_back();
        else if (i % 3 == 2) padded.insert(padded.end(), 8, 0);
        nals.push_back(std::move(padded));
        auto el = sample_nals(s, true);
        nals.insert(nals.end(), el.begin(), el.end());
        if (i == 7) {
            nals.push_back(Bytes{0x48, 1, 0x80}); // EOS and EOB after RPU and EL.
            nals.push_back(Bytes{0x4a, 1, 0x80});
        }
        pkt = packet(pack(nals), s.pts * step, (i - 2) * step);
        CHECK(dovi.send(pkt.get()) == MFK_OK);
        CHECK(avcodec_send_packet(decoder, pkt.get()) == 0);
        receive(); pair();
    }
    CHECK(dovi.send(nullptr) == MFK_OK);
    CHECK(avcodec_send_packet(decoder, nullptr) == 0);
    receive(); pair();
    CHECK(waiting.empty() && pictures == 8 && metadata == pictures);
    std::printf("real RPU / synthetic NAL63 AUs: %d decoded pictures, %d complete metadata, %d residual pairs\n",
                pictures, metadata, residuals);
    // A CRC-only mutation must fail even though the RPU is syntactically valid.
    avcodec_flush_buffers(decoder);
    Bytes corrupt = rpu;
    while (!corrupt.back()) corrupt.pop_back();
    CHECK(corrupt.size() > 3 && corrupt.back() == 0x80);
    corrupt[corrupt.size() - 2] ^= 1;
    auto nals = sample_nals(samples[0], false);
    nals.push_back(std::move(corrupt));
    auto el = sample_nals(samples[0], true);
    nals.insert(nals.end(), el.begin(), el.end());
    pkt = packet(pack(nals), 0, -2 * step);
    int result = avcodec_send_packet(decoder, pkt.get());
    if (result >= 0) {
        Frame frame(av_frame_alloc()); CHECK(frame);
        result = avcodec_receive_frame(decoder, frame.get());
    }
    CHECK(result == AVERROR_INVALIDDATA);
    avcodec_free_context(&decoder);
}

void test_real_dual_track_prefix(const char *path)
{
    std::ifstream file(path, std::ios::binary); CHECK(file.good());
    Memory bytes{Bytes(std::istreambuf_iterator<char>(file), {})};
    auto *buffer = static_cast<uint8_t *>(av_malloc(4096)); CHECK(buffer);
    auto *io = avio_alloc_context(buffer, 4096, 0, &bytes, Memory::read, nullptr, Memory::seek);
    CHECK(io);
    AVFormatContext *format = avformat_alloc_context(); CHECK(format);
    format->pb = io; format->flags |= AVFMT_FLAG_CUSTOM_IO;
    CHECK(avformat_open_input(&format, "fixture.mp4", av_find_input_format("mov"), nullptr) == 0);
    CHECK(format->nb_streams == 2);
    FilmDoviInput input;
    CHECK(input.open(format, 0) == MFK_OK);
    auto *bl = format->streams[input.base_index()], *el = format->streams[input.enhancement_index()];
    CHECK(bl->codecpar->codec_id == AV_CODEC_ID_HEVC && el->codecpar->codec_id == AV_CODEC_ID_HEVC);
    CHECK(bl->time_base.num == el->time_base.num && bl->time_base.den == el->time_base.den);
    CHECK(bl->codecpar->extradata_size >= 23 && el->codecpar->extradata_size >= 23);
    CHECK(bl->codecpar->extradata[0] == 1 && el->codecpar->extradata[0] == 1);
    const auto *sd = av_packet_side_data_get(el->codecpar->coded_side_data,
                                            el->codecpar->nb_coded_side_data, AV_PKT_DATA_DOVI_CONF);
    CHECK(sd && sd->size >= sizeof(AVDOVIDecoderConfigurationRecord));
    const auto *config = reinterpret_cast<const AVDOVIDecoderConfigurationRecord *>(sd->data);
    CHECK(config->dv_profile == 7 && config->el_present_flag && !config->bl_present_flag);
    const AVRational ticks = bl->time_base;
    const int el_width = el->codecpar->width, el_height = el->codecpar->height;
    std::vector<Packet> packets;
    int bases = 0, enhancements = 0;
    Packet pkt(av_packet_alloc()); CHECK(pkt);
    // The public FATE sample's tenth AU has a duplicate PTS. Do not mask it or
    // claim the whole file passes: test only its first nine complete BL/EL AUs.
    while ((bases < 9 || enhancements < 9) && av_read_frame(format, pkt.get()) >= 0) {
        auto &count = pkt->stream_index == input.base_index() ? bases : enhancements;
        CHECK(count < 9); count++;
        packets.emplace_back(av_packet_clone(pkt.get())); CHECK(packets.back());
        av_packet_unref(pkt.get());
    }
    CHECK(bases == 9 && enhancements == 9);
    avformat_close_input(&format);
    av_freep(&io->buffer); avio_context_free(&io);
    FilmDovi dovi; CHECK(dovi.open(input.parameters(), ticks) == MFK_OK);
    auto *codec = avcodec_find_decoder_by_name("hevc"); CHECK(codec);
    auto *decoder = avcodec_alloc_context3(codec); CHECK(decoder);
    CHECK(avcodec_parameters_to_context(decoder, input.parameters()) == 0);
    decoder->pkt_timebase = ticks; decoder->thread_count = 2;
    CHECK(avcodec_open2(decoder, codec, nullptr) == 0);
    std::deque<Frame> waiting;
    int pictures = 0;
    int64_t last_pts = AV_NOPTS_VALUE;
    auto receive_pair = [&] {
        for (;;) {
            Frame frame(av_frame_alloc()); CHECK(frame);
            int result = avcodec_receive_frame(decoder, frame.get());
            if (result == AVERROR(EAGAIN) || result == AVERROR_EOF) break;
            CHECK(result == 0 && !frame->decode_error_flags && !(frame->flags & AV_FRAME_FLAG_CORRUPT));
            waiting.push_back(std::move(frame));
        }
        while (!waiting.empty()) {
            auto *base = waiting.front().get();
            AVFrame *raw = nullptr;
            int result = dovi.pair(base, &raw);
            if (result == AVERROR(EAGAIN)) break;
            CHECK(result == MFK_OK);
            Frame enhancement(raw);
            CHECK(enhancement && enhancement->pts == base->pts);
            CHECK(enhancement->width == el_width && enhancement->height == el_height);
            CHECK(av_frame_get_side_data(base, AV_FRAME_DATA_DOVI_METADATA));
            CHECK(av_frame_get_side_data(base, AV_FRAME_DATA_DOVI_RPU_BUFFER));
            CHECK(last_pts == AV_NOPTS_VALUE || base->pts > last_pts);
            last_pts = base->pts;
            pictures++; waiting.pop_front();
        }
    };
    for (const auto &source : packets) {
        CHECK(input.send(source.get()) == MFK_OK);
        for (;;) {
            AVPacket *raw = nullptr;
            CHECK(input.take(&raw) == MFK_OK); Packet merged(raw);
            if (!merged) break;
            CHECK(dovi.send(merged.get()) == MFK_OK && avcodec_send_packet(decoder, merged.get()) == 0);
            receive_pair();
        }
    }
    CHECK(input.finish() == MFK_OK);
    CHECK(dovi.send(nullptr) == MFK_OK && avcodec_send_packet(decoder, nullptr) == 0);
    receive_pair();
    CHECK(waiting.empty() && pictures == 9 && dovi.status() == MFK_OK);
    std::printf("real P7 dual-track input: %d decoded/paired AUs (nine-AU prefix, not whole file)\n", pictures);
    avcodec_free_context(&decoder);
}
}

int main(int argc, char **argv)
{
    test_nals();
    test_configuration();
    test_metadata();
    test_decode_and_seek();
    test_dual_track_input();
    test_backpressure();
    test_missing_pts_and_el();
    test_rpu_failure();
    test_hvce_demux();
    std::puts("FilmDovi: 9 test groups passed");
    if (argc == 2) {
        test_real_rpu(argv[1]);
        test_real_dual_track_prefix(argv[1]);
    }
    else CHECK(argc == 1);
    return 0;
}
