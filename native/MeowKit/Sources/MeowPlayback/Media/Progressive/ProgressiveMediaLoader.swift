import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

protocol ProgressiveMediaSink: AnyObject {
    var range: ProgressiveReadRange { get }
    func receive(_ information: ProgressiveContentInformation)
    func receive(_ data: Data)
    func finish(_ error: PlaybackNetworkError?)
}

/// Range-based HTTP loader with bounded per-request windows.
final class ProgressiveMediaLoader: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private static let windowBytes = 256 * 1024
    private static let deliveryBytes = 64 * 1024
    let queue: DispatchQueue
    let plan: PlaybackNetworkPlan
    private let queueKey = DispatchSpecificKey<Bool>()
    private let configuration: URLSessionConfiguration
    private let onFailure: @Sendable (PlaybackNetworkError) -> Void
    private var session: URLSession?
    private var invalidated = false
    private var reportedFailure = false
    private var loads: [ObjectIdentifier: Load] = [:]
    private var pending: [ObjectIdentifier] = []
    private var transfers: [Int: Transfer] = [:]
    private var information: ProgressiveContentInformation?
    private var closeWaiters: [CheckedContinuation<Void, Never>] = []
    private var pumping = false
    private var pumpAgain = false

    private final class Load {
        let sink: ProgressiveMediaSink
        var position: Int64
        var taskID: Int?
        init(_ sink: ProgressiveMediaSink) {
            self.sink = sink
            position = sink.range.start
        }
    }

    private final class Transfer {
        let task: URLSessionDataTask
        let sinkID: ObjectIdentifier
        let start: Int64
        let end: Int64
        var policy: PlaybackHTTPRequestPolicy
        var response: ProgressiveMediaResponse?
        var received: Int64 = 0
        var prefix = Data()
        var checkedPrefix = false
        init(task: URLSessionDataTask, sinkID: ObjectIdentifier, start: Int64, end: Int64,
             policy: PlaybackHTTPRequestPolicy) {
            self.task = task; self.sinkID = sinkID; self.start = start; self.end = end
            self.policy = policy
        }
    }

    init(plan: PlaybackNetworkPlan,
         configuration: URLSessionConfiguration = .ephemeral,
         onFailure: @escaping @Sendable (PlaybackNetworkError) -> Void) {
        precondition(plan.route == .progressive)
        self.plan = plan; self.onFailure = onFailure
        let config = configuration.copy() as! URLSessionConfiguration
        config.httpCookieStorage = nil
        config.httpShouldSetCookies = false
        config.urlCredentialStorage = nil
        config.urlCache = nil
        config.requestCachePolicy = .reloadIgnoringLocalCacheData
        config.httpAdditionalHeaders = nil
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 60
        self.configuration = config
        queue = DispatchQueue(label: "meow.playback.bytes.\(UUID().uuidString)")
        super.init()
        queue.setSpecific(key: queueKey, value: true)
    }

    func enqueue(_ sink: ProgressiveMediaSink) {
        performSynchronized {
            guard !invalidated else { sink.finish(.cancelled); return }
            guard !reportedFailure else { sink.finish(.transportFailed); return }
            let id = ObjectIdentifier(sink)
            guard loads[id] == nil else { return }
            loads[id] = Load(sink)
            pending.append(id)
            pump()
        }
    }

    func cancel(_ sink: ProgressiveMediaSink) {
        performSynchronized { remove(ObjectIdentifier(sink), finish: false, error: nil) }
    }

    func invalidate() {
        performSynchronized {
            guard !invalidated else { return }
            invalidated = true
            for id in Array(loads.keys) { remove(id, finish: false, error: nil) }
            session?.invalidateAndCancel()
        }
    }

    func close() async {
        invalidate()
        await withCheckedContinuation { continuation in
            performSynchronized {
                if transfers.isEmpty { continuation.resume() }
                else { closeWaiters.append(continuation) }
            }
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
                if load.position >= information.length {
                    remove(id, finish: true, error: nil)
                    continue
                }
            }
            if let end = load.sink.range.endExclusive, load.position >= end {
                remove(id, finish: true, error: nil)
                continue
            }
            let requestedEnd = load.sink.range.endExclusive
            let remaining = requestedEnd.map { $0 - load.position } ?? Int64(Self.windowBytes)
            let bytes = Int(min(Int64(Self.windowBytes), remaining))
            guard bytes > 0, load.position <= Int64.max - Int64(bytes) else {
                failAsset(.invalidResponse); return
            }
            let end = min(load.position + Int64(bytes) - 1, (information?.length ?? Int64.max) - 1)
            do {
                let policy = PlaybackHTTPRequestPolicy(plan: plan)
                let request = try policy.request(start: load.position, end: end, validator: information?.validator)
                let task = makeSession().dataTask(with: request)
                let transfer = Transfer(task: task, sinkID: id, start: load.position, end: end, policy: policy)
                transfers[task.taskIdentifier] = transfer
                load.taskID = task.taskIdentifier
                pending.removeAll { $0 == id }
                task.resume()
            } catch {
                failAsset(.invalidResponse); return
            }
        }
    }

    private func remove(_ id: ObjectIdentifier, finish: Bool, error: PlaybackNetworkError?) {
        guard let load = loads.removeValue(forKey: id) else { return }
        pending.removeAll { $0 == id }
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
            let parsed = try ProgressiveMediaResponse.parse(response, start: transfer.start, end: transfer.end,
                                                            previous: information,
                                                            fallbackExtension: plan.url.pathExtension)
            information = parsed.information
            transfer.response = parsed
            load.sink.receive(parsed.information)
            guard !invalidated, !reportedFailure, loads[transfer.sinkID] === load else {
                completionHandler(.cancel); return
            }
            if parsed.bodyBytes == 0 {
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
                    deliver(transfer.prefix, load: load, transfer: transfer)
                    transfer.prefix.removeAll(keepingCapacity: false)
                }
            }
            if offset < data.count { deliver(Data(data.dropFirst(offset)), load: load, transfer: transfer) }
        } catch {
            failAsset(error as? PlaybackNetworkError ?? .invalidResponse)
        }
    }

    private func deliver(_ data: Data, load: Load, transfer: Transfer) {
        var offset = 0
        while offset < data.count {
            guard !invalidated, !reportedFailure, loads[transfer.sinkID] === load else { return }
            let count = min(Self.deliveryBytes, data.count - offset)
            let start = data.startIndex + offset
            load.sink.receive(data.subdata(in: start..<(start + count)))
            load.position += Int64(count)
            offset += count
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard let transfer = transfers.removeValue(forKey: task.taskIdentifier) else { return }
        if invalidated, transfers.isEmpty {
            let waiters = closeWaiters; closeWaiters.removeAll()
            waiters.forEach { $0.resume() }
        }
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
            guard let target = request.url else { throw PlaybackNetworkError.invalidURL }
            try transfer.policy.redirect(to: target)
            completionHandler(try transfer.policy.request(start: transfer.start, end: transfer.end,
                                                           validator: information?.validator))
        } catch {
            completionHandler(nil)
            failAsset(error as? PlaybackNetworkError ?? .invalidResponse)
        }
    }

    func urlSession(_ session: URLSession, didBecomeInvalidWithError error: Error?) {
        self.session = nil
    }
}
