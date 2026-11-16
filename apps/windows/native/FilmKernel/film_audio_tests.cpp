#include "film_audio.h"
#include <cmath>
#include <cstdio>
#include <cstdlib>

/* Standalone executable: on Windows compile this translation unit, not an
 * additional film_audio.cpp, and link the same locked FFmpeg/COM libraries.
 * Default tests need no endpoint. --device additionally exercises WASAPI.
 * On other hosts this only tests the production clock accounting. */
#ifdef _WIN32
#include "film_audio.cpp"
#include <future>
#include <vector>
#endif

namespace {
void check(bool condition, const char *message)
{
    if (!condition) { std::fprintf(stderr, "%s\n", message); std::abort(); }
}
void clock_tests()
{
    film_audio_detail::ConsumptionClock clock;
    clock.reset(48000, 10000000);
    check(!clock.valid && clock.consumed == 0, "clock starts unanchored");
    check(clock.observe(1000), "initial device observation");
    clock.submit(1000, 0, 480);
    check(clock.observe(21000) && clock.consumed == 96, "device frequency is not necessarily sample rate");
    clock.submit(21000, 384, 480);
    check(clock.observe(151000) && clock.consumed == 720, "continuous submissions share an origin");
    check(clock.observe(10000000) && clock.consumed == 960, "underrun stops media consumption");
    clock.submit(10000000, 0, 480);
    check(clock.observe(10010000) && clock.consumed == 1008, "refill excludes underrun silence");
    check(clock.observe(20000000) && clock.consumed == 1440, "tail is consumed once");
    check(clock.observe(20000000) && clock.consumed == 1440, "duplicate polling never duplicates frames");
    check(!clock.observe(19999999), "unreset device clock regression is an error");

    clock.reset(48000, 48000);
    check(!clock.valid && clock.submitted == 0 && clock.observe(0), "seek resets every device origin");
    clock.submit(100, 0, 100);
    check(clock.observe(150) && clock.consumed == 50, "first interval consumption");
    clock.submit(150, 0, 100);
    check(clock.observe(175) && clock.consumed == 75, "zero padding does not truncate the device tail");
    check(clock.observe(225) && clock.consumed == 125, "refill never overlaps an earlier tail");
    check(clock.observe(300) && clock.consumed == 200, "both intervals drain exactly");

    /* Deterministic reference stream: arbitrary polls, adjacent submissions
     * and long empty gaps, including a frequency expressed in bytes/second. */
    for (uint64_t frequency : {48000ULL, 192000ULL, 10000000ULL}) {
        clock.reset(48000, frequency);
        uint64_t device_frame = 0, end = 0, submitted = 0, consumed = 0;
        uint32_t random = 0x514f89u;
        for (int i = 0; i < 5000; ++i) {
            random = random*1664525u+1013904223u;
            const uint64_t step = (random >> 16)%193;
            consumed += std::min(step, end > device_frame ? end-device_frame : 0);
            device_frame += step;
            const uint64_t ticks = device_frame*frequency/48000;
            check(clock.observe(ticks), "simulated clock remains monotonic");
            /* Integer device ticks can quantize consumption by one frame. */
            check(clock.consumed <= consumed && consumed-clock.consumed <= 1,
                  "polling loses or repeats a submitted frame");
            if (random & 1) {
                const auto padding = static_cast<uint32_t>(end > device_frame ? end-device_frame : 0);
                const uint32_t frames = 1+(random >> 8)%257;
                clock.submit(ticks, padding, frames);
                end = std::max(end, device_frame)+frames; submitted += frames;
            }
            check(clock.submitted == submitted, "each submission is recorded exactly once");
        }
        check(clock.observe((end+1)*frequency/48000) && clock.consumed == submitted,
              "all simulated tails drain");
    }
}

#ifdef _WIN32
Frame make_frame(int sample_rate, int samples, AVSampleFormat format, int first = 0, bool silent = false)
{
    Frame frame(av_frame_alloc());
    check(static_cast<bool>(frame), "allocate input frame");
    frame->sample_rate = sample_rate; frame->format = format; frame->nb_samples = samples;
    av_channel_layout_default(&frame->ch_layout, 2);
    check(av_frame_get_buffer(frame.get(), 0) >= 0, "allocate input PCM");
    for (int i = 0; i < samples; ++i) {
        const float value = silent ? 0 : static_cast<float>(
            0.25*std::sin(2*3.14159265358979323846*440*(first+i)/sample_rate));
        for (int channel = 0; channel < 2; ++channel) {
            if (format == AV_SAMPLE_FMT_FLTP)
                reinterpret_cast<float *>(frame->extended_data[channel])[i] = value;
            else reinterpret_cast<float *>(frame->data[0])[i*2+channel] = value;
        }
    }
    return frame;
}

void pull_filter(AudioFilter &filter, std::vector<float> &samples, bool eof)
{
    for (;;) {
        Frame frame(av_frame_alloc());
        check(static_cast<bool>(frame), "allocate filter output");
        const int result = av_buffersink_get_frame(filter.sink, frame.get());
        if (result == AVERROR(EAGAIN) || result == AVERROR_EOF) {
            check((result == AVERROR_EOF) == eof, "filter EOF must drain the actual resampler tail");
            return;
        }
        check(result >= 0 && frame->format == AV_SAMPLE_FMT_FLT &&
              frame->sample_rate == 48000 && frame->ch_layout.nb_channels == 2,
              "aformat converts planar PCM to the packed device format and rate");
        const auto *values = reinterpret_cast<const float *>(frame->data[0]);
        for (int i = 0; i < frame->nb_samples; ++i) samples.push_back(values[i*2]);
    }
}

void filter_tests()
{
    InputFormat input;
    input.sample_rate = 44100; input.format = AV_SAMPLE_FMT_FLTP;
    av_channel_layout_default(&input.layout, 2);
    AudioDevice output;
    output.sample_rate = 48000; output.format = AV_SAMPLE_FMT_FLT;
    av_channel_layout_default(&output.layout, 2);
    AudioFilter filter;
    for (double rate : {0.25, 0.5, 1.0, 2.0, 4.0}) {
        check(filter.open(input, output, rate) == MFK_OK, "open the production tempo/conversion graph");
        std::vector<float> samples;
        constexpr int total = 88207;
        for (int first = 0; first < total; first += 1024) {
            auto frame = make_frame(input.sample_rate, std::min(1024, total-first), input.format, first);
            frame->pts = first;
            check(av_buffersrc_add_frame_flags(filter.source, frame.get(), AV_BUFFERSRC_FLAG_KEEP_REF) >= 0,
                  "feed the production tempo graph");
            pull_filter(filter, samples, false);
        }
        const size_t before_eof = samples.size();
        check(av_buffersrc_add_frame_flags(filter.source, nullptr, 0) >= 0, "finish the filter source");
        pull_filter(filter, samples, true);
        check(samples.size() > before_eof, "Finish retains the delayed atempo/resampler samples");
        const double expected = static_cast<double>(total)*48000/input.sample_rate/rate;
        check(std::abs(static_cast<double>(samples.size())-expected) < 0.08*48000/rate,
              "tempo output duration follows the requested rate");
        if (rate == 1)
            check(std::abs(static_cast<double>(samples.size())-expected) <= 1,
                  "unity rate loses or duplicates no resampled frames");
        const size_t begin = samples.size()/5, end = samples.size()*4/5;
        int crossings = 0;
        for (size_t i = begin+1; i < end; ++i)
            if (samples[i-1] <= 0 && samples[i] > 0) ++crossings;
        const double pitch = crossings*48000.0/(end-begin);
        check(std::abs(pitch-440) < 6, "atempo preserves pitch at every supported rate");
    }
    /* Reopening a graph must discard both tempo and resampler history. */
    check(filter.open(input, output, 0.5) == MFK_OK, "open delayed graph");
    auto old = make_frame(44100, 4096, AV_SAMPLE_FMT_FLTP);
    check(av_buffersrc_add_frame_flags(filter.source, old.get(), AV_BUFFERSRC_FLAG_KEEP_REF) >= 0,
          "prime old filter delay");
    check(filter.open(input, output, 1) == MFK_OK, "reset the production graph");
    auto silence = make_frame(44100, 4410, AV_SAMPLE_FMT_FLTP, 0, true);
    check(av_buffersrc_add_frame_flags(filter.source, silence.get(), AV_BUFFERSRC_FLAG_KEEP_REF) >= 0,
          "feed post-seek silence");
    check(av_buffersrc_add_frame_flags(filter.source, nullptr, 0) >= 0, "finish post-seek filter");
    std::vector<float> samples;
    pull_filter(filter, samples, true);
    for (float value : samples) check(value == 0, "seek leaks no old filter samples");
}

template<class Predicate> void await(Predicate predicate, const char *message)
{
    const auto deadline = std::chrono::steady_clock::now()+std::chrono::seconds(6);
    while (!predicate()) {
        check(std::chrono::steady_clock::now() < deadline, message);
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    }
}

void device_tests()
{
    std::atomic<bool> cancelled{false};
    AVCodecContext *codec = avcodec_alloc_context3(nullptr);
    check(codec != nullptr, "allocate codec format");
    codec->sample_rate = 48000; codec->sample_fmt = AV_SAMPLE_FMT_FLT;
    av_channel_layout_default(&codec->ch_layout, 2);
    auto audio = std::make_unique<FilmAudio>(cancelled);
    check(audio->open(codec, 1) == MFK_OK, "open the actual WASAPI endpoint");
    audio->set_volume(0);
    auto frame = make_frame(48000, 9600, AV_SAMPLE_FMT_FLT);
    check(audio->write(frame.get(), 0) == MFK_OK, "write while initially paused");
    check(audio->pause(true) == MFK_OK, "acknowledge pause without a parent callback");
    const double paused = audio->clock();
    std::this_thread::sleep_for(std::chrono::milliseconds(60));
    check(audio->clock() == paused, "a stopped device consumes no media");
    check(audio->pause(false) == MFK_OK, "resume the device");
    await([&] { return audio->clock() >= 0.06 || audio->error(); }, "device clock progresses");
    check(audio->pause(true) == MFK_OK, "pause running device");
    const double stopped = audio->clock();
    std::this_thread::sleep_for(std::chrono::milliseconds(60));
    check(audio->clock() == stopped, "pause returns the final stable consumed clock");
    audio->finish();
    check(audio->pause(false) == MFK_OK, "resume before draining");
    await([&] { return audio->drained() || audio->error(); }, "Finish drains device and filter tails");
    check(audio->error() == MFK_OK && audio->clock() == 0.2, "final media position is the accepted endpoint");

    check(audio->reset(3) == MFK_OK, "reset device and filter clock");
    frame = make_frame(48000, 4800, AV_SAMPLE_FMT_FLT);
    check(audio->write(frame.get(), 3) == MFK_OK && audio->pause(false) == MFK_OK, "play a short interval");
    await([&] { return audio->clock() >= 3.099 || audio->error(); }, "first interval consumed");
    std::this_thread::sleep_for(std::chrono::milliseconds(200));
    check(audio->clock() <= 3.1 && !audio->drained(), "underrun time is not media time or EOF");
    frame = make_frame(48000, 14400, AV_SAMPLE_FMT_FLT);
    check(audio->write(frame.get(), 3.1) == MFK_OK, "refill an underrun");
    std::this_thread::sleep_for(std::chrono::milliseconds(50));
    check(audio->clock() < 3.25, "refill cannot immediately consume the underrun gap");
    audio->finish();
    await([&] { return audio->drained() || audio->error(); }, "refilled interval drains");
    check(audio->error() == MFK_OK && std::abs(audio->clock()-3.4) < 0.000001, "refill consumes each frame once");

    check(audio->reset(10) == MFK_OK, "seek resets clock and old queue");
    auto snapshot = audio->snapshot();
    check(!snapshot.has_clock && !snapshot.drained && snapshot.position == 10 &&
          snapshot.buffered_until == 10, "seek snapshot has no old clock/filter/EOF state");
    frame = make_frame(48000, 1920, AV_SAMPLE_FMT_FLT);
    check(audio->write(frame.get(), 9.99) == MFK_OK, "seek trims the overlapping source frame");
    audio->finish();
    check(audio->pause(false) == MFK_OK, "resume trimmed seek");
    await([&] { return audio->drained() || audio->error(); }, "trimmed seek tail drains");
    check(audio->error() == MFK_OK && std::abs(audio->clock()-10.03) < 0.000001,
          "seek starts at the requested source sample");

    check(audio->open(codec, 4) == MFK_OK && audio->reset(0) == MFK_OK, "reopen tempo clears old state");
    auto large = make_frame(48000, 480000, AV_SAMPLE_FMT_FLT);
    auto writing = std::async(std::launch::async, [&] { return audio->write(large.get(), 0); });
    check(writing.wait_for(std::chrono::milliseconds(150)) == std::future_status::timeout,
          "paused writes have bounded backpressure");
    audio->interrupt_write();
    check(writing.wait_for(std::chrono::seconds(1)) == std::future_status::ready &&
          writing.get() == MFK_CANCELLED, "interrupt wakes only the queued write");
    check(audio->error() == MFK_OK && !cancelled.load(), "local write interruption does not cancel media");
    const double accepted = audio->buffered_until();
    check(accepted > 0 && accepted < 3, "accepted PCM remains bounded after interrupt");
    audio->finish();
    check(audio->pause(false) == MFK_OK, "play the accepted interrupted prefix");
    await([&] { return audio->drained() || audio->error(); }, "interrupted prefix retains and drains its tail");
    check(audio->error() == MFK_OK && audio->clock() == accepted, "interrupt does not lose accepted PCM");

    check(audio->reset(0) == MFK_OK, "reset before cancellation");
    writing = std::async(std::launch::async, [&] { return audio->write(large.get(), 0); });
    check(writing.wait_for(std::chrono::milliseconds(150)) == std::future_status::timeout,
          "cancel test reaches backpressure");
    cancelled.store(true);
    check(writing.wait_for(std::chrono::seconds(1)) == std::future_status::ready &&
          writing.get() == MFK_CANCELLED, "media cancellation wakes a producer without device events");
    const auto before = std::chrono::steady_clock::now();
    audio.reset();
    check(std::chrono::steady_clock::now()-before < std::chrono::seconds(1),
          "destruction joins the cancelled device thread promptly");
    cancelled.store(false);
    audio = std::make_unique<FilmAudio>(cancelled);
    check(audio->open(codec, 1) == MFK_OK, "reopen before idle destruction");
    const auto idle = std::chrono::steady_clock::now();
    audio.reset();
    check(std::chrono::steady_clock::now()-idle < std::chrono::seconds(1),
          "idle destruction wakes and joins without an external cancellation");
    avcodec_free_context(&codec);
}
#endif
}

int main(int argc, char **argv)
{
    clock_tests();
#ifdef _WIN32
    filter_tests();
    if (argc == 2 && std::strcmp(argv[1], "--device") == 0) device_tests();
    else check(argc == 1, "usage: film_audio_tests [--device]");
    std::puts(argc == 2 ? "film_audio_tests: clock, FFmpeg filter and WASAPI passed" :
                         "film_audio_tests: clock and FFmpeg filter passed");
#else
    check(argc == 1, "FFmpeg filter and device tests require the Windows build");
    (void)argv;
    std::puts("film_audio_tests: clock accounting passed; Windows tests not run");
#endif
}
