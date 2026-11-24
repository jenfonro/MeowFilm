#ifndef MEOWFILM_FILM_AUDIO_H
#define MEOWFILM_FILM_AUDIO_H

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <deque>
#include <functional>
#include <memory>
#include "film_kernel.h"

struct AVCodecContext;
struct AVFrame;

namespace film_audio_detail {
/* IAudioClock continues across an underrun. Each discontinuous submission has
 * its own device origin; silence between submissions never consumes media. */
class ConsumptionClock {
    struct Span { long double origin; uint64_t first, frames; };
    std::deque<Span> spans;
    int sample_rate = 0;
    uint64_t frequency = 0, last_position = 0;
public:
    uint64_t submitted = 0, consumed = 0;
    bool valid = false;

    void reset(int samples_per_second, uint64_t ticks_per_second)
    {
        spans.clear();
        sample_rate = samples_per_second; frequency = ticks_per_second;
        submitted = consumed = last_position = 0; valid = false;
    }
    bool observe(uint64_t position, bool device_empty = false)
    {
        if (!frequency || sample_rate <= 0) return false;
        // Tolerate tiny monotonic clock jitter (<= 10ms) from audio drivers.
        if (position < last_position) {
            const uint64_t jitter = last_position - position;
            if (jitter > frequency / 100) return false;
            position = last_position;
        } else {
            last_position = position;
        }
        while (!spans.empty()) {
            const auto &span = spans.front();
            const long double elapsed = (static_cast<long double>(position) - span.origin) *
                sample_rate / frequency;
            const long double rounded = std::round(elapsed);
            const auto frames = static_cast<uint64_t>(
                std::clamp(rounded, 0.0L, static_cast<long double>(span.frames)));
            consumed = (std::max)(consumed, span.first + frames);
            if (frames < span.frames) break;
            spans.pop_front();
        }
        if (device_empty && !spans.empty() && consumed + 1 >= submitted) {
            consumed = submitted;
            spans.clear();
        }
        return true;
    }
    void submit(uint64_t position, uint32_t padding, uint32_t frames)
    {
        if (!frames) return;
        if (!padding || spans.empty()) {
            long double origin = position;
            if (!spans.empty()) {
                const auto &tail = spans.back();
                origin = (std::max)(origin, tail.origin +
                    static_cast<long double>(tail.frames) * frequency / sample_rate);
            }
            if (spans.empty() && padding)
                origin += static_cast<long double>(padding) * frequency / sample_rate;
            spans.push_back({origin, submitted, frames});
        } else {
            spans.back().frames += frames;
        }
        submitted += frames; valid = true;
    }
};
}

/* Owns the film's WASAPI device and device-consumed media clock, never music
 * playback or system media. COM and FFmpeg filters stay on one owned thread.
 * Supports automatic degraded muted wall-clock fallback when no audio device is
 * present, seamless reconnect on device hot-plug / invalidation, dynamic input
 * format changes, seekless rate changes, and volume ramping.
 * No parent lock is ever acquired, including during pause and destruction. */
class FilmAudio {
public:
    using LogFn = std::function<void(int level, const char *component, const char *message)>;
    using InterruptFn = std::function<bool()>;

    struct Snapshot {
        double position = 0, buffered_until = 0;
        bool has_clock = false, drained = false, degraded = false;
        int error = MFK_OK;
        uint64_t underruns = 0, reconnects = 0;
    };

    FilmAudio(const std::atomic<bool> &cancelled, InterruptFn interrupted = {}, LogFn logger = {});
    ~FilmAudio();

    int open(const AVCodecContext *codec, double rate);
    int write(const AVFrame *frame, double pts);
    int pause(bool paused);
    /* Changes playback speed without dropping buffered audio or seeking. */
    int set_rate(double rate);
    /* Stops and clears device, queue, filter delay and clock for seek. */
    int reset(double position);
    void interrupt_write();
    void set_volume(double volume);
    void set_muted(bool muted);
    void finish();

    Snapshot snapshot() const;
    bool drained() const;
private:
    struct Impl;
    std::unique_ptr<Impl> impl;
};
#endif
