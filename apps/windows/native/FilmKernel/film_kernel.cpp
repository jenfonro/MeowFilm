#include "film_kernel.h"
#include "film_audio.h"
#include "film_renderer.h"
#include "film_dovi.h"
#include <algorithm>
#include <atomic>
#include <cctype>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstddef>
#include <cstdio>
#include <cstring>
#include <deque>
#include <future>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>
extern "C" {
#include "film_io.h"
#include <libavutil/dovi_meta.h>
#include <libavutil/error.h>
#include <libavutil/hwcontext.h>
#include <libavutil/mastering_display_metadata.h>
#include <libavutil/pixdesc.h>
}

// Compile-time C ABI v3 layout verification (zero implicit padding).
static_assert(MFK_ABI == 3u, "Expected MFK_ABI == 3");
static_assert(sizeof(mfk_snapshot) == 96, "mfk_snapshot size mismatch");
static_assert(offsetof(mfk_snapshot, position) == 16, "mfk_snapshot.position offset");
static_assert(offsetof(mfk_snapshot, width) == 40, "mfk_snapshot.width offset");
static_assert(offsetof(mfk_snapshot, submitted_pictures) == 88, "mfk_snapshot.submitted_pictures offset");
static_assert(sizeof(mfk_diagnostics) == 280, "mfk_diagnostics size mismatch");
static_assert(offsetof(mfk_diagnostics, av_sync_diff) == 40, "mfk_diagnostics.av_sync_diff offset");
static_assert(offsetof(mfk_diagnostics, video_packet_queue) == 56, "mfk_diagnostics.video_packet_queue offset");
static_assert(offsetof(mfk_diagnostics, decode_fallback_reason) == 88, "mfk_diagnostics.decode_fallback_reason offset");
static_assert(offsetof(mfk_diagnostics, last_native_error) == 152, "mfk_diagnostics.last_native_error offset");
static_assert(sizeof(mfk_track) == 208, "mfk_track size mismatch");
static_assert(sizeof(mfk_chapter) == 280, "mfk_chapter size mismatch");
static_assert(sizeof(mfk_surface) == 40, "mfk_surface size mismatch");

namespace {
using Clock = std::chrono::steady_clock;
constexpr size_t picture_limit = 12;
constexpr size_t prepared_picture_limit = 16;
constexpr size_t waiting_base_limit = 32;
constexpr size_t video_pkt_max_count = 256;
constexpr size_t video_pkt_max_bytes = 64 * 1024 * 1024;
constexpr size_t audio_pkt_max_count = 1024;
constexpr size_t audio_pkt_max_bytes = 32 * 1024 * 1024;
constexpr size_t sub_pkt_max_count = 256;
constexpr size_t sub_pkt_max_bytes = 8 * 1024 * 1024;

struct FrameDelete { void operator()(AVFrame *frame) const { av_frame_free(&frame); } };
using Frame = std::unique_ptr<AVFrame, FrameDelete>;
struct PacketDelete { void operator()(AVPacket *packet) const { av_packet_free(&packet); } };
using Packet = std::unique_ptr<AVPacket, PacketDelete>;

void copy_utf8(char *dst, size_t cap, const char *src)
{
    if (!dst || cap == 0) return;
    if (!src) { dst[0] = '\0'; return; }
    size_t len = strnlen(src, cap - 1);
    if (len == cap - 1 && src[len] != '\0') {
        // Back off incomplete multi-byte UTF-8 sequence at boundary.
        while (len > 0 && (static_cast<unsigned char>(src[len]) & 0xC0) == 0x80) --len;
        if (len > 0) {
            const unsigned char lead = static_cast<unsigned char>(src[len - 1]);
            size_t seq = (lead & 0x80) == 0 ? 1 :
                         (lead & 0xE0) == 0xC0 ? 2 :
                         (lead & 0xF0) == 0xE0 ? 3 :
                         (lead & 0xF8) == 0xF0 ? 4 : 1;
            if (seq > 1 && len + seq - 1 > cap - 1) --len;
        }
    }
    memcpy(dst, src, len);
    dst[len] = '\0';
}

std::string redact_message(const char *raw)
{
    if (!raw) return {};
    std::string out(raw);
    for (const char *scheme : {"https://", "http://"}) {
        size_t pos = 0;
        while ((pos = out.find(scheme, pos)) != std::string::npos) {
            size_t end = pos;
            while (end < out.size() && !std::isspace(static_cast<unsigned char>(out[end])) &&
                   out[end] != '\'' && out[end] != '"' && out[end] != ')' && out[end] != ']')
                ++end;
            out.replace(pos, end - pos, "[redacted-url]");
            pos += 14;
        }
    }
    return out;
}

struct Picture {
    Frame base, enhancement;
    double pts = 0;
    uint64_t generation = 0;
    double duration = 0;
};

struct QueuedPacket {
    Packet packet;
    size_t bytes = 0;
    uint64_t serial = 0;
};

struct PacketQueue {
    std::deque<QueuedPacket> items;
    size_t bytes = 0;
    size_t max_count = 0;
    size_t max_bytes = 0;
    bool eof = false;

    void configure(size_t count_cap, size_t bytes_cap)
    {
        max_count = count_cap;
        max_bytes = bytes_cap;
    }
    bool full() const { return items.size() >= max_count || bytes >= max_bytes; }
    bool empty() const { return items.empty(); }
    void push(Packet pkt, size_t cost, uint64_t serial)
    {
        bytes += cost;
        items.push_back({std::move(pkt), cost, serial});
    }
    QueuedPacket pop()
    {
        QueuedPacket item = std::move(items.front());
        items.pop_front();
        bytes = bytes >= item.bytes ? bytes - item.bytes : 0;
        return item;
    }
    void clear()
    {
        items.clear();
        bytes = 0;
        eof = false;
    }
};

struct Command {
    enum Kind { Seek, VideoTrack, AudioTrack, SubtitleTrack } kind;
    double value = 0;
    std::promise<int> result;
};

/* Lock hierarchy (strict single direction — NEVER hold gate across WASAPI, GPU or IO):
 *   command_gate -> output_gate -> gate
 * External commands serialize on command_gate; GPU / subtitle codec serialize on
 * output_gate; lightweight state & packet/picture queues serialize on gate. */
struct Owner {
    mfk_input input{};
    std::atomic<bool> cancelled{false};
    std::mutex command_gate;
    std::mutex output_gate;
    std::mutex audio_pause_gate;
    mutable std::mutex gate;
    std::condition_variable changed;

    std::thread demux_thread, video_thread, audio_thread, present_thread;
    bool preparing = false, prepared = false, committed = false;
    bool demux_alive = false, video_alive = false, audio_alive = false;
    bool quiesce_video = false, quiesce_audio = false;
    bool video_quiesced = false, audio_quiesced = false;
    bool seeking = false, video_eof = false, audio_eof = false, input_eof = false;
    bool audio_started = false, audio_anchor_pending = true, audio_handed_off = false;
    bool audio_buffering_paused = false;

    std::atomic<uint64_t> output_generation{0};
    uint64_t video_serial = 1, audio_serial = 1, sub_serial = 1;
    std::atomic<bool> paused{true};
    std::atomic<bool> muted{false};
    std::atomic<double> rate{1.0}, volume{0.8};

    int state = MFK_NEW, error = MFK_OK;
    AVFormatContext *format = nullptr;
    AVIOContext *root = nullptr;
    AVCodecContext *video = nullptr, *sound = nullptr, *subtitle = nullptr;
    int video_index = -1, audio_index = -1, subtitle_index = -1;
    int width = 0, height = 0;
    double start = 0, duration = 0, discard_before = 0, aspect = 0;
    double position = 0, buffered = 0, wall_base = 0;
    double next_video_pts = 0, next_audio_pts = 0;
    double audio_start_pts = 0;
    double last_picture_end = 0;
    Clock::time_point wall_time = Clock::now();

    bool hardware = false, hardware_fallback = false;
    bool seekable = false, native_hdr = false, tone_mapped = false;
    bool display_hdr_enabled = false, dolby_vision = false;
    bool target_attached = false; // Protected by output_gate.

    std::unique_ptr<FilmRenderer> renderer;
    std::shared_ptr<FilmAudio> audio;
    FilmDoviInput dovi_input;
    FilmDovi dovi;

    PacketQueue video_pkts, audio_pkts, sub_pkts;
    std::deque<Picture> pictures;
    std::deque<Picture> waiting_base;
    Frame retained, retained_enhancement;
    std::deque<std::shared_ptr<Command>> commands;
    std::vector<mfk_track> tracks;
    std::vector<mfk_chapter> chapters;

    // Diagnostics counters
    std::atomic<uint64_t> dropped_frames{0};
    std::atomic<double> av_sync_diff{0.0};
    std::atomic<double> render_fps{0.0};
    std::string decode_fallback_reason;
    std::string last_native_error;

    Owner()
    {
        video_pkts.configure(video_pkt_max_count, video_pkt_max_bytes);
        audio_pkts.configure(audio_pkt_max_count, audio_pkt_max_bytes);
        sub_pkts.configure(sub_pkt_max_count, sub_pkt_max_bytes);
    }

    ~Owner()
    {
        cancel();
        if (demux_thread.joinable()) demux_thread.join();
        if (video_thread.joinable()) video_thread.join();
        if (audio_thread.joinable()) audio_thread.join();
        if (present_thread.joinable()) present_thread.join();
        video_pkts.clear();
        audio_pkts.clear();
        sub_pkts.clear();
        pictures.clear();
        waiting_base.clear();
        last_discarded.reset();
        retained.reset();
        retained_enhancement.reset();
        audio.reset();
        avcodec_free_context(&subtitle);
        avcodec_free_context(&sound);
        avcodec_free_context(&video);
        renderer.reset();
        avformat_close_input(&format);
        mfk_io_close_all(&input);
        if (input.keys) { SecureZeroMemory(input.keys, input.key_count * sizeof(mfk_clear_key)); free(input.keys); }
        SecureZeroMemory(input.default_key, sizeof(input.default_key));
        if (input.uri) { SecureZeroMemory(input.uri, strlen(input.uri)); free(input.uri); }
    }

    void log(int level, const char *component, const char *message) const
    {
        if (!input.callbacks.log || !message) return;
        const std::string clean = redact_message(message);
        input.callbacks.log(input.callbacks.opaque, level, component ? component : "film-kernel", clean.c_str());
    }

    void record_error_text(const char *text)
    {
        std::lock_guard lock(gate);
        last_native_error = redact_message(text);
    }

    void cancel()
    {
        cancelled.store(true);
        InterlockedExchange(&input.cancelled, 1);
        std::shared_ptr<FilmAudio> a;
        {
            std::lock_guard lock(gate);
            a = audio;
            changed.notify_all();
        }
        if (a) a->interrupt_write();
    }

    void fail(int code, const char *reason = nullptr) noexcept
    {
        try {
            bool report = false;
            {
                std::lock_guard lock(gate);
                if (!cancelled.load() && error == MFK_OK) {
                    error = code;
                    state = MFK_FAILED;
                    if (reason) last_native_error = redact_message(reason);
                    report = true;
                }
                changed.notify_all();
            }
            if (reason) log(MFK_LOG_ERROR, "film-kernel", reason);
            if (report) input.callbacks.failed(input.callbacks.opaque, code);
        } catch (...) {
        }
    }

    int sync_audio_pause(const std::shared_ptr<FilmAudio> &target)
    {
        if (!target) return MFK_OK;
        std::lock_guard pause_lock(audio_pause_gate);
        bool want = true;
        {
            std::lock_guard lock(gate);
            if (audio != target) return MFK_OK;
            want = paused.load() || seeking || !audio_started || audio_buffering_paused ||
                state == MFK_BUFFERING || state == MFK_ENDED;
        }
        return target->pause(want);
    }

    bool interrupted() const { return cancelled.load() || mfk_interrupted(const_cast<mfk_input *>(&input)); }

    double clock_locked() const
    {
        if (audio && audio_started && !audio_handed_off && !audio_buffering_paused) {
            const auto current = audio->snapshot();
            if (current.has_clock && !current.drained) return current.position;
        }
        if (paused.load() || seeking || !committed || state == MFK_BUFFERING || state == MFK_ENDED)
            return position;
        return wall_base + std::chrono::duration<double>(Clock::now() - wall_time).count() * rate.load();
    }

    static size_t packet_cost(const AVPacket *packet)
    {
        constexpr size_t limit = 16 * 1024 * 1024;
        if (!packet || packet->size < 0 || static_cast<size_t>(packet->size) > limit) return limit + 1;
        size_t bytes = sizeof(AVPacket) + static_cast<size_t>(packet->size);
        for (int i = 0; i < packet->side_data_elems; i++) {
            if (packet->side_data[i].size > limit || bytes > limit - packet->side_data[i].size)
                return limit + 1;
            bytes += packet->side_data[i].size;
        }
        return bytes;
    }

    void update_stream_discards()
    {
        if (!format) return;
        const int el_idx = dovi_input.enhancement_index();
        for (unsigned i = 0; i < format->nb_streams; i++) {
            const int idx = static_cast<int>(i);
            const bool keep = (idx == video_index) || (idx == el_idx) ||
                              (idx == audio_index) || (idx == subtitle_index);
            format->streams[i]->discard = keep ? AVDISCARD_DEFAULT : AVDISCARD_ALL;
        }
    }

    void update_track_selection_locked()
    {
        for (auto &track : tracks) {
            track.selected = (track.index == video_index) ||
                             (track.index == audio_index) ||
                             (track.index == subtitle_index);
        }
    }

    static AVPixelFormat choose_format(AVCodecContext *context, const AVPixelFormat *formats)
    {
        if (context->hw_device_ctx)
            for (const auto *format = formats; *format != AV_PIX_FMT_NONE; format++)
                if (*format == AV_PIX_FMT_D3D11) return *format;
        for (const auto *format = formats; *format != AV_PIX_FMT_NONE; format++)
            if (const auto *descriptor = av_pix_fmt_desc_get(*format);
                descriptor && !(descriptor->flags & AV_PIX_FMT_FLAG_HWACCEL)) return *format;
        return AV_PIX_FMT_NONE;
    }

    AVCodecContext *open_codec(int index, bool acceleration,
                               const AVCodecParameters *override_params = nullptr)
    {
        if (index < 0 || !format || static_cast<unsigned>(index) >= format->nb_streams) return nullptr;
        const auto *stream = format->streams[index];
        const auto *parameters = override_params ? override_params :
            (index == video_index ? dovi_input.parameters() : stream->codecpar);
        const auto *codec = avcodec_find_decoder(parameters->codec_id);
        if (!codec) return nullptr;
        auto *context = avcodec_alloc_context3(codec);
        if (!context || avcodec_parameters_to_context(context, parameters) < 0) {
            avcodec_free_context(&context);
            return nullptr;
        }
        context->pkt_timebase = stream->time_base;
        const int hw_threads = static_cast<int>(std::thread::hardware_concurrency());
        const int sw_threads = std::clamp(hw_threads > 0 ? hw_threads / 2 : 4, 2, 8);
        bool hw_attached = false;
        if (acceleration && renderer && renderer->hardware_device()) {
            for (int i = 0; ; i++) {
                const auto *config = avcodec_get_hw_config(codec, i);
                if (!config) break;
                if (config->device_type == AV_HWDEVICE_TYPE_D3D11VA &&
                    (config->methods & AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX)) {
                    context->hw_device_ctx = av_buffer_ref(renderer->hardware_device());
                    context->get_format = choose_format;
                    context->extra_hw_frames = static_cast<int>(prepared_picture_limit + waiting_base_limit + 4);
                    hw_attached = true;
                    break;
                }
            }
        }
        context->thread_count = hw_attached ? 1 : (stream->codecpar->codec_type == AVMEDIA_TYPE_VIDEO ? sw_threads : 2);
        if (avcodec_open2(context, codec, nullptr) < 0) {
            avcodec_free_context(&context);
            return nullptr;
        }
        return context;
    }

    double pts(const AVFrame *frame, int index, double fallback) const
    {
        if (!frame || frame->best_effort_timestamp == AV_NOPTS_VALUE) return fallback;
        return frame->best_effort_timestamp * av_q2d(format->streams[index]->time_base) - start;
    }

    void copy_stream_metadata(AVFrame *frame)
    {
        const auto *parameters = format->streams[video_index]->codecpar;
        if (frame->color_trc == AVCOL_TRC_UNSPECIFIED) frame->color_trc = parameters->color_trc;
        if (frame->color_primaries == AVCOL_PRI_UNSPECIFIED) frame->color_primaries = parameters->color_primaries;
        if (frame->colorspace == AVCOL_SPC_UNSPECIFIED) frame->colorspace = parameters->color_space;
        if (frame->color_range == AVCOL_RANGE_UNSPECIFIED) frame->color_range = parameters->color_range;
        for (auto type : {AV_PKT_DATA_MASTERING_DISPLAY_METADATA, AV_PKT_DATA_CONTENT_LIGHT_LEVEL}) {
            const auto *side = av_packet_side_data_get(parameters->coded_side_data,
                parameters->nb_coded_side_data, type);
            const auto target = type == AV_PKT_DATA_MASTERING_DISPLAY_METADATA ?
                AV_FRAME_DATA_MASTERING_DISPLAY_METADATA : AV_FRAME_DATA_CONTENT_LIGHT_LEVEL;
            if (side && !av_frame_get_side_data(frame, target)) {
                auto *copy = av_frame_new_side_data(frame, target, side->size);
                if (copy) memcpy(copy->data, side->data, side->size);
            }
        }
    }

    // Track last discarded frame during seek/prepare so seeking right to EOF
    // still presents the final frame rather than failing with MFK_SEEK_FAILED.
    Frame last_discarded;
    double last_discarded_pts = 0;
    double last_discarded_dur = 0;

    int enqueue_picture(Frame frame, bool preparing_phase)
    {
        copy_stream_metadata(frame.get());
        const double time = pts(frame.get(), video_index, next_video_pts);
        const auto frame_rate = av_guess_frame_rate(format, format->streams[video_index], nullptr);
        const double length = frame->duration > 0 ?
            frame->duration * av_q2d(format->streams[video_index]->time_base) :
            frame_rate.num > 0 && frame_rate.den > 0 ? av_q2d(av_inv_q(frame_rate)) : 0.0;
        next_video_pts = time + length;

        if ((length > 0 ? time + length : time) + 0.000001 < discard_before) {
            if (preparing_phase) {
                // Only advance DoVi discard past the previously superseded frame so
                // last_discarded itself can still pair if seek lands at/past EOF.
                if (last_discarded && last_discarded->pts != AV_NOPTS_VALUE &&
                    last_discarded->pts < INT64_MAX) {
                    int result = dovi.discard_before(last_discarded->pts + 1);
                    if (result != MFK_OK) return result;
                }
                last_discarded = std::move(frame);
                last_discarded_pts = time;
                last_discarded_dur = length;
            } else if (frame && frame->pts != AV_NOPTS_VALUE && frame->pts < INT64_MAX) {
                int result = dovi.discard_before(frame->pts + 1);
                if (result != MFK_OK) return result;
            }
            return MFK_OK;
        }
        if (waiting_base.size() >= waiting_base_limit) return MFK_DOVI_UNSUPPORTED;
        waiting_base.push_back({std::move(frame), {}, time, output_generation.load(), length});
        return pair_waiting(preparing_phase);
    }

    int receive_video(bool preparing_phase)
    {
        for (;;) {
            Frame frame(av_frame_alloc());
            if (!frame) return MFK_DECODE_FAILED;
            const int result = avcodec_receive_frame(video, frame.get());
            if (result == AVERROR(EAGAIN) || result == AVERROR_EOF) return MFK_OK;
            if (result < 0) return MFK_DECODE_FAILED;
            const int queued = enqueue_picture(std::move(frame), preparing_phase);
            if (queued != MFK_OK) return queued;
        }
    }

    int pair_waiting(bool preparing_phase)
    {
        while (!waiting_base.empty()) {
            AVFrame *enhancement = nullptr;
            int paired = dovi.pair(waiting_base.front().base.get(), &enhancement);
            if (paired == AVERROR(EAGAIN)) return MFK_OK;
            if (paired != MFK_OK) return paired;
            Picture picture = std::move(waiting_base.front());
            waiting_base.pop_front();
            picture.enhancement.reset(enhancement);
            const double time = picture.pts;
            FilmRenderer::InspectInfo info{};
            const int result = renderer->inspect(picture.base.get(), picture.enhancement.get(), &info);
            if (result != MFK_OK) return result;
            bool log_runtime_fallback = false;
            {
                std::unique_lock lock(gate);
                if (!preparing_phase) {
                    changed.wait(lock, [&] {
                        return interrupted() || quiesce_video || error != MFK_OK || pictures.size() < picture_limit;
                    });
                    if (interrupted()) return MFK_CANCELLED;
                    if (quiesce_video) return MFK_OK;
                    if (error != MFK_OK) return error;
                }
                if (pictures.size() >= prepared_picture_limit) return MFK_FORMAT_UNSUPPORTED;
                width = info.display_width > 0 ? info.display_width : picture.base->width;
                height = info.display_height > 0 ? info.display_height : picture.base->height;
                const bool is_hw = picture.base->format == AV_PIX_FMT_D3D11;
                hardware = is_hw;
                if (!is_hw && video && video->hw_device_ctx != nullptr && !hardware_fallback) {
                    hardware_fallback = true;
                    if (decode_fallback_reason.empty())
                        decode_fallback_reason = "d3d11va_runtime_fallback";
                    log_runtime_fallback = true;
                }
                pictures.push_back(std::move(picture));
                if (preparing_phase) {
                    native_hdr = info.native_hdr != 0;
                    tone_mapped = info.tone_mapped != 0;
                    display_hdr_enabled = info.display_hdr_enabled != 0;
                    dolby_vision = info.dolby_vision != 0;
                }
                buffered = std::max(buffered, time + pictures.back().duration);
                changed.notify_all();
            }
            if (log_runtime_fallback) {
                log(MFK_LOG_WARN, "video-decode", "D3D11VA fell back to software decoding at runtime");
            }
        }
        return MFK_OK;
    }

    int decode_video_packet(const AVPacket *packet, bool preparing_phase)
    {
        int result = dovi.send(packet);
        if (result == AVERROR(EAGAIN)) {
            result = receive_video(preparing_phase);
            if (result == MFK_OK) result = pair_waiting(preparing_phase);
            if (result == MFK_OK) result = dovi.send(packet);
            if (result == AVERROR(EAGAIN)) return MFK_DOVI_UNSUPPORTED;
        }
        if (result != MFK_OK) return result;
        result = pair_waiting(preparing_phase);
        if (result != MFK_OK) return result;
        result = avcodec_send_packet(video, packet);
        if (result == AVERROR(EAGAIN)) {
            int drained = receive_video(preparing_phase);
            if (drained != MFK_OK) return drained;
            result = avcodec_send_packet(video, packet);
        }
        if (result < 0) {
            if (result == AVERROR_INVALIDDATA) {
                // Non-fatal corrupt packet on ordinary stream; skip and continue.
                log(MFK_LOG_WARN, "video-decode", "Skipped corrupt video packet");
                return MFK_OK;
            }
            return MFK_DECODE_FAILED;
        }
        return receive_video(preparing_phase);
    }

    int process_video_packet(const AVPacket *packet, bool preparing_phase)
    {
        if (dovi_input.enhancement_index() < 0) return decode_video_packet(packet, preparing_phase);
        int result = dovi_input.send(packet);
        while (result == MFK_OK) {
            AVPacket *merged = nullptr;
            result = dovi_input.take(&merged);
            Packet owned(merged);
            if (result != MFK_OK || !owned) break;
            result = decode_video_packet(owned.get(), preparing_phase);
        }
        return result;
    }

    int drain_video(bool preparing_phase)
    {
        if (dovi_input.finish() != MFK_OK) return MFK_DOVI_UNSUPPORTED;
        int result = MFK_OK;
        int sent = avcodec_send_packet(video, nullptr);
        if (sent == AVERROR(EAGAIN)) {
            result = receive_video(preparing_phase);
            if (result != MFK_OK) return result;
            sent = avcodec_send_packet(video, nullptr);
        }
        if (sent < 0 && sent != AVERROR_EOF) return MFK_DECODE_FAILED;
        result = receive_video(preparing_phase);
        if (result == MFK_OK) {
            result = dovi.send(nullptr);
            if (result == AVERROR(EAGAIN)) {
                result = pair_waiting(preparing_phase);
                if (result == MFK_OK) result = dovi.send(nullptr);
                if (result == AVERROR(EAGAIN)) return MFK_DOVI_UNSUPPORTED;
            }
        }
        if (result == MFK_OK) result = pair_waiting(preparing_phase);
        if (result == MFK_OK && !waiting_base.empty()) result = MFK_DOVI_UNSUPPORTED;
        return result;
    }

    int receive_audio_frames()
    {
        for (;;) {
            Frame frame(av_frame_alloc());
            if (!frame) return MFK_DECODE_FAILED;
            int result = avcodec_receive_frame(sound, frame.get());
            if (result == AVERROR(EAGAIN) || result == AVERROR_EOF) return MFK_OK;
            if (result < 0) {
                if (result == AVERROR_INVALIDDATA) continue;
                return MFK_DECODE_FAILED;
            }
            if (frame->sample_rate <= 0 || frame->nb_samples < 0) continue;
            double time = pts(frame.get(), audio_index, next_audio_pts);
            const double frame_dur = static_cast<double>(frame->nb_samples) / frame->sample_rate;
            next_audio_pts = time + frame_dur;
            if (time + frame_dur <= discard_before) continue;

            // Clamp trimming safely without overflowing int32 or mutating extended_data twice.
            std::shared_ptr<FilmAudio> out_audio;
            bool should_start_audio = false;
            {
                std::lock_guard lock(gate);
                if (quiesce_audio || interrupted()) return MFK_CANCELLED;
                out_audio = audio;
                if (audio_anchor_pending) {
                    audio_start_pts = time;
                    audio_anchor_pending = false;
                }
                if (out_audio && !audio_started && !paused.load() && !seeking &&
                    !audio_buffering_paused && time <= clock_locked() + 0.002) {
                    audio_started = true;
                    audio_handed_off = false;
                    should_start_audio = true;
                }
            }
            if (!out_audio) {
                auto created = std::make_shared<FilmAudio>(
                    cancelled,
                    [this] { return interrupted(); },
                    [this](int lvl, const char *comp, const char *msg) { log(lvl, comp, msg); });
                result = created->open(sound, rate.load());
                if (result != MFK_OK) return result;
                created->set_volume(volume.load());
                created->set_muted(muted.load());
                {
                    std::lock_guard lock(gate);
                    if (quiesce_audio || interrupted()) return MFK_CANCELLED;
                    audio = created;
                    out_audio = created;
                    if (!audio_started && !paused.load() && !seeking &&
                        !audio_buffering_paused && time <= clock_locked() + 0.002) {
                        audio_started = true;
                        audio_handed_off = false;
                        should_start_audio = true;
                    }
                }
            }
            if (should_start_audio) {
                // Serialize pause sync outside gate using the latest desired state.
                int pr = sync_audio_pause(out_audio);
                if (pr != MFK_OK) return pr;
            }
            // FilmAudio::write itself trims any samples preceding trim_before safely.
            result = out_audio->write(frame.get(), time);
            if (result == MFK_CANCELLED) {
                std::lock_guard lock(gate);
                if (quiesce_audio) return MFK_CANCELLED;
            }
            if (result != MFK_OK) return result;
            {
                std::lock_guard lock(gate);
                buffered = std::max(buffered, time + frame_dur);
            }
        }
    }

    // Decodes ready subtitle packets on present_thread (or prime) under output_gate.
    void drain_subtitles_locked_output(double current_pts)
    {
        if (!subtitle || !renderer) return;
        for (;;) {
            QueuedPacket qp;
            int sidx = -1;
            {
                std::lock_guard lock(gate);
                if (sub_pkts.empty()) return;
                sidx = subtitle_index;
                const auto &front = sub_pkts.items.front();
                if (front.serial != sub_serial || sidx < 0) {
                    sub_pkts.pop();
                    continue;
                }
                if (front.packet->pts != AV_NOPTS_VALUE) {
                    const double pkt_pts = front.packet->pts * av_q2d(format->streams[sidx]->time_base) - start;
                    if (pkt_pts > current_pts + 1.5) return;
                }
                qp = sub_pkts.pop();
                changed.notify_all();
            }
            AVSubtitle decoded{};
            int got = 0;
            if (avcodec_decode_subtitle2(subtitle, &decoded, &got, qp.packet.get()) >= 0 && got) {
                const double pkt_pts = qp.packet->pts == AV_NOPTS_VALUE ? current_pts :
                    qp.packet->pts * av_q2d(format->streams[sidx]->time_base) - start;
                renderer->subtitle(decoded, pkt_pts, start, subtitle->width, subtitle->height);
            }
            avsubtitle_free(&decoded);
        }
    }

    void park_workers(bool park_video, bool park_audio)
    {
        std::shared_ptr<FilmAudio> a;
        {
            std::lock_guard lock(gate);
            if (park_video) quiesce_video = true;
            if (park_audio) {
                quiesce_audio = true;
                a = audio;
            }
            changed.notify_all();
        }
        if (a) a->interrupt_write();
        std::unique_lock lock(gate);
        changed.wait(lock, [&] {
            if (interrupted()) return true;
            const bool v_ok = !park_video || !video_alive || video_quiesced;
            const bool a_ok = !park_audio || !audio_alive || audio_quiesced;
            return v_ok && a_ok;
        });
    }

    void unpark_workers(bool unpark_video, bool unpark_audio)
    {
        std::lock_guard lock(gate);
        if (unpark_video) quiesce_video = false;
        if (unpark_audio) quiesce_audio = false;
        changed.notify_all();
    }

    int seek_to_quiesced(double target)
    {
        if (!seekable || !std::isfinite(target) || target < 0)
            return MFK_SEEK_FAILED;
        if (duration > 0 && target > duration)
            target = duration;
        // Clamp very slightly inside duration so seeking to exact end finds final keyframe.
        const double seek_target = (duration > 0.05 && target >= duration) ? (duration - 0.001) : target;
        const int64_t absolute = static_cast<int64_t>((seek_target + start) * AV_TIME_BASE);
        if (avformat_seek_file(format, -1, INT64_MIN, absolute, absolute, AVSEEK_FLAG_BACKWARD) < 0)
            return interrupted() ? MFK_CANCELLED : MFK_SEEK_FAILED;

        avformat_flush(format);
        avcodec_flush_buffers(video);
        video->skip_frame = AVDISCARD_DEFAULT;
        if (sound) avcodec_flush_buffers(sound);
        dovi_input.flush();
        dovi.flush();
        waiting_base.clear();
        last_discarded.reset();

        std::shared_ptr<FilmAudio> a;
        {
            std::lock_guard lock(gate);
            ++output_generation;
            ++video_serial;
            ++audio_serial;
            ++sub_serial;
            video_pkts.clear();
            audio_pkts.clear();
            sub_pkts.clear();
            pictures.clear();
            retained.reset();
            retained_enhancement.reset();
            position = wall_base = discard_before = buffered = target;
            next_video_pts = next_audio_pts = target;
            last_picture_end = target;
            audio_started = false;
            audio_anchor_pending = true;
            audio_handed_off = false;
            audio_buffering_paused = false;
            wall_time = Clock::now();
            video_eof = audio_eof = input_eof = false;
            if (state == MFK_ENDED) state = paused.load() ? MFK_PAUSED : MFK_PLAYING;
            a = audio;
        }
        {
            std::lock_guard lock(output_gate);
            if (subtitle) avcodec_flush_buffers(subtitle);
            renderer->clear_subtitles();
            if (subtitle) renderer->subtitle_header(subtitle);
        }
        return a ? a->reset(target) : MFK_OK;
    }

    int prime(bool display)
    {
        struct DiscardGuard {
            Frame &slot;
            ~DiscardGuard() { slot.reset(); }
        } discard_guard{last_discarded};
        Packet packet(av_packet_alloc());
        if (!packet) return MFK_DECODE_FAILED;
        last_discarded.reset();
        while (!interrupted()) {
            bool ready = false;
            { std::lock_guard lock(gate); ready = !pictures.empty(); }
            if (ready) {
                Picture first_copy;
                double first_pts = 0;
                {
                    std::lock_guard lock(gate);
                    const auto &picture = pictures.front();
                    first_pts = picture.pts;
                    if (display) {
                        first_copy = {Frame(av_frame_clone(picture.base.get())),
                            Frame(picture.enhancement ? av_frame_clone(picture.enhancement.get()) : nullptr),
                            picture.pts, picture.generation, picture.duration};
                        if (!first_copy.base || (picture.enhancement && !first_copy.enhancement))
                            return MFK_DECODE_FAILED;
                    }
                }
                if (display) {
                    std::lock_guard lock(output_gate);
                    drain_subtitles_locked_output(first_pts);
                    int result = target_attached ?
                        renderer->present(first_copy.base.get(), first_copy.enhancement.get(), aspect, first_copy.pts) : MFK_OK;
                    if (result != MFK_OK) return result;
                }
                std::shared_ptr<FilmAudio> a;
                {
                    std::lock_guard lock(gate);
                    if (display) {
                        retained = std::move(first_copy.base);
                        retained_enhancement = std::move(first_copy.enhancement);
                        // Pop displayed first frame so present_loop does not render it a second time.
                        if (!pictures.empty()) {
                            last_picture_end = pictures.front().pts + pictures.front().duration;
                            pictures.pop_front();
                        }
                    }
                    position = wall_base = std::max(discard_before, first_pts);
                    wall_time = Clock::now();
                    seeking = false;
                    a = audio;
                    changed.notify_all();
                }
                if (a) {
                    int pr = sync_audio_pause(a);
                    if (pr != MFK_OK) return pr;
                }
                return MFK_OK;
            }
            if (input_eof) {
                // Seek landed at/past the last frame: present the last discarded frame if available.
                if (last_discarded) {
                    Frame fallback = std::move(last_discarded);
                    const double fb_pts = last_discarded_pts;
                    const double fb_dur = last_discarded_dur;
                    waiting_base.push_back({std::move(fallback), {}, fb_pts, output_generation.load(), fb_dur});
                    int pr = pair_waiting(true);
                    if (pr == MFK_OK) continue;
                }
                return MFK_SEEK_FAILED;
            }
            int read = av_read_frame(format, packet.get());
            int result = MFK_OK;
            if (read == AVERROR_EOF) {
                result = drain_video(true);
                input_eof = true;
                std::lock_guard lock(gate);
                audio_pkts.eof = true;
                video_pkts.eof = true;
            } else if (read < 0) {
                return interrupted() ? MFK_CANCELLED : MFK_READ_FAILED;
            } else {
                size_t side_size = 0;
                if (av_packet_get_side_data(packet.get(), AV_PKT_DATA_ENCRYPTION_INFO, &side_size)) {
                    av_packet_unref(packet.get());
                    return MFK_ENCRYPTION_UNSUPPORTED;
                }
                const int sidx = packet->stream_index;
                if (sidx == video_index || sidx == dovi_input.enhancement_index()) {
                    result = process_video_packet(packet.get(), true);
                } else if (sidx == audio_index && sound) {
                    // Drop obviously stale pre-seek audio packets; queue the rest without a 1024-packet ceiling.
                    bool skip = false;
                    if (packet->pts != AV_NOPTS_VALUE && packet->duration > 0) {
                        const double end_pts = (packet->pts + packet->duration) *
                            av_q2d(format->streams[audio_index]->time_base) - start;
                        if (end_pts < discard_before - 0.1) skip = true;
                    }
                    if (!skip) {
                        const size_t cost = packet_cost(packet.get());
                        std::lock_guard lock(gate);
                        if (cost <= audio_pkt_max_bytes && audio_pkts.bytes + cost <= audio_pkt_max_bytes) {
                            Packet copy(av_packet_clone(packet.get()));
                            if (!copy) { av_packet_unref(packet.get()); return MFK_DECODE_FAILED; }
                            audio_pkts.push(std::move(copy), cost, audio_serial);
                        }
                    }
                } else if (sidx == subtitle_index && subtitle) {
                    const size_t cost = packet_cost(packet.get());
                    std::lock_guard lock(gate);
                    if (cost <= sub_pkt_max_bytes && !sub_pkts.full()) {
                        Packet copy(av_packet_clone(packet.get()));
                        if (copy) sub_pkts.push(std::move(copy), cost, sub_serial);
                    }
                }
                av_packet_unref(packet.get());
            }
            if (result != MFK_OK) return result;
        }
        return MFK_CANCELLED;
    }

    int run_command(const Command &cmd)
    {
        if (cmd.kind == Command::Seek) {
            if (!seekable || !std::isfinite(cmd.value) || cmd.value < 0)
                return MFK_SEEK_FAILED;
            {
                std::lock_guard lock(gate);
                position = clock_locked();
                seeking = true;
            }
            park_workers(true, true);
            if (interrupted()) {
                unpark_workers(true, true);
                return MFK_CANCELLED;
            }
            int res = seek_to_quiesced(cmd.value);
            if (res == MFK_OK) {
                res = prime(true);
            }
            if (res != MFK_OK) {
                // Non-fatal seek/prime failure: restore seeking flag so playback stays alive.
                std::lock_guard lock(gate);
                seeking = false;
                changed.notify_all();
            }
            unpark_workers(true, true);
            return res;
        }

        const double resume = [this] { std::lock_guard lock(gate); return clock_locked(); }();

        if (cmd.kind == Command::SubtitleTrack) {
            // Seekless subtitle switching / disabling: zero network reconnect!
            const int index = static_cast<int>(cmd.value);
            if (index < -1 || (index >= 0 && (static_cast<unsigned>(index) >= format->nb_streams ||
                format->streams[index]->codecpar->codec_type != AVMEDIA_TYPE_SUBTITLE)))
                return MFK_INVALID_REQUEST;
            AVCodecContext *replacement = index >= 0 ? open_codec(index, false) : nullptr;
            if (index >= 0 && !replacement) return MFK_DECODE_FAILED;

            Frame frame, enhancement;
            uint64_t gen = 0;
            bool is_paused_or_ended = false;
            {
                std::lock_guard lock(gate);
                subtitle_index = index;
                ++sub_serial;
                sub_pkts.clear();
                update_track_selection_locked();
                is_paused_or_ended = paused.load() || state == MFK_ENDED;
                gen = output_generation.load();
                if (is_paused_or_ended && retained) {
                    frame.reset(av_frame_clone(retained.get()));
                    if (retained_enhancement)
                        enhancement.reset(av_frame_clone(retained_enhancement.get()));
                }
            }
            update_stream_discards();
            {
                std::lock_guard lock(output_gate);
                avcodec_free_context(&subtitle);
                subtitle = replacement;
                renderer->clear_subtitles();
                if (subtitle) renderer->subtitle_header(subtitle);
                if (frame && is_paused_or_ended && target_attached && gen == output_generation.load()) {
                    renderer->present(frame.get(), enhancement.get(), aspect, resume);
                }
            }
            return MFK_OK;
        }

        if (cmd.kind == Command::AudioTrack) {
            const int index = static_cast<int>(cmd.value);
            if (index < 0 || static_cast<unsigned>(index) >= format->nb_streams ||
                format->streams[index]->codecpar->codec_type != AVMEDIA_TYPE_AUDIO)
                return MFK_INVALID_REQUEST;
            AVCodecContext *replacement = open_codec(index, false);
            if (!replacement) return MFK_DECODE_FAILED;

            park_workers(seekable, true);
            if (interrupted()) {
                avcodec_free_context(&replacement);
                unpark_workers(seekable, true);
                return MFK_CANCELLED;
            }
            std::shared_ptr<FilmAudio> retired;
            {
                std::lock_guard lock(gate);
                retired = std::move(audio);
                audio_index = index;
                ++audio_serial;
                audio_pkts.clear();
                audio_started = false;
                audio_anchor_pending = true;
                audio_handed_off = false;
                audio_eof = false;
                position = wall_base = resume;
                wall_time = Clock::now();
                if (!seekable) discard_before = resume;
                update_track_selection_locked();
            }
            retired.reset();
            avcodec_free_context(&sound);
            sound = replacement;
            update_stream_discards();

            int res = MFK_OK;
            if (seekable) {
                { std::lock_guard lock(gate); seeking = true; }
                res = seek_to_quiesced(resume);
                if (res == MFK_OK) res = prime(true);
                if (res != MFK_OK) {
                    std::lock_guard lock(gate);
                    seeking = false;
                    changed.notify_all();
                }
            }
            unpark_workers(seekable, true);
            return res;
        }

        if (cmd.kind == Command::VideoTrack) {
            const int index = static_cast<int>(cmd.value);
            if (index < 0 || static_cast<unsigned>(index) >= format->nb_streams ||
                format->streams[index]->codecpar->codec_type != AVMEDIA_TYPE_VIDEO ||
                index == dovi_input.enhancement_index())
                return MFK_INVALID_REQUEST;

            park_workers(true, seekable);
            if (interrupted()) {
                unpark_workers(true, seekable);
                return MFK_CANCELLED;
            }

            FilmDoviInput next_dovi_input;
            int res = next_dovi_input.open(format, index);
            if (res != MFK_OK) {
                unpark_workers(true, seekable);
                return res;
            }
            const int next_video_index = next_dovi_input.base_index();
            FilmDovi next_dovi;
            res = next_dovi.open(next_dovi_input.parameters(), format->streams[next_video_index]->time_base);
            if (res != MFK_OK) {
                unpark_workers(true, seekable);
                return res;
            }

            AVCodecContext *replacement = open_codec(next_video_index, true, next_dovi_input.parameters());
            bool fell_back = false;
            if (!replacement) {
                replacement = open_codec(next_video_index, false, next_dovi_input.parameters());
                fell_back = replacement != nullptr;
            }
            if (!replacement) {
                unpark_workers(true, seekable);
                return MFK_DECODE_FAILED;
            }
            avcodec_free_context(&video);
            video = replacement;
            dovi_input = std::move(next_dovi_input);
            dovi = std::move(next_dovi);
            waiting_base.clear();
            {
                std::lock_guard lock(output_gate);
                renderer->set_stream(format->streams[next_video_index]);
            }
            {
                std::lock_guard lock(gate);
                video_index = next_video_index;
                ++output_generation;
                ++video_serial;
                video_pkts.clear();
                pictures.clear();
                hardware = video->hw_device_ctx != nullptr;
                if (fell_back) {
                    hardware_fallback = true;
                    decode_fallback_reason = "d3d11va_open_failed";
                }
                update_track_selection_locked();
            }
            update_stream_discards();
            if (seekable) {
                { std::lock_guard lock(gate); seeking = true; }
                res = seek_to_quiesced(resume);
                if (res == MFK_OK) res = prime(true);
                if (res != MFK_OK) {
                    std::lock_guard lock(gate);
                    seeking = false;
                    changed.notify_all();
                }
            }
            unpark_workers(true, seekable);
            return res;
        }
        return MFK_INVALID_REQUEST;
    }

    void demux_loop()
    {
        const HRESULT apartment = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        try {
            Packet packet(av_packet_alloc());
            if (!packet) throw std::bad_alloc();
            for (;;) {
                std::shared_ptr<Command> cmd;
                {
                    std::unique_lock lock(gate);
                    changed.wait(lock, [&] {
                        if (interrupted() || error != MFK_OK) return true;
                        if (!commands.empty()) return true;
                        if (input_eof) return false;
                        const bool both_room = !video_pkts.full() && (audio_index < 0 || !audio_pkts.full());
                        const bool one_starved = video_pkts.empty() || (audio_index >= 0 && audio_pkts.empty());
                        const size_t total_bytes = video_pkts.bytes + audio_pkts.bytes;
                        constexpr size_t hard_cap = video_pkt_max_bytes + audio_pkt_max_bytes;
                        return both_room || (one_starved && total_bytes < hard_cap);
                    });
                    if (interrupted() || error != MFK_OK) break;
                    if (!commands.empty()) {
                        cmd = commands.front();
                        commands.pop_front();
                    }
                }
                if (cmd) {
                    int result;
                    try { result = run_command(*cmd); }
                    catch (...) { result = MFK_DECODE_FAILED; }
                    cmd->result.set_value(result);
                    // Non-fatal command failures (invalid request / seek failed / unsupported track)
                    // return error to the caller WITHOUT killing the healthy playback session.
                    if (result == MFK_DEVICE_LOST || result == MFK_READ_FAILED ||
                        result == MFK_RENDER_FAILED || result == MFK_AUDIO_FAILED ||
                        result == MFK_ENCRYPTION_UNSUPPORTED) {
                        if (!interrupted()) fail(result, "Fatal error during command execution");
                        break;
                    }
                    continue;
                }

                int read = av_read_frame(format, packet.get());
                if (read == AVERROR_EOF) {
                    std::lock_guard lock(gate);
                    input_eof = true;
                    video_pkts.eof = true;
                    audio_pkts.eof = true;
                    sub_pkts.eof = true;
                    changed.notify_all();
                    continue;
                }
                if (read < 0) {
                    if (!interrupted()) {
                        char errbuf[128]{};
                        av_strerror(read, errbuf, sizeof(errbuf));
                        fail(MFK_READ_FAILED, errbuf);
                    }
                    break;
                }
                size_t side_size = 0;
                if (av_packet_get_side_data(packet.get(), AV_PKT_DATA_ENCRYPTION_INFO, &side_size)) {
                    av_packet_unref(packet.get());
                    fail(MFK_ENCRYPTION_UNSUPPORTED, "Unencrypted/missing key for encrypted packet");
                    break;
                }

                const int sidx = packet->stream_index;
                const size_t cost = packet_cost(packet.get());
                std::lock_guard lock(gate);
                if (sidx == video_index || sidx == dovi_input.enhancement_index()) {
                    Packet owned(av_packet_clone(packet.get()));
                    if (owned) video_pkts.push(std::move(owned), cost, video_serial);
                    changed.notify_all();
                } else if (sidx == audio_index && sound) {
                    Packet owned(av_packet_clone(packet.get()));
                    if (owned) audio_pkts.push(std::move(owned), cost, audio_serial);
                    changed.notify_all();
                } else if (sidx == subtitle_index && subtitle) {
                    if (!sub_pkts.full() && cost <= sub_pkt_max_bytes) {
                        Packet owned(av_packet_clone(packet.get()));
                        if (owned) sub_pkts.push(std::move(owned), cost, sub_serial);
                    }
                }
                av_packet_unref(packet.get());
            }
        } catch (...) {
            fail(MFK_DECODE_FAILED, "Unexpected exception in demux loop");
        }
        {
            std::lock_guard lock(gate);
            demux_alive = false;
            for (auto &c : commands) c->result.set_value(MFK_CANCELLED);
            commands.clear();
            changed.notify_all();
        }
        if (SUCCEEDED(apartment)) CoUninitialize();
    }

    void video_loop()
    {
        const HRESULT apartment = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        try {
            for (;;) {
                QueuedPacket qp;
                bool do_drain = false;
                {
                    std::unique_lock lock(gate);
                    for (;;) {
                        if (interrupted() || error != MFK_OK) break;
                        if (quiesce_video) {
                            video_quiesced = true;
                            changed.notify_all();
                            changed.wait(lock, [&] { return interrupted() || error != MFK_OK || !quiesce_video; });
                            video_quiesced = false;
                            continue;
                        }
                        if (!video_pkts.empty()) {
                            qp = video_pkts.pop();
                            changed.notify_all();
                            break;
                        }
                        if (video_pkts.eof && !video_eof) {
                            do_drain = true;
                            break;
                        }
                        changed.wait(lock);
                    }
                    if (interrupted() || error != MFK_OK) break;
                }
                if (do_drain) {
                    int res = drain_video(false);
                    if (res != MFK_OK && res != MFK_CANCELLED) {
                        fail(res, "Video decoder drain failed");
                        break;
                    }
                    std::lock_guard lock(gate);
                    if (!quiesce_video) {
                        video_eof = true;
                        changed.notify_all();
                    }
                    continue;
                }
                {
                    std::lock_guard lock(gate);
                    if (qp.serial != video_serial) continue;
                    // Skip non-reference B-frames when high-speed decode falls behind master clock (e.g. 60fps @ 2x).
                    if (!dolby_vision && dovi_input.enhancement_index() < 0 && !seeking && !paused.load() && rate.load() > 1.05) {
                        const double lag = clock_locked() - next_video_pts;
                        if (lag > 0.15 && video->skip_frame != AVDISCARD_NONREF) {
                            video->skip_frame = AVDISCARD_NONREF;
                        } else if (lag < 0.05 && video->skip_frame != AVDISCARD_DEFAULT) {
                            video->skip_frame = AVDISCARD_DEFAULT;
                        }
                    } else if (video->skip_frame != AVDISCARD_DEFAULT) {
                        video->skip_frame = AVDISCARD_DEFAULT;
                    }
                }
                int res = process_video_packet(qp.packet.get(), false);
                if (res != MFK_OK && res != MFK_CANCELLED) {
                    fail(res, "Video decode failed");
                    break;
                }
            }
        } catch (...) {
            fail(MFK_DECODE_FAILED, "Unexpected exception in video decode loop");
        }
        {
            std::lock_guard lock(gate);
            video_alive = false;
            video_quiesced = true;
            changed.notify_all();
        }
        if (SUCCEEDED(apartment)) CoUninitialize();
    }

    void audio_loop()
    {
        const HRESULT apartment = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        try {
            for (;;) {
                QueuedPacket qp;
                bool do_drain = false;
                {
                    std::unique_lock lock(gate);
                    for (;;) {
                        if (interrupted() || error != MFK_OK) break;
                        if (quiesce_audio) {
                            audio_quiesced = true;
                            changed.notify_all();
                            changed.wait(lock, [&] { return interrupted() || error != MFK_OK || !quiesce_audio; });
                            audio_quiesced = false;
                            continue;
                        }
                        if (sound && !audio_pkts.empty()) {
                            qp = audio_pkts.pop();
                            changed.notify_all();
                            break;
                        }
                        if (audio_pkts.eof && !audio_eof) {
                            do_drain = true;
                            break;
                        }
                        changed.wait(lock);
                    }
                    if (interrupted() || error != MFK_OK) break;
                }
                if (do_drain) {
                    if (sound) {
                        int sent = avcodec_send_packet(sound, nullptr);
                        if (sent == AVERROR(EAGAIN)) {
                            int rr = receive_audio_frames();
                            if (rr != MFK_OK && rr != MFK_CANCELLED) { fail(rr, "Audio drain failed"); break; }
                            bool quiesced = false;
                            { std::lock_guard lock(gate); quiesced = quiesce_audio; }
                            if (!quiesced) sent = avcodec_send_packet(sound, nullptr);
                        }
                        if (sent >= 0 || sent == AVERROR_EOF) {
                            int rr = receive_audio_frames();
                            if (rr != MFK_OK && rr != MFK_CANCELLED) { fail(rr, "Audio drain failed"); break; }
                        }
                    }
                    std::shared_ptr<FilmAudio> a;
                    {
                        std::lock_guard lock(gate);
                        if (!quiesce_audio) {
                            audio_eof = true;
                            a = audio;
                            changed.notify_all();
                        }
                    }
                    if (a) a->finish();
                    continue;
                }
                {
                    std::lock_guard lock(gate);
                    if (qp.serial != audio_serial || !sound) continue;
                }
                int sent = avcodec_send_packet(sound, qp.packet.get());
                if (sent == AVERROR(EAGAIN)) {
                    int rr = receive_audio_frames();
                    if (rr == MFK_CANCELLED) continue; // Quiesced/interrupted; never double-call receive!
                    if (rr != MFK_OK) { fail(rr, "Audio decode failed"); break; }
                    sent = avcodec_send_packet(sound, qp.packet.get());
                }
                if (sent < 0 && sent != AVERROR_INVALIDDATA && sent != AVERROR_EOF) {
                    fail(MFK_DECODE_FAILED, "Audio send_packet failed");
                    break;
                }
                int rr = receive_audio_frames();
                if (rr != MFK_OK && rr != MFK_CANCELLED) {
                    fail(rr, "Audio decode/write failed");
                    break;
                }
            }
        } catch (...) {
            fail(MFK_AUDIO_FAILED, "Unexpected exception in audio decode loop");
        }
        {
            std::lock_guard lock(gate);
            audio_alive = false;
            audio_quiesced = true;
            changed.notify_all();
        }
        if (SUCCEEDED(apartment)) CoUninitialize();
    }

    void present_loop()
    {
        const HRESULT apartment = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        auto last_present_tp = Clock::now();
        bool have_present_tp = false;
        try {
            for (;;) {
                Picture picture;
                uint64_t generation = 0;
                std::shared_ptr<FilmAudio> audio_to_resume;
                std::shared_ptr<FilmAudio> audio_to_pause_buffering;
                double present_sync_diff = 0.0;
                {
                    std::unique_lock lock(gate);
                    if (interrupted() || error != MFK_OK) break;

                    if (audio) {
                        const auto asnap = audio->snapshot();
                        if (asnap.error != MFK_OK && asnap.error != MFK_CANCELLED) {
                            const int code = asnap.error;
                            lock.unlock();
                            fail(code, "Audio subsystem failed");
                            break;
                        }
                        // Seamless handoff to monotonic wall clock if audio ends before video.
                        if (audio_started && !audio_handed_off && asnap.drained) {
                            wall_base = asnap.position;
                            wall_time = Clock::now();
                            audio_handed_off = true;
                        }
                    }

                    // Idle wait (0% CPU) when paused or ended.
                    if (paused.load() || state == MFK_ENDED) {
                        changed.wait(lock, [&] {
                            return interrupted() || error != MFK_OK ||
                                   (!paused.load() && state != MFK_ENDED);
                        });
                        continue;
                    }

                    if (seeking) {
                        changed.wait(lock, [&] {
                            return interrupted() || error != MFK_OK || !seeking || paused.load();
                        });
                        continue;
                    }

                    // Leave BUFFERING as soon as video frames are available (or video reached EOF).
                    if (state == MFK_BUFFERING && (!pictures.empty() || video_eof)) {
                        wall_base = position;
                        wall_time = Clock::now();
                        state = MFK_PLAYING;
                        if (audio_buffering_paused && audio) {
                            audio_buffering_paused = false;
                            audio_to_resume = audio;
                        }
                        changed.notify_all();
                    }

                    if ((!pictures.empty() || video_eof) && audio && !audio_started &&
                        !audio_anchor_pending && !audio_buffering_paused &&
                        clock_locked() + 0.002 >= audio_start_pts) {
                        audio_started = true;
                        audio_handed_off = false;
                        audio_to_resume = audio;
                    }

                    if (audio_to_resume) {
                        lock.unlock();
                        int pr = sync_audio_pause(audio_to_resume);
                        if (pr != MFK_OK && pr != MFK_CANCELLED) {
                            fail(pr, "Failed to start audio playback");
                            break;
                        }
                        continue;
                    }

                    if (pictures.empty()) {
                        if (video_eof) {
                            const bool audio_done = !audio || !sound || (audio_eof && audio->drained());
                            const double now = clock_locked();
                            if (audio_done && now + 0.002 >= last_picture_end) {
                                position = std::max(now, last_picture_end);
                                state = MFK_ENDED;
                                paused.store(true);
                                changed.notify_all();
                                continue;
                            }
                            const double rem = std::max(0.002, (last_picture_end - now) / std::max(0.25, rate.load()));
                            changed.wait_for(lock, std::chrono::duration<double>(std::min(0.01, rem)));
                            continue;
                        }
                        // Entering BUFFERING: freeze wall clock and pause audio outside gate.
                        if (state != MFK_BUFFERING) {
                            position = clock_locked();
                            wall_base = position;
                            wall_time = Clock::now();
                            state = MFK_BUFFERING;
                            if (audio && audio_started && !audio_handed_off && !audio_buffering_paused) {
                                audio_buffering_paused = true;
                                audio_to_pause_buffering = audio;
                            }
                            changed.notify_all();
                        }
                        if (audio_to_pause_buffering) {
                            lock.unlock();
                            sync_audio_pause(audio_to_pause_buffering);
                            continue;
                        }
                        changed.wait(lock, [&] {
                            return interrupted() || error != MFK_OK || paused.load() ||
                                   seeking || !pictures.empty() || video_eof;
                        });
                        continue;
                    }

                    const double now = clock_locked();
                    const double delay = pictures.front().pts - now;
                    if (delay > 0.002) {
                        const double wait_sec = std::min(0.008, delay / std::max(0.25, rate.load()));
                        changed.wait_for(lock, std::chrono::duration<double>(wait_sec));
                        continue;
                    }
                    picture = std::move(pictures.front());
                    pictures.pop_front();
                    generation = picture.generation;
                    if (generation != output_generation.load()) {
                        changed.notify_all();
                        continue;
                    }
                    if ((!pictures.empty() && delay < -0.05) || (!video_pkts.empty() && delay < -0.12)) {
                        ++dropped_frames;
                        changed.notify_all();
                        continue;
                    }
                    present_sync_diff = delay;
                    position = std::max(0.0, now);
                    last_picture_end = std::max(last_picture_end, picture.pts + picture.duration);
                    state = MFK_PLAYING;
                    changed.notify_all();
                }

                int result = MFK_OK;
                int nhdr = 0, tm = 0, dhdr = 0, dv = 0, dw = 0, dh = 0;
                {
                    std::lock_guard lock(output_gate);
                    if (generation != output_generation.load()) continue;
                    drain_subtitles_locked_output(picture.pts);
                    result = target_attached ?
                        renderer->present(picture.base.get(), picture.enhancement.get(), aspect, picture.pts) : MFK_OK;
                    nhdr = renderer->native_hdr();
                    tm = renderer->tone_mapped();
                    dhdr = renderer->display_hdr_enabled();
                    dv = renderer->dolby_vision();
                    dw = renderer->display_width();
                    dh = renderer->display_height();
                }
                if (result != MFK_OK) {
                    fail(result, "Video frame present failed");
                    break;
                }
                const auto now_tp = Clock::now();
                if (have_present_tp) {
                    const double dt = std::chrono::duration<double>(now_tp - last_present_tp).count();
                    if (dt > 0.0005 && dt < 2.0) {
                        const double inst_fps = 1.0 / dt;
                        const double prev = render_fps.load();
                        render_fps.store(prev > 0 ? (prev * 0.85 + inst_fps * 0.15) : inst_fps);
                    }
                }
                last_present_tp = now_tp;
                have_present_tp = true;
                av_sync_diff.store(present_sync_diff);

                {
                    std::lock_guard lock(gate);
                    if (generation != output_generation.load()) continue;
                    retained = std::move(picture.base);
                    retained_enhancement = std::move(picture.enhancement);
                    native_hdr = nhdr != 0;
                    tone_mapped = tm != 0;
                    display_hdr_enabled = dhdr != 0;
                    dolby_vision = dv != 0;
                    if (dw > 0 && dh > 0) { width = dw; height = dh; }
                }
            }
        } catch (...) {
            fail(MFK_RENDER_FAILED, "Unexpected exception in present loop");
        }
        if (SUCCEEDED(apartment)) CoUninitialize();
    }
};

std::once_flag logging_once;
void no_log(void *, int, const char *, va_list) { }

int prepare(Owner &owner, double position, void *display_window)
{
    if (!std::isfinite(position) || position < 0) return MFK_INVALID_REQUEST;
    {
        std::lock_guard lock(owner.gate);
        if (owner.preparing || owner.prepared || owner.committed) return MFK_INVALID_REQUEST;
        if (owner.interrupted()) return MFK_CANCELLED;
        owner.preparing = true;
    }

    auto renderer = std::make_unique<FilmRenderer>();
    int result = renderer->open(display_window,
        [&owner](int lvl, const char *comp, const char *msg) { owner.log(lvl, comp, msg); });
    if (result != MFK_OK) return owner.interrupted() ? MFK_CANCELLED : result;
    {
        std::lock_guard out_lock(owner.output_gate);
        std::lock_guard lock(owner.gate);
        owner.renderer = std::move(renderer);
        owner.display_hdr_enabled = owner.renderer->display_hdr_enabled() != 0;
    }

    owner.format = avformat_alloc_context();
    if (!owner.format) return MFK_OPEN_FAILED;
    auto *format = owner.format;
    format->opaque = &owner.input;
    format->io_open = mfk_io_open;
    format->io_close2 = mfk_io_close;
    format->interrupt_callback = {mfk_interrupted, &owner.input};
    format->flags |= AVFMT_FLAG_CUSTOM_IO;
    int io_res = mfk_io_open(format, &owner.root, owner.input.uri, AVIO_FLAG_READ, nullptr);
    if (io_res < 0) return owner.interrupted() ? MFK_CANCELLED : MFK_OPEN_FAILED;
    format->pb = owner.root;

    uint8_t *location = nullptr;
    av_opt_get(owner.root, "location", AV_OPT_SEARCH_CHILDREN, &location);
    AVDictionary *options = nullptr;
    result = mfk_key_options(&owner.input, &options);
    av_dict_set(&options, "http_persistent", "0", 0);
    const AVInputFormat *forced = owner.input.format == MFK_HLS ? av_find_input_format("hls") :
        owner.input.format == MFK_DASH ? av_find_input_format("dash") : nullptr;
    if (result >= 0) {
        result = avformat_open_input(&owner.format,
            location ? reinterpret_cast<char *>(location) : owner.input.uri, forced, &options);
    }
    mfk_clear_dictionary(&options);
    if (location) {
        SecureZeroMemory(location, strlen(reinterpret_cast<char *>(location)));
        av_free(location);
    }
    if (owner.interrupted()) return MFK_CANCELLED;
    if (result < 0) {
        char errbuf[128]{};
        av_strerror(result, errbuf, sizeof(errbuf));
        owner.record_error_text(errbuf);
        return MFK_OPEN_FAILED;
    }
    int info_res = avformat_find_stream_info(owner.format, nullptr);
    if (owner.interrupted()) return MFK_CANCELLED;
    if (info_res < 0) {
        char errbuf[128]{};
        av_strerror(info_res, errbuf, sizeof(errbuf));
        owner.record_error_text(errbuf);
        return MFK_OPEN_FAILED;
    }

    format = owner.format;
    owner.start = format->start_time == AV_NOPTS_VALUE ? 0 :
        static_cast<double>(format->start_time) / AV_TIME_BASE;
    const double discovered_duration = format->duration > 0 ?
        static_cast<double>(format->duration) / AV_TIME_BASE : 0;

    int best_video = av_find_best_stream(format, AVMEDIA_TYPE_VIDEO, -1, -1, nullptr, 0);
    if (best_video < 0) return MFK_FORMAT_UNSUPPORTED;
    result = owner.dovi_input.open(format, best_video);
    if (result != MFK_OK) return result;
    const int discovered_video = owner.dovi_input.base_index();

    int best_audio = av_find_best_stream(format, AVMEDIA_TYPE_AUDIO, -1, discovered_video, nullptr, 0);
    if (best_audio < 0) {
        for (unsigned i = 0; i < format->nb_streams; i++)
            if (format->streams[i]->codecpar->codec_type == AVMEDIA_TYPE_AUDIO)
                return MFK_FORMAT_UNSUPPORTED;
        best_audio = -1;
    }
    if (discovered_video < 0) return MFK_FORMAT_UNSUPPORTED;
    {
        std::lock_guard lock(owner.gate);
        owner.duration = discovered_duration;
        owner.video_index = discovered_video;
        owner.audio_index = best_audio;
        owner.subtitle_index = -1;
    }

    owner.update_stream_discards();

    {
        std::lock_guard out_lock(owner.output_gate);
        owner.renderer->set_stream(format->streams[owner.video_index]);
        for (unsigned i = 0; i < format->nb_streams; i++) {
            const auto *stream = format->streams[i];
            if (stream->codecpar->codec_type != AVMEDIA_TYPE_ATTACHMENT) continue;
            const auto *name = av_dict_get(stream->metadata, "filename", nullptr, 0);
            const auto *mime = av_dict_get(stream->metadata, "mimetype", nullptr, 0);
            owner.renderer->add_attachment(name ? name->value : nullptr,
                mime ? mime->value : nullptr, stream->codecpar->extradata,
                stream->codecpar->extradata_size > 0 ? static_cast<size_t>(stream->codecpar->extradata_size) : 0);
        }
        owner.renderer->finish_attachments();
    }

    owner.video = owner.open_codec(owner.video_index, true);
    bool hw_fell_back = false;
    if (!owner.video) {
        owner.video = owner.open_codec(owner.video_index, false);
        hw_fell_back = owner.video != nullptr;
    }
    if (!owner.video) return MFK_DECODE_FAILED;

    if (owner.audio_index >= 0) {
        owner.sound = owner.open_codec(owner.audio_index, false);
        if (!owner.sound) return MFK_DECODE_FAILED;
        auto created = std::make_shared<FilmAudio>(
            owner.cancelled,
            [&owner] { return owner.interrupted(); },
            [&owner](int lvl, const char *comp, const char *msg) { owner.log(lvl, comp, msg); });
        result = created->open(owner.sound, owner.rate.load());
        if (result != MFK_OK) return result;
        created->set_volume(owner.volume.load());
        created->set_muted(owner.muted.load());
        std::lock_guard lock(owner.gate);
        owner.audio = std::move(created);
    }

    result = owner.dovi.open(owner.dovi_input.parameters(),
                             format->streams[owner.video_index]->time_base);
    if (result != MFK_OK) return result;

    const bool is_seekable = owner.duration > 0 && !(format->ctx_flags & AVFMTCTX_UNSEEKABLE) &&
        ((owner.root->seekable & AVIO_SEEKABLE_NORMAL) ||
         !strcmp(format->iformat->name, "hls") || !strcmp(format->iformat->name, "dash"));

    std::vector<mfk_track> discovered_tracks;
    for (unsigned i = 0; i < format->nb_streams; i++) {
        if (static_cast<int>(i) == owner.dovi_input.enhancement_index()) continue;
        const auto *stream = format->streams[i];
        int kind = stream->codecpar->codec_type == AVMEDIA_TYPE_VIDEO ? MFK_VIDEO :
            stream->codecpar->codec_type == AVMEDIA_TYPE_AUDIO ? MFK_AUDIO :
            stream->codecpar->codec_type == AVMEDIA_TYPE_SUBTITLE ? MFK_SUBTITLE : 0;
        if (!kind || !avcodec_find_decoder(stream->codecpar->codec_id)) continue;
        mfk_track track{};
        track.size = sizeof(track);
        track.index = static_cast<int>(i);
        track.kind = kind;
        track.selected = static_cast<int>(i) == owner.video_index || static_cast<int>(i) == owner.audio_index;
        copy_utf8(track.codec, sizeof(track.codec), avcodec_get_name(stream->codecpar->codec_id));
        const auto *language = av_dict_get(stream->metadata, "language", nullptr, 0);
        const auto *title = av_dict_get(stream->metadata, "title", nullptr, 0);
        if (language) copy_utf8(track.language, sizeof(track.language), language->value);
        if (title) copy_utf8(track.title, sizeof(track.title), title->value);
        discovered_tracks.push_back(track);
    }

    std::vector<mfk_chapter> discovered_chapters;
    for (unsigned i = 0; i < format->nb_chapters; i++) {
        const auto *ch = format->chapters[i];
        if (!ch || ch->time_base.num <= 0 || ch->time_base.den <= 0 || ch->start == AV_NOPTS_VALUE)
            continue;
        mfk_chapter item{};
        item.size = sizeof(item);
        item.index = static_cast<int>(i);
        item.start = std::max(0.0, ch->start * av_q2d(ch->time_base) - owner.start);
        item.end = ch->end == AV_NOPTS_VALUE ? item.start :
            std::max(item.start, ch->end * av_q2d(ch->time_base) - owner.start);
        const auto *title = av_dict_get(ch->metadata, "title", nullptr, 0);
        if (title) copy_utf8(item.title, sizeof(item.title), title->value);
        discovered_chapters.push_back(item);
    }

    {
        std::lock_guard lock(owner.gate);
        owner.hardware = owner.video->hw_device_ctx != nullptr;
        if (hw_fell_back) {
            owner.hardware_fallback = true;
            owner.decode_fallback_reason = "d3d11va_open_failed";
        }
        owner.width = owner.video->width;
        owner.height = owner.video->height;
        owner.seekable = is_seekable;
        owner.tracks = std::move(discovered_tracks);
        owner.chapters = std::move(discovered_chapters);
    }
    if (hw_fell_back)
        owner.log(MFK_LOG_WARN, "video-decode", "D3D11VA unavailable for codec; using software decoder");

    if (position > 0 && is_seekable) {
        result = owner.seek_to_quiesced(position);
        if (result != MFK_OK) return result;
    } else {
        owner.discard_before = position;
    }
    result = owner.prime(false);
    if (result == MFK_OK) {
        std::lock_guard lock(owner.gate);
        owner.prepared = true;
        owner.preparing = false;
        owner.state = MFK_PREPARED;
    } else {
        std::lock_guard lock(owner.gate);
        owner.preparing = false;
    }
    return result;
}

int dispatch_command(Owner &owner, Command::Kind kind, double value)
{
    if (!std::isfinite(value)) return MFK_INVALID_REQUEST;
    std::lock_guard cmd_lock(owner.command_gate);
    auto requested = std::make_shared<Command>();
    requested->kind = kind;
    requested->value = value;
    auto done = requested->result.get_future();
    {
        std::lock_guard lock(owner.gate);
        if (owner.interrupted() || owner.error != MFK_OK || !owner.committed || !owner.demux_alive)
            return MFK_CANCELLED;
        if (kind == Command::Seek) {
            if (!owner.seekable || value < 0) return MFK_SEEK_FAILED;
        } else if (kind == Command::VideoTrack || kind == Command::AudioTrack || kind == Command::SubtitleTrack) {
            const int index = static_cast<int>(value);
            const auto type = kind == Command::VideoTrack ? AVMEDIA_TYPE_VIDEO :
                              kind == Command::AudioTrack ? AVMEDIA_TYPE_AUDIO : AVMEDIA_TYPE_SUBTITLE;
            if (value != index || ((index != -1 || type != AVMEDIA_TYPE_SUBTITLE) &&
                (index < 0 || static_cast<unsigned>(index) >= owner.format->nb_streams ||
                 owner.format->streams[index]->codecpar->codec_type != type)))
                return MFK_INVALID_REQUEST;
            const int current = kind == Command::VideoTrack ? owner.video_index :
                                kind == Command::AudioTrack ? owner.audio_index : owner.subtitle_index;
            if (owner.commands.empty() && index == current) return MFK_OK;
        }
        owner.commands.push_back(requested);
        owner.changed.notify_all();
    }
    try { return done.get(); }
    catch (...) { return MFK_CANCELLED; }
}
}

const char *MFK_CALL mfk_build_identity() { return MFK_BUILD_IDENTITY; }

int MFK_CALL mfk_create(const mfk_callbacks *callbacks, const char *uri, int format,
    const mfk_clear_key *keys, uint32_t count, const uint8_t *default_key, void **result)
{
    if (!result || !callbacks || callbacks->size != sizeof(*callbacks) || callbacks->abi != MFK_ABI ||
        !callbacks->open || !callbacks->location || !callbacks->read || !callbacks->seek ||
        !callbacks->close || !callbacks->interrupted || !callbacks->failed ||
        !uri || format < MFK_AUTO || format > MFK_DASH || (count && !keys))
        return MFK_INVALID_REQUEST;
    *result = nullptr;
    try {
        std::call_once(logging_once, [] { av_log_set_callback(no_log); });
        auto owner = std::make_unique<Owner>();
        owner->input.callbacks = *callbacks;
        owner->input.format = format;
        owner->input.uri = _strdup(uri);
        if (!owner->input.uri) return MFK_OPEN_FAILED;
        if (count) {
            owner->input.keys = static_cast<mfk_clear_key *>(malloc(static_cast<size_t>(count) * sizeof(*keys)));
            if (!owner->input.keys) return MFK_OPEN_FAILED;
            memcpy(owner->input.keys, keys, static_cast<size_t>(count) * sizeof(*keys));
            owner->input.key_count = count;
        }
        if (default_key) {
            memcpy(owner->input.default_key, default_key, 16);
            owner->input.has_default = 1;
        }
        *result = owner.release();
        return MFK_OK;
    } catch (...) {
        return MFK_OPEN_FAILED;
    }
}

int MFK_CALL mfk_prepare(void *opaque, double position, void *display_window)
{
    if (!opaque) return MFK_INVALID_REQUEST;
    auto &owner = *static_cast<Owner *>(opaque);
    const HRESULT apartment = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    int result;
    try { result = prepare(owner, position, display_window); }
    catch (...) {
        { std::lock_guard lock(owner.gate); owner.preparing = false; }
        result = owner.interrupted() ? MFK_CANCELLED : MFK_OPEN_FAILED;
    }
    if (SUCCEEDED(apartment)) CoUninitialize();
    if (result != MFK_OK && result != MFK_CANCELLED && result != MFK_INVALID_REQUEST)
        owner.fail(result);
    return result;
}

int MFK_CALL mfk_attach(void *opaque, const mfk_surface *surface, void **swapchain)
{
    if (!swapchain) return MFK_INVALID_REQUEST;
    *swapchain = nullptr;
    if (!opaque) return MFK_INVALID_REQUEST;
    auto &owner = *static_cast<Owner *>(opaque);
    try {
        std::lock_guard lock(owner.output_gate);
        if (!owner.renderer) return MFK_RENDER_FAILED;
        int result = owner.renderer->attach(surface, swapchain);
        if (!surface || result == MFK_OK) {
            owner.target_attached = surface != nullptr;
            std::lock_guard glock(owner.gate);
            owner.native_hdr = owner.renderer->native_hdr() != 0;
            owner.tone_mapped = owner.renderer->tone_mapped() != 0;
            owner.display_hdr_enabled = owner.renderer->display_hdr_enabled() != 0;
        }
        return result;
    } catch (...) {
        return MFK_RENDER_FAILED;
    }
}

int MFK_CALL mfk_surface_update(void *opaque, const mfk_surface *surface)
{
    if (!opaque || !surface) return MFK_INVALID_REQUEST;
    auto &owner = *static_cast<Owner *>(opaque);
    try {
        Frame frame, enhancement;
        double position = 0;
        uint64_t generation = 0;
        bool repaint_retained = false;
        {
            std::lock_guard lock(owner.gate);
            repaint_retained = (owner.paused.load() || owner.state == MFK_ENDED) && owner.retained;
            if (repaint_retained) frame.reset(av_frame_clone(owner.retained.get()));
            if (frame && owner.retained_enhancement)
                enhancement.reset(av_frame_clone(owner.retained_enhancement.get()));
            if ((repaint_retained && !frame) ||
                (frame && owner.retained_enhancement && !enhancement))
                return MFK_DECODE_FAILED;
            position = owner.position;
            generation = owner.output_generation.load();
        }
        std::lock_guard lock(owner.output_gate);
        if (!owner.renderer || !owner.target_attached) return MFK_RENDER_FAILED;
        int result = owner.renderer->surface_update(*surface);
        if (result == MFK_OK && frame && repaint_retained &&
            generation == owner.output_generation.load()) {
            result = owner.renderer->present(frame.get(), enhancement.get(), owner.aspect, position);
        }
        {
            std::lock_guard glock(owner.gate);
            owner.native_hdr = owner.renderer->native_hdr() != 0;
            owner.tone_mapped = owner.renderer->tone_mapped() != 0;
            owner.display_hdr_enabled = owner.renderer->display_hdr_enabled() != 0;
        }
        return result;
    } catch (...) {
        return MFK_RENDER_FAILED;
    }
}

int MFK_CALL mfk_start(void *opaque, int paused)
{
    if (!opaque) return MFK_INVALID_REQUEST;
    auto &owner = *static_cast<Owner *>(opaque);
    try {
        std::lock_guard cmd_lock(owner.command_gate);
        Picture first;
        {
            std::lock_guard lock(owner.gate);
            if (!owner.prepared || owner.committed || owner.interrupted()) return MFK_INVALID_REQUEST;
            if (owner.pictures.empty()) return MFK_OPEN_FAILED;
            const auto &front = owner.pictures.front();
            first.base.reset(av_frame_clone(front.base.get()));
            first.enhancement.reset(front.enhancement ? av_frame_clone(front.enhancement.get()) : nullptr);
            if (!first.base || (front.enhancement && !first.enhancement)) return MFK_DECODE_FAILED;
            first.pts = front.pts;
            first.duration = front.duration;
            first.generation = front.generation;
        }
        int nhdr = 0, tm = 0, dhdr = 0, dv = 0, dw = 0, dh = 0;
        {
            std::lock_guard output_lock(owner.output_gate);
            if (!owner.target_attached || !owner.renderer) return MFK_RENDER_FAILED;
            owner.drain_subtitles_locked_output(first.pts);
            const auto submitted = owner.renderer->submitted_pictures();
            int result = owner.renderer->present(first.base.get(), first.enhancement.get(),
                                                 owner.aspect, first.pts);
            if (result != MFK_OK) return result;
            if (owner.renderer->submitted_pictures() == submitted) return MFK_RENDER_FAILED;
            nhdr = owner.renderer->native_hdr();
            tm = owner.renderer->tone_mapped();
            dhdr = owner.renderer->display_hdr_enabled();
            dv = owner.renderer->dolby_vision();
            dw = owner.renderer->display_width();
            dh = owner.renderer->display_height();
        }
        {
            std::lock_guard lock(owner.gate);
            if (owner.interrupted()) return MFK_CANCELLED;
            owner.retained = std::move(first.base);
            owner.retained_enhancement = std::move(first.enhancement);
            // Pop first frame now that it has been rendered so present_loop won't duplicate it.
            if (!owner.pictures.empty()) owner.pictures.pop_front();
            owner.last_picture_end = first.pts + first.duration;
            owner.native_hdr = nhdr != 0;
            owner.tone_mapped = tm != 0;
            owner.display_hdr_enabled = dhdr != 0;
            owner.dolby_vision = dv != 0;
            if (dw > 0 && dh > 0) { owner.width = dw; owner.height = dh; }
            owner.paused.store(paused != 0);
            owner.state = owner.paused.load() ? MFK_PAUSED : MFK_PLAYING;
            owner.wall_base = owner.position;
            owner.wall_time = Clock::now();
            owner.demux_alive = true;
            owner.video_alive = true;
            owner.audio_alive = true;
        }
        try {
            owner.demux_thread = std::thread([&owner] { owner.demux_loop(); });
            owner.video_thread = std::thread([&owner] { owner.video_loop(); });
            owner.audio_thread = std::thread([&owner] { owner.audio_loop(); });
            owner.present_thread = std::thread([&owner] { owner.present_loop(); });
        } catch (...) {
            owner.cancel();
            if (owner.demux_thread.joinable()) owner.demux_thread.join();
            if (owner.video_thread.joinable()) owner.video_thread.join();
            if (owner.audio_thread.joinable()) owner.audio_thread.join();
            if (owner.present_thread.joinable()) owner.present_thread.join();
            return MFK_OPEN_FAILED;
        }
        {
            std::lock_guard lock(owner.gate);
            owner.committed = true;
            owner.changed.notify_all();
        }
        return MFK_OK;
    } catch (...) {
        return MFK_OPEN_FAILED;
    }
}

int MFK_CALL mfk_pause(void *opaque, int paused)
{
    if (!opaque) return MFK_INVALID_REQUEST;
    auto &owner = *static_cast<Owner *>(opaque);
    try {
        std::shared_ptr<FilmAudio> a;
        {
            std::lock_guard lock(owner.gate);
            if (owner.interrupted()) return MFK_CANCELLED;
            if (!owner.committed || owner.state == MFK_FAILED || owner.state == MFK_ENDED)
                return MFK_INVALID_REQUEST;
            owner.position = owner.clock_locked();
            owner.paused.store(paused != 0);
            owner.wall_base = owner.position;
            owner.wall_time = Clock::now();
            if (paused != 0) {
                owner.state = MFK_PAUSED;
                owner.audio_buffering_paused = false;
            } else {
                const bool buffering = owner.pictures.empty() && !owner.video_eof;
                owner.state = buffering ? MFK_BUFFERING : MFK_PLAYING;
                owner.audio_buffering_paused = buffering && owner.audio && owner.audio_started && !owner.audio_handed_off;
            }
            a = owner.audio;
            owner.changed.notify_all();
        }
        // Serialize pause sync outside owner.gate using the latest desired state.
        if (a) {
            int result = owner.sync_audio_pause(a);
            if (result != MFK_OK && result != MFK_CANCELLED) return result;
        }
        return MFK_OK;
    } catch (...) {
        return MFK_AUDIO_FAILED;
    }
}

int MFK_CALL mfk_seek(void *opaque, double position)
{
    if (!opaque) return MFK_INVALID_REQUEST;
    auto &owner = *static_cast<Owner *>(opaque);
    try {
        bool is_committed = false;
        {
            std::lock_guard lock(owner.gate);
            if (owner.interrupted()) return MFK_CANCELLED;
            if (!owner.prepared) return MFK_INVALID_REQUEST;
            is_committed = owner.committed;
        }
        if (!is_committed) {
            std::lock_guard cmd_lock(owner.command_gate);
            {
                std::lock_guard lock(owner.gate);
                if (owner.committed) is_committed = true;
            }
            if (!is_committed) {
                int result = owner.seek_to_quiesced(position);
                if (result != MFK_OK) return result;
                return owner.prime(false);
            }
        }
        return dispatch_command(owner, Command::Seek, position);
    } catch (...) {
        return MFK_SEEK_FAILED;
    }
}

int MFK_CALL mfk_rate(void *opaque, double rate)
{
    if (!opaque || !std::isfinite(rate) || rate < 0.25 || rate > 4.0)
        return MFK_INVALID_REQUEST;
    auto &owner = *static_cast<Owner *>(opaque);
    try {
        std::lock_guard cmd_lock(owner.command_gate);
        std::shared_ptr<FilmAudio> a;
        {
            std::lock_guard lock(owner.gate);
            if (owner.interrupted() || owner.error != MFK_OK) return MFK_CANCELLED;
            if (!owner.prepared) return MFK_INVALID_REQUEST;
            if (owner.rate.load() == rate) return MFK_OK;
            owner.position = owner.clock_locked();
            owner.wall_base = owner.position;
            owner.wall_time = Clock::now();
            owner.rate.store(rate);
            a = owner.audio;
            owner.changed.notify_all();
        }
        // Seekless speed change: update audio tempo in-place without reconnecting HTTP.
        if (a) {
            int res = a->set_rate(rate);
            if (res != MFK_OK && res != MFK_CANCELLED) return res;
        }
        return MFK_OK;
    } catch (...) {
        return MFK_AUDIO_FAILED;
    }
}

int MFK_CALL mfk_volume(void *opaque, double value)
{
    if (!opaque || !std::isfinite(value) || value < 0.0 || value > 1.0)
        return MFK_INVALID_REQUEST;
    auto &owner = *static_cast<Owner *>(opaque);
    try {
        std::shared_ptr<FilmAudio> a;
        {
            std::lock_guard lock(owner.gate);
            owner.volume.store(value);
            a = owner.audio;
        }
        if (a) a->set_volume(value);
        return MFK_OK;
    } catch (...) {
        return MFK_AUDIO_FAILED;
    }
}

int MFK_CALL mfk_mute(void *opaque, int muted)
{
    if (!opaque) return MFK_INVALID_REQUEST;
    auto &owner = *static_cast<Owner *>(opaque);
    try {
        std::shared_ptr<FilmAudio> a;
        {
            std::lock_guard lock(owner.gate);
            owner.muted.store(muted != 0);
            a = owner.audio;
        }
        if (a) a->set_muted(muted != 0);
        return MFK_OK;
    } catch (...) {
        return MFK_AUDIO_FAILED;
    }
}

int MFK_CALL mfk_aspect(void *opaque, double ratio)
{
    if (!opaque || !std::isfinite(ratio) || ratio < 0) return MFK_INVALID_REQUEST;
    auto &owner = *static_cast<Owner *>(opaque);
    try {
        Frame frame, enhancement;
        double position = 0;
        uint64_t generation = 0;
        bool repaint_retained = false;
        {
            std::lock_guard lock(owner.gate);
            // Only repaint retained frame immediately when paused/ended; while playing,
            // present_loop applies the new aspect on the very next frame without flashing old pixels.
            repaint_retained = (owner.paused.load() || owner.state == MFK_ENDED) && owner.retained;
            if (repaint_retained) frame.reset(av_frame_clone(owner.retained.get()));
            if (frame && owner.retained_enhancement)
                enhancement.reset(av_frame_clone(owner.retained_enhancement.get()));
            position = owner.position;
            generation = owner.output_generation.load();
        }
        std::lock_guard lock(owner.output_gate);
        owner.aspect = ratio;
        if (frame && repaint_retained && owner.target_attached && generation == owner.output_generation.load())
            return owner.renderer->present(frame.get(), enhancement.get(), ratio, position);
        return MFK_OK;
    } catch (...) {
        return MFK_RENDER_FAILED;
    }
}

int MFK_CALL mfk_select_track(void *opaque, int kind, int index)
{
    if (!opaque || (kind != MFK_VIDEO && kind != MFK_AUDIO && kind != MFK_SUBTITLE))
        return MFK_INVALID_REQUEST;
    try {
        const auto cmd_kind = kind == MFK_VIDEO ? Command::VideoTrack :
                              kind == MFK_AUDIO ? Command::AudioTrack : Command::SubtitleTrack;
        return dispatch_command(*static_cast<Owner *>(opaque), cmd_kind, index);
    } catch (...) {
        return MFK_DECODE_FAILED;
    }
}

int MFK_CALL mfk_snapshot_read(void *opaque, mfk_snapshot *snapshot)
{
    if (!opaque || !snapshot || snapshot->size != sizeof(*snapshot) || snapshot->abi != MFK_ABI)
        return MFK_INVALID_REQUEST;
    auto &owner = *static_cast<Owner *>(opaque);
    try {
        std::shared_ptr<FilmAudio> a;
        uint64_t pics = 0;
        {
            std::lock_guard lock(owner.gate);
            snapshot->state = owner.state;
            snapshot->error = owner.error;
            snapshot->position = owner.clock_locked();
            snapshot->duration = owner.duration;
            snapshot->buffered_until = owner.buffered;
            snapshot->width = owner.width;
            snapshot->height = owner.height;
            snapshot->video_track = owner.video_index;
            snapshot->audio_track = owner.audio_index;
            snapshot->subtitle_track = owner.subtitle_index;
            snapshot->native_hdr = owner.native_hdr ? 1 : 0;
            snapshot->tone_mapped = owner.tone_mapped ? 1 : 0;
            snapshot->dolby_vision = owner.dolby_vision ? 1 : 0;
            snapshot->hardware_decoding = owner.hardware ? 1 : 0;
            snapshot->seekable = owner.seekable ? 1 : 0;
            snapshot->muted = owner.muted.load() ? 1 : 0;
            a = owner.audio;
            if (owner.renderer) pics = owner.renderer->submitted_pictures();
        }
        snapshot->audio_degraded = a && a->snapshot().degraded ? 1 : 0;
        snapshot->submitted_pictures = pics;
        return MFK_OK;
    } catch (...) {
        return MFK_INVALID_REQUEST;
    }
}

int MFK_CALL mfk_diagnostics_read(void *opaque, mfk_diagnostics *diag)
{
    if (!opaque || !diag || diag->size != sizeof(*diag) || diag->abi != MFK_ABI)
        return MFK_INVALID_REQUEST;
    auto &owner = *static_cast<Owner *>(opaque);
    try {
        std::shared_ptr<FilmAudio> a;
        std::string fb_reason;
        std::string last_err;
        uint64_t sub_errs = 0;
        {
            std::lock_guard lock(owner.gate);
            diag->dropped_frames = owner.dropped_frames.load();
            diag->av_sync_diff = owner.av_sync_diff.load();
            diag->render_fps = owner.render_fps.load();
            diag->video_packet_queue = static_cast<int32_t>(owner.video_pkts.items.size());
            diag->audio_packet_queue = static_cast<int32_t>(owner.audio_pkts.items.size());
            diag->subtitle_packet_queue = static_cast<int32_t>(owner.sub_pkts.items.size());
            diag->picture_queue = static_cast<int32_t>(owner.pictures.size());
            diag->hardware_fallback = owner.hardware_fallback ? 1 : 0;
            diag->tone_mapped = owner.tone_mapped ? 1 : 0;
            diag->display_hdr_enabled = owner.display_hdr_enabled ? 1 : 0;
            fb_reason = owner.decode_fallback_reason;
            last_err = owner.last_native_error;
            a = owner.audio;
            if (owner.renderer) sub_errs = owner.renderer->subtitle_errors();
        }
        if (fb_reason.empty()) {
            std::lock_guard out_lock(owner.output_gate);
            if (owner.renderer) {
                fb_reason = owner.renderer->transfer_fallback_reason();
                if (!fb_reason.empty()) diag->hardware_fallback = 1;
            }
        }
        const auto asnap = a ? a->snapshot() : FilmAudio::Snapshot{};
        diag->audio_underruns = asnap.underruns;
        diag->audio_reconnects = asnap.reconnects;
        diag->audio_degraded = asnap.degraded ? 1 : 0;
        diag->subtitle_errors = sub_errs;
        copy_utf8(diag->decode_fallback_reason, sizeof(diag->decode_fallback_reason), fb_reason.c_str());
        copy_utf8(diag->last_native_error, sizeof(diag->last_native_error), last_err.c_str());
        return MFK_OK;
    } catch (...) {
        return MFK_INVALID_REQUEST;
    }
}

int MFK_CALL mfk_track_count(void *opaque)
{
    if (!opaque) return 0;
    try {
        auto &owner = *static_cast<Owner *>(opaque);
        std::lock_guard lock(owner.gate);
        return static_cast<int>(owner.tracks.size());
    } catch (...) {
        return 0;
    }
}

int MFK_CALL mfk_track_read(void *opaque, int ordinal, mfk_track *track)
{
    if (!opaque || !track || track->size != sizeof(*track) || ordinal < 0)
        return MFK_INVALID_REQUEST;
    try {
        auto &owner = *static_cast<Owner *>(opaque);
        std::lock_guard lock(owner.gate);
        if (static_cast<size_t>(ordinal) >= owner.tracks.size()) return MFK_INVALID_REQUEST;
        *track = owner.tracks[static_cast<size_t>(ordinal)];
        return MFK_OK;
    } catch (...) {
        return MFK_INVALID_REQUEST;
    }
}

int MFK_CALL mfk_chapter_count(void *opaque)
{
    if (!opaque) return 0;
    try {
        auto &owner = *static_cast<Owner *>(opaque);
        std::lock_guard lock(owner.gate);
        return static_cast<int>(owner.chapters.size());
    } catch (...) {
        return 0;
    }
}

int MFK_CALL mfk_chapter_read(void *opaque, int ordinal, mfk_chapter *chapter)
{
    if (!opaque || !chapter || chapter->size != sizeof(*chapter) || ordinal < 0)
        return MFK_INVALID_REQUEST;
    try {
        auto &owner = *static_cast<Owner *>(opaque);
        std::lock_guard lock(owner.gate);
        if (static_cast<size_t>(ordinal) >= owner.chapters.size()) return MFK_INVALID_REQUEST;
        *chapter = owner.chapters[static_cast<size_t>(ordinal)];
        return MFK_OK;
    } catch (...) {
        return MFK_INVALID_REQUEST;
    }
}

void MFK_CALL mfk_cancel(void *opaque)
{
    if (!opaque) return;
    try { static_cast<Owner *>(opaque)->cancel(); }
    catch (...) { }
}

void MFK_CALL mfk_release(void *opaque)
{
    if (!opaque) return;
    try { delete static_cast<Owner *>(opaque); }
    catch (...) { }
}
