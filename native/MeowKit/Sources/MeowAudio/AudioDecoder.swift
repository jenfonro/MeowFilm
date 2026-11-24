import Foundation
import CMeowAudioKernel

/// One serial FFmpeg owner. The C framework exposes no FFmpeg types or symbols.
public final class AudioDecoder: @unchecked Sendable {
    private static let readBlockFrames = 4_096

    private let queue = DispatchQueue(label: "meow.audio.decoder." + UUID().uuidString, qos: .userInitiated)
    private let control = NSLock()
    private let callbacks: AudioInputCallbacks
    private var native: OpaquePointer?
    private var pendingPCM: AudioPCMChunk?
    private var active: AudioDecoderOperation?
    private var closed = false
    private var closing: Task<Void, Error>?

    public init(input: any AudioByteInput) {
        callbacks = AudioInputCallbacks(input)
    }

    public func prepare() async throws -> AudioStreamInformation {
        try await run {
            if self.native == nil {
                var error: Int32 = 0
                let input = MeowAudioInput(
                    opaque: Unmanaged.passUnretained(self.callbacks).toOpaque(),
                    read: audioRead, seek: audioSeek, interrupted: audioInterrupted
                )
                let created = self.withCString(self.callbacks.input.formatHint) { format in
                    self.withCString(self.callbacks.input.loopbackRoot) { root in
                        self.withCString(self.callbacks.input.primaryURL) { primary in
                            meow_audio_decoder_create(input, 48_000, format, root, primary, &error)
                        }
                    }
                }
                guard let created else { throw self.failure(error) }
                self.control.lock()
                self.native = created
                self.control.unlock()
            }
            return try self.readInformation()
        }
    }

    public func information() async throws -> AudioStreamInformation {
        try await run { try self.readInformation() }
    }

    public func read() async throws -> AudioPCMChunk? {
        try await run({
            if let pending = self.pendingPCM { return pending }
            guard let native = self.native else { throw AudioCoreError.invalidMedia }
            var position = 0.0
            var count: Int32 = 0
            var right: [Float] = []
            let left = [Float](unsafeUninitializedCapacity: Self.readBlockFrames) { leftBuf, leftCount in
                right = [Float](unsafeUninitializedCapacity: Self.readBlockFrames) { rightBuf, rightCount in
                    count = meow_audio_decoder_read(
                        native,
                        leftBuf.baseAddress,
                        rightBuf.baseAddress,
                        Int32(Self.readBlockFrames),
                        &position
                    )
                    rightCount = count > 0 ? min(Int(count), Self.readBlockFrames) : 0
                }
                leftCount = count > 0 ? min(Int(count), Self.readBlockFrames) : 0
            }
            guard count >= 0 else { throw self.failure(count) }
            guard count > 0 else { return nil }
            guard Int(count) <= Self.readBlockFrames, position.isFinite, position >= 0 else {
                throw AudioCoreError.invalidMedia
            }
            let chunk = AudioPCMChunk(left: left, right: right, position: position)
            self.pendingPCM = chunk
            return chunk
        }, delivered: { _ in self.pendingPCM = nil })
    }

    /// Decodes and validates frames without copying PCM samples (used for cache inspection).
    public func readFrameCount() async throws -> Int {
        try await run {
            guard let native = self.native else { throw AudioCoreError.invalidMedia }
            self.pendingPCM = nil
            var position = 0.0
            let count = meow_audio_decoder_read(native, nil, nil, Int32(Self.readBlockFrames), &position)
            guard count >= 0 else { throw self.failure(count) }
            guard Int(count) <= Self.readBlockFrames else { throw AudioCoreError.invalidMedia }
            return Int(count)
        }
    }

    public func seek(to position: Double) async throws {
        guard position.isFinite, position >= 0 else { throw AudioCoreError.seekFailed }
        try await run {
            guard let native = self.native else { throw AudioCoreError.invalidMedia }
            self.pendingPCM = nil
            let result = meow_audio_decoder_seek(native, position)
            guard result == 0 else { throw self.failure(result) }
        }
    }

    /// Physical interruption is scoped to the operation that is actually
    /// running. Cancelling an enqueued seek must not interrupt another read.
    public func interrupt() {
        control.lock()
        active?.cancel()
        if let native { meow_audio_decoder_cancel(native) }
        callbacks.input.cancelReads()
        control.unlock()
    }

    /// Strict unidirectional teardown: cancel signals -> drain worker & destroy FFmpeg -> close input.
    public func close() async throws {
        control.lock()
        if closing == nil {
            closed = true
            active?.cancel()
            if let native { meow_audio_decoder_cancel(native) }
            let input = callbacks.input
            closing = Task { [self] in
                input.cancelReads()
                await withCheckedContinuation { continuation in
                    queue.async { [self] in
                        control.lock()
                        let retiring = native
                        native = nil
                        pendingPCM = nil
                        control.unlock()
                        if let retiring { meow_audio_decoder_destroy(retiring) }
                        continuation.resume()
                    }
                }
                try await input.close()
            }
        }
        let task = closing!
        control.unlock()
        try await task.value
    }

    private func run<T>(_ body: @escaping () throws -> T,
                        delivered: @escaping (T) -> Void = { _ in }) async throws -> T {
        let operation = AudioDecoderOperation()
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                queue.async { [self] in
                    control.lock()
                    if closed || operation.isCancelled {
                        control.unlock()
                        continuation.resume(throwing: CancellationError())
                        return
                    }
                    callbacks.clearFailure()
                    callbacks.input.resumeReads()
                    if let native { meow_audio_decoder_resume(native) }
                    active = operation
                    control.unlock()
                    let result: Result<T, Error>
                    do {
                        let value = try body()
                        result = operation.finish(.success(value))
                    } catch { result = operation.finish(.failure(error)) }
                    if case .success(let value) = result { delivered(value) }
                    control.lock()
                    if active === operation { active = nil }
                    control.unlock()
                    continuation.resume(with: result)
                }
            }
        } onCancel: { [self] in
            guard operation.cancel() else { return }
            control.lock()
            let running = active === operation
            if running, let native { meow_audio_decoder_cancel(native) }
            if running { callbacks.input.cancelReads() }
            control.unlock()
        }
    }

    private func readInformation() throws -> AudioStreamInformation {
        guard let native else { throw AudioCoreError.invalidMedia }
        var value = MeowAudioInfo()
        let result = meow_audio_decoder_info(native, &value)
        guard result == 0 else { throw failure(result) }
        guard value.sample_rate == 48_000, value.channels == 2 else { throw AudioCoreError.invalidMedia }
        return .init(duration: value.duration.isFinite && value.duration > 0 ? value.duration : nil,
                     sampleRate: Int(value.sample_rate), channels: Int(value.channels), seekable: value.seekable != 0,
                     sourceSampleRate: Int(value.source_sample_rate), sourceChannels: Int(value.source_channels),
                     decodedSourceFrames: value.decoded_source_frames, isComplete: value.complete != 0)
    }

    private func failure(_ code: Int32) -> Error {
        if code == -1 {
            _ = callbacks.takeFailure()
            return CancellationError()
        }
        if let error = callbacks.takeFailure() { return error }
        switch code {
        case -2: return AudioCoreError.invalidMedia
        case -3: return AudioCoreError.inputFailed
        case -4: return AudioCoreError.unsupportedFormat
        case -5: return AudioCoreError.allocationFailed
        default: return AudioCoreError.invalidMedia
        }
    }

    private func withCString<T>(_ value: String?, _ body: (UnsafePointer<CChar>?) -> T) -> T {
        if let value { return value.withCString(body) }
        return body(nil)
    }

    deinit {
        if let native { meow_audio_decoder_destroy(native) }
    }
}

private final class AudioDecoderOperation: @unchecked Sendable {
    private let lock = NSLock()
    private var cancelled = false
    private var finished = false
    var isCancelled: Bool { lock.lock(); defer { lock.unlock() }; return cancelled }
    @discardableResult
    func cancel() -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard !finished else { return false }
        cancelled = true
        return true
    }
    func finish<T>(_ result: Result<T, Error>) -> Result<T, Error> {
        lock.lock(); defer { lock.unlock() }
        finished = true
        return cancelled ? .failure(CancellationError()) : result
    }
}

private final class AudioInputCallbacks: @unchecked Sendable {
    let input: any AudioByteInput
    private let lock = NSLock()
    private var failure: Error?
    init(_ input: any AudioByteInput) { self.input = input }
    func fail(_ error: Error) { lock.lock(); failure = error; lock.unlock() }
    func clearFailure() { lock.lock(); failure = nil; lock.unlock() }
    func takeFailure() -> Error? {
        lock.lock(); defer { lock.unlock() }
        let result = failure; failure = nil; return result
    }
}

private let audioRead: @convention(c) (UnsafeMutableRawPointer?, UnsafeMutablePointer<UInt8>?, Int32) -> Int32 = {
    opaque, buffer, count in
    guard let opaque, let buffer, count > 0 else { return -3 }
    let callbacks = Unmanaged<AudioInputCallbacks>.fromOpaque(opaque).takeUnretainedValue()
    do {
        let bytes = try callbacks.input.read(maximumBytes: Int(count))
        guard bytes.count <= Int(count) else { throw AudioCoreError.inputFailed }
        bytes.copyBytes(to: buffer, count: bytes.count)
        return Int32(bytes.count)
    } catch {
        callbacks.fail(error)
        return error is CancellationError || error as? AudioCoreError == .cancelled ? -1 : -3
    }
}

private let audioSeek: @convention(c) (UnsafeMutableRawPointer?, Int64, Int32) -> Int64 = {
    opaque, offset, flags in
    guard let opaque else { return -3 }
    let callbacks = Unmanaged<AudioInputCallbacks>.fromOpaque(opaque).takeUnretainedValue()
    let whence = flags & ~Int32(0x20000)
    do {
        if whence == 0x10000 { return try callbacks.input.seek(.size) }
        let origin: AudioSeekOrigin
        switch whence {
        case 0: origin = .start
        case 1: origin = .current
        case 2: origin = .end
        default: throw AudioCoreError.seekFailed
        }
        return try callbacks.input.seek(.offset(offset, origin))
    } catch {
        if error is CancellationError || error as? AudioCoreError == .cancelled {
            return -1
        }
        if error as? AudioCoreError == .unsupportedFormat ||
           (whence == 0x10000 && error as? AudioCoreError == .seekFailed) {
            // Capability probes returning ENOSYS are not terminal input errors.
            return -4
        }
        callbacks.fail(error)
        return -3
    }
}

private let audioInterrupted: @convention(c) (UnsafeMutableRawPointer?) -> Int32 = { opaque in
    guard let opaque else { return 1 }
    return Unmanaged<AudioInputCallbacks>.fromOpaque(opaque).takeUnretainedValue().input.isReadCancelled ? 1 : 0
}
