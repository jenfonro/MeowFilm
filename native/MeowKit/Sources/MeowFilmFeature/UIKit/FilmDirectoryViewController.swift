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
    private let metadataSourceID: String?
    private let season: Int?
    private let path: [String]
    private let presentationOwner = UUID()
    private var displayedError: String?
    private var displayedEntries: [FilmPlaybackDirectory.Entry]?
    private var displayedState: String?
    private var playbackAvailable = false
    var onPresentPlayback: (() -> Void)?

    init(model: FilmPlaybackModel, sourceID: String, lineID: FilmPlaybackIdentifier?,
         metadataSourceID: String?, season: Int?, path: [String] = [],
         themeProvider: any PageThemeProviding) {
        self.model = model; self.sourceID = sourceID; self.lineID = lineID; self.path = path
        self.metadataSourceID = metadataSourceID; self.season = season
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
        let selected = model.presentation
        let ownsSelection = selected?.browsing.sourceID == sourceID && selected?.browsing.lineID == lineID &&
            selected?.metadataSourceID == metadataSourceID && selected?.browsing.season == season
        let failure = ownsSelection ? model.actionError ?? model.observationError ??
            selected?.browsing.error.map { FilmPlaybackError(code: $0) } : model.observationError
        if let failure {
            if failure.code != displayedError {
                displayedError = failure.code
                self.failure(failure, retry: { [weak self] in
                    guard let self else { return }
                    if model.observationError != nil { model.reconnect() }
                    else { model.refreshBrowsing() }
                })
            }
        } else { displayedError = nil }
        let reading = ownsSelection && selected?.browsing.error != nil
        let directory = reading ? nil : model.loadedDirectory(sourceID: sourceID, lineID: lineID,
            metadataSourceID: metadataSourceID, season: season)
        guard let browsing = directory?.browsing else {
            let loading = model.presentation?.browsing.loading == true
            let state = failure != nil ? "加载失败" : loading ? "加载中" : "无可用视频源"
            if displayedEntries != nil || displayedState != state {
                displayedEntries = nil
                displayedState = state
                sections = [.init(title: "", rows: [.init(title: state,
                                                         id: "film.directory.state", enabled: false)])]
                render()
            }
            return
        }
        displayedState = nil
        let entries = FilmPlaybackDirectory(files: browsing.originalFiles).children(at: path)
        let available = model.executionStage != nil && !model.restoringRead
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
                                                           metadataSourceID: metadataSourceID, season: season,
                                                           path: path + [name], themeProvider: themeProvider)
                    page.onPresentPlayback = onPresentPlayback
                    navigationController?.pushViewController(page, animated: true)
                })
            case .file(let file):
                return .init(title: file.name, id: "film.directory.file.\(file.id)",
                             enabled: file.candidateID != nil && available, action: { [weak self] in
                    guard let self, file.candidateID != nil else { return }
                    model.selectTarget(file.playbackTarget, metadataSourceID: metadataSourceID)
                    onPresentPlayback?()
                })
            }
        }
        sections = [.init(title: "", rows: rows.isEmpty ?
            [.init(title: "无可用视频源", id: "film.directory.empty", enabled: false)] : rows)]
        render()
    }
}
#endif
