#if os(iOS)
import UIKit
import MeowUI

@MainActor
class FilmSourcePageController: FilmSettingsListController {
    let service: FilmSourceService
    private(set) var snapshot: FilmSourceSnapshot?
    private(set) var busy = false
    private(set) var sourcePageGeneration = UUID()
    init(service: FilmSourceService, themeProvider: any PageThemeProviding) {
        self.service = service; super.init(themeProvider: themeProvider)
    }
    override func viewDidLoad() {
        super.viewDidLoad()
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (page: FilmSourcePageController, _: UITraitCollection) in page.render()
        }
        render()
    }
    override func pageDidBecomeActive() {
        runPageTask { [weak self, service] in
            for await snapshot in await service.snapshots() {
                guard !Task.isCancelled, let self else { return }
                self.snapshot = snapshot; self.render()
            }
        }
    }
    override func pageDidBecomeHidden() { sourcePageGeneration = UUID() }
    override func pageDidBecomeInactive() { sourcePageGeneration = UUID() }
    func canContinueSourceBatch(_ generation: UUID) -> Bool {
        sourcePageGeneration == generation && isPageActive && isPageVisible
    }
    override func applyTheme() {
        super.applyTheme()
        if isViewLoaded { render() }
    }
    func perform(_ operation: @escaping @MainActor () async throws -> Void,
                 success: String? = nil, completed: (() -> Void)? = nil) {
        guard !busy else { return }
        let generation = sourcePageGeneration
        busy = true; render()
        Task { [weak self] in
            guard let self else { return }
            do {
                try await operation()
                snapshot = await service.snapshot; busy = false; render()
                if canContinueSourceBatch(generation) { completed?() }
                if let success { notice(success) }
            } catch {
                snapshot = await service.snapshot; busy = false; render()
                failure(error, retry: { [weak self] in self?.perform(operation, success: success, completed: completed) })
            }
        }
    }
    func sourceStateRows(empty: Bool) -> [FilmSettingsRow] {
        if busy || snapshot == nil { return [.init(title: "加载中", id: "film.sources.busy")] }
        if empty { return [.init(title: "无来源", id: "film.sources.empty")] }
        return []
    }
    func deleteBatch<Item>(_ items: [Item],
                           isPresent: @escaping @MainActor (Item, FilmSourceSnapshot) -> Bool,
                           completed: (() -> Void)? = nil,
                           operation: @escaping @MainActor (Item) async throws -> Void) {
        let generation = sourcePageGeneration
        perform({ [weak self, service] in
            for item in items {
                guard self?.canContinueSourceBatch(generation) == true else { return }
                // A delete can commit before its following runtime refresh fails.
                // Keep that error visible, but retry from the owner's committed
                // snapshot rather than inferring rollback from the thrown error.
                let current = await service.snapshot
                guard self?.canContinueSourceBatch(generation) == true else { return }
                if isPresent(item, current) { try await operation(item) }
            }
        }, completed: { [weak self] in
            if self?.canContinueSourceBatch(generation) == true { completed?() }
        })
    }
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
