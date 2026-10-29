import Foundation

/// Counters for this controller's controlled transports, including cancelled
/// tasks that have not completed. Not total AVFoundation/decoder/RSS usage.
public struct PlaybackNetworkUsage: Equatable, Sendable {
    public let activeTasks: Int
    public let reservedPayloadBytes: Int
    public let activeConnections: Int
    public let activeListeners: Int
    init(activeTasks: Int, reservedPayloadBytes: Int, activeConnections: Int = 0, activeListeners: Int = 0) {
        self.activeTasks = activeTasks; self.reservedPayloadBytes = reservedPayloadBytes
        self.activeConnections = activeConnections; self.activeListeners = activeListeners
    }
}

struct PlaybackNetworkLimits {
    // Conservative product-wide cap, including retiring generations. Keep it
    // no larger than the default host cap so redirect waves cannot overbook a
    // CDN by first acquiring slots against several initial hosts.
    var activeTasks = 2
    var tasksPerHost = 2
    var retainedPayloadBytes = 16 * 1024 * 1024
    var windowBytes = 256 * 1024
    var deliveryBytes = 64 * 1024
    var requestsPerAsset = 8
    var budgetWaitSeconds: TimeInterval = 30
    var hlsConnections = 8
    var hlsListeners = 2
}

/// Product/controller lifetime, not item lifetime. A cancelled old item's
/// uncooperative task therefore cannot donate its slot to the next item.
final class PlaybackNetworkBudget: @unchecked Sendable {
    let limits: PlaybackNetworkLimits
    private let lock = NSLock()
    private var tasks: [UUID: String] = [:]
    private var bytes = 0
    private var listeners: [UUID: @Sendable () -> Void] = [:]
    private var auxiliary: [UUID: PlaybackAuxiliaryReservation.Kind] = [:]

    init(limits: PlaybackNetworkLimits = .init()) {
        precondition(limits.activeTasks > 0 && limits.tasksPerHost > 0 && limits.windowBytes > 0 &&
                     limits.deliveryBytes > 0 && limits.deliveryBytes <= limits.windowBytes &&
                     limits.retainedPayloadBytes >= limits.windowBytes && limits.requestsPerAsset > 0 &&
                     limits.budgetWaitSeconds > 0)
        self.limits = limits
    }

    var usage: PlaybackNetworkUsage {
        lock.lock(); defer { lock.unlock() }
        return PlaybackNetworkUsage(activeTasks: tasks.count, reservedPayloadBytes: bytes,
            activeConnections: auxiliary.values.filter { $0 == .connection }.count,
            activeListeners: auxiliary.values.filter { $0 == .listener }.count)
    }

    func observe(_ callback: @escaping @Sendable () -> Void) -> UUID {
        lock.lock(); defer { lock.unlock() }
        let id = UUID(); listeners[id] = callback
        return id
    }

    func unobserve(_ id: UUID) {
        lock.lock(); defer { lock.unlock() }
        listeners[id] = nil
    }

    func acquire(host: String, bytes requested: Int, additionalStorageBytes: Int = 0) -> PlaybackNetworkReservation? {
        lock.lock(); defer { lock.unlock() }
        guard requested > 0, requested <= limits.windowBytes,
              additionalStorageBytes >= 0,
              tasks.count < limits.activeTasks,
              tasks.values.filter({ $0 == host }).count < limits.tasksPerHost,
              requested <= limits.retainedPayloadBytes - bytes,
              additionalStorageBytes <= limits.retainedPayloadBytes - bytes - requested else { return nil }
        let id = UUID()
        let total = requested + additionalStorageBytes
        tasks[id] = host; bytes += total
        return PlaybackNetworkReservation(budget: self, id: id, bytes: total)
    }

    fileprivate func move(_ id: UUID, to host: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard let old = tasks[id] else { return false }
        if old == host { return true }
        guard tasks.values.filter({ $0 == host }).count < limits.tasksPerHost else { return false }
        tasks[id] = host
        return true
    }

    fileprivate func release(bytes count: Int, task: UUID? = nil) {
        lock.lock()
        bytes -= count
        if let task { tasks[task] = nil }
        assert(bytes >= 0)
        let callbacks = Array(listeners.values)
        lock.unlock()
        callbacks.forEach { $0() }
    }

    func acquireAuxiliary(_ kind: PlaybackAuxiliaryReservation.Kind, bytes requested: Int) -> PlaybackAuxiliaryReservation? {
        lock.lock(); defer { lock.unlock() }
        let maximum = kind == .connection ? limits.hlsConnections : kind == .listener ? limits.hlsListeners : Int.max
        guard requested >= 0, requested <= limits.retainedPayloadBytes - bytes,
              auxiliary.values.filter({ $0 == kind }).count < maximum else { return nil }
        let id = UUID(); auxiliary[id] = kind; bytes += requested
        return PlaybackAuxiliaryReservation(budget: self, id: id, bytes: requested)
    }

    fileprivate func releaseAuxiliary(_ id: UUID, bytes count: Int) {
        lock.lock()
        guard auxiliary.removeValue(forKey: id) != nil else { lock.unlock(); return }
        bytes -= count
        let callbacks = Array(listeners.values)
        lock.unlock()
        callbacks.forEach { $0() }
    }
}

/// Connections/listeners release only after Network.framework's cancelled
/// state. Storage releases after its owning bytes/maps have been discarded.
final class PlaybackAuxiliaryReservation: @unchecked Sendable {
    enum Kind { case listener, connection, storage }
    private let budget: PlaybackNetworkBudget
    private let id: UUID
    private let bytes: Int
    fileprivate init(budget: PlaybackNetworkBudget, id: UUID, bytes: Int) {
        self.budget = budget; self.id = id; self.bytes = bytes
    }
    func complete() { budget.releaseAuxiliary(id, bytes: bytes) }
}

final class PlaybackNetworkReservation: @unchecked Sendable {
    private let budget: PlaybackNetworkBudget
    private let id: UUID
    private let lock = NSLock()
    private var remaining: Int
    private var finished = false

    fileprivate init(budget: PlaybackNetworkBudget, id: UUID, bytes: Int) {
        self.budget = budget; self.id = id; remaining = bytes
    }

    func move(to host: String) -> Bool { budget.move(id, to: host) }

    /// A bounded copy whose ownership can be transferred to AVFoundation.
    /// The reservation remains charged until the Data backing store is released.
    /// Framework-internal copies/decoded frames are outside this counter.
    func payload(_ data: Data) throws -> Data {
        lock.lock()
        guard !finished, !data.isEmpty, data.count <= remaining else {
            lock.unlock()
            throw PlaybackNetworkError.resourceBudgetExceeded
        }
        remaining -= data.count
        lock.unlock()
        let count = data.count
        let pointer = UnsafeMutableRawPointer.allocate(byteCount: count, alignment: 1)
        data.copyBytes(to: pointer.assumingMemoryBound(to: UInt8.self), count: count)
        let owner = budget
        return Data(bytesNoCopy: pointer, count: count, deallocator: .custom { address, size in
            address.deallocate()
            owner.release(bytes: size)
        })
    }

    /// Only URLSession's didComplete callback (or never-started-task cleanup)
    /// may call this. Calling task.cancel() is NOT completion.
    func complete() {
        lock.lock()
        guard !finished else { lock.unlock(); return }
        finished = true
        let unused = remaining; remaining = 0
        lock.unlock()
        budget.release(bytes: unused, task: id)
    }
}
