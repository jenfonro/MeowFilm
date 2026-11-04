#if os(iOS)
import UIKit
import SwiftUI
import MeowUI
import UniformTypeIdentifiers

@MainActor
public final class MusicCredentialImportViewController: HostingPageViewController<MusicCredentialImportContent>,
    UIDocumentPickerDelegate
{
    private var completed: PageHeaderTextButton?
    private let owner: MusicViewModel
    private let model: MusicCredentialImportViewModel
    private let draft = MusicCredentialImportDraft()
    private var generation: UUID?
    private var available = false
    private var fileGeneration: UUID?
    private var rendered: (MusicCredentialImportState, PageTheme)?
    private weak var replacementAlert: UIAlertController?

    public init(
        owner: MusicViewModel,
        service: any MusicCredentialsImporting,
        appearance: MusicAppearancePreferencesStore
    ) {
        self.owner = owner
        model = MusicCredentialImportViewModel(
            service: service,
            onSessionChanged: { [weak owner] platform in owner?.accountSessionsChanged(platform) }
        )
        super.init(
            themeProvider: appearance,
            content: MusicCredentialImportContent(
                draft: draft, theme: PageTheme(palette: appearance.pageColorway)
            )
        )
        pageTitle = "导入音乐凭据"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        observe(model) { [weak self] in self?.render() }
        observe(owner.accessChanges) { [weak self] in self?.syncAccess() }
        render()
    }

    public override func pageDidBecomeActive() {
        model.activate(
            isAvailable: owner.isAvailable && !owner.isClosed,
            accessGeneration: owner.accountAccessGeneration
        )
        syncAccess()
    }

    public override func pageDidBecomeVisible() { render() }

    public override func pageDidFinishAppearing() {
        if model.error != nil { render() }
    }

    public override func pageDidBecomeInactive() {
        draft.text = ""
        fileGeneration = nil
        replacementAlert = nil
        model.deactivate()
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
            guard let self, self.isPageActive, self.owner.isAvailable, !self.owner.isClosed,
                  access == self.owner.accountAccessGeneration, self.generation == access else { return }
            if let url = urls.first, urls.count == 1 { self.model.previewFile(url) }
            else { self.model.fileSelectionFailed() }
        }
    }

    public func documentPickerWasCancelled(_ controller: UIDocumentPickerViewController) {
        fileGeneration = nil
    }

    private func syncAccess() {
        guard isPageActive else { return }
        let access = owner.accountAccessGeneration
        let isAvailable = owner.isAvailable && !owner.isClosed
        if generation != access || available != isAvailable {
            draft.text = ""
            fileGeneration = nil
            replacementAlert?.dismiss(animated: true)
            replacementAlert = nil
        }
        generation = access
        available = isAvailable
        model.setAccess(isAvailable: isAvailable, accessGeneration: access)
        render()
    }

    private func render() {
        guard isViewLoaded else { return }
        let currentAccess = generation == owner.accountAccessGeneration &&
            available && owner.isAvailable && !owner.isClosed
        if !currentAccess, !draft.text.isEmpty { draft.text = "" }
        let state = MusicCredentialImportState(
            platform: model.platform,
            format: model.format,
            formats: model.formats,
            preview: currentAccess ? model.preview : nil,
            completed: currentAccess ? model.completed.map { "\($0.platform.title) · \($0.phase.title)" } : nil,
            available: currentAccess,
            busy: currentAccess && model.isBusy
        )
        if rendered?.0 != state || rendered?.1 != theme {
            rendered = (state, theme)
            setContent(MusicCredentialImportContent(
                state: state,
                draft: draft,
                theme: theme,
                selectPlatform: { [weak self] value in
                    guard let self, self.generation == self.owner.accountAccessGeneration else { return }
                    self.draft.text = ""
                    self.model.selectPlatform(value)
                },
                selectFormat: { [weak self] value in
                    guard let self, self.generation == self.owner.accountAccessGeneration else { return }
                    self.draft.text = ""
                    self.model.selectFormat(value)
                },
                chooseFile: { [weak self] in self?.openFile() },
                preview: { [weak self] in self?.previewText() },
                commit: { [weak self] in self?.commitImport() }
            ))
        }
        if state.completed == nil {
            completed = nil
            pageHeader.setTrailing([])
        } else if completed == nil {
            let button = PageHeaderTextButton(title: "完成") { [weak self] in self?.close() }
            completed = button
            pageHeader.setTrailing([button])
        }
        if currentAccess, let error = model.error {
            let access = owner.accountAccessGeneration
            showError(error.localizedDescription, acknowledged: { [weak self] in
                guard let self, self.owner.accountAccessGeneration == access,
                      self.model.error == error else { return }
                self.model.dismissError()
            })
        }
    }

    private func previewText() {
        let value = draft.text
        draft.text = ""
        guard generation == owner.accountAccessGeneration, owner.isAvailable, !owner.isClosed else { return }
        model.previewText(value)
    }

    private func commitImport() {
        guard generation == owner.accountAccessGeneration, owner.isAvailable, !owner.isClosed,
              let value = model.preview else { return }
        if value.requiresReplacement {
            let access = owner.accountAccessGeneration
            let alert = UIAlertController(title: "替换已保存的会话？", message: nil, preferredStyle: .alert)
            alert.addAction(UIAlertAction(title: "取消", style: .cancel))
            alert.addAction(UIAlertAction(title: "替换并导入", style: .destructive) { [weak self] _ in
                guard let self, self.isPageActive, self.owner.accountAccessGeneration == access,
                      self.owner.isAvailable, !self.owner.isClosed, self.generation == access,
                      self.model.preview?.id == value.id else { return }
                self.model.commit(replaceExisting: true)
            })
            replacementAlert = alert
            present(alert, animated: true)
        } else {
            model.commit(replaceExisting: false)
        }
    }

    private func openFile() {
        guard isPageActive, generation == owner.accountAccessGeneration,
              owner.isAvailable, !owner.isClosed, !model.isBusy else { return }
        model.cancel()
        draft.text = ""
        fileGeneration = owner.accountAccessGeneration
        let picker = UIDocumentPickerViewController(
            forOpeningContentTypes: [.json, .plainText, .data], asCopy: true
        )
        picker.delegate = self
        present(picker, animated: true)
    }

    private func close() { navigationController?.popViewController(animated: true) }
}
#endif
