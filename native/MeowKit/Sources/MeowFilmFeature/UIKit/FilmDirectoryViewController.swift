#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class FilmDirectoryViewController: FilmSettingsListController, FilmPlaybackPage {
    override var usesSettingsColors: Bool { false }
    var filmPlaybackModel: FilmPlaybackModel? { model }
    private let model: FilmPlaybackModel
    private let sourceID: String
    private let lineID: FilmPlaybackIdentifier?
    private let path: [String]
    private let presentationOwner = UUID()
    private var displayedError: String?
    private var displayedEntries: [FilmPlaybackDirectory.Entry]?
    private var playbackAvailable = false
    var onPresentPlayback: (() -> Void)?

    init(model: FilmPlaybackModel, sourceID: String, lineID: FilmPlaybackIdentifier?, path: [String] = [],
         themeProvider: any PageThemeProviding) {
        self.model = model; self.sourceID = sourceID; self.lineID = lineID; self.path = path
        super.init(themeProvider: themeProvider)
        pageTitle = path.last.map { $0.isEmpty ? "/" : $0 } ?? "原始目录"
    }
    override func viewDidLoad() {
        super.viewDidLoad()
        table.accessibilityIdentifier = "film.directory.list"
        observe(model) { [weak self] in self?.update() }
        update()
    }
    override func pageDidBecomeVisible() {
        model.setPresentationVisible(true, owner: presentationOwner)
        model.open()
    }
    override func pageDidBecomeHidden() {
        model.setPresentationVisible(false, owner: presentationOwner)
    }

    private func update() {
        guard let browsing = model.presentation?.browsing, browsing.sourceID == sourceID, browsing.lineID == lineID else {
            if displayedEntries != nil || sections.isEmpty {
                displayedEntries = nil
                sections = [.init(title: "", rows: [.init(title: "目录已变更", id: "film.directory.changed")])]
                render()
            }
            return
        }
        let failure = model.actionError ?? model.observationError ?? model.snapshot?.browsing.error.map { FilmPlaybackError(code: $0) }
        if let failure {
            if failure.code != displayedError {
                displayedError = failure.code
                self.failure(failure, retry: { [weak self] in
                    guard let self else { return }
                    if model.restoringRead { model.refreshBrowsing() }
                    else { model.reconnect() }
                })
            }
        } else { displayedError = nil }
        let entries = FilmPlaybackDirectory(files: browsing.originalFiles).children(at: path)
        let execution = model.snapshot
        let available = model.executionStage != nil &&
            execution?.entryResolution != "loading" && execution?.entryResolution != "error" &&
            !(execution?.browsing.loading == true && execution?.browsing.refreshing != true) &&
            execution?.browsing.error == nil
        if entries == displayedEntries {
            guard playbackAvailable != available else { return }
            playbackAvailable = available
            for (index, entry) in entries.enumerated() {
                guard case .file(let file) = entry else { continue }
                let enabled = available && file.candidateID != nil
                sections[0].rows[index].enabled = enabled
                guard let cell = table.cellForRow(at: IndexPath(row: index, section: 0)) else { continue }
                if var content = cell.contentConfiguration as? UIListContentConfiguration {
                    content.textProperties.color = UIColor(enabled ? theme.ink : theme.muted)
                    cell.contentConfiguration = content
                }
                cell.selectionStyle = enabled ? .default : .none
                cell.accessibilityTraits = enabled ? .button : [.button, .notEnabled]
            }
            return
        }
        displayedEntries = entries
        playbackAvailable = available
        let rows = entries.enumerated().map { index, entry -> FilmSettingsRow in
            switch entry {
            case .folder(let name):
                return .init(title: name.isEmpty ? "/" : name, id: "film.directory.folder.\(index)", disclosure: true,
                             action: { [weak self] in
                    guard let self else { return }
                    let page = FilmDirectoryViewController(model: model, sourceID: sourceID, lineID: lineID,
                                                           path: path + [name], themeProvider: themeProvider)
                    page.onPresentPlayback = onPresentPlayback
                    navigationController?.pushViewController(page, animated: true)
                })
            case .file(let file):
                return .init(title: file.name, id: "film.directory.file.\(file.id)",
                             enabled: file.candidateID != nil && available, action: { [weak self] in
                    guard let self, let candidateID = file.candidateID else { return }
                    model.selectCandidate(candidateID); onPresentPlayback?()
                })
            }
        }
        sections = [.init(title: "", rows: rows.isEmpty ? [.init(title: "空目录", id: "film.directory.empty")] : rows)]
        render()
    }
}
#endif
