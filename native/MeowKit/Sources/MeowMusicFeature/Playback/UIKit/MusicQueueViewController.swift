#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class MusicQueueViewController: MusicPageViewController,
    UICollectionViewDelegate, UITableViewDelegate
{
    private let model: MusicViewModel
    private let favorites: MusicMiniPlayerFavorites
    private let sheet = FixedSheetTransition { viewport in
        let metrics = MusicReferenceQueueMetrics(viewportWidth: Double(viewport.width),
                                                viewportHeight: Double(viewport.height))
        return CGSize(width: metrics.width, height: metrics.height)
    }
    private let handle = UIButton(type: .system), grabber = UIView(), categories = UIStackView()
    private let indicator = UIView()
    private lazy var tabs = {
        let theme = self.theme
        return MusicReferenceQueuePage.allCases.map { MusicQueueTabButton(page: $0, theme: theme) }
    }()
    private let clear = UIButton(type: .system), dots = UIPageControl()
    private let pager = MusicQueuePagerViewController()
    private let current = MusicQueueCurrentPage(), songs = MusicQueueTablePage()
    private let emptyHistory = MusicQueueTablePage()
    private var histories: [String: MusicHistoryCardViewController] = [:]
    private var currentDataSource: UICollectionViewDiffableDataSource<Int, String>!
    private var songsDataSource: UITableViewDiffableDataSource<String, String>!
    private var reorder: MusicQueueReorderController!
    private var entries: [MusicQueueEntry] = []
    private var entryRows: [String: (entry: MusicQueueEntry, index: Int)] = [:]
    private var playedRows: [String: (song: MusicPlayedSong, index: Int)] = [:]
    private var displayed: MusicReferenceQueueIntent?
    private var dragIntent: MusicReferenceQueueIntent?
    private var reorderTask: Task<Void, Never>?
    private var historyAccess: UUID?
    private var previewedQueue: MusicReferenceQueueIntent?
    private var previewAccount: UUID?
    private var selected: String { pager.selectedID }

    public init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore) {
        self.model = model
        favorites = model.favorites
        super.init(appearance: appearance)
        modalPresentationStyle = .custom
        transitioningDelegate = sheet
    }

    public override func loadView() { view = UIView() }
    public override var usesPageHeader: Bool { false }

    public override func viewDidLoad() {
        super.viewDidLoad()
        view.layer.cornerRadius = 24
        view.layer.maskedCorners = [.layerMinXMinYCorner, .layerMaxXMinYCorner]
        view.clipsToBounds = true
        view.isOpaque = true
        view.accessibilityIdentifier = "music.queue.sheet"
        grabber.layer.cornerRadius = 2
        handle.addSubview(grabber)
        handle.accessibilityLabel = "关闭播放队列"
        handle.accessibilityTraits = .button
        handle.isAccessibilityElement = true
        handle.addAction(UIAction { [weak self] _ in self?.dismiss(animated: true) }, for: .touchUpInside)
        sheet.install(on: self, dragHandle: handle)
        categories.distribution = .fillEqually
        categories.spacing = 4
        categories.accessibilityIdentifier = "music.queue.tabs"
        indicator.layer.cornerRadius = 12
        indicator.isUserInteractionEnabled = false
        indicator.accessibilityIdentifier = "music.queue.tab.indicator"
        categories.addSubview(indicator)
        for (index, tab) in tabs.enumerated() {
            categories.addArrangedSubview(tab)
            tab.accessibilityIdentifier = "music.queue.tab.\(index)"
            tab.addAction(UIAction { [weak self] _ in
                guard let self, let category = MusicReferenceQueuePage(rawValue: index) else { return }
                self.pager.select(MusicReferenceQueueStrip.tab(category, selected: self.selected,
                                                               history: self.model.playedPlaylists.map(\.id)))
            }, for: .touchUpInside)
        }
        clear.setImage(PageIcon.image(.remove), for: .normal)
        clear.accessibilityIdentifier = "music.queue.clear"
        clear.addAction(UIAction { [weak self] _ in self?.clearSelectedHistory() }, for: .touchUpInside)
        dots.addAction(UIAction { [weak self] _ in
            guard let self, self.model.playedPlaylists.indices.contains(self.dots.currentPage) else { return }
            self.pager.select("history:" + self.model.playedPlaylists[self.dots.currentPage].id)
        }, for: .valueChanged)
        pager.makePage = { [weak self] in self?.page($0) }
        pager.onCategoryPosition = { [weak self] in self?.positionIndicator($0) }
        pager.onSelect = { [weak self] in
            guard let self else { return }
            self.renderChrome()
            if self.selected.hasPrefix("history:") {
                self.histories[String(self.selected.dropFirst("history:".count))]?.selectionDidChange()
            }
        }
        addChild(pager)
        for item in [handle, categories, clear, pager.view!, dots] { view.addSubview(item) }
        pager.didMove(toParent: self)
        current.loadViewIfNeeded()
        current.collection.delegate = self
        current.collection.register(MusicQueueCell.self, forCellWithReuseIdentifier: MusicQueueCell.reuseIdentifier)
        songs.loadViewIfNeeded()
        songs.table.delegate = self
        songs.table.register(MusicQueueSongCell.self, forCellReuseIdentifier: MusicQueueSongCell.reuseIdentifier)
        currentDataSource = UICollectionViewDiffableDataSource(collectionView: current.collection) {
            [weak self] view, path, id in
            guard let self, let row = self.entryRows[id] else { return nil }
            let cell = view.dequeueReusableCell(withReuseIdentifier: MusicQueueCell.reuseIdentifier,
                                                for: path) as! MusicQueueCell
            self.configure(cell, row: row)
            return cell
        }
        currentDataSource.reorderingHandlers.canReorderItem = { [weak self] _ in
            self?.selected == "current" && self?.captureQueueIntent() != nil
        }
        reorder = MusicQueueReorderController(collection: current.collection, pager: pager.scroll)
        reorder.onBegin = { [weak self] in
            guard let self, self.selected == "current", let intent = self.captureQueueIntent() else { return false }
            self.dragIntent = intent
            return true
        }
        reorder.onCancel = { [weak self] in self?.dragIntent = nil; self?.renderQueue(force: true) }
        reorder.onEnd = { [weak self] in
            guard let self else { return }
            let intent = self.dragIntent
            self.dragIntent = nil
            // Native movement has finished updating its diffable snapshot.
            if let intent { self.commitReorder(self.currentDataSource.snapshot().itemIdentifiers, intent: intent) }
            else { self.renderQueue(force: true) }
        }
        songsDataSource = UITableViewDiffableDataSource(tableView: songs.table) { [weak self] view, path, id in
            guard let self, let row = self.playedRows[id] else { return nil }
            let cell = view.dequeueReusableCell(withIdentifier: MusicQueueSongCell.reuseIdentifier,
                                                for: path) as! MusicQueueSongCell
            self.configure(cell, row: row)
            return cell
        }
        current.mode.addAction(UIAction { [weak self] _ in self?.cycleMode() }, for: .touchUpInside)
        current.retry.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            if self.model.similarMode?.error == .commitUnconfirmed ||
                self.model.queueRecommendations?.error == .commitUnconfirmed ||
                self.model.fmPlayback?.error == .commitUnconfirmed {
                self.readbackRecommendations()
            } else if let snapshot = self.model.similarMode, snapshot.canRetry {
                self.runSimilar(snapshot) { await self.model.retrySimilarMode(snapshotID: snapshot.id) }
            } else if let snapshot = self.model.queueRecommendations, snapshot.canRetry {
                self.runQueueRecommendation(snapshot) { await self.model.retryQueueRecommendations(snapshotID: snapshot.id) }
            }
        }, for: .touchUpInside)
        current.returnToQueue.addAction(UIAction { [weak self] _ in
            guard let self, self.isPageActive, self.selected == "current" else { return }
            let account = self.model.searchAccessGeneration, snapshot = self.model.similarMode?.id
            self.runPageTask { [weak self] in
                guard let self, self.model.searchAccessGeneration == account,
                      self.model.similarMode?.id == snapshot || self.model.isChangingSimilarMode else { return }
                await self.model.exitSimilarMode()
            }
        }, for: .touchUpInside)
        current.onCandidate = { [weak self] snapshotID, candidateID in
            guard let self, let snapshot = self.model.queueRecommendations, snapshot.id == snapshotID else { return }
            self.runQueueRecommendation(snapshot) {
                await self.model.playQueueRecommendation(snapshotID: snapshotID, candidateID: candidateID)
            }
        }
        observe(model.queueChanges) { [weak self] in
            self?.renderQueue(); self?.renderChrome(); self?.previewRecommendationsIfNeeded()
        }
        observe(model.historyChanges) { [weak self] in self?.renderHistory() }
        observe(model.searchAccessChanges) { [weak self] in self?.reconcileAccess() }
        observe(favorites) { [weak self] in self?.updateVisiblePlayback(); self?.showFavoriteError() }
        renderQueue(); renderHistory(); renderChrome()
    }

    public override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        let viewport = view.window?.bounds.size ?? view.bounds.size
        let metrics = MusicReferenceQueueMetrics(viewportWidth: Double(viewport.width),
                                                viewportHeight: Double(viewport.height))
        let width = view.bounds.width, padding = CGFloat(metrics.horizontalPadding)
        let handleHeight = CGFloat(metrics.handleHeight)
        handle.frame = CGRect(x: 0, y: 0, width: width, height: handleHeight)
        grabber.frame = CGRect(x: (width - 32) / 2, y: (handleHeight - 4) / 2, width: 32, height: 4)
        for tab in tabs { tab.setFontSize(CGFloat(metrics.tabFontSize)) }
        let tabWidth = tabs.map { max(0, $0.intrinsicContentSize.width) }.max() ?? 0
        let preferred = tabWidth * CGFloat(tabs.count) + categories.spacing * CGFloat(tabs.count - 1)
        categories.frame = CGRect(x: padding + 2, y: handleHeight,
            width: min(preferred, max(0, width - padding * 2 - 4 - 48)), height: 44)
        categories.layoutIfNeeded()
        positionIndicator(pager.categoryPosition)
        clear.frame = CGRect(x: width - padding - 44, y: handleHeight, width: 44, height: 44)
        let top = handleHeight + 44 + CGFloat(metrics.headingBottomPadding)
        let bottom = CGFloat(MusicReferenceQueueMetrics.bottomPadding(safeArea: Double(view.safeAreaInsets.bottom)))
        let dotHeight: CGFloat = dots.isHidden ? 0 : 32
        let height = max(0, view.bounds.height - top - bottom - dotHeight)
        pager.view.frame = CGRect(x: padding, y: top, width: max(0, width - padding * 2), height: height)
        dots.frame = CGRect(x: padding, y: top + height, width: width - padding * 2, height: dotHeight)
    }

    public override func pageDidBecomeActive() { reconcileAccess() }
    public override func pageDidBecomeHidden() { reorder?.cancel(); sheet.cancelInteraction(); dragIntent = nil }
    public override func pageDidBecomeInactive() {
        reorder?.cancel()
        dragIntent = nil
        histories.removeAll()
        model.cancelQueueRecommendationPreview()
    }
    public override func pageDidFinishAppearing() { showFavoriteError() }
    public override func viewWillTransition(to size: CGSize, with coordinator: UIViewControllerTransitionCoordinator) {
        reorder?.cancel(); sheet.cancelInteraction()
        super.viewWillTransition(to: size, with: coordinator)
    }
    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        view.backgroundColor = UIColor(theme.surface)
        grabber.backgroundColor = UIColor(theme.border)
        indicator.backgroundColor = UIColor(theme.chip)
        clear.tintColor = UIColor(theme.muted)
        dots.pageIndicatorTintColor = UIColor(theme.border)
        dots.currentPageIndicatorTintColor = UIColor(theme.accent)
        updateVisiblePlayback()
        renderChrome()
    }

    private func configure(_ cell: MusicQueueCell, row: (entry: MusicQueueEntry, index: Int)) {
        let track = row.entry.track, active = row.entry.id == model.state.currentId
        cell.row.configure(title: track.title, artist: track.artist, index: row.index, active: active,
                           favorite: favorites.isFavorite(track), removable: true, reorderable: true, theme: theme)
        let access = model.accountAccessGeneration
        cell.row.onFavorite = { [weak self] in
            guard let self, self.isPageActive, self.model.accountAccessGeneration == access else { return }
            self.favorites.toggle(track)
        }
        cell.row.onRemove = { [weak self] in
            guard let self, let intent = self.captureQueueIntent() else { return }
            self.runQueue(.remove(row.entry.id), intent: intent)
        }
        cell.accessibilityLabel = [track.title, track.artist].filter { !$0.isEmpty }.joined(separator: "，")
        cell.accessibilityTraits = active ? [.button, .selected] : .button
        cell.accessibilityCustomActions = [
            UIAccessibilityCustomAction(name: favorites.isFavorite(track) ? "取消收藏" : "收藏") {
                [weak cell] _ in cell?.row.onFavorite?(); return true
            },
            UIAccessibilityCustomAction(name: "移出队列") { [weak cell] _ in cell?.row.onRemove?(); return true },
            UIAccessibilityCustomAction(name: "上移") { [weak self] _ in self?.move(row.entry.id, step: -1) ?? false },
            UIAccessibilityCustomAction(name: "下移") { [weak self] _ in self?.move(row.entry.id, step: 1) ?? false },
        ]
    }

    private func configure(_ cell: MusicQueueSongCell, row: (song: MusicPlayedSong, index: Int)) {
        let track = row.song.track
        let active = row.song.displayIdentity?.matches(model.current?.displayIdentity) == true
        cell.row.configure(title: track.title, artist: track.artist, index: row.index, active: active,
                           favorite: favorites.isFavorite(track), removable: false, reorderable: false, theme: theme)
        let access = model.accountAccessGeneration
        cell.row.onFavorite = { [weak self] in
            guard let self, self.isPageActive, self.model.accountAccessGeneration == access else { return }
            self.favorites.toggle(track)
        }
        cell.accessibilityLabel = [track.title, track.artist].filter { !$0.isEmpty }.joined(separator: "，")
        cell.accessibilityTraits = active ? [.button, .selected] : .button
        cell.accessibilityCustomActions = [
            UIAccessibilityCustomAction(name: favorites.isFavorite(track) ? "取消收藏" : "收藏") {
                [weak cell] _ in cell?.row.onFavorite?(); return true
            },
        ]
    }

    public func collectionView(_ collectionView: UICollectionView, shouldSelectItemAt indexPath: IndexPath) -> Bool {
        selected == "current" && reorder?.isActive != true && reorderTask == nil
    }
    public func collectionView(_ collectionView: UICollectionView, didSelectItemAt indexPath: IndexPath) {
        collectionView.deselectItem(at: indexPath, animated: false)
        guard let intent = captureQueueIntent(), let id = currentDataSource.itemIdentifier(for: indexPath) else { return }
        runQueue(.select(id), intent: intent, startPlayback: true)
    }
    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: false)
        guard let id = songsDataSource.itemIdentifier(for: indexPath), let row = playedRows[id]?.song else { return }
        let access = model.accountAccessGeneration
        runPageTask { [weak self] in
            guard let self, self.selected == "songs", self.model.accountAccessGeneration == access else { return }
            await self.model.playHistorySong(row)
        }
    }
    public func tableView(_ tableView: UITableView, viewForHeaderInSection section: Int) -> UIView? {
        let dates = songsDataSource.snapshot().sectionIdentifiers
        guard dates.indices.contains(section) else { return nil }
        let header = UITableViewHeaderFooterView(reuseIdentifier: nil)
        var content = header.defaultContentConfiguration()
        content.text = dates[section]; content.textProperties.font = .systemFont(ofSize: 12)
        content.textProperties.color = UIColor(theme.muted)
        header.contentConfiguration = content
        return header
    }
    public func tableView(_ tableView: UITableView, heightForHeaderInSection section: Int) -> CGFloat { 28 }

    private func reconcileAccess() {
        guard isPageActive else { return }
        reorder?.cancel(); dragIntent = nil
        renderQueue(force: true); renderHistory()
        guard model.isAvailable, !model.isClosed else { historyAccess = nil; return }
        previewRecommendationsIfNeeded()
        let access = model.accountAccessGeneration
        guard historyAccess != access else { return }
        historyAccess = access
        runPageTask { [weak self, model] in
            guard !Task.isCancelled, self?.historyAccess == access else { return }
            await model.loadHistory()
        }
    }

    private func previewRecommendationsIfNeeded() {
        guard isPageActive, model.isAvailable, !model.isClosed, !model.isChangingQueue, !model.isNavigating,
              !model.isChangingSimilarMode, !model.isChangingQueueRecommendations, !model.isFMActive,
              model.similarMode?.active != true, model.current != nil, model.queueRecommendations != nil else { return }
        let intent = MusicReferenceQueueIntent(access: model.accountAccessGeneration,
            revision: model.state.revision, ids: model.queue.map(\.id))
        let account = model.searchAccessGeneration
        guard previewAccount != account || previewedQueue != intent else { return }
        previewedQueue = intent; previewAccount = account
        runPageTask { [weak self] in
            guard let self, self.model.searchAccessGeneration == account,
                  intent.admits(access: self.model.accountAccessGeneration, revision: self.model.state.revision,
                                ids: self.model.queue.map(\.id), permitted: self.isPageActive) else { return }
            await self.model.previewQueueRecommendations()
        }
    }

    private func renderQueue(force: Bool = false) {
        guard currentDataSource != nil else { return }
        if reorder?.isActive == true {
            if dragIntent?.admits(access: model.accountAccessGeneration, revision: model.state.revision,
                                   ids: model.queue.map(\.id), permitted: model.isAvailable && !model.isClosed) == true {
                return
            }
            reorder.cancel()
        }
        guard force || reorderTask == nil else { return }
        entries = model.isAvailable && !model.isClosed ? model.queue : []
        displayed = .init(access: model.accountAccessGeneration, revision: model.state.revision, ids: entries.map(\.id))
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
            let rows = groups[date] ?? []
            for (index, row) in rows.enumerated() { playedRows[row.id] = (row, index) }
            snapshot.appendItems(rows.map(\.id), toSection: date)
        }
        let changed = snapshot.itemIdentifiers.filter {
            guard let old = previous[$0], let row = playedRows[$0] else { return false }
            return old.song.track != row.song.track || old.song.displayIdentity != row.song.displayIdentity || old.index != row.index
        }
        snapshot.reconfigureItems(changed)
        let old = songsDataSource.snapshot()
        if old.sectionIdentifiers != dates || old.itemIdentifiers != snapshot.itemIdentifiers || !changed.isEmpty {
            songsDataSource.apply(snapshot, animatingDifferences: false)
        }
        let live = Set(model.playedPlaylists.map(\.id))
        histories = histories.filter { live.contains($0.key) }
        for summary in model.playedPlaylists { histories[summary.id]?.update(summary: summary) }
        songs.setEmpty(playedRows.isEmpty ? "还没有播放记录" : nil, theme: theme)
        let reconciled = MusicReferenceQueueStrip.reconciled(selected, history: model.playedPlaylists.map(\.id))
        pager.setPages(MusicReferenceQueueStrip.ids(history: model.playedPlaylists.map(\.id)), selected: reconciled)
        renderChrome()
    }

    private func updateVisiblePlayback() {
        guard currentDataSource != nil, songsDataSource != nil else { return }
        for path in current.collection.indexPathsForVisibleItems {
            if let id = currentDataSource.itemIdentifier(for: path), let row = entryRows[id],
               let cell = current.collection.cellForItem(at: path) as? MusicQueueCell { configure(cell, row: row) }
        }
        for path in songs.table.indexPathsForVisibleRows ?? [] {
            if let id = songsDataSource.itemIdentifier(for: path), let row = playedRows[id],
               let cell = songs.table.cellForRow(at: path) as? MusicQueueSongCell { configure(cell, row: row) }
        }
    }

    private func renderChrome() {
        guard currentDataSource != nil else { return }
        let category = MusicReferenceQueueStrip.category(selected)
        let theme = self.theme
        for (index, tab) in tabs.enumerated() {
            let active = index == category.rawValue
            tab.configure(count: index == MusicReferenceQueuePage.current.rawValue ? entries.count : nil,
                          theme: theme)
            tab.accessibilityTraits = active ? [.button, .selected] : .button
        }
        clear.accessibilityLabel = "清空" + category.title
        dots.numberOfPages = model.playedPlaylists.count
        dots.currentPage = model.playedPlaylists.firstIndex(where: { "history:" + $0.id == selected }) ?? 0
        dots.isHidden = category != .playlists || model.playedPlaylists.isEmpty
        current.configure(snapshot: model.similarMode, recommendations: model.queueRecommendations,
                          changing: model.isChangingSimilarMode || model.isChangingQueueRecommendations,
                          returning: model.isChangingSimilarMode, count: entries.count,
                          modeTitle: MusicPlaybackModePresentation(mode: model.state.mode, similar: model.similarMode,
                              recommendations: model.queueRecommendations, fm: model.fmPlayback).title,
                          theme: theme, fm: model.fmPlayback)
        emptyHistory.setEmpty("还没有已播歌单", theme: theme)
        positionIndicator(pager.categoryPosition)
        view.setNeedsLayout()
    }

    private func positionIndicator(_ position: CGFloat) {
        guard let interval = MusicPagingTabTransition.interval(position: position, count: tabs.count) else { return }
        let position = CGFloat(interval.lower) + interval.progress
        UIView.performWithoutAnimation {
            for (index, tab) in tabs.enumerated() {
                tab.applySelection(MusicPagingTabTransition.selectionAmount(position: position, index: index))
            }
            guard let first = tabs.first, first.bounds.width > 0 else { return }
            let x = MusicPagingTabTransition.interpolate(
                tabs[interval.lower].frame.minX,
                tabs[interval.upper].frame.minX,
                progress: interval.progress
            )
            indicator.frame = CGRect(x: x, y: 6, width: first.bounds.width, height: 32)
        }
    }

    private func captureQueueIntent() -> MusicReferenceQueueIntent? {
        guard isPageActive, selected == "current", model.isAvailable, !model.isClosed, reorderTask == nil,
              displayed?.access == model.accountAccessGeneration, entries == model.queue else { return nil }
        return .init(access: model.accountAccessGeneration, revision: model.state.revision, ids: entries.map(\.id))
    }

    private func move(_ id: String, step: Int) -> Bool {
        guard let intent = captureQueueIntent(), let index = intent.ids.firstIndex(of: id),
              intent.ids.indices.contains(index + step) else { return false }
        let destination = index + step + (step > 0 ? 1 : 0)
        guard let order = intent.moving(IndexSet(integer: index), to: destination) else { return false }
        commitReorder(order, intent: intent)
        return true
    }

    private func commitReorder(_ order: [String], intent: MusicReferenceQueueIntent) {
        guard order != intent.ids, order.count == intent.ids.count, Set(order) == Set(intent.ids),
              intent.admits(access: model.accountAccessGeneration, revision: model.state.revision,
                            ids: model.queue.map(\.id), permitted: isPageActive && selected == "current"),
              reorderTask == nil else { renderQueue(force: true); return }
        entries = order.compactMap { entryRows[$0]?.entry }
        applyQueueRows()
        updateVisiblePlayback()
        // An accepted drop belongs to the shared model. Closing the sheet
        // releases its UI, not an already submitted local queue transaction.
        reorderTask = Task { [weak self, model] in
            defer {
                if let self {
                    self.reorderTask = nil
                    if self.isPageActive { self.renderQueue(force: true); self.renderChrome() }
                }
            }
            await model.changeDisplayedQueue(.reorder(order), intent: intent)
        }
    }

    private func clearQueue() {
        guard canPresentPageAlert, let intent = captureQueueIntent(), !intent.ids.isEmpty else { return }
        let alert = UIAlertController(title: "停止播放并清空队列？", message: nil, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "清空", style: .destructive) { [weak self] _ in
            self?.runQueue(.clear, intent: intent)
        })
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        present(alert, animated: true)
    }

    private func clearSelectedHistory() {
        switch MusicReferenceQueueStrip.category(selected) {
        case .current: clearQueue()
        case .songs: confirmClearHistory(.songs)
        case .playlists: confirmClearHistory(.playlists)
        }
    }

    private func confirmClearHistory(_ category: MusicHistoryCategory) {
        guard canPresentPageAlert, model.isAvailable, !model.isClosed,
              !(category == .songs ? model.playedSongs.isEmpty : model.playedPlaylists.isEmpty) else { return }
        let page = MusicReferenceQueueStrip.category(selected), access = model.accountAccessGeneration
        let alert = UIAlertController(title: "清空\(page.title)？", message: nil, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "清空", style: .destructive) { [weak self] _ in
            self?.runPageTask {
                guard let self, self.model.accountAccessGeneration == access,
                      MusicReferenceQueueStrip.category(self.selected) == page else { return }
                await self.model.clearHistory(category)
            }
        })
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        present(alert, animated: true)
    }

    private func runQueue(_ change: MusicQueueChange, intent: MusicReferenceQueueIntent, startPlayback: Bool = false) {
        runPageTask { [weak self] in
            guard let self, self.selected == "current" else { return }
            await self.model.changeDisplayedQueue(change, intent: intent, startPlayback: startPlayback)
        }
    }

    private func runSimilar(_ snapshot: MusicSimilarModeSnapshot, operation: @escaping @MainActor () async -> Void) {
        guard let intent = captureQueueIntent() else { return }
        let account = model.searchAccessGeneration, command = model.recommendationCommandID
        runPageTask { [weak self] in
            guard let self, self.selected == "current", self.model.similarMode?.id == snapshot.id,
                  self.model.similarMode?.errorID == snapshot.errorID,
                  self.model.recommendationCommandID == command,
                  !self.model.isChangingSimilarMode, self.model.searchAccessGeneration == account,
                  intent.admits(access: self.model.accountAccessGeneration, revision: self.model.state.revision,
                                ids: self.model.queue.map(\.id), permitted: self.isPageActive) else { return }
            await operation()
        }
    }

    private func runQueueRecommendation(_ snapshot: MusicQueueRecommendationSnapshot,
                                        operation: @escaping @MainActor () async -> Void) {
        guard let intent = captureQueueIntent() else { return }
        let account = model.searchAccessGeneration, command = model.recommendationCommandID
        runPageTask { [weak self] in
            guard let self, self.selected == "current", self.model.queueRecommendations?.id == snapshot.id,
                  self.model.queueRecommendations?.errorID == snapshot.errorID,
                  self.model.recommendationCommandID == command,
                  self.model.searchAccessGeneration == account, !self.model.isChangingQueueRecommendations,
                  !self.model.isChangingSimilarMode,
                  intent.admits(access: self.model.accountAccessGeneration, revision: self.model.state.revision,
                                ids: self.model.queue.map(\.id), permitted: self.isPageActive) else { return }
            await operation()
        }
    }

    private func readbackRecommendations() {
        guard let intent = captureQueueIntent() else { return }
        let account = model.searchAccessGeneration, command = model.recommendationCommandID
        let similarID = model.similarMode?.id, recommendationID = model.queueRecommendations?.id, fmID = model.fmPlayback?.id
        let errorID = model.similarMode?.errorID ?? model.queueRecommendations?.errorID ?? model.fmPlayback?.errorID
        runPageTask { [weak self] in
            guard let self, self.selected == "current", self.model.searchAccessGeneration == account,
                  self.model.recommendationCommandID == command, self.model.similarMode?.id == similarID,
                  self.model.queueRecommendations?.id == recommendationID, self.model.fmPlayback?.id == fmID,
                  (self.model.similarMode?.errorID ?? self.model.queueRecommendations?.errorID ?? self.model.fmPlayback?.errorID) == errorID,
                  intent.admits(access: self.model.accountAccessGeneration, revision: self.model.state.revision,
                                ids: self.model.queue.map(\.id), permitted: self.isPageActive) else { return }
            await self.model.loadSimilarMode()
        }
    }

    private func cycleMode() {
        guard let index = MusicPlaybackMode.allCases.firstIndex(of: model.state.mode) else { return }
        let mode = MusicPlaybackMode.allCases[(index + 1) % MusicPlaybackMode.allCases.count]
        let revision = model.state.settingsRevision
        let operation: @MainActor () async -> Void = {
            if self.model.state.settingsRevision == revision { await self.model.setMode(mode) }
        }
        if let snapshot = model.similarMode, snapshot.active { runSimilar(snapshot, operation: operation) }
        else if let snapshot = model.queueRecommendations { runQueueRecommendation(snapshot, operation: operation) }
        else { runPageTask { await operation() } }
    }

    private func showFavoriteError() {
        _ = showError(favorites.error?.localizedDescription) { [weak self] in self?.favorites.clearError() }
    }


    private func page(_ id: String) -> UIViewController? {
        if id == "current" { return current }
        if id == "songs" { return songs }
        if id == "playlists" { return emptyHistory }
        guard let summary = model.playedPlaylists.first(where: { "history:" + $0.id == id }) else { return nil }
        if let existing = histories[summary.id] { return existing }
        let card = MusicHistoryCardViewController(model: model, summary: summary, appearance: appearance)
        card.isSelectedPage = { [weak self] in self?.selected == id }
        card.lifetimeOwner = self
        histories[summary.id] = card
        return card
    }

}

@MainActor
private final class MusicQueueTabButton: UIButton {
    private let page: MusicReferenceQueuePage, text = UILabel(), badge = UILabel()
    private var count: Int?, fontSize: CGFloat = 14
    private var primary: PageColor, secondary: PageColor
    private var primaryColor: UIColor, secondaryColor: UIColor

    init(page: MusicReferenceQueuePage, theme: PageTheme) {
        self.page = page
        primary = theme.ink; secondary = theme.muted
        primaryColor = UIColor(theme.ink); secondaryColor = UIColor(theme.muted)
        super.init(frame: .zero)
        text.numberOfLines = 1
        text.lineBreakMode = .byClipping
        text.textAlignment = .center
        text.textColor = secondaryColor
        text.isAccessibilityElement = false
        text.accessibilityIdentifier = "music.queue.tab.title.\(page.rawValue)"
        addSubview(text)
        badge.font = UIFont.monospacedDigitSystemFont(ofSize: 8, weight: .regular)
        badge.textColor = secondaryColor
        badge.isAccessibilityElement = false
        badge.accessibilityIdentifier = "music.queue.tab.badge.\(page.rawValue)"
        addSubview(badge)
        isAccessibilityElement = true
        renderTitle()
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(count: Int?, theme: PageTheme) {
        if primary != theme.ink || secondary != theme.muted {
            primary = theme.ink; secondary = theme.muted
            primaryColor = UIColor(theme.ink); secondaryColor = UIColor(theme.muted)
        }
        if self.count != count { self.count = count; renderTitle() }
    }

    func setFontSize(_ size: CGFloat) {
        guard fontSize != size else { return }
        fontSize = size
        renderTitle()
    }

    override var intrinsicContentSize: CGSize {
        let side = max(18, badge.isHidden ? 0 : ceil(badge.intrinsicContentSize.width) + 4)
        return CGSize(width: ceil(text.intrinsicContentSize.width) + side * 2, height: 44)
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let size = text.intrinsicContentSize
        text.bounds = CGRect(x: 0, y: 0, width: min(size.width, max(0, bounds.width - 36)), height: size.height)
        text.center = CGPoint(x: bounds.midX, y: bounds.midY)
        positionBadge()
    }

    func applySelection(_ amount: CGFloat) {
        let scale = (fontSize + 2 * amount) / (fontSize + 2)
        text.transform = CGAffineTransform(scaleX: scale, y: scale)
        text.textColor = amount == 0 ? secondaryColor : amount == 1 ? primaryColor :
            UIColor(secondary.mixed(with: primary, ratio: Double(amount)))
        badge.textColor = text.textColor
        positionBadge()
    }

    private func positionBadge() {
        guard !badge.isHidden else { return }
        let size = badge.intrinsicContentSize, title = text.frame
        badge.frame = CGRect(x: min(title.maxX, bounds.maxX - size.width),
                             y: title.minY - 1, width: size.width, height: size.height)
    }

    private func renderTitle() {
        // Draw at the largest size; paging only scales down and blends text color.
        let typeface = UIFont.systemFont(ofSize: fontSize + 2)
        text.font = typeface
        text.text = page.title
        badge.text = count.map(String.init)
        badge.isHidden = count == nil
        accessibilityLabel = count.map { "\(page.title) \($0)" } ?? page.title
        invalidateIntrinsicContentSize()
        setNeedsLayout()
    }
}
#endif
