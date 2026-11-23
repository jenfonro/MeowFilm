#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class FilmPlaybackLibraryController: SettingsListViewController {
    override var avoidsKeyboard: Bool { false }
    private let owner: FilmPlaybackSettingsModel
    private let area: FilmPlaybackLibrary.Area

    init(owner: FilmPlaybackSettingsModel, area: FilmPlaybackLibrary.Area, themeProvider: any PageThemeProviding) {
        self.owner = owner; self.area = area
        super.init(themeProvider: themeProvider)
        switch area {
        case .manual: pageTitle = "Manual 匹配源管理"
        case .blocks: pageTitle = "匹配禁用管理"
        }
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        table.accessibilityIdentifier = "film.playback-library.list"
        if area == .manual {
            pageHeader.setTrailing([PageHeaderTextButton(title: "添加") { [weak self] in self?.editManual(nil) }])
        }
        observe(owner, whenVisible: true) { [weak self] in self?.update() }
        update()
    }

    override func pageDidBecomeVisible() {
        run { [owner, area] in try await owner.loadLibrary(area: area) }
    }

    private func update() {
        guard isViewLoaded else { return }
        guard let library = owner.library, library.area == area else {
            applySections([SettingsListSection(id: "loading", rows: [
                SettingsListRow(id: "film.playback-library.loading", title: "加载中", enabled: false),
            ])])
            return
        }
        let rows: [SettingsListRow]
        switch area {
        case .manual:
            rows = library.manual.map { item in
                SettingsListRow(
                    id: "film.manual.\(item.id)",
                    title: item.name,
                    detail: "\(item.enabled ? "已启用" : "已停用") · 失败 \(item.failureCount)",
                    accessory: .disclosure,
                    enabled: !owner.busy,
                    action: { [weak self] in self?.editManual(item) }
                )
            }
        case .blocks:
            rows = library.blocks.enumerated().map { index, item in
                SettingsListRow(
                    id: "film.blocks.\(index)",
                    title: item.targetID,
                    detail: "\(item.sourceKey) · \(item.detailID.display) · \(item.lineID.display)",
                    accessory: .disclosure,
                    enabled: !owner.busy,
                    action: { [weak self] in
                        self?.presentPageConfirmation(
                            title: "解除片源屏蔽",
                            message: item.targetID,
                            confirmTitle: "确认",
                            destructive: true,
                            onConfirm: { [weak self] in
                                guard let self else { return }
                                self.run { try await self.owner.removeBlock(item) }
                            }
                        )
                    }
                )
            }
        }
        var sectionRows = rows
        if sectionRows.isEmpty {
            sectionRows.append(SettingsListRow(id: "film.playback-library.empty", title: "暂无记录", enabled: false))
        }
        if !rows.isEmpty && area != .manual {
            sectionRows.append(SettingsListRow(
                id: "film.playback-library.clear",
                title: "解除全部屏蔽",
                enabled: !owner.busy,
                action: { [weak self] in self?.clear() }
            ))
        }
        if area == .manual, !library.manual.isEmpty {
            let targets = library.manual.reduce(into: [String]()) { if !$0.contains($1.targetID) { $0.append($1.targetID) } }
            let sections = targets.map { target in
                SettingsListSection(
                    id: "manual.\(target)",
                    title: library.manual.first(where: { $0.targetID == target })?.value["title"]?.filmText ?? target,
                    rows: zip(library.manual, rows).filter { $0.0.targetID == target }.map { $0.1 }
                )
            }
            applySections(sections)
        } else {
            applySections([SettingsListSection(id: "main", rows: sectionRows)])
        }
    }

    private func editManual(_ item: FilmManualMatch?) {
        let page = FilmManualMatchEditor(owner: owner, item: item, themeProvider: themeProvider)
        navigationController?.pushViewController(page, animated: true)
    }

    private func clear() {
        presentPageConfirmation(
            title: "解除全部片源屏蔽",
            confirmTitle: "确认",
            destructive: true,
            onConfirm: { [weak self] in
                guard let self else { return }
                run { [owner] in try await owner.removeBlock() }
            }
        )
    }

    private func run(_ action: @escaping @MainActor () async throws -> Void) {
        guard !hasActivePageTask(key: "run") else { return }
        runPageTask(key: "run", retry: { [weak self] in self?.run(action) }) { [weak self] in
            try await action()
            self?.update()
        }
    }
}
#endif
