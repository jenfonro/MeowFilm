import Foundation

/// A single-consumer observation. Intermediate snapshots may be coalesced; at most
/// one completed snapshot waits for the consumer. Reconnect uses a new observation.
public struct RuntimeHTTPEvents: AsyncSequence, Sendable {
    public typealias Element = RuntimeValue
    private let stream: AsyncThrowingStream<RuntimeValue, Error>
    private let lifetime: RuntimeHTTPEventLifetime
    let connection: RuntimeHTTPEventConnection

    init(sessionID: UUID, requestID: String, drained: @escaping @Sendable () -> Void) {
        let (stream, continuation) = AsyncThrowingStream<RuntimeValue, Error>.makeStream(bufferingPolicy: .bufferingNewest(1))
        self.stream = stream
        connection = RuntimeHTTPEventConnection(sessionID: sessionID, requestID: requestID,
                                                continuation: continuation, drained: drained)
        lifetime = RuntimeHTTPEventLifetime(connection)
        continuation.onTermination = { [weak connection] termination in
            if case .cancelled = termination { connection?.cancel() }
        }
    }

    /// Waits for the subscription's actual URLSession invalidation, not merely cancel().
    public func cancel() async {
        connection.cancel()
        await connection.waitForDrain()
    }

    public func makeAsyncIterator() -> Iterator {
        Iterator(iterator: stream.makeAsyncIterator(), lifetime: lifetime,
                 iteration: RuntimeHTTPEventLifetime(connection))
    }

    public struct Iterator: AsyncIteratorProtocol {
        fileprivate var iterator: AsyncThrowingStream<RuntimeValue, Error>.Iterator
        fileprivate let lifetime: RuntimeHTTPEventLifetime
        fileprivate let iteration: RuntimeHTTPEventLifetime

        public mutating func next() async throws -> RuntimeValue? {
            let connection = lifetime.connection
            do {
                return try await withTaskCancellationHandler {
                    guard !Task.isCancelled else { throw RuntimeHTTPError.cancelled }
                    try connection.checkCancellation()
                    let value = try await iterator.next()
                    guard !Task.isCancelled else { throw RuntimeHTTPError.cancelled }
                    try connection.checkCancellation()
                    return value
                } onCancel: { connection.cancel() }
            } catch {
                connection.cancel()
                await connection.waitForDrain()
                if Task.isCancelled { throw RuntimeHTTPError.cancelled }
                throw error
            }
        }
    }
}

fileprivate final class RuntimeHTTPEventLifetime: @unchecked Sendable {
    let connection: RuntimeHTTPEventConnection
    init(_ connection: RuntimeHTTPEventConnection) { self.connection = connection }
    deinit { connection.cancel() }
}

final class RuntimeHTTPEventConnection: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private let lock = NSLock()
    private let requestID: String
    private let continuation: AsyncThrowingStream<RuntimeValue, Error>.Continuation
    private let onDrain: @Sendable () -> Void
    private var parser: RuntimeHTTPEventParser
    private var session: URLSession?
    private var failure: RuntimeHTTPError?
    private var cancellation: RuntimeHTTPError?
    private var drained = false
    private var waiters: [CheckedContinuation<Void, Never>] = []
    private var received = false

    init(sessionID: UUID, requestID: String,
         continuation: AsyncThrowingStream<RuntimeValue, Error>.Continuation,
         drained: @escaping @Sendable () -> Void) {
        self.requestID = requestID
        self.continuation = continuation
        onDrain = drained
        parser = RuntimeHTTPEventParser(sessionID: sessionID)
    }

    func start(request: URLRequest, configuration: URLSessionConfiguration) {
        let session = URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
        let task = session.dataTask(with: request)
        lock.lock()
        self.session = session
        let cancelled = failure != nil
        lock.unlock()
        if cancelled { session.invalidateAndCancel() }
        else { task.resume() }
    }

    func cancel(_ error: RuntimeHTTPError = .cancelled) {
        lock.lock()
        if !drained && failure == nil { failure = error }
        if cancellation == nil { cancellation = error }
        let session = session
        lock.unlock()
        session?.invalidateAndCancel()
    }

    func checkCancellation() throws {
        lock.lock(); let cancellation = cancellation; lock.unlock()
        if let cancellation { throw cancellation }
    }

    func waitForDrain() async {
        await withCheckedContinuation { continuation in
            lock.lock()
            if drained { lock.unlock(); continuation.resume(); return }
            waiters.append(continuation)
            lock.unlock()
        }
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        guard let response = response as? HTTPURLResponse,
              response.statusCode == 200,
              response.value(forHTTPHeaderField: "X-Meow-Request-Id") == requestID,
              response.mimeType?.lowercased() == "text/event-stream",
              response.textEncodingName == nil || response.textEncodingName?.lowercased() == "utf-8",
              response.value(forHTTPHeaderField: "Content-Encoding") == nil ||
                response.value(forHTTPHeaderField: "Content-Encoding")?.lowercased() == "identity" else {
            cancel(.invalidResponse); completionHandler(.cancel); return
        }
        completionHandler(.allow)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        lock.lock(); let cancelled = failure != nil; lock.unlock()
        guard !cancelled else { return }
        do {
            for byte in data {
                if let snapshot = try parser.feed(byte) {
                    received = true
                    if case .terminated = continuation.yield(snapshot) { cancel(); return }
                }
            }
        } catch { cancel(.invalidResponse) }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) {
        cancel(.invalidResponse)
        completionHandler(nil)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        lock.lock()
        if failure == nil {
            if let error {
                failure = (error as NSError).code == NSURLErrorTimedOut ? .timeout : .closed
            } else { failure = !received || parser.incomplete ? .invalidResponse : .closed }
        }
        lock.unlock()
        session.finishTasksAndInvalidate()
    }

    func urlSession(_ session: URLSession, didBecomeInvalidWithError error: Error?) {
        lock.lock()
        guard !drained else { lock.unlock(); return }
        drained = true
        let failure = failure ?? RuntimeHTTPError.closed, pending = waiters
        waiters.removeAll()
        self.session = nil
        lock.unlock()
        continuation.finish(throwing: failure)
        onDrain()
        for waiter in pending { waiter.resume() }
    }
}

/// No line strings or history: protocol fields are matched incrementally and
/// unknown/comment bytes discarded. Only the current JSON snapshot is retained;
/// the existing JSON value contract has no arbitrary byte quota.
struct RuntimeHTTPEventParser {
    private static let fields: [[UInt8]] = [Array("id".utf8), Array("event".utf8), Array("data".utf8)]
    private static let snapshot = Array("snapshot".utf8)
    private let sessionID: UUID
    private var data = Data()
    private var candidates = 7, fieldLength = 0, field = -1, eventOffset = 0, bom = 0
    private var utf8Remaining = 0, utf8Minimum: UInt8 = 0x80, utf8Maximum: UInt8 = 0xbf
    private var id: Int64 = 0
    private var lineStarted = false, valueStarted = false, skipSpace = false, skipLF = false
    private var hasID = false, hasEvent = false, hasData = false, idDigit = false

    init(sessionID: UUID) { self.sessionID = sessionID }
    var incomplete: Bool { lineStarted || hasID || hasEvent || hasData || bom == 1 || bom == 2 || utf8Remaining != 0 }

    mutating func feed(_ byte: UInt8) throws -> RuntimeValue? {
        try validateUTF8(byte)
        if bom < 3 {
            let prefix: [UInt8] = [0xef, 0xbb, 0xbf]
            if bom != 0 || byte == prefix[0] {
                guard byte == prefix[bom] else { throw RuntimeHTTPError.invalidResponse }
                bom += 1
                return nil
            }
            bom = 3
        }
        if skipLF { skipLF = false; if byte == 10 { return nil } }
        if byte == 10 || byte == 13 {
            skipLF = byte == 13
            return try endLine()
        }
        lineStarted = true
        if !valueStarted {
            if byte == 58 {
                field = -1
                for index in Self.fields.indices where candidates & (1 << index) != 0 && fieldLength == Self.fields[index].count {
                    field = index
                }
                valueStarted = true; skipSpace = true
                if field == 0 {
                    guard !hasID else { throw RuntimeHTTPError.invalidResponse }
                    id = 0; idDigit = false
                } else if field == 1 {
                    guard !hasEvent else { throw RuntimeHTTPError.invalidResponse }
                    eventOffset = 0
                } else if field == 2 {
                    if hasData { data.append(10) }
                    hasData = true
                }
                return nil
            }
            for index in Self.fields.indices {
                if fieldLength >= Self.fields[index].count || Self.fields[index][fieldLength] != byte {
                    candidates &= ~(1 << index)
                }
            }
            if candidates != 0 { fieldLength += 1 }
            return nil
        }
        if skipSpace { skipSpace = false; if byte == 32 { return nil } }
        if field == 0 {
            guard (48...57).contains(byte), id <= (9_007_199_254_740_991 - Int64(byte - 48)) / 10 else {
                throw RuntimeHTTPError.invalidResponse
            }
            id = id * 10 + Int64(byte - 48); idDigit = true
        } else if field == 1 {
            guard eventOffset < Self.snapshot.count, Self.snapshot[eventOffset] == byte else {
                throw RuntimeHTTPError.invalidResponse
            }
            eventOffset += 1
        } else if field == 2 { data.append(byte) }
        return nil
    }

    private mutating func validateUTF8(_ byte: UInt8) throws {
        if utf8Remaining != 0 {
            guard byte >= utf8Minimum, byte <= utf8Maximum else { throw RuntimeHTTPError.invalidResponse }
            utf8Remaining -= 1; utf8Minimum = 0x80; utf8Maximum = 0xbf
        } else if byte < 0x80 { }
        else if (0xc2...0xdf).contains(byte) { utf8Remaining = 1 }
        else if (0xe0...0xef).contains(byte) {
            utf8Remaining = 2
            if byte == 0xe0 { utf8Minimum = 0xa0 }
            if byte == 0xed { utf8Maximum = 0x9f }
        } else if (0xf0...0xf4).contains(byte) {
            utf8Remaining = 3
            if byte == 0xf0 { utf8Minimum = 0x90 }
            if byte == 0xf4 { utf8Maximum = 0x8f }
        } else { throw RuntimeHTTPError.invalidResponse }
    }

    private mutating func endLine() throws -> RuntimeValue? {
        if lineStarted {
            if field == 0 { guard idDigit else { throw RuntimeHTTPError.invalidResponse }; hasID = true }
            if field == 1 { guard eventOffset == Self.snapshot.count else { throw RuntimeHTTPError.invalidResponse }; hasEvent = true }
            candidates = 7; fieldLength = 0; field = -1
            lineStarted = false; valueStarted = false; skipSpace = false
            return nil
        }
        if !hasID && !hasEvent && !hasData { return nil }
        guard hasID, hasEvent, hasData,
              let snapshot = try? RuntimeJSON.decode(data), case .object(let fields) = snapshot,
              case .string(let identity)? = fields["sessionId"], UUID(uuidString: identity) == sessionID,
              case .number(let sequence)? = fields["sequence"], sequence == Double(id) else {
            throw RuntimeHTTPError.invalidResponse
        }
        data = Data()
        hasID = false; hasEvent = false; hasData = false
        return snapshot
    }
}
