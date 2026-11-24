#if os(iOS) || os(macOS)
import Foundation
import MeowAudio
import MeowContracts

/// Adapts local files, in-process growing byte sources, and rewritten HLS playlists to FFmpeg.
/// Remote HTTP audio uses `PlaybackHTTPStreamInput` directly.
final class PlaybackAudioInput: AudioByteInput, @unchecked Sendable {
    let formatHint: String?
    let loopbackRoot: String?
    private(set) var primaryURL: String?
    private let source: (any PlaybackByteSource)?
    private let playlist: Data?
    private let route: HLSLoopbackRoute?
    private let lock = NSLock()
    private var file: FileHandle?
    private var cancelled = false
    private var closed = false
    private var position: Int64 = 0
    private var length: Int64?
    private var reads: [UUID: Task<Void, Never>] = [:]

    init(file url: URL) throws {
        do {
            let handle = try FileHandle(forReadingFrom: url)
            let total = Int64(try handle.seekToEnd())
            try handle.seek(toOffset: 0)
            self.file = handle
            self.length = total
        } catch { throw PlaybackNetworkError.transportFailed }
        source = nil; playlist = nil; route = nil
        formatHint = nil; loopbackRoot = nil
    }

    init(source: any PlaybackByteSource) {
        self.source = source
        file = nil; playlist = nil; route = nil
        formatHint = nil; loopbackRoot = nil
    }

    init(playlist: Data, route: HLSLoopbackRoute, url: URL) {
        self.playlist = playlist; self.route = route
        length = Int64(playlist.count)
        file = nil; source = nil
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
        if let playlist {
            return try lock.withLock {
                try checkLocked()
                let start = min(playlist.count, Int(position))
                let data = playlist.subdata(in: start..<min(playlist.count, start + count))
                position += Int64(data.count)
                return data
            }
        }
        if let source {
            let (offset, knownLength): (Int64, Int64?) = try lock.withLock {
                try checkLocked()
                return (position, length)
            }
            let total: Int64
            if let knownLength {
                total = knownLength
            } else {
                let info = try wait { try await source.information() }
                guard info.length > 0 else { throw PlaybackNetworkError.invalidResponse }
                lock.withLock { length = info.length }
                total = info.length
            }
            if offset >= total { return Data() }
            let requestCount = Int(min(Int64(count), total - offset))
            let data = try wait {
                try await source.read(offset: offset, count: requestCount)
            }
            guard data.count <= count, Int64(data.count) <= total - offset else {
                throw PlaybackNetworkError.invalidResponse
            }
            lock.withLock { position = offset + Int64(data.count) }
            return data
        }
        return try lock.withLock {
            try checkLocked()
            guard let file else { throw PlaybackNetworkError.invalidResponse }
            let offset = position
            let data: Data
            do { data = try file.read(upToCount: count) ?? Data() }
            catch { throw PlaybackNetworkError.transportFailed }
            guard data.count <= count, length == nil || Int64(data.count) <= length! - offset else {
                throw PlaybackNetworkError.invalidResponse
            }
            position = offset + Int64(data.count)
            return data
        }
    }

    func seek(_ request: AudioByteSeek) throws -> Int64 {
        try check()
        if let source {
            let needsInfo = lock.withLock { length == nil }
            if needsInfo {
                let info = try wait { try await source.information() }
                guard info.length > 0 else { throw PlaybackNetworkError.invalidResponse }
                lock.withLock { length = info.length }
            }
        }
        return try lock.withLock {
            try checkLocked()
            switch request {
            case .size:
                guard let length else { throw AudioCoreError.seekFailed }
                return length
            case .offset(let offset, let origin):
                let start: Int64
                switch origin {
                case .start: start = 0
                case .current: start = position
                case .end:
                    guard let length else { throw AudioCoreError.seekFailed }
                    start = length
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
    }

    func cancelReads() {
        lock.lock()
        cancelled = true
        let activeReads = Array(reads.values)
        lock.unlock()
        activeReads.forEach { $0.cancel() }
    }

    func resumeReads() {
        lock.lock()
        if !closed { cancelled = false }
        lock.unlock()
    }

    private func beginClose() -> ([Task<Void, Never>], FileHandle?) {
        lock.withLock {
            closed = true
            cancelled = true
            let tasks = Array(reads.values)
            let retiringFile = file
            file = nil
            return (tasks, retiringFile)
        }
    }

    func close() async throws {
        let (activeReads, handle) = beginClose()
        activeReads.forEach { $0.cancel() }
        source?.retire()
        route?.invalidate()
        for read in activeReads { await read.value }
        await route?.close()
        do { try handle?.close() } catch { throw PlaybackNetworkError.transportFailed }
    }

    private func check() throws { if isReadCancelled { throw CancellationError() } }
    private func checkLocked() throws { if cancelled || closed { throw CancellationError() } }

    private func removeRead(_ id: UUID) {
        lock.withLock { reads[id] = nil }
    }

    private func wait<T: Sendable>(_ operation: @escaping @Sendable () async throws -> T) throws -> T {
        let pending = AudioInputResult<T>(), id = UUID()
        lock.lock()
        guard !closed, !cancelled else { lock.unlock(); throw CancellationError() }
        let task = Task { [self] in
            do {
                try Task.checkCancellation()
                let value = try await operation()
                try Task.checkCancellation()
                pending.finish(.success(value))
            } catch { pending.finish(.failure(error)) }
            self.removeRead(id)
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
#endif
