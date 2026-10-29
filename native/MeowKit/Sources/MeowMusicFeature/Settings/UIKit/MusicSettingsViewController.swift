#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
public final class MusicSettingsViewController: MusicPageViewController,
    UITableViewDataSource, UITableViewDelegate
{
    private enum Row: CaseIterable {
        case aggregation, search, playback, appearance, metadata

        var title: String {
            switch self {
            case .aggregation: return "平台聚合设置"
            case .search: return "搜索设置"
            case .playback: return "播放设置"
            case .appearance: return "偏好设置"
            case .metadata: return "歌词／封面设置"
            }
        }

        var symbol: String {
            switch self {
            case .aggregation: return "rectangle.stack"
            case .search: return "magnifyingglass"
            case .playback: return "play.circle"
            case .appearance: return "slider.horizontal.3"
            case .metadata: return "text.alignleft"
            }
        }
    }

    private let model: MusicViewModel
    private weak var router: (any MusicUIRouting)?
    private let table = UITableView(frame: .zero, style: .insetGrouped)

    public init(
        model: MusicViewModel,
        appearance: MusicAppearancePreferencesStore,
        router: any MusicUIRouting
    ) {
        self.model = model
        self.router = router
        super.init(appearance: appearance)
        title = "设置"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        table.backgroundColor = .clear
        table.dataSource = self
        table.delegate = self
        table.register(UITableViewCell.self, forCellReuseIdentifier: "SettingsRow")
        view.addSubview(table)
        table.pinEdges(to: view)
    }

    public override func pageDidBecomeActive() {
        runPageTask { [weak model] in await model?.load() }
    }

    public func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int {
        Row.allCases.count
    }

    public func tableView(
        _ tableView: UITableView,
        cellForRowAt indexPath: IndexPath
    ) -> UITableViewCell {
        let row = Row.allCases[indexPath.row]
        let cell = tableView.dequeueReusableCell(withIdentifier: "SettingsRow", for: indexPath)
        var configuration = cell.defaultContentConfiguration()
        configuration.text = row.title
        configuration.image = UIImage(systemName: row.symbol)
        configuration.textProperties.color = UIColor(theme.ink)
        configuration.imageProperties.tintColor = UIColor(theme.ink)
        cell.contentConfiguration = configuration
        cell.backgroundColor = UIColor(theme.surface).withAlphaComponent(0.58)
        cell.accessoryType = .disclosureIndicator
        cell.accessibilityIdentifier = {
            switch row {
            case .aggregation: return "music.settings.aggregation"
            case .search: return "settings.search"
            case .playback: return "music.settings.playback"
            case .appearance: return "settings.preferences"
            case .metadata: return "music.settings.metadata"
            }
        }()
        return cell
    }

    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        let controller: UIViewController
        switch Row.allCases[indexPath.row] {
        case .aggregation:
            controller = MusicAccountsViewController(
                model: model,
                appearance: appearance,
                router: router
            )
        case .search:
            controller = MusicSearchSettingsViewController(appearance: appearance)
        case .playback:
            controller = MusicPlaybackSettingsViewController(model: model, appearance: appearance)
        case .appearance:
            controller = MusicAppearanceSettingsViewController(store: appearance)
        case .metadata:
            controller = MusicMetadataSettingsViewController(model: model, appearance: appearance)
        }
        navigationController?.pushViewController(controller, animated: true)
    }
}

@MainActor
private final class MusicSearchSettingsViewController: MusicPageViewController,
    UITableViewDataSource, UITableViewDelegate
{
    private enum Section: Int, CaseIterable { case songs, playlists, playlistPlatforms }
    private let store = MusicSearchPreferencesStore()
    private let table = UITableView(frame: .zero, style: .insetGrouped)

    override init(appearance: MusicAppearancePreferencesStore) {
        super.init(appearance: appearance)
        title = "搜索设置"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        table.backgroundColor = .clear
        table.dataSource = self
        table.delegate = self
        view.addSubview(table)
        table.pinEdges(to: view)
        observe(store) { [weak self] in self?.table.reloadData() }
    }

    public override func pageDidBecomeActive() {
        store.reload()
    }

    public func numberOfSections(in tableView: UITableView) -> Int { Section.allCases.count }

    public func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int {
        switch Section(rawValue: section)! {
        case .songs: return MusicSearchPreferences.platforms.count
        case .playlists: return 2
        case .playlistPlatforms:
            return store.preferences.playlistMode == "aggregate"
                ? MusicSearchPreferences.platforms.count : 0
        }
    }

    public func tableView(_ tableView: UITableView, titleForHeaderInSection section: Int) -> String? {
        switch Section(rawValue: section)! {
        case .songs: return "歌曲搜索平台"
        case .playlists: return nil
        case .playlistPlatforms: return "歌单搜索聚合平台"
        }
    }

    public func tableView(
        _ tableView: UITableView,
        cellForRowAt indexPath: IndexPath
    ) -> UITableViewCell {
        let cell = UITableViewCell(style: .value1, reuseIdentifier: nil)
        cell.backgroundColor = UIColor(theme.surface).withAlphaComponent(0.58)
        cell.textLabel?.textColor = UIColor(theme.ink)
        cell.detailTextLabel?.textColor = UIColor(theme.muted)
        switch Section(rawValue: indexPath.section)! {
        case .songs:
            let platform = MusicSearchPreferences.platforms[indexPath.row]
            cell.textLabel?.text = platform.title
            cell.accessoryType = store.preferences.songPlatforms.contains(platform) ? .checkmark : .none
            cell.accessibilityIdentifier = "music.search.platform.\(platform.rawValue)"
        case .playlists:
            if indexPath.row == 0 {
                cell.textLabel?.text = "搜索歌曲同时搜索歌单"
                cell.accessoryType = store.preferences.searchPlaylists ? .checkmark : .none
            } else {
                cell.textLabel?.text = "歌单搜索"
                cell.detailTextLabel?.text = store.preferences.playlistMode == "aggregate"
                    ? "聚合"
                    : MusicSearchPreferences.platforms.first {
                        $0.rawValue == store.preferences.playlistMode
                    }?.title
                cell.accessoryType = .disclosureIndicator
            }
        case .playlistPlatforms:
            let platform = MusicSearchPreferences.platforms[indexPath.row]
            cell.textLabel?.text = platform.title
            cell.accessoryType = store.preferences.playlistPlatforms.contains(platform) ? .checkmark : .none
        }
        return cell
    }

    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        var value = store.preferences
        switch Section(rawValue: indexPath.section)! {
        case .songs:
            toggle(MusicSearchPreferences.platforms[indexPath.row], in: &value.songPlatforms)
        case .playlists:
            if indexPath.row == 0 {
                value.searchPlaylists.toggle()
            } else {
                selectPlaylistMode()
                return
            }
        case .playlistPlatforms:
            toggle(MusicSearchPreferences.platforms[indexPath.row], in: &value.playlistPlatforms)
        }
        store.update(value)
    }

    private func toggle(_ platform: MusicPublicBrowsePlatform, in values: inout [MusicPublicBrowsePlatform]) {
        if values.contains(platform) { values.removeAll { $0 == platform } }
        else { values.append(platform) }
    }

    private func selectPlaylistMode() {
        let alert = UIAlertController(title: "歌单搜索", message: nil, preferredStyle: .actionSheet)
        for platform in MusicSearchPreferences.platforms {
            alert.addAction(UIAlertAction(title: platform.title, style: .default) { [weak self] _ in
                guard let self else { return }
                var value = self.store.preferences
                value.playlistMode = platform.rawValue
                self.store.update(value)
            })
        }
        alert.addAction(UIAlertAction(title: "聚合", style: .default) { [weak self] _ in
            guard let self else { return }
            var value = self.store.preferences
            value.playlistMode = "aggregate"
            self.store.update(value)
        })
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        present(alert, animated: true)
    }
}

@MainActor
private final class MusicPlaybackSettingsViewController: MusicPageViewController,
    UITableViewDataSource, UITableViewDelegate
{
    private let model: MusicViewModel
    private let table = UITableView(frame: .zero, style: .insetGrouped)

    public init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore) {
        self.model = model
        super.init(appearance: appearance)
        title = "播放设置"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        table.backgroundColor = .clear
        table.dataSource = self
        table.delegate = self
        view.addSubview(table)
        table.pinEdges(to: view)
    }

    public func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int { 2 }

    public func tableView(
        _ tableView: UITableView,
        cellForRowAt indexPath: IndexPath
    ) -> UITableViewCell {
        let cell = UITableViewCell(style: .default, reuseIdentifier: nil)
        cell.backgroundColor = UIColor(theme.surface).withAlphaComponent(0.58)
        cell.textLabel?.textColor = UIColor(theme.ink)
        cell.textLabel?.text = indexPath.row == 0 ? "音质设置" : "空间／全景音频设置"
        cell.accessoryType = .disclosureIndicator
        return cell
    }

    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        navigationController?.pushViewController(
            MusicQualitySettingsViewController(
                model: model,
                spatial: indexPath.row == 1,
                appearance: appearance
            ),
            animated: true
        )
    }
}

@MainActor
private final class MusicQualitySettingsViewController: MusicPageViewController,
    UITableViewDataSource, UITableViewDelegate
{
    private static let qualities = ["master", "spatial", "enhanced", "hires", "lossless", "high", "standard"]
    private static let spatialTypes = ["dolby", "sky", "vivid", "dts_x", "sony360", "qq_atmos", "viper_atmos"]
    private let model: MusicViewModel
    private let spatial: Bool
    private var settingsTask: Task<Void, Never>?
    private let table = UITableView(frame: .zero, style: .insetGrouped)

    public init(
        model: MusicViewModel,
        spatial: Bool,
        appearance: MusicAppearancePreferencesStore
    ) {
        self.model = model
        self.spatial = spatial
        super.init(appearance: appearance)
        title = spatial ? "空间／全景音频设置" : "音质设置"
    }

    private var order: [String] {
        MusicQualityTogglePolicy.ordered(spatial ? model.state.spatialOrder : model.state.qualityOrder,
                                         spatial: spatial)
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        table.backgroundColor = .clear
        table.dataSource = self
        table.delegate = self
        table.isEditing = true
        table.allowsSelectionDuringEditing = true
        view.addSubview(table)
        table.pinEdges(to: view)
        observe(model) { [weak self] in self?.table.reloadData() }
    }

    public func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int { order.count }

    public func tableView(
        _ tableView: UITableView,
        cellForRowAt indexPath: IndexPath
    ) -> UITableViewCell {
        let id = order[indexPath.row]
        let cell = UITableViewCell(style: .default, reuseIdentifier: nil)
        cell.backgroundColor = UIColor(theme.surface).withAlphaComponent(0.58)
        cell.textLabel?.text = qualityTitle(id)
        cell.textLabel?.textColor = UIColor(theme.ink)
        let enabled = (spatial ? model.state.spatialEnabled : model.state.qualityEnabled).contains(id)
        cell.accessoryType = enabled ? .checkmark : .none
        cell.showsReorderControl = true
        cell.accessibilityIdentifier = "music.settings." + (spatial ? "spatial." : "quality.") + id
        return cell
    }

    public func tableView(_ tableView: UITableView, editingStyleForRowAt indexPath: IndexPath)
        -> UITableViewCell.EditingStyle { .none }

    public func tableView(_ tableView: UITableView, shouldIndentWhileEditingRowAt indexPath: IndexPath) -> Bool {
        false
    }

    public func tableView(_ tableView: UITableView, canMoveRowAt indexPath: IndexPath) -> Bool { true }

    public func tableView(
        _ tableView: UITableView,
        moveRowAt sourceIndexPath: IndexPath,
        to destinationIndexPath: IndexPath
    ) {
        var values = order
        let moved = values.remove(at: sourceIndexPath.row)
        values.insert(moved, at: destinationIndexPath.row)
        let revision = model.state.settingsRevision
        save(spatial ? MusicSettingsChange(ifRevision: revision, spatialOrder: values)
                     : MusicSettingsChange(ifRevision: revision, qualityOrder: values))
    }

    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        let id = order[indexPath.row]
        let revision = model.state.settingsRevision
        var values = spatial ? model.state.spatialEnabled : model.state.qualityEnabled
        if values.contains(id) {
            let locked = spatial
                ? MusicQualityTogglePolicy.locksSpatial(id, qualities: model.state.qualityEnabled,
                                                        spatial: model.state.spatialEnabled)
                : MusicQualityTogglePolicy.locksQuality(id, qualities: model.state.qualityEnabled,
                                                        spatial: model.state.spatialEnabled)
            guard !locked else { return }
            values.removeAll { $0 == id }
        } else {
            values.append(id)
        }
        save(spatial ? MusicSettingsChange(ifRevision: revision, spatialEnabled: values)
                     : MusicSettingsChange(ifRevision: revision, qualityEnabled: values))
    }

    public override func pageDidBecomeInactive() { settingsTask?.cancel() }

    private func save(_ change: MusicSettingsChange) {
        guard settingsTask == nil, model.isAvailable, !model.isClosed, isPageActive else { return }
        let access = model.accountAccessGeneration
        settingsTask = Task { [weak self] in
            defer { self?.settingsTask = nil }
            guard let self, !Task.isCancelled, self.isPageActive,
                  access == self.model.accountAccessGeneration else { return }
            await self.model.updateSettings(change)
        }
    }

    private func qualityTitle(_ id: String) -> String { MusicQualityLabel.name(id) }
    deinit { settingsTask?.cancel() }
}

@MainActor
private final class MusicAppearanceSettingsViewController: MusicPageViewController,
    UITableViewDataSource, UITableViewDelegate
{
    private let store: MusicAppearancePreferencesStore
    private let table = UITableView(frame: .zero, style: .insetGrouped)

    public init(store: MusicAppearancePreferencesStore) {
        self.store = store
        super.init(appearance: store)
        title = "偏好设置"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        table.backgroundColor = .clear
        table.dataSource = self
        table.delegate = self
        view.addSubview(table)
        table.pinEdges(to: view)
        observe(store) { [weak self] in self?.table.reloadData() }
    }

    public override func pageDidBecomeActive() {
        store.reload()
    }

    public func numberOfSections(in tableView: UITableView) -> Int { 3 }

    public func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int {
        section == 1 && store.preferences.colorMode == .fixed ? 2 : 1
    }

    public func tableView(
        _ tableView: UITableView,
        cellForRowAt indexPath: IndexPath
    ) -> UITableViewCell {
        let cell = UITableViewCell(style: .value1, reuseIdentifier: nil)
        cell.backgroundColor = UIColor(theme.surface).withAlphaComponent(0.58)
        cell.textLabel?.textColor = UIColor(theme.ink)
        cell.detailTextLabel?.textColor = UIColor(theme.muted)
        switch (indexPath.section, indexPath.row) {
        case (0, 0):
            cell.textLabel?.text = "歌单样式"
            cell.detailTextLabel?.text = store.preferences.playlistView.title
        case (1, 0):
            cell.textLabel?.text = "全局配色"
            cell.detailTextLabel?.text = store.preferences.colorMode.title
        case (1, 1):
            cell.textLabel?.text = "固定配色"
            cell.detailTextLabel?.text = store.preferences.colorPalette.title
        default:
            cell.textLabel?.text = "锁屏歌词"
            cell.accessoryType = store.preferences.lockLyric ? .checkmark : .none
        }
        return cell
    }

    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        switch (indexPath.section, indexPath.row) {
        case (0, 0):
            choose(
                title: "歌单样式",
                values: MusicPlaylistAppearance.allCases.map { ($0.title, $0) }
            ) { [weak store] value in store?.set(\.playlistView, value) }
        case (1, 0):
            choose(
                title: "全局配色",
                values: MusicColorMode.allCases.map { ($0.title, $0) }
            ) { [weak store] value in store?.set(\.colorMode, value) }
        case (1, 1):
            choose(
                title: "固定配色",
                values: PageColorway.allCases.map { ($0.title, $0) }
            ) { [weak store] value in store?.selectFixedPalette(value) }
        default:
            store.set(\.lockLyric, !store.preferences.lockLyric)
        }
    }

    private func choose<Value>(
        title: String,
        values: [(String, Value)],
        action: @escaping (Value) -> Void
    ) {
        let alert = UIAlertController(title: title, message: nil, preferredStyle: .actionSheet)
        for value in values {
            alert.addAction(UIAlertAction(title: value.0, style: .default) { _ in action(value.1) })
        }
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        present(alert, animated: true)
    }
}

@MainActor
private final class MusicMetadataSettingsViewController: MusicPageViewController,
    UITableViewDataSource, UITableViewDelegate {
    private let owner: MusicViewModel
    private let state: MusicMetadataSettingsModel?
    private let table = UITableView(frame: .zero, style: .insetGrouped)
    init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore) {
        owner = model
        state = model.metadataPreferencesService.map { service in
            MusicMetadataSettingsModel(service: service) { [weak model] _ in
                model?.scheduleMetadataPreferencesRefresh()
            }
        }
        super.init(appearance: appearance)
        title = "歌词／封面设置"
    }
    override func viewDidLoad() {
        super.viewDidLoad()
        table.backgroundColor = .clear; table.dataSource = self; table.delegate = self
        table.isEditing = true; table.allowsSelectionDuringEditing = true
        view.addSubview(table); table.pinEdges(to: view)
        if let state { observe(state) { [weak self] in self?.render() } }
        observe(owner) { [weak self] in self?.activate() }
    }
    override func pageDidBecomeActive() { activate() }
    override func pageDidBecomeInactive() { state?.setAccess(available: false, generation: owner.accountAccessGeneration) }
    func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int {
        state?.value?.platforms.count ?? 0
    }
    func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let value = state!.value!, platform = value.platforms[indexPath.row]
        let cell = UITableViewCell(style: .default, reuseIdentifier: nil)
        cell.textLabel?.text = platform.title; cell.textLabel?.textColor = UIColor(theme.ink)
        cell.backgroundColor = UIColor(theme.surface).withAlphaComponent(0.58)
        cell.accessoryType = value.enabledPlatforms.contains(platform) ? .checkmark : .none
        cell.showsReorderControl = true
        return cell
    }
    func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        guard let value = state?.value else { return }
        let platform = value.platforms[indexPath.row]
        state?.setEnabled(platform, !value.enabledPlatforms.contains(platform))
    }
    func tableView(_ tableView: UITableView, editingStyleForRowAt indexPath: IndexPath) -> UITableViewCell.EditingStyle { .none }
    func tableView(_ tableView: UITableView, shouldIndentWhileEditingRowAt indexPath: IndexPath) -> Bool { false }
    func tableView(_ tableView: UITableView, canMoveRowAt indexPath: IndexPath) -> Bool { state?.busy == false }
    func tableView(_ tableView: UITableView, moveRowAt source: IndexPath, to destination: IndexPath) {
        guard var platforms = state?.value?.platforms else { return }
        let moved = platforms.remove(at: source.row); platforms.insert(moved, at: destination.row)
        state?.reorder(platforms)
    }
    private func activate() {
        state?.setAccess(available: owner.isAvailable && !owner.isClosed, generation: owner.accountAccessGeneration)
        if state?.value == nil { state?.reload() }
        render()
    }
    private func render() {
        table.reloadData()
        showError(state?.error)
    }
}

@MainActor
private final class MusicAccountsViewController: MusicPageViewController,
    UITableViewDataSource, UITableViewDelegate
{
    private enum Section: Int, CaseIterable { case accounts, tools }
    private let owner: MusicViewModel
    private let accounts: MusicAccountsViewModel?
    private let sync: MusicLibrarySyncViewModel?
    private weak var router: (any MusicUIRouting)?
    private let table = UITableView(frame: .zero, style: .insetGrouped)
    private let feedbackState: MusicPlaybackFeedbackSettingsModel?
    private var feedback: MusicPlaybackFeedbackSettings? { feedbackState?.value }

    public init(
        model: MusicViewModel,
        appearance: MusicAppearancePreferencesStore,
        router: (any MusicUIRouting)?
    ) {
        owner = model
        feedbackState = model.playbackFeedbackSettings.map { MusicPlaybackFeedbackSettingsModel(service: $0) }
        accounts = model.accounts.map { MusicAccountsViewModel(service: $0) }
        sync = model.librarySync.map {
            MusicLibrarySyncViewModel(service: $0, accounts: model.accounts) { [weak model] in
                await model?.load()
            }
        }
        self.router = router
        super.init(appearance: appearance)
        title = "平台聚合设置"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        table.backgroundColor = .clear
        table.dataSource = self
        table.delegate = self
        view.addSubview(table)
        table.pinEdges(to: view)
        if let accounts {
            observe(accounts) { [weak self] in self?.render() }
        }
        if let sync {
            observe(sync) { [weak self] in self?.render() }
        }
        if let feedbackState { observe(feedbackState) { [weak self] in self?.render() } }
        observe(owner) { [weak self] in self?.reconcileAccess() }
        render()
    }

    public override func pageDidBecomeActive() {
        accounts?.activate(
            isAvailable: owner.isAvailable && !owner.isClosed,
            accessGeneration: owner.accountAccessGeneration
        )
        accounts?.setForeground(true)
        sync?.setPresentationActive(true)
        sync?.activate(
            available: owner.isAvailable && !owner.isClosed,
            generation: owner.accountAccessGeneration
        )
        runPageTask { [weak sync] in await sync?.load() }
        loadFeedback()
    }

    public override func pageDidBecomeInactive() {
        accounts?.setForeground(false)
        accounts?.deactivate()
        sync?.deactivate()
        feedbackState?.setAccess(available: false, generation: owner.accountAccessGeneration)
    }

    private func reconcileAccess() {
        accounts?.setAccess(isAvailable: owner.isAvailable && !owner.isClosed,
                            accessGeneration: owner.accountAccessGeneration)
        sync?.activate(available: owner.isAvailable && !owner.isClosed,
                       generation: owner.accountAccessGeneration)
        feedbackState?.setAccess(available: owner.isAvailable && !owner.isClosed,
                                 generation: owner.accountAccessGeneration)
        render()
    }

    public func numberOfSections(in tableView: UITableView) -> Int { Section.allCases.count }

    public func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int {
        switch Section(rawValue: section)! {
        case .accounts: return accounts?.rows.count ?? 0
        case .tools:
            return (owner.accounts is any MusicCredentialsImporting ? 1 : 0) + 1
        }
    }

    public func tableView(_ tableView: UITableView, titleForHeaderInSection section: Int) -> String? {
        Section(rawValue: section) == .tools ? "导入" : nil
    }

    public func tableView(
        _ tableView: UITableView,
        cellForRowAt indexPath: IndexPath
    ) -> UITableViewCell {
        let cell = UITableViewCell(style: .subtitle, reuseIdentifier: nil)
        cell.backgroundColor = UIColor(theme.surface).withAlphaComponent(0.58)
        cell.textLabel?.textColor = UIColor(theme.ink)
        cell.detailTextLabel?.textColor = UIColor(theme.muted)
        cell.accessoryType = .disclosureIndicator
        switch Section(rawValue: indexPath.section)! {
        case .accounts:
            let row = accounts!.rows[indexPath.row]
            cell.textLabel?.text = row.platform.title
            var detail = [row.nickname, row.phase.title].filter { !$0.isEmpty }
            if row.phase == .signedIn, let membership = row.profile?.membership {
                detail.append(membership.title(at: Date()))
            }
            if let sync {
                let value = sync.selection(row.platform)
                if value.favorites || value.playlists { detail.append("自动同步") }
            }
            if feedback?.platforms[row.platform]?.enabled == true { detail.append("播放上报") }
            cell.detailTextLabel?.text = detail.joined(separator: " · ")
        case .tools:
            let hasCredentials = owner.accounts is any MusicCredentialsImporting
            if hasCredentials, indexPath.row == 0 {
                cell.textLabel?.text = "导入账号凭据"
                cell.accessibilityIdentifier = "music.credentials.import"
            } else {
                cell.textLabel?.text = "导入曲库"
                cell.accessibilityIdentifier = "music.library.import"
            }
        }
        return cell
    }

    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        guard Section(rawValue: indexPath.section) == .accounts else {
            openTool(indexPath.row)
            return
        }
        guard let accounts else { return }
        let row = accounts.rows[indexPath.row]
        let alert = UIAlertController(title: row.platform.title, message: row.phase.title, preferredStyle: .actionSheet)
        if row.phase == .signedIn || row.phase == .requiresValidation {
            alert.addAction(UIAlertAction(title: "验证登录会话", style: .default) { _ in
                accounts.validate(row.platform)
            })
            if accounts.canRefreshSession(row.platform) {
                alert.addAction(UIAlertAction(title: "刷新登录会话", style: .default) { _ in
                    accounts.refreshSession(row.platform)
                })
            }
            if accounts.supportsProfileRefresh {
                alert.addAction(UIAlertAction(title: "刷新资料", style: .default) { _ in
                    accounts.refreshProfile(row.platform)
                })
            }
            alert.addAction(UIAlertAction(title: "退出登录", style: .destructive) { _ in
                accounts.logout(row.platform)
            })
        } else if row.canLogin {
            alert.addAction(UIAlertAction(title: "登录", style: .default) { [weak self] _ in
                guard let self else { return }
                self.navigationController?.pushViewController(
                    MusicAccountLoginViewController(
                        owner: self.owner,
                        service: self.owner.accounts!,
                        platform: row.platform,
                        appearance: self.appearance
                    ),
                    animated: true
                )
            })
        }
        if row.phase == .signedIn, let sync {
            let value = sync.selection(row.platform)
            if sync.supportsAutomatic, sync.automaticRevision != nil {
                alert.addAction(UIAlertAction(
                    title: (value.favorites ? "关闭" : "开启") + "收藏歌曲自动同步",
                    style: .default
                ) { _ in
                    sync.select(row.platform, favorites: !value.favorites)
                })
                alert.addAction(UIAlertAction(
                    title: (value.playlists ? "关闭" : "开启") + "歌单自动同步",
                    style: .default
                ) { _ in
                    sync.select(row.platform, playlists: !value.playlists)
                })
            }
            if sync.canSync {
                alert.addAction(UIAlertAction(title: "立即同步", style: .default) { [weak self] _ in
                    self?.runPageTask { await sync.synchronize() }
                })
            }
        }
        if let setting = feedback?.platforms[row.platform],
           row.platform == .netease || row.playbackFeedbackAvailable {
            alert.addAction(UIAlertAction(
                title: (setting.enabled ? "关闭" : "开启") + "播放上报",
                style: .default
            ) { [weak self] _ in
                self?.changeFeedback(row.platform, enabled: !setting.enabled)
            })
        }
        if row.phase == .signedIn, row.platform != .netease {
            let choices: [(String, String)] = row.platform == .qq
                ? [("guess", "猜你喜欢"), ("radar", "雷达推荐")]
                : [("daily", "每日推荐"), ("fm", "红心 Radio · 本次推荐")]
            for choice in choices {
                alert.addAction(UIAlertAction(title: choice.1, style: .default) { [weak self] _ in
                    self?.router?.showPublicBrowse(
                        .accountRecommendation(row.platform, kind: choice.0, title: choice.1),
                        title: choice.1
                    )
                })
            }
        }
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        present(alert, animated: true)
    }

    private func render() {
        guard isViewLoaded else { return }
        table.reloadData()
        if accounts == nil {
            let label = UILabel()
            label.text = "账号服务不可用"
            label.textAlignment = .center
            label.textColor = UIColor(theme.muted)
            table.backgroundView = label
        } else {
            table.backgroundView = nil
        }
        showError(accounts?.error)
        showError(sync?.error)
    }

    private func openTool(_ row: Int) {
        let hasCredentials = owner.accounts is any MusicCredentialsImporting
        if hasCredentials, row == 0,
           let service = owner.accounts as? any MusicCredentialsImporting {
            navigationController?.pushViewController(
                MusicCredentialImportViewController(
                    owner: owner,
                    service: service,
                    appearance: appearance
                ),
                animated: true
            )
        } else {
            navigationController?.pushViewController(
                MusicLibraryImportViewController(model: owner, appearance: appearance),
                animated: true
            )
        }
    }

    private func loadFeedback() {
        guard let feedbackState else { return }
        feedbackState.setAccess(available: owner.isAvailable && !owner.isClosed,
                                generation: owner.accountAccessGeneration)
        runPageTask { await feedbackState.load() }
    }

    private func changeFeedback(_ platform: MusicAccountPlatform, enabled: Bool) {
        guard let state = feedbackState else { return }
        state.update(platform, enabled)
        guard let ticket = state.confirmation else { return }
        let alert = UIAlertController(
            title: "允许通过HTTP发送酷狗听音记录？",
            message: "HTTP传输会暴露记录、设备标识及CSCC会话信息，可能被窃取或伪造。",
            preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel) { _ in state.dismissConfirmation() })
        alert.addAction(UIAlertAction(title: "允许", style: .default) { _ in state.confirm(ticket) })
        present(alert, animated: true)
    }
}

@MainActor
private final class MusicAccountLoginViewController: MusicPageViewController {
    private let owner: MusicViewModel
    private let model: MusicAccountsViewModel
    private let platform: MusicAccountPlatform
    private let method = UISegmentedControl(items: ["二维码登录", "验证码登录"])
    private let qr = UIImageView()
    private let saveQR = UIButton(type: .system)
    private let country = UITextField()
    private let phone = UITextField()
    private let code = UITextField()
    private let send = UIButton(type: .system)
    private let submit = UIButton(type: .system)
    private let status = UILabel()

    public init(
        owner: MusicViewModel,
        service: any MusicAccountsServing,
        platform: MusicAccountPlatform,
        appearance: MusicAppearancePreferencesStore
    ) {
        self.owner = owner
        self.model = MusicAccountsViewModel(service: service)
        self.platform = platform
        super.init(appearance: appearance)
        title = platform.title
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        method.selectedSegmentIndex = 0
        method.addAction(UIAction { [weak self] _ in self?.changeMethod() }, for: .valueChanged)
        qr.contentMode = .scaleAspectFit
        qr.layer.cornerRadius = 12
        qr.clipsToBounds = true
        saveQR.setTitle("保存二维码", for: .normal)
        saveQR.addAction(UIAction { [weak self] _ in self?.shareQR() }, for: .touchUpInside)
        country.placeholder = "国家／地区代码"
        phone.placeholder = "手机号"
        code.placeholder = "验证码"
        for field in [country, phone, code] {
            field.borderStyle = .roundedRect
            field.keyboardType = .numberPad
            field.addAction(UIAction { [weak self] _ in self?.updateFields() }, for: .editingChanged)
        }
        send.setTitle("发送验证码", for: .normal)
        send.addAction(UIAction { [weak self] _ in
            self?.updateFields()
            self?.model.sendSMS()
        }, for: .touchUpInside)
        submit.setTitle("登录", for: .normal)
        submit.addAction(UIAction { [weak self] _ in
            self?.updateFields()
            self?.model.submitSMS()
        }, for: .touchUpInside)
        status.textAlignment = .center
        status.numberOfLines = 0

        let sms = UIStackView(arrangedSubviews: [country, phone, code, send, submit])
        sms.axis = .vertical
        sms.spacing = 10
        sms.tag = 100
        let stack = UIStackView(arrangedSubviews: [method, qr, saveQR, sms, status])
        stack.axis = .vertical
        stack.spacing = 16
        stack.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 24),
            stack.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -24),
            stack.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 24),
            qr.heightAnchor.constraint(equalTo: qr.widthAnchor),
            saveQR.heightAnchor.constraint(equalToConstant: 44),
            country.heightAnchor.constraint(equalToConstant: 44),
            phone.heightAnchor.constraint(equalToConstant: 44),
            code.heightAnchor.constraint(equalToConstant: 44),
            send.heightAnchor.constraint(equalToConstant: 44),
            submit.heightAnchor.constraint(equalToConstant: 44),
        ])
        observe(model) { [weak self] in self?.render() }
        render()
    }

    public override func pageDidBecomeActive() {
        model.activate(isAvailable: owner.isAvailable && !owner.isClosed,
                       accessGeneration: owner.accountAccessGeneration)
        model.setForeground(true)
        model.selectLogin(platform)
    }

    public override func pageDidBecomeInactive() {
        model.setForeground(false)
        model.deactivate()
        qr.image = nil
        country.text = ""; phone.text = ""; code.text = ""
    }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        status.textColor = UIColor(theme.muted)
        method.selectedSegmentTintColor = UIColor(theme.surface)
        send.tintColor = UIColor(theme.accent)
        submit.tintColor = UIColor(theme.accent)
        saveQR.tintColor = UIColor(theme.accent)
    }

    private func changeMethod() {
        model.selectMethod(method.selectedSegmentIndex == 0 ? .qr : .sms)
    }

    private func updateFields() {
        model.countryCode = String((country.text ?? "").prefix(4))
        model.phone = String((phone.text ?? "").prefix(15))
        model.code = String((code.text ?? "").prefix(8))
    }

    private func render() {
        guard isViewLoaded else { return }
        let qrMode = model.loginMethod == .qr
        method.selectedSegmentIndex = qrMode ? 0 : 1
        qr.isHidden = !qrMode
        saveQR.isHidden = !qrMode || model.challenge == nil
        view.viewWithTag(100)?.isHidden = qrMode
        if let challenge = model.challenge {
            qr.image = UIImage(data: challenge.image)
        } else {
            qr.image = nil
        }
        if !country.isFirstResponder { country.text = model.countryCode }
        if !phone.isFirstResponder { phone.text = model.phone }
        if !code.isFirstResponder { code.text = model.code }
        send.isEnabled = model.canSendSMS
        submit.isEnabled = model.canSubmitSMS
        send.setTitle(
            model.resendSeconds > 0 ? "\(model.resendSeconds)秒后重发" : "发送验证码",
            for: .normal
        )
        status.text = model.error ?? model.progress.title
        applyTheme()
    }

    private func shareQR() {
        guard let image = qr.image else { return }
        present(UIActivityViewController(activityItems: [image], applicationActivities: nil), animated: true)
    }
}

#endif
