/* FFmpeg track/decode/flush and bounded-frame mechanisms ported from the
 * project's KSPlayer-derived Apple kernel (fixed d0d063a); platform output,
 * device clock and DOVI composition are Windows implementations. */
#include "film_kernel.h"
#include "film_audio.h"
#include "film_renderer.h"
#include "film_dovi.h"
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstring>
#include <deque>
#include <future>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>
extern "C" {
#include "film_io.h"
#include <libavutil/dovi_meta.h>
#include <libavutil/hwcontext.h>
#include <libavutil/mastering_display_metadata.h>
#include <libavutil/pixdesc.h>
}

namespace {
using Clock = std::chrono::steady_clock;
constexpr size_t picture_limit = 12, prepared_picture_limit = 16, waiting_base_limit = 32;
struct FrameDelete { void operator()(AVFrame *frame) const { av_frame_free(&frame); } };
using Frame = std::unique_ptr<AVFrame, FrameDelete>;
struct PacketDelete { void operator()(AVPacket *packet) const { av_packet_free(&packet); } };
using Packet = std::unique_ptr<AVPacket, PacketDelete>;
struct Picture { Frame base, enhancement; double pts = 0; uint64_t generation = 0; double duration = 0; };
struct Command {
    enum Kind { Seek, Rate, AudioTrack, SubtitleTrack } kind;
    double value;
    std::promise<int> result;
};

struct Owner {
    mfk_input input{};
    std::atomic<bool> cancelled{false};
    std::mutex gate, output_gate;
    std::condition_variable changed;
    std::thread decoder, presenter;
    bool prepared = false, committed = false, decode_eof = false, input_eof = false;
    bool decoder_alive = false, seeking = false;
    bool audio_started = false, audio_anchor_pending = true;
    std::atomic<uint64_t> output_generation{0};
    std::atomic<bool> paused{true};
    int state = MFK_NEW, error = MFK_OK;
    AVFormatContext *format = nullptr;
    AVIOContext *root = nullptr;
    AVCodecContext *video = nullptr, *sound = nullptr, *subtitle = nullptr;
    int video_index = -1, audio_index = -1, subtitle_index = -1, width = 0, height = 0;
    double start = 0, duration = 0, discard_before = 0, aspect = 0;
    std::atomic<double> rate{1}, volume{0.8};
    double position = 0, buffered = 0, wall_base = 0;
    double next_video_pts = 0, next_audio_pts = 0;
    double audio_start_pts = 0;
    Clock::time_point wall_time = Clock::now();
    bool hardware = false, seekable = false, native_hdr = false, dolby_vision = false;
    bool target_attached = false; // Protected by output_gate, like the swapchain.
    std::unique_ptr<FilmRenderer> renderer;
    std::shared_ptr<FilmAudio> audio;
    FilmDoviInput dovi_input;
    FilmDovi dovi;
    std::deque<Picture> pictures;
    Frame retained, retained_enhancement;
    std::deque<std::shared_ptr<Command>> commands;
    std::vector<mfk_track> tracks;
    std::deque<Picture> waiting_base;
    std::deque<Packet> prepared_audio;
    size_t prepared_audio_bytes = 0;

    ~Owner()
    {
        cancel();
        if (decoder.joinable()) decoder.join();
        if (presenter.joinable()) presenter.join();
        pictures.clear(); waiting_base.clear();
        retained.reset(); retained_enhancement.reset();
        audio.reset();
        avcodec_free_context(&subtitle);
        avcodec_free_context(&sound);
        avcodec_free_context(&video);
        renderer.reset();
        avformat_close_input(&format);
        mfk_io_close_all(&input);
        if (input.keys) { SecureZeroMemory(input.keys, input.key_count*sizeof(mfk_clear_key)); free(input.keys); }
        SecureZeroMemory(input.default_key, sizeof(input.default_key));
        if (input.uri) { SecureZeroMemory(input.uri, strlen(input.uri)); free(input.uri); }
    }
    void cancel()
    {
        cancelled.store(true);
        InterlockedExchange(&input.cancelled, 1);
        std::lock_guard lock(gate);
        changed.notify_all();
    }
    void fail(int code)
    {
        bool report = false;
        {
            std::lock_guard lock(gate);
            if (!cancelled.load() && error == MFK_OK) {
                error = code; state = MFK_FAILED; report = true;
            }
            changed.notify_all();
        }
        if (report) input.callbacks.failed(input.callbacks.opaque, code);
    }
    bool interrupted() const { return cancelled.load() || mfk_interrupted(const_cast<mfk_input *>(&input)); }
    std::shared_ptr<FilmAudio> audio_output()
    {
        std::lock_guard lock(gate); return audio;
    }
    double clock_locked() const
    {
        if (audio && audio_started) {
            const auto current = audio->snapshot();
            if (current.has_clock) return current.position;
        }
        return paused || seeking || !committed ? position :
            wall_base + std::chrono::duration<double>(Clock::now()-wall_time).count()*rate.load();
    }
    double position_value()
    {
        std::lock_guard lock(gate); return position;
    }
    static size_t packet_cost(const AVPacket *packet)
    {
        constexpr size_t limit = 16*1024*1024;
        if (packet->size < 0 || static_cast<size_t>(packet->size) > limit) return limit+1;
        size_t bytes = sizeof(AVPacket)+packet->size;
        for (int i = 0; i < packet->side_data_elems; i++) {
            if (packet->side_data[i].size > limit || bytes > limit-packet->side_data[i].size)
                return limit+1;
            bytes += packet->side_data[i].size;
        }
        return bytes;
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
    AVCodecContext *open_codec(int index, bool acceleration)
    {
        if (index < 0) return nullptr;
        const auto *stream = format->streams[index];
        const auto *codec = avcodec_find_decoder(stream->codecpar->codec_id);
        if (!codec) return nullptr;
        auto *context = avcodec_alloc_context3(codec);
        const auto *parameters = index == video_index ? dovi_input.parameters() : stream->codecpar;
        if (!context || avcodec_parameters_to_context(context, parameters) < 0) {
            avcodec_free_context(&context); return nullptr;
        }
        context->pkt_timebase = stream->time_base;
        context->thread_count = 2;
        if (acceleration) {
            for (int i = 0; ; i++) {
                const auto *config = avcodec_get_hw_config(codec, i);
                if (!config) break;
                if (config->device_type == AV_HWDEVICE_TYPE_D3D11VA &&
                    (config->methods & AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX)) {
                    context->hw_device_ctx = av_buffer_ref(renderer->hardware_device());
                    context->get_format = choose_format;
                    // FFmpeg budgets codec references and its frame threads,
                    // not our queues, retained picture and in-flight presenter.
                    context->extra_hw_frames = prepared_picture_limit + waiting_base_limit + 2;
                    break;
                }
            }
        }
        if (avcodec_open2(context, codec, nullptr) < 0) {
            avcodec_free_context(&context); return nullptr;
        }
        return context;
    }
    double pts(const AVFrame *frame, int index, double fallback) const
    {
        return frame->best_effort_timestamp == AV_NOPTS_VALUE ? fallback :
            frame->best_effort_timestamp*av_q2d(format->streams[index]->time_base)-start;
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
    int enqueue_picture(Frame frame, bool preparing)
    {
        copy_stream_metadata(frame.get());
        const double time = pts(frame.get(), video_index, next_video_pts);
        const auto frame_rate = av_guess_frame_rate(format, format->streams[video_index], nullptr);
        const double length = frame->duration > 0 ?
            frame->duration*av_q2d(format->streams[video_index]->time_base) :
            frame_rate.num > 0 && frame_rate.den > 0 ? av_q2d(av_inv_q(frame_rate)) : 0;
        next_video_pts = time+length;
        // A seek inside the final frame must not discard the frame covering it.
        if ((length > 0 ? time+length : time)+0.000001 < discard_before) {
            if (frame->pts != AV_NOPTS_VALUE && frame->pts < INT64_MAX) {
                int result = dovi.discard_before(frame->pts+1);
                if (result != MFK_OK) return result;
            }
            return MFK_OK;
        }
        if (waiting_base.size() >= waiting_base_limit) return MFK_DOVI_UNSUPPORTED;
        waiting_base.push_back({std::move(frame), {}, time, output_generation.load(), length});
        return pair_waiting(preparing);
    }
    int receive_video(bool preparing)
    {
        for (;;) {
            Frame frame(av_frame_alloc());
            if (!frame) return MFK_DECODE_FAILED;
            const int result = avcodec_receive_frame(video, frame.get());
            if (result == AVERROR(EAGAIN) || result == AVERROR_EOF) return MFK_OK;
            if (result < 0) return MFK_DECODE_FAILED;
            const int queued = enqueue_picture(std::move(frame), preparing);
            if (queued != MFK_OK) return queued;
        }
    }
    int receive_audio()
    {
        for (;;) {
            Frame frame(av_frame_alloc());
            if (!frame) return MFK_DECODE_FAILED;
            int result = avcodec_receive_frame(sound, frame.get());
            if (result == AVERROR(EAGAIN) || result == AVERROR_EOF) return MFK_OK;
            if (result < 0) return MFK_DECODE_FAILED;
            double time = pts(frame.get(), audio_index, next_audio_pts);
            if (frame->sample_rate <= 0 || frame->nb_samples < 0) return MFK_DECODE_FAILED;
            next_audio_pts = time+static_cast<double>(frame->nb_samples)/frame->sample_rate;
            if (time+static_cast<double>(frame->nb_samples)/frame->sample_rate <= discard_before)
                continue;
            if (time < discard_before) {
                const int skip = std::min(frame->nb_samples,
                    static_cast<int>(std::ceil((discard_before-time)*frame->sample_rate)));
                const int planes = av_sample_fmt_is_planar(static_cast<AVSampleFormat>(frame->format)) ?
                    frame->ch_layout.nb_channels : 1;
                const int bytes = av_get_bytes_per_sample(static_cast<AVSampleFormat>(frame->format)) *
                    (planes == 1 ? frame->ch_layout.nb_channels : 1);
                for (int plane = 0; plane < planes; plane++) frame->extended_data[plane] += skip*bytes;
                frame->nb_samples -= skip; time += static_cast<double>(skip)/frame->sample_rate;
            }
            if (!audio) {
                auto created = std::make_shared<FilmAudio>(cancelled);
                result = created->open(sound, rate.load());
                if (result != MFK_OK) return result;
                std::lock_guard lock(gate);
                created->set_volume(volume.load());
                audio = std::move(created);
            }
            {
                std::lock_guard lock(gate);
                if (audio_anchor_pending) {
                    audio_start_pts = time; audio_anchor_pending = false;
                }
                // Do not start a late audio track ahead of its media timestamp.
                if (!audio_started && !paused && !seeking && time <= clock_locked()+0.002) {
                    result = audio->pause(false);
                    if (result != MFK_OK) return result;
                    audio_started = true;
                }
            }
            result = audio->write(frame.get(), time);
            if (result == MFK_CANCELLED) {
                std::lock_guard lock(gate);
                if (!interrupted() && !commands.empty()) return MFK_OK;
            }
            if (result != MFK_OK) return result;
        }
    }
    int pair_waiting(bool preparing)
    {
        while (!waiting_base.empty()) {
            AVFrame *enhancement = nullptr;
            int paired = dovi.pair(waiting_base.front().base.get(), &enhancement);
            if (paired == AVERROR(EAGAIN)) return MFK_OK;
            if (paired != MFK_OK) return paired;
            Picture picture = std::move(waiting_base.front()); waiting_base.pop_front();
            picture.enhancement.reset(enhancement);
            const double time = picture.pts;
            int result, hdr, dv;
            {
                std::lock_guard lock(output_gate);
                result = renderer->inspect(picture.base.get(), picture.enhancement.get());
                hdr = renderer->native_hdr(); dv = renderer->dolby_vision();
            }
            if (result != MFK_OK) return result;
            std::unique_lock lock(gate);
            if (!preparing) {
                changed.wait(lock, [&] { return interrupted() || !commands.empty() || pictures.size() < picture_limit; });
                if (interrupted()) return MFK_CANCELLED;
                if (!commands.empty()) return MFK_OK;
            }
            if (pictures.size() >= prepared_picture_limit) return MFK_FORMAT_UNSUPPORTED;
            width = picture.base->width; height = picture.base->height;
            hardware = picture.base->format == AV_PIX_FMT_D3D11;
            pictures.push_back(std::move(picture));
            native_hdr = hdr; dolby_vision = dv;
            buffered = std::max(buffered, time+picture.duration); changed.notify_all();
        }
        return MFK_OK;
    }
    int decode_video_packet(const AVPacket *packet, bool preparing)
    {
        int result = dovi.send(packet);
        if (result == AVERROR(EAGAIN)) {
            result = receive_video(preparing);
            if (result == MFK_OK) result = pair_waiting(preparing);
            if (result == MFK_OK) result = dovi.send(packet);
            if (result == AVERROR(EAGAIN)) return MFK_DOVI_UNSUPPORTED;
        }
        if (result != MFK_OK) return result;
        result = pair_waiting(preparing);
        if (result != MFK_OK) return result;
        result = avcodec_send_packet(video, packet);
        if (result == AVERROR(EAGAIN)) {
            int drained = receive_video(preparing);
            if (drained != MFK_OK) return drained;
            result = avcodec_send_packet(video, packet);
        }
        if (result < 0) return MFK_DECODE_FAILED;
        return receive_video(preparing);
    }
    int decode_packet(const AVPacket *packet, bool preparing)
    {
        if (packet->stream_index == video_index ||
            packet->stream_index == dovi_input.enhancement_index()) {
            if (dovi_input.enhancement_index() < 0) return decode_video_packet(packet, preparing);
            int result = dovi_input.send(packet);
            while (result == MFK_OK) {
                AVPacket *merged = nullptr;
                result = dovi_input.take(&merged);
                Packet owned(merged);
                if (result != MFK_OK || !owned) break;
                result = decode_video_packet(owned.get(), preparing);
            }
            return result;
        }
        if (packet->stream_index == audio_index && sound) {
            if (preparing) {
                const size_t bytes = packet_cost(packet);
                if (prepared_audio.size() >= 1024 || bytes > 16*1024*1024 ||
                    prepared_audio_bytes > 16*1024*1024-bytes)
                    return MFK_FORMAT_UNSUPPORTED;
                Packet copy(av_packet_clone(packet));
                if (!copy) return MFK_DECODE_FAILED;
                prepared_audio_bytes += bytes;
                prepared_audio.push_back(std::move(copy));
                return MFK_OK;
            }
            int result = avcodec_send_packet(sound, packet);
            if (result == AVERROR(EAGAIN)) {
                result = receive_audio();
                if (result != MFK_OK) return result;
                result = avcodec_send_packet(sound, packet);
            }
            return result < 0 ? MFK_DECODE_FAILED : receive_audio();
        }
        if (packet->stream_index == subtitle_index && subtitle) {
            AVSubtitle decoded{}; int got = 0;
            if (avcodec_decode_subtitle2(subtitle, &decoded, &got, const_cast<AVPacket *>(packet)) < 0)
                return MFK_DECODE_FAILED;
            int result = MFK_OK;
            if (got) {
                const double time = packet->pts == AV_NOPTS_VALUE ? position_value() :
                    packet->pts*av_q2d(format->streams[subtitle_index]->time_base)-start;
                std::lock_guard lock(output_gate);
                result = renderer->subtitle(decoded, time, subtitle->width, subtitle->height);
            }
            avsubtitle_free(&decoded);
            return result;
        }
        return MFK_OK;
    }
    int seek_to(double target)
    {
        if (!seekable || !std::isfinite(target) || target < 0 || (duration > 0 && target > duration))
            return MFK_SEEK_FAILED;
        {
            std::lock_guard lock(gate);
            position = clock_locked();
            seeking = true;
            ++output_generation; pictures.clear();
        }
        // Wait out any presentation that began before the new generation.
        { std::lock_guard lock(output_gate); }
        const int64_t absolute = static_cast<int64_t>((target+start)*AV_TIME_BASE);
        if (avformat_seek_file(format, -1, INT64_MIN, absolute, absolute, AVSEEK_FLAG_BACKWARD) < 0)
            return MFK_SEEK_FAILED;
        avformat_flush(format);
        avcodec_flush_buffers(video);
        if (sound) avcodec_flush_buffers(sound);
        if (subtitle) avcodec_flush_buffers(subtitle);
        dovi_input.flush(); dovi.flush();
        waiting_base.clear(); prepared_audio.clear(); prepared_audio_bytes = 0;
        {
            std::lock_guard lock(gate);
            pictures.clear(); retained.reset(); retained_enhancement.reset();
            position = wall_base = discard_before = buffered = target;
            next_video_pts = next_audio_pts = target;
            audio_started = false; audio_anchor_pending = true;
            wall_time = Clock::now(); decode_eof = input_eof = false;
        }
        {
            std::lock_guard lock(output_gate); renderer->clear_subtitles();
            if (subtitle) renderer->subtitle_header(subtitle);
        }
        return audio ? audio->reset(target) : MFK_OK;
    }
    int drain(bool preparing)
    {
        if (dovi_input.finish() != MFK_OK) return MFK_DOVI_UNSUPPORTED;
        int result;
        int sent = avcodec_send_packet(video, nullptr);
        if (sent == AVERROR(EAGAIN)) {
            result = receive_video(preparing);
            if (result != MFK_OK) return result;
            sent = avcodec_send_packet(video, nullptr);
        }
        if (sent < 0 && sent != AVERROR_EOF) return MFK_DECODE_FAILED;
        result = receive_video(preparing);
        if (result == MFK_OK) {
            result = dovi.send(nullptr);
            if (result == AVERROR(EAGAIN)) {
                result = pair_waiting(preparing);
                if (result == MFK_OK) result = dovi.send(nullptr);
                if (result == AVERROR(EAGAIN)) return MFK_DOVI_UNSUPPORTED;
            }
        }
        if (result == MFK_OK) result = pair_waiting(preparing);
        if (result == MFK_OK && !waiting_base.empty()) result = MFK_DOVI_UNSUPPORTED;
        if (!preparing && result == MFK_OK && sound) {
            sent = avcodec_send_packet(sound, nullptr);
            if (sent == AVERROR(EAGAIN)) {
                result = receive_audio();
                if (result != MFK_OK) return result;
                sent = avcodec_send_packet(sound, nullptr);
            }
            if (sent < 0 && sent != AVERROR_EOF) return MFK_DECODE_FAILED;
            result = receive_audio();
            if (audio) audio->finish();
        }
        return result;
    }
    int prime(bool display)
    {
        Packet packet(av_packet_alloc());
        if (!packet) return MFK_DECODE_FAILED;
        while (!interrupted()) {
            bool ready;
            { std::lock_guard lock(gate); ready = !pictures.empty(); }
            if (ready) {
                if (display) {
                    Picture first;
                    {
                        std::lock_guard lock(gate);
                        const auto &picture = pictures.front();
                        first = {Frame(av_frame_clone(picture.base.get())),
                            Frame(picture.enhancement ? av_frame_clone(picture.enhancement.get()) : nullptr),
                            picture.pts, picture.generation};
                        if (!first.base || (picture.enhancement && !first.enhancement)) return MFK_DECODE_FAILED;
                    }
                    {
                        std::lock_guard lock(output_gate);
                        int result = target_attached ?
                            renderer->present(first.base.get(), first.enhancement.get(), aspect, first.pts) : MFK_OK;
                        if (result != MFK_OK) return result;
                    }
                    std::lock_guard lock(gate);
                    retained = std::move(first.base); retained_enhancement = std::move(first.enhancement);
                }
                std::lock_guard lock(gate);
                position = wall_base = std::max(discard_before, pictures.front().pts);
                wall_time = Clock::now(); seeking = false;
                if (audio) {
                    int result = audio->pause(paused.load() || !audio_started);
                    if (result != MFK_OK) return result;
                }
                changed.notify_all();
                return MFK_OK;
            }
            if (input_eof) return MFK_SEEK_FAILED;
            int read = av_read_frame(format, packet.get());
            int result;
            if (read == AVERROR_EOF) {
                result = drain(true);
                input_eof = true;
            } else if (read < 0) {
                return interrupted() ? MFK_CANCELLED : MFK_READ_FAILED;
            } else {
                size_t side_size = 0;
                if (av_packet_get_side_data(packet.get(), AV_PKT_DATA_ENCRYPTION_INFO, &side_size))
                    return MFK_ENCRYPTION_UNSUPPORTED;
                result = decode_packet(packet.get(), true);
                av_packet_unref(packet.get());
            }
            if (result != MFK_OK) return result;
        }
        return MFK_CANCELLED;
    }
    int run_command(const Command &command)
    {
        if (command.kind == Command::Seek) {
            int result = seek_to(command.value);
            return result == MFK_OK ? prime(true) : result;
        }
        const double resume = [this] { std::lock_guard lock(gate); return clock_locked(); }();
        if (command.kind == Command::Rate) {
            if (!std::isfinite(command.value) || command.value < 0.25 || command.value > 4)
                return MFK_INVALID_REQUEST;
            rate.store(command.value);
            if (audio) {
                int result = audio->open(sound, rate.load());
                if (result != MFK_OK) return result;
            }
        } else if (command.kind == Command::AudioTrack) {
            const int index = static_cast<int>(command.value);
            if (index < 0 || static_cast<unsigned>(index) >= format->nb_streams ||
                format->streams[index]->codecpar->codec_type != AVMEDIA_TYPE_AUDIO)
                return MFK_INVALID_REQUEST;
            AVCodecContext *replacement = open_codec(index, false);
            if (!replacement) return MFK_DECODE_FAILED;
            std::shared_ptr<FilmAudio> retired;
            {
                std::lock_guard lock(gate);
                retired = std::move(audio); audio_index = index;
                audio_started = false; audio_anchor_pending = true;
            }
            retired.reset(); avcodec_free_context(&sound);
            sound = replacement;
        } else {
            const int index = static_cast<int>(command.value);
            if (index < -1 || (index >= 0 && (static_cast<unsigned>(index) >= format->nb_streams ||
                format->streams[index]->codecpar->codec_type != AVMEDIA_TYPE_SUBTITLE)))
                return MFK_INVALID_REQUEST;
            AVCodecContext *replacement = index >= 0 ? open_codec(index, false) : nullptr;
            if (index >= 0 && !replacement) return MFK_DECODE_FAILED;
            avcodec_free_context(&subtitle); subtitle = replacement;
            { std::lock_guard lock(gate); subtitle_index = index; }
        }
        {
            std::lock_guard lock(gate);
            for (auto &track : tracks) track.selected =
                track.index == video_index || track.index == audio_index || track.index == subtitle_index;
        }
        if (seekable) {
            int result = seek_to(resume);
            return result == MFK_OK ? prime(true) : result;
        }
        Frame frame, enhancement;
        uint64_t generation;
        {
            std::lock_guard lock(gate);
            generation = ++output_generation; pictures.clear();
            position = wall_base = discard_before = resume; wall_time = Clock::now();
            if (retained) {
                frame.reset(av_frame_clone(retained.get()));
                if (!frame) return MFK_DECODE_FAILED;
            }
            if (retained_enhancement) {
                enhancement.reset(av_frame_clone(retained_enhancement.get()));
                if (!enhancement) return MFK_DECODE_FAILED;
            }
        }
        if (audio) {
            int result = audio->reset(resume);
            if (result != MFK_OK) return result;
            std::lock_guard lock(gate);
            audio_started = false; audio_anchor_pending = true;
        }
        {
            std::lock_guard lock(output_gate);
            renderer->clear_subtitles();
            if (subtitle) {
                int result = renderer->subtitle_header(subtitle);
                if (result != MFK_OK) return result;
            }
            // Paused, non-seekable input cannot decode another picture to
            // replace subtitle pixels already composited into the swapchain.
            if (frame && target_attached && generation == output_generation.load())
                return renderer->present(frame.get(), enhancement.get(), aspect, resume);
        }
        return MFK_OK;
    }
    void decode_loop()
    {
        const HRESULT apartment = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        try {
            Packet packet(av_packet_alloc());
            if (!packet) throw std::bad_alloc();
            for (;;) {
                std::shared_ptr<Command> command;
                {
                    std::unique_lock lock(gate);
                    changed.wait(lock, [&] {
                        return interrupted() || error || !commands.empty() || (!paused && !decode_eof);
                    });
                    if (interrupted() || error) break;
                    if (!commands.empty()) { command = commands.front(); commands.pop_front(); }
                }
                if (command) {
                    int result;
                    try { result = run_command(*command); }
                    catch (...) { result = MFK_DECODE_FAILED; }
                    command->result.set_value(result);
                    if (result != MFK_OK) {
                        if (!interrupted()) fail(result);
                        break;
                    }
                    continue;
                }
                if (!prepared_audio.empty()) {
                    Packet prepared = std::move(prepared_audio.front());
                    prepared_audio.pop_front(); prepared_audio_bytes -= packet_cost(prepared.get());
                    int result = decode_packet(prepared.get(), false);
                    if (result != MFK_OK) { if (!interrupted()) fail(result); break; }
                    continue;
                }
                int result = av_read_frame(format, packet.get());
                if (result == AVERROR_EOF) {
                    result = drain(false);
                    { std::lock_guard lock(gate); decode_eof = true; changed.notify_all(); }
                    if (result != MFK_OK) { if (!interrupted()) fail(result); break; }
                    continue;
                }
                if (result < 0) { if (!interrupted()) fail(MFK_READ_FAILED); break; }
                size_t side_size = 0;
                if (av_packet_get_side_data(packet.get(), AV_PKT_DATA_ENCRYPTION_INFO, &side_size)) {
                    fail(MFK_ENCRYPTION_UNSUPPORTED); break;
                }
                result = decode_packet(packet.get(), false);
                av_packet_unref(packet.get());
                if (result != MFK_OK) { if (!interrupted()) fail(result); break; }
            }
        } catch (...) { fail(MFK_DECODE_FAILED); }
        std::lock_guard lock(gate);
        decoder_alive = false;
        for (auto &command : commands) command->result.set_value(MFK_CANCELLED);
        commands.clear(); changed.notify_all();
        if (SUCCEEDED(apartment)) CoUninitialize();
    }
    void present_loop()
    {
        const HRESULT apartment = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        try {
            for (;;) {
                Picture picture;
                uint64_t generation;
                {
                    std::unique_lock lock(gate);
                    if (interrupted() || error) break;
                    if (audio && audio->snapshot().error != MFK_OK) {
                        const int code = audio->snapshot().error;
                        lock.unlock(); fail(code); break;
                    }
                    if (!paused && !seeking && audio && !audio_started &&
                        !audio_anchor_pending && clock_locked()+0.002 >= audio_start_pts) {
                        int result = audio->pause(false);
                        if (result != MFK_OK) { lock.unlock(); fail(result); break; }
                        audio_started = true;
                    }
                    if (paused || seeking || pictures.empty()) {
                        if (!paused && decode_eof && pictures.empty() && (!audio || audio->drained())) {
                            position = audio && audio->has_clock() ? audio->clock() : buffered;
                            state = MFK_ENDED; paused = true;
                        } else if (!paused && pictures.empty() && !decode_eof) state = MFK_BUFFERING;
                        changed.wait_for(lock, std::chrono::milliseconds(10));
                        continue;
                    }
                    const double now = clock_locked();
                    const double delay = pictures.front().pts-now;
                    if (delay > 0.002) {
                        changed.wait_for(lock, std::chrono::duration<double>(std::min(0.02, delay/rate.load())));
                        continue;
                    }
                    picture = std::move(pictures.front()); pictures.pop_front();
                    generation = picture.generation;
                    if (generation != output_generation.load()) { changed.notify_all(); continue; }
                    if (pictures.size() > 1 && delay < -0.1) { changed.notify_all(); continue; }
                    position = std::max(0.0, now); state = MFK_PLAYING;
                    changed.notify_all();
                }
                int result;
                {
                    std::lock_guard lock(output_gate);
                    if (generation != output_generation.load()) continue;
                    result = target_attached ?
                        renderer->present(picture.base.get(), picture.enhancement.get(), aspect, picture.pts) : MFK_OK;
                }
                if (result != MFK_OK) { fail(result); break; }
                {
                    std::lock_guard lock(gate);
                    if (generation != output_generation.load()) continue;
                    retained = std::move(picture.base);
                    retained_enhancement = std::move(picture.enhancement);
                }
            }
        } catch (...) { fail(MFK_RENDER_FAILED); }
        if (SUCCEEDED(apartment)) CoUninitialize();
    }
};

std::once_flag logging;
void no_log(void *, int, const char *, va_list) { }

int prepare(Owner &owner, double position, void *hwnd)
{
    if (!std::isfinite(position) || position < 0) return MFK_INVALID_REQUEST;
    owner.renderer = std::make_unique<FilmRenderer>();
    int result = owner.renderer->open(hwnd);
    if (result != MFK_OK) return result;
    owner.format = avformat_alloc_context();
    if (!owner.format) return MFK_OPEN_FAILED;
    auto *format = owner.format;
    format->opaque = &owner.input;
    format->io_open = mfk_io_open; format->io_close2 = mfk_io_close;
    format->interrupt_callback = {mfk_interrupted, &owner.input};
    format->flags |= AVFMT_FLAG_CUSTOM_IO;
    if (mfk_io_open(format, &owner.root, owner.input.uri, AVIO_FLAG_READ, nullptr) < 0)
        return MFK_OPEN_FAILED;
    format->pb = owner.root;
    uint8_t *location = nullptr;
    av_opt_get(owner.root, "location", AV_OPT_SEARCH_CHILDREN, &location);
    AVDictionary *options = nullptr;
    result = mfk_key_options(&owner.input, &options);
    av_dict_set(&options, "http_persistent", "0", 0);
    av_dict_set(&options, "http_multiple", "0", 0);
    const AVInputFormat *forced = owner.input.format == MFK_HLS ? av_find_input_format("hls") :
        owner.input.format == MFK_DASH ? av_find_input_format("dash") : nullptr;
    if (result >= 0) result = avformat_open_input(&owner.format,
        location ? reinterpret_cast<char *>(location) : owner.input.uri, forced, &options);
    mfk_clear_dictionary(&options); av_free(location);
    if (result < 0 || avformat_find_stream_info(owner.format, nullptr) < 0) return MFK_OPEN_FAILED;
    format = owner.format;
    owner.start = format->start_time == AV_NOPTS_VALUE ? 0 :
        static_cast<double>(format->start_time)/AV_TIME_BASE;
    owner.duration = format->duration > 0 ? static_cast<double>(format->duration)/AV_TIME_BASE : 0;
    owner.video_index = av_find_best_stream(format, AVMEDIA_TYPE_VIDEO, -1, -1, nullptr, 0);
    if (owner.video_index < 0) return MFK_FORMAT_UNSUPPORTED;
    result = owner.dovi_input.open(format, owner.video_index);
    if (result != MFK_OK) return result;
    owner.video_index = owner.dovi_input.base_index();
    owner.audio_index = av_find_best_stream(format, AVMEDIA_TYPE_AUDIO, -1, owner.video_index, nullptr, 0);
    if (owner.audio_index < 0) {
        for (unsigned i = 0; i < format->nb_streams; i++)
            if (format->streams[i]->codecpar->codec_type == AVMEDIA_TYPE_AUDIO)
                return MFK_FORMAT_UNSUPPORTED;
        owner.audio_index = -1;
    }
    owner.subtitle_index = -1;
    if (owner.video_index < 0) return MFK_FORMAT_UNSUPPORTED;
    owner.renderer->set_stream(format->streams[owner.video_index]);
    for (unsigned i = 0; i < format->nb_streams; i++) {
        const auto *stream = format->streams[i];
        if (stream->codecpar->codec_type != AVMEDIA_TYPE_ATTACHMENT) continue;
        const auto *name = av_dict_get(stream->metadata, "filename", nullptr, 0);
        const auto *mime = av_dict_get(stream->metadata, "mimetype", nullptr, 0);
        result = owner.renderer->add_attachment(name ? name->value : nullptr,
            mime ? mime->value : nullptr, stream->codecpar->extradata,
            stream->codecpar->extradata_size);
        if (result != MFK_OK) return result;
    }
    owner.video = owner.open_codec(owner.video_index, true);
    if (!owner.video) owner.video = owner.open_codec(owner.video_index, false);
    if (!owner.video) return MFK_DECODE_FAILED;
    owner.hardware = owner.video->hw_device_ctx != nullptr;
    owner.width = owner.video->width; owner.height = owner.video->height;
    if (owner.audio_index >= 0) {
        owner.sound = owner.open_codec(owner.audio_index, false);
        if (!owner.sound) return MFK_DECODE_FAILED;
    }
    result = owner.dovi.open(owner.dovi_input.parameters(),
                              format->streams[owner.video_index]->time_base);
    if (result != MFK_OK) return result;
    owner.seekable = owner.duration > 0 && !(format->ctx_flags & AVFMTCTX_UNSEEKABLE) &&
        ((owner.root->seekable & AVIO_SEEKABLE_NORMAL) ||
         !strcmp(format->iformat->name, "hls") || !strcmp(format->iformat->name, "dash"));
    for (unsigned i = 0; i < format->nb_streams; i++) {
        if (static_cast<int>(i) == owner.dovi_input.enhancement_index()) continue;
        const auto *stream = format->streams[i];
        int kind = stream->codecpar->codec_type == AVMEDIA_TYPE_VIDEO ? MFK_VIDEO :
            stream->codecpar->codec_type == AVMEDIA_TYPE_AUDIO ? MFK_AUDIO :
            stream->codecpar->codec_type == AVMEDIA_TYPE_SUBTITLE ? MFK_SUBTITLE : 0;
        if (!kind || !avcodec_find_decoder(stream->codecpar->codec_id)) continue;
        mfk_track track{}; track.size = sizeof(track); track.index = i; track.kind = kind;
        track.selected = static_cast<int>(i) == owner.video_index || static_cast<int>(i) == owner.audio_index;
        strncpy(track.codec, avcodec_get_name(stream->codecpar->codec_id), sizeof(track.codec)-1);
        const auto *language = av_dict_get(stream->metadata, "language", nullptr, 0);
        const auto *title = av_dict_get(stream->metadata, "title", nullptr, 0);
        if (language) strncpy(track.language, language->value, sizeof(track.language)-1);
        if (title) strncpy(track.title, title->value, sizeof(track.title)-1);
        owner.tracks.push_back(track);
    }
    if (position > 0) {
        result = owner.seek_to(position);
        if (result != MFK_OK) return result;
    }
    owner.discard_before = position;
    result = owner.prime(false);
    if (result == MFK_OK) {
        std::lock_guard lock(owner.gate);
        owner.prepared = true; owner.state = MFK_PREPARED;
    }
    return result;
}
int command(Owner &owner, Command::Kind kind, double value)
{
    if (!std::isfinite(value)) return MFK_INVALID_REQUEST;
    auto requested = std::make_shared<Command>();
    requested->kind = kind; requested->value = value;
    auto done = requested->result.get_future();
    {
        std::lock_guard lock(owner.gate);
        if (owner.interrupted() || owner.error || !owner.committed || !owner.decoder_alive)
            return MFK_CANCELLED;
        if (kind == Command::Rate) {
            if (value < 0.25 || value > 4) return MFK_INVALID_REQUEST;
            if (owner.commands.empty() && value == owner.rate.load()) return MFK_OK;
        } else if (kind == Command::Seek) {
            if (!owner.seekable || value < 0 || (owner.duration > 0 && value > owner.duration))
                return MFK_SEEK_FAILED;
        } else {
            const int index = static_cast<int>(value);
            const auto type = kind == Command::AudioTrack ? AVMEDIA_TYPE_AUDIO : AVMEDIA_TYPE_SUBTITLE;
            if (value != index || ((index != -1 || type != AVMEDIA_TYPE_SUBTITLE) &&
                (index < 0 || static_cast<unsigned>(index) >= owner.format->nb_streams ||
                 owner.format->streams[index]->codecpar->codec_type != type)))
                return MFK_INVALID_REQUEST;
            if (owner.commands.empty() &&
                index == (kind == Command::AudioTrack ? owner.audio_index : owner.subtitle_index))
                return MFK_OK;
        }
        if (owner.audio) owner.audio->interrupt_write();
        owner.commands.push_back(requested); owner.changed.notify_all();
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
        std::call_once(logging, [] { av_log_set_callback(no_log); });
        auto owner = std::make_unique<Owner>();
        owner->input.callbacks = *callbacks; owner->input.format = format;
        owner->input.uri = _strdup(uri);
        if (!owner->input.uri) return MFK_OPEN_FAILED;
        if (count) {
            owner->input.keys = static_cast<mfk_clear_key *>(malloc(static_cast<size_t>(count)*sizeof(*keys)));
            if (!owner->input.keys) return MFK_OPEN_FAILED;
            memcpy(owner->input.keys, keys, static_cast<size_t>(count)*sizeof(*keys));
            owner->input.key_count = count;
        }
        if (default_key) {
            memcpy(owner->input.default_key, default_key, 16); owner->input.has_default = 1;
        }
        *result = owner.release(); return MFK_OK;
    } catch (...) { return MFK_OPEN_FAILED; }
}
int MFK_CALL mfk_prepare(void *opaque, double position, void *hwnd)
{
    if (!opaque) return MFK_INVALID_REQUEST;
    auto &owner = *static_cast<Owner *>(opaque);
    const HRESULT apartment = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    int result;
    try { result = prepare(owner, position, hwnd); }
    catch (...) { result = MFK_OPEN_FAILED; }
    if (SUCCEEDED(apartment)) CoUninitialize();
    if (result != MFK_OK && result != MFK_CANCELLED) owner.fail(result);
    return result;
}
int MFK_CALL mfk_attach(void *opaque, void *hwnd)
{
    if (!opaque) return MFK_INVALID_REQUEST;
    auto &owner = *static_cast<Owner *>(opaque);
    try {
        Frame frame, enhancement; double position; uint64_t generation;
        {
            std::lock_guard lock(owner.gate);
            if (owner.retained) frame.reset(av_frame_clone(owner.retained.get()));
            if (owner.retained_enhancement)
                enhancement.reset(av_frame_clone(owner.retained_enhancement.get()));
            position = owner.position;
            generation = owner.output_generation.load();
        }
        std::lock_guard lock(owner.output_gate);
        if (!owner.renderer) return MFK_RENDER_FAILED;
        int result = owner.renderer->attach(hwnd);
        if (result == MFK_OK) owner.target_attached = hwnd != nullptr;
        if (result == MFK_OK && owner.target_attached && frame && generation == owner.output_generation.load())
            result = owner.renderer->present(frame.get(), enhancement.get(), owner.aspect, position);
        return result;
    } catch (...) { return MFK_RENDER_FAILED; }
}
int MFK_CALL mfk_start(void *opaque, int paused)
{
    if (!opaque) return MFK_INVALID_REQUEST;
    auto &owner = *static_cast<Owner *>(opaque);
    try {
        std::lock_guard lock(owner.gate);
        if (!owner.prepared || owner.committed || owner.interrupted()) return MFK_INVALID_REQUEST;
        if (owner.pictures.empty()) return MFK_OPEN_FAILED;
        const auto &first = owner.pictures.front();
        owner.retained.reset(av_frame_clone(first.base.get()));
        owner.retained_enhancement.reset(first.enhancement ? av_frame_clone(first.enhancement.get()) : nullptr);
        if (!owner.retained || (first.enhancement && !owner.retained_enhancement)) return MFK_DECODE_FAILED;
        {
            std::lock_guard output_lock(owner.output_gate);
            if (!owner.target_attached) return MFK_RENDER_FAILED;
            int result = owner.renderer->present(first.base.get(), first.enhancement.get(),
                                                  owner.aspect, first.pts);
            if (result != MFK_OK) return result;
        }
        owner.committed = true; owner.paused = paused != 0;
        owner.state = owner.paused ? MFK_PAUSED : MFK_PLAYING;
        owner.wall_base = owner.position; owner.wall_time = Clock::now();
        if (owner.audio) {
            int result = owner.audio->pause(owner.paused.load() || !owner.audio_started);
            if (result != MFK_OK) return result;
        }
        owner.decoder_alive = true;
        owner.decoder = std::thread([&owner] { owner.decode_loop(); });
        owner.presenter = std::thread([&owner] { owner.present_loop(); });
        return MFK_OK;
    } catch (...) { return MFK_OPEN_FAILED; }
}
int MFK_CALL mfk_pause(void *opaque, int paused)
{
    if (!opaque) return MFK_INVALID_REQUEST;
    auto &owner = *static_cast<Owner *>(opaque);
    std::lock_guard lock(owner.gate);
    owner.position = owner.clock_locked();
    if (owner.audio) {
        int result = owner.audio->pause(paused != 0 || !owner.audio_started);
        if (result != MFK_OK) return result;
    }
    owner.paused = paused != 0;
    owner.wall_base = owner.position; owner.wall_time = Clock::now();
    owner.state = owner.paused ? MFK_PAUSED : MFK_PLAYING;
    owner.changed.notify_all(); return MFK_OK;
}
int MFK_CALL mfk_seek(void *opaque, double position)
{
    if (!opaque) return MFK_INVALID_REQUEST;
    auto &owner = *static_cast<Owner *>(opaque);
    if (!owner.committed) {
        if (!owner.prepared) return MFK_INVALID_REQUEST;
        try {
            int result = owner.seek_to(position);
            if (result != MFK_OK) return result;
            return owner.prime(false);
        } catch (...) { return MFK_SEEK_FAILED; }
    }
    return command(owner, Command::Seek, position);
}
int MFK_CALL mfk_rate(void *opaque, double rate)
{
    return opaque ? command(*static_cast<Owner *>(opaque), Command::Rate, rate) : MFK_INVALID_REQUEST;
}
int MFK_CALL mfk_volume(void *opaque, double value)
{
    if (!opaque || !std::isfinite(value) || value < 0 || value > 1) return MFK_INVALID_REQUEST;
    auto &owner = *static_cast<Owner *>(opaque);
    std::lock_guard lock(owner.gate);
    owner.volume = value;
    if (owner.audio) owner.audio->set_volume(value);
    return MFK_OK;
}
int MFK_CALL mfk_aspect(void *opaque, double ratio)
{
    if (!opaque || !std::isfinite(ratio) || ratio < 0) return MFK_INVALID_REQUEST;
    auto &owner = *static_cast<Owner *>(opaque);
    try {
        Frame frame, enhancement; double position; uint64_t generation;
        {
            std::lock_guard lock(owner.gate);
            if (owner.retained) frame.reset(av_frame_clone(owner.retained.get()));
            if (owner.retained_enhancement)
                enhancement.reset(av_frame_clone(owner.retained_enhancement.get()));
            position = owner.position; generation = owner.output_generation.load();
        }
        std::lock_guard lock(owner.output_gate);
        owner.aspect = ratio;
        if (frame && owner.target_attached && generation == owner.output_generation.load())
            return owner.renderer->present(frame.get(), enhancement.get(), ratio, position);
        return MFK_OK;
    } catch (...) { return MFK_RENDER_FAILED; }
}
int MFK_CALL mfk_select_track(void *opaque, int kind, int index)
{
    if (!opaque || (kind != MFK_AUDIO && kind != MFK_SUBTITLE)) return MFK_INVALID_REQUEST;
    return command(*static_cast<Owner *>(opaque),
        kind == MFK_AUDIO ? Command::AudioTrack : Command::SubtitleTrack, index);
}
int MFK_CALL mfk_snapshot_read(void *opaque, mfk_snapshot *snapshot)
{
    if (!opaque || !snapshot || snapshot->size != sizeof(*snapshot) || snapshot->abi != MFK_ABI)
        return MFK_INVALID_REQUEST;
    auto &owner = *static_cast<Owner *>(opaque);
    std::lock_guard lock(owner.gate);
    snapshot->state = owner.state; snapshot->error = owner.error;
    snapshot->position = owner.clock_locked(); snapshot->duration = owner.duration;
    snapshot->buffered_until = owner.buffered;
    snapshot->width = owner.width; snapshot->height = owner.height;
    snapshot->audio_track = owner.audio_index; snapshot->subtitle_track = owner.subtitle_index;
    snapshot->native_hdr = owner.native_hdr;
    snapshot->dolby_vision = owner.dolby_vision;
    snapshot->hardware_decoding = owner.hardware; snapshot->seekable = owner.seekable;
    snapshot->submitted_pictures = owner.renderer ? owner.renderer->submitted_pictures() : 0;
    return MFK_OK;
}
int MFK_CALL mfk_track_count(void *opaque)
{
    if (!opaque) return 0;
    auto &owner = *static_cast<Owner *>(opaque);
    std::lock_guard lock(owner.gate); return static_cast<int>(owner.tracks.size());
}
int MFK_CALL mfk_track_read(void *opaque, int ordinal, mfk_track *track)
{
    if (!opaque || !track || track->size != sizeof(*track) || ordinal < 0) return MFK_INVALID_REQUEST;
    auto &owner = *static_cast<Owner *>(opaque);
    std::lock_guard lock(owner.gate);
    if (static_cast<size_t>(ordinal) >= owner.tracks.size()) return MFK_INVALID_REQUEST;
    *track = owner.tracks[ordinal]; return MFK_OK;
}
void MFK_CALL mfk_cancel(void *opaque) { if (opaque) static_cast<Owner *>(opaque)->cancel(); }
void MFK_CALL mfk_release(void *opaque) { delete static_cast<Owner *>(opaque); }
