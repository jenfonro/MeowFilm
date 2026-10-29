import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

/// The AVFoundation adapter and deterministic test sinks implement this small
/// byte boundary. Every method is called on the loader's serial queue.
protocol ProgressiveMediaSink: AnyObject {
    var range: ProgressiveReadRange? { get } // nil = content-information only
    func receive(_ information: ProgressiveContentInformation)
    func receive(_ data: Data)
    func finish(_ error: PlaybackNetworkError?)
}

/// One item/generation. No whole-file accumulator, cache, HTTP listener or
/// per-byte actor hops. Serial delegate delivery provides bounded backpressure.
final class ProgressiveMediaLoader: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    let queue: DispatchQueue
    let plan: PlaybackNetworkPlan
    private let queueKey = DispatchSpecificKey<Bool>()
    private let budget: PlaybackNetworkBudget
    private let configuration: URLSessionConfiguration
    private let onFailure: @Sendable (PlaybackNetworkError) -> Void
    private var session: URLSession?
    private var observation: UUID?
    private var invalidated = false
    private var reportedFailure = false
    private var loads: [ObjectIdentifier: Load] = [:]
    private var pending: [ObjectIdentifier] = [] // bounded, round-robin between windows
    private var transfers: [Int: Transfer] = [:]
    private var information: ProgressiveContentInformation?
    private var pumping = false
    private var pumpAgain = false

    private final class Load {
        let sink: ProgressiveMediaSink
        var position: Int64
        var taskID: Int?
        var wait: DispatchWorkItem?
        var waitID: UUID?
        init(_ sink: ProgressiveMediaSink) {
            self.sink = sink
            position = sink.range?.start ?? 0
        }
    }

    private final class Transfer {
        let task: URLSessionDataTask
        let sinkID: ObjectIdentifier
        let start: Int64
        let end: Int64
        let reservation: PlaybackNetworkReservation
        var policy: PlaybackHTTPRequestPolicy
        var response: ProgressiveMediaResponse?
        var received: Int64 = 0
        var prefix = Data() // at most 16 bytes, charged within the window
        var checkedPrefix = false
        init(task: URLSessionDataTask, sinkID: ObjectIdentifier, start: Int64, end: Int64,
             reservation: PlaybackNetworkReservation, policy: PlaybackHTTPRequestPolicy) {
            self.task = task; self.sinkID = sinkID; self.start = start; self.end = end
            self.reservation = reservation; self.policy = policy
        }
    }

    init(plan: PlaybackNetworkPlan, budget: PlaybackNetworkBudget,
         configuration: URLSessionConfiguration = .ephemeral,
         onFailure: @escaping @Sendable (PlaybackNetworkError) -> Void) {
        precondition(plan.route == .progressive)
        self.plan = plan; self.budget = budget; self.onFailure = onFailure
        // Even injected test configurations cannot opt into shared credentials.
        let config = configuration.copy() as! URLSessionConfiguration
        config.httpCookieStorage = nil
        config.httpShouldSetCookies = false
        config.urlCredentialStorage = nil
        config.urlCache = nil
        config.requestCachePolicy = .reloadIgnoringLocalCacheData
        config.httpAdditionalHeaders = nil
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 60
        config.httpMaximumConnectionsPerHost = budget.limits.tasksPerHost
        self.configuration = config
        queue = DispatchQueue(label: "meow.playback.bytes.\(UUID().uuidString)")
        super.init()
        queue.setSpecific(key: queueKey, value: true)
        observation = budget.observe { [weak self] in
            guard let self else { return }
            self.queue.async { [weak self] in self?.pump() }
        }
    }

    func enqueue(_ sink: ProgressiveMediaSink) {
        performSynchronized {
            guard !invalidated else { sink.finish(.cancelled); return }
            guard !reportedFailure else { sink.finish(.transportFailed); return }
            let id = ObjectIdentifier(sink)
            guard loads[id] == nil else { return }
            guard loads.count < budget.limits.requestsPerAsset else {
                sink.finish(.resourceBudgetExceeded)
                failAsset(.resourceBudgetExceeded)
                return
            }
            loads[id] = Load(sink)
            pending.append(id)
            pump()
        }
    }

    /// AVFoundation already cancelled this request: no finishLoading callback.
    func cancel(_ sink: ProgressiveMediaSink) {
        performSynchronized { remove(ObjectIdentifier(sink), finish: false, error: nil) }
    }

    /// Synchronous revocation fence. After return no sink can receive another
    /// byte and no redirect/new task can start. Actual task leases remain held
    /// until URLSession delivers didComplete, even across item replacement.
    func invalidate() {
        performSynchronized {
            guard !invalidated else { return }
            invalidated = true
            if let observation { budget.unobserve(observation); self.observation = nil }
            for id in Array(loads.keys) { remove(id, finish: false, error: nil) }
            session?.invalidateAndCancel()
        }
    }

    func performSynchronized(_ body: () -> Void) {
        if DispatchQueue.getSpecific(key: queueKey) == true { body() } else { queue.sync(execute: body) }
    }

    private func makeSession() -> URLSession {
        if let session { return session }
        let operationQueue = OperationQueue()
        operationQueue.maxConcurrentOperationCount = 1
        operationQueue.underlyingQueue = queue
        let created = URLSession(configuration: configuration, delegate: self, delegateQueue: operationQueue)
        session = created
        return created
    }

    private func pump() {
        guard !invalidated, !reportedFailure else { return }
        guard !pumping else { pumpAgain = true; return }
        pumping = true
        defer {
            pumping = false
            if pumpAgain {
                pumpAgain = false
                queue.async { [weak self] in self?.pump() }
            }
        }
        for id in pending {
            guard let load = loads[id], load.taskID == nil else { continue }
            if let information {
                load.sink.receive(information)
                guard !invalidated, !reportedFailure, loads[id] === load, load.taskID == nil else { continue }
                if load.sink.range == nil || load.position >= information.length {
                    remove(id, finish: true, error: nil)
                    continue
                }
            }
            if let end = load.sink.range?.endExclusive, load.position >= end {
                remove(id, finish: true, error: nil)
                continue
            }
            let requestedEnd = load.sink.range?.endExclusive
            let remaining = requestedEnd.map { $0 - load.position } ?? Int64(budget.limits.windowBytes)
            let bytes = load.sink.range == nil ? 2 : Int(min(Int64(budget.limits.windowBytes), remaining))
            guard bytes > 0, load.position <= Int64.max - Int64(bytes) else {
                failAsset(.invalidResponse); return
            }
            let end = min(load.position + Int64(bytes) - 1, (information?.length ?? Int64.max) - 1)
            let count = Int(end - load.position + 1)
            let origin: PlaybackHTTPOrigin
            do { origin = try PlaybackHTTPOrigin(plan.url) } catch { failAsset(.invalidURL); return }
            guard let reservation = budget.acquire(host: origin.host, bytes: count) else {
                armWait(load, id: id)
                continue
            }
            load.wait?.cancel(); load.wait = nil; load.waitID = nil
            do {
                let policy = PlaybackHTTPRequestPolicy(plan: plan)
                let request = try policy.request(start: load.position, end: end, validator: information?.validator)
                let task = makeSession().dataTask(with: request)
                let transfer = Transfer(task: task, sinkID: id, start: load.position, end: end,
                                        reservation: reservation, policy: policy)
                transfers[task.taskIdentifier] = transfer
                load.taskID = task.taskIdentifier
                pending.removeAll { $0 == id }
                task.resume()
            } catch {
                // No task was started; this is the one other legal release.
                reservation.complete()
                failAsset(.invalidResponse); return
            }
        }
    }

    private func armWait(_ load: Load, id: ObjectIdentifier) {
        guard load.wait == nil else { return }
        let waitID = UUID()
        load.waitID = waitID
        let work = DispatchWorkItem { [weak self, weak load] in
            guard let self, let load, self.loads[id] === load, load.taskID == nil,
                  load.waitID == waitID else { return }
            self.failAsset(.budgetWaitTimedOut)
        }
        load.wait = work
        queue.asyncAfter(deadline: .now() + budget.limits.budgetWaitSeconds, execute: work)
    }

    private func remove(_ id: ObjectIdentifier, finish: Bool, error: PlaybackNetworkError?) {
        guard let load = loads.removeValue(forKey: id) else { return }
        pending.removeAll { $0 == id }
        load.wait?.cancel(); load.waitID = nil
        if let taskID = load.taskID { transfers[taskID]?.task.cancel() }
        if finish { load.sink.finish(error) }
    }

    private func failAsset(_ error: PlaybackNetworkError) {
        guard !invalidated, !reportedFailure else { return }
        reportedFailure = true
        for id in Array(loads.keys) { remove(id, finish: true, error: error) }
        onFailure(error)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask,
                    didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        guard !invalidated, let transfer = transfers[dataTask.taskIdentifier],
              let load = loads[transfer.sinkID], load.taskID == dataTask.taskIdentifier else {
            completionHandler(.cancel); return
        }
        do {
            guard let response = response as? HTTPURLResponse else { throw PlaybackNetworkError.invalidResponse }
            // A delegate/transport must not silently deliver a different origin
            // without having executed our redirect policy.
            guard let responseURL = response.url,
                  try PlaybackHTTPOrigin(responseURL) == PlaybackHTTPOrigin(transfer.policy.current) else {
                throw PlaybackNetworkError.redirectDenied
            }
            let parsed = try ProgressiveMediaResponse.parse(response, start: transfer.start, end: transfer.end,
                                                            previous: information,
                                                            fallbackExtension: plan.url.pathExtension)
            information = parsed.information
            transfer.response = parsed
            load.sink.receive(parsed.information)
            guard !invalidated, !reportedFailure, loads[transfer.sinkID] === load else {
                completionHandler(.cancel); return
            }
            if load.sink.range == nil || parsed.bodyBytes == 0 {
                remove(transfer.sinkID, finish: true, error: nil)
                completionHandler(.cancel)
            } else {
                completionHandler(.allow)
            }
        } catch {
            completionHandler(.cancel)
            failAsset(error as? PlaybackNetworkError ?? .invalidResponse)
        }
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        guard !invalidated, let transfer = transfers[dataTask.taskIdentifier],
              let load = loads[transfer.sinkID], load.taskID == dataTask.taskIdentifier else { return }
        guard let response = transfer.response, Int64(data.count) <= response.bodyBytes - transfer.received else {
            failAsset(.invalidResponse); return
        }
        transfer.received += Int64(data.count)
        do {
            var offset = 0
            if transfer.start == 0, !transfer.checkedPrefix {
                let count = min(16 - transfer.prefix.count, data.count)
                transfer.prefix.append(data.prefix(count)); offset = count
                if transfer.prefix.count == 16 || transfer.received == response.bodyBytes {
                    guard !ProgressiveMediaResponse.isPlaylistPrefix(transfer.prefix) else {
                        throw PlaybackNetworkError.hlsRequiresExplicitFormat
                    }
                    transfer.checkedPrefix = true
                    try deliver(transfer.prefix, load: load, transfer: transfer)
                    transfer.prefix.removeAll(keepingCapacity: false)
                }
            }
            if offset < data.count { try deliver(Data(data.dropFirst(offset)), load: load, transfer: transfer) }
        } catch {
            failAsset(error as? PlaybackNetworkError ?? .resourceBudgetExceeded)
        }
    }

    private func deliver(_ data: Data, load: Load, transfer: Transfer) throws {
        var offset = 0
        while offset < data.count {
            guard !invalidated, !reportedFailure, loads[transfer.sinkID] === load else { return }
            let count = min(budget.limits.deliveryBytes, data.count - offset)
            let start = data.startIndex + offset
            let chunk = data.subdata(in: start..<(start + count))
            let payload = try transfer.reservation.payload(chunk)
            load.sink.receive(payload)
            load.position += Int64(count)
            offset += count
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard let transfer = transfers.removeValue(forKey: task.taskIdentifier) else { return }
        // Release before pumping, never before this real completion.
        transfer.reservation.complete()
        guard !invalidated, let load = loads[transfer.sinkID], load.taskID == task.taskIdentifier else { return }
        load.taskID = nil
        guard error == nil, let response = transfer.response, transfer.received == response.bodyBytes else {
            failAsset(error == nil ? .invalidResponse : .transportFailed); return
        }
        pending.append(transfer.sinkID)
        pump()
    }

    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) {
        guard !invalidated, let transfer = transfers[task.taskIdentifier],
              loads[transfer.sinkID]?.taskID == task.taskIdentifier else { completionHandler(nil); return }
        do {
            guard let target = request.url,
                  [301, 302, 303, 307, 308].contains(response.statusCode) else {
                throw PlaybackNetworkError.redirectDenied
            }
            try transfer.policy.redirect(to: target)
            let origin = try PlaybackHTTPOrigin(target)
            guard transfer.reservation.move(to: origin.host) else { throw PlaybackNetworkError.resourceBudgetExceeded }
            completionHandler(try transfer.policy.request(start: transfer.start, end: transfer.end,
                                                           validator: information?.validator))
        } catch {
            completionHandler(nil)
            failAsset(error as? PlaybackNetworkError ?? .redirectDenied)
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        guard !invalidated, !reportedFailure, let transfer = transfers[task.taskIdentifier],
              loads[transfer.sinkID]?.taskID == task.taskIdentifier else {
            completionHandler(.cancelAuthenticationChallenge, nil); return
        }
        completeChallenge(challenge, completionHandler: completionHandler)
    }

    func urlSession(_ session: URLSession, didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        guard !invalidated, !reportedFailure, !loads.isEmpty else {
            completionHandler(.cancelAuthenticationChallenge, nil); return
        }
        completeChallenge(challenge, completionHandler: completionHandler)
    }

    private func completeChallenge(_ challenge: URLAuthenticationChallenge,
                                   completionHandler: (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        // TLS trust uses the system; never fetch shared HTTP credentials or
        // prompt for a client certificate/basic/digest account behind the DTO.
        if challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust {
            completionHandler(.performDefaultHandling, nil)
        } else {
            completionHandler(.cancelAuthenticationChallenge, nil)
        }
    }

    func urlSession(_ session: URLSession, didBecomeInvalidWithError error: Error?) {
        self.session = nil
    }

    deinit {
        if let observation { budget.unobserve(observation) }
        // A created URLSession retains its delegate until invalidated, so owners
        // must call invalidate(); the AV adapter/controller do so synchronously.
    }
}
