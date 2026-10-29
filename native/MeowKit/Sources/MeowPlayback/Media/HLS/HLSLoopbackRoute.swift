#if os(iOS) || os(macOS)
import Foundation
@preconcurrency import Network

/// An attempt's exceptional HLS input route, not a general HTTP proxy. The
/// controller owns this until stop/replacement, independently of any view.
final class HLSLoopbackRoute: NSObject, @unchecked Sendable {
    let queue = DispatchQueue(label: "meow.playback.hls." + UUID().uuidString)
    let queueKey = DispatchSpecificKey<Bool>()
    let budget: PlaybackNetworkBudget
    let registry: HLSResourceRegistry
    let configuration: URLSessionConfiguration
    let listenerLease: PlaybackAuxiliaryReservation
    let registryLease: PlaybackAuxiliaryReservation
    let onFailure: @Sendable (PlaybackNetworkError) -> Void
    var plan: PlaybackNetworkPlan?
    var listener: NWListener?
    var session: URLSession?
    var observation: UUID?
    var clients: [UUID: HLSClient] = [:]
    var transfers: [Int: HLSTransfer] = [:]
    var port: UInt16?
    var started = false
    var invalidated = false
    var startup: (@Sendable (Result<URL, PlaybackNetworkError>) -> Void)?
    var startupTimeout: DispatchWorkItem?

    init(plan: PlaybackNetworkPlan, budget: PlaybackNetworkBudget,
         configuration: URLSessionConfiguration = .ephemeral,
         onFailure: @escaping @Sendable (PlaybackNetworkError) -> Void) throws {
        guard plan.route == .scopedHLS else { throw PlaybackNetworkError.invalidURL }
        registry = try HLSResourceRegistry(url: plan.url)
        guard let listenerLease = budget.acquireAuxiliary(.listener, bytes: 0) else {
            throw PlaybackNetworkError.resourceBudgetExceeded
        }
        guard let registryLease = budget.acquireAuxiliary(.storage, bytes: HLSResourceRegistry.maximumBytes) else {
            listenerLease.complete(); throw PlaybackNetworkError.resourceBudgetExceeded
        }
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: .any)
        parameters.includePeerToPeer = false
        let listener: NWListener
        do { listener = try NWListener(using: parameters, on: .any) }
        catch { listenerLease.complete(); registryLease.complete(); throw PlaybackNetworkError.loopbackUnavailable }
        self.plan = plan; self.budget = budget; self.listener = listener
        self.listenerLease = listenerLease; self.registryLease = registryLease; self.onFailure = onFailure
        let config = configuration.copy() as! URLSessionConfiguration
        config.httpCookieStorage = nil; config.httpShouldSetCookies = false; config.urlCredentialStorage = nil
        config.urlCache = nil; config.httpAdditionalHeaders = nil; config.requestCachePolicy = .reloadIgnoringLocalCacheData
        config.timeoutIntervalForRequest = 30; config.timeoutIntervalForResource = 60
        config.httpMaximumConnectionsPerHost = budget.limits.tasksPerHost
        self.configuration = config
        super.init()
        queue.setSpecific(key: queueKey, value: true)
    }

    func synchronized<T>(_ body: () throws -> T) rethrows -> T {
        if DispatchQueue.getSpecific(key: queueKey) == true { return try body() }
        return try queue.sync(execute: body)
    }

    func start(_ completion: @escaping @Sendable (Result<URL, PlaybackNetworkError>) -> Void) {
        synchronized {
            guard !started, !invalidated, let listener else { completion(.failure(.cancelled)); return }
            started = true; startup = completion
            observation = budget.observe { [weak self] in
                self?.queue.async { [weak self] in self?.pump() }
            }
            // Strong callbacks keep physical ownership until cancellation.
            // Terminal handlers explicitly break these cycles.
            listener.stateUpdateHandler = { [self] state in
                switch state {
                case .ready:
                    guard !invalidated, let number = listener.port?.rawValue,
                          let root = registry.root,
                          let url = URL(string: "http://127.0.0.1:\(number)" + registry.path(root)) else {
                        if !invalidated { fail(.loopbackUnavailable) }; return
                    }
                    port = number
                    startupTimeout?.cancel(); startupTimeout = nil
                    let callback = startup; startup = nil
                    refreshConnectionLimit()
                    callback?(.success(url))
                case .failed, .waiting:
                    if !invalidated { fail(.loopbackUnavailable) }
                case .cancelled:
                    if !invalidated { fail(.loopbackUnavailable) }
                    listener.stateUpdateHandler = nil; listener.newConnectionHandler = nil
                    self.listener = nil; listenerLease.complete()
                default: break
                }
            }
            listener.newConnectionHandler = { [self] connection in accept(connection) }
            refreshConnectionLimit()
            let timeout = DispatchWorkItem { [weak self] in
                guard let self, self.startup != nil else { return }
                self.fail(.loopbackUnavailable)
            }
            startupTimeout = timeout
            queue.asyncAfter(deadline: .now() + 10, execute: timeout)
            listener.start(queue: queue)
        }
    }

    func invalidate() { synchronized { invalidateLocked() } }

    func invalidateLocked() {
        guard !invalidated else { return }
        invalidated = true
        startupTimeout?.cancel(); startupTimeout = nil
        let callback = startup; startup = nil
        if let observation { budget.unobserve(observation); self.observation = nil }
        for client in Array(clients.values) { close(client) }
        registry.clear(); plan = nil
        registryLease.complete()
        listener?.cancel()
        if !started { listener = nil; listenerLease.complete() }
        session?.invalidateAndCancel()
        callback?(.failure(.cancelled))
    }

    func fail(_ error: PlaybackNetworkError) {
        guard !invalidated else { return }
        let callback = startup; startup = nil
        invalidateLocked()
        callback?(.failure(error))
        onFailure(error)
    }

    func refreshConnectionLimit() {
        guard !invalidated else { return }
        let remaining = max(0, budget.limits.hlsConnections - budget.usage.activeConnections)
        listener?.newConnectionLimit = min(remaining, max(0, 8 - clients.count))
    }

    func accept(_ connection: NWConnection) {
        guard !invalidated, clients.count < 8,
              let lease = budget.acquireAuxiliary(.connection, bytes: 64 * 1024) else {
            connection.cancel(); refreshConnectionLimit(); return
        }
        let client = HLSClient(connection: connection, lease: lease)
        clients[client.id] = client
        connection.stateUpdateHandler = { [self, client] state in
            switch state {
            case .ready: if !client.closed { readHeader(client) }
            case .failed: close(client)
            case .cancelled:
                if !client.closed { close(client) }
                client.connection.stateUpdateHandler = nil
                clients[client.id] = nil
                client.lease.complete()
                refreshConnectionLimit()
            default: break
            }
        }
        armDeadline(client, seconds: 10)
        connection.start(queue: queue)
        refreshConnectionLimit()
    }

    func armDeadline(_ client: HLSClient, seconds: TimeInterval) {
        client.deadline?.cancel()
        let identity = UUID(); client.deadlineID = identity
        let deadline = DispatchWorkItem { [weak self, weak client] in
            guard let self, let client, !client.closed, client.deadlineID == identity else { return }
            if client.request == nil { self.close(client) }
            else { self.fail(.budgetWaitTimedOut) }
        }
        client.deadline = deadline; queue.asyncAfter(deadline: .now() + seconds, execute: deadline)
    }

    func readHeader(_ client: HLSClient) {
        guard !invalidated, !client.closed else { return }
        let remaining = HLSClientRequest.maximumHeaderBytes - client.header.count
        guard remaining > 0 else { reject(client, status: 431); return }
        client.connection.receive(minimumIncompleteLength: 1, maximumLength: remaining) { [self, client] data, _, complete, error in
            guard !invalidated, !client.closed else { return }
            if let data {
                guard data.count <= remaining else { reject(client, status: 431); return }
                client.header.append(data)
            }
            if let end = client.header.range(of: Data("\r\n\r\n".utf8)) {
                // One header-only request per connection. No body/pipelining.
                guard end.upperBound == client.header.endIndex, let port else { reject(client, status: 400); return }
                do {
                    let request = try HLSClientRequest(client.header, port: port)
                    guard let resource = registry.lookup(path: request.path) else { reject(client, status: 404); return }
                    registry.retain(resource)
                    client.header.removeAll()
                    client.request = request; client.resource = resource
                    armDeadline(client, seconds: budget.limits.budgetWaitSeconds)
                    watchDisconnect(client)
                    pump()
                } catch { reject(client, status: 400) }
            } else if complete || error != nil { close(client) }
            else { readHeader(client) }
        }
    }

    func watchDisconnect(_ client: HLSClient) {
        guard !client.closed, !invalidated else { return }
        client.connection.receive(minimumIncompleteLength: 1, maximumLength: 1) { [self, client] data, _, complete, error in
            guard !client.closed else { return }
            if complete || error != nil || data?.isEmpty == false { close(client) }
            else if !invalidated { watchDisconnect(client) }
        }
    }

    func reject(_ client: HLSClient, status: Int) {
        guard !client.closed, !invalidated else { close(client); return }
        let data = Data("HTTP/1.1 \(status) Rejected\r\nContent-Length: 0\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n".utf8)
        client.connection.send(content: data, contentContext: .finalMessage, isComplete: true,
                               completion: .contentProcessed { [self, client] error in
            close(client, graceful: error == nil && !invalidated)
        })
    }

    func close(_ client: HLSClient, graceful: Bool = false) {
        guard !client.closed else {
            if !graceful { client.connection.forceCancel() }
            return
        }
        client.closed = true
        client.deadline?.cancel(); client.deadline = nil; client.deadlineID = nil
        client.header.removeAll()
        if let resource = client.resource { registry.release(resource); client.resource = nil }
        if let id = client.taskID, let transfer = transfers[id] {
            transfer.cancelled = true
            transfer.policy.revoke()
            transfer.output.removeAll(); transfer.playlist.removeAll(); transfer.prefix.removeAll()
            // Data already submitted to Network remains charged until its
            // contentProcessed callback. No new delivery after this fence.
            transfer.task.cancel()
            if transfer.suspended { transfer.suspended = false; transfer.task.resume() }
            releaseIfDrained(transfer)
        }
        if graceful { client.connection.cancel() } else { client.connection.forceCancel() }
    }

    func makeSession() -> URLSession {
        if let session { return session }
        let delegateQueue = OperationQueue()
        delegateQueue.maxConcurrentOperationCount = 1; delegateQueue.underlyingQueue = queue
        let session = URLSession(configuration: configuration, delegate: self, delegateQueue: delegateQueue)
        self.session = session
        return session
    }

    deinit {
        // A started route is retained by terminal Network/URLSession callbacks
        // until physical cleanup. This covers only a never-started binding.
        if !started { listener?.cancel(); listenerLease.complete(); registryLease.complete() }
    }
}

final class HLSClient: @unchecked Sendable {
    let id = UUID()
    let connection: NWConnection
    let lease: PlaybackAuxiliaryReservation
    var header = Data()
    var request: HLSClientRequest?
    var resource: HLSResource?
    var taskID: Int?
    var closed = false
    var deadline: DispatchWorkItem?
    var deadlineID: UUID?
    init(connection: NWConnection, lease: PlaybackAuxiliaryReservation) { self.connection = connection; self.lease = lease }
}

final class HLSTransfer: @unchecked Sendable {
    let task: URLSessionDataTask
    let client: HLSClient
    let resource: HLSResource
    let request: HLSClientRequest
    let reservation: PlaybackNetworkReservation
    var policy: HLSRequestPolicy
    var response: HLSUpstreamResponse?
    var playlist = Data()
    var prefix = Data()
    var checkedPrefix = false
    var output: [Data] = []
    var buffered = 0
    var received: Int64 = 0
    var sending = false, suspended = false, completed = false, cancelled = false
    var expectedCancellation = false, chunked = false, trailerSent = false, finishing = false
    init(task: URLSessionDataTask, client: HLSClient, resource: HLSResource, request: HLSClientRequest,
         reservation: PlaybackNetworkReservation, policy: HLSRequestPolicy) {
        self.task = task; self.client = client; self.resource = resource; self.request = request
        self.reservation = reservation; self.policy = policy
    }
}
#endif
