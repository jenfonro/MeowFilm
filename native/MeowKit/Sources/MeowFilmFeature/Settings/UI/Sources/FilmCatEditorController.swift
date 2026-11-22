#if os(iOS)
import UIKit
import UniformTypeIdentifiers
import MeowUI

@MainActor
final class FilmCatEditorController: FilmSettingsListController, UIDocumentPickerDelegate {
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
    private var reading: Task<Void, Never>?
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
        runPageTask { [weak self, service] in
            do { try await service.initialize() }
            catch { self?.failure(error); return }
            let snapshot = await service.snapshot
            guard let self, !Task.isCancelled else { return }
            targets = snapshot.cats; render()
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
    override func render() {
        name.isEnabled = !busy; sourceURL.isEnabled = !busy
        name.textColor = UIColor(theme.settings.ink); sourceURL.textColor = UIColor(theme.settings.ink)
        if case .updateLocal = mode { sections = [] }
        else { sections = [.init(title: "名称", rows: [.init(title: "", id: "", content: name)])] }
        if !local { sections.append(.init(title: "URL", rows: [.init(title: "", id: "", content: sourceURL)])) }
        if local, importing || { if case .updateLocal = mode { return true }; return false }() {
            sections.append(.init(title: "文件", rows: [
                .init(title: file?.fileName ?? "选择 .js 文件", id: "film.cat.editor.file", enabled: !busy,
                      action: { [weak self] in self?.chooseFile() }),
            ]))
        }
        if importing {
            sections.append(.init(title: "安装", rows: [
                .init(title: installationId.flatMap { id in targets.first { $0.installationId == id }?.name } ?? "新安装",
                      id: "film.cat.editor.target", enabled: !busy, action: { [weak self] in self?.chooseTarget() }),
            ]))
        }
        sections.append(.init(title: "", rows: [
            .init(title: busy ? "处理中" : "保存", id: "film.cat.editor.save", enabled: !busy,
                  action: { [weak self] in self?.save() }),
        ])); super.render()
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
        reading = Task { [weak self] in
            do {
                let value = try await FilmScriptFileSnapshot.read(url)
                guard let self, !Task.isCancelled else { return }
                file = value
                if name.text?.isEmpty != false { name.text = URL(fileURLWithPath: value.fileName).deletingPathExtension().lastPathComponent }
                busy = false; reading = nil; render()
            } catch {
                guard let self, !Task.isCancelled else { return }
                busy = false; reading = nil; render()
                failure(error, retry: { [weak self] in self?.chooseFile() })
            }
        }
    }
    override func pageDidBecomeInactive() {
        // Accepted saves settle normally, but this editor can no longer hand
        // the batch to another page or submit its next item.
        onSaved = nil
        if let reading {
            reading.cancel(); self.reading = nil; busy = false; render()
        }
    }
    override func pageDidBecomeHidden() { saveVisibilityGeneration = UUID() }
    private func chooseTarget() {
        guard canPresentPageAlert else { return }
        var actions: [PageAlertAction] = [
            PageAlertAction(title: "新安装") { [weak self] in
                self?.installationId = nil; self?.render()
            }
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
            failure(FilmScriptFileSnapshot.FileError.scriptRequired, retry: { [weak self] in self?.chooseFile() }); return
        }
        if importing, installationId != nil {
            confirm("重新导入猫源？") { [weak self] in self?.commit() }
        } else { commit() }
    }
    private func commit() {
        guard !busy else { return }
        view.endEditing(true); busy = true; render()
        let name = name.text ?? "", url = sourceURL.text ?? "", source = file?.source, target = installationId
        let visibility = saveVisibilityGeneration
        Task { [weak self] in
            guard let self else { return }
            defer {
                // A retry may save again, but must not revive a batch whose
                // accepted save crossed a visibility boundary.
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
                else { notice("已保存") }
            } catch {
                busy = false; render(); failure(error, retry: { [weak self] in self?.save() })
            }
        }
    }
}
#endif
