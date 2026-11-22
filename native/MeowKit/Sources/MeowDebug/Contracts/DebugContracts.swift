import Foundation
#if os(iOS)
import UIKit
#endif

public enum DebugAPIErrorCode: String, Sendable {
    case invalidArgument = "INVALID_ARGUMENT"
    case notFound = "NOT_FOUND"
    case unsupportedOnPlatform = "UNSUPPORTED_ON_PLATFORM"
    case invalidState = "INVALID_STATE"
    case timeout = "TIMEOUT"
    case runtimeUnavailable = "RUNTIME_UNAVAILABLE"
    case internalError = "INTERNAL_ERROR"

    public var httpStatus: Int {
        switch self {
        case .invalidArgument: return 400
        case .notFound: return 404
        case .unsupportedOnPlatform: return 501
        case .invalidState: return 409
        case .timeout: return 504
        case .runtimeUnavailable: return 503
        case .internalError: return 500
        }
    }
}

public struct DebugAPIError: Error, Sendable {
    public let code: DebugAPIErrorCode
    public let message: String
    public let details: DebugJSONValue?

    public init(_ code: DebugAPIErrorCode, _ message: String, details: DebugJSONValue? = nil) {
        self.code = code
        self.message = message
        self.details = details
    }

    public static func invalidArgument(_ message: String, details: DebugJSONValue? = nil) -> Self {
        .init(.invalidArgument, message, details: details)
    }
    public static func notFound(_ message: String, details: DebugJSONValue? = nil) -> Self {
        .init(.notFound, message, details: details)
    }
    public static func unsupported(_ message: String, details: DebugJSONValue? = nil) -> Self {
        .init(.unsupportedOnPlatform, message, details: details)
    }
    public static func invalidState(_ message: String, details: DebugJSONValue? = nil) -> Self {
        .init(.invalidState, message, details: details)
    }
    public static func timeout(_ message: String, details: DebugJSONValue? = nil) -> Self {
        .init(.timeout, message, details: details)
    }
    public static func runtimeUnavailable(_ message: String, details: DebugJSONValue? = nil) -> Self {
        .init(.runtimeUnavailable, message, details: details)
    }
    public static func internalError(_ message: String, details: DebugJSONValue? = nil) -> Self {
        .init(.internalError, message, details: details)
    }
}

public struct DebugTabItem: Sendable {
    public let group: String
    public let index: Int
    public let id: String
    public let title: String
    public let selected: Bool
    public let enabled: Bool

    public init(group: String, index: Int, id: String, title: String, selected: Bool, enabled: Bool = true) {
        self.group = group
        self.index = index
        self.id = id
        self.title = title
        self.selected = selected
        self.enabled = enabled
    }

    public func toJSON() -> DebugJSONValue {
        .object([
            "group": .string(group),
            "index": .int(index),
            "id": .string(id),
            "title": .string(title),
            "selected": .bool(selected),
            "enabled": .bool(enabled)
        ])
    }
}

public struct DebugFilterItem: Sendable {
    public let group: String
    public let index: Int
    public let id: String
    public let title: String
    public let selected: Bool

    public init(group: String, index: Int, id: String, title: String, selected: Bool) {
        self.group = group
        self.index = index
        self.id = id
        self.title = title
        self.selected = selected
    }

    public func toJSON() -> DebugJSONValue {
        .object([
            "group": .string(group),
            "index": .int(index),
            "id": .string(id),
            "title": .string(title),
            "selected": .bool(selected)
        ])
    }
}

public struct DebugContentItem: Sendable {
    public let index: Int
    public let id: String
    public let kind: String
    public let title: String
    public let subtitle: String?
    public let badge: String?
    public let coverUrl: String?
    public let actions: [String]
    public let meta: [String: DebugJSONValue]

    public init(
        index: Int,
        id: String,
        kind: String,
        title: String,
        subtitle: String? = nil,
        badge: String? = nil,
        coverUrl: String? = nil,
        actions: [String] = ["open"],
        meta: [String: DebugJSONValue] = [:]
    ) {
        self.index = index
        self.id = id
        self.kind = kind
        self.title = title
        self.subtitle = subtitle
        self.badge = badge
        self.coverUrl = coverUrl
        self.actions = actions
        self.meta = meta
    }

    public func toJSON() -> DebugJSONValue {
        .object([
            "index": .int(index),
            "id": .string(id),
            "kind": .string(kind),
            "title": .string(title),
            "subtitle": .optional(subtitle),
            "badge": .optional(badge),
            "coverUrl": .optional(coverUrl),
            "actions": .array(actions.map(DebugJSONValue.string)),
            "meta": .object(meta)
        ])
    }
}

public struct DebugContentSection: Sendable {
    public let index: Int
    public let id: String
    public let title: String
    public let kind: String
    public let layout: String
    public let hasMore: Bool
    public let itemCount: Int
    public let items: [DebugContentItem]

    public init(
        index: Int,
        id: String,
        title: String,
        kind: String,
        layout: String,
        hasMore: Bool = false,
        itemCount: Int? = nil,
        items: [DebugContentItem]
    ) {
        self.index = index
        self.id = id
        self.title = title
        self.kind = kind
        self.layout = layout
        self.hasMore = hasMore
        self.itemCount = itemCount ?? items.count
        self.items = items
    }

    public func toJSON() -> DebugJSONValue {
        .object([
            "index": .int(index),
            "id": .string(id),
            "title": .string(title),
            "kind": .string(kind),
            "layout": .string(layout),
            "hasMore": .bool(hasMore),
            "itemCount": .int(itemCount),
            "items": .array(items.map { $0.toJSON() })
        ])
    }
}

public struct DebugContentRow: Sendable {
    public let index: Int
    public let id: String
    public let title: String
    public let subtitle: String?
    public let selected: Bool
    public let state: String?
    public let actions: [String]
    public let meta: [String: DebugJSONValue]

    public init(
        index: Int,
        id: String,
        title: String,
        subtitle: String? = nil,
        selected: Bool = false,
        state: String? = nil,
        actions: [String] = ["open"],
        meta: [String: DebugJSONValue] = [:]
    ) {
        self.index = index
        self.id = id
        self.title = title
        self.subtitle = subtitle
        self.selected = selected
        self.state = state
        self.actions = actions
        self.meta = meta
    }

    public func toJSON() -> DebugJSONValue {
        .object([
            "index": .int(index),
            "id": .string(id),
            "title": .string(title),
            "subtitle": .optional(subtitle),
            "selected": .bool(selected),
            "state": .optional(state),
            "actions": .array(actions.map(DebugJSONValue.string)),
            "meta": .object(meta)
        ])
    }
}

public struct DebugContentSnapshot: Sendable {
    public var target: String
    public var kind: String
    public var loading: Bool
    public var error: String?
    public var tabs: [DebugTabItem]
    public var filters: [DebugFilterItem]
    public var sections: [DebugContentSection]
    public var rows: [DebugContentRow]
    public var hasMore: Bool
    public var detail: DebugJSONValue?
    public var player: DebugJSONValue?
    public var reader: DebugJSONValue?
    public var settings: DebugJSONValue?
    public var extra: [String: DebugJSONValue]

    public init(
        target: String,
        kind: String,
        loading: Bool = false,
        error: String? = nil,
        tabs: [DebugTabItem] = [],
        filters: [DebugFilterItem] = [],
        sections: [DebugContentSection] = [],
        rows: [DebugContentRow] = [],
        hasMore: Bool = false,
        detail: DebugJSONValue? = nil,
        player: DebugJSONValue? = nil,
        reader: DebugJSONValue? = nil,
        settings: DebugJSONValue? = nil,
        extra: [String: DebugJSONValue] = [:]
    ) {
        self.target = target
        self.kind = kind
        self.loading = loading
        self.error = error
        self.tabs = tabs
        self.filters = filters
        self.sections = sections
        self.rows = rows
        self.hasMore = hasMore
        self.detail = detail
        self.player = player
        self.reader = reader
        self.settings = settings
        self.extra = extra
    }

    public func toJSON() -> DebugJSONValue {
        var fields: [String: DebugJSONValue] = [
            "target": .string(target),
            "kind": .string(kind),
            "loading": .bool(loading),
            "error": .optional(error),
            "tabs": .array(tabs.map { $0.toJSON() }),
            "filters": .array(filters.map { $0.toJSON() }),
            "sections": .array(sections.map { $0.toJSON() }),
            "rows": .array(rows.map { $0.toJSON() }),
            "hasMore": .bool(hasMore),
            "detail": detail ?? .null,
            "player": player ?? .null,
            "reader": reader ?? .null,
            "settings": settings ?? .null
        ]
        for (k, v) in extra where fields[k] == nil {
            fields[k] = v
        }
        return .object(fields)
    }
}

public struct DebugPageStatus: Sendable {
    public let target: String
    public let moduleId: String
    public let route: String
    public let title: String
    public let isBusy: Bool
    public let statusText: String?
    public let isError: Bool
    public let errorText: String?
    public let settingsDomain: String?
    public let settingsSection: String?

    public init(
        target: String,
        moduleId: String,
        route: String,
        title: String,
        isBusy: Bool = false,
        statusText: String? = nil,
        isError: Bool = false,
        errorText: String? = nil,
        settingsDomain: String? = nil,
        settingsSection: String? = nil
    ) {
        self.target = target
        self.moduleId = moduleId
        self.route = route
        self.title = title
        self.isBusy = isBusy
        self.statusText = statusText
        self.isError = isError
        self.errorText = errorText
        self.settingsDomain = settingsDomain
        self.settingsSection = settingsSection
    }
}

public struct DebugActionRequest: Sendable {
    public let action: String
    public let params: [String: DebugJSONValue]

    public init(action: String, params: [String: DebugJSONValue]) {
        self.action = action
        self.params = params
    }
}

@MainActor
public protocol DebugPageInspectable: AnyObject {
    var debugPageStatus: DebugPageStatus { get }
    func debugContent(maxItems: Int, includeRaw: Bool) -> DebugContentSnapshot
}

@MainActor
public protocol DebugPageActionable: AnyObject {
    func debugPerformAction(_ request: DebugActionRequest) async throws -> DebugJSONValue
}

@MainActor
public protocol DebugPageRefreshable: AnyObject {
    func debugRefresh()
}

#if os(iOS)
/// Safe public registry for `UIAlertController` action callbacks so `dialog.action`
/// never touches private KVC keys on `UIAlertAction`.
@MainActor
public final class DebugAlertActionRegistry {
    public struct RegisteredAction {
        public let index: Int
        public let label: String
        public let kind: String
        public let handler: @MainActor () -> Void

        public init(index: Int, label: String, kind: String, handler: @escaping @MainActor () -> Void) {
            self.index = index
            self.label = label
            self.kind = kind
            self.handler = handler
        }
    }

    public static let shared = DebugAlertActionRegistry()
    private var entries: [ObjectIdentifier: (weakAlert: WeakAlertBox, actions: [RegisteredAction])] = [:]

    private final class WeakAlertBox {
        weak var value: UIAlertController?
        init(_ value: UIAlertController) { self.value = value }
    }

    public func register(alert: UIAlertController, actions: [RegisteredAction]) {
        guard DebugRuntime.isEnabled else { return }
        prune()
        entries[ObjectIdentifier(alert)] = (WeakAlertBox(alert), actions)
    }

    public func actions(for alert: UIAlertController) -> [RegisteredAction]? {
        prune()
        return entries[ObjectIdentifier(alert)]?.actions
    }

    private func prune() {
        entries = entries.filter { $0.value.weakAlert.value != nil }
    }
}

public struct DebugDialogActionDescriptor: Sendable {
    public let index: Int
    public let label: String
    public let kind: String
    public let enabled: Bool

    public init(index: Int, label: String, kind: String, enabled: Bool = true) {
        self.index = index
        self.label = label
        self.kind = kind
        self.enabled = enabled
    }
}

public struct DebugDialogSnapshot: Sendable {
    public let typeName: String
    public let title: String
    public let message: String?
    public let errorText: String?
    public let actions: [DebugDialogActionDescriptor]

    public init(typeName: String, title: String, message: String? = nil,
                errorText: String? = nil, actions: [DebugDialogActionDescriptor]) {
        self.typeName = typeName
        self.title = title
        self.message = message
        self.errorText = errorText
        self.actions = actions
    }
}

@MainActor
public protocol DebugDialogControllable: AnyObject {
    func debugDialogSnapshot() -> DebugDialogSnapshot
    func debugInvokeDialogAction(kind: String?, index: Int?, label: String?) throws -> String
}
#endif

/// Lightweight debug-only UI render/cache counters (no-op when DebugRuntime is disabled).
public final class DebugUIMetrics: @unchecked Sendable {
    public static let shared = DebugUIMetrics()
    private let lock = NSLock()
    private var counters: [String: Int64] = [:]

    public func increment(_ key: String, by delta: Int64 = 1) {
        guard DebugRuntime.isEnabled else { return }
        lock.lock()
        counters[key, default: 0] += delta
        lock.unlock()
    }

    public func snapshot() -> [String: DebugJSONValue] {
        lock.lock()
        let copy = counters
        lock.unlock()
        return copy.mapValues { .integer($0) }
    }
}

public struct DebugBinaryResponse: Sendable {
    public let data: Data
    public let contentType: String
    public let headers: [String: String]

    public init(data: Data, contentType: String, headers: [String: String] = [:]) {
        self.data = data
        self.contentType = contentType
        self.headers = headers
    }
}

public protocol DebugControlDelegate: AnyObject, Sendable {
    func debugHealth() async throws -> DebugJSONValue
    func debugCapabilities() async throws -> DebugJSONValue
    func debugUpdateConfig(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue

    func debugNavOpen(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue
    func debugNavBack() async throws -> DebugJSONValue
    func debugNavForward() async throws -> DebugJSONValue
    func debugNavRefresh() async throws -> DebugJSONValue

    func debugUIState() async throws -> DebugJSONValue
    func debugUIContent(maxItems: Int, includeRaw: Bool) async throws -> DebugJSONValue
    func debugUITree(maxDepth: Int, visibleOnly: Bool) async throws -> DebugJSONValue
    func debugUIScreenshot(save: Bool) async throws -> DebugBinaryResponse
    func debugUIAction(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue
    func debugUIWait(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue

    func debugPlaybackState() async throws -> DebugJSONValue
    func debugPlaybackCommand(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue
    func debugNovelCommand(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue

    func debugRuntimeInvoke(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue
    func debugStorageQuery(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue

    func debugDiagSummary() async throws -> DebugJSONValue
    func debugDiagThreads() async throws -> DebugJSONValue
    func debugDiagGC() async throws -> DebugJSONValue
    func debugDiagDump(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue
    func debugDiagNodeHeapSnapshot() async throws -> DebugJSONValue
    func debugDiagNodeCPUProfile(_ body: [String: DebugJSONValue]) async throws -> DebugJSONValue
    func debugDiagExceptions(limit: Int, kind: String) async throws -> DebugJSONValue
}
