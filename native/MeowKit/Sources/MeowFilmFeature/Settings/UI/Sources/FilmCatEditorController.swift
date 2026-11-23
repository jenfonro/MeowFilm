#if os(iOS)
import UIKit
import UniformTypeIdentifiers
import MeowUI

@MainActor
final class FilmCatEditorController: SettingsListViewController, UIDocumentPickerDelegate {
    override var usesPageHeader: Bool { !isFloating }
    override var avoidsKeyboard: Bool { !isFloating }
    enum Mode {
        case importSource(local: Bool), edit(FilmCatSource), updateLocal(FilmCatSource)
    }
    let service: FilmSourceService
    let mode: Mode
    let name = filmTextField("名称", id: "film.cat.editor.name")
    let sourceURL = filmTextField("URL", id: "film.cat.editor.url")
    private(set) var file: FilmScriptFileSnapshot?
    private var installationId: String?
    private var targets: [FilmCatSource] = []
    private var busy = false
    var isBusy: Bool { busy }
    private var saveVisibilityGeneration = UUID()
    var onSaved: (() -> Void)?
    var onCancel: (() -> Void)?
    var isFloating = false

    init(service: FilmSourceService, mode: Mode, themeProvider: any PageThemeProviding) {
        self.service = service; self.mode = mode
        super.init(themeProvider: themeProvider)
        switch mode {
        case .importSource: pageTitle = "导入猫源"
        case .edit(let cat): pageTitle = "编辑猫源"; name.text = cat.name; sourceURL.text = cat.sourceUrl
        case .updateLocal(let cat): pageTitle = "更新猫源"; name.text = cat.name
        }
    }

    override func loadView() {
        if isFloating {
            let container = UIView()
            container.backgroundColor = .clear
            view = container
        } else {
            super.loadView()
        }
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        sourceURL.keyboardType = .URL
        if isFloating {
            table.directionalLayoutMargins = .zero
            table.contentInsetAdjustmentBehavior = .never
        }
        render()
    }

    override func pageDidBecomeActive() {
        runPageTask(key: "initialize") { [weak self, service] in
            try await service.initialize()
            let snapshot = await service.snapshot
            guard let self, !Task.isCancelled else { return }
            self.targets = snapshot.cats
            self.render()
        }
    }

    private var local: Bool {
        switch mode {
        case .importSource(let local): return local
        case .edit(let cat): return cat.sourceType == "local"
        case .updateLocal: return true
        }
    }

    private var importing: Bool { if case .importSource = mode { return true }; return false }

    func render() {
        guard isViewLoaded else { return }
        name.isEnabled = !busy; sourceURL.isEnabled = !busy
        name.applyTheme(theme); sourceURL.applyTheme(theme)
        var sections: [SettingsListSection] = []
        if case .updateLocal = mode {
            // No name field when updating local script.
        } else {
            sections.append(SettingsListSection(id: "name", title: "名称", rows: [
                SettingsListRow(id: "film.cat.editor.name.row", embeddedContent: name),
            ]))
        }
        if !local {
            sections.append(SettingsListSection(id: "url", title: "URL", rows: [
                SettingsListRow(id: "film.cat.editor.url.row", embeddedContent: sourceURL),
            ]))
        }
        if local, importing || { if case .updateLocal = mode { return true }; return false }() {
            sections.append(SettingsListSection(id: "file", title: "文件", rows: [
                SettingsListRow(
                    id: "film.cat.editor.file",
                    title: file?.fileName ?? "选择 .js 文件",
                    enabled: !busy,
                    action: { [weak self] in self?.chooseFile() }
                ),
            ]))
        }
        if importing {
            sections.append(SettingsListSection(id: "target", title: "安装", rows: [
                SettingsListRow(
                    id: "film.cat.editor.target",
                    title: installationId.flatMap { id in targets.first { $0.installationId == id }?.name } ?? "新安装",
                    enabled: !busy,
                    action: { [weak self] in self?.chooseTarget() }
                ),
            ]))
        }
        sections.append(SettingsListSection(id: "save", rows: [
            SettingsListRow(
                id: "film.cat.editor.save",
                title: busy ? "处理中" : "保存",
                enabled: !busy,
                action: { [weak self] in self?.save() }
            ),
        ]))
        applySections(sections)
    }

    func chooseFile() {
        guard canPresentPageAlert else { return }
        let picker = UIDocumentPickerViewController(forOpeningContentTypes: [UTType(filenameExtension: "js") ?? .javaScript], asCopy: true)
        picker.allowsMultipleSelection = false; picker.delegate = self
        present(picker, animated: true)
    }

    func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
        guard let url = urls.first else { return }
        readFile(url)
    }

    func documentPickerWasCancelled(_ controller: UIDocumentPickerViewController) {}

    func readFile(_ url: URL) {
        guard !busy else { return }
        busy = true; render()
        runPageTask(
            key: "readFile",
            retry: { [weak self] in self?.chooseFile() },
            onFinally: { [weak self] in
                guard let self else { return }
                self.busy = false
                self.render()
            }
        ) { [weak self] in
            let value = try await FilmScriptFileSnapshot.read(url)
            guard let self, !Task.isCancelled else { return }
            self.file = value
            if self.name.text?.isEmpty != false {
                self.name.text = URL(fileURLWithPath: value.fileName).deletingPathExtension().lastPathComponent
            }
        }
    }

    override func pageDidBecomeInactive() {
        onSaved = nil
        if busy {
            busy = false
            render()
        }
    }

    override func pageDidBecomeHidden() { saveVisibilityGeneration = UUID() }

    private func chooseTarget() {
        guard canPresentPageAlert else { return }
        var actions: [PageAlertAction] = [
            PageAlertAction(title: "新安装") { [weak self] in
                self?.installationId = nil; self?.render()
            },
        ]
        for cat in targets {
            actions.append(PageAlertAction(title: cat.name) { [weak self] in
                self?.installationId = cat.installationId; self?.render()
            })
        }
        presentPageActionSheet(title: "安装", sourceView: view, actions: actions)
    }

    func save() {
        guard !busy else { return }
        if local, importing || { if case .updateLocal = mode { return true }; return false }(), file == nil {
            enqueueError(FilmScriptFileSnapshot.FileError.scriptRequired.localizedDescription,
                         retry: { [weak self] in self?.chooseFile() })
            return
        }
        if importing, installationId != nil {
            presentPageConfirmation(title: "重新导入猫源？", confirmTitle: "确认", destructive: true) { [weak self] in
                self?.commit()
            }
        } else {
            commit()
        }
    }

    private func commit() {
        guard !busy else { return }
        view.endEditing(true); busy = true; render()
        let name = name.text ?? "", url = sourceURL.text ?? "", source = file?.source, target = installationId
        let visibility = saveVisibilityGeneration
        Task { [weak self] in
            guard let self else { return }
            defer {
                if visibility != saveVisibilityGeneration { onSaved = nil }
            }
            do {
                switch mode {
                case .importSource(let local):
                    if let target {
                        if local, let source {
                            _ = try await service.editCat(target, name: name, sourceType: "local", source: source)
                        } else {
                            _ = try await service.editCat(target, name: name, sourceType: "network", sourceURL: url)
                        }
                    } else if local, let source {
                        _ = try await service.importLocal(name: name, source: source)
                    } else {
                        _ = try await service.importNetwork(name: name, sourceURL: url)
                    }
                case .edit(let cat): _ = try await service.editCat(cat.installationId, name: name, sourceURL: local ? nil : url)
                case .updateLocal(let cat): _ = try await service.updateCat(cat.installationId, source: source)
                }
                busy = false; render()
                if visibility == saveVisibilityGeneration, let onSaved { onSaved() }
                else { enqueueNotice(title: "已保存") }
            } catch {
                busy = false; render()
                enqueueError(error.localizedDescription, retry: { [weak self] in self?.save() })
            }
        }
    }
}
#endif
