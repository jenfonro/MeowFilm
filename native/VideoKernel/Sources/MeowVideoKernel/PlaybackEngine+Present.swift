import Foundation
import QuartzCore

extension PlaybackEngine {
    private func completeFirstFramePreview(enqueued: Bool, pts: Double, serial: Int) {
        pthread_mutex_lock(&stateMutex)
        firstFrameInFlight = false
        let accepted = enqueued && self.videoSerial == serial && !closed
        if accepted {
            firstFramePreviewDone = true
            lastSubmittedPts = pts
            currentPosition = max(0, pts)
            wallClockBaseMedia = currentPosition
            wallClockBaseHost = CACurrentMediaTime()
            hasWallClock = true
            submittedPictures &+= 1
        }
        let curV = self.videoSerial
        let wall = wallClockBaseMedia
        pthread_cond_broadcast(&stateCond)
        pthread_mutex_unlock(&stateMutex)
        VideoLogSinkBox.shared.emit(level: 32, message: "[mvk] preview.done enqueued=\(enqueued) accepted=\(accepted) pts=\(String(format: "%.2f", pts)) serial=\(serial)/\(curV) wall=\(String(format: "%.2f", wall))")
    }

    func presentLoop() {
        while !interruptBox.isAborted {
            pthread_mutex_lock(&stateMutex)
            let isPlaying = playing
            let needFirstFrame = !firstFramePreviewDone
            let isEnded = ended
            let curSerial = videoSerial
            pthread_mutex_unlock(&stateMutex)

            // 0% CPU idle park when paused/ended and first-frame preview is already displayed.
            if (!isPlaying && !needFirstFrame) || isEnded {
                pthread_mutex_lock(&stateMutex)
                while !interruptBox.isAborted && ((!playing && firstFramePreviewDone) || ended) {
                    pthread_cond_wait(&stateCond, &stateMutex)
                }
                pthread_mutex_unlock(&stateMutex)
                continue
            }

            var reachedEOF = false
            guard let frame = popDecodedFrame(expectedSerial: curSerial, reachedEOF: &reachedEOF) else {
                if interruptBox.isAborted { break }
                if reachedEOF {
                    // Wait for final frame duration and any remaining audio tail before firing onEnd.
                    pthread_mutex_lock(&stateMutex)
                    let stillPlaying = playing
                    let hasAudio = audioStreamIndex >= 0
                    let dur = max(0, min(0.2, durationSeconds > 0 ? durationSeconds - lastSubmittedPts : 0.033))
                    pthread_mutex_unlock(&stateMutex)
                    if stillPlaying && dur > 0.005 {
                        waitOnStateCond(seconds: dur, expectedVideoSerial: curSerial)
                    }
                    if hasAudio && stillPlaying {
                        while !interruptBox.isAborted && !audioOutput.isDrained {
                            pthread_mutex_lock(&stateMutex)
                            let keepWaiting = playing && !ended && videoSerial == curSerial
                            pthread_mutex_unlock(&stateMutex)
                            if !keepWaiting { break }
                            waitOnStateCond(seconds: 0.015, expectedVideoSerial: curSerial)
                        }
                    }
                    pthread_mutex_lock(&stateMutex)
                    if videoSerial == curSerial && !ended && !interruptBox.isAborted {
                        ended = true
                        playing = false
                        if durationSeconds > 0 { currentPosition = durationSeconds }
                        pthread_mutex_unlock(&stateMutex)
                        audioOutput.pause()
                        onEnd?()
                    } else {
                        pthread_mutex_unlock(&stateMutex)
                    }
                }
                continue
            }

            // First-frame preview immediately displays and confirms actual AVSampleBufferDisplayLayer enqueue.
            if needFirstFrame {
                var retryCount = 0
                while !interruptBox.isAborted && currentVideoSerialSnapshot() == curSerial {
                    pthread_mutex_lock(&stateMutex)
                    if firstFramePreviewDone {
                        pthread_mutex_unlock(&stateMutex)
                        break
                    }
                    firstFrameInFlight = true
                    pthread_mutex_unlock(&stateMutex)

                    let cue = subtitleEngine.activeCue(at: frame.pts)
                    let dispatched = presentFrame?(frame.pixelBuffer, frame.pts, true, cue, frame.serial) { [weak self] enqueued in
                        self?.completeFirstFramePreview(enqueued: enqueued, pts: frame.pts, serial: frame.serial)
                    } ?? false

                    if !dispatched {
                        pthread_mutex_lock(&stateMutex)
                        firstFrameInFlight = false
                        pthread_mutex_unlock(&stateMutex)
                        waitOnStateCond(seconds: 0.010, expectedVideoSerial: curSerial)
                        continue
                    }

                    // Wait for MainActor enqueue confirmation (woken immediately by cancel / seek / completion).
                    pthread_mutex_lock(&stateMutex)
                    while !interruptBox.isAborted && videoSerial == curSerial && firstFrameInFlight {
                        var tv = timeval()
                        gettimeofday(&tv, nil)
                        var ts = timespec(tv_sec: tv.tv_sec, tv_nsec: Int(tv.tv_usec) * 1000 + 15_000_000)
                        if ts.tv_nsec >= 1_000_000_000 {
                            ts.tv_sec += 1
                            ts.tv_nsec -= 1_000_000_000
                        }
                        pthread_cond_timedwait(&stateCond, &stateMutex, &ts)
                    }
                    let done = firstFramePreviewDone
                    pthread_mutex_unlock(&stateMutex)
                    if done { break }

                    retryCount += 1
                    if retryCount >= 50 { break }
                    waitOnStateCond(seconds: 0.010, expectedVideoSerial: curSerial)
                }
                continue
            }

            // MasterClock synchronization loop (exact pacing for 0.25x..8.0x + audio underrun buffering detection).
            var dropFrame = false
            var abortFrame = false
            var syncDiff: Double = 0
            var audioStarvedSince: Double?
            var signaledAudioBuffering = false
            defer {
                if signaledAudioBuffering {
                    setBuffering(false)
                }
            }
            while !interruptBox.isAborted {
                pthread_mutex_lock(&stateMutex)
                let stillPlaying = playing
                let nowSerial = videoSerial
                let nowRate = playbackRate
                let nowEnded = ended
                let hasAudio = audioStreamIndex >= 0
                pthread_mutex_unlock(&stateMutex)
                if !stillPlaying || nowSerial != curSerial || nowEnded {
                    abortFrame = true
                    break
                }
                let clock = readMasterClock(rate: Double(nowRate))
                let diff = frame.pts - clock
                syncDiff = diff
                if diff <= 0.005 {
                    if diff < -0.05 {
                        pthread_mutex_lock(&frameMutex)
                        let hasMore = !frameQueue.isEmpty
                        pthread_mutex_unlock(&frameMutex)
                        if hasMore { dropFrame = true }
                    }
                    break
                }
                // Detect audio-ring starvation when video frames are still queued so buffering is reported cleanly.
                if hasAudio && !audioOutput.isDrained && audioOutput.bufferedDurationSeconds <= 0.001 && audioPackets.count == 0 {
                    let now = CACurrentMediaTime()
                    if audioStarvedSince == nil {
                        audioStarvedSince = now
                    } else if !signaledAudioBuffering && (now - audioStarvedSince!) >= 0.15 {
                        signaledAudioBuffering = true
                        setBuffering(true)
                    }
                } else if signaledAudioBuffering && (audioOutput.bufferedDurationSeconds >= 0.05 || audioOutput.isDrained) {
                    signaledAudioBuffering = false
                    audioStarvedSince = nil
                    setBuffering(false)
                } else if audioOutput.bufferedDurationSeconds > 0.001 || audioOutput.isDrained {
                    audioStarvedSince = nil
                }

                let sliceSec = min(diff / Double(max(0.25, nowRate)), 0.015)
                if sliceSec <= 0.0005 { break }
                waitOnStateCond(seconds: sliceSec, expectedVideoSerial: curSerial)
            }
            if signaledAudioBuffering {
                signaledAudioBuffering = false
                setBuffering(false)
            }
            if abortFrame { continue }
            if dropFrame {
                pthread_mutex_lock(&stateMutex)
                droppedFrames &+= 1
                lastAvSyncDiff = syncDiff
                pthread_mutex_unlock(&stateMutex)
                continue
            }

            let cue = subtitleEngine.activeCue(at: frame.pts)
            _ = presentFrame?(frame.pixelBuffer, frame.pts, true, cue, curSerial, nil)

            pthread_mutex_lock(&stateMutex)
            if videoSerial == curSerial {
                lastSubmittedPts = frame.pts
                currentPosition = max(0, frame.pts)
                lastAvSyncDiff = syncDiff
                submittedPictures &+= 1
            }
            pthread_mutex_unlock(&stateMutex)
        }
    }

    // MARK: - MasterClock & Frame Queue Helpers

    func waitOnStateCond(seconds: Double, expectedVideoSerial: Int) {
        let clamped = max(0.0005, min(seconds, 0.25))
        var tv = timeval()
        gettimeofday(&tv, nil)
        let addNsec = Int64(clamped * 1_000_000_000)
        var sec = Int(tv.tv_sec) + Int(addNsec / 1_000_000_000)
        var nsec = Int(tv.tv_usec) * 1000 + Int(addNsec % 1_000_000_000)
        if nsec >= 1_000_000_000 {
            sec += 1
            nsec -= 1_000_000_000
        }
        var ts = timespec(tv_sec: sec, tv_nsec: nsec)
        pthread_mutex_lock(&stateMutex)
        if !interruptBox.isAborted && videoSerial == expectedVideoSerial {
            pthread_cond_timedwait(&stateCond, &stateMutex, &ts)
        }
        pthread_mutex_unlock(&stateMutex)
    }

    func waitOnStateCondForAudio(seconds: Double, expectedAudioSerial: Int) {
        let clamped = max(0.0005, min(seconds, 0.25))
        var tv = timeval()
        gettimeofday(&tv, nil)
        let addNsec = Int64(clamped * 1_000_000_000)
        var sec = Int(tv.tv_sec) + Int(addNsec / 1_000_000_000)
        var nsec = Int(tv.tv_usec) * 1000 + Int(addNsec % 1_000_000_000)
        if nsec >= 1_000_000_000 {
            sec += 1
            nsec -= 1_000_000_000
        }
        var ts = timespec(tv_sec: sec, tv_nsec: nsec)
        pthread_mutex_lock(&stateMutex)
        if !interruptBox.isAborted && audioSerial == expectedAudioSerial {
            pthread_cond_timedwait(&stateCond, &stateMutex, &ts)
        }
        pthread_mutex_unlock(&stateMutex)
    }

    private func readMasterClock(rate: Double) -> Double {
        pthread_mutex_lock(&stateMutex)
        let hasAudio = audioStreamIndex >= 0
        pthread_mutex_unlock(&stateMutex)

        if hasAudio, let audioSec = audioOutput.clockSeconds() {
            pthread_mutex_lock(&stateMutex)
            wallClockBaseMedia = audioSec
            wallClockBaseHost = CACurrentMediaTime()
            hasWallClock = true
            pthread_mutex_unlock(&stateMutex)
            return audioSec
        }
        pthread_mutex_lock(&stateMutex)
        defer { pthread_mutex_unlock(&stateMutex) }
        let now = CACurrentMediaTime()
        if !hasWallClock {
            wallClockBaseMedia = currentPosition
            wallClockBaseHost = now
            hasWallClock = true
            return currentPosition
        }
        if buffering || !playing {
            wallClockBaseHost = now
            return wallClockBaseMedia
        }
        let elapsed = max(0, now - wallClockBaseHost)
        let advanced = wallClockBaseMedia + elapsed * rate
        wallClockBaseMedia = advanced
        wallClockBaseHost = now
        return advanced
    }

    func pushDecodedFrame(_ frame: DecodedVideoFrame) {
        pthread_mutex_lock(&frameMutex)
        defer { pthread_mutex_unlock(&frameMutex) }
        while !interruptBox.isAborted && frameQueue.count >= maxVideoFrames && currentVideoSerialSnapshot() == frame.serial {
            pthread_cond_wait(&frameNotFull, &frameMutex)
        }
        guard !interruptBox.isAborted, currentVideoSerialSnapshot() == frame.serial else { return }
        frameQueue.append(frame)
        pthread_cond_signal(&frameNotEmpty)
    }

    private func popDecodedFrame(expectedSerial: Int, reachedEOF: inout Bool) -> DecodedVideoFrame? {
        var signaledBuffering = false
        defer {
            if signaledBuffering {
                setBuffering(false)
            }
        }

        pthread_mutex_lock(&frameMutex)
        while !interruptBox.isAborted {
            // Discard any frames belonging to an older serial epoch before checking queue emptiness.
            while let first = frameQueue.first, first.serial != expectedSerial {
                frameQueue.removeFirst()
                pthread_cond_signal(&frameNotFull)
            }
            if !frameQueue.isEmpty || videoDecodeEOF || currentVideoSerialSnapshot() != expectedSerial {
                break
            }
            pthread_mutex_lock(&stateMutex)
            let isPlaying = playing
            let previewDone = firstFramePreviewDone
            pthread_mutex_unlock(&stateMutex)

            if previewDone && !isPlaying {
                pthread_mutex_unlock(&frameMutex)
                reachedEOF = false
                return nil
            }
            if !signaledBuffering && previewDone && isPlaying {
                // Drop frameMutex before notifying buffering / pausing audioOutput.
                pthread_mutex_unlock(&frameMutex)
                signaledBuffering = true
                setBuffering(true)
                pthread_mutex_lock(&frameMutex)
                continue
            }
            pthread_cond_wait(&frameNotEmpty, &frameMutex)
        }
        if interruptBox.isAborted || currentVideoSerialSnapshot() != expectedSerial {
            pthread_mutex_unlock(&frameMutex)
            reachedEOF = false
            return nil
        }
        while let first = frameQueue.first, first.serial != expectedSerial {
            frameQueue.removeFirst()
            pthread_cond_signal(&frameNotFull)
        }
        if frameQueue.isEmpty {
            reachedEOF = videoDecodeEOF
            pthread_mutex_unlock(&frameMutex)
            return nil
        }
        pthread_mutex_lock(&stateMutex)
        let shouldPark = firstFramePreviewDone && !playing
        pthread_mutex_unlock(&stateMutex)
        if shouldPark {
            pthread_mutex_unlock(&frameMutex)
            reachedEOF = false
            return nil
        }
        let frame = frameQueue.removeFirst()
        reachedEOF = false
        pthread_cond_signal(&frameNotFull)
        pthread_mutex_unlock(&frameMutex)
        return frame
    }

    func clearFrameQueue() {
        pthread_mutex_lock(&frameMutex)
        frameQueue.removeAll(keepingCapacity: true)
        videoDecodeEOF = false
        pthread_cond_broadcast(&frameNotEmpty)
        pthread_cond_broadcast(&frameNotFull)
        pthread_mutex_unlock(&frameMutex)
    }

    private func setBuffering(_ value: Bool) {
        pthread_mutex_lock(&stateMutex)
        guard buffering != value else { pthread_mutex_unlock(&stateMutex); return }
        buffering = value
        let hasAudio = audioStreamIndex >= 0
        let shouldResumeAudio = !value && playing && committed && hasAudio && !closed
        if value {
            wallClockBaseHost = CACurrentMediaTime()
        }
        pthread_mutex_unlock(&stateMutex)

        if value {
            if hasAudio { audioOutput.pause() }
        } else if shouldResumeAudio {
            _ = audioOutput.start()
        }
        onBufferingChanged?(value)
    }
}
