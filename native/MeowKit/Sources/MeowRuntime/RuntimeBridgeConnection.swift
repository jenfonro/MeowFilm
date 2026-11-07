import Foundation
import Darwin
import MeowJSON

/// One duplex protocol-1 peer over a connected stream descriptor. It never
/// listens, reconnects or registers methods implicitly; the owner supplies the
/// complete reverse-call table at construction. Outbound bytes are bounded like
/// the native Windows peer: eight frames, one reserved for cancel/error replies.
public final class RuntimeBridgeConnection: @unchecked Sendable {
    public typealias Handler = @Sendable (JSONValue) async throws -> JSONValue

    public let session: String
    private let descriptor: Int32
    private let handlers: [String: Handler]
    private let defaultTimeout: TimeInterval
    private let writer = DispatchQueue(label: "meowfilm.runtime-bridge.writer")
    private let lock = NSLock()
    private let callAdmission = NSLock()
    private var closed = false
    private var nextID = 0
    private var lastIncomingID = 0
    private var queuedBytes = 0
    private var pending: [Int: Pending] = [:]
    private var active: [Int: Task<Void, Never>] = [:]
    private var observers: [@Sendable () -> Void] = []
    private var descriptorClosed = false
    private var drainWaiters: [CheckedContinuation<Void, Never>] = []

    private struct Pending {
        var continuation: CheckedContinuation<JSONValue, Error>?
        var outcome: Result<JSONValue, Error>?
        var timer: DispatchWorkItem?
        var sent = false
    }

    /// Takes ownership of `descriptor` only when initialization succeeds.
    public init(descriptor: Int32, session: String, handlers: [String: Handler] = [:],
                timeout: TimeInterval = 30) throws {
        guard descriptor >= 0, RuntimeBridgeFrame.validSession(session),
              handlers.keys.allSatisfy(RuntimeBridgeFrame.validMethod),
              timeout > 0, timeout <= 300 else { throw RuntimeBridgeError.invalidMessage }
        var enabled: Int32 = 1
        guard setsockopt(descriptor, SOL_SOCKET, SO_NOSIGPIPE, &enabled, socklen_t(MemoryLayout<Int32>.size)) == 0 else {
            throw RuntimeBridgeError.invalidMessage
        }
        self.descriptor = descriptor; self.session = session
        self.handlers = handlers; defaultTimeout = timeout
        // The reader owns this connection until EOF or close, then closes the
        // descriptor after already-queued writes have finished.
        let reader = Thread { [self] in self.readLoop() }
        reader.name = "meowfilm.runtime-bridge.reader"
        reader.start()
    }

    public var isClosed: Bool { lock.lock(); defer { lock.unlock() }; return closed }

    /// Called once, after close, on an arbitrary thread. Already closed: immediately.
    public func onClose(_ observer: @escaping @Sendable () -> Void) {
        lock.lock()
        if closed { lock.unlock(); observer(); return }
        observers.append(observer)
        lock.unlock()
    }

    public func call(_ method: String, _ params: JSONValue = .null, timeout: TimeInterval? = nil) async throws -> JSONValue {
        let limit = timeout ?? defaultTimeout
        guard RuntimeBridgeFrame.validMethod(method), limit > 0, limit <= 300 else {
            throw RuntimeBridgeError.invalidMessage
        }
        let id = try beginCall(method, params, limit: limit)
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                lock.lock()
                guard var entry = pending[id] else {
                    lock.unlock(); continuation.resume(throwing: RuntimeBridgeError.closed); return
                }
                if let outcome = entry.outcome {
                    pending[id] = nil; lock.unlock(); continuation.resume(with: outcome); return
                }
                entry.continuation = continuation; pending[id] = entry
                lock.unlock()
            }
        } onCancel: { [weak self] in self?.cancelCall(id, code: .cancelled) }
    }

    /// ID assignment and writer admission are ordered together. Concurrent
    /// callers must not put request N+1 on the wire before request N.
    private func beginCall(_ method: String, _ params: JSONValue, limit: TimeInterval) throws -> Int {
        callAdmission.lock(); defer { callAdmission.unlock() }
        lock.lock()
        if closed { lock.unlock(); throw RuntimeBridgeError.closed }
        guard nextID < 9_007_199_254_740_991 else { lock.unlock(); throw RuntimeBridgeError.limit }
        nextID += 1
        let id = nextID
        pending[id] = Pending()
        lock.unlock()
        if Task.isCancelled { finish(id, .failure(RuntimeBridgeError.cancelled)) }
        else {
            do {
                let frame = try RuntimeBridgeFrame(session: session, id: id, kind: .request(method: method, params: params)).encoded()
                enqueue(frame, control: false, shouldSend: { [weak self] in self?.markSent(id) ?? false },
                        rejected: { [weak self] error in self?.finish(id, .failure(error)) })
                let timer = DispatchWorkItem { [weak self] in self?.cancelCall(id, code: .timeout) }
                lock.lock()
                if pending[id]?.outcome == nil { pending[id]?.timer = timer } else { timer.cancel() }
                lock.unlock()
                if !timer.isCancelled { DispatchQueue.global().asyncAfter(deadline: .now() + limit, execute: timer) }
            } catch { finish(id, .failure(error)) }
        }
        return id
    }

    public func close() {
        lock.lock()
        guard !closed else { lock.unlock(); return }
        closed = true
        // Unblocks the reader (EOF) and any blocked write before the fd is closed.
        _ = shutdown(descriptor, SHUT_RDWR)
        var resumed: [CheckedContinuation<JSONValue, Error>] = []
        for (id, var entry) in pending where entry.outcome == nil {
            entry.timer?.cancel(); entry.timer = nil
            if let continuation = entry.continuation { resumed.append(continuation); pending[id] = nil }
            else { entry.outcome = .failure(RuntimeBridgeError.closed); pending[id] = entry }
        }
        let tasks = Array(active.values), callbacks = observers
        observers.removeAll()
        lock.unlock()
        for continuation in resumed { continuation.resume(throwing: RuntimeBridgeError.closed) }
        for task in tasks { task.cancel() }
        for callback in callbacks { callback() }
    }

    /// Closes admission, then waits for the reader/writer descriptor and every
    /// admitted reverse handler to finish. Call from the owner, not a handler.
    public func closeAndWait() async {
        close()
        await withCheckedContinuation { continuation in
            lock.lock()
            if descriptorClosed && active.isEmpty {
                lock.unlock(); continuation.resume()
            } else {
                drainWaiters.append(continuation); lock.unlock()
            }
        }
    }

    private func finishDescriptorClose() {
        lock.lock()
        descriptorClosed = true
        let waiters = takeDrainWaiters()
        lock.unlock()
        for waiter in waiters { waiter.resume() }
    }

    /// Called under lock. Closed handlers remain owned until their real exit.
    private func takeDrainWaiters() -> [CheckedContinuation<Void, Never>] {
        guard descriptorClosed && active.isEmpty else { return [] }
        let waiters = drainWaiters
        drainWaiters.removeAll()
        return waiters
    }

    // MARK: Outbound calls

    /// Settles once; returns whether the request had already reached the writer.
    @discardableResult
    private func finish(_ id: Int, _ outcome: Result<JSONValue, Error>) -> Bool? {
        lock.lock()
        guard var entry = pending[id], entry.outcome == nil else { lock.unlock(); return nil }
        entry.timer?.cancel(); entry.timer = nil
        let sent = entry.sent
        if let continuation = entry.continuation {
            pending[id] = nil; lock.unlock(); continuation.resume(with: outcome)
        } else {
            entry.outcome = outcome; pending[id] = entry; lock.unlock()
        }
        return sent
    }

    private func cancelCall(_ id: Int, code: RuntimeBridgeError) {
        // Settling and reading `sent` are atomic with markSent: an unsent request
        // is never written afterwards, and a written one is always cancelled.
        guard finish(id, .failure(code)) == true,
              let frame = try? RuntimeBridgeFrame(session: session, id: id, kind: .cancel).encoded() else { return }
        enqueue(frame, control: true)
    }

    private func markSent(_ id: Int) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard pending[id] != nil, pending[id]?.outcome == nil else { return false }
        pending[id]?.sent = true
        return true
    }

    // MARK: Writer

    private func enqueue(_ frame: Data, control: Bool, shouldSend: @escaping @Sendable () -> Bool = { true },
                         rejected: (@Sendable (Error) -> Void)? = nil) {
        lock.lock()
        let budget = RuntimeBridgeFrame.maximumBytes * 8 - (control ? 0 : RuntimeBridgeFrame.maximumBytes)
        if closed { lock.unlock(); rejected?(RuntimeBridgeError.closed); return }
        if queuedBytes + frame.count > budget {
            lock.unlock()
            // Losing a cancel/error reply would silently retain remote work.
            if control { close() }
            rejected?(RuntimeBridgeError.limit)
            return
        }
        queuedBytes += frame.count
        // Register the writer before close can enqueue its descriptor barrier.
        // An admitted frame must not land behind that barrier.
        writer.async { [self] in
            lock.lock()
            let open = !closed
            lock.unlock()
            var failed = !open
            if open, shouldSend() { failed = !writeAll(frame) }
            lock.lock(); queuedBytes -= frame.count; lock.unlock()
            if failed {
                rejected?(RuntimeBridgeError.closed)
                close()
            }
        }
        lock.unlock()
    }

    private func writeAll(_ data: Data) -> Bool {
        data.withUnsafeBytes { raw -> Bool in
            guard let base = raw.baseAddress else { return true }
            var offset = 0
            while offset < raw.count {
                let count = write(descriptor, base + offset, raw.count - offset)
                if count < 0, errno == EINTR { continue }
                guard count > 0 else { return false }
                offset += count
            }
            return true
        }
    }

    // MARK: Reader

    private func readLoop() {
        var header = [UInt8](repeating: 0, count: 4)
        while readExactly(&header) {
            let length = Int(header[0]) << 24 | Int(header[1]) << 16 | Int(header[2]) << 8 | Int(header[3])
            guard length > 0, length <= RuntimeBridgeFrame.maximumBytes else { break }
            var body = [UInt8](repeating: 0, count: length)
            guard readExactly(&body), let frame = try? RuntimeBridgeFrame.decode(body: Data(body)),
                  accept(frame) else { break }
        }
        close()
        // Every write queued before close has already observed `closed`.
        writer.async { [self] in
            _ = Darwin.close(descriptor)
            finishDescriptorClose()
        }
    }

    private func readExactly(_ buffer: inout [UInt8]) -> Bool {
        let count = buffer.count
        var offset = 0
        return buffer.withUnsafeMutableBytes { raw -> Bool in
            guard let base = raw.baseAddress else { return false }
            while offset < count {
                let result = read(descriptor, base + offset, count - offset)
                if result < 0, errno == EINTR { continue }
                guard result > 0 else { return false }
                offset += result
            }
            return true
        }
    }

    private func accept(_ frame: RuntimeBridgeFrame) -> Bool {
        guard frame.session == session else { return false }
        switch frame.kind {
        case .result(let value):
            finish(frame.id, .success(value))
        case .error(let code):
            finish(frame.id, .failure(code))
        case .cancel:
            lock.lock(); let task = active[frame.id]; lock.unlock()
            task?.cancel()
        case .request(let method, let params):
            lock.lock()
            guard !closed else { lock.unlock(); return true }
            guard frame.id > lastIncomingID else { lock.unlock(); return false }
            lastIncomingID = frame.id
            guard let handler = handlers[method] else {
                lock.unlock()
                reply(frame.id, .error(.methodUnavailable))
                return true
            }
            let id = frame.id
            // Registered under the same lock, so completion cannot outrun registration.
            active[id] = Task { [weak self] in
                let kind: RuntimeBridgeFrame.Kind
                do {
                    let value = try await handler(params)
                    kind = Task.isCancelled ? .error(.cancelled) : .result(value)
                } catch let error as RuntimeBridgeError {
                    kind = .error(error)
                } catch {
                    kind = .error(Task.isCancelled ? .cancelled : .internalError)
                }
                self?.respond(id, kind)
            }
            lock.unlock()
        }
        return true
    }

    private func respond(_ id: Int, _ kind: RuntimeBridgeFrame.Kind) {
        lock.lock()
        let owned = active.removeValue(forKey: id) != nil
        let waiters = takeDrainWaiters()
        lock.unlock()
        if owned { reply(id, kind) }
        for waiter in waiters { waiter.resume() }
    }

    private func reply(_ id: Int, _ kind: RuntimeBridgeFrame.Kind) {
        do {
            let frame = try RuntimeBridgeFrame(session: session, id: id, kind: kind).encoded()
            if case .error = kind { enqueue(frame, control: true) }
            else {
                enqueue(frame, control: false, rejected: { [weak self] error in
                    guard let self, !self.isClosed else { return }
                    self.reply(id, .error(error as? RuntimeBridgeError ?? .internalError))
                })
            }
        } catch {
            // An unencodable or oversized result fails explicitly with a fixed code.
            let code = error as? RuntimeBridgeError ?? .internalError
            if let frame = try? RuntimeBridgeFrame(session: session, id: id, kind: .error(code)).encoded() {
                enqueue(frame, control: true)
            }
        }
    }
}

/// The process-wide embedded runtime. A platform launcher starts it at most
/// once; the connected owner's reverse-call table serves every runtime call.
/// A closed connection is final for this process.
public protocol RuntimeBridgeLaunching: Sendable {
    func connect(handlers: [String: RuntimeBridgeConnection.Handler]) async throws -> RuntimeBridgeConnection
    /// Called after connect has settled and its connection has closed. Returns
    /// only after the launched executor exits; cancellation is not an exit.
    func waitForExit() async
}
