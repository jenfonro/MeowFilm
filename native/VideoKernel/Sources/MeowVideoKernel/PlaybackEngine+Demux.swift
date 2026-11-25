import Foundation
import CMeowVideoFFmpeg

extension PlaybackEngine {
    func demuxLoop() {
        guard let ctx = fmtCtx else { return }
        var eofSent = false
        var deferredPacket: (packet: UnsafeMutablePointer<AVPacket>, videoSerial: Int, audioSerial: Int)?
        defer {
            if let pkt = deferredPacket?.packet {
                var mutable: UnsafeMutablePointer<AVPacket>? = pkt
                av_packet_free(&mutable)
            }
        }

        while !interruptBox.isAborted {
            switch handlePendingCommandsOnDemuxThread(ctx: ctx) {
            case .seeked:
                if let pending = deferredPacket {
                    deferredPacket = nil
                    var mutable: UnsafeMutablePointer<AVPacket>? = pending.packet
                    av_packet_free(&mutable)
                }
                eofSent = false
                continue
            case .trackSwitched:
                // Retain deferredPacket for unaffected streams; the deferred check below drops invalidated packets only.
                continue
            case .none:
                break
            }

            if let pending = deferredPacket {
                deferredPacket = nil
                let (curV, curA) = currentSerialsSnapshot()
                let streamIdx = pending.packet.pointee.stream_index
                pthread_mutex_lock(&stateMutex)
                let isV = streamIdx == videoStreamIndex
                let isA = streamIdx == audioStreamIndex
                let isS = streamIdx == subtitleStreamIndex
                pthread_mutex_unlock(&stateMutex)
                if (isV && pending.videoSerial != curV) || (isA && pending.audioSerial != curA) || (!isV && !isA && !isS) {
                    var mutable: UnsafeMutablePointer<AVPacket>? = pending.packet
                    av_packet_free(&mutable)
                    continue
                }
                if !routePacket(pending.packet, videoSerial: curV, audioSerial: curA) {
                    deferredPacket = (pending.packet, curV, curA)
                }
                continue
            }

            if eofSent {
                pthread_mutex_lock(&stateMutex)
                while !interruptBox.isAborted && pendingSeeks.isEmpty && pendingTrackSwitches.isEmpty {
                    pthread_cond_wait(&stateCond, &stateMutex)
                }
                pthread_mutex_unlock(&stateMutex)
                continue
            }

            let (curV, curA) = currentSerialsSnapshot()
            guard let pkt = av_packet_alloc() else { break }
            let ret = av_read_frame(ctx, pkt)
            if ret < 0 {
                var mutable: UnsafeMutablePointer<AVPacket>? = pkt
                av_packet_free(&mutable)
                if interruptBox.isAborted { break }
                if hasPendingCommand() { continue }
                if ret == mvk_averror_eof() {
                    videoPackets.markEOF(expectedSerial: curV)
                    audioPackets.markEOF(expectedSerial: curA)
                    eofSent = true
                    continue
                }
                let err = VideoKernelFailure.fromFFmpeg(ret, encrypted: encryptedStream)
                reportFatal(err)
                break
            }

            if hasPendingCommand() {
                deferredPacket = (pkt, curV, curA)
                continue
            }

            if !routePacket(pkt, videoSerial: curV, audioSerial: curA) {
                deferredPacket = (pkt, curV, curA)
            }
        }
    }

    /// Routes one packet; returns `false` only if interrupted by a pending command (caller retains `pkt`).
    private func routePacket(_ pkt: UnsafeMutablePointer<AVPacket>, videoSerial curV: Int, audioSerial curA: Int) -> Bool {
        pthread_mutex_lock(&stateMutex)
        let vIdx = videoStreamIndex
        let aIdx = audioStreamIndex
        let sIdx = subtitleStreamIndex
        let vw = videoWidth
        let vh = videoHeight
        pthread_mutex_unlock(&stateMutex)

        let streamIdx = pkt.pointee.stream_index
        if streamIdx == vIdx {
            let result = videoPackets.push(pkt, expectedSerial: curV) { [self] in hasPendingCommand() }
            return result != .interrupted
        } else if streamIdx == aIdx {
            let result = audioPackets.push(pkt, expectedSerial: curA) { [self] in hasPendingCommand() }
            return result != .interrupted
        } else if streamIdx == sIdx {
            subtitleEngine.decode(packet: pkt, videoWidth: vw, videoHeight: vh)
            var mutable: UnsafeMutablePointer<AVPacket>? = pkt
            av_packet_free(&mutable)
            return true
        } else {
            var mutable: UnsafeMutablePointer<AVPacket>? = pkt
            av_packet_free(&mutable)
            return true
        }
    }

    private func handlePendingCommandsOnDemuxThread(ctx: UnsafeMutablePointer<AVFormatContext>) -> DemuxCommandResult {
        pthread_mutex_lock(&stateMutex)
        let seekReq: SeekTicket? = pendingSeeks.isEmpty ? nil : pendingSeeks.removeFirst()
        let trackReq: TrackSwitchCommand? = (seekReq == nil && !pendingTrackSwitches.isEmpty)
            ? pendingTrackSwitches.removeFirst() : nil
        pthread_mutex_unlock(&stateMutex)

        if let req = seekReq {
            let ts = Int64((req.target + startTimeSeconds) * Double(AV_TIME_BASE))
            let ret = avformat_seek_file(ctx, -1, Int64.min, ts, ts, AVSEEK_FLAG_BACKWARD)
            var seekErr: VideoKernelFailure? = nil
            if ret < 0 {
                seekErr = interruptBox.isAborted ? .cancelled : .seekFailed
            } else {
                avformat_flush(ctx)
                videoPackets.flush(newSerial: req.videoSerial, resetBufferedTo: req.target)
                audioPackets.flush(newSerial: req.audioSerial, resetBufferedTo: req.target)
                subtitleEngine.flush()
                clearFrameQueue()
                audioOutput.flush(newSerial: req.audioSerial)
            }
            VideoLogSinkBox.shared.emit(level: 32, message: "[mvk] seek.demux target=\(String(format: "%.2f", req.target)) serial=\(req.videoSerial) ret=\(ret)")
            req.completion.complete(error: seekErr)
            return .seeked
        }

        if let sw = trackReq {
            var err: VideoKernelFailure? = nil
            var geometryUpdate: (Int, Bool, Bool, Int, Int)?
            switch sw.kind {
            case .subtitle:
                if sw.streamIndex < 0 {
                    pthread_mutex_lock(&stateMutex)
                    subtitleStreamIndex = -1
                    pthread_mutex_unlock(&stateMutex)
                    subtitleEngine.close()
                    applyStreamDiscards(ctx: ctx)
                } else if sw.streamIndex < Int32(ctx.pointee.nb_streams),
                          let st = ctx.pointee.streams[Int(sw.streamIndex)],
                          st.pointee.codecpar.pointee.codec_type == AVMEDIA_TYPE_SUBTITLE {
                    if subtitleEngine.open(stream: st, startTimeSeconds: startTimeSeconds) {
                        pthread_mutex_lock(&stateMutex)
                        subtitleStreamIndex = sw.streamIndex
                        pthread_mutex_unlock(&stateMutex)
                        applyStreamDiscards(ctx: ctx)
                    } else {
                        pthread_mutex_lock(&stateMutex)
                        subtitleStreamIndex = -1
                        pthread_mutex_unlock(&stateMutex)
                        applyStreamDiscards(ctx: ctx)
                        err = .formatFailed
                    }
                } else {
                    err = .formatFailed
                }
            case .audio:
                if sw.streamIndex >= 0 && sw.streamIndex < Int32(ctx.pointee.nb_streams),
                   let st = ctx.pointee.streams[Int(sw.streamIndex)],
                   st.pointee.codecpar.pointee.codec_type == AVMEDIA_TYPE_AUDIO {
                    if let nextAudio = createAudioDecoder(stream: st) {
                        let nextAudioSerial = bumpAudioSerial()
                        let curPos = position
                        audioPackets.flush(newSerial: nextAudioSerial, resetBufferedTo: curPos)
                        audioOutput.flush(newSerial: nextAudioSerial)
                        pthread_mutex_lock(&audioDecoderLock)
                        closeAudioDecoderLocked()
                        audioCodecCtx = nextAudio.codecCtx
                        swrCtx = nextAudio.swrCtx
                        swrInSampleRate = nextAudio.inSampleRate
                        swrInChannels = nextAudio.inChannels
                        swrInFormat = nextAudio.inFormat
                        pthread_mutex_unlock(&audioDecoderLock)
                        pthread_mutex_lock(&stateMutex)
                        audioStreamIndex = sw.streamIndex
                        audioSeekTargetPts = nil
                        pthread_mutex_unlock(&stateMutex)
                        applyStreamDiscards(ctx: ctx)
                        audioPackets.setTimeBase(st.pointee.time_base.den > 0 ? av_q2d(st.pointee.time_base) : 1.0 / 48_000.0,
                                                 startOffset: startTimeSeconds)
                    } else {
                        err = .decodeFailed
                    }
                } else {
                    err = .formatFailed
                }
            case .video:
                if sw.streamIndex >= 0 && sw.streamIndex < Int32(ctx.pointee.nb_streams),
                   let st = ctx.pointee.streams[Int(sw.streamIndex)],
                   st.pointee.codecpar.pointee.codec_type == AVMEDIA_TYPE_VIDEO {
                    let geom = mvk_inspect_video_stream(st)
                    let nextDecoder = VideoDecoderPipeline()
                    if nextDecoder.open(stream: st, geometry: geom, startTimeSeconds: startTimeSeconds) {
                        let nextVideoSerial = bumpVideoSerial()
                        let curPos = position
                        videoPackets.flush(newSerial: nextVideoSerial, resetBufferedTo: curPos)
                        clearFrameQueue()
                        pthread_mutex_lock(&videoDecoderLock)
                        let oldDecoder = videoDecoder
                        videoDecoder = nextDecoder
                        pthread_mutex_unlock(&videoDecoderLock)
                        oldDecoder.close()
                        let rawW = Int(st.pointee.codecpar.pointee.width)
                        let rawH = Int(st.pointee.codecpar.pointee.height)
                        let rotated = geom.rotation_degrees == 90 || geom.rotation_degrees == 270
                        let dispW = rotated ? rawH : rawW
                        let dispH = rotated ? rawW : rawH
                        let colorMeta = VideoDecoderPipeline.extractColorMetadata(par: st.pointee.codecpar, geometry: geom)
                        pthread_mutex_lock(&stateMutex)
                        videoStreamIndex = sw.streamIndex
                        videoSeekTargetPts = nil
                        videoWidth = dispW
                        videoHeight = dispH
                        isHDRStream = colorMeta.isHDR
                        doviProfile = geom.dovi_profile >= 0 ? UInt8(geom.dovi_profile) : nil
                        pthread_mutex_unlock(&stateMutex)
                        applyStreamDiscards(ctx: ctx)
                        videoPackets.setTimeBase(st.pointee.time_base.den > 0 ? av_q2d(st.pointee.time_base) : 1.0 / 90_000.0,
                                                 startOffset: startTimeSeconds)
                        geometryUpdate = (Int(geom.rotation_degrees), geom.hflip, geom.vflip, dispW, dispH)
                    } else {
                        nextDecoder.close()
                        err = .decodeFailed
                    }
                } else {
                    err = .formatFailed
                }
            }
            if err == nil {
                pthread_mutex_lock(&stateMutex)
                tracksCache = buildTrackListLocked(ctx: ctx)
                pthread_mutex_unlock(&stateMutex)
            }
            if let g = geometryUpdate {
                onGeometryChanged?(g.0, g.1, g.2, g.3, g.4)
            }
            sw.completion?.complete(error: err)
            return .trackSwitched
        }
        return .none
    }

    func applyStreamDiscards(ctx: UnsafeMutablePointer<AVFormatContext>) {
        pthread_mutex_lock(&stateMutex)
        let vIdx = videoStreamIndex
        let aIdx = audioStreamIndex
        let sIdx = subtitleStreamIndex
        pthread_mutex_unlock(&stateMutex)

        let count = Int(ctx.pointee.nb_streams)
        for i in 0..<count {
            guard let st = ctx.pointee.streams[i] else { continue }
            let idx = Int32(i)
            let wanted = idx == vIdx || idx == aIdx || idx == sIdx
            st.pointee.discard = wanted ? AVDISCARD_DEFAULT : AVDISCARD_ALL
        }
    }

    func buildTrackListLocked(ctx: UnsafeMutablePointer<AVFormatContext>) -> [VideoTrack] {
        var list: [VideoTrack] = []
        let count = Int(ctx.pointee.nb_streams)
        for i in 0..<count {
            guard let st = ctx.pointee.streams[i], let par = st.pointee.codecpar else { continue }
            let kind: VideoTrack.Kind
            let enabled: Bool
            switch par.pointee.codec_type {
            case AVMEDIA_TYPE_VIDEO:
                guard (st.pointee.disposition & AV_DISPOSITION_ATTACHED_PIC) == 0 else { continue }
                kind = .video; enabled = Int32(i) == videoStreamIndex
            case AVMEDIA_TYPE_AUDIO:
                kind = .audio; enabled = Int32(i) == audioStreamIndex
            case AVMEDIA_TYPE_SUBTITLE:
                kind = .subtitle; enabled = Int32(i) == subtitleStreamIndex
            default:
                continue
            }
            let lang = Self.metadataValue(st.pointee.metadata, key: "language")
            let title = Self.metadataValue(st.pointee.metadata, key: "title")
            let codec = Self.codecName(par.pointee.codec_id)
            let displayName = title ?? lang ?? "\(kind.rawValue) #\(i)"
            list.append(VideoTrack(id: Int32(i), kind: kind, name: displayName, codec: codec, language: lang, isEnabled: enabled))
        }
        return list
    }
}
