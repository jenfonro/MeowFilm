import Foundation
import CMeowAudioKernel

/// One serial FFmpeg owner. The C framework exposes no FFmpeg types or symbols.
public final class AudioDecoder: @unchecked Sendable {
    private let queue = DispatchQueue(label: "meow.audio.decoder." + UUID().uuidString, qos: .userInitiated)
    private let control = NSLock()
    private let callbacks: AudioInputCallbacks
    private let beforePCMDelivery: (@Sendable () -> Void)?
    private var native: OpaquePointer?
    private var pendingPCM: AudioPCMChunk?
    private var active: AudioDecoderOperation?
    private var closed = false
    private var closing: Task<Void, Error>?

    public convenience init(input: any AudioByteInput) {
        self.init(input: input, beforePCMDelivery: nil)
    }

    /// Controls the consumed-PCM handoff in deterministic cancellation tests.
    init(input: any AudioByteInput, beforePCMDelivery: (@Sendable () -> Void)?) {
        callbacks = AudioInputCallbacks(input)
        self.beforePCMDelivery = beforePCMDelivery
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
            var samples = [Float](repeating: 0, count: 4_096 * 2)
            var position = 0.0
            let count = samples.withUnsafeMutableBufferPointer {
                meow_audio_decoder_read(native, $0.baseAddress, 4_096, &position)
            }
            guard count >= 0 else { throw self.failure(count) }
            guard count > 0 else { return nil }
            guard count <= 4_096, position.isFinite, position >= 0 else { throw AudioCoreError.invalidMedia }
            samples.removeLast(samples.count - Int(count) * 2)
            let chunk = AudioPCMChunk(samples: samples, position: position)
            self.pendingPCM = chunk
            self.beforePCMDelivery?()
            return chunk
        }, delivered: { _ in self.pendingPCM = nil })
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

    public func close() async throws {
        control.lock()
        if closing == nil {
            closed = true
            active?.cancel()
            if let native { meow_audio_decoder_cancel(native) }
            let input = callbacks.input
            closing = Task { [self] in
                input.cancelReads()
                let inputClosing = Task { try await input.close() }
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
                try await inputClosing.value
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
        if let error = callbacks.takeFailure() { return error }
        switch code {
        case -1: return CancellationError()
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
        // A live operation retains this owner. If preparation never started,
        // there is no native decoder; callers still explicitly close inputs.
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
    do {
        let whence = flags & ~Int32(0x20000)
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
        callbacks.fail(error)
        return error is CancellationError ? -1 : -4
    }
}

private let audioInterrupted: @convention(c) (UnsafeMutableRawPointer?) -> Int32 = { opaque in
    guard let opaque else { return 1 }
    return Unmanaged<AudioInputCallbacks>.fromOpaque(opaque).takeUnretainedValue().input.isReadCancelled ? 1 : 0
}
