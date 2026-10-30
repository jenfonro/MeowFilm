#if os(iOS)
import UIKit
import SwiftUI
import MeowUI
import UniformTypeIdentifiers

@MainActor
public final class MusicLibraryImportViewController: HostingPageViewController<MusicLibraryImportContent>,
    UIDocumentPickerDelegate
{
    private let model: MusicViewModel
    private let appearance: MusicAppearancePreferencesStore
    private var fileGeneration: UUID?
    private var rendered: (MusicLibraryImportState, PageTheme)?

    public init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore) {
        self.model = model
        self.appearance = appearance
        super.init(
            themeProvider: appearance,
            content: MusicLibraryImportContent(theme: PageTheme(palette: appearance.pageColorway))
        )
        title = "导入曲库"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        observe(model.importChanges) { [weak self] in self?.render() }
        observe(model.accessChanges) { [weak self] in
            guard let self else { return }
            if !self.model.isAvailable || self.fileGeneration != self.model.accountAccessGeneration {
                self.fileGeneration = nil
            }
            self.render()
        }
        observe(model.noticeChanges) { [weak self] in self?.render() }
        render()
    }

    public override func pageDidBecomeVisible() { render() }

    public override func pageDidFinishAppearing() {
        if model.error != nil { render() }
    }

    public override func pageDidBecomeInactive() {
        fileGeneration = nil
        model.cancelImportPresentation()
    }

    public override func applyTheme() {
        super.applyTheme()
        render()
    }

    public func documentPicker(
        _ controller: UIDocumentPickerViewController,
        didPickDocumentsAt urls: [URL]
    ) {
        let access = fileGeneration
        fileGeneration = nil
        controller.dismiss(animated: true) { [weak self] in
            guard let self, self.isPageActive, self.model.isAvailable, !self.model.isClosed,
                  access == self.model.accountAccessGeneration else { return }
            guard let url = urls.first, urls.count == 1 else {
                self.model.error = "文件读取失败"
                return
            }
            self.runPageTask { [weak model = self.model] in
                guard let model, model.accountAccessGeneration == access else { return }
                await model.importFile(url)
            }
        }
    }

    public func documentPickerWasCancelled(_ controller: UIDocumentPickerViewController) {
        fileGeneration = nil
    }

    private func render() {
        guard isViewLoaded else { return }
        let state = MusicLibraryImportState(
            available: model.isAvailable && !model.isClosed,
            importing: model.showingImport,
            busy: model.isImporting,
            owner: model.importOwner,
            preview: model.importPreview?.report
        )
        if rendered?.0 != state || rendered?.1 != theme {
            rendered = (state, theme)
            setContent(MusicLibraryImportContent(
                state: state,
                owner: Binding(
                    get: { [weak model] in model?.importOwner ?? "" },
                    set: { [weak model] value in
                        guard let model, model.isAvailable, !model.isClosed,
                              model.showingImport, !model.isImporting, model.importPreview == nil else { return }
                        model.importOwner = value
                    }
                ),
                theme: theme,
                chooseFile: { [weak self] in self?.openFile() },
                preview: { [weak self] in self?.previewImport() },
                confirm: { [weak self] in self?.confirmImport() },
                cancel: { [weak model] in model?.cancelImportPresentation() },
                history: { [weak self] in self?.openHistory() }
            ))
        }
        if let error = model.error {
            let access = model.accountAccessGeneration
            showError(error, acknowledged: { [weak model] in
                if model?.accountAccessGeneration == access, model?.error == error { model?.error = nil }
            })
        }
    }

    private func openFile() {
        guard isPageActive, model.isAvailable, !model.isClosed, !model.isImporting else { return }
        fileGeneration = model.accountAccessGeneration
        let picker = UIDocumentPickerViewController(forOpeningContentTypes: [.json], asCopy: true)
        picker.delegate = self
        present(picker, animated: true)
    }

    private func previewImport() {
        let access = model.accountAccessGeneration, owner = model.importOwner
        runPageTask { [weak model] in
            guard let model, model.accountAccessGeneration == access, model.importOwner == owner else { return }
            await model.previewImport()
        }
    }

    private func confirmImport() {
        guard let id = model.importPreview?.importId else { return }
        let access = model.accountAccessGeneration
        runPageTask { [weak model] in
            guard let model, model.accountAccessGeneration == access, model.importPreview?.importId == id else { return }
            await model.confirmImport()
        }
    }

    private func openHistory() {
        guard isPageActive, model.isAvailable, !model.isClosed, let service = model.importManagement else { return }
        navigationController?.pushViewController(
            MusicImportHistoryViewController(owner: model, service: service, appearance: appearance),
            animated: true
        )
    }
}
#endif
