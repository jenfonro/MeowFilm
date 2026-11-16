#include "film_audio.h"
#include <cerrno>
#include <charconv>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <audioclient.h>
#include <mmdeviceapi.h>
#include <ksmedia.h>
extern "C" {
#include <libavcodec/avcodec.h>
#include <libavfilter/avfilter.h>
#include <libavfilter/buffersink.h>
#include <libavfilter/buffersrc.h>
#include <libavutil/channel_layout.h>
#include <libavutil/error.h>
#include <libavutil/mem.h>
#include <libavutil/samplefmt.h>
}

namespace {
constexpr DWORD poll_ms = 10;
constexpr auto control_timeout = std::chrono::seconds(5);
template<class T> void release_audio(T *&object) { if (object) object->Release(); object = nullptr; }
struct FrameDelete { void operator()(AVFrame *frame) const { av_frame_free(&frame); } };
using Frame = std::unique_ptr<AVFrame, FrameDelete>;

struct InputFormat {
    int sample_rate = 0;
    AVSampleFormat format = AV_SAMPLE_FMT_NONE;
    AVChannelLayout layout{};
    ~InputFormat() { av_channel_layout_uninit(&layout); }
};

/* Constructed and destroyed on the worker, including failed initialization. */
struct AudioDevice {
    HRESULT apartment = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    IMMDeviceEnumerator *enumerator = nullptr;
    IMMDevice *device = nullptr;
    IAudioClient *client = nullptr;
    IAudioRenderClient *render = nullptr;
    IAudioClock *clock = nullptr;
    WAVEFORMATEX *mix = nullptr;
    AVChannelLayout layout{};
    AVSampleFormat format = AV_SAMPLE_FMT_NONE;
    UINT32 capacity = 0;
    uint64_t frequency = 0;
    int sample_rate = 0, channels = 0, bytes_per_frame = 0;
    bool running = false;

    ~AudioDevice()
    {
        if (client) { client->Stop(); client->Reset(); }
        release_audio(clock); release_audio(render); release_audio(client);
        release_audio(device); release_audio(enumerator);
        av_channel_layout_uninit(&layout);
        if (mix) CoTaskMemFree(mix);
        if (SUCCEEDED(apartment)) CoUninitialize();
    }
    template<class Stop> HRESULT open(HANDLE samples, Stop stopped)
    {
        HRESULT result = apartment;
        const auto proceed = [&] {
            if (SUCCEEDED(result) && stopped()) result = E_ABORT;
            return SUCCEEDED(result);
        };
        if (proceed()) result = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr,
            CLSCTX_ALL, __uuidof(IMMDeviceEnumerator), reinterpret_cast<void **>(&enumerator));
        if (proceed()) result = enumerator->GetDefaultAudioEndpoint(eRender, eMultimedia, &device);
        if (proceed()) result = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr,
                                                        reinterpret_cast<void **>(&client));
        if (proceed()) result = client->GetMixFormat(&mix);
        if (FAILED(result)) return result;
        bool floating = mix->wFormatTag == WAVE_FORMAT_IEEE_FLOAT;
        bool pcm = mix->wFormatTag == WAVE_FORMAT_PCM;
        const WAVEFORMATEXTENSIBLE *ext = nullptr;
        if (mix->wFormatTag == WAVE_FORMAT_EXTENSIBLE &&
            mix->cbSize >= sizeof(WAVEFORMATEXTENSIBLE)-sizeof(WAVEFORMATEX)) {
            ext = reinterpret_cast<const WAVEFORMATEXTENSIBLE *>(mix);
            floating = IsEqualGUID(ext->SubFormat, KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
            pcm = IsEqualGUID(ext->SubFormat, KSDATAFORMAT_SUBTYPE_PCM);
        }
        sample_rate = static_cast<int>(mix->nSamplesPerSec);
        channels = mix->nChannels; bytes_per_frame = mix->nBlockAlign;
        format = floating && mix->wBitsPerSample == 32 ? AV_SAMPLE_FMT_FLT :
            pcm && mix->wBitsPerSample == 16 ? AV_SAMPLE_FMT_S16 :
            pcm && mix->wBitsPerSample == 32 ? AV_SAMPLE_FMT_S32 : AV_SAMPLE_FMT_NONE;
        if (format == AV_SAMPLE_FMT_NONE || sample_rate <= 0 || channels <= 0 ||
            bytes_per_frame != channels*av_get_bytes_per_sample(format)) return E_FAIL;
        if (ext && ext->dwChannelMask) {
            if (av_channel_layout_from_mask(&layout, ext->dwChannelMask) < 0) return E_FAIL;
        } else av_channel_layout_default(&layout, channels);
        if (layout.nb_channels != channels || !av_channel_layout_check(&layout)) return E_FAIL;
        if (proceed()) result = client->Initialize(AUDCLNT_SHAREMODE_SHARED,
            AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_NOPERSIST, 0, 0, mix, nullptr);
        if (proceed()) result = client->SetEventHandle(samples);
        if (proceed()) result = client->GetBufferSize(&capacity);
        if (proceed()) result = client->GetService(__uuidof(IAudioRenderClient),
                                                         reinterpret_cast<void **>(&render));
        if (proceed()) result = client->GetService(__uuidof(IAudioClock),
                                                         reinterpret_cast<void **>(&clock));
        if (proceed()) result = clock->GetFrequency(&frequency);
        return SUCCEEDED(result) && (!capacity || !frequency) ? E_FAIL : result;
    }
    HRESULT stop()
    {
        if (!running) return S_OK;
        const HRESULT result = client->Stop();
        if (SUCCEEDED(result)) running = false;
        return result;
    }
    HRESULT reset()
    {
        HRESULT result = stop();
        if (SUCCEEDED(result)) result = client->Reset();
        return result;
    }
};

struct AudioFilter {
    AVFilterGraph *graph = nullptr;
    AVFilterContext *source = nullptr, *sink = nullptr;
    ~AudioFilter() { avfilter_graph_free(&graph); }
    int open(const InputFormat &input, const AudioDevice &output, double rate)
    {
        avfilter_graph_free(&graph); source = sink = nullptr;
        graph = avfilter_graph_alloc();
        if (!graph) return MFK_AUDIO_FAILED;
        graph->nb_threads = 1;
        char input_layout[256]{}, output_layout[256]{};
        if (av_channel_layout_describe(&input.layout, input_layout, sizeof(input_layout)) < 0 ||
            av_channel_layout_describe(&output.layout, output_layout, sizeof(output_layout)) < 0)
            return MFK_AUDIO_FAILED;
        const char *format = av_get_sample_fmt_name(input.format);
        const char *target = av_get_sample_fmt_name(output.format);
        const AVFilter *buffer_source = avfilter_get_by_name("abuffer");
        const AVFilter *buffer_sink = avfilter_get_by_name("abuffersink");
        if (!format || !target || !buffer_source || !buffer_sink) return MFK_AUDIO_FAILED;
        const std::string args = "time_base=1/" + std::to_string(input.sample_rate) +
            ":sample_rate=" + std::to_string(input.sample_rate) +
            ":sample_fmt=" + format + ":channel_layout=" + input_layout;
        if (avfilter_graph_create_filter(&source, buffer_source, "input",
                                         args.c_str(), nullptr, graph) < 0 ||
            avfilter_graph_create_filter(&sink, buffer_sink, "output",
                                         nullptr, nullptr, graph) < 0) return MFK_AUDIO_FAILED;
        std::string chain;
        while (rate < 0.5) { chain += "atempo=0.5,"; rate /= 0.5; }
        while (rate > 2) { chain += "atempo=2,"; rate /= 2; }
        /* atempo changes duration without changing pitch. aresample uses
         * libswresample; aformat fixes the packed WASAPI device format. */
        if (rate != 1) {
            char tempo[64];
            const auto text = std::to_chars(tempo, tempo+sizeof(tempo), rate);
            chain += "atempo=" + std::string(tempo, text.ptr) + ",";
        }
        chain += "aresample=" +
            std::to_string(output.sample_rate) + ",aformat=sample_fmts=" + target +
            ":sample_rates=" + std::to_string(output.sample_rate) +
            ":channel_layouts=" + output_layout;
        AVFilterInOut *in = avfilter_inout_alloc(), *out = avfilter_inout_alloc();
        if (!in || !out) { avfilter_inout_free(&in); avfilter_inout_free(&out); return MFK_AUDIO_FAILED; }
        out->name = av_strdup("in"); out->filter_ctx = source; out->pad_idx = 0;
        in->name = av_strdup("out"); in->filter_ctx = sink; in->pad_idx = 0;
        const bool names = in->name && out->name;
        const int result = names ? avfilter_graph_parse_ptr(graph, chain.c_str(), &in, &out, nullptr) : -1;
        avfilter_inout_free(&in); avfilter_inout_free(&out);
        return result < 0 || avfilter_graph_config(graph, nullptr) < 0 ? MFK_AUDIO_FAILED : MFK_OK;
    }
};
}

struct FilmAudio::Impl {
    const std::atomic<bool> &cancelled;
    mutable std::mutex gate;
    std::condition_variable changed;
    std::thread worker;
    HANDLE command = nullptr, samples = nullptr;
    std::atomic<bool> closing{false};
    std::atomic<int> failure{MFK_OK};
    bool ready = false, opened = false, ending = false, write_interrupted = false;
    uint64_t generation = 0;
    size_t queued_samples = 0;
    std::shared_ptr<InputFormat> input;
    Snapshot cached;
    double volume = 1, trim_before = 0;
    struct Input { Frame frame; double pts = 0; };
    std::deque<Input> queue;
    struct Request {
        enum Kind { Open, Pause, Reset } kind;
        std::shared_ptr<InputFormat> format;
        double value;
        bool done = false;
        int result = MFK_OK;
    };
    std::deque<std::shared_ptr<Request>> requests;

    explicit Impl(const std::atomic<bool> &c) : cancelled(c)
    {
        command = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        samples = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (!command || !samples) fail(MFK_AUDIO_FAILED);
    }
    ~Impl()
    {
        closing.store(true); wake();
        /* No producer/parent lock is held across join. Cancellation is polled
         * even when WASAPI stops delivering sample notifications. */
        if (worker.joinable()) worker.join();
        if (command) CloseHandle(command);
        if (samples) CloseHandle(samples);
    }
    void wake()
    {
        if (command) SetEvent(command);
        changed.notify_all();
    }
    int status() const
    {
        const int result = failure.load();
        if (result != MFK_OK) return result;
        return closing.load() || cancelled.load() ? MFK_CANCELLED : MFK_OK;
    }
    int fail(int code)
    {
        int expected = MFK_OK;
        failure.compare_exchange_strong(expected, code);
        wake(); return failure.load();
    }
    int device_error(HRESULT result)
    {
        if (SUCCEEDED(result)) return MFK_OK;
        return fail(result == AUDCLNT_E_DEVICE_INVALIDATED ? MFK_DEVICE_LOST : MFK_AUDIO_FAILED);
    }
    int request(Request::Kind kind, double value, std::shared_ptr<InputFormat> format = {})
    {
        auto work = std::make_shared<Request>();
        work->kind = kind; work->value = value; work->format = std::move(format);
        std::unique_lock lock(gate);
        if (status() != MFK_OK) return status();
        if (!ready || (kind != Request::Open && !opened)) return MFK_INVALID_REQUEST;
        if (kind != Request::Pause) { ++generation; write_interrupted = false; }
        requests.push_back(work); wake();
        const auto deadline = std::chrono::steady_clock::now()+control_timeout;
        while (!work->done && status() == MFK_OK) {
            if (std::chrono::steady_clock::now() >= deadline) return fail(MFK_AUDIO_FAILED);
            changed.wait_for(lock, std::chrono::milliseconds(poll_ms));
        }
        return status() != MFK_OK ? status() : work->result;
    }
    int start()
    {
        std::unique_lock lock(gate);
        if (status() != MFK_OK) return status();
        if (!worker.joinable()) {
            try { worker = std::thread([this] {
                try { run(); }
                catch (...) { fail(MFK_AUDIO_FAILED); }
                wake();
            }); }
            catch (...) { return fail(MFK_AUDIO_FAILED); }
        }
        const auto deadline = std::chrono::steady_clock::now()+control_timeout;
        while (!ready && status() == MFK_OK) {
            if (std::chrono::steady_clock::now() >= deadline) return fail(MFK_AUDIO_FAILED);
            changed.wait_for(lock, std::chrono::milliseconds(poll_ms));
        }
        return status();
    }
    void run()
    {
        AudioDevice device;
        const HRESULT initialized = device.open(samples, [this] { return status() != MFK_OK; });
        if (initialized == E_ABORT && status() != MFK_OK) return;
        if (device_error(initialized) != MFK_OK || status() != MFK_OK) return;
        AudioFilter filter;
        film_audio_detail::ConsumptionClock clock;
        clock.reset(device.sample_rate, device.frequency);
        Frame pending;
        int offset = 0;
        int64_t source_samples = 0;
        bool paused = true, filter_eof = false, filter_closed = false, anchored = false;
        double rate = 1, base = 0;
        std::shared_ptr<InputFormat> format;
        {
            std::lock_guard lock(gate); ready = true; changed.notify_all();
        }
        HANDLE events[] = {command, samples};
        while (status() == MFK_OK) {
            std::shared_ptr<Request> work;
            {
                std::lock_guard lock(gate);
                if (!requests.empty()) { work = requests.front(); requests.pop_front(); }
            }
            if (work) {
                int result = MFK_OK;
                if (work->kind == Request::Pause) {
                    paused = work->value != 0;
                    if (paused) result = device_error(device.stop());
                } else {
                    result = device_error(device.reset());
                    if (work->kind == Request::Open) { format = work->format; rate = work->value; }
                    if (result == MFK_OK) result = filter.open(*format, device, rate);
                    if (result != MFK_OK) fail(result);
                    paused = true; pending.reset(); offset = 0; source_samples = 0;
                    filter_eof = filter_closed = anchored = false;
                    clock.reset(device.sample_rate, device.frequency);
                    {
                        std::lock_guard lock(gate);
                        base = work->kind == Request::Reset ? work->value : cached.position;
                        trim_before = base;
                        queue.clear(); queued_samples = 0; ending = false;
                        input = format; opened = result == MFK_OK;
                        cached = {base, base, false, false, MFK_OK};
                        changed.notify_all();
                    }
                }
                /* Pause acknowledges Stop and its final consumed clock;
                 * resume acknowledges the prefill/Start below. */
                if (result != MFK_OK) {
                    std::lock_guard lock(gate);
                    work->result = result; work->done = true; changed.notify_all(); break;
                }
            }
            UINT32 padding = 0;
            UINT64 position = 0;
            HRESULT result = device.client->GetCurrentPadding(&padding);
            if (SUCCEEDED(result)) result = device.clock->GetPosition(&position, nullptr);
            if (device_error(result) != MFK_OK) break;
            if (padding > device.capacity || !clock.observe(position)) { fail(MFK_AUDIO_FAILED); break; }
            UINT32 available = device.capacity-padding;
            bool progressed = false;
            while (available && format && status() == MFK_OK) {
                /* Service controls between chunks; this thread never waits
                 * for producer space and never acquires a parent lock. */
                {
                    std::lock_guard lock(gate); if (!requests.empty()) break;
                }
                if (!pending && !filter_eof) {
                    Frame frame(av_frame_alloc());
                    if (!frame) { fail(MFK_AUDIO_FAILED); break; }
                    const int pulled = av_buffersink_get_frame(filter.sink, frame.get());
                    if (pulled == AVERROR_EOF) { filter_eof = true; progressed = true; }
                    else if (pulled == AVERROR(EAGAIN)) {
                        Input next;
                        bool end = false;
                        {
                            std::lock_guard lock(gate);
                            if (!queue.empty()) {
                                next = std::move(queue.front()); queue.pop_front();
                                queued_samples -= next.frame->nb_samples; changed.notify_all();
                            } else end = ending;
                        }
                        if (next.frame) {
                            if (!anchored) { base = next.pts; anchored = true; }
                            next.frame->pts = source_samples; source_samples += next.frame->nb_samples;
                            if (av_buffersrc_add_frame_flags(filter.source, next.frame.get(),
                                                            AV_BUFFERSRC_FLAG_KEEP_REF) < 0) {
                                fail(MFK_AUDIO_FAILED); break;
                            }
                            progressed = true; continue;
                        }
                        if (end && !filter_closed) {
                            if (av_buffersrc_add_frame_flags(filter.source, nullptr, 0) < 0) {
                                fail(MFK_AUDIO_FAILED); break;
                            }
                            filter_closed = true; progressed = true; continue;
                        }
                        if (end && filter_closed) fail(MFK_AUDIO_FAILED);
                        break;
                    } else if (pulled < 0) { fail(MFK_AUDIO_FAILED); break; }
                    else {
                        if (frame->format != device.format || frame->sample_rate != device.sample_rate ||
                            av_channel_layout_compare(&frame->ch_layout, &device.layout) != 0 ||
                            frame->nb_samples <= 0 || !frame->data[0]) { fail(MFK_AUDIO_FAILED); break; }
                        pending = std::move(frame); offset = 0;
                    }
                }
                if (!pending) break;
                /* Padding must be refreshed for every submission: conversion
                 * and copying may themselves span an underrun. */
                result = device.client->GetCurrentPadding(&padding);
                if (SUCCEEDED(result)) result = device.clock->GetPosition(&position, nullptr);
                if (device_error(result) != MFK_OK) break;
                if (padding > device.capacity || !clock.observe(position)) { fail(MFK_AUDIO_FAILED); break; }
                available = device.capacity-padding;
                if (!available) break;
                const UINT32 frames = std::min<UINT32>(available, pending->nb_samples-offset);
                BYTE *target = nullptr;
                result = device.render->GetBuffer(frames, &target);
                if (device_error(result) != MFK_OK) break;
                const size_t bytes = static_cast<size_t>(frames)*device.bytes_per_frame;
                memcpy(target, pending->data[0]+static_cast<size_t>(offset)*device.bytes_per_frame, bytes);
                double gain;
                { std::lock_guard lock(gate); gain = volume; }
                const size_t values = static_cast<size_t>(frames)*device.channels;
                if (device.format == AV_SAMPLE_FMT_FLT) {
                    auto *out = reinterpret_cast<float *>(target);
                    for (size_t i = 0; i < values; ++i) out[i] *= static_cast<float>(gain);
                } else if (device.format == AV_SAMPLE_FMT_S16) {
                    auto *out = reinterpret_cast<int16_t *>(target);
                    for (size_t i = 0; i < values; ++i) out[i] = static_cast<int16_t>(out[i]*gain);
                } else {
                    auto *out = reinterpret_cast<int32_t *>(target);
                    for (size_t i = 0; i < values; ++i) out[i] = static_cast<int32_t>(out[i]*gain);
                }
                /* The device may have run dry during the copy. Query again
                 * before ReleaseBuffer so that the new origin excludes that
                 * silence as well as the time spent in the filter graph. */
                result = device.client->GetCurrentPadding(&padding);
                if (SUCCEEDED(result)) result = device.clock->GetPosition(&position, nullptr);
                if (FAILED(result) || padding > device.capacity || !clock.observe(position)) {
                    device.render->ReleaseBuffer(frames, AUDCLNT_BUFFERFLAGS_SILENT);
                    if (FAILED(result)) device_error(result); else fail(MFK_AUDIO_FAILED);
                    break;
                }
                result = device.render->ReleaseBuffer(frames, 0);
                if (device_error(result) != MFK_OK) break;
                clock.submit(position, padding, frames);
                offset += frames; padding += frames; available -= frames; progressed = true;
                if (offset == pending->nb_samples) pending.reset();
            }
            if (status() != MFK_OK) break;
            if (!paused && !device.running && padding) {
                result = device.client->Start();
                if (device_error(result) != MFK_OK) break;
                device.running = true;
            }
            result = device.client->GetCurrentPadding(&padding);
            if (SUCCEEDED(result)) result = device.clock->GetPosition(&position, nullptr);
            if (device_error(result) != MFK_OK) break;
            if (padding > device.capacity || !clock.observe(position)) { fail(MFK_AUDIO_FAILED); break; }
            bool drained;
            {
                std::lock_guard lock(gate);
                drained = filter_eof && !pending && queue.empty() && padding == 0 &&
                    clock.consumed == clock.submitted;
            }
            if (drained && device_error(device.stop()) != MFK_OK) break;
            {
                std::lock_guard lock(gate);
                cached.has_clock = clock.valid;
                cached.drained = drained;
                cached.position = anchored ? std::min(cached.buffered_until,
                    base+static_cast<double>(clock.consumed)*rate/device.sample_rate) : base;
                if (drained) cached.position = cached.buffered_until;
                if (work) { work->done = true; changed.notify_all(); }
            }
            if (progressed) continue;
            const DWORD waited = WaitForMultipleObjects(2, events, FALSE, poll_ms);
            if (waited == WAIT_FAILED) { fail(MFK_AUDIO_FAILED); break; }
        }
    }
};

FilmAudio::FilmAudio(const std::atomic<bool> &cancelled) : impl(std::make_unique<Impl>(cancelled)) { }
FilmAudio::~FilmAudio() = default;
int FilmAudio::open(const AVCodecContext *codec, double rate)
{
    auto &p = *impl;
    if (p.status() != MFK_OK) return p.status();
    if (!codec || codec->sample_rate <= 0 || av_get_bytes_per_sample(codec->sample_fmt) <= 0 ||
        !av_channel_layout_check(&codec->ch_layout) || !std::isfinite(rate) || rate < 0.25 || rate > 4)
        return MFK_INVALID_REQUEST;
    auto format = std::make_shared<InputFormat>();
    format->sample_rate = codec->sample_rate; format->format = codec->sample_fmt;
    if (av_channel_layout_copy(&format->layout, &codec->ch_layout) < 0) return p.fail(MFK_AUDIO_FAILED);
    const int result = p.start();
    return result != MFK_OK ? result : p.request(Impl::Request::Open, rate, std::move(format));
}
int FilmAudio::write(const AVFrame *frame, double pts)
{
    auto &p = *impl;
    if (p.status() != MFK_OK) return p.status();
    std::shared_ptr<InputFormat> format;
    uint64_t generation;
    double trim_before;
    {
        std::lock_guard lock(p.gate);
        if (!p.opened || p.ending) return MFK_INVALID_REQUEST;
        if (p.write_interrupted) { p.write_interrupted = false; return MFK_CANCELLED; }
        format = p.input; generation = p.generation; trim_before = p.trim_before;
    }
    if (!frame || !std::isfinite(pts) || frame->nb_samples < 0 ||
        frame->format != format->format || frame->sample_rate != format->sample_rate ||
        av_channel_layout_compare(&frame->ch_layout, &format->layout) != 0 ||
        !frame->extended_data) return MFK_INVALID_REQUEST;
    int offset = 0;
    if (pts < trim_before) {
        const double skip = std::ceil((trim_before-pts)*format->sample_rate-0.000001);
        offset = skip >= frame->nb_samples ? frame->nb_samples : static_cast<int>(std::max(0.0, skip));
    }
    while (offset < frame->nb_samples) {
        const int count = std::min({4096, format->sample_rate, frame->nb_samples-offset});
        Frame copy(av_frame_alloc());
        if (!copy) return p.fail(MFK_AUDIO_FAILED);
        copy->sample_rate = format->sample_rate; copy->format = format->format; copy->nb_samples = count;
        if (av_channel_layout_copy(&copy->ch_layout, &format->layout) < 0 ||
            av_frame_get_buffer(copy.get(), 0) < 0 ||
            av_samples_copy(copy->extended_data, frame->extended_data, 0, offset, count,
                            format->layout.nb_channels, format->format) < 0)
            return p.fail(MFK_AUDIO_FAILED);
        std::unique_lock lock(p.gate);
        while (p.status() == MFK_OK && p.generation == generation && !p.write_interrupted &&
               p.queued_samples+count > static_cast<size_t>(format->sample_rate)*2)
            p.changed.wait_for(lock, std::chrono::milliseconds(poll_ms));
        if (p.status() != MFK_OK) return p.status();
        if (p.generation != generation || p.write_interrupted) {
            p.write_interrupted = false; return MFK_CANCELLED;
        }
        p.queue.push_back({std::move(copy), pts+static_cast<double>(offset)/format->sample_rate});
        p.queued_samples += count; offset += count;
        p.cached.buffered_until = std::max(p.cached.buffered_until,
            pts+static_cast<double>(offset)/format->sample_rate);
        p.wake();
    }
    return MFK_OK;
}
int FilmAudio::pause(bool paused) { return impl->request(Impl::Request::Pause, paused ? 1 : 0); }
int FilmAudio::reset(double position)
{
    if (!std::isfinite(position) || position < 0) return MFK_INVALID_REQUEST;
    return impl->request(Impl::Request::Reset, position);
}
void FilmAudio::interrupt_write()
{
    std::lock_guard lock(impl->gate); impl->write_interrupted = true; impl->wake();
}
void FilmAudio::set_volume(double value)
{
    if (!std::isfinite(value)) return;
    std::lock_guard lock(impl->gate); impl->volume = std::clamp(value, 0.0, 1.0);
}
void FilmAudio::finish()
{
    std::lock_guard lock(impl->gate);
    if (impl->opened && impl->status() == MFK_OK) { impl->ending = true; impl->wake(); }
}
FilmAudio::Snapshot FilmAudio::snapshot() const
{
    std::lock_guard lock(impl->gate);
    Snapshot result = impl->cached;
    result.error = impl->status();
    if (result.error != MFK_OK) result.drained = false;
    return result;
}
bool FilmAudio::drained() const { return snapshot().drained; }
bool FilmAudio::has_clock() const { return snapshot().has_clock; }
double FilmAudio::clock() const { return snapshot().position; }
double FilmAudio::buffered_until() const { return snapshot().buffered_until; }
int FilmAudio::error() const { return impl->status(); }
