#if os(iOS) || os(macOS)
import Foundation
import MeowAudio

/// Unified streaming HTTP audio input (headerless or custom headers).
/// Supports full 206, CDN chunked sub-range 206 (auto-continuation), and
/// non-Range 200 OK streams with an O(1) chunk FIFO, watermark backpressure,
/// and lazy URLSession creation.
final class PlaybackHTTPStreamInput: NSObject, AudioByteInput, URLSessionDataDelegate, @unchecked Sendable {
    private static let windowBytes = 256 * 1024
    let formatHint: String? = nil
    let loopbackRoot: String? = nil
    private let plan: PlaybackNetworkPlan
    private let condition = NSCondition()
    private var session: URLSession?
    private var task: URLSessionDataTask?
    private var policy: PlaybackHTTPRequestPolicy
    private var resolvedPolicy: PlaybackHTTPRequestPolicy?
    private var information: PlaybackHTTPContentInformation?
    private var chunks: [Data] = []
    private var headIndex = 0
    private var headOffset = 0
    private var bufferedBytes = 0
    private var position: Int64 = 0
    private var requestedStart: Int64 = 0
    private var received: Int64 = 0
    private var segmentExpected: Int64?
    private var responseReady = false
    private var ended = false
    private var suspended = false
    private var cancelled = false
    private var closed = false
    private var checkedPrefix = false
    private var failure: Error?
    private var closeWaiters: [CheckedContinuation<Void, Never>] = []

    var isReadCancelled: Bool { condition.lock(); defer { condition.unlock() }; return cancelled || closed }

    init(plan: PlaybackNetworkPlan) {
        self.plan = plan
        self.policy = PlaybackHTTPRequestPolicy(plan: plan)
        super.init()
    }

    private func ensureSessionLocked() -> URLSession {
        if let session { return session }
        let config = URLSessionConfiguration.ephemeral
        config.httpCookieStorage = nil
        config.httpShouldSetCookies = false
        config.urlCredentialStorage = nil
        config.urlCache = nil
        config.httpAdditionalHeaders = nil
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 3600
        let queue = OperationQueue()
        queue.maxConcurrentOperationCount = 1
        let created = URLSession(configuration: config, delegate: self, delegateQueue: queue)
        session = created
        return created
    }

    private func checkLocked() throws {
        if closed || cancelled { throw CancellationError() }
        if let failure { throw failure }
    }

    private func clearBufferLocked() {
        chunks.removeAll(keepingCapacity: true)
        headIndex = 0
        headOffset = 0
        bufferedBytes = 0
    }

    private func compactQueueIfNeededLocked() {
        if headIndex == chunks.count {
            chunks.removeAll(keepingCapacity: true)
            headIndex = 0
            headOffset = 0
        } else if headIndex >= 32, headIndex * 2 >= chunks.count {
            chunks.removeFirst(headIndex)
            headIndex = 0
        }
    }

    private func peekPrefixLocked(_ limit: Int) -> Data {
        var result = Data()
        result.reserveCapacity(min(limit, bufferedBytes))
        var idx = headIndex
        var off = headOffset
        while idx < chunks.count && result.count < limit {
            let chunk = chunks[idx]
            let avail = chunk.count - off
            let take = min(avail, limit - result.count)
            let start = chunk.index(chunk.startIndex, offsetBy: off)
            let end = chunk.index(start, offsetBy: take)
            result.append(chunk[start..<end])
            idx += 1
            off = 0
        }
        return result
    }

    private func consumeLocked(_ maximumBytes: Int) -> Data {
        let total = min(maximumBytes, bufferedBytes)
        guard total > 0, headIndex < chunks.count else { return Data() }
        let first = chunks[headIndex]
        let firstAvail = first.count - headOffset
        if total <= firstAvail {
            let out: Data
            if headOffset == 0 && total == first.count {
                out = first
            } else {
                let start = first.index(first.startIndex, offsetBy: headOffset)
                let end = first.index(start, offsetBy: total)
                out = first.subdata(in: start..<end)
            }
            headOffset += total
            if headOffset == first.count {
                headIndex += 1
                headOffset = 0
            }
            bufferedBytes -= total
            compactQueueIfNeededLocked()
            return out
        }
        var out = Data(capacity: total)
        var remaining = total
        while remaining > 0, headIndex < chunks.count {
            let chunk = chunks[headIndex]
            let avail = chunk.count - headOffset
            let take = min(avail, remaining)
            let start = chunk.index(chunk.startIndex, offsetBy: headOffset)
            let end = chunk.index(start, offsetBy: take)
            out.append(chunk[start..<end])
            headOffset += take
            remaining -= take
            if headOffset == chunk.count {
                headIndex += 1
                headOffset = 0
            }
        }
        bufferedBytes -= total
        compactQueueIfNeededLocked()
        return out
    }

    private func discardLocked(_ count: Int) {
        var remaining = min(count, bufferedBytes)
        bufferedBytes -= remaining
        while remaining > 0, headIndex < chunks.count {
            let avail = chunks[headIndex].count - headOffset
            if remaining < avail {
                headOffset += remaining
                remaining = 0
            } else {
                remaining -= avail
                headIndex += 1
                headOffset = 0
            }
        }
        compactQueueIfNeededLocked()
    }

    private func startLocked() throws {
        guard task == nil, !ended else { return }
        let nextStart = position + Int64(bufferedBytes)
        if let total = information?.length, nextStart >= total {
            ended = true
            return
        }
        if var resolved = resolvedPolicy {
            resolved.resetRedirects()
            policy = resolved
        } else {
            policy = PlaybackHTTPRequestPolicy(plan: plan)
        }
        let request = try policy.request(start: nextStart)
        let activeSession = ensureSessionLocked()
        let dataTask = activeSession.dataTask(with: request)
        task = dataTask
        requestedStart = nextStart
        received = 0
        segmentExpected = nil
        responseReady = false
        suspended = false
        dataTask.resume()
    }

    func read(maximumBytes: Int) throws -> Data {
        condition.lock(); defer { condition.unlock() }
        try checkLocked()
        try startLocked()
        while (bufferedBytes == 0 || (!checkedPrefix && position == 0 && bufferedBytes < 16)), !ended {
            try checkLocked()
            if task == nil {
                try startLocked()
                if ended { break }
            }
            condition.wait()
        }
        try checkLocked()
        if !checkedPrefix, position == 0 {
            guard !PlaybackNetworkPlan.isPlaylistPrefix(peekPrefixLocked(16)) else {
                throw PlaybackNetworkError.hlsRequiresExplicitFormat
            }
            checkedPrefix = true
        }
        let bytes = consumeLocked(maximumBytes)
        position += Int64(bytes.count)
        if suspended, bufferedBytes <= Self.windowBytes / 2 {
            suspended = false
            task?.resume()
        }
        return bytes
    }

    func seek(_ request: AudioByteSeek) throws -> Int64 {
        condition.lock(); defer { condition.unlock() }
        try checkLocked()
        if information == nil {
            try startLocked()
            while !responseReady, !ended {
                try checkLocked()
                condition.wait()
            }
            try checkLocked()
        }
        switch request {
        case .size:
            guard let total = information?.length else { throw AudioCoreError.unsupportedFormat }
            return total
        case .offset(let offset, let origin):
            guard information?.supportsRanges == true else { throw AudioCoreError.unsupportedFormat }
            let base: Int64
            switch origin {
            case .start: base = 0
            case .current: base = position
            case .end:
                guard let total = information?.length else { throw AudioCoreError.unsupportedFormat }
                base = total
            }
            let (target, overflow) = base.addingReportingOverflow(offset)
            guard !overflow, target >= 0, information?.length == nil || target <= information!.length! else {
                throw AudioCoreError.seekFailed
            }
            if target == position {
                return position
            }
            // Fast path: target lies inside already-buffered unread window; no new HTTP request needed.
            if target > position, target <= position + Int64(bufferedBytes) {
                discardLocked(Int(target - position))
                position = target
                checkedPrefix = true
                if suspended, bufferedBytes <= Self.windowBytes / 2 {
                    suspended = false
                    task?.resume()
                }
                return position
            }
            let retiring = task
            let wasSuspended = suspended
            suspended = false
            if retiring != nil {
                condition.unlock()
                retiring?.cancel()
                if wasSuspended { retiring?.resume() }
                condition.lock()
                while task != nil {
                    if closed || cancelled { throw CancellationError() }
                    condition.wait()
                }
                if closed || cancelled { throw CancellationError() }
            }
            clearBufferLocked()
            position = target
            ended = information?.length == target
            responseReady = information != nil
            checkedPrefix = target != 0
            failure = nil
            return position
        }
    }

    func cancelReads() {
        condition.lock()
        cancelled = true
        condition.broadcast()
        condition.unlock()
    }

    func resumeReads() {
        condition.lock()
        if !closed { cancelled = false }
        condition.unlock()
    }

    func close() async throws {
        await withCheckedContinuation { continuation in
            condition.lock()
            closed = true
            cancelled = true
            clearBufferLocked()
            condition.broadcast()
            let retiring = task
            let wasSuspended = suspended
            let activeSession = session
            session = nil
            suspended = false
            if retiring == nil {
                continuation.resume()
            } else {
                closeWaiters.append(continuation)
            }
            condition.unlock()
            retiring?.cancel()
            if wasSuspended { retiring?.resume() }
            activeSession?.invalidateAndCancel()
        }
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        condition.lock(); defer { condition.unlock() }
        do {
            guard !closed, dataTask === task, let http = response as? HTTPURLResponse else {
                throw PlaybackNetworkError.invalidResponse
            }
            let rawMime = http.value(forHTTPHeaderField: "Content-Type")?
                .split(separator: ";", maxSplits: 1).first.map(String.init)?
                .trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
            let mime = (rawMime?.isEmpty == false ? rawMime! : (http.mimeType ?? "application/octet-stream").lowercased())
            if PlaybackNetworkPlan.hlsMIMEs.contains(mime) { throw PlaybackNetworkError.hlsRequiresExplicitFormat }
            guard !["text/html", "text/plain", "application/json"].contains(mime) else {
                throw PlaybackNetworkError.unsupportedContent
            }
            let totalLength: Int64?
            let ranges: Bool
            if http.statusCode == 206 {
                // Accept both open-to-EOF 206 and CDN chunked sub-range 206 (end < total - 1).
                guard let raw = http.value(forHTTPHeaderField: "Content-Range"),
                      let value = PlaybackNetworkPlan.parseContentRange(raw),
                      value.start == requestedStart, value.end >= value.start, value.end < value.total else {
                    throw PlaybackNetworkError.invalidResponse
                }
                totalLength = value.total
                segmentExpected = value.end - value.start + 1
                ranges = true
            } else if http.statusCode == 200, requestedStart == 0 {
                let expectedLength = http.expectedContentLength > 0 ? http.expectedContentLength : nil
                totalLength = expectedLength
                segmentExpected = expectedLength
                ranges = false
            } else if http.statusCode == 200 {
                throw information?.supportsRanges == true ? PlaybackNetworkError.resourceChanged : .rangeUnsupported
            } else {
                throw PlaybackNetworkError.httpStatus(http.statusCode)
            }
            if let old = information {
                if let oldLen = old.length, let totalLength, oldLen != totalLength {
                    throw PlaybackNetworkError.resourceChanged
                }
            } else {
                information = .init(length: totalLength, mime: mime, supportsRanges: ranges)
            }
            resolvedPolicy = policy
            responseReady = true
            condition.broadcast()
            completionHandler(.allow)
        } catch {
            failure = error
            responseReady = true
            condition.broadcast()
            completionHandler(.cancel)
        }
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        condition.lock(); defer { condition.unlock() }
        guard !closed, dataTask === task, failure == nil, !data.isEmpty else { return }
        if let segmentExpected, Int64(data.count) > segmentExpected - received {
            failure = PlaybackNetworkError.invalidResponse
            dataTask.cancel()
            condition.broadcast()
            return
        }
        chunks.append(data)
        bufferedBytes += data.count
        received += Int64(data.count)
        if !suspended, bufferedBytes >= Self.windowBytes {
            suspended = true
            dataTask.suspend()
        }
        condition.broadcast()
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        condition.lock()
        guard task === self.task else { condition.unlock(); return }
        self.task = nil
        suspended = false
        if !closed, error == nil {
            if let segmentExpected, received != segmentExpected {
                failure = PlaybackNetworkError.invalidResponse
                ended = true
            } else if let total = information?.length, requestedStart + received < total {
                // Chunked CDN 206 sub-range finished cleanly; keep ended=false so next read continues.
                ended = false
            } else {
                ended = true
            }
        } else if !closed, (error as NSError?)?.code != NSURLErrorCancelled {
            failure = PlaybackNetworkError.transportFailed
            ended = true
        }
        condition.broadcast()
        let waiters = closeWaiters
        closeWaiters.removeAll()
        condition.unlock()
        waiters.forEach { $0.resume() }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        condition.lock(); defer { condition.unlock() }
        do {
            guard !closed, let url = request.url else { throw PlaybackNetworkError.invalidURL }
            try policy.redirect(to: url)
            let redirected = try policy.request(start: requestedStart)
            completionHandler(redirected)
        } catch {
            failure = error
            completionHandler(nil)
            condition.broadcast()
        }
    }

    deinit {
        session?.invalidateAndCancel()
    }
}
#endif
