import Foundation
import CMeowJavaScript

public protocol JavaScriptJSONEngine: Sendable {
    func invoke(_ request: Data) async throws -> Data
    func close() async
}

public enum NativeJavaScriptError: Error, Equatable, Sendable, LocalizedError {
    case closed, executionFailed, timedOut

    public var errorDescription: String? {
        switch self {
        case .closed: return "规则引擎已关闭"
        case .executionFailed: return "规则引擎执行失败"
        case .timedOut: return "规则处理超时"
        }
    }
}

/// A fixed application program on one native thread. User input crosses only
/// the JSON ABI; no loader, filesystem, network or alternate engine is exposed.
public final class NativeJavaScriptJSONEngine: JavaScriptJSONEngine, @unchecked Sendable {
    private let worker: Worker

    public init(source: String) {
        worker = Worker(source: source)
        worker.start()
    }

    public func invoke(_ request: Data) async throws -> Data {
        let control = CallControl()
        // QuickJS requires buf[length] == 0 even when length is explicit.
        let bytes = request + Data([0])
        return try await withTaskCancellationHandler {
            try Task.checkCancellation()
            return try await withCheckedThrowingContinuation { continuation in
                worker.submit(Request(bytes: bytes, control: control, continuation: continuation))
            }
        } onCancel: { control.cancel() }
    }

    /// Returns only after accepted requests settle and the C context is freed.
    public func close() async {
        await withCheckedContinuation { worker.close($0) }
    }

    deinit { worker.close(nil) }
}

private final class CallControl: @unchecked Sendable {
    private let lock = NSLock()
    private var cancelled = false
    private var deadline: UInt64?

    func cancel() {
        lock.lock(); cancelled = true; lock.unlock()
    }

    func begin() {
        lock.lock()
        // Same execution resource boundary as the Windows adapter.
        deadline = DispatchTime.now().uptimeNanoseconds + 2_000_000_000
        lock.unlock()
    }

    var error: Error? {
        lock.lock(); defer { lock.unlock() }
        if cancelled { return CancellationError() }
        if let deadline, DispatchTime.now().uptimeNanoseconds >= deadline {
            return NativeJavaScriptError.timedOut
        }
        return nil
    }
}

private struct Request {
    let bytes: Data
    let control: CallControl
    let continuation: CheckedContinuation<Data, Error>
}

private final class Worker: @unchecked Sendable {
    private let source: [CChar]
    private let condition = NSCondition()
    private var requests: [Request] = []
    // Written only by the owning thread under condition. Other threads may
    // cancel its control, but never inspect or use the native context.
    private var active: Request?
    private var closing = false
    private var finished = false
    private var closingWaiters: [CheckedContinuation<Void, Never>] = []

    init(source: String) { self.source = source.utf8CString.map { $0 } }

    func start() {
        let thread = Thread { [self] in run() }
        thread.name = "meowfilm.javascript"
        thread.qualityOfService = .userInitiated
        // Keep Swift/callback frames outside QuickJS's 512 KiB stack budget.
        thread.stackSize = 2 * 1024 * 1024
        thread.start()
    }

    func submit(_ request: Request) {
        condition.lock()
        if closing {
            condition.unlock()
            request.continuation.resume(throwing: NativeJavaScriptError.closed)
            return
        }
        requests.append(request)
        condition.signal()
        condition.unlock()
    }

    func close(_ continuation: CheckedContinuation<Void, Never>?) {
        condition.lock()
        if finished {
            condition.unlock()
            continuation?.resume()
            return
        }
        closing = true
        if let continuation { closingWaiters.append(continuation) }
        active?.control.cancel()
        for request in requests { request.control.cancel() }
        condition.signal()
        condition.unlock()
    }

    private func interrupted() -> Int32 {
        // Called by QuickJS exclusively on run()'s thread.
        active?.control.error == nil ? 0 : 1
    }

    private func run() {
        var engine: OpaquePointer?
        while true {
            condition.lock()
            while requests.isEmpty && !closing { condition.wait() }
            if requests.isEmpty && closing {
                condition.unlock()
                break
            }
            let request = requests.removeFirst()
            active = request
            condition.unlock()
            let result = autoreleasepool { execute(request, engine: &engine) }
            condition.lock()
            active = nil
            condition.unlock()
            request.continuation.resume(with: result)
        }
        if let engine { mf_js_destroy(engine) }
        condition.lock()
        finished = true
        let waiters = closingWaiters
        closingWaiters.removeAll()
        condition.unlock()
        for waiter in waiters { waiter.resume() }
    }

    private func execute(_ request: Request, engine: inout OpaquePointer?) -> Result<Data, Error> {
        if let error = request.control.error { return .failure(error) }
        request.control.begin()
        if engine == nil {
            engine = source.withUnsafeBufferPointer {
                mf_js_create($0.baseAddress, $0.count - 1, 64 * 1024 * 1024, 512 * 1024,
                    { opaque in
                        guard let opaque else { return 1 }
                        return Unmanaged<Worker>.fromOpaque(opaque).takeUnretainedValue().interrupted()
                    }, Unmanaged.passUnretained(self).toOpaque())
            }
        }
        guard let current = engine else {
            return .failure(request.control.error ?? NativeJavaScriptError.executionFailed)
        }
        var output: UnsafeMutablePointer<CChar>?
        var count = 0
        let status = request.bytes.withUnsafeBytes {
            mf_js_invoke(current, $0.bindMemory(to: CChar.self).baseAddress, $0.count - 1, &output, &count)
        }
        defer { if let output { mf_js_free(output) } }
        if let error = request.control.error {
            mf_js_destroy(current); engine = nil
            return .failure(error)
        }
        guard status == 0, let output else {
            mf_js_destroy(current); engine = nil
            return .failure(NativeJavaScriptError.executionFailed)
        }
        return .success(Data(bytes: output, count: count))
    }
}
