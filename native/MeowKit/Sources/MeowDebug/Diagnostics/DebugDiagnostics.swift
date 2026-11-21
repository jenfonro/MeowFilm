import Foundation
import Darwin

public struct DebugExceptionRecord: Codable, Sendable {
    public let id: Int64
    public let ts: String
    public let kind: String
    public let type: String
    public let message: String
    public let thread: String
    public let stack: String
    public let opId: String?
    public let traceId: String?
}

private final class UncaughtExceptionBox: @unchecked Sendable {
    static let shared = UncaughtExceptionBox()
    let lock = NSLock()
    weak var buffer: DebugExceptionBuffer?
    var previousHandler: (@convention(c) (NSException) -> Void)?
    var installed = false
}

private func meowDebugUncaughtNSExceptionHandler(_ exception: NSException) {
    let box = UncaughtExceptionBox.shared
    box.lock.lock()
    let buffer = box.buffer
    let previous = box.previousHandler
    box.lock.unlock()
    buffer?.record(
        kind: "unhandled",
        type: exception.name.rawValue,
        message: exception.reason ?? "",
        stack: exception.callStackSymbols.joined(separator: "\n"),
        opId: nil,
        traceId: nil
    )
    DebugRuntime.log(
        .diagnostics,
        sub: "ns_exception",
        event: "uncaught",
        level: .fatal,
        data: .object([
            "name": .string(exception.name.rawValue),
            "reason": .optional(exception.reason)
        ]),
        error: DebugErrorPayload(
            code: exception.name.rawValue,
            type: "NSException",
            message: exception.reason ?? "",
            stack: exception.callStackSymbols.joined(separator: "\n")
        ),
        recordException: false
    )
    DebugRuntime.current?.logger.flush()
    previous?(exception)
}

public final class DebugExceptionBuffer: @unchecked Sendable {
    private let lock = NSLock()
    private let capacity: Int
    private var nextId: Int64 = 0
    private var records: [DebugExceptionRecord] = []

    public init(capacity: Int = 500) {
        self.capacity = max(16, capacity)
    }

    public func installUncaughtHandler() {
        let box = UncaughtExceptionBox.shared
        box.lock.lock()
        box.buffer = self
        if !box.installed {
            box.previousHandler = NSGetUncaughtExceptionHandler()
            NSSetUncaughtExceptionHandler(meowDebugUncaughtNSExceptionHandler)
            box.installed = true
        }
        box.lock.unlock()
    }

    public func record(
        kind: String,
        type: String,
        message: String,
        stack: String,
        opId: String?,
        traceId: String?
    ) {
        let threadName: String
        if Thread.isMainThread {
            threadName = "main"
        } else if let name = Thread.current.name, !name.isEmpty {
            threadName = name
        } else {
            var buf = [CChar](repeating: 0, count: 64)
            pthread_getname_np(pthread_self(), &buf, buf.count)
            let cName = String(cString: buf)
            threadName = cName.isEmpty ? "thread-\(pthread_mach_thread_np(pthread_self()))" : cName
        }

        lock.lock()
        nextId += 1
        let item = DebugExceptionRecord(
            id: nextId,
            ts: DebugTimestamp.iso8601UTC(),
            kind: kind,
            type: type,
            message: message,
            thread: threadName,
            stack: stack,
            opId: opId,
            traceId: traceId
        )
        records.append(item)
        if records.count > capacity {
            records.removeFirst(records.count - capacity)
        }
        lock.unlock()
    }

    public func snapshot(limit: Int = 100, kind: String = "all") -> [DebugJSONValue] {
        lock.lock()
        let copy = records
        lock.unlock()
        let bounded = min(max(limit, 1), capacity)
        let filtered = kind == "all" ? copy : copy.filter { $0.kind == kind }
        let tail = filtered.count > bounded ? Array(filtered.suffix(bounded)) : filtered
        return tail.map { DebugJSONValue.fromEncodable($0) }
    }
}

public struct DebugProcessMemorySnapshot: Sendable {
    public let physFootprintBytes: UInt64
    public let residentBytes: UInt64
    public let virtualBytes: UInt64
    public let compressedBytes: UInt64
    public let internalBytes: UInt64
    public let externalBytes: UInt64
    public let threadCount: Int
    public let fdCount: Int

    public func toJSON() -> DebugJSONValue {
        .object([
            "physFootprintBytes": .uint64(physFootprintBytes),
            "residentBytes": .uint64(residentBytes),
            "virtualBytes": .uint64(virtualBytes),
            "compressedBytes": .uint64(compressedBytes),
            "internalBytes": .uint64(internalBytes),
            "externalBytes": .uint64(externalBytes),
            "threadCount": .int(threadCount),
            "fdCount": .int(fdCount)
        ])
    }
}

public final class DebugMachDiagnostics: @unchecked Sendable {
    private let exceptionBuffer: DebugExceptionBuffer

    public init(exceptionBuffer: DebugExceptionBuffer) {
        self.exceptionBuffer = exceptionBuffer
    }

    public func memorySnapshot() -> DebugProcessMemorySnapshot {
        var vmInfo = task_vm_info_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.size / MemoryLayout<natural_t>.size)
        let kr = withUnsafeMutablePointer(to: &vmInfo) { ptr in
            ptr.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count)
            }
        }
        let phys = kr == KERN_SUCCESS ? UInt64(vmInfo.phys_footprint) : 0
        let resident = kr == KERN_SUCCESS ? UInt64(vmInfo.resident_size) : 0
        let virtual = kr == KERN_SUCCESS ? UInt64(vmInfo.virtual_size) : 0
        let compressed = kr == KERN_SUCCESS ? UInt64(vmInfo.compressed) : 0
        let internalBytes = kr == KERN_SUCCESS ? UInt64(vmInfo.internal) : 0
        let externalBytes = kr == KERN_SUCCESS ? UInt64(vmInfo.external) : 0

        return DebugProcessMemorySnapshot(
            physFootprintBytes: phys,
            residentBytes: resident,
            virtualBytes: virtual,
            compressedBytes: compressed,
            internalBytes: internalBytes,
            externalBytes: externalBytes,
            threadCount: machThreadCount(),
            fdCount: openFileDescriptorCount()
        )
    }

    public func machThreadCount() -> Int {
        var list: thread_act_array_t?
        var count: mach_msg_type_number_t = 0
        let kr = task_threads(mach_task_self_, &list, &count)
        guard kr == KERN_SUCCESS, let list else { return 0 }
        let total = Int(count)
        for i in 0..<total {
            mach_port_deallocate(mach_task_self_, list[i])
        }
        let byteCount = vm_size_t(total * MemoryLayout<thread_act_t>.stride)
        vm_deallocate(mach_task_self_, vm_address_t(bitPattern: list), byteCount)
        return total
    }

    public func openFileDescriptorCount() -> Int {
        guard let dir = opendir("/dev/fd") else { return -1 }
        defer { closedir(dir) }
        var count = 0
        while let entry = readdir(dir) {
            let name = withUnsafePointer(to: &entry.pointee.d_name) {
                $0.withMemoryRebound(to: CChar.self, capacity: Int(MAXNAMLEN)) { String(cString: $0) }
            }
            if name != "." && name != ".." { count += 1 }
        }
        return max(0, count - 1)
    }

    public func threadsSnapshot(mainThreadStack: [String]?, mainMachThread: mach_port_t? = nil) -> [DebugJSONValue] {
        var list: thread_act_array_t?
        var count: mach_msg_type_number_t = 0
        let kr = task_threads(mach_task_self_, &list, &count)
        guard kr == KERN_SUCCESS, let list else { return [] }
        defer {
            for i in 0..<Int(count) {
                mach_port_deallocate(mach_task_self_, list[i])
            }
            let byteCount = vm_size_t(Int(count) * MemoryLayout<thread_act_t>.stride)
            vm_deallocate(mach_task_self_, vm_address_t(bitPattern: list), byteCount)
        }

        let currentMachThread = pthread_mach_thread_np(pthread_self())
        let currentStack = Thread.callStackSymbols
        var items: [DebugJSONValue] = []
        items.reserveCapacity(Int(count))

        for i in 0..<Int(count) {
            let thread = list[i]
            var basic = thread_basic_info_data_t()
            var infoCount = mach_msg_type_number_t(
                MemoryLayout<thread_basic_info_data_t>.size / MemoryLayout<integer_t>.size
            )
            let infoResult = withUnsafeMutablePointer(to: &basic) { ptr in
                ptr.withMemoryRebound(to: integer_t.self, capacity: Int(infoCount)) {
                    thread_info(thread, thread_flavor_t(THREAD_BASIC_INFO), $0, &infoCount)
                }
            }
            var threadName = ""
            var isMain = (mainMachThread != nil && thread == mainMachThread)
            if let pt = pthread_from_mach_thread_np(thread) {
                var buf = [CChar](repeating: 0, count: 128)
                if pthread_getname_np(pt, &buf, buf.count) == 0 {
                    threadName = String(cString: buf)
                }
                if !isMain {
                    isMain = (pthread_main_np() != 0 && pt == pthread_self()) || threadName == "com.apple.main-thread"
                }
            }
            if isMain && threadName.isEmpty {
                threadName = "main"
            }

            let runState: String
            let cpuUsage: Int
            let userMs: Double
            let systemMs: Double
            if infoResult == KERN_SUCCESS {
                runState = Self.runStateName(basic.run_state)
                cpuUsage = Int(basic.cpu_usage)
                userMs = Double(basic.user_time.seconds) * 1000.0 + Double(basic.user_time.microseconds) / 1000.0
                systemMs = Double(basic.system_time.seconds) * 1000.0 + Double(basic.system_time.microseconds) / 1000.0
            } else {
                runState = "unknown"
                cpuUsage = 0
                userMs = 0
                systemMs = 0
            }

            var fields: [String: DebugJSONValue] = [
                "index": .int(i),
                "machThread": .integer(Int64(thread)),
                "name": .string(threadName),
                "isMain": .bool(isMain),
                "isCurrent": .bool(thread == currentMachThread),
                "runState": .string(runState),
                "cpuUsage": .int(cpuUsage),
                "userTimeMs": .number((userMs * 100).rounded() / 100),
                "systemTimeMs": .number((systemMs * 100).rounded() / 100)
            ]
            if thread == currentMachThread {
                fields["stack"] = .array(currentStack.map(DebugJSONValue.string))
            } else if isMain, let mainThreadStack {
                fields["stack"] = .array(mainThreadStack.map(DebugJSONValue.string))
            }
            items.append(.object(fields))
        }
        return items
    }

    public func vmRegionsSummary(limit: Int = 256) -> DebugJSONValue {
        var address: vm_address_t = 0
        var size: vm_size_t = 0
        var depth: natural_t = 0
        var totalRegions = 0
        var iterations = 0
        var totalSize: UInt64 = 0
        var byProtection: [String: UInt64] = [:]
        var sample: [DebugJSONValue] = []

        while totalRegions < 4096 && iterations < 8192 {
            iterations += 1
            var info = vm_region_submap_info_64()
            var count = mach_msg_type_number_t(MemoryLayout<vm_region_submap_info_64>.size / MemoryLayout<natural_t>.size)
            let kr = withUnsafeMutablePointer(to: &info) { ptr in
                ptr.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                    vm_region_recurse_64(mach_task_self_, &address, &size, &depth, $0, &count)
                }
            }
            if kr != KERN_SUCCESS { break }
            if info.is_submap != 0 {
                depth += 1
                continue
            }
            totalRegions += 1
            totalSize += UInt64(size)
            let prot = Self.protectionString(info.protection)
            byProtection[prot, default: 0] += UInt64(size)
            if sample.count < limit {
                sample.append(.object([
                    "address": .string(String(format: "0x%llx", UInt64(address))),
                    "sizeBytes": .uint64(UInt64(size)),
                    "protection": .string(prot),
                    "userTag": .int(Int(info.user_tag)),
                    "residentPages": .int(Int(info.pages_resident))
                ]))
            }
            let (nextAddr, overflow) = address.addingReportingOverflow(size)
            if overflow || size == 0 { break }
            address = nextAddr
        }
        return .object([
            "totalRegions": .int(totalRegions),
            "totalVirtualBytes": .uint64(totalSize),
            "byProtectionBytes": .object(byProtection.mapValues { .uint64($0) }),
            "regions": .array(sample)
        ])
    }

    private static func runStateName(_ state: integer_t) -> String {
        switch state {
        case TH_STATE_RUNNING: return "running"
        case TH_STATE_STOPPED: return "stopped"
        case TH_STATE_WAITING: return "waiting"
        case TH_STATE_UNINTERRUPTIBLE: return "uninterruptible"
        case TH_STATE_HALTED: return "halted"
        default: return "state(\(state))"
        }
    }

    private static func protectionString(_ prot: vm_prot_t) -> String {
        let r = (prot & VM_PROT_READ) != 0 ? "r" : "-"
        let w = (prot & VM_PROT_WRITE) != 0 ? "w" : "-"
        let x = (prot & VM_PROT_EXECUTE) != 0 ? "x" : "-"
        return r + w + x
    }
}
