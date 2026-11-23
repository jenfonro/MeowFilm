#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
final class FilmDirectoryViewController: SettingsListViewController, FilmPlaybackPage {
    override var usesSettingsColors: Bool { false }
    override var avoidsKeyboard: Bool { false }
    var filmPlaybackModel: FilmPlaybackModel? { model }
    let model: FilmPlaybackModel
    private let sourceID: String
    private let lineID: FilmPlaybackIdentifier?
    private let metadataSourceID: String?
    private let season: Int?
    private let path: [String]
    private let presentationOwner = UUID()
    var displayedEntries: [FilmPlaybackDirectory.Entry]?
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
        let directoryChanges = Publishers.MergeMany([
            model.presentationStore.$snapshot.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            model.presentationStore.$presentation.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            model.presentationStore.$observationError.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            model.presentationStore.$actionError.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
        ]).eraseToAnyPublisher()
        observe(directoryChanges, whenVisible: true) { [weak self] in
            self?.update()
        }
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
        syncErrorSource(
            "directory",
            message: failure?.localizedDescription,
            token: failure?.code,
            retry: { [weak self] in
                guard let self else { return }
                if model.observationError != nil { model.reconnect() }
                else { model.refreshBrowsing() }
            }
        )
        let directory = ownsSelection && selected?.browsing.error != nil ? nil :
            model.loadedDirectory(sourceID: sourceID, lineID: lineID,
                                  metadataSourceID: metadataSourceID, season: season)
        guard let browsing = directory?.browsing else {
            let loading = model.presentation?.browsing.loading == true
            let state = loading ? "加载中" : "无可用视频源"
            if displayedEntries != nil || displayedState != state {
                displayedEntries = nil
                displayedState = state
                applySections([SettingsListSection(id: "directory", rows: [
                    SettingsListRow(id: "film.directory.state", title: state, enabled: false),
                ])])
            }
            return
        }
        displayedState = nil
        let entries = FilmPlaybackDirectory(files: browsing.originalFiles).children(at: path)
        let available = model.executionStage != nil && !model.restoringRead
        if entries == displayedEntries && playbackAvailable == available { return }
        displayedEntries = entries
        playbackAvailable = available
        let rows = entries.enumerated().map { index, entry -> SettingsListRow in
            switch entry {
            case .folder(let name):
                return SettingsListRow(
                    id: "film.directory.folder.\(index)",
                    title: name.isEmpty ? "/" : name,
                    accessory: .disclosure,
                    action: { [weak self] in
                        guard let self else { return }
                        let page = FilmDirectoryViewController(
                            model: model, sourceID: sourceID, lineID: lineID,
                            metadataSourceID: metadataSourceID, season: season,
                            path: path + [name], themeProvider: themeProvider
                        )
                        page.onPresentPlayback = onPresentPlayback
                        navigationController?.pushViewController(page, animated: true)
                    }
                )
            case .file(let file):
                return SettingsListRow(
                    id: "film.directory.file.\(file.id)",
                    title: file.name,
                    enabled: file.candidateID != nil && available,
                    action: { [weak self] in
                        guard let self, file.candidateID != nil else { return }
                        model.selectTarget(file.playbackTarget, metadataSourceID: metadataSourceID)
                        onPresentPlayback?()
                    }
                )
            }
        }
        applySections([SettingsListSection(
            id: "directory",
            rows: rows.isEmpty ? [SettingsListRow(id: "film.directory.empty", title: "无可用视频源", enabled: false)] : rows
        )])
    }
}
#endif
