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
    func sourceRow(title: String, status: String, id: String, isOn: Bool,
                   enabled: Bool, selecting: Bool, selected: Bool,
                   open: @escaping () -> Void, change: @escaping (Bool) -> Void,
                   actions: [UIMenuElement]) -> FilmSettingsRow {
        if selecting {
            return .init(title: title, detail: status, id: id, enabled: enabled,
                         selected: selected, action: open)
        }
        let name = UIButton(type: .system)
        let label = UILabel()
        label.text = title; label.textColor = UIColor(theme.settings.ink)
        label.font = .preferredFont(forTextStyle: .body)
        label.adjustsFontForContentSizeCategory = true; label.numberOfLines = 0
        label.isAccessibilityElement = false
        name.addSubview(label); label.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            label.topAnchor.constraint(equalTo: name.topAnchor),
            label.bottomAnchor.constraint(equalTo: name.bottomAnchor),
            label.leadingAnchor.constraint(equalTo: name.leadingAnchor),
            label.trailingAnchor.constraint(equalTo: name.trailingAnchor),
        ])
        name.accessibilityLabel = title
        name.accessibilityIdentifier = id + ".detail"
        name.isEnabled = enabled
        name.addAction(UIAction { _ in open() }, for: .touchUpInside)
        name.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        let summary = UILabel()
        summary.text = status; summary.textColor = UIColor(theme.settings.muted)
        summary.font = .preferredFont(forTextStyle: .footnote)
        summary.adjustsFontForContentSizeCategory = true; summary.numberOfLines = 0
        let text = UIStackView(arrangedSubviews: [name, summary])
        text.axis = .vertical; text.spacing = 2
        text.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        let toggle = UISwitch()
        toggle.isOn = isOn; toggle.isEnabled = enabled
        toggle.accessibilityLabel = title + " 启用"
        toggle.accessibilityIdentifier = id + ".enabled"
        toggle.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        toggle.setContentCompressionResistancePriority(.required, for: .horizontal)
        toggle.addAction(UIAction { [weak toggle] _ in
            if let toggle { change(toggle.isOn) }
        }, for: .valueChanged)
        let more = PageHeaderButton(image: UIImage(systemName: "ellipsis")!,
            accessibilityLabel: title + " 更多操作", menu: UIMenu(children: actions))
        more.isEnabled = enabled; more.accessibilityIdentifier = id + ".more"
        more.widthAnchor.constraint(equalToConstant: 44).isActive = true
        more.heightAnchor.constraint(equalToConstant: 44).isActive = true
        let controls = UIStackView(arrangedSubviews: [toggle, more])
        controls.spacing = 4; controls.alignment = .center
        controls.heightAnchor.constraint(greaterThanOrEqualToConstant: 44).isActive = true
        let row = UIStackView(arrangedSubviews: [text, controls])
        row.spacing = 12; row.alignment = .center
        if traitCollection.preferredContentSizeCategory.isAccessibilityCategory {
            controls.insertArrangedSubview(UIView(), at: 0)
            row.axis = .vertical; row.alignment = .fill
        }
        return .init(title: title, detail: status, id: id, enabled: enabled, action: open, content: row)
    }
    func runtimeRows() -> [FilmSettingsRow] {
        guard let message = snapshot?.runtimeError else { return [] }
        return [.init(title: "运行器不可用", id: "film.sources.runtimeError", enabled: !busy,
            action: { [weak self] in
                guard let self else { return }
                retryNotice("运行器不可用", message: message) { [weak self] in
                    guard let self, isPageActive, isPageVisible else { return }
                    perform({ [service] in try await service.start() })
                }
            })]
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
