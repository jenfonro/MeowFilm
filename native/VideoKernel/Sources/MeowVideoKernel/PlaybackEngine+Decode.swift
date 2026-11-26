import Foundation
import CMeowVideoFFmpeg

extension PlaybackEngine {
    func videoDecodeLoop() {
        var lastSerial = -1
        var lastSkippedSeekFrame: DecodedVideoFrame?
        var previewFlushedForSerial = false

        while !interruptBox.isAborted {
            var eof = false
            var eofSerial = 0
            guard let entry = videoPackets.pop(reachedEOF: &eof, eofSerial: &eofSerial) else {
                if interruptBox.isAborted { break }
                if eof {
                    guard currentVideoSerial == eofSerial else { continue }
                    var drained: [DecodedVideoFrame] = []
                    pthread_mutex_lock(&videoDecoderLock)
                    if currentVideoSerial == eofSerial {
                        videoDecoder.drainEOF(packetSerial: eofSerial) { drained.append($0) }
                    }
                    pthread_mutex_unlock(&videoDecoderLock)

                    emitVideoFramesWithSeekFilter(drained, serial: eofSerial, lastSkipped: &lastSkippedSeekFrame)
                    if let fallbackFrame = lastSkippedSeekFrame, fallbackFrame.serial == eofSerial {
                        lastSkippedSeekFrame = nil
                        pushDecodedFrame(fallbackFrame)
                    }

                    pthread_mutex_lock(&frameMutex)
                    if currentVideoSerial == eofSerial {
                        videoDecodeEOF = true
                        pthread_cond_broadcast(&frameNotEmpty)
                    }
                    pthread_mutex_unlock(&frameMutex)

                    // Park until aborted or videoSerial changes (seek / video switch).
                    pthread_mutex_lock(&stateMutex)
                    while !interruptBox.isAborted && videoSerial == eofSerial {
                        pthread_cond_wait(&stateCond, &stateMutex)
                    }
                    pthread_mutex_unlock(&stateMutex)
                }
                continue
            }
            defer {
                var mutable: UnsafeMutablePointer<AVPacket>? = entry.packet
                av_packet_free(&mutable)
            }
            if entry.serial != lastSerial {
                pthread_mutex_lock(&videoDecoderLock)
                if currentVideoSerial == entry.serial {
                    videoDecoder.flush(newSerial: entry.serial)
                }
                pthread_mutex_unlock(&videoDecoderLock)
                lastSkippedSeekFrame = nil
                previewFlushedForSerial = false
                lastSerial = entry.serial
            }
            let (streamPar, vTB): (UnsafePointer<AVCodecParameters>?, Double) = {
                pthread_mutex_lock(&stateMutex)
                let idx = videoStreamIndex
                pthread_mutex_unlock(&stateMutex)
                guard let ctx = fmtCtx, idx >= 0, idx < Int32(ctx.pointee.nb_streams),
                      let st = ctx.pointee.streams[Int(idx)] else { return (nil, 1.0 / 90_000.0) }
                let tb = st.pointee.time_base.den > 0 ? av_q2d(st.pointee.time_base) : 1.0 / 90_000.0
                return (UnsafePointer(st.pointee.codecpar), tb)
            }()

            var decodedBatch: [DecodedVideoFrame] = []
            pthread_mutex_lock(&videoDecoderLock)
            guard currentVideoSerial == entry.serial else {
                pthread_mutex_unlock(&videoDecoderLock)
                continue
            }
            let ok = videoDecoder.decode(packet: entry.packet, packetSerial: entry.serial, streamPar: streamPar) {
                decodedBatch.append($0)
            }
            pthread_mutex_unlock(&videoDecoderLock)
            if !ok {
                reportFatal(.decodeFailed)
                break
            }

            if emitVideoFramesWithSeekFilter(decodedBatch, serial: entry.serial, lastSkipped: &lastSkippedSeekFrame) > 0 {
                previewFlushedForSerial = true
            }

            // Flush earliest frame for preview once after crossing the seek target so exact seek
            // doesn't synchronously stall VideoToolbox on every subsequent packet.
            if !previewFlushedForSerial {
                pthread_mutex_lock(&stateMutex)
                let needPreview = !firstFramePreviewDone && !firstFrameInFlight
                let seekTarget = (videoSerial == entry.serial) ? videoSeekTargetPts : nil
                pthread_mutex_unlock(&stateMutex)
                if needPreview {
                    let rawPts = entry.packet.pointee.pts != mvk_av_nopts_value()
                        ? entry.packet.pointee.pts : entry.packet.pointee.dts
                    let pktSec = rawPts != mvk_av_nopts_value() ? Double(rawPts) * vTB - startTimeSeconds : 0
                    let allowPreviewFlush = seekTarget == nil || pktSec >= (seekTarget! - 0.05)
                    if allowPreviewFlush {
                        var previewBatch: [DecodedVideoFrame] = []
                        pthread_mutex_lock(&videoDecoderLock)
                        if currentVideoSerial == entry.serial {
                            videoDecoder.flushPreviewFrames(packetSerial: entry.serial) {
                                previewBatch.append($0)
                            }
                        }
                        pthread_mutex_unlock(&videoDecoderLock)
                        if emitVideoFramesWithSeekFilter(previewBatch, serial: entry.serial, lastSkipped: &lastSkippedSeekFrame) > 0 {
                            previewFlushedForSerial = true
                        }
                    }
                }
            }
        }
    }

    @discardableResult
    private func emitVideoFramesWithSeekFilter(_ frames: [DecodedVideoFrame], serial: Int,
                                               lastSkipped: inout DecodedVideoFrame?) -> Int {
        var emitted = 0
        for frame in frames {
            pthread_mutex_lock(&stateMutex)
            let target = (self.videoSerial == serial) ? videoSeekTargetPts : nil
            pthread_mutex_unlock(&stateMutex)

            if let target {
                let frameEnd = frame.pts + max(frame.duration, 0.04)
                if frameEnd < target - 0.01 {
                    lastSkipped = frame
                    continue
                }
                pthread_mutex_lock(&stateMutex)
                if self.videoSerial == serial {
                    videoSeekTargetPts = nil
                }
                pthread_mutex_unlock(&stateMutex)
                lastSkipped = nil
            }
            pushDecodedFrame(frame)
            emitted += 1
        }
        return emitted
    }

    func audioDecodeLoop() {
        guard let frame = av_frame_alloc() else { return }
        defer {
            var f: UnsafeMutablePointer<AVFrame>? = frame
            av_frame_free(&f)
        }
        var lastSerial = -1
        var outScratch: [Float] = []

        while !interruptBox.isAborted {
            var eof = false
            var eofSerial = 0
            guard let entry = audioPackets.pop(reachedEOF: &eof, eofSerial: &eofSerial) else {
                if interruptBox.isAborted { break }
                if eof {
                    guard currentAudioSerial == eofSerial else { continue }
                    drainAudioEOF(frame: frame, serial: eofSerial, outScratch: &outScratch)
                    if currentAudioSerial == eofSerial {
                        audioOutput.markEOF()
                    }
                    pthread_mutex_lock(&stateMutex)
                    while !interruptBox.isAborted && audioSerial == eofSerial {
                        pthread_cond_wait(&stateCond, &stateMutex)
                    }
                    pthread_mutex_unlock(&stateMutex)
                }
                continue
            }
            defer {
                var mutable: UnsafeMutablePointer<AVPacket>? = entry.packet
                av_packet_free(&mutable)
            }

            let aTB: Double = {
                pthread_mutex_lock(&stateMutex)
                let idx = audioStreamIndex
                pthread_mutex_unlock(&stateMutex)
                guard let fc = fmtCtx, idx >= 0, idx < Int32(fc.pointee.nb_streams),
                      let st = fc.pointee.streams[Int(idx)], st.pointee.time_base.den > 0 else {
                    return 1.0 / 48_000.0
                }
                return av_q2d(st.pointee.time_base)
            }()

            pthread_mutex_lock(&audioDecoderLock)
            guard currentAudioSerial == entry.serial, let ctx = audioCodecCtx else {
                pthread_mutex_unlock(&audioDecoderLock)
                continue
            }
            if entry.serial != lastSerial {
                avcodec_flush_buffers(ctx)
                lastSerial = entry.serial
            }
            let sendRet = avcodec_send_packet(ctx, entry.packet)
            pthread_mutex_unlock(&audioDecoderLock)

            if sendRet < 0 && sendRet != mvk_averror_eagain() && sendRet != mvk_averror_invaliddata() {
                continue
            }

            receiveAndPushAudioFrames(frame: frame, timeBase: aTB, serial: entry.serial, outScratch: &outScratch)
        }
    }

    private func drainAudioEOF(frame: UnsafeMutablePointer<AVFrame>, serial: Int, outScratch: inout [Float]) {
        let aTB: Double = {
            pthread_mutex_lock(&stateMutex)
            let idx = audioStreamIndex
            pthread_mutex_unlock(&stateMutex)
            guard let fc = fmtCtx, idx >= 0, idx < Int32(fc.pointee.nb_streams),
                  let st = fc.pointee.streams[Int(idx)], st.pointee.time_base.den > 0 else {
                return 1.0 / 48_000.0
            }
            return av_q2d(st.pointee.time_base)
        }()

        pthread_mutex_lock(&audioDecoderLock)
        if currentAudioSerial == serial, let ctx = audioCodecCtx {
            _ = avcodec_send_packet(ctx, nil)
        }
        pthread_mutex_unlock(&audioDecoderLock)
        receiveAndPushAudioFrames(frame: frame, timeBase: aTB, serial: serial, outScratch: &outScratch)

        // Flush swresample internal tail buffer.
        pthread_mutex_lock(&audioDecoderLock)
        var tailFrames = 0
        if currentAudioSerial == serial, let swr = swrCtx {
            let maxOut = 2048
            let needed = maxOut * AudioOutputPipeline.outputChannels
            if outScratch.count < needed { outScratch = [Float](repeating: 0, count: needed) }
            tailFrames = outScratch.withUnsafeMutableBufferPointer { buf in
                Int(mvk_swr_convert_frame(swr, buf.baseAddress, Int32(maxOut), nil))
            }
        }
        pthread_mutex_unlock(&audioDecoderLock)
        if tailFrames > 0 {
            writeAudioToRing(outScratch, frameCount: tailFrames, ptsSeconds: nil, serial: serial)
        }
    }

    private func receiveAndPushAudioFrames(frame: UnsafeMutablePointer<AVFrame>, timeBase aTB: Double,
                                           serial: Int, outScratch: inout [Float]) {
        while !interruptBox.isAborted && currentAudioSerial == serial {
            pthread_mutex_lock(&audioDecoderLock)
            guard currentAudioSerial == serial, let ctx = audioCodecCtx else {
                pthread_mutex_unlock(&audioDecoderLock)
                break
            }
            let r = avcodec_receive_frame(ctx, frame)
            if r == mvk_averror_eagain() || r == mvk_averror_eof() {
                pthread_mutex_unlock(&audioDecoderLock)
                break
            }
            if r < 0 {
                pthread_mutex_unlock(&audioDecoderLock)
                continue
            }

            // Dynamically rebuild swresample if mid-stream sample rate / channel layout changes (HE-AAC, HLS variant switch).
            if swrCtx == nil ||
               swrInSampleRate != frame.pointee.sample_rate ||
               swrInChannels != frame.pointee.ch_layout.nb_channels ||
               swrInFormat != frame.pointee.format {
                if swrCtx != nil { swr_free(&swrCtx) }
                swrCtx = mvk_swr_create_from_frame(frame, Int32(AudioOutputPipeline.outputSampleRate))
                swrInSampleRate = frame.pointee.sample_rate
                swrInChannels = frame.pointee.ch_layout.nb_channels
                swrInFormat = frame.pointee.format
            }

            let rawPts = frame.pointee.best_effort_timestamp != mvk_av_nopts_value()
                ? frame.pointee.best_effort_timestamp : frame.pointee.pts
            let ptsSec: Double? = rawPts != mvk_av_nopts_value() ? Double(rawPts) * aTB - startTimeSeconds : nil
            let frameDurSec = frame.pointee.sample_rate > 0
                ? Double(frame.pointee.nb_samples) / Double(frame.pointee.sample_rate) : 0.02

            var convertedFrames = 0
            if let swr = swrCtx {
                let delay = swr_get_delay(swr, Int64(max(1, frame.pointee.sample_rate)))
                let maxOut = Int(av_rescale_rnd(delay + Int64(frame.pointee.nb_samples),
                                                Int64(AudioOutputPipeline.outputSampleRate),
                                                Int64(max(1, frame.pointee.sample_rate)),
                                                AV_ROUND_UP)) + 32
                let neededSamples = maxOut * AudioOutputPipeline.outputChannels
                if outScratch.count < neededSamples {
                    outScratch = [Float](repeating: 0, count: neededSamples)
                }
                convertedFrames = outScratch.withUnsafeMutableBufferPointer { buf in
                    Int(mvk_swr_convert_frame(swr, buf.baseAddress, Int32(maxOut), frame))
                }
            }
            av_frame_unref(frame)
            pthread_mutex_unlock(&audioDecoderLock)

            guard convertedFrames > 0 else { continue }

            // Drop pre-target audio frames during exact seek.
            if let pts = ptsSec {
                pthread_mutex_lock(&stateMutex)
                let target = (self.audioSerial == serial) ? audioSeekTargetPts : nil
                pthread_mutex_unlock(&stateMutex)
                if let target {
                    if pts + frameDurSec < target - 0.01 {
                        continue
                    }
                    pthread_mutex_lock(&stateMutex)
                    if self.audioSerial == serial {
                        audioSeekTargetPts = nil
                    }
                    pthread_mutex_unlock(&stateMutex)
                }
            }

            writeAudioToRing(outScratch, frameCount: convertedFrames, ptsSeconds: ptsSec, serial: serial)
        }
    }

    private func writeAudioToRing(_ scratch: [Float], frameCount: Int, ptsSeconds: Double?, serial: Int) {
        var offsetFrames = 0
        while offsetFrames < frameCount && !interruptBox.isAborted && currentAudioSerial == serial {
            let written = scratch.withUnsafeBufferPointer { buf in
                audioOutput.writeInterleavedFloat(
                    buf.baseAddress!.advanced(by: offsetFrames * AudioOutputPipeline.outputChannels),
                    frameCount: frameCount - offsetFrames,
                    ptsSeconds: offsetFrames == 0 ? ptsSeconds : nil,
                    serial: serial
                )
            }
            offsetFrames += written
            if offsetFrames < frameCount {
                waitOnStateCond(seconds: 0.005) { audioSerial == serial }
            }
        }
    }

    func createAudioDecoder(stream: UnsafePointer<AVStream>) -> AudioDecoderBundle? {
        guard let par = stream.pointee.codecpar,
              let codec = avcodec_find_decoder(par.pointee.codec_id),
              let ctx = avcodec_alloc_context3(codec) else { return nil }
        if avcodec_parameters_to_context(ctx, par) < 0 {
            var mutable: UnsafeMutablePointer<AVCodecContext>? = ctx
            avcodec_free_context(&mutable)
            return nil
        }
        ctx.pointee.err_recognition = 0
        if avcodec_open2(ctx, codec, nil) < 0 {
            var mutable: UnsafeMutablePointer<AVCodecContext>? = ctx
            avcodec_free_context(&mutable)
            return nil
        }
        let swr = mvk_swr_create_stereo_float(par, Int32(AudioOutputPipeline.outputSampleRate))
        return AudioDecoderBundle(
            codecCtx: ctx,
            swrCtx: swr,
            inSampleRate: par.pointee.sample_rate,
            inChannels: par.pointee.ch_layout.nb_channels,
            inFormat: par.pointee.format
        )
    }

    func openAudioDecoderLocked(stream: UnsafePointer<AVStream>) -> Bool {
        guard let next = createAudioDecoder(stream: stream) else { return false }
        closeAudioDecoderLocked()
        audioCodecCtx = next.codecCtx
        swrCtx = next.swrCtx
        swrInSampleRate = next.inSampleRate
        swrInChannels = next.inChannels
        swrInFormat = next.inFormat
        return true
    }

    func closeAudioDecoderLocked() {
        if swrCtx != nil { swr_free(&swrCtx) }
        if audioCodecCtx != nil { avcodec_free_context(&audioCodecCtx) }
        swrInSampleRate = 0; swrInChannels = 0; swrInFormat = -1
    }
}
