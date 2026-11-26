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
    private let lineID: String?
    private let path: [String]
    private let presentationOwner = UUID()
    var displayedEntries: [FilmPlaybackDirectory.Entry]?
    private var displayedState: String?
    private var playbackAvailable = false
    var onPresentPlayback: (() -> Void)?

    init(model: FilmPlaybackModel, sourceID: String, lineID: String?,
         path: [String] = [], themeProvider: any PageThemeProviding) {
        self.model = model
        self.sourceID = sourceID
        self.lineID = lineID
        self.path = path
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
        let ownsSelection = selected?.sourceSelectedID == sourceID &&
            (lineID == nil || selected?.lineSelectedID == lineID)
        let failure = ownsSelection ? model.actionError ?? model.observationError ??
            selected?.directoryError.map { FilmPlaybackError(code: $0) } : model.observationError
        syncErrorSource(
            "directory",
            message: failure?.code == "NO_VIDEO_SOURCES" ? nil : failure?.localizedDescription,
            token: failure?.code,
            retry: { [weak self] in
                guard let self else { return }
                if model.observationError != nil { model.reconnect() }
                else { model.refreshBrowsing() }
            }
        )
        guard let selected, ownsSelection, selected.directoryError == nil, !selected.directoryRawItems.isEmpty else {
            let loading = selected?.directoryLoading == true
            let state = loading ? "loading" : "暂无可用视频源"
            if displayedEntries != nil || displayedState != state {
                displayedEntries = nil
                displayedState = state
                applySections([SettingsListSection(id: "directory", rows: [
                    loading
                        ? SettingsListRow(id: "film.directory.state", accessory: .loading(accessibilityLabel: "加载中"), enabled: false)
                        : SettingsListRow(id: "film.directory.state", title: state, enabled: false),
                ])])
            }
            return
        }
        displayedState = nil
        let entries = FilmPlaybackDirectory(files: selected.directoryRawItems).children(at: path)
        let available = model.executionStage != nil && !model.restoringRead && !selected.noSources
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
                            path: path + [name], themeProvider: themeProvider
                        )
                        page.onPresentPlayback = onPresentPlayback
                        navigationController?.pushViewController(page, animated: true)
                    }
                )
            case .file(let file):
                let displayTitle = file.fileName.isEmpty ? file.title : file.fileName
                return SettingsListRow(
                    id: "film.directory.file.\(file.id)",
                    title: displayTitle,
                    enabled: file.isPlayable && available,
                    action: { [weak self] in
                        guard let self, file.isPlayable else { return }
                        model.selectItem(file.id)
                        onPresentPlayback?()
                    }
                )
            }
        }
        applySections([SettingsListSection(
            id: "directory",
            rows: rows.isEmpty ? [SettingsListRow(id: "film.directory.empty", title: "暂无可用视频源", enabled: false)] : rows
        )])
    }
}
#endif
