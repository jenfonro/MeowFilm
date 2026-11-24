#if os(iOS)
import UIKit
import MeowDebug
import MeowUI

@MainActor
final class FilmCatSourcesViewController: FilmSourcePageController {
    override var usesSettingsTable: Bool { false }
    let manager = ManagementListView()
    private var selected = Set<String>()
    private var query = ""
    private var updateGeneration = UUID()
    private lazy var addButton = PageHeaderButton(image: UIImage(systemName: "plus")!,
        accessibilityLabel: "导入猫源", menu: UIMenu(children: [
            UIAction(title: "本地导入") { [weak self] _ in self?.importSource(local: true) },
            UIAction(title: "网络导入") { [weak self] _ in self?.importSource(local: false) },
        ]))

    override func viewDidLoad() {
        pageTitle = "猫源"
        super.viewDidLoad()
        view.accessibilityIdentifier = "film.cats"
        pageHeader.setTrailing([addButton])
        manager.accessibilityIdentifier = "film.cats.managementList"
        manager.search.placeholder = "搜索猫源"
        manager.onSearch = { [weak self] in self?.query = $0; self?.render() }
        manager.onSelection = { [weak self] in
            guard let self else { return }
            selected = manager.selected
            updateFooterMenu()
        }
        manager.onDelete = { [weak self] in self?.removeSelected() }
        manager.configureToolbar(showsSearch: true, buttons: [])
        view.addSubview(manager)
        manager.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            manager.topAnchor.constraint(equalTo: contentTopAnchor),
            manager.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor),
            manager.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor),
            manager.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        manager.applyTheme(theme)
        render()
    }
    override func render() {
        guard isViewLoaded else { return }
        let cats = snapshot?.cats ?? []
        selected.formIntersection(cats.map { rowID($0.installationId) })
        let normalizedQuery = query.trimmingCharacters(in: .whitespacesAndNewlines)
        let visible = normalizedQuery.isEmpty ? cats : cats.filter {
            [$0.name, $0.installationId].contains { $0.localizedCaseInsensitiveContains(normalizedQuery) }
        }
        let rows = visible.map { cat in
            ManagementListView.Row(id: rowID(cat.installationId), title: cat.name,
                detail: "\(filmSourceState(cat.state)) · \(filmSourceState(cat.updateState))",
                enabled: cat.enabled, failed: cat.state == "failed" || cat.updateState == "failed",
                controlsEnabled: !busy,
                edit: { [weak self] in self?.edit(cat) },
                open: { [weak self] in
                    guard let self, isPageActive, isPageVisible else { return }
                    navigationController?.pushViewController(FilmCatDetailController(service: service,
                        installationId: cat.installationId, themeProvider: themeProvider), animated: true)
                }, toggle: { [weak self] enabled in
                    guard let self, !busy else { return }
                    perform({ [service] in _ = try await service.enableCat(cat.installationId, enabled: enabled) })
                }, menu: UIMenu(children: [
                    UIAction(title: "更新") { [weak self] _ in self?.update([cat]) },
                    UIAction(title: "重启", attributes: cat.canRestart ? [] : .disabled) { [weak self] _ in
                        guard let self else { return }
                        perform({ [service] in _ = try await service.restartCat(cat.installationId) }, success: "已重启")
                    },
                ]))
        }
        manager.setRows(rows, total: cats.count,
                        validIDs: Set(cats.map { rowID($0.installationId) }),
                        emptyMessage: snapshot == nil ? "加载中" :
                            (cats.isEmpty ? "暂无猫源" : "没有匹配的猫源"))
        manager.setSelection(selected)
        addButton.isEnabled = !busy
        addButton.tintColor = UIColor(theme.ink)
        updateFooterMenu()
        presentRuntimeError()
    }
    private func presentRuntimeError() {
        let runtimeError = snapshot?.runtimeError
        let message = (runtimeError?.isEmpty == false) ? "运行器不可用：\(runtimeError!)" : nil
        syncErrorSource("runtime", message: message, token: runtimeError, retry: { [weak self] in
            guard let self, isPageActive else { return }
            self.perform({ [service] in try await service.start() })
        })
    }
    override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        manager.applyTheme(theme)
        addButton.tintColor = UIColor(theme.ink)
    }
    func importSource(local: Bool) {
        guard isPageActive, isPageVisible, !busy else { return }
        let editor = FilmCatEditorController(service: service, mode: .importSource(local: local),
            themeProvider: themeProvider)
        if local {
            navigationController?.pushViewController(editor, animated: true)
        } else {
            editor.isFloating = true
            let modal = FilmFloatingEditorController(editor: editor, themeProvider: themeProvider)
            let close = { [weak self, weak modal] in
                guard let self, let modal else { return }
                dismissPageModal(modal)
            }
            editor.onSaved = close
            editor.onCancel = close
            presentPageModal(modal)
        }
    }
    private func edit(_ cat: FilmCatSource) {
        guard isPageActive, isPageVisible, !busy else { return }
        navigationController?.pushViewController(FilmCatEditorController(service: service, mode: .edit(cat),
            themeProvider: themeProvider), animated: true)
    }
    private func remove(_ cats: [FilmCatSource]) {
        guard !cats.isEmpty else { return }
        presentPageConfirmation(
            title: "删除选中的 \(cats.count) 个猫源？",
            message: "关联视频源也将删除。",
            confirmTitle: "确认",
            destructive: true,
            onConfirm: { [weak self] in
                guard let self else { return }
                deleteBatch(cats, isPresent: { cat, snapshot in
                    snapshot.cats.contains { $0.installationId == cat.installationId }
                }) { [service] in try await service.deleteCat($0.installationId) }
            }
        )
    }
    func batch(_ action: Int) {
        let cats = (snapshot?.cats ?? []).filter { selected.contains(rowID($0.installationId)) }
        let generation = sourcePageGeneration
        if action == 4 {
            remove(cats)
        } else if action == 2 {
            update(cats)
        } else if action == 3 {
            let restartable = cats.filter(\.canRestart)
            var completedCount = 0
            perform({ [weak self, service] in
                while completedCount < restartable.count {
                    guard self?.canContinueSourceBatch(generation) == true else { return }
                    _ = try await service.restartCat(restartable[completedCount].installationId)
                    // Advance only after the service confirms the complete operation.
                    completedCount += 1
                }
            })
        } else {
            var completedCount = 0
            perform({ [weak self, service] in
                while completedCount < cats.count {
                    guard self?.canContinueSourceBatch(generation) == true else { return }
                    _ = try await service.enableCat(cats[completedCount].installationId, enabled: action == 0)
                    completedCount += 1
                }
            })
        }
    }
    private func rowID(_ id: String) -> String { "film.cat.\(id)" }
    private func removeSelected() {
        guard !busy else { return }
        remove((snapshot?.cats ?? []).filter { selected.contains(rowID($0.installationId)) })
    }
    private func updateFooterMenu() {
        let values = (snapshot?.cats ?? []).filter { selected.contains(rowID($0.installationId)) }
        manager.deleteButton.isEnabled = !busy && !values.isEmpty
        manager.footerMore.isEnabled = !busy && !values.isEmpty
        manager.footerMore.menu = UIMenu(children: [
            UIAction(title: "启用所选", attributes: values.isEmpty || busy ? .disabled : []) { [weak self] _ in self?.batch(0) },
            UIAction(title: "停用所选", attributes: values.isEmpty || busy ? .disabled : []) { [weak self] _ in self?.batch(1) },
            UIAction(title: "更新所选", attributes: values.isEmpty || busy ? .disabled : []) { [weak self] _ in self?.batch(2) },
            UIAction(title: "重启所选", attributes: values.isEmpty || busy ? .disabled : []) { [weak self] _ in self?.batch(3) },
        ])
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
    override func pageDidBecomeHidden() { super.pageDidBecomeHidden(); updateGeneration = UUID() }
    override func pageDidBecomeInactive() { super.pageDidBecomeInactive(); updateGeneration = UUID() }
}

@MainActor
final class FilmCatDetailController: FilmSourcePageController {
    let installationId: String
    init(service: FilmSourceService, installationId: String, themeProvider: any PageThemeProviding) {
        self.installationId = installationId; super.init(service: service, themeProvider: themeProvider)
        pageTitle = "猫源"
    }
    override func render() {
        guard isViewLoaded else { return }
        guard let cat = snapshot?.cats.first(where: { $0.installationId == installationId }) else {
            applySections([SettingsListSection(id: "state", rows: sourceStateRows(empty: true))])
            return
        }
        pageTitle = cat.name
        var sections: [SettingsListSection] = [
            SettingsListSection(id: "info", rows: [
                SettingsListRow(id: "film.cat.type", title: "类型", detail: cat.sourceType == "local" ? "本地" : "网络"),
                SettingsListRow(id: "film.cat.state", title: "状态", detail: filmSourceState(cat.state)),
                SettingsListRow(id: "film.cat.modified", title: "修改时间", detail: filmSourceDate(cat.modifiedAt)),
                SettingsListRow(id: "film.cat.updateState", title: "更新状态", detail: filmSourceState(cat.updateState)),
                SettingsListRow(
                    id: "film.cat.enabled",
                    title: "启用",
                    accessory: .toggle(isOn: cat.enabled, enabled: !busy) { [weak self] enabled in
                        guard let self else { return }
                        perform({ [service] in _ = try await service.enableCat(cat.installationId, enabled: enabled) })
                    },
                    enabled: !busy
                ),
            ]),
            SettingsListSection(id: "actions", title: "操作", rows: [
                SettingsListRow(id: "film.cat.edit", title: "编辑", enabled: !busy, action: { [weak self] in self?.editor(.edit(cat)) }),
                SettingsListRow(id: "film.cat.update", title: "更新", enabled: !busy, action: { [weak self] in
                    guard let self else { return }
                    if cat.sourceType == "local" { editor(.updateLocal(cat)) }
                    else { perform({ [service] in _ = try await service.updateCat(cat.installationId, source: nil) }, success: "更新完成") }
                }),
                SettingsListRow(id: "film.cat.restart", title: "重启", enabled: !busy && cat.canRestart, action: { [weak self] in
                    guard let self else { return }
                    perform({ [service] in _ = try await service.restartCat(cat.installationId) }, success: "已重启")
                }),
            ]),
        ]
        if let error = cat.error ?? cat.updateError {
            sections.append(SettingsListSection(id: "error", rows: [
                SettingsListRow(id: "film.cat.error", title: "查看错误", action: { [weak self] in
                    self?.enqueueNotice(title: "错误", message: error)
                }),
            ]))
        }
        if busy {
            sections.append(SettingsListSection(id: "busy", rows: sourceStateRows(empty: false)))
        }
        applySections(sections)
    }
    private func editor(_ mode: FilmCatEditorController.Mode) {
        guard let navigationController, navigationController.topViewController === self else { return }
        let editor = FilmCatEditorController(service: service, mode: mode, themeProvider: themeProvider)
        navigationController.setViewControllers(Array(navigationController.viewControllers.dropLast()) + [editor], animated: true)
    }
}

@MainActor
final class FilmFloatingEditorController: FloatingCardDialogController {
    override var showsHeaderDivider: Bool { true }
    override var cardMaxWidth: CGFloat { 480 }
    private let editor: FilmCatEditorController

    init(editor: FilmCatEditorController, themeProvider: any PageThemeProviding) {
        self.editor = editor
        super.init(title: editor.pageTitle ?? "导入猫源", themeProvider: themeProvider)
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "film.cat.networkImport.modal"
        cardView.accessibilityIdentifier = "film.cat.networkImport.card"
        scrollView.isScrollEnabled = false

        addChild(editor)
        addBodyView(editor.view)
        editor.view.translatesAutoresizingMaskIntoConstraints = false
        let preferredHeight = editor.view.heightAnchor.constraint(equalToConstant: 420)
        preferredHeight.priority = .defaultHigh
        let boundedHeight = editor.view.heightAnchor.constraint(
            lessThanOrEqualTo: scrollView.frameLayoutGuide.heightAnchor,
            constant: -36
        )
        boundedHeight.priority = UILayoutPriority(999)
        NSLayoutConstraint.activate([preferredHeight, boundedHeight])
        editor.didMove(toParent: self)
        applyTheme()
    }

    override func requestDismiss() {
        if let onCancel = editor.onCancel {
            onCancel()
        } else {
            super.requestDismiss()
        }
    }

    // MARK: - DebugDialogControllable

    override func debugDialogActions() -> [DebugDialogActionDescriptor] {
        [
            DebugDialogActionDescriptor(index: 0, label: "关闭", kind: "cancel", enabled: true),
            DebugDialogActionDescriptor(index: 1, label: editor.isBusy ? "处理中" : "保存", kind: "default", enabled: !editor.isBusy),
        ]
    }

    override func debugInvokeDialogAction(kind: String?, index: Int?, label: String?) throws -> String {
        if kind == "cancel" || index == 0 || label == "关闭" || label == "取消" {
            requestDismiss()
            return "关闭"
        }
        if kind == "confirm" || kind == "default" || index == 1 || label == "保存" {
            guard !editor.isBusy else {
                throw DebugAPIError.invalidState("FilmFloatingEditorController is busy")
            }
            editor.save()
            return "保存"
        }
        if let index {
            throw DebugAPIError.invalidArgument("FilmFloatingEditorController button index out of range: \(index)")
        }
        throw DebugAPIError.notFound("FilmFloatingEditorController action not available")
    }
}
#endif
