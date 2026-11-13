#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class FilmCatSourcesViewController: FilmSourcePageController {
    override var usesSettingsTable: Bool { false }
    let manager = ManagementListView()
    private var selected = Set<String>()
    private var query = ""
    private var updateGeneration = UUID()
    private lazy var addButton: UIButton = {
        let button = ManagementListView.icon("plus", label: "导入猫源")
        button.menu = UIMenu(children: [
            UIAction(title: "本地导入") { [weak self] _ in self?.importSource(local: true) },
            UIAction(title: "网络导入") { [weak self] _ in self?.importSource(local: false) },
        ])
        button.showsMenuAsPrimaryAction = true
        return button
    }()
    private lazy var refreshButton = ManagementListView.icon("arrow.clockwise", label: "刷新猫源") { [weak self] in
        guard let self else { return }; perform({ [service] in try await service.refresh() })
    }

    override func viewDidLoad() {
        pageTitle = "猫源"
        super.viewDidLoad()
        view.accessibilityIdentifier = "film.cats"
        manager.accessibilityIdentifier = "film.cats.managementList"
        manager.search.placeholder = "搜索猫源"
        manager.onSearch = { [weak self] in self?.query = $0; self?.render() }
        manager.onSelection = { [weak self] in
            guard let self else { return }
            selected = manager.selected
            updateFooterMenu()
        }
        manager.onDelete = { [weak self] in self?.removeSelected() }
        manager.configureToolbar(showsSearch: true, buttons: [refreshButton, addButton])
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
        var allRows = rows
        if let runtimeError = snapshot?.runtimeError {
            allRows.append(.init(id: "film.sources.runtimeError", title: "运行器不可用",
                detail: runtimeError, enabled: false, controlsEnabled: !busy,
                showsSelection: false, showsToggle: false,
                open: { [weak self] in
                    guard let self else { return }
                    retryNotice("运行器不可用", message: runtimeError) { [weak self] in
                        guard let self, isPageActive, isPageVisible else { return }
                        perform({ [service] in try await service.start() })
                    }
                }, toggle: { _ in }, menu: UIMenu(children: [])))
        }
        manager.setRows(allRows, total: cats.count,
                        validIDs: Set(cats.map { rowID($0.installationId) }),
                        emptyMessage: snapshot == nil ? "加载中" :
                            (cats.isEmpty ? "暂无猫源" : "没有匹配的猫源"))
        manager.setSelection(selected)
        refreshButton.isEnabled = !busy
        addButton.isEnabled = !busy
        updateFooterMenu()
    }
    override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        manager.applyTheme(theme)
        render()
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
            modal.modalPresentationStyle = .overFullScreen
            modal.modalTransitionStyle = .crossDissolve
            present(modal, animated: true)
        }
    }
    private func edit(_ cat: FilmCatSource) {
        guard isPageActive, isPageVisible, !busy else { return }
        navigationController?.pushViewController(FilmCatEditorController(service: service, mode: .edit(cat),
            themeProvider: themeProvider), animated: true)
    }
    private func remove(_ cats: [FilmCatSource]) {
        guard !cats.isEmpty else { return }
        confirm("删除选中的 \(cats.count) 个猫源？", message: "关联视频源也将删除。") { [weak self] in
            guard let self else { return }
            deleteBatch(cats, isPresent: { cat, snapshot in
                snapshot.cats.contains { $0.installationId == cat.installationId }
            }) { [service] in try await service.deleteCat($0.installationId) }
        }
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
        guard let cat = snapshot?.cats.first(where: { $0.installationId == installationId }) else {
            sections = [.init(title: "", rows: sourceStateRows(empty: true))]; super.render(); return
        }
        pageTitle = cat.name
        sections = [.init(title: "", rows: [
            .init(title: "类型", detail: cat.sourceType == "local" ? "本地" : "网络", id: "film.cat.type"),
            .init(title: "状态", detail: filmSourceState(cat.state), id: "film.cat.state"),
            .init(title: "修改时间", detail: filmSourceDate(cat.modifiedAt), id: "film.cat.modified"),
            .init(title: "更新状态", detail: filmSourceState(cat.updateState), id: "film.cat.updateState"),
            filmSwitchRow("启用", id: "film.cat.enabled", isOn: cat.enabled, enabled: !busy) { [weak self] enabled in
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
        ])]
        if let error = cat.error ?? cat.updateError {
            sections.append(.init(title: "", rows: [.init(title: "查看错误", id: "film.cat.error", action: { [weak self] in self?.notice("错误", message: error) })]))
        }
        if busy { sections.append(.init(title: "", rows: sourceStateRows(empty: false))) }
        super.render()
    }
    private func editor(_ mode: FilmCatEditorController.Mode) {
        guard let navigationController, navigationController.topViewController === self else { return }
        let editor = FilmCatEditorController(service: service, mode: mode, themeProvider: themeProvider)
        navigationController.setViewControllers(Array(navigationController.viewControllers.dropLast()) + [editor], animated: true)
    }
}

@MainActor
final class FilmFloatingEditorController: PageViewController {
    override var usesPageHeader: Bool { false }
    private let editor: FilmCatEditorController
    private weak var card: UIView?

    init(editor: FilmCatEditorController, themeProvider: any PageThemeProviding) {
        self.editor = editor
        super.init(themeProvider: themeProvider)
    }

    override func loadView() {
        let root = UIView()
        view = root
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "film.cat.networkImport.modal"

        let card = UIView()
        card.backgroundColor = UIColor(theme.settings.surface)
        card.layer.cornerRadius = 24
        card.layer.borderColor = UIColor(theme.settings.border).cgColor
        card.layer.borderWidth = 0.5
        card.clipsToBounds = true
        card.accessibilityIdentifier = "film.cat.networkImport.card"
        self.card = card
        view.addSubview(card)
        card.translatesAutoresizingMaskIntoConstraints = false
        let preferredWidth = card.widthAnchor.constraint(equalTo: view.safeAreaLayoutGuide.widthAnchor, constant: -32)
        preferredWidth.priority = .defaultHigh
        let preferredHeight = card.heightAnchor.constraint(equalToConstant: 520)
        preferredHeight.priority = .defaultHigh
        let center = card.centerYAnchor.constraint(equalTo: view.safeAreaLayoutGuide.centerYAnchor)
        center.priority = .defaultHigh
        NSLayoutConstraint.activate([
            preferredWidth,
            card.widthAnchor.constraint(lessThanOrEqualTo: view.safeAreaLayoutGuide.widthAnchor, constant: -32),
            card.widthAnchor.constraint(lessThanOrEqualToConstant: 560),
            preferredHeight,
            card.centerXAnchor.constraint(equalTo: view.centerXAnchor),
            center,
            card.topAnchor.constraint(greaterThanOrEqualTo: view.safeAreaLayoutGuide.topAnchor, constant: 24),
            card.bottomAnchor.constraint(lessThanOrEqualTo: view.keyboardLayoutGuide.topAnchor, constant: -16),
        ])

        addChild(editor)
        card.addSubview(editor.view)
        editor.view.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            editor.view.topAnchor.constraint(equalTo: card.topAnchor),
            editor.view.leadingAnchor.constraint(equalTo: card.leadingAnchor),
            editor.view.trailingAnchor.constraint(equalTo: card.trailingAnchor),
            editor.view.bottomAnchor.constraint(equalTo: card.bottomAnchor),
        ])
        editor.didMove(toParent: self)
    }

    override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        view.backgroundColor = UIColor.black.withAlphaComponent(0.28)
        card?.backgroundColor = UIColor(theme.settings.surface)
        card?.layer.borderColor = UIColor(theme.settings.border).cgColor
        editor.applyTheme()
    }
}
#endif
