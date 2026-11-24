import Foundation
import MeowDebug
import MeowRuntime
import MeowStorage
import MeowMusicFeature
import MeowMusicPlatform
import MeowMusicNativeService

final class MeowfilmDebugDiagnosticsBridge: @unchecked Sendable {
    private let runtime: DebugRuntime
    private weak var appDelegate: MeowfilmAppDelegate?
    private let navigator: MeowfilmDebugNavigator

    init(runtime: DebugRuntime, appDelegate: MeowfilmAppDelegate, navigator: MeowfilmDebugNavigator) {
        self.runtime = runtime
        self.appDelegate = appDelegate
        self.navigator = navigator
    }

    private func requireResources() async throws -> MeowfilmResources {
        let resources = await MainActor.run { appDelegate?.assembly?.resources }
        guard let resources else {
            throw DebugAPIError.invalidState("Application resources are not initialized yet")
        }
        return resources
    }

    private func requireRuntimeClient() async throws -> RuntimeHTTPClient {
        let resources = try await requireResources()
        do {
            return try await resources.scriptSession.connection()
        } catch {
            throw DebugAPIError.runtimeUnavailable("Embedded Node runtime is unavailable: \(error.localizedDescription)")
        }
    }

    // MARK: - Runtime Invoke & Read-Only SQLite Query

    func runtimeInvoke(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue {
        let method = body["method"]?.stringValue
        let module = body["module"]?.stringValue
        let operation = body["operation"]?.stringValue
        let input = body["input"] ?? .object([:])
        let timeoutMs = body["timeoutMs"]?.intValue

        let resolved = try RuntimeHTTPClient.resolveDebugInvokeTarget(
            method: method,
            module: module,
            operation: operation
        )
        let client = try await requireRuntimeClient()
        return try await client.debugInvoke(
            method: nil,
            module: resolved.module,
            operation: resolved.operation,
            input: input,
            timeoutMs: timeoutMs
        )
    }

    func storageQuery(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue {
        guard let sql = body["sql"]?.stringValue?.trimmingCharacters(in: .whitespacesAndNewlines),
              !sql.isEmpty else {
            throw DebugAPIError.invalidArgument("storage/query requires non-empty string 'sql'")
        }
        let dbName: String
        if let rawDB = body["db"] {
            guard let parsed = rawDB.stringValue, parsed == "data" || parsed == "cache" else {
                throw DebugAPIError.invalidArgument("storage/query 'db' must be 'data' or 'cache'")
            }
            dbName = parsed
        } else {
            dbName = "data"
        }
        let params: [DebugJSONValue]
        if let rawParams = body["params"] {
            guard let arr = rawParams.arrayValue else {
                throw DebugAPIError.invalidArgument("storage/query 'params' must be a JSON array")
            }
            params = arr
        } else {
            params = []
        }
        let maxRows: Int
        if let rawMax = body["maxRows"] {
            guard let parsed = rawMax.intValue, parsed >= 1 else {
                throw DebugAPIError.invalidArgument("storage/query 'maxRows' must be an integer >= 1")
            }
            maxRows = min(parsed, 5000)
        } else {
            maxRows = 200
        }
        let resources = try await requireResources()
        let targetDB = dbName == "cache" ? resources.cacheDatabase : resources.database
        let result = try await targetDB.debugReadOnlyQuery(
            sql: sql,
            params: params,
            maxRows: maxRows
        )
        return result.toJSON()
    }

    // MARK: - Diagnostics

    func diagSummary() async -> DebugJSONValue {
        let memory = runtime.diagnostics.memorySnapshot()
        let nativeLeases = await collectActiveLeases()

        var nodeDiag: DebugJSONValue = .object([
            "available": .bool(false),
            "reason": .string("RUNTIME_NOT_STARTED")
        ])
        if let resources = await MainActor.run(body: { appDelegate?.assembly?.resources }),
           let client = await resources.scriptSession.connectionIfStarted() {
            nodeDiag = await client.debugNodeDiagnostics()
        }

        let meta = runtime.session.currentMetadata
        return .object([
            "platform": .string("ios"),
            "pid": .int(meta.pid),
            "uptimeMs": .number((runtime.session.uptimeMs * 100).rounded() / 100),
            "native": .object([
                "memory": memory.toJSON(),
                "threadCount": .int(memory.threadCount),
                "fdCount": .int(memory.fdCount),
                "leases": nativeLeases
            ]),
            "node": nodeDiag
        ])
    }

    func diagThreads() async -> DebugJSONValue {
        let (mainStack, mainMach): ([String], mach_port_t) = await MainActor.run {
            (Thread.callStackSymbols, pthread_mach_thread_np(pthread_self()))
        }
        let threads = runtime.diagnostics.threadsSnapshot(mainThreadStack: mainStack, mainMachThread: mainMach)

        var nodeDiag: DebugJSONValue = .object([
            "available": .bool(false),
            "reason": .string("RUNTIME_NOT_STARTED")
        ])
        if let resources = await MainActor.run(body: { appDelegate?.assembly?.resources }),
           let client = await resources.scriptSession.connectionIfStarted() {
            nodeDiag = await client.debugNodeDiagnostics()
        }

        return .object([
            "platform": .string("ios"),
            "threadCount": .int(threads.count),
            "threads": .array(threads),
            "node": nodeDiag
        ])
    }

    func diagGC() async -> DebugJSONValue {
        let before = runtime.diagnostics.memorySnapshot()

        URLCache.shared.removeAllCachedResponses()
        let musicModel = await MainActor.run { () -> MusicViewModel? in
            autoreleasepool {
                appDelegate?.assembly?.initializedFilmImagesForDiagnostics?.debugClearMemoryCache()
                appDelegate?.assembly?.musicImages.debugClearMemoryCache()
                appDelegate?.assembly?.novel?.images.debugClearMemoryCache()
            }
            return appDelegate?.assembly?.music
        }
        await musicModel?.debugClearMemoryCaches()

        var nodeGC: DebugJSONValue = .object([
            "available": .bool(false),
            "reason": .string("RUNTIME_NOT_STARTED")
        ])
        if let resources = await MainActor.run(body: { appDelegate?.assembly?.resources }),
           let client = await resources.scriptSession.connectionIfStarted() {
            nodeGC = await client.debugNodeGC()
        }

        let after = runtime.diagnostics.memorySnapshot()
        let deltaFootprint = Int64(before.physFootprintBytes) - Int64(after.physFootprintBytes)
        let deltaResident = Int64(before.residentBytes) - Int64(after.residentBytes)

        DebugRuntime.log(.diagnostics, sub: "gc", event: "complete", data: .object([
            "beforePhysFootprintBytes": .uint64(before.physFootprintBytes),
            "afterPhysFootprintBytes": .uint64(after.physFootprintBytes),
            "freedPhysFootprintBytes": .integer(deltaFootprint)
        ]))

        return .object([
            "before": before.toJSON(),
            "after": after.toJSON(),
            "freedPhysFootprintBytes": .integer(deltaFootprint),
            "freedResidentBytes": .integer(deltaResident),
            "node": nodeGC
        ])
    }

    func diagDump(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue {
        let mode = body["mode"]?.stringValue?.lowercased() ?? "mini"
        guard mode == "mini" || mode == "full" || mode == "heap" else {
            throw DebugAPIError.invalidArgument("Invalid diag/dump mode: \(mode) (expected mini, full, or heap)")
        }
        let defaultVMLimit = mode == "mini" ? 256 : 1024
        let vmLimit = min(max(body["vmRegionLimit"]?.intValue ?? defaultVMLimit, 16), 2048)
        let summary = await diagSummary()
        let threads = await diagThreads()
        let vmRegions = runtime.diagnostics.vmRegionsSummary(limit: vmLimit)
        let exceptions = runtime.exceptionBuffer.snapshot(limit: 100, kind: "all")
        let uiState = await navigator.uiState()
        let playback = await navigator.playbackState()

        let report: DebugJSONValue = .object([
            "schemaVersion": .int(1),
            "generatedAt": .string(DebugTimestamp.iso8601UTC()),
            "sessionId": .string(runtime.session.sessionId),
            "mode": .string(mode),
            "summary": summary,
            "threads": threads,
            "vmRegions": vmRegions,
            "exceptions": .array(exceptions),
            "uiState": uiState,
            "playback": playback
        ])
        let data = try report.encode(pretty: true)
        let saved = try runtime.session.saveDump(kind: "ios-diag", ext: "json", data: data)

        DebugRuntime.log(.diagnostics, sub: "dump", event: "saved", data: .object([
            "path": .string(saved.relativePath),
            "relativePath": .string(saved.relativePath),
            "mode": .string(mode),
            "bytes": .int(data.count)
        ]))

        return .object([
            "path": .string(saved.relativePath),
            "relativePath": .string(saved.relativePath),
            "absolutePath": .string(saved.absoluteURL.path),
            "mode": .string(mode),
            "bytes": .int(data.count),
            "summary": summary
        ])
    }

    func diagNodeHeapSnapshot() async throws -> DebugJSONValue {
        let client = try await requireRuntimeClient()
        return try await client.debugNodeHeapSnapshot()
    }

    func diagNodeCPUProfile(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue {
        let durationMs: Int
        if let rawDuration = body["durationMs"] {
            guard let parsed = rawDuration.intValue, parsed >= 500, parsed <= 60000 else {
                throw DebugAPIError.invalidArgument("durationMs must be an integer between 500 and 60000")
            }
            durationMs = parsed
        } else {
            durationMs = 5000
        }
        let client = try await requireRuntimeClient()
        return try await client.debugNodeCPUProfile(durationMs: durationMs)
    }

    func diagExceptions(limit: Int, kind: String) -> DebugJSONValue {
        let items = runtime.exceptionBuffer.snapshot(limit: limit, kind: kind)
        return .object([
            "kind": .string(kind),
            "count": .int(items.count),
            "items": .array(items)
        ])
    }

    // MARK: - Active Business Leases

    private func collectActiveLeases() async -> DebugJSONValue {
        let (playbackSnap, filmMediaSnap, filmImagesStats, musicImagesStats, novelImagesStats, filmModelsCount, resources, musicModel, appearance) = await MainActor.run {
            let assembly = appDelegate?.assembly
            return (
                assembly?.playback.debugSnapshot(),
                assembly?.filmMedia?.debugSnapshot(),
                assembly?.initializedFilmImagesForDiagnostics?.debugCacheStats(),
                assembly?.musicImages.debugCacheStats(),
                assembly?.novel?.images.debugCacheStats(),
                assembly?.activeFilmModels.count ?? 0,
                assembly?.resources,
                assembly?.music,
                assembly?.appearance
            )
        }
        let runtimeStatus: DebugJSONValue
        if let session = resources?.scriptSession {
            runtimeStatus = await session.debugStatus()
        } else {
            runtimeStatus = .object(["connected": .bool(false), "started": .bool(false)])
        }
        let audioCacheStats: DebugJSONValue
        if let cache = resources?.musicAudioCache {
            audioCacheStats = await cache.debugCacheStats()
        } else {
            audioCacheStats = .null
        }
        let presentationCacheStats: DebugJSONValue
        if let cache = resources?.musicPresentationCache {
            presentationCacheStats = await cache.debugStats()
        } else {
            presentationCacheStats = .null
        }
        let accountCacheStats: DebugJSONValue
        if let accounts = resources?.musicAccounts {
            accountCacheStats = await accounts.debugCacheStats()
        } else {
            accountCacheStats = .null
        }
        let discoveryCacheStats: DebugJSONValue
        if let discovery = resources?.musicSourceDiscoveryCache {
            discoveryCacheStats = await discovery.debugStats()
        } else {
            discoveryCacheStats = .null
        }
        let homeSnapshotStats = resources?.musicHomeSnapshot.debugStats() ?? .null
        let songPaletteStats: DebugJSONValue
        if let appearance {
            songPaletteStats = await appearance.debugPaletteStats()
        } else {
            songPaletteStats = .null
        }
        let musicMemoryStats: DebugJSONValue
        if let musicModel {
            musicMemoryStats = await musicModel.debugCacheStats()
        } else {
            musicMemoryStats = .null
        }
        return .object([
            "playback": playbackSnap ?? .null,
            "filmMedia": filmMediaSnap ?? .null,
            "filmImagesCache": filmImagesStats ?? .null,
            "musicImagesCache": musicImagesStats ?? .null,
            "novelImagesCache": novelImagesStats ?? .null,
            "uiMetrics": .object(DebugUIMetrics.shared.snapshot()),
            "activeFilmModelsCount": .int(filmModelsCount),
            "musicAudioFileCache": audioCacheStats,
            "musicPresentationCache": presentationCacheStats,
            "musicAccountCaches": accountCacheStats,
            "musicSourceDiscoveryCache": discoveryCacheStats,
            "musicHomeSnapshot": homeSnapshotStats,
            "musicSongPalettes": songPaletteStats,
            "musicMemoryCaches": musicMemoryStats,
            "runtimeSession": runtimeStatus
        ])
    }
}
