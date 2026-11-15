using System.Globalization;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace MeowFilm.Media;

// The playback owner serializes Read/Seek/SetRate/Dispose. Cancel has a separate,
// lock-free path so an owner can abort a blocked AVIO callback before joining it.
internal unsafe sealed class AudioDecoder : IDisposable
{
    private const string Demuxers = "aac,ac3,eac3,aiff,amr,ape,asf,dsf,dsdiff,flac,matroska,mov,mp3,ogg,wav,wv";
    private const string Decoders = "aac,aac_latm,ac3,alac,amrnb,amrwb,ape,dca," +
        "dsd_lsbf,dsd_msbf,dsd_lsbf_planar,dsd_msbf_planar,eac3,flac,mp1float,mp2float,mp3float," +
        "opus,vorbis,wavpack,wmav1,wmav2,wmapro,wmalossless,wmavoice," +
        "pcm_alaw,pcm_mulaw,pcm_u8,pcm_s8,pcm_s16le,pcm_s16be,pcm_s24le,pcm_s24be," +
        "pcm_s32le,pcm_s32be,pcm_s64le,pcm_s64be,pcm_f32le,pcm_f32be,pcm_f64le,pcm_f64be," +
        "pcm_u16le,pcm_u16be,pcm_u24le,pcm_u24be,pcm_u32le,pcm_u32be," +
        "pcm_s16le_planar,pcm_s24le_planar,pcm_s32le_planar";
    private static readonly AVRational SampleTimeBase = new() { num = 1, den = 48000 };
    // Native diagnostics can contain input metadata. Keep this callback rooted for
    // the library's process lifetime; never restore stderr logging on disposal.
    private static readonly av_log_set_callback_callback DiscardNativeLog = (_, _, _, _) => { };
    private readonly AudioDecoderInput _input;
    // These delegates must outlive the format/AVIO contexts, including close_input.
    private readonly avio_alloc_context_read_packet _read;
    private readonly avio_alloc_context_seek _seek;
    private readonly AVIOInterruptCB_callback _interrupt;
    private readonly AVFormatContext_io_open _rejectOpen;
    private AVIOContext* _avio;
    private AVFormatContext* _format;
    private AVCodecContext* _codec;
    private AVPacket* _packet;
    private AVFrame* _decoded;
    private AVFrame* _converted;
    private AVFrame* _filtered;
    private SwrContext* _resampler;
    private AVFilterGraph* _graph;
    private AVFilterContext* _source;
    private AVFilterContext* _sink;
    private AVChannelLayout _inputLayout;
    private AVRational _streamTimeBase;
    private AVSampleFormat _inputFormat;
    private int _streamIndex, _inputSampleRate;
    private long _startTimestamp, _dropBefore, _convertedPosition;
    private bool _positionKnown, _requireTimestamp, _drainSent, _decoderEnded, _resamplerEnded, _filterClosed, _ended;
    private bool _convertedReady;
    private float[] _pending = [];
    private int _pendingOffset, _pendingCount, _disposed;
    private string? _error;
    internal int SampleRate => 48000;
    internal int Channels => 2;
    internal double DurationSeconds { get; private set; }
    internal double Rate { get; private set; } = 1;
    internal bool CanSeek => _input.CanSeek;
    internal CancellationToken Revoked => _input.Revoked;
    internal string? LastErrorCode => _input.LastErrorCode ?? Volatile.Read(ref _error);

    private AudioDecoder(AudioDecoderInput input)
    {
        _input = input;
        _read = ReadPacket;
        _seek = SeekInput;
        _interrupt = _ => _input.Interrupted ? 1 : 0;
        _rejectOpen = (_, _, _, _, _) =>
        {
            _input.Fail(PlaybackError.ReadFormatUnsupported);
            return -13; // EACCES; never permit a demuxer to open an external reference.
        };
    }

    internal static Task<AudioDecoder> OpenAsync(Uri uri, IReadOnlyDictionary<string, string> headers,
        CancellationToken token)
    {
        // Do not pass token to Task.Run: even a pre-cancelled call must retire its input.
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            if (uri is null || !uri.IsAbsoluteUri)
                throw new PlaybackException(PlaybackError.UnsupportedUri);
            AudioDecoderInput input;
            if (uri.IsFile)
            {
                if (uri.Query.Length != 0 || uri.Fragment.Length != 0)
                    throw new PlaybackException(PlaybackError.UnsupportedUri);
                if (headers is null || headers.Count != 0)
                    throw new PlaybackException(PlaybackError.UnsupportedHeaders);
                try { input = new(new AudioDecoderFileLease(uri.LocalPath), token); }
                catch (Exception error) when (error is not PlaybackException and not OperationCanceledException)
                { throw new PlaybackException(PlaybackError.OpenFailed); }
            }
            else input = new(uri, headers, token);
            return OpenOwned(input, uri, token);
        });
    }

    internal static Task<AudioDecoder> OpenReadAsync(INativeMediaReadLease lease, CancellationToken token)
    {
        if (lease is null)
            return Task.FromException<AudioDecoder>(new PlaybackException(PlaybackError.InvalidReadLease));
        var input = new AudioDecoderInput(lease, token); // Transfer ownership before queuing work.
        return Task.Run(() => OpenOwned(input, null, token));
    }

    private static AudioDecoder OpenOwned(AudioDecoderInput input, Uri? address, CancellationToken token)
    {
        var decoder = new AudioDecoder(input);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var abort = deadline.Token.Register(decoder.Cancel);
        try
        {
            input.CheckAvailable();
            AudioNativeLibrary.EnsureLoaded();
            ffmpeg.av_log_set_callback(DiscardNativeLog);
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
            input.Open(address);
            decoder.Initialize();
            // Confirm real PCM, but do not feed a tempo filter until Read. The owner
            // may select a rate before first output without seeking a non-Range stream.
            decoder.Prime();
            input.CheckAvailable();
            return decoder;
        }
        catch (Exception error)
        {
            string? code = decoder.LastErrorCode;
            decoder.Dispose();
            if (code is not null) throw new PlaybackException(code);
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            if (deadline.IsCancellationRequested) throw new PlaybackException(PlaybackError.HttpDeadline);
            if (error is PlaybackException) throw;
            throw new PlaybackException(error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
                ? PlaybackError.NativeUnavailable : PlaybackError.OpenFailed);
        }
    }

    private void Initialize()
    {
        _format = ffmpeg.avformat_alloc_context();
        if (_format == null) throw new PlaybackException(PlaybackError.NativeUnavailable);
        byte* buffer = (byte*)ffmpeg.av_malloc(FFmpegHttpTransport.BufferCapacity);
        if (buffer == null) throw new PlaybackException(PlaybackError.NativeUnavailable);
        _avio = ffmpeg.avio_alloc_context(buffer, FFmpegHttpTransport.BufferCapacity, 0, null,
            _read, default, _seek);
        if (_avio == null)
        {
            ffmpeg.av_free(buffer);
            throw new PlaybackException(PlaybackError.NativeUnavailable);
        }
        _avio->seekable = _input.CanSeek ? ffmpeg.AVIO_SEEKABLE_NORMAL : 0;
        _format->pb = _avio;
        _format->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;
        _format->interrupt_callback = new() { callback = _interrupt };
        _format->io_open = _rejectOpen;
        _format->probesize = 4 * 1024 * 1024;
        _format->max_analyze_duration = 5 * ffmpeg.AV_TIME_BASE;
        _format->error_recognition = ffmpeg.AV_EF_CRCCHECK | ffmpeg.AV_EF_BITSTREAM |
            ffmpeg.AV_EF_BUFFER | ffmpeg.AV_EF_EXPLODE;
        Check(ffmpeg.av_opt_set(_format, "format_whitelist", Demuxers, 0));
        Check(ffmpeg.av_opt_set(_format, "codec_whitelist", Decoders, 0));
        Check(ffmpeg.av_opt_set(_format, "protocol_whitelist", "none", 0));
        var format = _format;
        int result = ffmpeg.avformat_open_input(&format, "", null, null);
        _format = format; // FFmpeg may free and null the context on a rejected open.
        Check(result, PlaybackError.ReadFormatUnsupported);
        Check(ffmpeg.avformat_find_stream_info(_format, null), PlaybackError.ReadFormatUnsupported);
        _streamIndex = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
        Check(_streamIndex, PlaybackError.ReadFormatUnsupported);
        AVStream* stream = _format->streams[_streamIndex];
        AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec == null || !Decoders.Split(',').Contains(Marshal.PtrToStringUTF8((IntPtr)codec->name) ?? "",
            StringComparer.Ordinal))
            throw new PlaybackException(PlaybackError.ReadFormatUnsupported);
        _codec = ffmpeg.avcodec_alloc_context3(codec);
        if (_codec == null) throw new PlaybackException(PlaybackError.NativeUnavailable);
        Check(ffmpeg.avcodec_parameters_to_context(_codec, stream->codecpar));
        _codec->pkt_timebase = stream->time_base;
        _codec->thread_count = 1;
        _codec->err_recognition = _format->error_recognition;
        Check(ffmpeg.avcodec_open2(_codec, codec, null), PlaybackError.ReadFormatUnsupported);
        _streamTimeBase = stream->time_base;
        if (_streamTimeBase.num <= 0 || _streamTimeBase.den <= 0)
            throw new PlaybackException(PlaybackError.ReadFailed);
        _startTimestamp = stream->start_time != ffmpeg.AV_NOPTS_VALUE ? stream->start_time :
            _format->start_time != ffmpeg.AV_NOPTS_VALUE
                ? ffmpeg.av_rescale_q(_format->start_time, new() { num = 1, den = ffmpeg.AV_TIME_BASE }, _streamTimeBase) : 0;
        double duration = stream->duration != ffmpeg.AV_NOPTS_VALUE && stream->duration > 0
            ? stream->duration * (double)_streamTimeBase.num / _streamTimeBase.den
            : _format->duration > 0 && _format->duration != ffmpeg.AV_NOPTS_VALUE
                ? _format->duration / (double)ffmpeg.AV_TIME_BASE : 0;
        DurationSeconds = double.IsFinite(duration) && duration > 0 ? duration : 0;
        for (uint i = 0; i < _format->nb_streams; i++)
            if (i != _streamIndex) _format->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
        _packet = ffmpeg.av_packet_alloc();
        _decoded = ffmpeg.av_frame_alloc();
        _converted = ffmpeg.av_frame_alloc();
        _filtered = ffmpeg.av_frame_alloc();
        if (_packet == null || _decoded == null || _converted == null || _filtered == null)
            throw new PlaybackException(PlaybackError.NativeUnavailable);
        CreateFilter();
    }

    internal int Read(float[] buffer, int offset, int count, CancellationToken token = default)
    {
        if (buffer is null || offset < 0 || count < 0 || offset > buffer.Length - count)
            throw new PlaybackException(PlaybackError.InvalidRequest);
        _input.OperationToken = token;
        try
        {
            CheckAvailable();
            int copied = 0;
            while (copied < count)
            {
                if (_pendingCount == 0 && !FillPending()) break;
                int take = Math.Min(_pendingCount, count - copied);
                Array.Copy(_pending, _pendingOffset, buffer, offset + copied, take);
                _pendingOffset += take;
                _pendingCount -= take;
                copied += take;
            }
            CheckAvailable(); // Never deliver queued PCM after authorization loss.
            return copied;
        }
        catch (PlaybackException error) { RecordError(error.Code); throw; }
        finally { _input.OperationToken = default; }
    }

    private bool FillPending()
    {
        if (_convertedReady)
        {
            PushConverted();
            _convertedReady = false;
        }
        while (!_ended)
        {
            CheckAvailable();
            int result = ffmpeg.av_buffersink_get_frame(_sink, _filtered);
            if (result >= 0)
            {
                try
                {
                    if (_filtered->nb_samples == 0) continue;
                    if (_filtered->format != (int)AVSampleFormat.AV_SAMPLE_FMT_FLT ||
                        _filtered->sample_rate != SampleRate || _filtered->ch_layout.nb_channels != Channels)
                        throw new PlaybackException(PlaybackError.ReadFailed);
                    _pendingCount = checked(_filtered->nb_samples * Channels);
                    _pendingOffset = 0;
                    if (_pending.Length < _pendingCount) _pending = new float[_pendingCount];
                    Marshal.Copy((IntPtr)_filtered->data[0], _pending, 0, _pendingCount);
                    for (int i = 0; i < _pendingCount; i++)
                        if (!float.IsFinite(_pending[i])) throw new PlaybackException(PlaybackError.ReadFailed);
                    return true;
                }
                finally { ffmpeg.av_frame_unref(_filtered); }
            }
            if (result == ffmpeg.AVERROR_EOF) { _ended = true; return false; }
            if (result != ffmpeg.AVERROR(ffmpeg.EAGAIN)) Check(result);
            if (!_decoderEnded && DecodeFrame())
            {
                try { ConvertFrame(_decoded); }
                finally { ffmpeg.av_frame_unref(_decoded); }
                continue;
            }
            if (!_resamplerEnded)
            {
                if (_resampler != null && ConvertFrame(null) > 0) continue;
                _resamplerEnded = true;
            }
            if (_filterClosed) throw new PlaybackException(PlaybackError.ReadFailed);
            Check(ffmpeg.av_buffersrc_add_frame_flags(_source, null, 0));
            _filterClosed = true;
        }
        return false;
    }

    private void Prime()
    {
        while (true)
        {
            if (DecodeFrame())
            {
                try { ConvertFrame(_decoded, submit: false); }
                finally { ffmpeg.av_frame_unref(_decoded); }
            }
            else
            {
                if (_resampler == null) throw new PlaybackException(PlaybackError.ReadFailed);
                ConvertFrame(null, submit: false);
            }
            if (_converted->nb_samples > 0) { _convertedReady = true; return; }
            if (_decoderEnded) throw new PlaybackException(PlaybackError.ReadFailed);
        }
    }

    private bool DecodeFrame()
    {
        while (true)
        {
            CheckAvailable();
            int result = ffmpeg.avcodec_receive_frame(_codec, _decoded);
            if (result >= 0)
            {
                if ((_decoded->flags & ffmpeg.AV_FRAME_FLAG_CORRUPT) != 0 || _decoded->decode_error_flags != 0)
                    throw new PlaybackException(PlaybackError.ReadFailed);
                return true;
            }
            if (result == ffmpeg.AVERROR_EOF) { _decoderEnded = true; return false; }
            if (result != ffmpeg.AVERROR(ffmpeg.EAGAIN)) Check(result);
            if (_drainSent) throw new PlaybackException(PlaybackError.ReadFailed);
            do
            {
                ffmpeg.av_packet_unref(_packet);
                result = ffmpeg.av_read_frame(_format, _packet);
                _input.CheckAvailable();
                if (result == ffmpeg.AVERROR_EOF)
                {
                    if (_avio->error < 0 && _avio->error != ffmpeg.AVERROR_EOF) Check(_avio->error);
                    Check(ffmpeg.avcodec_send_packet(_codec, null));
                    _drainSent = true;
                    break;
                }
                Check(result);
            } while (_packet->stream_index != _streamIndex);
            if (_drainSent) continue;
            try
            {
                if ((_packet->flags & ffmpeg.AV_PKT_FLAG_CORRUPT) != 0)
                    throw new PlaybackException(PlaybackError.ReadFailed);
                Check(ffmpeg.avcodec_send_packet(_codec, _packet));
            }
            finally { ffmpeg.av_packet_unref(_packet); }
        }
    }

    private int ConvertFrame(AVFrame* frame, bool submit = true)
    {
        if (frame != null)
        {
            if (frame->sample_rate <= 0 || frame->nb_samples <= 0 ||
                ffmpeg.av_channel_layout_check(&frame->ch_layout) != 1)
                throw new PlaybackException(PlaybackError.ReadFailed);
            if (_resampler == null)
            {
                AVChannelLayout layout = default, stereo = default;
                Check(ffmpeg.av_channel_layout_copy(&layout, &frame->ch_layout));
                _inputLayout = layout;
                _inputSampleRate = frame->sample_rate;
                _inputFormat = (AVSampleFormat)frame->format;
                ffmpeg.av_channel_layout_default(&stereo, Channels);
                SwrContext* resampler = null;
                int result = ffmpeg.swr_alloc_set_opts2(&resampler, &stereo, AVSampleFormat.AV_SAMPLE_FMT_FLT,
                    SampleRate, &layout, _inputFormat, _inputSampleRate, 0, null);
                _resampler = resampler;
                ffmpeg.av_channel_layout_uninit(&stereo);
                Check(result);
                Check(ffmpeg.swr_init(_resampler));
            }
            var expectedLayout = _inputLayout;
            if (frame->sample_rate != _inputSampleRate || frame->format != (int)_inputFormat ||
                ffmpeg.av_channel_layout_compare(&expectedLayout, &frame->ch_layout) != 0)
                throw new PlaybackException(PlaybackError.ReadFormatUnsupported);
            long timestamp = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE
                ? frame->best_effort_timestamp : frame->pts;
            if (timestamp != ffmpeg.AV_NOPTS_VALUE)
            {
                // Resampling can retain samples from the previous frame. Attribute
                // this output block to its actual first timestamp, not the new
                // input frame's timestamp or an accumulated byte/sample estimate.
                _convertedPosition = ffmpeg.av_rescale_q(timestamp - _startTimestamp,
                    _streamTimeBase, SampleTimeBase) - ffmpeg.swr_get_delay(_resampler, SampleRate);
                _positionKnown = true;
            }
            else if (!_positionKnown)
            {
                if (_requireTimestamp) throw new PlaybackException(PlaybackError.ReadSeekFailed);
                _convertedPosition = 0;
                _positionKnown = true;
            }
        }
        ffmpeg.av_frame_unref(_converted);
        _converted->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLT;
        _converted->sample_rate = SampleRate;
        ffmpeg.av_channel_layout_default(&_converted->ch_layout, Channels);
        int capacity = ffmpeg.swr_get_out_samples(_resampler, frame == null ? 0 : frame->nb_samples);
        Check(capacity);
        _converted->nb_samples = Math.Max(1, capacity);
        Check(ffmpeg.av_frame_get_buffer(_converted, 0));
        // swr_init canonicalizes unspecified input layouts. Pass the already
        // validated samples, not a frame-layout comparison against that rewrite.
        int samples = ffmpeg.swr_convert(_resampler, _converted->extended_data, capacity,
            frame == null ? null : frame->extended_data, frame == null ? 0 : frame->nb_samples);
        Check(samples);
        _converted->nb_samples = samples;
        _converted->pts = _convertedPosition;
        _convertedPosition += samples;
        int skip = (int)Math.Clamp(_dropBefore - _converted->pts, 0, samples);
        // Trim in output sample units, before atempo sees the frame. FFmpeg may seek
        // to an earlier compressed packet; no pre-seek PCM may leak to the output.
        byte* first = _converted->data[0] + skip * Channels * sizeof(float);
        _converted->data[0] = first;
        _converted->extended_data[0] = first;
        _converted->nb_samples -= skip;
        _converted->pts += skip;
        if (submit && _converted->nb_samples > 0) PushConverted();
        return samples;
    }

    private void PushConverted() =>
        Check(ffmpeg.av_buffersrc_add_frame_flags(_source, _converted, (int)AvBuffersrcFlag.AV_BUFFERSRC_FLAG_KEEP_REF));

    private void CreateFilter()
    {
        _graph = ffmpeg.avfilter_graph_alloc();
        if (_graph == null) throw new PlaybackException(PlaybackError.NativeUnavailable);
        _graph->nb_threads = 1;
        _source = AddFilter("abuffer", "input", "time_base=1/48000:sample_rate=48000:sample_fmt=flt:channel_layout=stereo");
        AVFilterContext* last = _source;
        if (Rate != 1)
        {
            // Both stages stay within 0.5..2.0; a single 3x atempo can skip samples.
            string tempo = Math.Sqrt(Rate).ToString("R", CultureInfo.InvariantCulture);
            AVFilterContext* tempo1 = AddFilter("atempo", "tempo1", "tempo=" + tempo);
            AVFilterContext* tempo2 = AddFilter("atempo", "tempo2", "tempo=" + tempo);
            Check(ffmpeg.avfilter_link(last, 0, tempo1, 0));
            Check(ffmpeg.avfilter_link(tempo1, 0, tempo2, 0));
            last = tempo2;
        }
        AVFilterContext* format = AddFilter("aformat", "format",
            "sample_fmts=flt:sample_rates=48000:channel_layouts=stereo");
        _sink = AddFilter("abuffersink", "output", "");
        Check(ffmpeg.avfilter_link(last, 0, format, 0));
        Check(ffmpeg.avfilter_link(format, 0, _sink, 0));
        Check(ffmpeg.avfilter_graph_config(_graph, null));
    }

    private AVFilterContext* AddFilter(string type, string name, string arguments)
    {
        AVFilter* filter = ffmpeg.avfilter_get_by_name(type);
        if (filter == null) throw new PlaybackException(PlaybackError.NativeUnavailable);
        AVFilterContext* context = null;
        Check(ffmpeg.avfilter_graph_create_filter(&context, filter, name, arguments, null, _graph));
        return context;
    }

    internal void Seek(double seconds, CancellationToken token = default)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds >= long.MaxValue / (double)SampleRate)
            throw new PlaybackException(PlaybackError.InvalidSeek);
        if (!_input.CanSeek) throw new PlaybackException(PlaybackError.NotSeekable);
        _input.OperationToken = token;
        try
        {
            CheckAvailable();
            long sample = (long)Math.Ceiling(seconds * SampleRate);
            long timestamp = checked(_startTimestamp + ffmpeg.av_rescale_q(sample, SampleTimeBase, _streamTimeBase));
            // A cancelled producer read is not a cancelled input lifetime. Clear
            // AVIO's sticky interrupt result before the owner's explicit seek.
            _avio->error = 0;
            _avio->eof_reached = 0;
            Check(ffmpeg.av_seek_frame(_format, _streamIndex, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD),
                PlaybackError.NotSeekable);
            ffmpeg.avcodec_flush_buffers(_codec);
            ffmpeg.av_packet_unref(_packet);
            ffmpeg.av_frame_unref(_decoded);
            ffmpeg.av_frame_unref(_converted);
            ffmpeg.av_frame_unref(_filtered);
            FreeResampler();
            FreeFilter();
            _pendingOffset = _pendingCount = 0;
            _convertedReady = false;
            _dropBefore = sample;
            _positionKnown = false;
            _requireTimestamp = sample > 0;
            _drainSent = _decoderEnded = _resamplerEnded = _filterClosed = _ended = false;
            CreateFilter();
            CheckAvailable();
        }
        catch (PlaybackException error) { RecordError(error.Code); throw; }
        finally { _input.OperationToken = default; }
    }

    internal void SetRate(double rate)
    {
        CheckAvailable();
        if (!double.IsFinite(rate) || rate < .5 || rate > 3)
            throw new PlaybackException(PlaybackError.InvalidRate);
        if (rate == Rate) return;
        // On an active decoder the owner follows this with Seek to the actual output
        // position. Before first Read, the primed PCM has not entered the old graph.
        try
        {
            Rate = rate;
            FreeFilter();
            CreateFilter();
            _pendingOffset = _pendingCount = 0;
            _filterClosed = _ended = false;
        }
        catch (PlaybackException error) { RecordError(error.Code); throw; }
    }

    private int ReadPacket(void* opaque, byte* buffer, int count)
    {
        try
        {
            int read = _input.Read((IntPtr)buffer, count);
            return read == 0 ? ffmpeg.AVERROR_EOF : read;
        }
        catch { return _input.Interrupted ? ffmpeg.AVERROR_EXIT : -5; }
    }

    private long SeekInput(void* opaque, long offset, int whence)
    {
        try
        {
            _input.CheckAvailable();
            if ((whence & ffmpeg.AVSEEK_SIZE) != 0) return _input.Length >= 0 ? _input.Length : -38;
            if (!_input.CanSeek) return -38; // ENOSYS is a capability answer, not an input failure.
            return _input.Seek(offset, whence & ~ffmpeg.AVSEEK_FORCE);
        }
        catch { return _input.Interrupted ? ffmpeg.AVERROR_EXIT : -5; }
    }

    private void Check(int result, string code = PlaybackError.ReadFailed)
    {
        _input.CheckAvailable();
        if (result < 0) throw new PlaybackException(code);
    }

    internal void CheckAvailable()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new PlaybackException(PlaybackError.Disposed);
        if (LastErrorCode is { } code) throw new PlaybackException(code);
        _input.CheckAvailable();
    }

    private void RecordError(string code) => Interlocked.CompareExchange(ref _error, code, null);
    internal void Cancel() => _input.Cancel();

    private void FreeResampler()
    {
        if (_resampler != null)
        {
            var resampler = _resampler;
            ffmpeg.swr_free(&resampler);
            _resampler = null;
        }
        if (_inputLayout.nb_channels != 0)
        {
            var layout = _inputLayout;
            ffmpeg.av_channel_layout_uninit(&layout);
            _inputLayout = default;
        }
    }

    private void FreeFilter()
    {
        if (_graph != null)
        {
            var graph = _graph;
            ffmpeg.avfilter_graph_free(&graph);
            _graph = null;
        }
        _source = _sink = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Cancel();
        try
        {
            FreeFilter();
            FreeResampler();
            var packet = _packet;
            if (packet != null) ffmpeg.av_packet_free(&packet);
            _packet = null;
            var frame = _decoded;
            if (frame != null) ffmpeg.av_frame_free(&frame);
            _decoded = null;
            frame = _converted;
            if (frame != null) ffmpeg.av_frame_free(&frame);
            _converted = null;
            frame = _filtered;
            if (frame != null) ffmpeg.av_frame_free(&frame);
            _filtered = null;
            var codec = _codec;
            if (codec != null) ffmpeg.avcodec_free_context(&codec);
            _codec = null;
            var format = _format;
            if (format != null) ffmpeg.avformat_close_input(&format);
            _format = null;
            if (_avio != null)
            {
                // avformat_close_input does not free a custom AVIO buffer. FFmpeg
                // can replace the original allocation, so free the current pointer.
                ffmpeg.av_free(_avio->buffer);
                _avio->buffer = null;
                var avio = _avio;
                ffmpeg.avio_context_free(&avio);
                _avio = null;
            }
        }
        finally
        {
            Array.Clear(_pending);
            GC.KeepAlive(_read);
            GC.KeepAlive(_seek);
            GC.KeepAlive(_interrupt);
            GC.KeepAlive(_rejectOpen);
            _input.Dispose();
        }
    }
}
