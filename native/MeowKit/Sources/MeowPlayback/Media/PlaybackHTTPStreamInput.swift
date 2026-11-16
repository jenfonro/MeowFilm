#if os(iOS) || os(macOS)
import Foundation
import MeowAudio

/// Headerless automatic HTTP retains streaming/non-Range support. A bounded
/// window applies backpressure; all redirects still use the shared policy.
final class PlaybackHTTPStreamInput: NSObject, AudioByteInput, URLSessionDataDelegate, @unchecked Sendable {
    let formatHint: String? = nil
    let loopbackRoot: String? = nil
    private let plan: PlaybackNetworkPlan
    private let budget: PlaybackNetworkBudget
    private let beforeSeekRetirement: (@Sendable (PlaybackHTTPStreamInput) -> Void)?
    private let condition = NSCondition()
    private var session: URLSession!
    private var task: URLSessionDataTask?
    private var reservation: PlaybackNetworkReservation?
    private var storage: PlaybackAuxiliaryReservation?
    private var policy: PlaybackHTTPRequestPolicy
    private var information: ProgressiveContentInformation?
    private var buffer = Data()
    private var position: Int64 = 0
    private var requestedStart: Int64 = 0
    private var received: Int64 = 0
    private var expected: Int64?
    private var responseReady = false
    private var ended = false
    private var suspended = false
    private var cancelled = false
    private var closed = false
    private var checkedPrefix = false
    private var failure: Error?
    private var closeWaiters: [CheckedContinuation<Void, Never>] = []
    var isReadCancelled: Bool { condition.lock(); defer { condition.unlock() }; return cancelled || closed }

    init(plan: PlaybackNetworkPlan, budget: PlaybackNetworkBudget,
         configuration: URLSessionConfiguration = .ephemeral,
         beforeSeekRetirement: (@Sendable (PlaybackHTTPStreamInput) -> Void)? = nil) {
        self.plan = plan; self.budget = budget; policy = PlaybackHTTPRequestPolicy(plan: plan)
        self.beforeSeekRetirement = beforeSeekRetirement
        super.init()
        let config = configuration.copy() as! URLSessionConfiguration
        config.httpCookieStorage = nil; config.httpShouldSetCookies = false
        config.urlCredentialStorage = nil; config.urlCache = nil; config.httpAdditionalHeaders = nil
        config.timeoutIntervalForRequest = 30; config.timeoutIntervalForResource = 3600
        let queue = OperationQueue(); queue.maxConcurrentOperationCount = 1
        session = URLSession(configuration: config, delegate: self, delegateQueue: queue)
    }

    private func check() throws {
        if closed || cancelled { throw CancellationError() }
        if let failure { throw failure }
    }

    private func start() throws {
        guard task == nil, !ended else { return }
        if storage == nil {
            storage = budget.acquireAuxiliary(.storage, bytes: budget.limits.windowBytes)
            guard storage != nil else { throw PlaybackNetworkError.resourceBudgetExceeded }
        }
        guard let reservation = budget.acquire(host: plan.url.host!, bytes: 1) else {
            throw PlaybackNetworkError.resourceBudgetExceeded
        }
        self.reservation = reservation
        policy = PlaybackHTTPRequestPolicy(plan: plan)
        let start = position + Int64(buffer.count)
        if let information, start == information.length {
            reservation.complete(); self.reservation = nil
            ended = true
            return
        }
        var request = try policy.request(start: start, end: Int64.max - 1, validator: information?.validator)
        request.setValue("bytes=\(start)-", forHTTPHeaderField: "Range")
        task = session.dataTask(with: request)
        requestedStart = start; received = 0; expected = nil; responseReady = false
        task!.resume()
    }

    func read(maximumBytes: Int) throws -> Data {
        condition.lock(); defer { condition.unlock() }
        try check()
        try start()
        while (buffer.isEmpty || (!checkedPrefix && position == 0 && buffer.count < 16)), !ended {
            try check()
            // A cancelled seek can finish after its caller has returned. Join
            // that request and continue the same representation, including
            // bytes already retained before cancellation.
            if task == nil { try start(); if ended { break } }
            condition.wait()
        }
        try check()
        if !checkedPrefix, position == 0 {
            guard !ProgressiveMediaResponse.isPlaylistPrefix(buffer) else {
                throw PlaybackNetworkError.hlsRequiresExplicitFormat
            }
            checkedPrefix = true
        }
        let count = min(maximumBytes, buffer.count)
        let bytes = buffer.prefix(count)
        buffer.removeFirst(count); position += Int64(count)
        if suspended, buffer.count <= budget.limits.windowBytes / 2 {
            suspended = false; task?.resume()
        }
        return Data(bytes)
    }

    func seek(_ request: AudioByteSeek) throws -> Int64 {
        condition.lock(); defer { condition.unlock() }
        try check()
        try start()
        while !responseReady, !ended { try check(); condition.wait() }
        try check()
        switch request {
        case .size:
            guard let information else { throw AudioCoreError.unsupportedFormat }
            return information.length
        case .offset(let offset, let origin):
            // The capability probe must describe the actual HTTP response.
            guard information?.supportsRanges == true else { throw AudioCoreError.unsupportedFormat }
            let base: Int64
            switch origin {
            case .start: base = 0
            case .current: base = position
            case .end:
                guard let information else { throw AudioCoreError.unsupportedFormat }
                base = information.length
            }
            let (target, overflow) = base.addingReportingOverflow(offset)
            guard !overflow, target >= 0, information == nil || target <= information!.length else {
                throw AudioCoreError.seekFailed
            }
            if target != position {
                let retiring = task, wasSuspended = suspended
                suspended = false
                // URLSession cancellation can wait for protocol teardown.
                // Input revocation must remain able to wake this operation.
                condition.unlock()
                beforeSeekRetirement?(self)
                retiring?.cancel()
                if wasSuspended { retiring?.resume() }
                condition.lock()
                while task != nil { try check(); condition.wait() }
                try check()
                buffer.removeAll(); position = target; ended = information?.length == target
                responseReady = false; checkedPrefix = target != 0; failure = nil
            }
            return position
        }
    }

    func cancelReads() { condition.lock(); cancelled = true; condition.broadcast(); condition.unlock() }
    func resumeReads() { condition.lock(); if !closed { cancelled = false }; condition.unlock() }
    func close() async throws {
        await withCheckedContinuation { continuation in
            condition.lock()
            closed = true; cancelled = true; buffer.removeAll()
            storage?.complete(); storage = nil
            condition.broadcast()
            let retiring = task, wasSuspended = suspended
            suspended = false
            if retiring == nil { continuation.resume() }
            else { closeWaiters.append(continuation) }
            condition.unlock()
            retiring?.cancel()
            if wasSuspended { retiring?.resume() }
            session.invalidateAndCancel()
        }
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        condition.lock(); defer { condition.unlock() }
        do {
            guard !closed, dataTask === task, let http = response as? HTTPURLResponse,
                  http.url == policy.current else { throw PlaybackNetworkError.redirectDenied }
            let mime = (http.mimeType ?? "application/octet-stream").lowercased()
            if PlaybackNetworkPlan.hlsMIMEs.contains(mime) { throw PlaybackNetworkError.hlsRequiresExplicitFormat }
            guard !["text/html", "text/plain", "application/json"].contains(mime),
                  http.value(forHTTPHeaderField: "Content-Encoding") == nil ||
                    http.value(forHTTPHeaderField: "Content-Encoding")?.lowercased() == "identity" else {
                throw PlaybackNetworkError.unsupportedContent
            }
            let length: Int64?, ranges: Bool
            if http.statusCode == 206 {
                guard let raw = http.value(forHTTPHeaderField: "Content-Range"),
                      let value = ProgressiveMediaResponse.parseContentRange(raw),
                      value.start == requestedStart, value.end == value.total - 1 else {
                    throw PlaybackNetworkError.invalidResponse
                }
                length = value.total; expected = value.total - requestedStart; ranges = true
            } else if http.statusCode == 200, requestedStart == 0 {
                length = http.expectedContentLength > 0 ? http.expectedContentLength : nil
                expected = length; ranges = false
            } else if http.statusCode == 200 { throw PlaybackNetworkError.rangeUnsupported }
            else { throw PlaybackNetworkError.httpStatus(http.statusCode) }
            if let length {
                let validator = http.value(forHTTPHeaderField: "ETag") ?? http.value(forHTTPHeaderField: "Last-Modified")
                if let old = information, old.length != length || old.mime != mime ||
                    (old.validator != nil && old.validator != validator) { throw PlaybackNetworkError.resourceChanged }
                information = .init(length: length, mime: mime, supportsRanges: ranges, validator: validator)
            }
            responseReady = true; condition.broadcast(); completionHandler(.allow)
        } catch {
            failure = error; responseReady = true; condition.broadcast(); completionHandler(.cancel)
        }
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        condition.lock(); defer { condition.unlock() }
        guard !closed, dataTask === task, failure == nil else { return }
        guard data.count <= budget.limits.windowBytes - buffer.count,
              expected == nil || Int64(data.count) <= expected! - received else {
            failure = PlaybackNetworkError.resourceBudgetExceeded; dataTask.cancel(); condition.broadcast(); return
        }
        buffer.append(data); received += Int64(data.count)
        if !suspended, buffer.count >= budget.limits.windowBytes - 65_536 {
            suspended = true; dataTask.suspend()
        }
        condition.broadcast()
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        condition.lock(); defer { condition.unlock() }
        guard task === self.task else { return }
        self.task = nil; reservation?.complete(); reservation = nil
        if !closed, error == nil {
            if let expected, received != expected { failure = PlaybackNetworkError.invalidResponse }
            ended = true
        } else if !closed, (error as NSError?)?.code != NSURLErrorCancelled {
            failure = PlaybackNetworkError.transportFailed; ended = true
        }
        condition.broadcast()
        let waiters = closeWaiters; closeWaiters.removeAll(); waiters.forEach { $0.resume() }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        condition.lock(); defer { condition.unlock() }
        do {
            guard !closed, let url = request.url else { throw PlaybackNetworkError.redirectDenied }
            try policy.redirect(to: url)
            var redirected = try policy.request(start: requestedStart, end: Int64.max - 1, validator: information?.validator)
            redirected.setValue("bytes=\(requestedStart)-", forHTTPHeaderField: "Range")
            completionHandler(redirected)
        } catch { failure = error; completionHandler(nil); condition.broadcast() }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        #if os(iOS) || os(macOS)
        if challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust {
            completionHandler(.performDefaultHandling, nil); return
        }
        #endif
        completionHandler(.cancelAuthenticationChallenge, nil)
    }
}
#endif
