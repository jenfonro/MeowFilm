#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class FilmCatSourcesViewController: FilmSourcePageController {
    private var selecting = false
    private var selected = Set<String>()
    private var updateGeneration = UUID()
    override func viewDidLoad() { pageTitle = "猫源"; super.viewDidLoad(); view.accessibilityIdentifier = "film.cats" }
    override func render() {
        let cats = snapshot?.cats ?? []
        selected.formIntersection(cats.map(\.installationId))
        var rows = cats.map { cat in
            FilmSettingsRow(title: cat.name,
                detail: "\(cat.sourceType == "local" ? "本地" : "网络") · \(cat.enabled ? "启用" : "停用") · \(filmSourceState(cat.state))\n\(filmSourceDate(cat.modifiedAt)) · \(filmSourceState(cat.updateState))",
                id: "film.cat.\(cat.installationId)", enabled: !busy,
                selected: selecting && selected.contains(cat.installationId), disclosure: !selecting,
                action: { [weak self] in
                    guard let self else { return }
                    if selecting {
                        if !selected.insert(cat.installationId).inserted { selected.remove(cat.installationId) }
                        render()
                    } else {
                        navigationController?.pushViewController(FilmCatDetailController(service: service,
                            installationId: cat.installationId, themeProvider: themeProvider), animated: true)
                    }
                })
        }
        rows += sourceStateRows(empty: cats.isEmpty)
        sections = [.init(title: "", rows: [
            .init(title: "本地导入", id: "film.cats.import.local", enabled: !busy, action: { [weak self] in self?.importSource(local: true) }),
            .init(title: "网络导入", id: "film.cats.import.network", enabled: !busy, action: { [weak self] in self?.importSource(local: false) }),
            .init(title: selecting ? "完成选择" : "选择", id: "film.cats.select", enabled: !busy && !cats.isEmpty,
                action: { [weak self] in self?.selecting.toggle(); self?.render() }),
            .init(title: "刷新", id: "film.cats.refresh", enabled: !busy, action: { [weak self] in
                guard let self else { return }; perform({ [service] in try await service.refresh() })
            }),
        ]), .init(title: "", rows: rows)]
        if selecting {
            sections.append(.init(title: "已选 \(selected.count)", rows: [
                .init(title: "全选", id: "film.cats.selectAll", enabled: !busy, action: { [weak self] in self?.selected = Set(cats.map(\.installationId)); self?.render() }),
                .init(title: "取消选择", id: "film.cats.deselectAll", enabled: !busy, action: { [weak self] in self?.selected.removeAll(); self?.render() }),
            ] + ["启用", "停用", "更新", "重启", "删除"].enumerated().map { index, title in
                .init(title: title, id: "film.cats.batch.\(index)", enabled: !busy && !selected.isEmpty,
                      action: { [weak self] in self?.batch(index) })
            }))
        }
        let runtime = runtimeRows()
        if !runtime.isEmpty { sections.append(.init(title: "", rows: runtime)) }
        super.render()
    }
    private func importSource(local: Bool) {
        navigationController?.pushViewController(FilmCatEditorController(service: service, mode: .importSource(local: local),
            themeProvider: themeProvider), animated: true)
    }
    private func batch(_ action: Int) {
        let cats = (snapshot?.cats ?? []).filter { selected.contains($0.installationId) }
        if action == 4 {
            confirm("删除选中的 \(cats.count) 个猫源？", message: "关联视频源也将删除。") { [weak self] in
                guard let self else { return }
                deleteBatch(cats, isPresent: { cat, snapshot in
                    snapshot.cats.contains { $0.installationId == cat.installationId }
                }) { [service] in try await service.deleteCat($0.installationId) }
            }
        } else if action == 2 {
            update(cats)
        } else if action == 3 {
            let restartable = cats.filter(\.canRestart)
            var completedCount = 0
            perform({ [service] in
                while completedCount < restartable.count {
                    _ = try await service.restartCat(restartable[completedCount].installationId)
                    // Advance only after the service confirms the complete operation.
                    completedCount += 1
                }
            })
        } else {
            perform({ [service] in
                for cat in cats {
                    _ = try await service.enableCat(cat.installationId, enabled: action == 0)
                }
            })
        }
    }
    private func update(_ cats: [FilmCatSource]) {
        guard let first = cats.first else { return }
        if first.sourceType == "local" {
            let editor = FilmCatEditorController(service: service, mode: .updateLocal(first), themeProvider: themeProvider)
            editor.onSaved = { [weak self, weak editor] in
                guard let self, let editor, editor.isPageActive,
                      editor.isPageVisible, navigationController?.topViewController === editor else { return }
                navigationController?.popViewController(animated: false)
                update(Array(cats.dropFirst()))
            }
            navigationController?.pushViewController(editor, animated: true)
        } else {
            let generation = updateGeneration
            perform({ [service] in _ = try await service.updateCat(first.installationId, source: nil) },
                completed: { [weak self] in
                    guard let self, updateGeneration == generation, isPageActive, isPageVisible,
                          navigationController?.topViewController === self else { return }
                    update(Array(cats.dropFirst()))
                })
        }
    }
    override func pageDidBecomeHidden() { updateGeneration = UUID() }
    override func pageDidBecomeInactive() { updateGeneration = UUID() }
}

@MainActor
final class FilmCatDetailController: FilmSourcePageController {
    let installationId: String
    init(service: FilmSourceService, installationId: String, themeProvider: any PageThemeProviding) {
        self.installationId = installationId; super.init(service: service, themeProvider: themeProvider)
        pageTitle = "猫源"
    }
    override func render() {
        guard let cat = snapshot?.cats.first(where: { $0.installationId == installationId }) else {
            sections = [.init(title: "", rows: sourceStateRows(empty: true))]; super.render(); return
        }
        sections = [.init(title: "", rows: [
            .init(title: cat.name, id: "film.cat.name"),
            .init(title: "类型", detail: cat.sourceType == "local" ? "本地" : "网络", id: "film.cat.type"),
            .init(title: "状态", detail: filmSourceState(cat.state), id: "film.cat.state"),
            .init(title: "修改时间", detail: filmSourceDate(cat.modifiedAt), id: "film.cat.modified"),
            .init(title: "更新状态", detail: filmSourceState(cat.updateState), id: "film.cat.updateState"),
            filmSwitchRow("启用", id: "film.cat.enabled", isOn: cat.enabled, enabled: !busy, ink: UIColor(theme.ink)) { [weak self] enabled in
                guard let self else { return }
                perform({ [service] in _ = try await service.enableCat(cat.installationId, enabled: enabled) })
            },
        ]), .init(title: "操作", rows: [
            .init(title: "编辑", id: "film.cat.edit", enabled: !busy, action: { [weak self] in self?.editor(.edit(cat)) }),
            .init(title: "更新", id: "film.cat.update", enabled: !busy, action: { [weak self] in
                guard let self else { return }
                if cat.sourceType == "local" { editor(.updateLocal(cat)) }
                else { perform({ [service] in _ = try await service.updateCat(cat.installationId, source: nil) }, success: "更新完成") }
            }),
            .init(title: "重启", id: "film.cat.restart", enabled: !busy && cat.canRestart, action: { [weak self] in
                guard let self else { return }; perform({ [service] in _ = try await service.restartCat(cat.installationId) }, success: "已重启")
            }),
            .init(title: "删除", id: "film.cat.delete", enabled: !busy, action: { [weak self] in
                self?.confirm("删除猫源？", message: "关联视频源也将删除。") { [weak self] in
                    guard let self else { return }
                    deleteBatch([cat], isPresent: { cat, snapshot in
                        snapshot.cats.contains { $0.installationId == cat.installationId }
                    }, completed: { [weak self] in
                        guard let self, isPageActive, isPageVisible,
                              navigationController?.topViewController === self else { return }
                        navigationController?.popViewController(animated: true)
                    }) { [service] in try await service.deleteCat($0.installationId) }
                }
            }),
        ])]
        if let error = cat.error ?? cat.updateError {
            sections.append(.init(title: "", rows: [.init(title: "查看错误", id: "film.cat.error", action: { [weak self] in self?.notice("错误", message: error) })]))
        }
        if busy { sections.append(.init(title: "", rows: sourceStateRows(empty: false))) }
        super.render()
    }
    private func editor(_ mode: FilmCatEditorController.Mode) {
        navigationController?.pushViewController(FilmCatEditorController(service: service, mode: mode,
            themeProvider: themeProvider), animated: true)
    }
}
#endif
