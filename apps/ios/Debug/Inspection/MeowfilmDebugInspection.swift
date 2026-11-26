import UIKit
import MeowDebug
import MeowUI
import MeowMusicFeature

@MainActor
extension MeowfilmDebugNavigator {
    // MARK: - UI State, Content, Tree, Screenshot

    func uiState() -> DebugJSONValue {
        let activeVC = activeBusinessController()
        let status = statusFor(activeVC)

        var navStack: [DebugJSONValue] = []
        if let vcs = coordinator?.navigation.viewControllers {
            for vc in vcs {
                let s = statusFor(unwrapInspectable(vc))
                navStack.append(.object([
                    "target": .string(s.target),
                    "route": .string(s.route),
                    "title": .string(s.title)
                ]))
            }
        }
        for modal in presentedModalChain() where !isDialogController(modal) {
            let s = statusFor(unwrapInspectable(modal))
            navStack.append(.object([
                "target": .string(s.target),
                "route": .string(s.route),
                "title": .string(s.title)
            ]))
        }

        let settingsOpen = (coordinator?.navigation.viewControllers.contains {
            ($0 as? PageViewController)?.pageChromePolicy.isSettingsBranchRoot == true
        } ?? false)
            || status.settingsDomain != nil

        let canGoBack = (coordinator?.root.isSidebarVisible == true)
            || !presentedModalChain().isEmpty
            || ((coordinator?.navigation.viewControllers.count ?? 0) > 1)

        let activeDialogJSON: DebugJSONValue
        if let dialog = activeDialogController() {
            activeDialogJSON = describeDialog(dialog)
        } else {
            activeDialogJSON = .null
        }

        var notices: [DebugJSONValue] = []
        if let music = assembly?.music {
            if let err = music.notices.error {
                notices.append(.object([
                    "id": .string("music.error"),
                    "level": .string("error"),
                    "message": .string(err)
                ]))
            }
        }
        if assembly?.startupFailed == true {
            notices.append(.object([
                "id": .string("app.startupFailed"),
                "level": .string("error"),
                "message": .string("本地数据不可用")
            ]))
        }
        if let shutdownErr = assembly?.shutdownError {
            notices.append(.object([
                "id": .string("app.shutdownError"),
                "level": .string("error"),
                "message": .string(shutdownErr)
            ]))
        }
        if let filmModel = assembly?.filmMediaPlayback ?? assembly?.filmPlayback {
            let filmErr = filmModel.actionError ?? filmModel.observationError
                ?? filmModel.mediaStopError ?? filmModel.sessionReleaseError
            if let filmErr {
                notices.append(.object([
                    "id": .string("film.error.\(filmErr.code)"),
                    "level": .string("error"),
                    "message": .string(filmErr.localizedDescription)
                ]))
            }
        }

        let bounds = window?.bounds ?? .zero
        let scale = window?.windowScene?.screen.scale ?? window?.traitCollection.displayScale ?? 3
        let windowJSON: DebugJSONValue = .object([
            "visible": .bool(window != nil && window?.isHidden == false),
            "active": .bool(UIApplication.shared.applicationState == .active),
            "width": .number(Double(bounds.width)),
            "height": .number(Double(bounds.height)),
            "scale": .number(Double(scale)),
            "sidebarVisible": .bool(coordinator?.root.isSidebarVisible ?? false)
        ])

        return .object([
            "target": .string(status.target),
            "moduleId": .string(status.moduleId),
            "route": .string(status.route),
            "title": .string(status.title),
            "settingsOpen": .bool(settingsOpen),
            "settingsDomain": .optional(status.settingsDomain),
            "settingsSection": .optional(status.settingsSection),
            "navigationStack": .array(navStack),
            "canGoBack": .bool(canGoBack),
            "canGoForward": .bool(false),
            "isBusy": .bool(status.isBusy),
            "statusText": .optional(status.statusText),
            "isError": .bool(status.isError),
            "errorText": .optional(status.errorText),
            "activeDialog": activeDialogJSON,
            "notices": .array(notices),
            "window": windowJSON
        ])
    }

    func uiContent(maxItems: Int, includeRaw: Bool) -> DebugJSONValue {
        let activeVC = activeBusinessController()
        if let inspectable = activeVC as? any DebugPageInspectable {
            return inspectable.debugContent(maxItems: maxItems, includeRaw: includeRaw).toJSON()
        }
        let status = statusFor(activeVC)
        return DebugContentSnapshot(
            target: status.target,
            kind: "empty",
            loading: status.isBusy,
            error: status.errorText
        ).toJSON()
    }

    func uiTree(maxDepth: Int, visibleOnly: Bool) -> DebugJSONValue {
        guard let window else {
            return .object(["error": .string("window not attached"), "children": .array([])])
        }
        let clampedDepth = min(max(maxDepth, 1), 24)
        var vcMap: [ObjectIdentifier: UIViewController] = [:]
        if let root = window.rootViewController {
            indexViewControllers(root, into: &vcMap)
        }
        return serializeView(window, depth: 0, maxDepth: clampedDepth, visibleOnly: visibleOnly, window: window, vcMap: vcMap)
    }

    func uiScreenshot(save: Bool) throws -> DebugBinaryResponse {
        guard let window else {
            throw DebugAPIError.invalidState("UIWindow is not ready for screenshot")
        }
        let bounds = window.bounds
        guard bounds.width > 0, bounds.height > 0 else {
            throw DebugAPIError.invalidState("UIWindow has zero bounds")
        }
        let format = UIGraphicsImageRendererFormat()
        format.scale = window.screen.scale
        format.opaque = false
        let renderer = UIGraphicsImageRenderer(bounds: bounds, format: format)
        let image = renderer.image { ctx in
            if !window.drawHierarchy(in: bounds, afterScreenUpdates: true) {
                window.layer.render(in: ctx.cgContext)
            }
        }
        guard let png = image.pngData() else {
            throw DebugAPIError.internalError("Failed to encode window screenshot to PNG")
        }
        var headers: [String: String] = [:]
        if save {
            let saved = try runtime.session.saveScreenshotPNG(png)
            headers["X-Meow-Screenshot-Path"] = saved.relativePath
            DebugRuntime.log(.uiAction, sub: "screenshot", event: "saved", data: .object([
                "path": .string(saved.relativePath),
                "relativePath": .string(saved.relativePath),
                "bytes": .int(png.count),
                "width": .int(Int(bounds.width * format.scale)),
                "height": .int(Int(bounds.height * format.scale))
            ]))
        }
        return DebugBinaryResponse(data: png, contentType: "image/png", headers: headers)
    }

    // MARK: - Condition Waiting (ui/wait)

    func uiWait(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue {
        guard let condition = body["condition"]?.stringValue else {
            throw DebugAPIError.invalidArgument("Missing required string field 'condition'")
        }
        let params = body["params"]?.objectValue ?? [:]
        let timeoutMs = min(max(body["timeoutMs"]?.intValue ?? 15000, 100), 120_000)

        if condition == "log_event" {
            return try await waitForLogEvent(params: params, timeoutMs: timeoutMs)
        }
        let supportedConditions: Set<String> = ["idle", "route", "section_count", "row_count", "playback_state", "dialog"]
        guard supportedConditions.contains(condition) else {
            throw DebugAPIError.invalidArgument("Unknown ui/wait condition: \(condition)")
        }
        if condition == "route", params["target"]?.stringValue == nil, params["route"]?.stringValue == nil {
            throw DebugAPIError.invalidArgument("route wait condition requires 'target' or 'route'")
        }
        if condition == "playback_state" {
            if let rawDomain = params["domain"] {
                guard let str = rawDomain.stringValue, str == "music" || str == "film" else {
                    throw DebugAPIError.invalidArgument("playback_state 'domain' must be 'music' or 'film'")
                }
            }
            if let rawState = params["state"] {
                guard let wantState = rawState.stringValue,
                      ["playing", "paused", "buffering", "stopped", "ended", "error"].contains(wantState) else {
                    throw DebugAPIError.invalidArgument("Invalid playback_state 'state'")
                }
            }
        }

        let deadline = Date().addingTimeInterval(Double(timeoutMs) / 1000.0)
        while true {
            if let matched = evaluateWaitCondition(condition: condition, params: params) {
                return matched
            }
            if Date() >= deadline {
                var details: [String: DebugJSONValue] = [
                    "condition": .string(condition),
                    "state": uiState()
                ]
                if condition == "playback_state" {
                    details["playback"] = playbackState()
                }
                throw DebugAPIError.timeout("ui/wait timed out after \(timeoutMs)ms waiting for '\(condition)'", details: .object(details))
            }
            try? await Task.sleep(nanoseconds: 50_000_000)
        }
    }

    private func evaluateWaitCondition(condition: String, params: [String: DebugJSONValue]) -> DebugJSONValue? {
        switch condition {
        case "idle":
            let state = uiState()
            let busy = state["isBusy"]?.boolValue ?? true
            let transitioning = coordinator?.navigation.transitionCoordinator != nil
            if !busy && !transitioning {
                return .object(["matched": .bool(true), "condition": .string("idle"), "state": state])
            }
            return nil

        case "route":
            let state = uiState()
            let wantTarget = params["target"]?.stringValue
            let wantRoute = params["route"]?.stringValue
            let curTarget = state["target"]?.stringValue
            let curRoute = state["route"]?.stringValue
            if let wantTarget, curTarget == wantTarget {
                return .object(["matched": .bool(true), "condition": .string("route"), "state": state])
            }
            if let wantRoute, curRoute == wantRoute {
                return .object(["matched": .bool(true), "condition": .string("route"), "state": state])
            }
            return nil

        case "section_count":
            let content = uiContent(maxItems: 200, includeRaw: false)
            let sections = content["sections"]?.arrayValue ?? []
            let minSections = params["minSections"]?.intValue ?? 1
            guard sections.count >= minSections else { return nil }
            if let sIdx = params["sectionIndex"]?.intValue {
                guard sections.indices.contains(sIdx) else { return nil }
                let minItems = params["minItems"]?.intValue ?? 1
                let count = sections[sIdx]["itemCount"]?.intValue ?? (sections[sIdx]["items"]?.arrayValue?.count ?? 0)
                guard count >= minItems else { return nil }
            }
            return .object(["matched": .bool(true), "condition": .string("section_count"), "sectionCount": .int(sections.count)])

        case "row_count":
            let content = uiContent(maxItems: 500, includeRaw: false)
            let rows = content["rows"]?.arrayValue ?? []
            let minRows = params["minRows"]?.intValue ?? 1
            guard rows.count >= minRows else { return nil }
            return .object(["matched": .bool(true), "condition": .string("row_count"), "rowCount": .int(rows.count)])

        case "playback_state":
            let domain = params["domain"]?.stringValue ?? "music"
            let wantState = params["state"]?.stringValue ?? "playing"
            let pb = playbackState()
            let actual = pb[domain]?["state"]?.stringValue
            guard actual == wantState else { return nil }
            return .object(["matched": .bool(true), "condition": .string("playback_state"), "playback": pb])

        case "dialog":
            let wantOpen = params["open"]?.boolValue ?? true
            let isOpen = activeDialogController() != nil
            guard isOpen == wantOpen else { return nil }
            return .object(["matched": .bool(true), "condition": .string("dialog"), "state": uiState()])

        default:
            return nil
        }
    }

    private func waitForLogEvent(params: [String: DebugJSONValue], timeoutMs: Int) async throws -> DebugJSONValue {
        guard let cat = params["cat"]?.stringValue, !cat.isEmpty else {
            throw DebugAPIError.invalidArgument("log_event wait requires 'cat'")
        }
        guard cat == "all" || DebugLogCategory(rawValue: cat) != nil else {
            throw DebugAPIError.invalidArgument("Unknown log category for log_event wait: \(cat)")
        }
        let wantSub = params["sub"]?.stringValue
        let wantEvent = params["event"]?.stringValue
        let sinceSeq = params["sinceSeq"]?.intValue.map(Int64.init)
        let waitStartedTS = DebugTimestamp.iso8601UTC()

        if let sinceSeq,
           let existing = try? runtime.logger.tail(.init(
               cat: cat,
               limit: 100,
               sinceSeq: sinceSeq,
               sub: wantSub,
               event: wantEvent
           )),
           let first = existing.first {
            return .object(["matched": .bool(true), "condition": .string("log_event"), "event": first])
        }

        let logger = runtime.logger
        return try await withThrowingTaskGroup(of: DebugJSONValue.self) { group in
            let subBox = LogSubscriptionBox(logger: logger)
            group.addTask {
                try await withTaskCancellationHandler {
                    try await withCheckedThrowingContinuation { continuation in
                        subBox.start(cat: cat, sub: wantSub, event: wantEvent, sinceSeq: sinceSeq, continuation: continuation)
                    }
                } onCancel: {
                    subBox.cancelWithError(CancellationError())
                }
            }
            group.addTask {
                while !Task.isCancelled {
                    try await Task.sleep(nanoseconds: 150_000_000)
                    if let entries = try? logger.tail(.init(
                        cat: cat,
                        limit: 200,
                        sinceSeq: sinceSeq,
                        sub: wantSub,
                        event: wantEvent
                    )) {
                        let match: DebugJSONValue?
                        if sinceSeq != nil {
                            match = entries.first
                        } else {
                            match = entries.first { ($0["ts"]?.stringValue ?? "") >= waitStartedTS }
                        }
                        if let match {
                            return .object(["matched": .bool(true), "condition": .string("log_event"), "event": match])
                        }
                    }
                }
                throw CancellationError()
            }
            group.addTask {
                try await Task.sleep(nanoseconds: UInt64(timeoutMs) * 1_000_000)
                subBox.cancelWithTimeout(timeoutMs: timeoutMs, cat: cat)
                throw DebugAPIError.timeout("Timed out after \(timeoutMs)ms waiting for log_event \(cat)")
            }
            defer {
                subBox.cleanup()
                group.cancelAll()
            }
            guard let first = try await group.next() else {
                throw DebugAPIError.timeout("Timed out waiting for log_event")
            }
            return first
        }
    }

    // MARK: - Dialog & View Tree Serialization

    func describeDialog(_ dialog: UIViewController) -> DebugJSONValue {
        if let alert = dialog as? UIAlertController {
            let registered = DebugAlertActionRegistry.shared.actions(for: alert)
            let buttons: [DebugJSONValue]
            if let registered, !registered.isEmpty {
                buttons = registered.map { act in
                    .object([
                        "index": .int(act.index),
                        "label": .string(act.label),
                        "kind": .string(act.kind)
                    ])
                }
            } else {
                buttons = alert.actions.enumerated().map { idx, act in
                    let kind: String
                    switch act.style {
                    case .cancel: kind = "cancel"
                    case .destructive: kind = "destructive"
                    default: kind = "default"
                    }
                    return .object([
                        "index": .int(idx),
                        "label": .string(act.title ?? ""),
                        "kind": .string(kind)
                    ])
                }
            }
            return .object([
                "type": .string("UIAlertController"),
                "title": .optional(alert.title),
                "message": .optional(alert.message),
                "buttons": .array(buttons)
            ])
        }
        if let controllable = dialog as? any DebugDialogControllable {
            let snapshot = controllable.debugDialogSnapshot()
            let buttons = snapshot.actions.map { act -> DebugJSONValue in
                .object([
                    "index": .int(act.index),
                    "label": .string(act.label),
                    "kind": .string(act.kind),
                    "enabled": .bool(act.enabled)
                ])
            }
            return .object([
                "type": .string(snapshot.typeName),
                "title": .string(snapshot.title),
                "message": .optional(snapshot.message),
                "errorText": .optional(snapshot.errorText),
                "buttons": .array(buttons)
            ])
        }
        return .null
    }

    private func indexViewControllers(_ vc: UIViewController, into map: inout [ObjectIdentifier: UIViewController]) {
        if let v = vc.viewIfLoaded {
            map[ObjectIdentifier(v)] = vc
        }
        for child in vc.children {
            indexViewControllers(child, into: &map)
        }
        if let presented = vc.presentedViewController {
            indexViewControllers(presented, into: &map)
        }
    }

    private func serializeView(
        _ view: UIView,
        depth: Int,
        maxDepth: Int,
        visibleOnly: Bool,
        window: UIWindow,
        vcMap: [ObjectIdentifier: UIViewController]
    ) -> DebugJSONValue {
        let frame = view.convert(view.bounds, to: window)
        let visible = !view.isHidden && view.alpha >= 0.01
        let enabled = (view as? UIControl)?.isEnabled ?? view.isUserInteractionEnabled
        let text: String?
        if let label = view as? UILabel {
            text = label.text
        } else if let button = view as? UIButton {
            text = button.currentTitle ?? button.configuration?.title
        } else if let field = view as? UITextField {
            text = field.text
        } else if let tv = view as? UITextView {
            text = tv.text
        } else if let bar = view as? UISearchBar {
            text = bar.text
        } else {
            text = nil
        }

        var children: [DebugJSONValue] = []
        if depth < maxDepth {
            for sub in view.subviews {
                if visibleOnly && (sub.isHidden || sub.alpha < 0.01) { continue }
                children.append(serializeView(sub, depth: depth + 1, maxDepth: maxDepth, visibleOnly: visibleOnly, window: window, vcMap: vcMap))
            }
        }

        let ownerName = vcMap[ObjectIdentifier(view)].map { String(describing: type(of: $0)) }
        return .object([
            "type": .string(String(describing: type(of: view))),
            "controller": .optional(ownerName),
            "name": .optional(view.accessibilityLabel),
            "automationId": .optional(view.accessibilityIdentifier),
            "visible": .bool(visible),
            "enabled": .bool(enabled),
            "bounds": .object([
                "x": .number((Double(frame.origin.x) * 10).rounded() / 10),
                "y": .number((Double(frame.origin.y) * 10).rounded() / 10),
                "w": .number((Double(frame.size.width) * 10).rounded() / 10),
                "h": .number((Double(frame.size.height) * 10).rounded() / 10)
            ]),
            "text": .optional(text),
            "children": .array(children)
        ])
    }
}

private final class LogSubscriptionBox: @unchecked Sendable {
    private let logger: DebugLogger
    private let lock = NSLock()
    private var token: UUID?
    private var continuation: CheckedContinuation<DebugJSONValue, Error>?
    private var pendingError: Error?

    init(logger: DebugLogger) {
        self.logger = logger
    }

    func start(
        cat: String,
        sub: String?,
        event: String?,
        sinceSeq: Int64?,
        continuation: CheckedContinuation<DebugJSONValue, Error>
    ) {
        lock.lock()
        if let err = pendingError {
            lock.unlock()
            continuation.resume(throwing: err)
            return
        }
        self.continuation = continuation
        lock.unlock()

        let id = logger.subscribe { [weak self] record in
            guard let self else { return }
            if cat != "all", record.cat.rawValue != cat { return }
            if let sinceSeq, record.seq <= sinceSeq { return }
            if let sub, record.sub != sub { return }
            if let event, record.event != event { return }
            self.resolve(.success(.object([
                "matched": .bool(true),
                "condition": .string("log_event"),
                "event": record.json
            ])))
        }
        lock.lock()
        if self.continuation == nil {
            lock.unlock()
            logger.unsubscribe(id)
        } else {
            self.token = id
            lock.unlock()
        }
    }

    func cancelWithTimeout(timeoutMs: Int, cat: String) {
        resolve(.failure(DebugAPIError.timeout("Timed out after \(timeoutMs)ms waiting for log_event \(cat)")))
    }

    func cancelWithError(_ error: Error) {
        resolve(.failure(error))
    }

    func cleanup() {
        lock.lock()
        let t = token
        token = nil
        lock.unlock()
        if let t { logger.unsubscribe(t) }
    }

    private func resolve(_ result: Result<DebugJSONValue, Error>) {
        lock.lock()
        guard let cont = continuation else {
            if case .failure(let err) = result, pendingError == nil {
                pendingError = err
            }
            lock.unlock()
            return
        }
        continuation = nil
        let t = token
        token = nil
        lock.unlock()
        if let t { logger.unsubscribe(t) }
        cont.resume(with: result)
    }
}
