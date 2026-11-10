#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class FilmPlaybackLibraryController: FilmSettingsListController {
    enum Area { case history, manual, blocks }
    private let owner: FilmPlaybackSettingsModel
    private let area: Area
    private var task: Task<Void, Never>?

    init(owner: FilmPlaybackSettingsModel, area: Area, themeProvider: any PageThemeProviding) {
        self.owner = owner; self.area = area
        super.init(themeProvider: themeProvider)
        switch area {
        case .history: pageTitle = "播放历史"
        case .manual: pageTitle = "人工匹配"
        case .blocks: pageTitle = "片源屏蔽"
        }
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        table.accessibilityIdentifier = "film.playback-library.list"
        if area == .manual {
            pageHeader.setTrailing([PageHeaderTextButton(title: "添加") { [weak self] in self?.editManual(nil) }])
        }
        observe(owner) { [weak self] in self?.update() }
    }

    override func pageDidBecomeVisible() { run { [owner] in try await owner.loadLibrary() } }

    private func update() {
        guard let library = owner.library else {
            sections = [.init(title: "", rows: [.init(title: "加载中", id: "film.playback-library.loading")])]
            render(); return
        }
        let rows: [FilmSettingsRow]
        switch area {
        case .history:
            rows = library.history.map { item in
                .init(title: item.title, detail: time(item.position) + (item.duration.map { " / " + time($0) } ?? ""),
                      id: "film.history.\(item.targetID)", enabled: !owner.busy, disclosure: true,
                      action: { [weak self] in
                          guard let self else { return }
                          confirm("删除播放历史", message: item.title) { [weak self, owner] in
                              self?.run { try await owner.removeHistory(targetID: item.targetID) }
                          }
                      })
            }
        case .manual:
            rows = library.manual.map { item in
                .init(title: item.name, detail: "\(item.enabled ? "已启用" : "已停用") · 失败 \(item.failureCount)",
                      id: "film.manual.\(item.id)", enabled: !owner.busy, disclosure: true,
                      action: { [weak self] in self?.editManual(item) })
            }
        case .blocks:
            rows = library.blocks.enumerated().map { index, item in
                .init(title: item.targetID, detail: "\(item.sourceKey) · \(item.detailID.display) · \(item.lineID.display)",
                      id: "film.blocks.\(index)", enabled: !owner.busy, disclosure: true,
                      action: { [weak self] in
                          guard let self else { return }
                          confirm("解除片源屏蔽", message: item.targetID) { [weak self, owner] in
                              self?.run { try await owner.removeBlock(item) }
                          }
                      })
            }
        }
        var sectionRows = rows
        if sectionRows.isEmpty { sectionRows.append(.init(title: "暂无记录", id: "film.playback-library.empty")) }
        if !rows.isEmpty && area != .manual {
            sectionRows.append(.init(title: area == .history ? "清空历史" : "解除全部屏蔽",
                                     id: "film.playback-library.clear", enabled: !owner.busy,
                                     action: { [weak self] in self?.clear() }))
        }
        sections = [.init(title: "", rows: sectionRows)]
        render()
    }

    private func editManual(_ item: FilmManualMatch?) {
        let page = FilmManualMatchEditor(owner: owner, item: item, themeProvider: themeProvider)
        navigationController?.pushViewController(page, animated: true)
    }

    private func clear() {
        confirm(area == .history ? "清空播放历史" : "解除全部片源屏蔽") { [weak self] in
            guard let self else { return }
            run { [owner, area] in
                if area == .history { try await owner.removeHistory() } else { try await owner.removeBlock() }
            }
        }
    }

    private func run(_ action: @escaping @MainActor () async throws -> Void) {
        guard task == nil else { return }
        task = Task { [weak self] in
            guard let self else { return }
            defer { task = nil }
            do { try await action(); update() }
            catch { failure(error, retry: { [weak self] in self?.run(action) }) }
        }
    }

    private func time(_ seconds: Double) -> String {
        let seconds = Int(seconds)
        return String(format: "%d:%02d", seconds / 60, seconds % 60)
    }
}
#endif
