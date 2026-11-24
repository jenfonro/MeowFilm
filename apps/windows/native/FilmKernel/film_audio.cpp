#include "film_audio.h"
#include <algorithm>
#include <cerrno>
#include <charconv>
#include <chrono>
#include <climits>
#include <cmath>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <limits>
#include <mutex>
#include <string>
#include <thread>
#include <type_traits>
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <avrt.h>
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

#ifndef AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
#define AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM 0x80000000
#endif
#ifndef AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY
#define AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY 0x08000000
#endif

namespace {
using SteadyClock = std::chrono::steady_clock;
constexpr DWORD poll_ms = 10;
constexpr auto control_timeout = std::chrono::seconds(5);
constexpr auto reconnect_backoff = std::chrono::milliseconds(500);

template<class T> void release_audio(T *&object) { if (object) object->Release(); object = nullptr; }
struct FrameDelete { void operator()(AVFrame *frame) const { av_frame_free(&frame); } };
using Frame = std::unique_ptr<AVFrame, FrameDelete>;

struct InputFormat {
    int sample_rate = 0;
    AVSampleFormat format = AV_SAMPLE_FMT_NONE;
    AVChannelLayout layout{};
    ~InputFormat() { av_channel_layout_uninit(&layout); }
    bool same_as(const InputFormat &other) const
    {
        return sample_rate == other.sample_rate && format == other.format &&
            av_channel_layout_compare(&layout, &other.layout) == 0;
    }
    bool same_as_frame(const AVFrame *frame) const
    {
        return frame && sample_rate == frame->sample_rate &&
            format == static_cast<AVSampleFormat>(frame->format) &&
            av_channel_layout_compare(&layout, &frame->ch_layout) == 0;
    }
};

struct NotifierState {
    std::mutex lock;
    bool detached = false;
    bool endpoint_active = false;
    std::wstring current_device_id;
    std::atomic<bool> *changed = nullptr;
    HANDLE wake_event = nullptr;
};

class EndpointNotificationClient final : public IMMNotificationClient {
    std::atomic<LONG> refs{1};
    std::shared_ptr<NotifierState> state;

    void signal_locked()
    {
        if (state->detached) return;
        if (state->changed) state->changed->store(true);
        if (state->wake_event) SetEvent(state->wake_event);
    }
public:
    explicit EndpointNotificationClient(std::shared_ptr<NotifierState> shared)
        : state(std::move(shared)) { }
    ULONG STDMETHODCALLTYPE AddRef() override { return static_cast<ULONG>(++refs); }
    ULONG STDMETHODCALLTYPE Release() override
    {
        const ULONG remaining = static_cast<ULONG>(--refs);
        if (remaining == 0) delete this;
        return remaining;
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void **ppv) override
    {
        if (!ppv) return E_POINTER;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, __uuidof(IMMNotificationClient))) {
            *ppv = static_cast<IMMNotificationClient *>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    HRESULT STDMETHODCALLTYPE OnDeviceStateChanged(LPCWSTR device_id, DWORD new_state) override
    {
        std::lock_guard guard(state->lock);
        if (state->detached) return S_OK;
        if (!state->endpoint_active) {
            if (new_state == DEVICE_STATE_ACTIVE) signal_locked();
        } else if (device_id && state->current_device_id == device_id && new_state != DEVICE_STATE_ACTIVE) {
            signal_locked();
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnDeviceAdded(LPCWSTR) override
    {
        std::lock_guard guard(state->lock);
        if (!state->detached && !state->endpoint_active) signal_locked();
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnDeviceRemoved(LPCWSTR device_id) override
    {
        std::lock_guard guard(state->lock);
        if (state->detached) return S_OK;
        if (state->endpoint_active && device_id && state->current_device_id == device_id) {
            signal_locked();
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnDefaultDeviceChanged(EDataFlow flow, ERole role, LPCWSTR device_id) override
    {
        if (flow == eRender && (role == eMultimedia || role == eConsole)) {
            std::lock_guard guard(state->lock);
            if (state->detached) return S_OK;
            const std::wstring incoming = device_id ? device_id : L"";
            if (!state->endpoint_active || incoming != state->current_device_id) {
                signal_locked();
            }
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnPropertyValueChanged(LPCWSTR, const PROPERTYKEY) override
    {
        return S_OK;
    }
};

/* Constructed and destroyed strictly on the audio worker thread. */
struct AudioDevice {
    HRESULT apartment = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    IMMDeviceEnumerator *enumerator = nullptr;
    std::shared_ptr<NotifierState> notifier_state;
    EndpointNotificationClient *notifier = nullptr;
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
    bool active = false, running = false;

    ~AudioDevice()
    {
        close_endpoint();
        if (notifier_state) {
            std::lock_guard guard(notifier_state->lock);
            notifier_state->detached = true;
            notifier_state->changed = nullptr;
            notifier_state->wake_event = nullptr;
        }
        if (enumerator && notifier) enumerator->UnregisterEndpointNotificationCallback(notifier);
        release_audio(notifier);
        release_audio(enumerator);
        if (SUCCEEDED(apartment)) CoUninitialize();
    }

    HRESULT init_enumerator(std::atomic<bool> &changed, HANDLE wake_event)
    {
        if (FAILED(apartment)) return apartment;
        if (enumerator) return S_OK;
        HRESULT result = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr,
            CLSCTX_ALL, __uuidof(IMMDeviceEnumerator), reinterpret_cast<void **>(&enumerator));
        if (FAILED(result)) return result;
        notifier_state = std::make_shared<NotifierState>();
        notifier_state->changed = &changed;
        notifier_state->wake_event = wake_event;
        notifier = new EndpointNotificationClient(notifier_state);
        if (FAILED(enumerator->RegisterEndpointNotificationCallback(notifier))) {
            release_audio(notifier);
        }
        return S_OK;
    }

    void set_notifier_endpoint(bool is_active, const std::wstring &id)
    {
        if (!notifier_state) return;
        std::lock_guard guard(notifier_state->lock);
        notifier_state->endpoint_active = is_active;
        notifier_state->current_device_id = id;
    }

    void close_endpoint()
    {
        if (client) {
            client->Stop();
            client->Reset();
        }
        release_audio(clock);
        release_audio(render);
        release_audio(client);
        release_audio(device);
        av_channel_layout_uninit(&layout);
        if (mix) { CoTaskMemFree(mix); mix = nullptr; }
        format = AV_SAMPLE_FMT_NONE;
        capacity = 0; frequency = 0;
        sample_rate = channels = bytes_per_frame = 0;
        active = running = false;
        set_notifier_endpoint(false, {});
    }

    template<class Stop> HRESULT open_endpoint(HANDLE samples, Stop stopped)
    {
        close_endpoint();
        if (!enumerator) return E_FAIL;
        HRESULT result = S_OK;
        const auto proceed = [&] {
            if (SUCCEEDED(result) && stopped()) result = E_ABORT;
            return SUCCEEDED(result);
        };
        if (proceed()) result = enumerator->GetDefaultAudioEndpoint(eRender, eMultimedia, &device);
        if (proceed()) result = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr,
                                                 reinterpret_cast<void **>(&client));
        if (proceed()) result = client->GetMixFormat(&mix);
        if (FAILED(result)) { close_endpoint(); return result; }

        bool floating = mix->wFormatTag == WAVE_FORMAT_IEEE_FLOAT;
        bool pcm = mix->wFormatTag == WAVE_FORMAT_PCM;
        const WAVEFORMATEXTENSIBLE *ext = nullptr;
        if (mix->wFormatTag == WAVE_FORMAT_EXTENSIBLE &&
            mix->cbSize >= sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX)) {
            ext = reinterpret_cast<const WAVEFORMATEXTENSIBLE *>(mix);
            floating = IsEqualGUID(ext->SubFormat, KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
            pcm = IsEqualGUID(ext->SubFormat, KSDATAFORMAT_SUBTYPE_PCM);
        }

        sample_rate = static_cast<int>(mix->nSamplesPerSec);
        channels = mix->nChannels;
        if (sample_rate <= 0 || channels <= 0) { close_endpoint(); return E_FAIL; }

        format = floating && mix->wBitsPerSample == 32 ? AV_SAMPLE_FMT_FLT :
                 pcm && mix->wBitsPerSample == 16 ? AV_SAMPLE_FMT_S16 :
                 pcm && mix->wBitsPerSample == 32 ? AV_SAMPLE_FMT_S32 : AV_SAMPLE_FMT_NONE;

        if (ext && ext->dwChannelMask) {
            if (av_channel_layout_from_mask(&layout, ext->dwChannelMask) < 0)
                av_channel_layout_default(&layout, channels);
        } else {
            av_channel_layout_default(&layout, channels);
        }
        if (layout.nb_channels != channels || !av_channel_layout_check(&layout)) {
            av_channel_layout_uninit(&layout);
            av_channel_layout_default(&layout, channels);
        }

        WAVEFORMATEXTENSIBLE fallback_fmt{};
        const WAVEFORMATEX *selected = mix;
        DWORD flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_NOPERSIST;

        bool need_autoconvert = (format == AV_SAMPLE_FMT_NONE) ||
            (mix->nBlockAlign != channels * av_get_bytes_per_sample(format));

        auto prepare_float_fallback = [&] {
            format = AV_SAMPLE_FMT_FLT;
            bytes_per_frame = channels * 4;
            fallback_fmt = {};
            fallback_fmt.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
            fallback_fmt.Format.nChannels = static_cast<WORD>(channels);
            fallback_fmt.Format.nSamplesPerSec = static_cast<DWORD>(sample_rate);
            fallback_fmt.Format.wBitsPerSample = 32;
            fallback_fmt.Format.nBlockAlign = static_cast<WORD>(bytes_per_frame);
            fallback_fmt.Format.nAvgBytesPerSec = static_cast<DWORD>(sample_rate * bytes_per_frame);
            fallback_fmt.Format.cbSize = sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX);
            fallback_fmt.Samples.wValidBitsPerSample = 32;
            fallback_fmt.dwChannelMask = ext && ext->dwChannelMask ? ext->dwChannelMask :
                (channels == 1 ? SPEAKER_FRONT_CENTER :
                 channels == 2 ? (SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT) : 0);
            fallback_fmt.SubFormat = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
            selected = &fallback_fmt.Format;
            flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_NOPERSIST |
                    AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        };

        if (need_autoconvert) {
            prepare_float_fallback();
        } else {
            bytes_per_frame = mix->nBlockAlign;
        }

        if (proceed()) {
            result = client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 0, 0, selected, nullptr);
            if (!need_autoconvert && result == AUDCLNT_E_UNSUPPORTED_FORMAT) {
                prepare_float_fallback();
                result = client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 0, 0, selected, nullptr);
            }
        }
        if (proceed()) result = client->SetEventHandle(samples);
        if (proceed()) result = client->GetBufferSize(&capacity);
        if (proceed()) result = client->GetService(__uuidof(IAudioRenderClient),
                                                   reinterpret_cast<void **>(&render));
        if (proceed()) result = client->GetService(__uuidof(IAudioClock),
                                                   reinterpret_cast<void **>(&clock));
        if (proceed()) result = clock->GetFrequency(&frequency);
        if (FAILED(result) || !capacity || !frequency) {
            close_endpoint();
            return FAILED(result) ? result : E_FAIL;
        }
        std::wstring endpoint_id;
        LPWSTR raw_id = nullptr;
        if (SUCCEEDED(device->GetId(&raw_id)) && raw_id) {
            endpoint_id = raw_id;
            CoTaskMemFree(raw_id);
        }
        active = true;
        set_notifier_endpoint(true, endpoint_id);
        return S_OK;
    }

    HRESULT stop()
    {
        if (!active || !running) return S_OK;
        const HRESULT result = client->Stop();
        if (SUCCEEDED(result)) running = false;
        return result;
    }
    HRESULT reset()
    {
        if (!active) return S_OK;
        HRESULT result = stop();
        if (SUCCEEDED(result)) result = client->Reset();
        return result;
    }
};

struct AudioFilter {
    AVFilterGraph *graph = nullptr;
    AVFilterContext *source = nullptr, *sink = nullptr;
    ~AudioFilter() { avfilter_graph_free(&graph); }
    void close() { avfilter_graph_free(&graph); source = sink = nullptr; }
    int open(const InputFormat &input, const AudioDevice &output, double rate)
    {
        close();
        if (!output.active) return MFK_OK;
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
        double r = rate;
        while (r < 0.5) { chain += "atempo=0.5,"; r /= 0.5; }
        while (r > 2.0) { chain += "atempo=2,"; r /= 2.0; }
        if (std::abs(r - 1.0) > 1e-6) {
            char tempo[64];
            const auto text = std::to_chars(tempo, tempo + sizeof(tempo), r);
            chain += "atempo=" + std::string(tempo, text.ptr) + ",";
        }
        chain += "aresample=" + std::to_string(output.sample_rate) +
            ",aformat=sample_fmts=" + target +
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

struct PtsAnchor {
    uint64_t output_frame = 0;
    double pts = 0;
    double rate = 1;
};
}

struct FilmAudio::Impl {
    const std::atomic<bool> &cancelled;
    InterruptFn host_interrupted;
    LogFn logger;
    mutable std::mutex gate;
    std::condition_variable changed;
    std::thread worker;
    HANDLE command = nullptr, samples = nullptr;
    std::atomic<bool> closing{false};
    std::atomic<bool> device_changed{false};
    std::atomic<int> failure{MFK_OK};
    bool ready = false, opened = false, ending = false, write_interrupted = false;
    uint64_t generation = 0;
    size_t queued_samples = 0;
    std::shared_ptr<InputFormat> input;
    Snapshot cached;
    double volume = 1.0, trim_before = 0;
    bool muted = false;
    struct Input {
        Frame frame;
        std::shared_ptr<InputFormat> format;
        double pts = 0;
    };
    std::deque<Input> queue;
    struct Request {
        enum Kind { Open, Pause, Rate, Reset } kind;
        std::shared_ptr<InputFormat> format;
        double value = 0;
        bool done = false;
        int result = MFK_OK;
    };
    std::deque<std::shared_ptr<Request>> requests;

    Impl(const std::atomic<bool> &c, InterruptFn inter, LogFn log)
        : cancelled(c), host_interrupted(std::move(inter)), logger(std::move(log))
    {
        command = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        samples = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (!command || !samples) fail(MFK_AUDIO_FAILED);
    }
    ~Impl()
    {
        closing.store(true);
        wake();
        if (worker.joinable()) worker.join();
        if (command) CloseHandle(command);
        if (samples) CloseHandle(samples);
    }
    void log(int level, const char *message) const
    {
        if (logger) logger(level, "film-audio", message);
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
        if (closing.load() || cancelled.load() || (host_interrupted && host_interrupted()))
            return MFK_CANCELLED;
        return MFK_OK;
    }
    int fail(int code)
    {
        int expected = MFK_OK;
        failure.compare_exchange_strong(expected, code);
        wake();
        return failure.load();
    }
    int request(Request::Kind kind, double value, std::shared_ptr<InputFormat> format = {})
    {
        auto work = std::make_shared<Request>();
        work->kind = kind; work->value = value; work->format = std::move(format);
        std::unique_lock lock(gate);
        if (status() != MFK_OK) return status();
        if (!ready || (kind != Request::Open && !opened)) return MFK_INVALID_REQUEST;
        if (kind == Request::Open || kind == Request::Reset) {
            ++generation;
            write_interrupted = false;
        }
        requests.push_back(work);
        wake();
        const auto deadline = SteadyClock::now() + control_timeout;
        while (!work->done && status() == MFK_OK) {
            if (SteadyClock::now() >= deadline) return fail(MFK_AUDIO_FAILED);
            changed.wait_for(lock, std::chrono::milliseconds(poll_ms));
        }
        return status() != MFK_OK ? status() : work->result;
    }
    int start()
    {
        std::unique_lock lock(gate);
        if (status() != MFK_OK) return status();
        if (!worker.joinable()) {
            try {
                worker = std::thread([this] {
                    try { run(); }
                    catch (...) { fail(MFK_AUDIO_FAILED); }
                    wake();
                });
            } catch (...) {
                return fail(MFK_AUDIO_FAILED);
            }
        }
        const auto deadline = SteadyClock::now() + control_timeout;
        while (!ready && status() == MFK_OK) {
            if (SteadyClock::now() >= deadline) return fail(MFK_AUDIO_FAILED);
            changed.wait_for(lock, std::chrono::milliseconds(poll_ms));
        }
        return status();
    }

    void run()
    {
        DWORD mmcss_index = 0;
        HANDLE mmcss = AvSetMmThreadCharacteristicsW(L"Pro Audio", &mmcss_index);

        AudioDevice device;
        const HRESULT enum_hr = device.init_enumerator(device_changed, command);
        auto stop_check = [this] { return status() != MFK_OK; };

        bool degraded = false;
        if (SUCCEEDED(enum_hr)) {
            const HRESULT open_hr = device.open_endpoint(samples, stop_check);
            if (open_hr == E_ABORT && status() != MFK_OK) {
                if (mmcss) AvRevertMmThreadCharacteristics(mmcss);
                return;
            }
            if (FAILED(open_hr)) {
                degraded = true;
                log(MFK_LOG_WARN, "No active WASAPI audio render endpoint; entering muted wall-clock fallback");
            }
        } else {
            degraded = true;
            log(MFK_LOG_WARN, "Failed to initialize MMDeviceEnumerator; entering muted wall-clock fallback");
        }

        AudioFilter filter;
        film_audio_detail::ConsumptionClock clock;
        if (device.active) clock.reset(device.sample_rate, device.frequency);

        Frame pending;
        int offset = 0;
        int64_t source_samples = 0;
        bool paused = true, filter_eof = false, filter_closed = false, anchored = false;
        double rate = 1.0, base = 0.0;
        double expected_input_pts = 0.0;
        std::deque<PtsAnchor> anchors;
        std::shared_ptr<InputFormat> filter_format;
        uint64_t underruns = 0, reconnects = 0;
        int degraded_retries = 0;
        constexpr int max_degraded_retries = 5;
        SteadyClock::time_point last_reconnect_try{};
        SteadyClock::time_point wall_anchor = SteadyClock::now();
        double wall_position = 0.0;
        float current_gain = 1.0f;
        {
            std::lock_guard lock(gate);
            current_gain = muted ? 0.0f : static_cast<float>(volume);
            ready = true;
            cached.degraded = degraded;
            changed.notify_all();
        }

        auto compute_position = [&]() -> double {
            if (!anchored) return base;
            if (degraded || !device.active) {
                const double pos = paused ? wall_position :
                    wall_position + std::chrono::duration<double>(SteadyClock::now() - wall_anchor).count() * rate;
                return pos;
            }
            while (anchors.size() > 1 && clock.consumed >= anchors[1].output_frame) {
                anchors.pop_front();
            }
            if (anchors.empty()) {
                return base + static_cast<double>(clock.consumed) * rate / device.sample_rate;
            }
            const auto &a = anchors.front();
            const uint64_t delta = clock.consumed > a.output_frame ? (clock.consumed - a.output_frame) : 0;
            return a.pts + static_cast<double>(delta) * a.rate / device.sample_rate;
        };

        auto reset_filter_state = [&](double anchor_pos) {
            pending.reset(); offset = 0; source_samples = 0;
            filter_eof = filter_closed = anchored = false;
            base = wall_position = anchor_pos;
            wall_anchor = SteadyClock::now();
            anchors.clear();
            if (device.active) clock.reset(device.sample_rate, device.frequency);
        };

        auto try_reconnect_device = [&](double resume_pos, bool was_active) {
            if (status() != MFK_OK) return;
            const auto now = SteadyClock::now();
            const bool notified = device_changed.exchange(false);
            if (notified) degraded_retries = 0;
            if (!was_active && !notified &&
                last_reconnect_try.time_since_epoch().count() != 0 &&
                now - last_reconnect_try < reconnect_backoff) {
                return;
            }
            last_reconnect_try = now;
            const HRESULT hr = device.open_endpoint(samples, stop_check);
            if (SUCCEEDED(hr) && device.active) {
                degraded = false;
                degraded_retries = 0;
                ++reconnects;
                reset_filter_state(resume_pos);
                if (filter_format && filter.open(*filter_format, device, rate) != MFK_OK) {
                    device.close_endpoint();
                    degraded = true;
                    wall_position = resume_pos;
                    wall_anchor = SteadyClock::now();
                    anchored = true;
                    log(MFK_LOG_WARN, "AudioFilter rebuild failed after WASAPI reconnect; staying degraded");
                } else {
                    log(MFK_LOG_INFO, "WASAPI audio endpoint reconnected seamlessly");
                }
            } else {
                device.close_endpoint();
                if (!degraded) {
                    log(MFK_LOG_WARN, "WASAPI endpoint lost; falling back to muted wall-clock playback");
                }
                degraded = true;
                ++degraded_retries;
                wall_position = resume_pos;
                wall_anchor = SteadyClock::now();
                anchored = true;
            }
        };

        auto handle_device_failure = [&](HRESULT hr) -> bool {
            if (SUCCEEDED(hr)) return true;
            const double pos = compute_position();
            if (hr == AUDCLNT_E_DEVICE_INVALIDATED || hr == AUDCLNT_E_SERVICE_NOT_RUNNING ||
                hr == AUDCLNT_E_RESOURCES_INVALIDATED) {
                log(MFK_LOG_WARN, "WASAPI device invalidated; attempting reconnect");
                try_reconnect_device(pos, true);
                return true;
            }
            // Any unexpected device failure also degrades to muted wall clock rather than killing video.
            log(MFK_LOG_WARN, "WASAPI error encountered; degrading to muted wall-clock playback");
            device.close_endpoint();
            degraded = true;
            wall_position = pos;
            wall_anchor = SteadyClock::now();
            anchored = true;
            return true;
        };

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
                    const bool want_pause = work->value != 0;
                    if (want_pause && !paused) {
                        wall_position = compute_position();
                    }
                    paused = want_pause;
                    wall_anchor = SteadyClock::now();
                    if (paused && device.active) {
                        handle_device_failure(device.stop());
                    }
                } else if (work->kind == Request::Rate) {
                    const double cur_pos = compute_position();
                    const bool was_anchored = anchored;
                    rate = work->value;
                    if (device.active) handle_device_failure(device.reset());
                    reset_filter_state(cur_pos);
                    if (degraded || !device.active) {
                        anchored = was_anchored;
                    } else if (filter_format) {
                        if (filter.open(*filter_format, device, rate) != MFK_OK) {
                            log(MFK_LOG_WARN, "AudioFilter rebuild failed on rate change; degrading to muted wall-clock");
                            device.close_endpoint();
                            degraded = true;
                            anchored = was_anchored;
                        }
                    }
                    // Retain queued unplayed packets ahead of cur_pos; only drop already-consumed prefix.
                    {
                        std::lock_guard lock(gate);
                        trim_before = cur_pos;
                        while (!queue.empty()) {
                            const auto &front = queue.front();
                            const double dur = front.format && front.format->sample_rate > 0 ?
                                static_cast<double>(front.frame->nb_samples) / front.format->sample_rate : 0.0;
                            if (front.pts + dur <= cur_pos) {
                                queued_samples -= front.frame->nb_samples;
                                queue.pop_front();
                            } else {
                                break;
                            }
                        }
                        changed.notify_all();
                    }
                    wall_position = cur_pos;
                    wall_anchor = SteadyClock::now();
                } else {
                    if (device.active) handle_device_failure(device.reset());
                    if (work->kind == Request::Open) { filter_format = work->format; rate = work->value; }
                    if (device.active && filter_format) {
                        result = filter.open(*filter_format, device, rate);
                        if (result != MFK_OK) fail(result);
                    }
                    paused = true;
                    const double next_base = work->kind == Request::Reset ? work->value : cached.position;
                    reset_filter_state(next_base);
                    {
                        std::lock_guard lock(gate);
                        trim_before = next_base;
                        queue.clear(); queued_samples = 0; ending = false;
                        input = filter_format; opened = result == MFK_OK;
                        cached = {next_base, next_base, false, false, degraded, MFK_OK, underruns, reconnects};
                        changed.notify_all();
                    }
                }
                {
                    std::lock_guard lock(gate);
                    cached.degraded = degraded || !device.active;
                    work->result = result;
                    work->done = true;
                    changed.notify_all();
                }
                if (status() != MFK_OK) break;
            }

            const bool should_retry_degraded = (degraded || !device.active) &&
                degraded_retries > 0 && degraded_retries < max_degraded_retries &&
                last_reconnect_try.time_since_epoch().count() != 0 &&
                SteadyClock::now() - last_reconnect_try >= reconnect_backoff;
            if (device_changed.load() || should_retry_degraded) {
                const double pos = compute_position();
                try_reconnect_device(pos, device.active);
            }

            bool progressed = false;
            bool drained = false;

            if (degraded || !device.active) {
                // Degraded muted wall-clock mode: advance media clock and drain queue at playback rate.
                const auto now = SteadyClock::now();
                if (!paused) {
                    const double dt = std::chrono::duration<double>(now - wall_anchor).count();
                    if (dt > 0) wall_position += dt * rate;
                }
                wall_anchor = now;
                double buf_end = wall_position;
                bool queue_empty = true;
                bool is_ending = false;
                {
                    std::lock_guard lock(gate);
                    while (!queue.empty()) {
                        const auto &front = queue.front();
                        if (!anchored) {
                            base = wall_position = front.pts;
                            anchored = true;
                        }
                        const double dur = front.format && front.format->sample_rate > 0 ?
                            static_cast<double>(front.frame->nb_samples) / front.format->sample_rate : 0.0;
                        if (!paused && front.pts + dur <= wall_position + 0.005) {
                            queued_samples -= front.frame->nb_samples;
                            queue.pop_front();
                            progressed = true;
                            changed.notify_all();
                        } else {
                            break;
                        }
                    }
                    queue_empty = queue.empty();
                    is_ending = ending;
                    buf_end = cached.buffered_until;
                }
                if (is_ending && queue_empty && (!anchored || wall_position >= buf_end - 0.005)) {
                    drained = true;
                }
                {
                    std::lock_guard lock(gate);
                    cached.has_clock = anchored;
                    cached.drained = drained;
                    cached.degraded = true;
                    cached.underruns = underruns;
                    cached.reconnects = reconnects;
                    cached.position = anchored ? std::min(cached.buffered_until, wall_position) : base;
                    if (drained) cached.position = cached.buffered_until;
                }
                if (progressed) continue;
                const bool retry_pending = degraded_retries > 0 && degraded_retries < max_degraded_retries;
                WaitForSingleObject(command, (paused && !retry_pending) ? INFINITE : poll_ms);
                continue;
            }

            UINT32 padding = 0;
            UINT64 position = 0;
            HRESULT hr = device.client->GetCurrentPadding(&padding);
            if (SUCCEEDED(hr)) hr = device.clock->GetPosition(&position, nullptr);
            if (FAILED(hr)) { handle_device_failure(hr); continue; }
            if (padding > device.capacity || !clock.observe(position, padding == 0)) {
                log(MFK_LOG_WARN, "Audio clock observation out of bounds; reconnecting device");
                try_reconnect_device(compute_position(), true);
                continue;
            }
            if (!paused && device.running && padding == 0 && !filter_eof) {
                std::lock_guard lock(gate);
                if (!queue.empty() || pending) ++underruns;
            }
            UINT32 available = device.capacity - padding;
            while (available && filter_format && device.active && status() == MFK_OK) {
                {
                    std::lock_guard lock(gate);
                    if (!requests.empty() || device_changed.load()) break;
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
                            // Dynamic mid-stream format / sample rate / channel layout change.
                            if (next.format && !filter_format->same_as(*next.format)) {
                                log(MFK_LOG_INFO, "Audio input format changed mid-stream; rebuilding filter graph");
                                filter_format = next.format;
                                if (filter.open(*filter_format, device, rate) != MFK_OK) {
                                    fail(MFK_AUDIO_FAILED); break;
                                }
                                source_samples = 0;
                                filter_closed = false;
                                const uint64_t out_at = clock.submitted;
                                anchors.push_back({out_at, next.pts, rate});
                                expected_input_pts = next.pts;
                            }
                            if (!anchored) {
                                base = next.pts;
                                expected_input_pts = next.pts;
                                anchors.clear();
                                anchors.push_back({0, base, rate});
                                anchored = true;
                            } else if (std::abs(next.pts - expected_input_pts) > 0.05) {
                                // Smooth PTS anchor update across HLS/container discontinuities.
                                const uint64_t pending_out = pending ? static_cast<uint64_t>(pending->nb_samples - offset) : 0;
                                anchors.push_back({clock.submitted + pending_out, next.pts, rate});
                                expected_input_pts = next.pts;
                            }
                            const double frame_dur = filter_format->sample_rate > 0 ?
                                static_cast<double>(next.frame->nb_samples) / filter_format->sample_rate : 0.0;
                            expected_input_pts += frame_dur;
                            next.frame->pts = source_samples;
                            source_samples += next.frame->nb_samples;
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
                        if (end && filter_closed) filter_eof = true;
                        break;
                    } else if (pulled < 0) { fail(MFK_AUDIO_FAILED); break; }
                    else {
                        if (frame->format != device.format || frame->sample_rate != device.sample_rate ||
                            av_channel_layout_compare(&frame->ch_layout, &device.layout) != 0 ||
                            frame->nb_samples <= 0 || !frame->data[0]) {
                            fail(MFK_AUDIO_FAILED); break;
                        }
                        pending = std::move(frame); offset = 0;
                    }
                }
                if (!pending) break;
                hr = device.client->GetCurrentPadding(&padding);
                if (SUCCEEDED(hr)) hr = device.clock->GetPosition(&position, nullptr);
                if (FAILED(hr)) { handle_device_failure(hr); break; }
                if (padding > device.capacity || !clock.observe(position, padding == 0)) {
                    try_reconnect_device(compute_position(), true);
                    break;
                }
                available = device.capacity - padding;
                if (!available) break;
                const UINT32 frames = std::min<UINT32>(available, static_cast<UINT32>(pending->nb_samples - offset));
                BYTE *target = nullptr;
                hr = device.render->GetBuffer(frames, &target);
                if (FAILED(hr)) { handle_device_failure(hr); break; }
                const size_t bytes = static_cast<size_t>(frames) * device.bytes_per_frame;
                memcpy(target, pending->data[0] + static_cast<size_t>(offset) * device.bytes_per_frame, bytes);

                float target_gain;
                { std::lock_guard lock(gate); target_gain = muted ? 0.0f : static_cast<float>(volume); }
                const int ramp_frames = std::max(1, device.sample_rate / 100);
                const float max_step = 1.0f / static_cast<float>(ramp_frames);
                const int ch = device.channels;

                auto scale_sample = [](auto sample, float gain) {
                    using Sample = decltype(sample);
                    if constexpr (std::is_floating_point_v<Sample>) {
                        return static_cast<Sample>(sample * gain);
                    } else {
                        const double scaled = std::round(static_cast<double>(sample) * static_cast<double>(gain));
                        constexpr double lo = static_cast<double>(std::numeric_limits<Sample>::min());
                        constexpr double hi = static_cast<double>(std::numeric_limits<Sample>::max());
                        return static_cast<Sample>(std::clamp(scaled, lo, hi));
                    }
                };
                auto apply_gain = [&](auto *out) {
                    if (std::abs(current_gain - target_gain) < 1e-5f) {
                        current_gain = target_gain;
                        if (std::abs(current_gain - 1.0f) < 1e-5f) return;
                        const size_t count = static_cast<size_t>(frames) * ch;
                        for (size_t i = 0; i < count; ++i)
                            out[i] = scale_sample(out[i], current_gain);
                        return;
                    }
                    for (UINT32 f = 0; f < frames; ++f) {
                        const float diff = target_gain - current_gain;
                        if (std::abs(diff) <= max_step) current_gain = target_gain;
                        else current_gain += diff > 0 ? max_step : -max_step;
                        for (int c = 0; c < ch; ++c) {
                            const size_t idx = static_cast<size_t>(f) * ch + c;
                            out[idx] = scale_sample(out[idx], current_gain);
                        }
                    }
                };
                if (device.format == AV_SAMPLE_FMT_FLT) apply_gain(reinterpret_cast<float *>(target));
                else if (device.format == AV_SAMPLE_FMT_S16) apply_gain(reinterpret_cast<int16_t *>(target));
                else apply_gain(reinterpret_cast<int32_t *>(target));

                hr = device.client->GetCurrentPadding(&padding);
                if (SUCCEEDED(hr)) hr = device.clock->GetPosition(&position, nullptr);
                if (FAILED(hr) || padding > device.capacity || !clock.observe(position, padding == 0)) {
                    device.render->ReleaseBuffer(frames, AUDCLNT_BUFFERFLAGS_SILENT);
                    if (FAILED(hr)) handle_device_failure(hr);
                    else try_reconnect_device(compute_position(), true);
                    break;
                }
                hr = device.render->ReleaseBuffer(frames, 0);
                if (FAILED(hr)) { handle_device_failure(hr); break; }
                clock.submit(position, padding, frames);
                offset += static_cast<int>(frames);
                padding += frames;
                available -= frames;
                progressed = true;
                if (offset == pending->nb_samples) pending.reset();
            }
            if (status() != MFK_OK) break;
            if (!device.active) continue;

            if (!paused && !device.running && padding) {
                hr = device.client->Start();
                if (FAILED(hr)) { handle_device_failure(hr); continue; }
                device.running = true;
            }
            hr = device.client->GetCurrentPadding(&padding);
            if (SUCCEEDED(hr)) hr = device.clock->GetPosition(&position, nullptr);
            if (FAILED(hr)) { handle_device_failure(hr); continue; }
            if (padding > device.capacity || !clock.observe(position, padding == 0)) {
                try_reconnect_device(compute_position(), true);
                continue;
            }
            {
                std::lock_guard lock(gate);
                drained = filter_eof && !pending && queue.empty() && padding == 0 &&
                    clock.consumed >= clock.submitted;
            }
            if (drained) {
                handle_device_failure(device.stop());
            }
            {
                std::lock_guard lock(gate);
                cached.has_clock = clock.valid;
                cached.drained = drained;
                cached.degraded = false;
                cached.underruns = underruns;
                cached.reconnects = reconnects;
                cached.position = anchored ? std::min(cached.buffered_until, compute_position()) : base;
                if (drained) cached.position = cached.buffered_until;
            }
            if (progressed) continue;
            if (paused && !device.running) {
                const DWORD waited = WaitForSingleObject(command, INFINITE);
                if (waited == WAIT_FAILED) { fail(MFK_AUDIO_FAILED); break; }
            } else {
                const DWORD waited = WaitForMultipleObjects(2, events, FALSE, poll_ms);
                if (waited == WAIT_FAILED) { fail(MFK_AUDIO_FAILED); break; }
            }
        }
        if (mmcss) AvRevertMmThreadCharacteristics(mmcss);
    }
};

FilmAudio::FilmAudio(const std::atomic<bool> &cancelled, InterruptFn interrupted, LogFn logger)
    : impl(std::make_unique<Impl>(cancelled, std::move(interrupted), std::move(logger))) { }
FilmAudio::~FilmAudio() = default;

int FilmAudio::open(const AVCodecContext *codec, double rate)
{
    auto &p = *impl;
    if (p.status() != MFK_OK) return p.status();
    if (!codec || codec->sample_rate <= 0 || av_get_bytes_per_sample(codec->sample_fmt) <= 0 ||
        !av_channel_layout_check(&codec->ch_layout) || !std::isfinite(rate) || rate < 0.25 || rate > 4.0)
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
        frame->sample_rate <= 0 || av_get_bytes_per_sample(static_cast<AVSampleFormat>(frame->format)) <= 0 ||
        !av_channel_layout_check(&frame->ch_layout) || !frame->extended_data)
        return MFK_INVALID_REQUEST;

    // Support mid-stream format / channel / sample-rate switches dynamically.
    if (!format->same_as_frame(frame)) {
        auto next_fmt = std::make_shared<InputFormat>();
        next_fmt->sample_rate = frame->sample_rate;
        next_fmt->format = static_cast<AVSampleFormat>(frame->format);
        if (av_channel_layout_copy(&next_fmt->layout, &frame->ch_layout) < 0)
            return p.fail(MFK_AUDIO_FAILED);
        std::lock_guard lock(p.gate);
        if (p.generation != generation) return MFK_CANCELLED;
        p.input = next_fmt;
        format = next_fmt;
    }

    int offset = 0;
    if (pts < trim_before) {
        const double skip = std::ceil((trim_before - pts) * format->sample_rate - 0.000001);
        offset = skip >= frame->nb_samples ? frame->nb_samples : static_cast<int>(std::max(0.0, skip));
    }
    while (offset < frame->nb_samples) {
        const int count = std::min({4096, format->sample_rate, frame->nb_samples - offset});
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
               p.queued_samples + static_cast<size_t>(count) > static_cast<size_t>(format->sample_rate) * 2)
            p.changed.wait_for(lock, std::chrono::milliseconds(poll_ms));
        if (p.status() != MFK_OK) return p.status();
        if (p.generation != generation || p.write_interrupted) {
            p.write_interrupted = false; return MFK_CANCELLED;
        }
        p.queue.push_back({std::move(copy), format, pts + static_cast<double>(offset) / format->sample_rate});
        p.queued_samples += static_cast<size_t>(count); offset += count;
        p.cached.buffered_until = std::max(p.cached.buffered_until,
            pts + static_cast<double>(offset) / format->sample_rate);
        p.wake();
    }
    return MFK_OK;
}

int FilmAudio::pause(bool paused) { return impl->request(Impl::Request::Pause, paused ? 1.0 : 0.0); }
int FilmAudio::set_rate(double rate)
{
    if (!std::isfinite(rate) || rate < 0.25 || rate > 4.0) return MFK_INVALID_REQUEST;
    return impl->request(Impl::Request::Rate, rate);
}
int FilmAudio::reset(double position)
{
    if (!std::isfinite(position) || position < 0) return MFK_INVALID_REQUEST;
    return impl->request(Impl::Request::Reset, position);
}
void FilmAudio::interrupt_write()
{
    std::lock_guard lock(impl->gate);
    impl->write_interrupted = true;
    impl->wake();
}
void FilmAudio::set_volume(double value)
{
    if (!std::isfinite(value)) return;
    std::lock_guard lock(impl->gate);
    impl->volume = std::clamp(value, 0.0, 1.0);
}
void FilmAudio::set_muted(bool value)
{
    std::lock_guard lock(impl->gate);
    impl->muted = value;
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
