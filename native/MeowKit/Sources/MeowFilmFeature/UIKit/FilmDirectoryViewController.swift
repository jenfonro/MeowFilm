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
    private func update() {
        guard let browsing = model.snapshot?.browsing, browsing.sourceID == sourceID, browsing.lineID == lineID else {
            sections = [.init(title: "", rows: [.init(title: "目录已变更", id: "film.directory.changed")])]
            render(); return
        }
        if let error = browsing.error {
            sections = [.init(title: "", rows: [.init(title: FilmPlaybackError(code: error).localizedDescription,
                                                      id: "film.directory.error")])]
            render(); return
        }
        let entries = FilmPlaybackDirectory(files: browsing.originalFiles).children(at: path)
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
                             enabled: file.candidateID != nil, action: { [weak self] in
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
