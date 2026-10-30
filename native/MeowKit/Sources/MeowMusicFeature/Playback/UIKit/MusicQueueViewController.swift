#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class MusicQueueViewController: MusicPageViewController,
    UIPageViewControllerDataSource, UIPageViewControllerDelegate,
    UITableViewDelegate
{
    public var onDismiss: (() -> Void)?
    private let model: MusicViewModel
    private let favorites: MusicMiniPlayerFavorites
    private let categories = UISegmentedControl(items: MusicReferenceQueuePage.allCases.map(\.title))
    private let pager = UIPageViewController(transitionStyle: .scroll, navigationOrientation: .horizontal)
    private let current = MusicQueueTablePage()
    private let songs = MusicQueueTablePage()
    private let emptyHistory = MusicQueueTablePage()
    private var histories: [String: MusicHistoryCardViewController] = [:]
    private var selected = "current"
    private var paging = false
    private var pendingSelection: String?
    private var entries: [MusicQueueEntry] = []
    private var currentDataSource: MusicQueueDataSource!
    private var songsDataSource: UITableViewDiffableDataSource<String, String>!
    private var candidatesDataSource: UITableViewDiffableDataSource<Int, String>!
    private var entryRows: [String: (entry: MusicQueueEntry, index: Int)] = [:]
    private var playedRows: [String: (song: MusicPlayedSong, index: Int)] = [:]
    private var candidateRows: [String: (candidate: MusicSimilarModeCandidate, index: Int)] = [:]
    private var historyAccess: UUID?
    private var pendingReorder: UUID?
    private var shownSimilarError: String?
    private var displayed: MusicReferenceQueueIntent?
    private var similar: MusicSimilarModeSnapshot?
    private var ids: [String] { MusicReferenceQueueStrip.ids(history: model.playedPlaylists.map(\.id)) }

    public init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore) {
        self.model = model
        favorites = model.favorites
        super.init(appearance: appearance)
        title = "播放队列"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        categories.selectedSegmentIndex = 0
        categories.addAction(UIAction { [weak self] _ in
            guard let self, let category = MusicReferenceQueuePage(rawValue: self.categories.selectedSegmentIndex)
            else { return }
            self.select(MusicReferenceQueueStrip.tab(category, selected: self.selected,
                                                     history: self.model.playedPlaylists.map(\.id)))
        }, for: .valueChanged)
        view.addSubview(categories)
        categories.translatesAutoresizingMaskIntoConstraints = false
        addChild(pager)
        pager.dataSource = self
        pager.delegate = self
        view.addSubview(pager.view)
        pager.view.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            categories.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 12),
            categories.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -12),
            categories.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 8),
            categories.heightAnchor.constraint(equalToConstant: 36),
            pager.view.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            pager.view.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            pager.view.topAnchor.constraint(equalTo: categories.bottomAnchor, constant: 8),
            pager.view.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        pager.didMove(toParent: self)
        for (index, page) in [current, songs].enumerated() {
            page.table.tag = index
            page.table.delegate = self
            page.table.register(MusicTrackCell.self, forCellReuseIdentifier: MusicTrackCell.reuseIdentifier)
            page.table.register(UITableViewCell.self, forCellReuseIdentifier: "Status")
        }
        current.candidates.tag = 2
        current.candidates.delegate = self
        current.candidates.register(UITableViewCell.self, forCellReuseIdentifier: "Candidate")
        currentDataSource = MusicQueueDataSource(tableView: current.table) { [weak self] view, path, id in
            guard let self, let row = self.entryRows[id] else { return nil }
            let cell = view.dequeueReusableCell(withIdentifier: MusicTrackCell.reuseIdentifier,
                                                for: path) as! MusicTrackCell
            self.configure(cell, track: row.entry.track, active: id == self.model.state.currentId, index: row.index)
            return cell
        }
        currentDataSource.onMove = { [weak self] in self?.moveQueue(from: $0, to: $1) }
        songsDataSource = UITableViewDiffableDataSource(tableView: songs.table) { [weak self] view, path, id in
            guard let self, let row = self.playedRows[id] else { return nil }
            let cell = view.dequeueReusableCell(withIdentifier: MusicTrackCell.reuseIdentifier,
                                                for: path) as! MusicTrackCell
            let active = row.song.displayIdentity?.matches(self.model.current?.displayIdentity) == true
            self.configure(cell, track: row.song.track, active: active, index: row.index)
            return cell
        }
        candidatesDataSource = UITableViewDiffableDataSource(tableView: current.candidates) { [weak self] view, path, id in
            guard let self, let row = self.candidateRows[id] else { return nil }
            let cell = view.dequeueReusableCell(withIdentifier: "Candidate", for: path)
            self.configureCandidate(cell, row: row)
            return cell
        }
        current.mode.addAction(UIAction { [weak self] _ in self?.cycleMode() }, for: .touchUpInside)
        current.continuePlaying.addAction(UIAction { [weak self] _ in
            guard let self, let snapshot = self.similar else { return }
            self.runSimilar(snapshot) {
                await self.model.setSimilarContinue(!snapshot.continueEnabled, snapshotID: snapshot.id)
            }
        }, for: .touchUpInside)
        current.retry.addAction(UIAction { [weak self] _ in
            guard let self, let snapshot = self.similar, snapshot.canRetry else { return }
            self.runSimilar(snapshot) { await self.model.retrySimilarMode(snapshotID: snapshot.id) }
        }, for: .touchUpInside)
        navigationItem.rightBarButtonItem = UIBarButtonItem(
            title: "清空", style: .plain, target: self, action: #selector(clearQueue))
        navigationItem.leftBarButtonItem = editButtonItem
        observe(model.queueChanges) { [weak self] in
            self?.renderQueue()
            self?.renderCandidates()
            self?.renderChrome()
        }
        observe(model.historyChanges) { [weak self] in self?.renderHistory() }
        observe(model.playbackChanges) { [weak self] in
            self?.updateVisiblePlayback()
            self?.renderChrome()
        }
        observe(model.accessChanges) { [weak self] in self?.reconcileAccess() }
        observe(favorites) { [weak self] in
            self?.updateVisiblePlayback()
            if let self { _ = self.showError(self.favorites.error?.localizedDescription) {
                [weak self] in self?.favorites.clearError()
            } }
        }
        pager.setViewControllers([current], direction: .forward, animated: false)
        renderQueue(); renderHistory(); renderCandidates(); renderChrome()
    }

    public override func pageDidBecomeActive() {
        reconcileAccess()
    }

    public override func pageDidBecomeInactive() {
        for history in histories.values { PageViewController.invalidatePages(in: history) }
        histories.removeAll()
    }
    public override func pageDidBecomeVisible() { renderChrome() }
    public override func pageDidFinishAppearing() { renderChrome() }

    public override func viewDidDisappear(_ animated: Bool) {
        super.viewDidDisappear(animated)
        if navigationController?.isBeingDismissed == true { onDismiss?() }
    }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        categories.setTitleTextAttributes([.foregroundColor: UIColor(theme.ink)], for: .normal)
        updateVisiblePlayback(force: true)
        for path in current.candidates.indexPathsForVisibleRows ?? [] {
            if let id = candidatesDataSource?.itemIdentifier(for: path), let row = candidateRows[id],
               let cell = current.candidates.cellForRow(at: path) { configureCandidate(cell, row: row) }
        }
        renderChrome()
    }

    private func configure(_ cell: MusicTrackCell, track: MusicRemoteTrack, active: Bool, index: Int) {
        cell.configure(track: track, active: active, theme: theme, index: index)
        cell.accessoryType = favorites.isFavorite(track) ? .checkmark : .none
        cell.accessibilityTraits = active ? [.button, .selected] : .button
    }

    private func configureCandidate(_ cell: UITableViewCell,
                                    row: (candidate: MusicSimilarModeCandidate, index: Int)) {
        var content = cell.defaultContentConfiguration()
        content.text = "\(row.index + 1). \(row.candidate.song.title)"
        content.secondaryText = row.candidate.song.artist
        content.textProperties.color = UIColor(theme.ink)
        content.secondaryTextProperties.color = UIColor(theme.muted)
        cell.contentConfiguration = content
        cell.backgroundColor = .clear
    }

    public func tableView(_ tableView: UITableView, viewForHeaderInSection section: Int) -> UIView? {
        guard tableView.tag == 1 else { return nil }
        let dates = songsDataSource.snapshot().sectionIdentifiers
        guard dates.indices.contains(section) else { return nil }
        let header = UITableViewHeaderFooterView(reuseIdentifier: nil)
        var content = header.defaultContentConfiguration()
        content.text = dates[section]; content.textProperties.font = .systemFont(ofSize: 12)
        content.textProperties.color = UIColor(theme.muted)
        header.contentConfiguration = content
        return header
    }
    public func tableView(_ tableView: UITableView, heightForHeaderInSection section: Int) -> CGFloat {
        tableView.tag == 1 ? 28 : .leastNormalMagnitude
    }

    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        if tableView.tag == 0, let intent = captureQueueIntent() {
            guard let id = currentDataSource.itemIdentifier(for: indexPath) else { return }
            runQueue(.select(id), intent: intent, startPlayback: true)
        } else if tableView.tag == 1 {
            guard let id = songsDataSource.itemIdentifier(for: indexPath), let row = playedRows[id]?.song else { return }
            let access = model.accountAccessGeneration
            runPageTask { [weak self] in
                guard let self, self.selected == "songs", self.model.accountAccessGeneration == access else { return }
                await self.model.playHistorySong(row)
            }
        } else if let snapshot = similar {
            guard let id = candidatesDataSource.itemIdentifier(for: indexPath) else { return }
            runSimilar(snapshot) { await self.model.playSimilarCandidate(snapshotID: snapshot.id, candidateID: id) }
        }
    }

    public func tableView(_ tableView: UITableView,
                          trailingSwipeActionsConfigurationForRowAt indexPath: IndexPath)
        -> UISwipeActionsConfiguration? {
        guard tableView.tag != 2 else { return nil }
        let track: MusicRemoteTrack
        if tableView.tag == 0, let id = currentDataSource.itemIdentifier(for: indexPath),
           let row = entryRows[id] { track = row.entry.track }
        else if let id = songsDataSource.itemIdentifier(for: indexPath), let row = playedRows[id] { track = row.song.track }
        else { return nil }
        let access = model.accountAccessGeneration
        let like = UIContextualAction(style: .normal, title: favorites.isFavorite(track) ? "取消收藏" : "收藏") {
            [weak self] _, _, finish in
            guard let self, self.model.accountAccessGeneration == access else { finish(false); return }
            self.favorites.toggle(track)
            finish(true)
        }
        var actions = [like]
        if tableView.tag == 0, let intent = captureQueueIntent() {
            guard let id = currentDataSource.itemIdentifier(for: indexPath) else { return nil }
            actions.insert(UIContextualAction(style: .destructive, title: "移除") { [weak self] _, _, finish in
                self?.runQueue(.remove(id), intent: intent)
                finish(true)
            }, at: 0)
        }
        return UISwipeActionsConfiguration(actions: actions)
    }

    public func tableView(_ tableView: UITableView, shouldIndentWhileEditingRowAt indexPath: IndexPath) -> Bool {
        false
    }

    public func tableView(_ tableView: UITableView, editingStyleForRowAt indexPath: IndexPath)
        -> UITableViewCell.EditingStyle { .none }

    public override func setEditing(_ editing: Bool, animated: Bool) {
        super.setEditing(editing, animated: animated)
        current.table.setEditing(editing, animated: animated)
        if !editing { renderQueue() }
    }

    private func moveQueue(from: IndexPath, to: IndexPath) {
        guard let intent = captureQueueIntent(), intent.ids.indices.contains(from.row),
              intent.ids.indices.contains(to.row), pendingReorder == nil else { renderQueue(force: true); return }
        let destination = to.row > from.row ? to.row + 1 : to.row
        guard let order = intent.moving(IndexSet(integer: from.row), to: destination) else { return }
        let own = UUID()
        pendingReorder = own
        entries = order.compactMap { entryRows[$0]?.entry }
        applyQueueRows()
        runPageTask { [weak self] in
            guard let self else { return }
            defer {
                if self.pendingReorder == own {
                    self.pendingReorder = nil
                    if self.isPageActive { self.renderQueue(force: true); self.renderChrome() }
                }
            }
            guard !Task.isCancelled, self.selected == "current", self.pendingReorder == own else { return }
            await self.model.changeDisplayedQueue(.reorder(order), intent: intent)
        }
    }

    public func pageViewController(_ pageViewController: UIPageViewController,
                                   viewControllerBefore page: UIViewController) -> UIViewController? {
        neighbor(page, step: -1)
    }

    public func pageViewController(_ pageViewController: UIPageViewController,
                                   viewControllerAfter page: UIViewController) -> UIViewController? {
        neighbor(page, step: 1)
    }

    public func pageViewController(_ pageViewController: UIPageViewController,
                                   willTransitionTo pendingViewControllers: [UIViewController]) { paging = true }

    public func pageViewController(_ pageViewController: UIPageViewController, didFinishAnimating finished: Bool,
                                   previousViewControllers: [UIViewController], transitionCompleted completed: Bool) {
        paging = false
        if completed, let shown = pager.viewControllers?.first, let id = id(of: shown) { selected = id }
        renderChrome()
        if let pendingSelection { self.pendingSelection = nil; select(pendingSelection) }
    }

    private func reconcileAccess() {
        guard isPageActive else { return }
        pendingReorder = nil
        renderQueue(force: true); renderHistory(); renderCandidates(); renderChrome()
        guard model.isAvailable, !model.isClosed else { historyAccess = nil; return }
        let access = model.accountAccessGeneration
        guard historyAccess != access else { return }
        historyAccess = access
        let model = model
        runPageTask { [weak self, model] in
            guard !Task.isCancelled, self?.historyAccess == access else { return }
            await model.loadHistory()
            if !Task.isCancelled, self?.historyAccess == access, model.accountAccessGeneration == access {
                await model.loadSimilarMode(preview: true)
            }
        }
    }

    private func renderQueue(force: Bool = false) {
        guard currentDataSource != nil, force || pendingReorder == nil else { return }
        entries = model.isAvailable && !model.isClosed ? model.queue : []
        displayed = .init(access: model.accountAccessGeneration, revision: model.state.revision,
                          ids: entries.map(\.id))
        applyQueueRows()
        updateVisiblePlayback()
    }

    private func applyQueueRows() {
        let previous = entryRows
        entryRows = Dictionary(uniqueKeysWithValues: entries.enumerated().map {
            ($0.element.id, (entry: $0.element, index: $0.offset))
        })
        var snapshot = NSDiffableDataSourceSnapshot<Int, String>()
        snapshot.appendSections([0]); snapshot.appendItems(entries.map(\.id))
        let changed = snapshot.itemIdentifiers.filter {
            guard let old = previous[$0], let row = entryRows[$0] else { return false }
            return old.entry != row.entry || old.index != row.index
        }
        snapshot.reconfigureItems(changed)
        if currentDataSource.snapshot().itemIdentifiers != snapshot.itemIdentifiers || !changed.isEmpty {
            currentDataSource.apply(snapshot, animatingDifferences: false)
        }
    }

    private func renderHistory() {
        guard songsDataSource != nil else { return }
        let previous = playedRows
        playedRows = [:]
        var dates: [String] = [], groups: [String: [MusicPlayedSong]] = [:]
        for row in model.isAvailable && !model.isClosed ? model.playedSongs : [] {
            if groups[row.date] == nil { dates.append(row.date) }
            groups[row.date, default: []].append(row)
        }
        var snapshot = NSDiffableDataSourceSnapshot<String, String>()
        snapshot.appendSections(dates)
        for date in dates {
            let songs = groups[date] ?? []
            for (index, song) in songs.enumerated() { playedRows[song.id] = (song, index) }
            snapshot.appendItems(songs.map(\.id), toSection: date)
        }
        let changed = snapshot.itemIdentifiers.filter {
            guard let old = previous[$0], let row = playedRows[$0] else { return false }
            return old.song.track != row.song.track || old.song.displayIdentity != row.song.displayIdentity ||
                old.index != row.index
        }
        snapshot.reconfigureItems(changed)
        let old = songsDataSource.snapshot()
        if old.sectionIdentifiers != dates || old.itemIdentifiers != snapshot.itemIdentifiers || !changed.isEmpty {
            songsDataSource.apply(snapshot, animatingDifferences: false)
        }
        songs.setEmpty(playedRows.isEmpty ? "还没有播放记录" : nil, theme: theme)
        let live = Set(model.playedPlaylists.map(\.id))
        for (id, history) in histories where !live.contains(id) { PageViewController.invalidatePages(in: history) }
        histories = histories.filter { live.contains($0.key) }
        for summary in model.playedPlaylists { histories[summary.id]?.update(summary: summary) }
        renderChrome()
    }

    private func renderCandidates() {
        guard candidatesDataSource != nil else { return }
        let previous = candidateRows
        similar = model.isAvailable && !model.isClosed ? model.similarMode : nil
        let values = similar?.active == false ? similar?.candidates ?? [] : []
        candidateRows = Dictionary(uniqueKeysWithValues: values.enumerated().map {
            ($0.element.id, (candidate: $0.element, index: $0.offset))
        })
        var snapshot = NSDiffableDataSourceSnapshot<Int, String>()
        snapshot.appendSections([0]); snapshot.appendItems(values.map(\.id))
        let changed = snapshot.itemIdentifiers.filter {
            guard let old = previous[$0], let row = candidateRows[$0] else { return false }
            return old.candidate.song != row.candidate.song || old.index != row.index
        }
        snapshot.reconfigureItems(changed)
        if candidatesDataSource.snapshot().itemIdentifiers != snapshot.itemIdentifiers || !changed.isEmpty {
            candidatesDataSource.apply(snapshot, animatingDifferences: false)
        }
    }

    private func updateVisiblePlayback(force: Bool = false) {
        guard currentDataSource != nil, songsDataSource != nil else { return }
        for table in [current.table, songs.table] {
            for path in table.indexPathsForVisibleRows ?? [] {
                let track: MusicRemoteTrack, active: Bool, index: Int
                if table === current.table, let id = currentDataSource.itemIdentifier(for: path),
                   let row = entryRows[id] {
                    track = row.entry.track; active = id == model.state.currentId; index = row.index
                } else if let id = songsDataSource.itemIdentifier(for: path), let row = playedRows[id] {
                    track = row.song.track; active = row.song.displayIdentity?.matches(model.current?.displayIdentity) == true
                    index = row.index
                } else { continue }
                guard let cell = table.cellForRow(at: path) as? MusicTrackCell else { continue }
                if force || cell.accessibilityTraits.contains(.selected) != active {
                    configure(cell, track: track, active: active, index: index)
                } else { cell.accessoryType = favorites.isFavorite(track) ? .checkmark : .none }
            }
        }
    }

    private func renderChrome() {
        guard currentDataSource != nil else { return }
        current.configure(snapshot: similar, queueCount: entries.count, modeTitle: model.state.mode.title,
                          notice: model.queueNotice, theme: theme)
        current.retry.isEnabled = similar?.canRetry == true && !model.isChangingSimilarMode
        if !paging {
            let next = MusicReferenceQueueStrip.reconciled(selected, history: model.playedPlaylists.map(\.id))
            if next != selected { select(next, animated: false) }
        }
        categories.selectedSegmentIndex = MusicReferenceQueueStrip.category(selected).rawValue
        navigationItem.rightBarButtonItem = selected == "current" ? UIBarButtonItem(
            title: "清空", style: .plain, target: self, action: #selector(clearQueue)) : nil
        navigationItem.leftBarButtonItem = selected == "current" ? editButtonItem : nil
        if let error = similar?.error, similar?.status == .error {
            let message = error.localizedDescription
            if shownSimilarError != message, showError(message) { shownSimilarError = message }
        } else { shownSimilarError = nil }
    }

    private func captureQueueIntent() -> MusicReferenceQueueIntent? {
        let ids = entries.map(\.id)
        guard isPageActive, model.isAvailable, !model.isClosed, pendingReorder == nil,
              displayed?.access == model.accountAccessGeneration, entries == model.queue else { return nil }
        return .init(access: model.accountAccessGeneration, revision: model.state.revision, ids: ids)
    }

    @objc private func clearQueue() {
        guard let intent = captureQueueIntent(), !intent.ids.isEmpty else { return }
        let alert = UIAlertController(title: "停止播放并清空队列？", message: nil, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "清空", style: .destructive) { [weak self] _ in
            self?.runQueue(.clear, intent: intent)
        })
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        present(alert, animated: true)
    }

    private func runQueue(_ change: MusicQueueChange, intent: MusicReferenceQueueIntent, startPlayback: Bool = false) {
        runPageTask { [weak self] in
            guard let self, self.selected == "current" else { return }
            await self.model.changeDisplayedQueue(change, intent: intent, startPlayback: startPlayback)
            if !Task.isCancelled { self.renderQueue(force: true); self.renderChrome() }
        }
    }

    private func runSimilar(_ snapshot: MusicSimilarModeSnapshot, operation: @escaping @MainActor () async -> Void) {
        guard let intent = captureQueueIntent() else { return }
        runPageTask { [weak self] in
            guard let self, self.selected == "current", self.model.similarMode?.id == snapshot.id,
                  !self.model.isChangingSimilarMode,
                  intent.admits(access: self.model.accountAccessGeneration, revision: self.model.state.revision,
                                ids: self.model.queue.map(\.id), permitted: self.isPageActive) else { return }
            await operation()
        }
    }

    private func cycleMode() {
        guard let snapshot = similar, !model.isFMActive,
              let index = MusicPlaybackMode.allCases.firstIndex(of: model.state.mode) else { return }
        let mode = MusicPlaybackMode.allCases[(index + 1) % MusicPlaybackMode.allCases.count]
        let revision = model.state.settingsRevision
        runSimilar(snapshot) {
            if self.model.state.settingsRevision == revision { await self.model.setMode(mode) }
        }
    }

    private func select(_ id: String, animated: Bool = true) {
        if paging { pendingSelection = id; return }
        guard let page = page(id) else { return }
        let forward = (ids.firstIndex(of: id) ?? 0) >= (ids.firstIndex(of: selected) ?? 0)
        selected = id
        paging = animated
        pager.setViewControllers([page], direction: forward ? .forward : .reverse, animated: animated) {
            [weak self] _ in
            guard let self else { return }
            self.paging = false
            self.renderChrome()
            if let pending = self.pendingSelection { self.pendingSelection = nil; self.select(pending) }
        }
        categories.selectedSegmentIndex = MusicReferenceQueueStrip.category(id).rawValue
    }

    private func page(_ id: String) -> UIViewController? {
        if id == "current" { return current }
        if id == "songs" { return songs }
        guard let summary = model.playedPlaylists.first(where: { "history:" + $0.id == id }) else {
            if id == "playlists" {
                emptyHistory.setEmpty("还没有已播歌单", theme: theme)
                return emptyHistory
            }
            return nil
        }
        if let existing = histories[summary.id] { return existing }
        let card = MusicHistoryCardViewController(model: model, summary: summary, appearance: appearance)
        card.isSelectedPage = { [weak self] in self?.selected == id }
        card.lifetimeOwner = self
        histories[summary.id] = card
        return card
    }

    private func id(of page: UIViewController) -> String? {
        if page === current { return "current" }
        if page === songs { return "songs" }
        return histories.first { $0.value === page }.map { "history:" + $0.key } ?? "playlists"
    }

    private func neighbor(_ page: UIViewController, step: Int) -> UIViewController? {
        guard let id = id(of: page), let index = ids.firstIndex(of: id), ids.indices.contains(index + step)
        else { return nil }
        return self.page(ids[index + step])
    }

    isolated deinit {
        for history in histories.values { PageViewController.invalidatePages(in: history) }
    }
}

@MainActor
private final class MusicQueueDataSource: UITableViewDiffableDataSource<Int, String> {
    var onMove: ((IndexPath, IndexPath) -> Void)?
    override func tableView(_ tableView: UITableView, canMoveRowAt indexPath: IndexPath) -> Bool { true }
    override func tableView(_ tableView: UITableView, moveRowAt sourceIndexPath: IndexPath,
                            to destinationIndexPath: IndexPath) { onMove?(sourceIndexPath, destinationIndexPath) }
}

@MainActor
private final class MusicQueueTablePage: UIViewController {
    let table = UITableView(frame: .zero, style: .plain)
    let candidates = UITableView(frame: .zero, style: .plain)
    let mode = UIButton(type: .system), continuePlaying = UIButton(type: .system)
    let retry = UIButton(type: .system)
    private let heading = UILabel(), status = UILabel()
    private var snapshot: MusicSimilarModeSnapshot?
    private var queueCount = 0

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .clear
        for item in [table, candidates, mode, continuePlaying, retry, heading, status] { view.addSubview(item) }
        for list in [table, candidates] { list.backgroundColor = .clear; list.separatorStyle = .none; list.rowHeight = 64 }
        heading.font = .systemFont(ofSize: 12)
        status.font = .systemFont(ofSize: 11)
        status.numberOfLines = 0
        retry.setTitle("重试", for: .normal)
        configure(snapshot: nil, queueCount: 0, modeTitle: "", notice: nil, theme: PageTheme())
    }

    func configure(snapshot: MusicSimilarModeSnapshot?, queueCount: Int, modeTitle: String,
                   notice: String?, theme: PageTheme) {
        loadViewIfNeeded()
        self.snapshot = snapshot; self.queueCount = queueCount
        let presentation = snapshot.map { MusicReferenceSimilarPresentation(snapshot: $0) }
        heading.text = presentation?.showsSection == true ? presentation?.title : nil
        switch snapshot?.status {
        case .error: status.text = nil
        case .empty: status.text = snapshot?.active == true ? "暂无新的相似歌曲" : "暂无有效相似歌曲"
        default: status.text = presentation?.status ?? notice
        }
        retry.isHidden = presentation?.showsRetry != true
        mode.isHidden = snapshot?.active != true
        continuePlaying.isHidden = mode.isHidden
        mode.setTitle(modeTitle, for: .normal)
        continuePlaying.setTitle(snapshot?.continueEnabled == true ? "续播已开启" : "续播已关闭", for: .normal)
        for button in [mode, continuePlaying, retry] { button.tintColor = UIColor(theme.ink) }
        heading.textColor = UIColor(theme.muted); status.textColor = UIColor(theme.muted)
        setEmpty(queueCount == 0 ? "没有正在播放歌曲" : nil, theme: theme)
        view.setNeedsLayout()
    }

    func setEmpty(_ text: String?, theme: PageTheme) {
        loadViewIfNeeded()
        let label = UILabel()
        label.text = text; label.textColor = UIColor(theme.muted); label.textAlignment = .center
        table.backgroundView = text == nil ? nil : label
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        let width = view.bounds.width, height = view.bounds.height
        let hasCandidates = snapshot?.active == false && snapshot?.candidates.isEmpty == false
        let footer: CGFloat = snapshot?.active == true ? 52 : 0
        let information: CGFloat = heading.text == nil && status.text == nil ? 0 : 76
        let available = max(0, height - footer - information)
        let queueHeight = hasCandidates ? CGFloat(MusicReferenceQueueMetrics.previewQueueHeight(
            count: queueCount, availableHeight: Double(available))) : available
        table.frame = CGRect(x: 0, y: 0, width: width, height: queueHeight)
        heading.frame = CGRect(x: 16, y: queueHeight + 8, width: width - 32, height: 22)
        status.frame = CGRect(x: 16, y: queueHeight + 30, width: width - (retry.isHidden ? 32 : 108), height: 42)
        retry.frame = CGRect(x: width - 92, y: queueHeight + 28, width: 76, height: 44)
        candidates.frame = CGRect(x: 0, y: queueHeight + information, width: width,
                                  height: max(0, available - queueHeight))
        mode.frame = CGRect(x: 12, y: height - footer, width: (width - 32) / 2, height: 44)
        continuePlaying.frame = mode.frame.offsetBy(dx: (width - 8) / 2, dy: 0)
    }
}

#endif
