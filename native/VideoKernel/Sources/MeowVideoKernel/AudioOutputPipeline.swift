import Foundation
import AVFoundation
import CMeowVideoFFmpeg

/// Real-time-safe audio output built on AVAudioEngine + AVAudioUnitTimePitch + a lock-free C11 SPSC ring buffer.
/// All AVAudioEngine mutations are serialized under `engineLock`; the CoreAudio render callback delegates directly
/// to C `mvk_audio_ring_render_planar_stereo` with zero locks, zero ARC, and zero heap allocations.
final class AudioOutputPipeline: @unchecked Sendable {
    static let outputSampleRate: Double = 48_000
    static let outputChannels: Int = 2

    private let engine = AVAudioEngine()
    private let timePitch = AVAudioUnitTimePitch()
    private var sourceNode: AVAudioSourceNode?
    private let ring: OpaquePointer

    // Lock hierarchy: producerLock -> engineLock -> stateLock (never reversed; CoreAudio RT thread takes ZERO locks).
    private let producerLock = NSLock()
    private let engineLock = NSLock()
    private let stateLock = NSLock()
    private let recoveryQueue = DispatchQueue(label: "MeowVideoKernel.audioRecovery", qos: .userInitiated)
    private var observers: [NSObjectProtocol] = []

    // Clock & control tracking (guarded by stateLock).
    private var anchorPtsSeconds: Double = 0
    private var anchorFrameIndex: UInt64 = 0
    private var hasAnchor = false
    private var outputLatencySeconds: Double = 0
    private var desiredVolume: Float = 1.0
    private var muted = true
    private var desiredRate: Float = 1.0
    private var running = false
    private var committed = false
    private var closed = false
    private var eofReached = false
    // Producer serial guarded by producerLock so stale pre-seek audio chunks can never land after flush.
    private var producerSerial: Int = 1

    init() {
        // ~2 seconds of stereo 48kHz float32 audio (192,000 samples -> 262,144 power-of-two).
        ring = mvk_audio_ring_create(192_000)!
        engineLock.lock()
        configureGraphLocked()
        engineLock.unlock()
    }

    deinit {
        close()
        mvk_audio_ring_destroy(ring)
    }

    var underrunCount: UInt64 { mvk_audio_ring_underruns(ring) }

    var isDrained: Bool {
        stateLock.lock()
        let eof = eofReached
        stateLock.unlock()
        return eof && mvk_audio_ring_available_read(ring) == 0
    }

    func setCommitted(_ active: Bool) {
        engineLock.lock()
        stateLock.lock()
        committed = active
        let isClosed = closed
        stateLock.unlock()
        if active && !isClosed {
            installObserversLocked()
        } else {
            removeObserversLocked()
        }
        engineLock.unlock()
    }

    func setMuted(_ mute: Bool) {
        stateLock.lock()
        muted = mute
        let gain: Float = (mute || !running) ? 0.0 : desiredVolume
        stateLock.unlock()
        mvk_audio_ring_set_target_gain(ring, gain)
    }

    func setVolume(_ value: Float) {
        let clamped = value.isFinite ? min(1, max(0, value)) : 0
        stateLock.lock()
        desiredVolume = clamped
        let gain: Float = (muted || !running) ? 0.0 : clamped
        stateLock.unlock()
        mvk_audio_ring_set_target_gain(ring, gain)
    }

    var volume: Float {
        stateLock.lock()
        defer { stateLock.unlock() }
        return desiredVolume
    }

    /// Seekless playback rate adjustment (0.25x..8.0x) preserving queued PCM.
    func setRate(_ rate: Float) {
        guard rate.isFinite, rate > 0 else { return }
        let clamped = min(8.0, max(0.25, rate))
        engineLock.lock()
        stateLock.lock()
        desiredRate = clamped
        let isClosed = closed
        stateLock.unlock()
        if !isClosed {
            timePitch.rate = clamped
            timePitch.pitch = 0
            refreshLatencyLocked()
        }
        engineLock.unlock()
    }

    func start() -> Bool {
        engineLock.lock()
        defer { engineLock.unlock() }
        stateLock.lock()
        guard !closed else { stateLock.unlock(); return false }
        let gain: Float = muted ? 0.0 : desiredVolume
        stateLock.unlock()

        if !engine.isRunning {
            do {
                try engine.start()
            } catch {
                // Retry once after reconfiguring graph (e.g. route/configuration changed).
                rebuildGraphLocked()
                do {
                    try engine.start()
                } catch {
                    return false
                }
            }
        }
        refreshLatencyLocked()
        stateLock.lock()
        running = true
        stateLock.unlock()
        mvk_audio_ring_set_target_gain(ring, gain)
        mvk_audio_ring_set_playing(ring, true)
        return true
    }

    func pause() {
        mvk_audio_ring_set_target_gain(ring, 0)
        mvk_audio_ring_set_playing(ring, false)
        engineLock.lock()
        stateLock.lock()
        running = false
        stateLock.unlock()
        if engine.isRunning {
            engine.pause()
        }
        engineLock.unlock()
    }

    /// Flushes queued PCM safely without racing the CoreAudio render callback,
    /// resetting the anchor so the first real post-seek audio packet establishes the exact PTS.
    func flush(newSerial: Int) {
        producerLock.lock()
        producerSerial = newSerial
        engineLock.lock()
        if !engine.isRunning {
            mvk_audio_ring_clear_stopped(ring)
        } else {
            mvk_audio_ring_request_flush(ring)
        }
        engineLock.unlock()

        stateLock.lock()
        eofReached = false
        hasAnchor = false
        anchorPtsSeconds = 0
        anchorFrameIndex = 0
        stateLock.unlock()
        producerLock.unlock()
    }

    func markEOF() {
        stateLock.lock()
        eofReached = true
        stateLock.unlock()
    }

    /// Writes interleaved stereo Float32 PCM at 48kHz into the ring buffer, anchoring clock using
    /// the single-producer monotonic frame index on first write or significant PTS discontinuity (> 0.25s).
    func writeInterleavedFloat(_ data: UnsafePointer<Float>, frameCount: Int, ptsSeconds: Double?, serial: Int) -> Int {
        guard frameCount > 0 else { return 0 }
        producerLock.lock()
        defer { producerLock.unlock() }
        guard producerSerial == serial else { return frameCount }

        let sampleCount = frameCount * Self.outputChannels
        if let pts = ptsSeconds, pts.isFinite {
            let writeFrames = UInt64(mvk_audio_ring_write_pos(ring) / Self.outputChannels)
            stateLock.lock()
            if !hasAnchor {
                anchorPtsSeconds = max(0, pts)
                anchorFrameIndex = writeFrames
                hasAnchor = true
            } else {
                let expectedPts = anchorPtsSeconds +
                    Double(Int64(writeFrames) - Int64(anchorFrameIndex)) / Self.outputSampleRate
                if abs(pts - expectedPts) > 0.25 {
                    anchorPtsSeconds = max(0, pts)
                    anchorFrameIndex = writeFrames
                }
            }
            stateLock.unlock()
        }
        let writtenSamples = mvk_audio_ring_write(ring, data, sampleCount)
        return writtenSamples / Self.outputChannels
    }

    var bufferedDurationSeconds: Double {
        Double(mvk_audio_ring_available_read(ring) / Self.outputChannels) / Self.outputSampleRate
    }

    /// Returns the audio clock in media seconds compensated for TimePitch + hardware output latency,
    /// or nil if no audio anchor exists or audio has fully drained.
    func clockSeconds() -> Double? {
        stateLock.lock()
        guard hasAnchor else { stateLock.unlock(); return nil }
        let eof = eofReached
        let anchor = anchorPtsSeconds
        let baseFrames = anchorFrameIndex
        let rate = Double(desiredRate)
        let latency = outputLatencySeconds
        stateLock.unlock()

        if eof && mvk_audio_ring_available_read(ring) == 0 {
            return nil
        }
        let consumedFrames = UInt64(mvk_audio_ring_effective_read_pos(ring) / Self.outputChannels)
        if consumedFrames < baseFrames {
            return max(0, anchor)
        }
        let deltaFrames = Int64(consumedFrames - baseFrames)
        let mediaSeconds = anchor + Double(deltaFrames) / Self.outputSampleRate
        let adjusted = mediaSeconds - (latency.isFinite && latency > 0 ? latency * rate : 0)
        return max(0, adjusted)
    }

    func close() {
        mvk_audio_ring_set_playing(ring, false)
        mvk_audio_ring_set_target_gain(ring, 0)
        producerLock.lock()
        engineLock.lock()
        removeObserversLocked()
        stateLock.lock()
        closed = true
        running = false
        committed = false
        stateLock.unlock()
        if engine.isRunning { engine.stop() }
        detachGraphLocked()
        mvk_audio_ring_clear_stopped(ring)
        engineLock.unlock()
        producerLock.unlock()
    }

    // MARK: - Graph & Recovery (Always called under `engineLock`)

    private func configureGraphLocked() {
        detachGraphLocked()
        let format = AVAudioFormat(
            commonFormat: .pcmFormatFloat32,
            sampleRate: Self.outputSampleRate,
            channels: AVAudioChannelCount(Self.outputChannels),
            interleaved: false
        )!
        let ringPtr = ring
        let rampStep: Float = 1.0 / Float(Self.outputSampleRate * 0.010) // 10ms anti-pop linear ramp

        let node = AVAudioSourceNode(format: format) { _, _, frameCount, audioBufferList -> OSStatus in
            let abl = UnsafeMutableAudioBufferListPointer(audioBufferList)
            let frames = Int(frameCount)
            guard frames > 0, abl.count >= 2,
                  let left = abl[0].mData?.assumingMemoryBound(to: Float.self),
                  let right = abl[1].mData?.assumingMemoryBound(to: Float.self) else {
                for buf in abl {
                    if let ptr = buf.mData { memset(ptr, 0, Int(buf.mDataByteSize)) }
                }
                return noErr
            }
            mvk_audio_ring_render_planar_stereo(ringPtr, left, right, frames, rampStep)
            return noErr
        }
        // Support 4096-frame render slices (required for iOS screen-lock / PiP audio continuity).
        node.auAudioUnit.maximumFramesToRender = 4096
        timePitch.auAudioUnit.maximumFramesToRender = 4096

        sourceNode = node
        engine.attach(node)
        engine.attach(timePitch)
        engine.connect(node, to: timePitch, format: format)
        engine.connect(timePitch, to: engine.mainMixerNode, format: format)
        engine.prepare()
        refreshLatencyLocked()
    }

    private func detachGraphLocked() {
        if let node = sourceNode {
            engine.disconnectNodeOutput(node)
            engine.detach(node)
            sourceNode = nil
        }
        if timePitch.engine != nil {
            engine.disconnectNodeOutput(timePitch)
            engine.detach(timePitch)
        }
    }

    private func rebuildGraphLocked() {
        if engine.isRunning { engine.stop() }
        engine.reset()
        configureGraphLocked()
        stateLock.lock()
        let rate = desiredRate
        stateLock.unlock()
        timePitch.rate = rate
        timePitch.pitch = 0
        refreshLatencyLocked()
    }

    private func refreshLatencyLocked() {
        let total = engine.outputNode.presentationLatency + timePitch.latency
        stateLock.lock()
        outputLatencySeconds = (total.isFinite && total > 0) ? total : 0
        stateLock.unlock()
    }

    private func installObserversLocked() {
        guard observers.isEmpty else { return }
        let nc = NotificationCenter.default
        observers.append(
            nc.addObserver(forName: .AVAudioEngineConfigurationChange, object: engine, queue: nil) { [weak self] _ in
                self?.scheduleRecovery(forceRebuild: true)
            }
        )
        #if os(iOS)
        observers.append(
            nc.addObserver(forName: AVAudioSession.routeChangeNotification, object: nil, queue: nil) { [weak self] _ in
                self?.scheduleRecovery(forceRebuild: false)
            }
        )
        #endif
    }

    private func removeObserversLocked() {
        for observer in observers {
            NotificationCenter.default.removeObserver(observer)
        }
        observers.removeAll()
    }

    private func scheduleRecovery(forceRebuild: Bool) {
        recoveryQueue.async { [weak self] in
            guard let self else { return }
            self.engineLock.lock()
            defer { self.engineLock.unlock() }
            self.stateLock.lock()
            let shouldRecover = self.committed && self.running && !self.closed
            let gain: Float = self.muted ? 0.0 : self.desiredVolume
            self.stateLock.unlock()
            guard shouldRecover else { return }
            if forceRebuild || !self.engine.isRunning {
                if forceRebuild {
                    self.rebuildGraphLocked()
                }
                do {
                    try self.engine.start()
                    self.refreshLatencyLocked()
                    mvk_audio_ring_set_target_gain(self.ring, gain)
                    mvk_audio_ring_set_playing(self.ring, true)
                } catch {
                    if !forceRebuild {
                        self.rebuildGraphLocked()
                        try? self.engine.start()
                        self.refreshLatencyLocked()
                    }
                }
            }
        }
    }
}
