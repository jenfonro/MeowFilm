#if os(iOS)
import SwiftUI
import UIKit
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
        let own = model.accountAccessGeneration
        let rows = try await service.sourceExclusions()
        let scripts = (try? await model.scriptSources?.scriptSources()) ?? []
        try Task.checkCancellation(); try model.check(own)
        self.rows = rows; self.scripts = scripts
        if !model.queue.contains(where: { $0.id == song }) { song = model.current?.id ?? model.queue.first?.id ?? "" }
    }
}
@MainActor
final class MusicSourceExclusionsViewController: MusicSettingsPageViewController<MusicSourceExclusionsContent> {
    private let state: MusicSourceExclusionsState
    init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore) {
        state = .init(model: model)
        super.init(appearance: appearance, content: .init(state: state, theme: .init(colors: appearance.pagePalette)))
        pageTitle = "错源管理"
    }
    override func pageDidBecomeVisible() { perform { try await self.state.reload() } }
    override func applyTheme() {
        super.applyTheme()
        guard contentThemeChanged() else { return }
        setContent(.init(state: state, theme: theme, add: { [weak self] in self?.add() }, restore: { [weak self] in self?.restore($0) }))
    }
    private func perform(_ work: @escaping @MainActor () async throws -> Void) {
        guard !state.busy, state.model.isAvailable else { return }
        state.busy = true
        runPageTask { [weak self] in
            defer { self?.state.busy = false }
            do { try await work() }
            catch { if !Task.isCancelled { self?.reportError(MusicServiceError.sanitized(error).localizedDescription) } }
        }
    }
    private func add() {
        guard let track = state.model.queue.first(where: { $0.id == state.song })?.track, let service = state.service else { return }
        let provider = state.provider, platform = state.platform
        perform { try await service.excludeSource(for: track.ref, provider: provider, platform: platform); try await self.state.reload() }
    }
    private func restore(_ id: String) {
        guard let service = state.service else { return }
        perform { try await service.restoreSourceExclusion(id: id); try await self.state.reload() }
    }
}
@MainActor
struct MusicSourceExclusionsContent: View {
    @ObservedObject var state: MusicSourceExclusionsState
    let theme: PageTheme
    var add: () -> Void = {}
    var restore: (String) -> Void = { _ in }
    var body: some View {
        List {
            Section("添加错源") {
                Picker("歌曲", selection: $state.song) {
                    ForEach(state.model.queue) { entry in Text(entry.track.title + " — " + entry.track.artist).tag(entry.id) }
                }.pickerStyle(.menu).modifier(MusicSettingsRowStyle(theme: theme, first: true, last: false))
                Picker("音源", selection: $state.provider) {
                    Text("官方音源").tag("official:")
                    ForEach(state.scripts) { item in Text(item.name).tag(item.id) }
                }.pickerStyle(.menu).modifier(MusicSettingsRowStyle(theme: theme, first: false, last: false))
                Picker("平台", selection: $state.platform) {
                    ForEach(MusicPublicBrowsePlatform.allCases) { Text($0.title).tag($0.rawValue) }
                }.pickerStyle(.menu).modifier(MusicSettingsRowStyle(theme: theme, first: false, last: false))
                Button("添加", action: add).disabled(state.song.isEmpty)
                    .foregroundStyle(Color(uiColor: UIColor(theme.settings.accent)))
                    .modifier(MusicSettingsRowStyle(theme: theme, first: false, last: true))
            }
            .listSectionSeparator(.hidden)
            Section("已排除") {
                if state.rows.isEmpty { Text("暂无错源").foregroundStyle(Color(uiColor: UIColor(theme.settings.muted))).modifier(MusicSettingsRowStyle(theme: theme, first: true, last: true)) }
                ForEach(state.rows) { row in
                    HStack(alignment: .center) {
                        VStack(alignment: .leading, spacing: 4) {
                            Text(row.title + " — " + row.artist)
                            Text(exclusionLabel(row))
                                .font(.caption).foregroundStyle(Color(uiColor: UIColor(theme.settings.muted)))
                        }
                        Spacer()
                        Button("恢复") { restore(row.id) }
                            .foregroundStyle(Color(uiColor: UIColor(theme.settings.accent)))
                    }.modifier(MusicSettingsRowStyle(theme: theme,
                        first: row.id == state.rows.first?.id, last: row.id == state.rows.last?.id))
                }
            }
            .listSectionSeparator(.hidden)
        }
        .disabled(state.busy || !state.model.isAvailable)
        .modifier(MusicSettingsListStyle(theme: theme))
    }
    private func exclusionLabel(_ row: MusicSourceExclusion) -> String {
        let platform = MusicPublicBrowsePlatform(rawValue: row.platform)?.title ?? row.platform
        switch row.scope {
        case .providerPlatform: return providerName(row) + " · " + platform + " · 全部录音"
        case .recording(_, _, let key): return "录音来源 · " + platform + " · " + key
        }
    }
    private func providerName(_ row: MusicSourceExclusion) -> String {
        row.provider.hasPrefix("official:") ? "官方音源" : state.scripts.first { $0.id == row.provider }?.name ?? row.provider
    }
}
#endif
