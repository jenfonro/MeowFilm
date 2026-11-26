#if os(iOS)
import UIKit
import UniformTypeIdentifiers
import MeowUI

@MainActor
final class FilmCatEditorController: SettingsListViewController, UIDocumentPickerDelegate {
    enum Mode {
        case importLocal, edit(FilmCatSource), updateLocal(FilmCatSource)
    }
    let service: FilmSourceService
    let mode: Mode
    let name = filmTextField("名称", id: "film.cat.editor.name")
    let sourceURL = filmTextField("URL", id: "film.cat.editor.url")
    private(set) var file: FilmScriptFileSnapshot?
    private var installationId: String?
    private var targets: [FilmCatSource] = []
    private var busy = false
    var onSaved: (() -> Void)?

    init(service: FilmSourceService, mode: Mode, themeProvider: any PageThemeProviding) {
        self.service = service; self.mode = mode
        super.init(themeProvider: themeProvider)
        switch mode {
        case .importLocal: pageTitle = "导入猫源"
        case .edit(let cat): pageTitle = "编辑猫源"; name.text = cat.name; sourceURL.text = cat.sourceUrl
        case .updateLocal(let cat): pageTitle = "更新猫源"; name.text = cat.name
        }
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        sourceURL.keyboardType = .URL
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
        case .importLocal, .updateLocal: return true
        case .edit(let cat): return cat.sourceType == "local"
        }
    }

    private var importing: Bool { if case .importLocal = mode { return true }; return false }
    private var isUpdatingLocal: Bool { if case .updateLocal = mode { return true }; return false }

    func render() {
        guard isViewLoaded else { return }
        name.isEnabled = !busy; sourceURL.isEnabled = !busy
        var sections: [SettingsListSection] = []
        if isUpdatingLocal {
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
        if local, importing || isUpdatingLocal {
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
        let picker = UIDocumentPickerViewController(forOpeningContentTypes: [UTType(filenameExtension: "js") ?? .javaScript], asCopy: true)
        picker.allowsMultipleSelection = false; picker.delegate = self
        presentPageModal(picker)
    }

    func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
        guard let url = urls.first else { return }
        dismissPageModal(controller) { [weak self] in self?.readFile(url) }
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

    override func pageDidBecomeHidden() { onSaved = nil }

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
        if importing || isUpdatingLocal, file == nil {
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
        Task { [weak self] in
            guard let self else { return }
            do {
                switch mode {
                case .importLocal:
                    guard let source else { throw FilmScriptFileSnapshot.FileError.scriptRequired }
                    if let target {
                        _ = try await service.editCat(target, name: name, sourceType: "local", source: source)
                    } else {
                        _ = try await service.importLocal(name: name, source: source)
                    }
                case .edit(let cat): _ = try await service.editCat(cat.installationId, name: name, sourceURL: local ? nil : url)
                case .updateLocal(let cat): _ = try await service.updateCat(cat.installationId, source: source)
                }
                busy = false; render()
                if isPageActive, isPageVisible, let onSaved { onSaved() }
                else { enqueueNotice(title: "已保存") }
            } catch {
                busy = false; render()
                enqueueError(error.localizedDescription, retry: { [weak self] in self?.save() })
            }
        }
    }
}
#endif
