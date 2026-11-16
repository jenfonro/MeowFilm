#if os(iOS) || os(macOS)
import Foundation
import MeowAudio
import MeowContracts

/// Adapts the product's controlled byte owners, not a remote media URL, to
/// FFmpeg. Blocking waits are confined to the decoder's dedicated worker.
final class PlaybackAudioInput: AudioByteInput, @unchecked Sendable {
    let formatHint: String?
    let loopbackRoot: String?
    private(set) var primaryURL: String?
    private let file: FileHandle?
    private let source: (any PlaybackByteSource)?
    private let loader: ProgressiveMediaLoader?
    private let playlist: Data?
    private let route: HLSLoopbackRoute?
    private let lock = NSLock()
    private var cancelled = false
    private var closed = false
    private var position: Int64 = 0
    private var length: Int64?
    private var reads: [UUID: Task<Void, Never>] = [:]
    private var sink: AudioProgressiveSink?

    init(file url: URL) throws {
        do {
            file = try FileHandle(forReadingFrom: url)
            length = Int64(try file!.seekToEnd())
            try file!.seek(toOffset: 0)
        } catch { throw PlaybackNetworkError.transportFailed }
        source = nil; loader = nil; playlist = nil; route = nil
        formatHint = nil; loopbackRoot = nil
    }

    init(source: any PlaybackByteSource) {
        self.source = source
        file = nil; loader = nil; playlist = nil; route = nil
        formatHint = nil; loopbackRoot = nil
    }

    init(plan: PlaybackNetworkPlan, budget: PlaybackNetworkBudget,
         configuration: URLSessionConfiguration = .ephemeral,
         onFailure: @escaping @Sendable (PlaybackNetworkError) -> Void) {
        loader = ProgressiveMediaLoader(plan: plan, budget: budget,
                                        configuration: configuration, onFailure: onFailure)
        file = nil; source = nil; playlist = nil; route = nil
        formatHint = nil; loopbackRoot = nil
    }

    init(playlist: Data, route: HLSLoopbackRoute, url: URL) {
        self.playlist = playlist; self.route = route
        length = Int64(playlist.count)
        file = nil; source = nil; loader = nil
        formatHint = "hls"
        loopbackRoot = url.deletingLastPathComponent().absoluteString
        primaryURL = url.absoluteString
    }

    var isReadCancelled: Bool {
        lock.lock(); defer { lock.unlock() }
        return cancelled || closed
    }

    func read(maximumBytes: Int) throws -> Data {
        try check()
        let count = min(maximumBytes, 65_536)
        let offset = position
        let data: Data
        if let file {
            do { data = try file.read(upToCount: count) ?? Data() }
            catch { throw PlaybackNetworkError.transportFailed }
        } else if let source {
            if length == nil {
                let info = try wait { try await source.information() }
                guard info.length > 0 else { throw PlaybackNetworkError.invalidResponse }
                length = info.length
            }
            if let length, offset >= length { return Data() }
            data = try wait {
                try await source.read(offset: offset, count: Int(min(Int64(count), self.length! - offset)))
            }
        } else if let playlist {
            let start = min(playlist.count, Int(offset))
            data = playlist.subdata(in: start..<min(playlist.count, start + count))
        } else if let loader {
            if let length, offset >= length { return Data() }
            let pending = try AudioProgressiveSink(start: offset, count: count)
            lock.lock()
            guard !cancelled, !closed else { lock.unlock(); throw CancellationError() }
            sink = pending
            loader.enqueue(pending)
            lock.unlock()
            let result = pending.wait()
            lock.lock()
            if sink === pending { sink = nil }
            lock.unlock()
            let value = try result.get()
            length = value.1.length
            data = value.0
        } else { throw PlaybackNetworkError.invalidResponse }
        guard data.count <= count, length == nil || Int64(data.count) <= length! - offset else {
            throw PlaybackNetworkError.invalidResponse
        }
        // Successful bytes belong to AVIO even if cancellation raced here.
        position += Int64(data.count)
        return data
    }

    func seek(_ request: AudioByteSeek) throws -> Int64 {
        try check()
        if length == nil, let source {
            let info = try wait { try await source.information() }
            guard info.length > 0 else { throw PlaybackNetworkError.invalidResponse }
            length = info.length
        }
        switch request {
        case .size:
            if let length { return length }
            // A bounded read obtains HTTP representation information.
            let saved = position
            _ = try read(maximumBytes: 1)
            position = saved
            guard let length else { throw AudioCoreError.seekFailed }
            return length
        case .offset(let offset, let origin):
            let start: Int64
            switch origin {
            case .start: start = 0
            case .current: start = position
            case .end: start = try seek(.size)
            }
            let (target, overflow) = start.addingReportingOverflow(offset)
            guard !overflow, target >= 0, length == nil || target <= length! else {
                throw AudioCoreError.seekFailed
            }
            if let file {
                do { try file.seek(toOffset: UInt64(target)) }
                catch { throw AudioCoreError.seekFailed }
            }
            position = target
            return target
        }
    }

    func cancelReads() {
        lock.lock()
        cancelled = true
        let reads = Array(reads.values), pending = sink
        lock.unlock()
        reads.forEach { $0.cancel() }
        if let pending { loader?.cancel(pending); pending.finish(.cancelled) }
    }

    func resumeReads() {
        lock.lock()
        if !closed { cancelled = false }
        lock.unlock()
    }

    func close() async throws {
        lock.lock()
        closed = true
        let reads = Array(reads.values)
        lock.unlock()
        cancelReads()
        source?.retire()
        route?.invalidate()
        loader?.invalidate()
        for read in reads { await read.value }
        await loader?.close()
        await route?.close()
        do { try file?.close() } catch { throw PlaybackNetworkError.transportFailed }
    }

    private func check() throws { if isReadCancelled { throw CancellationError() } }

    private func wait<T: Sendable>(_ operation: @escaping @Sendable () async throws -> T) throws -> T {
        let pending = AudioInputResult<T>(), id = UUID()
        lock.lock()
        guard !closed, !cancelled else { lock.unlock(); throw CancellationError() }
        // Register while holding the same lock as cancellation/close. The task
        // may be queued, but its read lease already belongs to this input.
        let task = Task { [self] in
            do {
                try Task.checkCancellation()
                let value = try await operation()
                try Task.checkCancellation()
                pending.finish(.success(value))
            } catch { pending.finish(.failure(error)) }
            lock.lock(); reads[id] = nil; lock.unlock()
        }
        reads[id] = task
        lock.unlock()
        return try pending.wait().get()
    }
}

private final class AudioInputResult<T>: @unchecked Sendable {
    private let condition = NSCondition()
    private var result: Result<T, Error>?
    func finish(_ value: Result<T, Error>) {
        condition.lock(); defer { condition.unlock() }
        guard result == nil else { return }
        result = value; condition.broadcast()
    }
    func wait() -> Result<T, Error> {
        condition.lock(); defer { condition.unlock() }
        while result == nil { condition.wait() }
        return result!
    }
}

private final class AudioProgressiveSink: ProgressiveMediaSink, @unchecked Sendable {
    let range: ProgressiveReadRange?
    private let result = AudioInputResult<(Data, ProgressiveContentInformation)>()
    private var information: ProgressiveContentInformation?
    private var data = Data()
    private let maximum: Int
    init(start: Int64, count: Int) throws {
        maximum = count
        range = try .init(requestedOffset: start, currentOffset: start, requestedLength: count, toEnd: false)
    }
    func receive(_ information: ProgressiveContentInformation) { self.information = information }
    func receive(_ bytes: Data) {
        guard bytes.count <= maximum - data.count else { finish(.resourceBudgetExceeded); return }
        data.append(bytes)
    }
    func finish(_ error: PlaybackNetworkError?) {
        if let error { result.finish(.failure(error)) }
        else if let information { result.finish(.success((data, information))) }
        else { result.finish(.failure(PlaybackNetworkError.invalidResponse)) }
    }
    func wait() -> Result<(Data, ProgressiveContentInformation), Error> { result.wait() }
}
#endif
