import UIKit
import MeowDebug
import MeowRuntime

/// Application-level Debug host. Instantiated ONLY when `--debug` is present.
final class MeowfilmDebugHost: DebugControlDelegate, @unchecked Sendable {
    let runtime: DebugRuntime
    private var server: DebugHTTPServer?
    private let navigator: MeowfilmDebugNavigator
    private let diagnosticsBridge: MeowfilmDebugDiagnosticsBridge

    @MainActor
    init(configuration: DebugLaunchConfiguration, appDelegate: MeowfilmAppDelegate) throws {
        let initialDataRoot: URL
        if let override = configuration.dataRootOverride {
            initialDataRoot = override
        } else {
            let support = try FileManager.default.url(
                for: .applicationSupportDirectory,
                in: .userDomainMask,
                appropriateFor: nil,
                create: true
            )
            initialDataRoot = support.standardizedFileURL.appendingPathComponent("meowfilm", isDirectory: true)
        }

        let appVersion = (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String) ?? "0.0.0"
        let runtime = try DebugRuntime(
            configuration: configuration,
            dataRoot: initialDataRoot,
            appVersion: appVersion
        )
        DebugRuntime.install(runtime)

        self.runtime = runtime
        self.navigator = MeowfilmDebugNavigator(runtime: runtime, appDelegate: appDelegate)
        self.diagnosticsBridge = MeowfilmDebugDiagnosticsBridge(
            runtime: runtime,
            appDelegate: appDelegate,
            navigator: self.navigator
        )
        self.server = DebugHTTPServer(runtime: runtime, delegate: self)
    }

    func start() throws {
        try server?.start()
        DebugRuntime.log(.lifecycle, sub: "debug.host", event: "started", data: .object([
            "sessionId": .string(runtime.session.sessionId),
            "sessionDir": .string(runtime.session.sessionDir.path),
            "bind": .string(runtime.configuration.bindAddress),
            "requestedPort": .int(Int(runtime.configuration.port))
        ]))
    }

    @MainActor
    func attach(window: UIWindow?, coordinator: MeowfilmCoordinator?) {
        navigator.attach(window: window, coordinator: coordinator)
        DebugRuntime.log(.lifecycle, sub: "debug.host", event: "window.attached", data: .object([
            "windowReady": .bool(window != nil),
            "coordinatorReady": .bool(coordinator != nil)
        ]))
    }

    func shutdown(code: Int = 0, clean: Bool = true, lastError: String? = nil) {
        server?.stop()
        DebugRuntime.log(.lifecycle, sub: "debug.host", event: "shutdown", data: .object([
            "code": .int(code),
            "clean": .bool(clean),
            "lastError": .optional(lastError)
        ]))
        runtime.session.markExited(code: code, clean: clean, lastError: lastError)
        runtime.logger.flush()
        runtime.logger.closeAll()
        DebugRuntime.uninstall()
    }

    // MARK: - Session & Config

    func debugHealth() async throws -> DebugJSONValue {
        let meta = runtime.session.currentMetadata
        let (windowReady, runtimeStatus) = await MainActor.run { () -> (Bool, RuntimeSession?) in
            (navigator.isWindowReady, navigator.assembly?.resources?.scriptSession)
        }
        var runtimeConnected = false
        var runtimeBaseUrl: String? = meta.runtimeBaseUrl
        if let runtimeStatus {
            let status = await runtimeStatus.debugStatus()
            runtimeConnected = status["connected"]?.boolValue ?? false
            if let liveUrl = status["baseUrl"]?.stringValue {
                runtimeBaseUrl = liveUrl
            }
        }
        return .object([
            "platform": .string("ios"),
            "pid": .int(meta.pid),
            "appVersion": .string(meta.appVersion),
            "sessionId": .string(meta.sessionId),
            "sessionDir": .string(meta.sessionDir),
            "controlUrl": .string(meta.controlUrl),
            "uptimeMs": .number((runtime.session.uptimeMs * 100).rounded() / 100),
            "runtimeConnected": .bool(runtimeConnected),
            "runtimeBaseUrl": .optional(runtimeBaseUrl),
            "windowReady": .bool(windowReady),
            "dataRoot": .string(meta.dataRoot)
        ])
    }

    func debugCapabilities() async throws -> DebugJSONValue {
        let supportedTargets = [
            "music.home", "music.channels", "music.discovery", "music.personal",
            "music.daily", "music.fm", "music.library", "music.favorites",
            "music.history", "music.archives", "music.playlist", "music.player", "music.queue",
            "film.home", "film.data", "film.detail", "film.playback",
            "novel.home", "novel.categories", "novel.book", "novel.reader",
            "search",
            "settings.general", "settings.music", "settings.film", "settings.novel", "settings.cache"
        ]
        let unsupportedTargets = [
            "novel.shelf",
            "settings.media-controls"
        ]
        let actions = [
            "section.item.invoke", "section.more", "tab.select",
            "list.item.invoke", "list.load_more", "search.submit",
            "dialog.action", "setting.set", "setting.action"
        ]
        let waitConditions = [
            "idle", "route", "section_count", "row_count",
            "playback_state", "dialog", "log_event"
        ]
        let diagnostics = [
            "summary", "threads", "gc", "dump",
            "node/heapsnapshot", "node/cpu-profile", "exceptions"
        ]
        return .object([
            "platform": .string("ios"),
            "targets": .array(supportedTargets.map(DebugJSONValue.string)),
            "unsupportedTargets": .array(unsupportedTargets.map(DebugJSONValue.string)),
            "actions": .array(actions.map(DebugJSONValue.string)),
            "waitConditions": .array(waitConditions.map(DebugJSONValue.string)),
            "diagnostics": .array(diagnostics.map(DebugJSONValue.string)),
            "logCategories": .array(DebugLogCategory.allNames.map(DebugJSONValue.string)),
            "platformFeatures": .object([
                "navForward": .bool(false),
                "screenshot": .bool(true),
                "fileDownload": .bool(true),
                "readonlyStorageQuery": .bool(true),
                "runtimeInvoke": .bool(true),
                "localNetworkBind": .bool(true),
                "targetNotes": .object([
                    "novel.shelf": .string("unsupported_on_ios; use storage/query or novel/command toggle_shelf"),
                    "settings.media-controls": .string("windows_only"),
                    "settings.general.window": .string("windows_only"),
                    "settings.general.shortcuts": .string("windows_only"),
                    "music.history": .string("mapped_to_queue_songs_tab"),
                    "music.archives": .string("mapped_to_queue_playlists_tab"),
                    "music.personal": .string("mapped_to_music_library"),
                    "music.favorites": .string("mapped_to_favorites_collection_or_library")
                ])
            ])
        ])
    }

    func debugUpdateConfig(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue {
        var newModules: [String]?
        if let rawModules = body["modules"] {
            if let arr = rawModules.arrayValue {
                let strings = arr.compactMap(\.stringValue)
                guard strings.count == arr.count else {
                    throw DebugAPIError.invalidArgument("'modules' must be an array of strings")
                }
                do { newModules = try DebugLaunchConfiguration.parseModulesArray(strings) }
                catch { throw DebugAPIError.invalidArgument(error.localizedDescription) }
            } else if let csv = rawModules.stringValue {
                do { newModules = try DebugLaunchConfiguration.parseModules(csv) }
                catch { throw DebugAPIError.invalidArgument(error.localizedDescription) }
            } else {
                throw DebugAPIError.invalidArgument("'modules' must be string[] or comma-separated string")
            }
        }
        var newInlineLimit: Int?
        if let val = body["inlineLimit"] {
            guard let n = val.intValue, n >= 0 else {
                throw DebugAPIError.invalidArgument("'inlineLimit' must be an integer >= 0")
            }
            newInlineLimit = n
        }
        var newEventLimit: Int?
        if let val = body["eventBytesLimit"] {
            guard let n = val.intValue, n >= 0 else {
                throw DebugAPIError.invalidArgument("'eventBytesLimit' must be an integer >= 0")
            }
            newEventLimit = n
        }
        var newFirstChance: Bool?
        if let val = body["firstChanceExceptions"] {
            guard let b = val.boolValue else {
                throw DebugAPIError.invalidArgument("'firstChanceExceptions' must be boolean")
            }
            newFirstChance = b
        }

        let updated = runtime.session.updateOptions { opts in
            if let newModules { opts.modules = newModules }
            if let newInlineLimit { opts.inlineLimit = newInlineLimit }
            if let newEventLimit { opts.eventBytesLimit = newEventLimit }
            if let newFirstChance { opts.firstChanceExceptions = newFirstChance }
        }
        DebugRuntime.log(.lifecycle, sub: "debug.config", event: "updated", data: DebugJSONValue.fromEncodable(updated))

        var nodeSynced = false
        if let scriptSession = await MainActor.run(body: { navigator.assembly?.resources?.scriptSession }),
           let client = await scriptSession.connectionIfStarted() {
            nodeSynced = await client.debugSyncConfig(DebugJSONValue.fromEncodable(updated))
        }
        return .object([
            "options": DebugJSONValue.fromEncodable(updated),
            "nodeSynced": .bool(nodeSynced)
        ])
    }

    // MARK: - Navigation & UI Delegation

    func debugNavOpen(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue {
        try await navigator.navOpen(body)
    }

    func debugNavBack() async throws -> DebugJSONValue {
        try await navigator.navBack()
    }

    func debugNavForward() async throws -> DebugJSONValue {
        throw DebugAPIError.unsupported("iOS does not support forward navigation history")
    }

    func debugNavRefresh() async throws -> DebugJSONValue {
        try await navigator.navRefresh()
    }

    func debugUIState() async throws -> DebugJSONValue {
        await navigator.uiState()
    }

    func debugUIContent(maxItems: Int, includeRaw: Bool) async throws -> DebugJSONValue {
        await navigator.uiContent(maxItems: maxItems, includeRaw: includeRaw)
    }

    func debugUITree(maxDepth: Int, visibleOnly: Bool) async throws -> DebugJSONValue {
        await navigator.uiTree(maxDepth: maxDepth, visibleOnly: visibleOnly)
    }

    func debugUIScreenshot(save: Bool) async throws -> DebugBinaryResponse {
        try await navigator.uiScreenshot(save: save)
    }

    func debugUIAction(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue {
        try await navigator.uiAction(body)
    }

    func debugUIWait(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue {
        try await navigator.uiWait(body)
    }

    // MARK: - Playback & Novel Delegation

    func debugPlaybackState() async throws -> DebugJSONValue {
        await navigator.playbackState()
    }

    func debugPlaybackCommand(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue {
        try await navigator.playbackCommand(body)
    }

    func debugNovelCommand(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue {
        try await navigator.novelCommand(body)
    }

    // MARK: - Runtime, Storage & Diagnostics Delegation

    func debugRuntimeInvoke(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue {
        try await diagnosticsBridge.runtimeInvoke(body)
    }

    func debugStorageQuery(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue {
        try await diagnosticsBridge.storageQuery(body)
    }

    func debugDiagSummary() async throws -> DebugJSONValue {
        await diagnosticsBridge.diagSummary()
    }

    func debugDiagThreads() async throws -> DebugJSONValue {
        await diagnosticsBridge.diagThreads()
    }

    func debugDiagGC() async throws -> DebugJSONValue {
        await diagnosticsBridge.diagGC()
    }

    func debugDiagDump(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue {
        try await diagnosticsBridge.diagDump(body)
    }

    func debugDiagNodeHeapSnapshot() async throws -> DebugJSONValue {
        try await diagnosticsBridge.diagNodeHeapSnapshot()
    }

    func debugDiagNodeCPUProfile(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue {
        try await diagnosticsBridge.diagNodeCPUProfile(body)
    }

    func debugDiagExceptions(limit: Int, kind: String) async throws -> DebugJSONValue {
        diagnosticsBridge.diagExceptions(limit: limit, kind: kind)
    }
}
