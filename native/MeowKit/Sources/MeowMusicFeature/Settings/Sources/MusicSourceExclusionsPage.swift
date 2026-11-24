#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
final class MusicSourceExclusionsState: ObservableObject {
    @Published var rows: [MusicSourceExclusion] = []
    @Published var scripts: [MusicScriptSourceItem] = []
    @Published var song = ""
    @Published var provider = "official:"
    @Published var platform = "wy"
    @Published var busy = false
    let model: MusicViewModel
    init(model: MusicViewModel) { self.model = model }
    var service: (any MusicSourceExclusionServing)? { model.service as? any MusicSourceExclusionServing }
    func reload() async throws {
        guard let service else { throw MusicServiceError.unsupported }
        let rows = try await service.sourceExclusions()
        let scripts = (try? await model.scriptSources?.scriptSources()) ?? []
        try Task.checkCancellation()
        guard !model.isClosed else { throw MusicServiceError.staleAccess }
        self.rows = rows; self.scripts = scripts
        if !model.queue.contains(where: { $0.id == song }) { song = model.current?.id ?? model.queue.first?.id ?? "" }
        if provider != "official:", !scripts.contains(where: { $0.id == provider }) { provider = "official:" }
    }
}

@MainActor
final class MusicSourceExclusionsViewController: SettingsListViewController {
    private let state: MusicSourceExclusionsState

    init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore) {
        state = MusicSourceExclusionsState(model: model)
        super.init(themeProvider: appearance)
        pageTitle = "错源管理"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        let uiChanges = Publishers.MergeMany([
            state.$rows.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.$scripts.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.$song.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.$provider.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.$platform.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            state.$busy.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
        ])
        observe(uiChanges.eraseToAnyPublisher(), whenVisible: true) { [weak self] in self?.render() }
        observe(state.model.accessChanges, whenVisible: true) { [weak self] in self?.render() }
        render()
    }

    override func pageDidBecomeVisible() {
        super.pageDidBecomeVisible()
        let state = self.state
        perform { try await state.reload() }
    }

    private func perform(_ work: @escaping @MainActor () async throws -> Void) {
        guard !state.busy, !state.model.isClosed else { return }
        state.busy = true
        runPageTask(
            key: "work",
            mapError: { MusicServiceError.sanitized($0).localizedDescription },
            onFinally: { [weak self] in self?.state.busy = false }
        ) {
            try await work()
        }
    }

    private func add() {
        let state = self.state
        guard let track = state.model.queue.first(where: { $0.id == state.song })?.track,
              let service = state.service else { return }
        let provider = state.provider, platform = state.platform
        perform {
            try await service.excludeSource(for: track.ref, provider: provider, platform: platform)
            try await state.reload()
        }
    }

    private func restore(_ id: String) {
        guard let service = state.service else { return }
        let state = self.state
        perform {
            try await service.restoreSourceExclusion(id: id)
            try await state.reload()
        }
    }

    private func render() {
        guard isViewLoaded else { return }
        let interactive = !state.busy && !state.model.isClosed

        let selectedSongTitle = state.model.queue
            .first { $0.id == state.song }
            .map { $0.track.title + " — " + $0.track.artist } ?? "无"
        let songMenu = UIMenu(options: .singleSelection, children: state.model.queue.map { entry in
            UIAction(
                title: entry.track.title + " — " + entry.track.artist,
                state: state.song == entry.id ? .on : .off
            ) { [weak state] _ in state?.song = entry.id }
        })

        let selectedProviderTitle = state.provider == "official:"
            ? "官方音源"
            : state.scripts.first { $0.id == state.provider }?.name ?? ""
        var providerActions: [UIMenuElement] = [
            UIAction(title: "官方音源", state: state.provider == "official:" ? .on : .off) { [weak state] _ in
                state?.provider = "official:"
            }
        ]
        providerActions += state.scripts.map { item in
            UIAction(title: item.name, state: state.provider == item.id ? .on : .off) { [weak state] _ in
                state?.provider = item.id
            }
        }
        let providerMenu = UIMenu(options: .singleSelection, children: providerActions)

        let selectedPlatformTitle = MusicPublicBrowsePlatform.allCases
            .first { $0.rawValue == state.platform }?.title ?? ""
        let platformMenu = UIMenu(options: .singleSelection, children: MusicPublicBrowsePlatform.allCases.map { p in
            UIAction(title: p.title, state: state.platform == p.rawValue ? .on : .off) { [weak state] _ in
                state?.platform = p.rawValue
            }
        })

        let addRows: [SettingsListRow] = [
            SettingsListRow(
                id: "music.exclusions.song",
                title: "歌曲",
                accessory: .menu(title: selectedSongTitle, menu: songMenu, enabled: interactive && !state.model.queue.isEmpty),
                enabled: interactive && !state.model.queue.isEmpty
            ),
            SettingsListRow(
                id: "music.exclusions.provider",
                title: "音源",
                accessory: .menu(title: selectedProviderTitle, menu: providerMenu, enabled: interactive),
                enabled: interactive
            ),
            SettingsListRow(
                id: "music.exclusions.platform",
                title: "平台",
                accessory: .menu(title: selectedPlatformTitle, menu: platformMenu, enabled: interactive),
                enabled: interactive
            ),
            SettingsListRow(
                id: "music.exclusions.add",
                title: "添加",
                enabled: interactive && !state.song.isEmpty,
                action: { [weak self] in self?.add() }
            ),
        ]

        let exclusionRows: [SettingsListRow]
        if state.rows.isEmpty {
            exclusionRows = [
                SettingsListRow(id: "music.exclusions.empty", title: "暂无错源", enabled: false)
            ]
        } else {
            exclusionRows = state.rows.map { row in
                SettingsListRow(
                    id: "music.exclusions.row.\(row.id)",
                    title: row.title + " — " + row.artist,
                    detail: exclusionLabel(row),
                    accessory: .action(title: "恢复", enabled: interactive) { [weak self] in
                        self?.restore(row.id)
                    },
                    enabled: interactive
                )
            }
        }

        applySections([
            SettingsListSection(id: "add", title: "添加错源", rows: addRows),
            SettingsListSection(id: "existing", title: "已排除", rows: exclusionRows),
        ])
    }

    private func exclusionLabel(_ row: MusicSourceExclusion) -> String {
        let platform = MusicPublicBrowsePlatform(rawValue: row.platform)?.title ?? row.platform
        switch row.scope {
        case .providerPlatform: return providerName(row) + " · " + platform + " · 全部录音"
        case .recording:
            let name = row.provider.isEmpty ? platform : providerName(row) + " · " + platform
            return name + " · 单录音"
        }
    }

    private func providerName(_ row: MusicSourceExclusion) -> String {
        row.provider.hasPrefix("official:") ? "官方音源" : state.scripts.first { $0.id == row.provider }?.name ?? row.provider
    }
}
#endif
