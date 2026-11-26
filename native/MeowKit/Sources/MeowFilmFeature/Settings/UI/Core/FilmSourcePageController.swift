#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class FilmSourcePageState {
    let service: FilmSourceService
    private weak var host: PageViewController?
    private let onRender: @MainActor () -> Void
    private(set) var snapshot: FilmSourceSnapshot?
    private(set) var busy = false
    private var snapshotNeedsRender = false

    init(service: FilmSourceService, host: PageViewController, onRender: @escaping @MainActor () -> Void) {
        self.service = service
        self.host = host
        self.onRender = onRender
    }

    func observeSnapshots() {
        host?.runPageTask(key: "snapshots") { [weak self, service] in
            try await service.initialize()
            for await snapshot in await service.snapshots() {
                guard !Task.isCancelled, let self, let host = self.host else { return }
                self.snapshot = snapshot
                if host.isPageVisible {
                    self.snapshotNeedsRender = false
                    self.onRender()
                } else {
                    self.snapshotNeedsRender = true
                }
            }
        }
    }

    func didBecomeVisible() {
        if snapshotNeedsRender {
            snapshotNeedsRender = false
            onRender()
        }
    }

    func markTraitDirty() {
        if host?.isPageVisible == true { onRender() } else { snapshotNeedsRender = true }
    }

    func cancelBatch() {
        host?.cancelPageTask(key: "batch")
    }

    func perform(_ operation: @escaping @MainActor () async throws -> Void,
                 success: String? = nil) {
        guard !busy else { return }
        busy = true; onRender()
        Task { [weak self] in
            guard let self else { return }
            do {
                try await operation()
                snapshot = await service.snapshot; busy = false; onRender()
                if let success { host?.enqueueNotice(title: success) }
            } catch {
                snapshot = await service.snapshot; busy = false; onRender()
                host?.enqueueError(error.localizedDescription, retry: { [weak self] in
                    self?.perform(operation, success: success)
                })
            }
        }
    }

    func runBatch(
        retry: (@MainActor () -> Void)? = nil,
        completed: (@MainActor () -> Void)? = nil,
        _ operation: @escaping @MainActor () async throws -> Void
    ) {
        guard !busy, let host, host.isPageActive, host.isPageVisible else { return }
        busy = true; onRender()
        host.runPageTask(key: "batch") { [weak self, service] in
            guard let self else { return }
            do {
                try await operation()
                self.snapshot = await service.snapshot
                self.busy = false
                self.onRender()
                if !Task.isCancelled, self.host?.isPageActive == true, self.host?.isPageVisible == true {
                    completed?()
                }
            } catch is CancellationError {
                self.snapshot = await service.snapshot
                self.busy = false
                self.onRender()
            } catch {
                self.snapshot = await service.snapshot
                self.busy = false
                self.onRender()
                if !Task.isCancelled, self.host?.isPageActive == true {
                    self.host?.enqueueError(error.localizedDescription, retry: retry)
                }
            }
        }
    }

    func deleteBatch<Item>(_ items: [Item],
                           isPresent: @escaping @MainActor (Item, FilmSourceSnapshot) -> Bool,
                           operation: @escaping @MainActor (Item) async throws -> Void) {
        var index = 0
        runBatch(retry: { [weak self] in
            self?.deleteBatch(Array(items.dropFirst(index)), isPresent: isPresent, operation: operation)
        }) { [weak self, service] in
            while index < items.count {
                try Task.checkCancellation()
                guard self?.host?.isPageActive == true, self?.host?.isPageVisible == true else { return }
                let item = items[index]
                let current = await service.snapshot
                try Task.checkCancellation()
                if isPresent(item, current) {
                    try await Task { @MainActor in try await operation(item) }.value
                }
                index += 1
            }
        }
    }
}

@MainActor
class FilmSourcePageController: SettingsListViewController {
    override var avoidsKeyboard: Bool { false }
    let service: FilmSourceService
    private lazy var state = FilmSourcePageState(service: service, host: self) { [weak self] in self?.render() }
    var snapshot: FilmSourceSnapshot? { state.snapshot }
    var busy: Bool { state.busy }

    init(service: FilmSourceService, themeProvider: any PageThemeProviding) {
        self.service = service
        super.init(themeProvider: themeProvider)
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (page: FilmSourcePageController, _: UITraitCollection) in page.state.markTraitDirty()
        }
        render()
    }

    func render() {}
    override func pageDidBecomeActive() { state.observeSnapshots() }
    override func pageDidBecomeVisible() { super.pageDidBecomeVisible(); state.didBecomeVisible() }

    func perform(_ operation: @escaping @MainActor () async throws -> Void,
                 success: String? = nil) {
        state.perform(operation, success: success)
    }

    func sourceStateRows(empty: Bool) -> [SettingsListRow] {
        if busy || snapshot == nil { return [SettingsListRow(id: "film.sources.busy", accessory: .loading(accessibilityLabel: "加载中"), enabled: false)] }
        if empty { return [SettingsListRow(id: "film.sources.empty", title: "无来源", enabled: false)] }
        return []
    }
}

@MainActor
class FilmSourceManagementController: ManagementPageViewController {
    let service: FilmSourceService
    private lazy var state = FilmSourcePageState(service: service, host: self) { [weak self] in self?.render() }
    var snapshot: FilmSourceSnapshot? { state.snapshot }
    var busy: Bool { state.busy }

    init(service: FilmSourceService, themeProvider: any PageThemeProviding) {
        self.service = service
        super.init(themeProvider: themeProvider)
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (page: FilmSourceManagementController, _: UITraitCollection) in page.state.markTraitDirty()
        }
        render()
    }

    func render() {}
    override func pageDidBecomeActive() { state.observeSnapshots() }
    override func pageDidBecomeVisible() { super.pageDidBecomeVisible(); state.didBecomeVisible() }
    override func pageDidBecomeHidden() { state.cancelBatch() }
    override func pageDidBecomeInactive() { state.cancelBatch() }

    func perform(_ operation: @escaping @MainActor () async throws -> Void,
                 success: String? = nil) {
        state.perform(operation, success: success)
    }

    func runBatch(
        retry: (@MainActor () -> Void)? = nil,
        completed: (@MainActor () -> Void)? = nil,
        _ operation: @escaping @MainActor () async throws -> Void
    ) {
        state.runBatch(retry: retry, completed: completed, operation)
    }

    func deleteBatch<Item>(_ items: [Item],
                           isPresent: @escaping @MainActor (Item, FilmSourceSnapshot) -> Bool,
                           operation: @escaping @MainActor (Item) async throws -> Void) {
        state.deleteBatch(items, isPresent: isPresent, operation: operation)
    }
}

@MainActor
func filmTextField(_ placeholder: String, id: String, value: String = "",
                   appearance: SettingsInputAppearance = .plain) -> SettingsTextField {
    let field = SettingsTextField()
    field.inputAppearance = appearance
    field.placeholder = placeholder; field.text = value
    field.font = .preferredFont(forTextStyle: .body)
    field.adjustsFontForContentSizeCategory = true
    field.borderStyle = .none
    field.autocorrectionType = .no; field.autocapitalizationType = .none
    field.smartQuotesType = .no; field.smartDashesType = .no
    field.accessibilityIdentifier = id; field.accessibilityLabel = placeholder
    return field
}

func filmSourceDate(_ timestamp: Int64?) -> String {
    guard let timestamp else { return "—" }
    return Date(timeIntervalSince1970: Double(timestamp) / 1_000).formatted(date: .abbreviated, time: .shortened)
}

func filmSourceState(_ state: String) -> String {
    switch state {
    case "ready": return "就绪"
    case "starting": return "启动中"
    case "stopped": return "已停止"
    case "disabled": return "已停用"
    case "failed", "error": return "失败"
    case "idle": return "未更新"
    case "updated": return "已更新"
    case "unchanged": return "无变化"
    case "success": return "成功"
    case "unsupported": return "不支持"
    case "empty": return "空结果"
    case "valid": return "有效"
    case "invalid": return "无效"
    case "search_error": return "搜索异常"
    case "category_error": return "分类异常"
    default: return state
    }
}
#endif
