#ifndef MEOWFILM_FILM_AUDIO_H
#define MEOWFILM_FILM_AUDIO_H

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <deque>
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
    bool observe(uint64_t position)
    {
        if (!frequency || sample_rate <= 0 || position < last_position) return false;
        last_position = position;
        while (!spans.empty()) {
            const auto &span = spans.front();
            const long double elapsed = (static_cast<long double>(position)-span.origin)*
                sample_rate/frequency;
            const auto frames = static_cast<uint64_t>(
                std::clamp(elapsed, 0.0L, static_cast<long double>(span.frames)));
            consumed = (std::max)(consumed, span.first+frames);
            if (frames < span.frames) break;
            spans.pop_front();
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
                origin = (std::max)(origin, tail.origin+
                    static_cast<long double>(tail.frames)*frequency/sample_rate);
            }
            if (spans.empty() && padding)
                origin += static_cast<long double>(padding)*frequency/sample_rate;
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
 * open/write/reset/finish are a single producer's ordered operations; pause,
 * interrupt_write, volume and snapshot access may run concurrently. Callers
 * must stop/join their producer before destroying FilmAudio. No parent callback
 * or parent lock is acquired, including during pause and destruction. */
class FilmAudio {
public:
    struct Snapshot {
        double position = 0, buffered_until = 0;
        bool has_clock = false, drained = false;
        int error = MFK_OK;
    };
    explicit FilmAudio(const std::atomic<bool> &cancelled);
    ~FilmAudio();
    /* Initial open and reopen leave the device paused. Reopen changes format/
     * tempo atomically with discarding old PCM/filter delay/device clock. */
    int open(const AVCodecContext *codec, double rate);
    /* Deep copies accepted PCM with at most two input seconds queued. A write
     * may block while paused. MFK_CANCELLED after interrupt is local to that
     * operation: an accepted prefix remains queued; do not resubmit the frame.
     * reset/open deliberately discard that prefix for seek/rate changes. */
    int write(const AVFrame *frame, double pts);
    int pause(bool paused);
    /* Stops and clears the device, queue, atempo/resampler delay and clock.
     * Samples preceding position in the first post-seek frame are trimmed. */
    int reset(double position);
    /* Wakes a current blocked write, or the next write if the producer is
     * between writes. Does not set the media cancellation flag or error. */
    void interrupt_write();
    void set_volume(double volume);
    /* Nonblocking EOF after all accepted PCM. Caller must first drain its
     * decoder. drained requires filter EOF, device padding == 0 and actual
     * IAudioClock consumption of the tail; poll snapshot().error as well. */
    void finish();
    Snapshot snapshot() const;
    bool drained() const;
    bool has_clock() const;
    double clock() const;
    double buffered_until() const;
    int error() const;
private:
    struct Impl;
    std::unique_ptr<Impl> impl;
};
#endif
