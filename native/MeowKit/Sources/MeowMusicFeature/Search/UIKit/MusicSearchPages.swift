#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
public final class MusicSearchViewController: SearchPageViewController,
    UITableViewDelegate
{
    private enum RowID: Hashable { case history, suggestions, charts }
    private let model: MusicViewModel
    private let appearance: MusicAppearancePreferencesStore
    private weak var router: (any MusicUIRouting)?
    private let history: SearchHistoryStore
    private let table = UITableView(frame: .zero, style: .plain)
    private let historyCell = MusicSearchHistoryCell(style: .default, reuseIdentifier: "history")
    private let suggestionsCell = MusicSearchSuggestionsCell(style: .default, reuseIdentifier: "suggestions")
    private let chartsCell = MusicSearchChartRailCell(style: .default, reuseIdentifier: "charts")
    private let loading = UIActivityIndicatorView(style: .medium)
    private let discoveryPage: MusicSearchDiscoveryModel?
    private var historyExpanded = false
    private var rowHeights: [RowID: CGFloat] = [:]
    private var updatingHeights = false
    private var applyingSnapshot = false, needsRender = false
    private var renderedTheme: PageTheme?
    private var renderedHistory: [String] = [], renderedSuggestions: [String] = []
    private var renderedExpanded = false
    private var renderedCards: [MusicSearchChartCardViewData] = []
    private var shownFailures: [MusicSearchDiscoveryModel.Target: MusicServiceError] = [:]
    private var dataSource: UITableViewDiffableDataSource<Int, RowID>!

    public init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore, router: any MusicUIRouting,
                history: SearchHistoryStore? = nil) {
        self.model = model; self.appearance = appearance; self.router = router
        self.history = history ?? SearchHistoryStore()
        if let provider = model.searchDiscoveryProvider, let browser = model.publicBrowser {
            discoveryPage = MusicSearchDiscoveryModel(
                provider: provider, browser: browser, hotPlayback: model.searchHotPlaybackProvider
            )
        } else { discoveryPage = nil }
        super.init(themeProvider: appearance, query: model.query, initialFocus: true)
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        searchField.placeholder = "搜索歌曲、歌手、歌单"
        onSubmit = { [weak self] in self?.submit($0) }
        table.backgroundColor = .clear
        table.delegate = self
        table.accessibilityIdentifier = "music.search.discovery"
        table.keyboardDismissMode = .onDrag
        table.separatorStyle = .none
        table.sectionHeaderTopPadding = 0
        table.contentInset.top = PageLayoutMetrics.contentGap
        table.estimatedRowHeight = 100
        loading.hidesWhenStopped = true
        loading.accessibilityLabel = "搜索内容加载中"
        table.backgroundView = loading
        dataSource = UITableViewDiffableDataSource(tableView: table) { [weak self] _, _, id in
            guard let self else { return nil }
            switch id {
            case .history: return self.historyCell
            case .suggestions: return self.suggestionsCell
            case .charts: return self.chartsCell
            }
        }
        view.addSubview(table)
        table.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            table.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            table.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            table.topAnchor.constraint(equalTo: contentTopAnchor),
            table.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        observe(history) { [weak self] in self?.render() }
        observe(model.accessChanges) { [weak self] in self?.reconcileAccess() }
        if let discoveryPage {
            observe(discoveryPage) { [weak self] in
                self?.render()
                self?.showDiscoveryError()
            }
        }
        chartsCell.onViewportChanged = { [weak self] in self?.measureMemberDemand() }
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (page: MusicSearchViewController, _: UITraitCollection) in page.render()
        }
        render()
    }

    public override func pageDidBecomeActive() {
        reconcileAccess()
        render()
    }
    public override func pageDidBecomeVisible() {
        discoveryPage?.appear()
        measureMemberDemand()
        showDiscoveryError()
    }
    public override func pageDidFinishAppearing() {
        discoveryPage?.appear()
        measureMemberDemand()
        showDiscoveryError()
    }
    public override func pageDidBecomeHidden() { discoveryPage?.disappear() }

    public override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        updateRowHeights()
        measureMemberDemand()
    }

    public override func pageDidBecomeInactive() {
        discoveryPage?.close()
    }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded, dataSource != nil else { return }
        render()
    }

    public func tableView(_ tableView: UITableView, heightForRowAt indexPath: IndexPath) -> CGFloat {
        switch dataSource.itemIdentifier(for: indexPath) {
        case .history: return historyCell.height(for: tableView.bounds.width)
        case .suggestions: return suggestionsCell.height(for: tableView.bounds.width)
        case .charts: return chartsCell.height(for: tableView.bounds.width)
        case nil: return 0
        }
    }

    public func scrollViewDidScroll(_ scrollView: UIScrollView) { measureMemberDemand() }
    public func scrollViewDidChangeAdjustedContentInset(_ scrollView: UIScrollView) { measureMemberDemand() }

    private func submit(_ keyword: String) {
        guard model.isAvailable, !model.isClosed else { return }
        model.query = keyword
        history.record(keyword)
        query = keyword
        searchField.resignFirstResponder()
        navigationController?.pushViewController(MusicSearchResultsViewController(
            model: model, query: keyword, appearance: appearance, router: router, history: history
        ), animated: true)
    }

    func retryDiscovery() {
        guard let discoveryPage else { return }
        let targets = Set(discoveryPage.failures.map(\.target))
        for target in targets { shownFailures.removeValue(forKey: target) }
        discoveryPage.retry(targets)
    }

    private func reconcileAccess() {
        discoveryPage?.setAccess(available: model.isAvailable && !model.isClosed,
                                 generation: model.accountAccessGeneration)
        shownFailures = shownFailures.filter { target, error in
            discoveryPage?.failures.contains { $0.target == target && $0.error == error } == true
        }
        render()
        if isPageVisible { measureMemberDemand() }
    }

    private func showDiscoveryError() {
        guard canPresentPageAlert, let discoveryPage else { return }
        let failures = discoveryPage.failures.filter { shownFailures[$0.target] != $0.error }
        guard !failures.isEmpty else { return }
        let access = model.accountAccessGeneration, targets = Set(failures.map(\.target))
        let message = failures.map { failure -> String in
            let operation = failure.target == .hot ? "热搜榜读取" : "音乐榜单读取"
            return failure.error.message(operation: operation, platform: MusicPublicBrowsePlatform.netease.title)
        }.joined(separator: "；")
        let alert = UIAlertController(title: "错误", message: message, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "重试", style: .default) { [weak self] _ in
            guard let self, self.isPageActive, self.model.accountAccessGeneration == access else { return }
            for target in targets { self.shownFailures.removeValue(forKey: target) }
            self.discoveryPage?.retry(targets)
        })
        for failure in failures { shownFailures[failure.target] = failure.error }
        present(alert, animated: true)
    }

    private func render() {
        guard dataSource != nil else { return }
        guard !applyingSnapshot else { needsRender = true; return }
        if history.entries.isEmpty { historyExpanded = false }
        let historyIDs = history.entries.map(\.id), suggestions = discoveryPage?.suggestions ?? []
        let suggestionIDs = suggestions.map { Data($0.utf8).base64EncodedString() }
        let themeChanged = renderedTheme != theme
        if themeChanged || historyIDs != renderedHistory || renderedExpanded != historyExpanded {
            historyCell.configure(entries: history.entries, expanded: historyExpanded, theme: theme,
            onKeyword: { [weak self] in self?.submit($0) },
            onToggle: { [weak self] in
                guard let self else { return }
                self.historyExpanded.toggle(); self.render()
            }, onClear: { [weak self] in self?.history.clear() })
            renderedHistory = historyIDs; renderedExpanded = historyExpanded
        }
        if themeChanged || suggestionIDs != renderedSuggestions {
            suggestionsCell.configure(keywords: suggestions, theme: theme,
                onKeyword: { [weak self] in self?.submit($0) },
                onRotate: { [weak self] in self?.discoveryPage?.rotateSuggestions() })
            renderedSuggestions = suggestionIDs
        }
        let cards = chartCards()
        if themeChanged || cards != renderedCards {
            chartsCell.configure(cards: cards, theme: theme,
            onRow: { [weak self] chartID, rowID in
                guard let self else { return }
                if chartID == "search.hot" {
                    if let hot = self.discoveryPage?.hot.first(where: { $0.id == rowID }) { self.submit(hot.keyword) }
                } else { self.play(chartID: chartID, nativeID: rowID) }
            }, onPlay: { [weak self] chartID in
                if chartID == "search.hot" { self?.playHot() }
                else { self?.play(chartID: chartID, nativeID: nil) }
            })
            renderedCards = cards
        }
        renderedTheme = theme
        var snapshot = NSDiffableDataSourceSnapshot<Int, RowID>()
        snapshot.appendSections([0])
        if !history.entries.isEmpty { snapshot.appendItems([.history]) }
        if discoveryPage?.loadedComponents.contains(.hot) == true { snapshot.appendItems([.suggestions]) }
        if !cards.isEmpty { snapshot.appendItems([.charts]) }
        if snapshot.itemIdentifiers.isEmpty, discoveryPage?.loadingComponents.isEmpty == false {
            loading.startAnimating()
        } else { loading.stopAnimating() }
        let previous = dataSource.snapshot()
        if previous.sectionIdentifiers != snapshot.sectionIdentifiers ||
            previous.itemIdentifiers != snapshot.itemIdentifiers {
            applyingSnapshot = true
            dataSource.apply(snapshot, animatingDifferences: false) { [weak self] in
                guard let self else { return }
                self.applyingSnapshot = false
                if self.needsRender {
                    self.needsRender = false
                    self.render()
                } else { self.updateRowHeights(); self.measureMemberDemand() }
            }
        } else { updateRowHeights() }
    }

    private func chartCards() -> [MusicSearchChartCardViewData] {
        guard let discoveryPage else { return [] }
        var cards: [MusicSearchChartCardViewData] = []
        if discoveryPage.loadedComponents.contains(.hot) {
            cards.append(.init(id: "search.hot", title: "热搜榜",
                rows: discoveryPage.hot.map {
                    .init(id: $0.id, rank: $0.rank, title: $0.keyword)
                }, rowsAreMusic: false, showsPlayButton: discoveryPage.hotCanPlay, state: .loaded))
        }
        for chart in discoveryPage.charts {
            let state: MusicSearchChartCardViewData.State
            switch discoveryPage.chartContent[chart.id] ?? .idle {
            case .idle: state = .idle
            case .loading: state = .loading
            case .loaded: state = .loaded
            case .failed: state = .failed
            }
            cards.append(.init(id: chart.id, title: chart.collection.title,
                rows: discoveryPage.members(of: chart.id).map {
                    .init(id: $0.nativeID, rank: $0.sourcePosition + 1, title: $0.track.title)
                }, rowsAreMusic: true, showsPlayButton: true, state: state))
        }
        return cards
    }

    private func updateRowHeights() {
        guard dataSource != nil, !updatingHeights, !applyingSnapshot, table.bounds.width > 0 else { return }
        var heights: [RowID: CGFloat] = [:]
        for row in dataSource.snapshot().itemIdentifiers {
            switch row {
            case .history: heights[row] = historyCell.height(for: table.bounds.width)
            case .suggestions: heights[row] = suggestionsCell.height(for: table.bounds.width)
            case .charts: heights[row] = chartsCell.height(for: table.bounds.width)
            }
        }
        guard heights != rowHeights else { return }
        rowHeights = heights; updatingHeights = true
        UIView.performWithoutAnimation { table.beginUpdates(); table.endUpdates() }
        updatingHeights = false
    }

    private func measureMemberDemand() {
        guard isPageActive, isPageVisible, view.window != nil, chartsCell.superview != nil,
              navigationController == nil || navigationController?.topViewController === self else { return }
        let viewport = table.convert(table.bounds.inset(by: table.adjustedContentInset), to: view)
            .intersection(view.safeAreaLayoutGuide.layoutFrame)
        let ids = chartsCell.visibleMusicChartIDs(in: view.convert(viewport, to: chartsCell))
        discoveryPage?.setVisibleCharts(ids)
    }

    private func play(chartID: String, nativeID: String?) {
        guard let discoveryPage, !discoveryPage.isPreparingPlayback, isPageVisible else { return }
        let access = model.accountAccessGeneration
        runPageTask { [weak self] in
            do {
                let playback = try await discoveryPage.preparePlayback(chartID: chartID, nativeID: nativeID)
                guard let self, !Task.isCancelled, self.isPageActive, self.isPageVisible,
                      self.model.accountAccessGeneration == access else { return }
                await self.model.play(playback.tracks, index: playback.index)
            } catch {
                guard let self, !Task.isCancelled, self.isPageVisible,
                      self.model.accountAccessGeneration == access else { return }
                guard MusicServiceError.sanitized(error) != .cancelled else { return }
                self.model.report(error, epoch: access, operation: "播放榜单预览",
                                  trace: MusicRequestTrace(.playback, platform: .netease))
            }
        }
    }

    private func playHot() {
        guard let discoveryPage, !discoveryPage.isPreparingPlayback, isPageVisible else { return }
        let access = model.accountAccessGeneration
        runPageTask { [weak self] in
            do {
                let playback = try await discoveryPage.prepareHotPlayback()
                guard let self, !Task.isCancelled, self.isPageActive, self.isPageVisible,
                      self.model.accountAccessGeneration == access else { return }
                await self.model.play(playback.tracks, index: playback.index)
            } catch {
                guard let self, !Task.isCancelled, self.isPageVisible,
                      self.model.accountAccessGeneration == access else { return }
                guard MusicServiceError.sanitized(error) != .cancelled else { return }
                self.model.report(error, epoch: access, operation: "播放热搜榜",
                                  trace: MusicRequestTrace(.playback, platform: .netease))
            }
        }
    }

    isolated deinit { discoveryPage?.close() }
}

@MainActor
private final class MusicSearchResultsViewController: SearchPageViewController,
    UIPageViewControllerDataSource, UIPageViewControllerDelegate, UITableViewDelegate
{
    private enum Kind: CaseIterable, Hashable {
        case all, songs, playlists
        var title: String {
            switch self { case .all: return "综合"; case .songs: return "单曲"; case .playlists: return "歌单" }
        }
    }
    private let model: MusicViewModel
    private let appearance: MusicAppearancePreferencesStore
    private weak var router: (any MusicUIRouting)?
    private let preferences: MusicSearchPreferencesStore
    private let history: SearchHistoryStore
    private let results: MusicSearchResultsModel
    private let actions: MusicSearchActions
    private let categories = MusicSearchResultTabsView()
    private let pager = UIPageViewController(transitionStyle: .scroll, navigationOrientation: .horizontal)
    private weak var pagerScrollView: UIScrollView?
    private var pagerOffsetObservation: NSKeyValueObservation?
    private var categoriesHeight: NSLayoutConstraint!
    private var pages: [Kind: MusicSearchTableController] = [:]
    private var selected = Kind.all
    private var paging = false
    private var pagingOriginIndex: Int?
    private var pagingTargetIndex: Int?
    private var pendingKind: Kind?
    private var songRows: [Data: (song: MusicSearchSong, index: Int)] = [:]
    private var renderedSongs: [MusicSearchSong] = []
    private var renderedPlaylists: [MusicPublicBrowseCollection] = []
    private var renderedRequest: UUID?
    private var shownFailures: [Int: String] = [:]
    private var submittedKeyword: String
    private var kinds: [Kind] { results.preferences.searchPlaylists ? Kind.allCases : [.songs] }

    init(model: MusicViewModel, query: String, appearance: MusicAppearancePreferencesStore,
         router: (any MusicUIRouting)?, history: SearchHistoryStore) {
        submittedKeyword = query.trimmingCharacters(in: .whitespacesAndNewlines)
        self.model = model; self.appearance = appearance; self.router = router
        self.history = history
        preferences = MusicSearchPreferencesStore()
        actions = MusicSearchActions(model: model)
        results = MusicSearchResultsModel(readSongs: { [weak model] platform, query, page, limit in
            guard let model else { throw MusicServiceError.closed }
            return try await model.searchPage(platform: platform.rawValue, query: query, page: page, limit: limit)
        }, readPlaylists: { [weak model] platform, query, page, limit in
            guard let model else { throw MusicServiceError.closed }
            return try await model.searchPlaylistPage(platform: platform, query: query, page: page, limit: limit)
        }, readRecording: { [weak model] request in
            guard let model else { throw MusicServiceError.closed }
            return try await model.searchRecording(request)
        })
        super.init(themeProvider: appearance, query: query)
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        searchField.placeholder = "搜索歌曲、歌手、歌单"
        onSubmit = { [weak self] _ in self?.submit() }
        categories.onSelect = { [weak self] index in
            guard let self, self.kinds.indices.contains(index) else { return }
            self.select(self.kinds[index], animated: true)
        }
        view.addSubview(categories)
        categories.translatesAutoresizingMaskIntoConstraints = false
        addChild(pager)
        pager.dataSource = self; pager.delegate = self
        view.addSubview(pager.view)
        pager.view.translatesAutoresizingMaskIntoConstraints = false
        categoriesHeight = categories.heightAnchor.constraint(equalToConstant: 48)
        NSLayoutConstraint.activate([
            categories.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 16),
            categories.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -16),
            categories.topAnchor.constraint(equalTo: contentTopAnchor),
            categoriesHeight,
            pager.view.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            pager.view.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            pager.view.topAnchor.constraint(equalTo: categories.bottomAnchor),
            pager.view.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        pager.didMove(toParent: self)
        if let scroll = pager.view.subviews.compactMap({ $0 as? UIScrollView }).first {
            pagerScrollView = scroll
            pagerOffsetObservation = scroll.observe(\.contentOffset, options: [.new]) { [weak self] scroll, _ in
                MainActor.assumeIsolated { self?.updatePagingProgress(scroll) }
            }
        }
        for (index, kind) in Kind.allCases.enumerated() {
            let page = MusicSearchTableController()
            page.table.tag = index
            page.table.accessibilityIdentifier = "music.search.results.page.\(index)"
            page.table.delegate = self
            page.table.delaysContentTouches = false
            page.table.canCancelContentTouches = true
            page.table.backgroundColor = .clear; page.table.separatorStyle = .none
            page.table.sectionHeaderTopPadding = 0
            page.table.estimatedSectionHeaderHeight = 0
            page.table.estimatedSectionFooterHeight = 0
            page.table.register(MusicSearchSongCell.self, forCellReuseIdentifier: MusicSearchSongCell.reuseIdentifier)
            page.table.register(MusicSearchSectionHeaderCell.self,
                                forCellReuseIdentifier: MusicSearchSectionHeaderCell.reuseIdentifier)
            page.table.register(MusicSearchPlaylistCell.self, forCellReuseIdentifier: "Playlists")
            page.table.register(UITableViewCell.self, forCellReuseIdentifier: "Status")
            page.dataSource = UITableViewDiffableDataSource(tableView: page.table) { [weak self] view, path, id in
                self?.makeCell(in: view, at: path, id: id)
            }
            page.onLayout = { [weak self, weak page] in
                guard let self, let page, page.columns != self.playlistColumns(page.table) else { return }
                self.render()
            }
            pages[kind] = page
        }
        observe(results) { [weak self] in self?.render() }
        observe(actions) { [weak self] in
            self?.updateVisibleSongs()
            self?.showActionError()
        }
        observe(model.searchAccessChanges) { [weak self] in self?.reconcileAccess() }
        observe(model.libraryChanges) { [weak self] in self?.actions.refreshFavorites() }
        observe(model.playbackChanges) { [weak self] in self?.updateVisibleSongs() }
        observe(appearance.$preferences.map(\.playlistView).removeDuplicates().dropFirst().eraseToAnyPublisher()) {
            [weak self] _ in self?.render()
        }
        select(selected, animated: false)
    }

    override func pageDidBecomeActive() {
        preferences.reload()
        _ = results.setAccess(available: model.isAvailable && !model.isClosed,
                              generation: model.searchAccessGeneration)
        results.appear(); actions.setActive(true)
        if results.query.isEmpty, model.isAvailable, !model.isClosed, !submittedKeyword.isEmpty {
            results.submit(submittedKeyword, preferences: preferences.preferences)
            results.holdPagination()
        }
        render()
    }

    override func pageDidBecomeInactive() { results.disappear(); actions.suspend() }
    override func pageDidBecomeHidden() {
        results.holdPagination()
        finishPagingTransition(at: selected)
        resetPlaylistPressFeedback()
    }
    override func pageDidBecomeVisible() { showActionError(); showSearchFailure() }
    override func pageDidFinishAppearing() {
        showActionError(); showSearchFailure()
    }

    override func applyTheme() {
        super.applyTheme()
        categories.applyTheme(theme)
        updateVisibleSongs()
        for page in pages.values {
            for path in page.table.indexPathsForVisibleRows ?? [] {
                guard let id = page.dataSource.itemIdentifier(for: path),
                      let cell = page.table.cellForRow(at: path) else { continue }
                if case .playlists = id { configurePlaylistCell(cell, id: id, page: page) }
                else if case .status(let section) = id { configureStatusCell(cell, in: page.table, section: section) }
                else if case .heading(let section) = id,
                        let cell = cell as? MusicSearchSectionHeaderCell {
                    configureHeaderCell(cell, in: page.table, section: section)
                }
            }
        }
    }

    override func shouldBeginInteractiveBack(at point: CGPoint, in view: UIView) -> Bool {
        // UIPageViewController keeps the visible page at the middle offset.
        // Its selected kind, not that implementation offset, owns admission.
        kinds.firstIndex(of: selected) == 0 && !paging
    }

    func pageViewController(_ pageViewController: UIPageViewController, viewControllerBefore page: UIViewController)
        -> UIViewController? { neighbor(page, step: -1) }
    func pageViewController(_ pageViewController: UIPageViewController, viewControllerAfter page: UIViewController)
        -> UIViewController? { neighbor(page, step: 1) }
    func pageViewController(_ pageViewController: UIPageViewController,
                            willTransitionTo pendingViewControllers: [UIViewController]) {
        guard let page = pendingViewControllers.first,
              let kind = kinds.first(where: { pages[$0] === page }) else { return }
        paging = true
        beginPagingTransition(to: kind)
    }
    func pageViewController(_ pageViewController: UIPageViewController, didFinishAnimating finished: Bool,
                            previousViewControllers: [UIViewController], transitionCompleted completed: Bool) {
        if completed, let page = pager.viewControllers?.first,
           let kind = kinds.first(where: { pages[$0] === page }) { selected = kind }
        finishPagingTransition(at: selected)
        paging = false
        if !kinds.contains(selected) { select(.songs, animated: false) }
        showSearchFailure()
        if let pending = pendingKind { pendingKind = nil; select(pending, animated: true) }
    }

    private func beginPagingTransition(to kind: Kind) {
        results.holdPagination()
        resetPlaylistPressFeedback()
        guard let origin = kinds.firstIndex(of: selected), let target = kinds.firstIndex(of: kind) else {
            pagingOriginIndex = nil; pagingTargetIndex = nil
            return
        }
        pagingOriginIndex = origin
        pagingTargetIndex = target
        categories.setSelectedIndex(origin)
        categories.setPosition(CGFloat(origin))
    }

    private func updatePagingProgress(_ scroll: UIScrollView) {
        guard let origin = pagingOriginIndex, let target = pagingTargetIndex,
              scroll.bounds.width > 0 else { return }
        let progress = min(1, max(0, abs(scroll.contentOffset.x - scroll.bounds.width) / scroll.bounds.width))
        categories.setPosition(CGFloat(origin) + CGFloat(target - origin) * progress)
    }

    private func finishPagingTransition(at kind: Kind) {
        pagingOriginIndex = nil
        pagingTargetIndex = nil
        let index = kinds.firstIndex(of: kind) ?? 0
        categories.setSelectedIndex(index)
        categories.setPosition(CGFloat(index))
    }
    func tableView(_ tableView: UITableView, heightForRowAt indexPath: IndexPath) -> CGFloat {
        if case .heading? = rowID(in: tableView, at: indexPath) { return 44 }
        if case .status(let section)? = rowID(in: tableView, at: indexPath) {
            guard let footer = statusView(in: tableView, section: section) else { return .leastNormalMagnitude }
            return max(52, footer.systemLayoutSizeFitting(CGSize(width: tableView.bounds.width, height: 0),
                withHorizontalFittingPriority: .required, verticalFittingPriority: .fittingSizeLevel).height)
        }
        if case .song? = rowID(in: tableView, at: indexPath) { return 72 }
        let columns = playlistColumns(tableView)
        return columns == 1 ? 80 : (tableView.bounds.width - 32 - CGFloat(columns - 1) * 12) /
            CGFloat(columns) + 60
    }
    func tableView(_ tableView: UITableView, estimatedHeightForRowAt indexPath: IndexPath) -> CGFloat {
        self.tableView(tableView, heightForRowAt: indexPath)
    }
    private func makeCell(in tableView: UITableView, at indexPath: IndexPath,
                          id: MusicSearchResultRowID) -> UITableViewCell? {
        if case .heading(let section) = id {
            let cell = tableView.dequeueReusableCell(
                withIdentifier: MusicSearchSectionHeaderCell.reuseIdentifier,
                for: indexPath
            ) as! MusicSearchSectionHeaderCell
            configureHeaderCell(cell, in: tableView, section: section)
            return cell
        }
        if case .song(let id) = id, let row = songRows[id] {
            let cell = tableView.dequeueReusableCell(withIdentifier: MusicSearchSongCell.reuseIdentifier,
                                                     for: indexPath) as! MusicSearchSongCell
            configureSongCell(cell, row: row)
            return cell
        }
        if case .status(let section) = id {
            let cell = tableView.dequeueReusableCell(withIdentifier: "Status", for: indexPath)
            configureStatusCell(cell, in: tableView, section: section)
            return cell
        }
        let cell = tableView.dequeueReusableCell(withIdentifier: "Playlists", for: indexPath) as! MusicSearchPlaylistCell
        if let page = pages[Kind.allCases[tableView.tag]] { configurePlaylistCell(cell, id: id, page: page) }
        return cell
    }

    private func configureStatusCell(_ cell: UITableViewCell, in tableView: UITableView, section: Int) {
        cell.backgroundColor = .clear; cell.selectionStyle = .none
        for view in cell.contentView.subviews { view.removeFromSuperview() }
        if let footer = statusView(in: tableView, section: section) {
            cell.contentView.addSubview(footer); footer.pinEdges(to: cell.contentView)
        }
    }
    func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        if let song = song(in: tableView, at: indexPath) { actions.playSingle(song, session: results) }
    }
    func tableView(_ tableView: UITableView, trailingSwipeActionsConfigurationForRowAt indexPath: IndexPath)
        -> UISwipeActionsConfiguration? {
        guard let song = song(in: tableView, at: indexPath) else { return nil }
        let request = results.requestID
        let favorite = UIContextualAction(style: .normal,
            title: actions.isFavorite(song) ? "取消收藏" : "收藏") { [weak self] _, _, finish in
            guard let self, self.results.requestID == request else { finish(false); return }
            self.actions.toggleFavorite(song) { try await self.results.refreshTrack(song) }
            finish(true)
        }
        favorite.backgroundColor = UIColor(theme.accent)
        return UISwipeActionsConfiguration(actions: [favorite])
    }
    func tableView(_ tableView: UITableView, contextMenuConfigurationForRowAt indexPath: IndexPath,
                   point: CGPoint) -> UIContextMenuConfiguration? {
        guard let song = song(in: tableView, at: indexPath) else { return nil }
        let request = results.requestID
        return UIContextMenuConfiguration(identifier: nil, previewProvider: nil) { [weak self] _ in
            guard let self, self.results.requestID == request else { return nil }
            return self.songMenu(song, request: request)
        }
    }
    func tableView(_ tableView: UITableView, willDisplay cell: UITableViewCell, forRowAt indexPath: IndexPath) {
        guard isPageVisible, !paging, Kind.allCases[tableView.tag] == selected, selected != .all else { return }
        if case .song? = rowID(in: tableView, at: indexPath), indexPath.row >= results.songs.count - 3 {
            results.more(.songs, automatic: true)
        }
        if case .playlists? = rowID(in: tableView, at: indexPath),
           indexPath.row * playlistColumns(tableView) >= results.playlists.count - 3 {
            results.more(.playlists, automatic: true)
        }
    }
    func tableView(_ tableView: UITableView, didEndDisplaying cell: UITableViewCell, forRowAt indexPath: IndexPath) {
        (cell as? MusicSearchPlaylistCell)?.resetPressFeedback()
    }
    func scrollViewWillBeginDragging(_ scrollView: UIScrollView) {
        if let table = scrollView as? UITableView {
            for cell in table.visibleCells.compactMap({ $0 as? MusicSearchPlaylistCell }) { cell.resetPressFeedback() }
        }
        guard isPageVisible, !paging, scrollView === pages[selected]?.table else { return }
        results.resumePagination()
    }

    private func resetPlaylistPressFeedback() {
        for page in pages.values {
            for cell in page.table.visibleCells.compactMap({ $0 as? MusicSearchPlaylistCell }) {
                cell.resetPressFeedback()
            }
        }
    }

    private func submit() {
        let query = self.query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty, model.isAvailable, !model.isClosed else { return }
        submittedKeyword = query
        preferences.reload()
        history.record(query)
        model.query = query
        actions.suspend()
        results.submit(query, preferences: preferences.preferences)
        results.holdPagination()
        actions.setActive(true)
    }
    private func reconcileAccess() {
        let available = model.isAvailable && !model.isClosed
        let keyword = submittedKeyword
        guard results.setAccess(available: available, generation: model.searchAccessGeneration) else { return }
        if isPageActive, available, !keyword.isEmpty {
            preferences.reload()
            results.submit(keyword, preferences: preferences.preferences)
            results.holdPagination()
        }
        actions.setActive(model.isAvailable && !model.isClosed)
        render()
    }
    private func render() {
        guard isViewLoaded else { return }
        if !paging, !kinds.contains(selected) { select(.songs, animated: false) }
        let titles = kinds.map(\.title)
        categories.setTitles(titles, theme: theme)
        categories.isHidden = titles.count <= 1
        categoriesHeight.constant = categories.isHidden ? 0 : 48
        if !paging {
            let index = kinds.firstIndex(of: selected) ?? 0
            categories.setSelectedIndex(index)
            categories.setPosition(CGFloat(index))
        }
        let requestChanged = renderedRequest != results.requestID
        let songsChanged = requestChanged || renderedSongs != results.songs
        let playlistsChanged = requestChanged || renderedPlaylists != results.playlists
        var changedSongs: Set<Data> = []
        if songsChanged {
            let previousSongs = songRows
            songRows = Dictionary(uniqueKeysWithValues: results.songs.enumerated().map {
                (Data($0.element.id.utf8), (song: $0.element, index: $0.offset))
            })
            changedSongs = Set(songRows.keys.filter {
                guard let previous = previousSongs[$0], let row = songRows[$0] else { return false }
                return requestChanged || previous.song != row.song || previous.index != row.index
            })
            renderedSongs = results.songs
        }
        if playlistsChanged { renderedPlaylists = results.playlists }
        renderedRequest = results.requestID
        for (kind, page) in pages {
            let columns = playlistColumns(page.table)
            let updateSongs = songsChanged && kind != .playlists
            let updatePlaylists = (playlistsChanged || page.columns != columns) && kind != .songs
            var headers: [Int: MusicSearchHeaderState] = [:]
            var footers: [Int: MusicSearchFooterState] = [:]
            for section in 0...1 {
                if let title = headingTitle(kind: kind, section: section) {
                    headers[section] = .init(title: title,
                        showsPlay: section == 0 && !results.songs.isEmpty, request: results.requestID)
                }
                footers[section] = footerState(kind: kind, section: section)
            }
            guard updateSongs || updatePlaylists ||
                    page.headerStates != headers || page.footerStates != footers else { continue }
            let previous = page.dataSource.snapshot()
            var snapshot = previous
            if snapshot.sectionIdentifiers.isEmpty { snapshot.appendSections([0, 1]) }
            var changed: [MusicSearchResultRowID] = []
            if updateSongs {
                let old = snapshot.itemIdentifiers(inSection: 0).filter {
                    if case .song = $0 { return true }; return false
                }
                snapshot.deleteItems(old)
                let songs = kind == .all ? Array(results.songs.prefix(5)) : results.songs
                let ids = songs.map { MusicSearchResultRowID.song(Data($0.id.utf8)) }
                if page.footerStates[0] != nil { snapshot.insertItems(ids, beforeItem: .status(0)) }
                else { snapshot.appendItems(ids, toSection: 0) }
                let oldIDs = Set(old)
                changed += ids.filter {
                    if case .song(let id) = $0 { return oldIDs.contains($0) && changedSongs.contains(id) }
                    return false
                }
            }
            if updatePlaylists {
                let old = snapshot.itemIdentifiers(inSection: 1).filter {
                    if case .playlists = $0 { return true }; return false
                }
                snapshot.deleteItems(old)
                let previousRows = page.playlistRows
                page.playlistRows = [:]
                var ids: [MusicSearchResultRowID] = []
                let count = playlistCount(kind)
                for start in stride(from: 0, to: count, by: columns) {
                    let items = Array(results.playlists[start..<min(start + columns, count)])
                    let id = Data(items[0].id.utf8)
                    page.playlistRows[id] = items
                    ids.append(.playlists(id))
                    if let oldItems = previousRows[id],
                       requestChanged || page.columns != columns || oldItems != items {
                        changed.append(.playlists(id))
                    }
                }
                if page.footerStates[1] != nil { snapshot.insertItems(ids, beforeItem: .status(1)) }
                else { snapshot.appendItems(ids, toSection: 1) }
            }
            for section in 0...1 {
                if headers[section] != page.headerStates[section] {
                    if page.headerStates[section] == nil {
                        if let first = snapshot.itemIdentifiers(inSection: section).first {
                            snapshot.insertItems([.heading(section)], beforeItem: first)
                        } else { snapshot.appendItems([.heading(section)], toSection: section) }
                    } else { changed.append(.heading(section)) }
                }
                if footers[section] != page.footerStates[section] {
                    if footers[section] == nil { snapshot.deleteItems([.status(section)]) }
                    else if page.footerStates[section] == nil {
                        snapshot.appendItems([.status(section)], toSection: section)
                    } else { changed.append(.status(section)) }
                }
            }
            page.columns = columns
            page.headerStates = headers
            page.footerStates = footers
            snapshot.reconfigureItems(changed)
            if previous.sectionIdentifiers != snapshot.sectionIdentifiers ||
                previous.itemIdentifiers != snapshot.itemIdentifiers || !changed.isEmpty {
                page.dataSource.apply(snapshot, animatingDifferences: false)
            }
        }
        showActionError()
        showSearchFailure()
    }

    private func rowID(in table: UITableView, at path: IndexPath) -> MusicSearchResultRowID? {
        pages[Kind.allCases[table.tag]]?.dataSource.itemIdentifier(for: path)
    }

    private func song(in table: UITableView, at path: IndexPath) -> MusicSearchSong? {
        guard case .song(let id)? = rowID(in: table, at: path) else { return nil }
        return songRows[id]?.song
    }

    private func configureSongCell(_ cell: MusicSearchSongCell, row: (song: MusicSearchSong, index: Int)) {
        let active = model.current.map { MusicSearchFusion.sameRecording($0.track, row.song.track) } == true
        let request = results.requestID
        cell.configure(
            song: row.song,
            active: active,
            favorite: actions.isFavorite(row.song),
            enabled: !actions.busy,
            theme: theme,
            onPlay: { [weak self] in
                guard let self, self.results.requestID == request else { return }
                self.actions.playSingle(row.song, session: self.results)
            },
            onFavorite: { [weak self] in
                guard let self, self.results.requestID == request else { return }
                self.actions.toggleFavorite(row.song) { try await self.results.refreshTrack(row.song) }
            },
            onAppend: { [weak self] in
                guard let self, self.results.requestID == request else { return }
                self.actions.perform(song: row.song, session: self.results) { [weak model = self.model] in
                    await model?.append($0)
                }
            },
            menu: songMenu(row.song, request: request)
        )
    }

    private func updateVisibleSongs() {
        for page in pages.values {
            for path in page.table.indexPathsForVisibleRows ?? [] {
                guard case .song(let id)? = page.dataSource.itemIdentifier(for: path), let row = songRows[id],
                      let cell = page.table.cellForRow(at: path) as? MusicSearchSongCell else { continue }
                configureSongCell(cell, row: row)
            }
        }
    }

    private func configurePlaylistCell(_ cell: UITableViewCell, id: MusicSearchResultRowID,
                                       page: MusicSearchTableController) {
        guard case .playlists(let id) = id, let items = page.playlistRows[id],
              let cell = cell as? MusicSearchPlaylistCell else { return }
        let request = results.requestID
        cell.configure(items, columns: page.columns, theme: theme, open: { [weak self] in
            guard let self, self.results.requestID == request else { return }
            self.router?.showPublicCollection($0)
        })
    }

    private func headingTitle(kind: Kind, section: Int) -> String? {
        if section == 0, kind != .playlists { return "单曲" }
        if section == 1, kind == .all { return "歌单" }
        return nil
    }

    private func configureHeaderCell(_ cell: MusicSearchSectionHeaderCell,
                                     in tableView: UITableView, section: Int) {
        let kind = Kind.allCases[tableView.tag]
        guard let title = headingTitle(kind: kind, section: section) else { return }
        let request = results.requestID
        cell.configure(title: title, showsPlay: section == 0 && !results.songs.isEmpty,
                       theme: theme) { [weak self] in
            guard let self, self.results.requestID == request else { return }
            self.actions.playAll(session: self.results)
        }
    }

    private func songMenu(_ song: MusicSearchSong, request: UUID) -> UIMenu {
        var menu: [UIMenuElement] = [
            UIAction(title: "下一首播放") { [weak self] _ in
                guard let self, self.results.requestID == request else { return }
                self.actions.perform(song: song, session: self.results) { [weak model = self.model] in
                    await model?.insertNext($0)
                }
            },
            UIAction(title: "添加到播放队列") { [weak self] _ in
                guard let self, self.results.requestID == request else { return }
                self.actions.perform(song: song, session: self.results) { [weak model = self.model] in
                    await model?.append($0)
                }
            },
            UIAction(title: "音源") { [weak self] _ in
                guard let self, self.results.requestID == request else { return }
                self.showSources(song)
            },
        ]
        let collections = model.collections.filter { $0.kind != "favorites" }.map { target in
            UIAction(title: target.title) { [weak self] _ in
                guard let self, self.results.requestID == request else { return }
                self.actions.perform(song: song, session: self.results) { [weak model = self.model] in
                    await model?.add($0, to: target)
                }
            }
        }
        if !collections.isEmpty { menu.append(UIMenu(title: "添加到歌单", children: collections)) }
        return UIMenu(children: menu)
    }

    private func showActionError() { _ = showError(actions.actionError ?? actions.error?.localizedDescription) }

    private func showSearchFailure(section requested: Int? = nil, force: Bool = false) {
        if results.songFailures.isEmpty { shownFailures[0] = nil }
        if results.playlistFailures.isEmpty { shownFailures[1] = nil }
        let sections = requested.map { [$0] } ?? (selected == .songs ? [0] : selected == .playlists ? [1] : [0, 1])
        guard canPresentPageAlert else { return }
        for section in sections {
            let failures = section == 0 ? results.songFailures : results.playlistFailures
            guard !failures.isEmpty else { continue }
            let message = failures.map { $0.error.message(operation: section == 0 ? "单曲搜索" : "歌单搜索",
                platform: $0.platform.title, diagnosticID: $0.diagnosticID) }.joined(separator: "；")
            guard force || shownFailures[section] != message else { continue }
            let request = results.requestID
            let alert = UIAlertController(title: "错误", message: message, preferredStyle: .alert)
            alert.addAction(UIAlertAction(title: "取消", style: .cancel))
            alert.addAction(UIAlertAction(title: "重试", style: .default) {
                [weak self] _ in
                guard let self, self.results.requestID == request else { return }
                self.shownFailures[section] = nil
                self.results.retry(section == 0 ? .songs : .playlists)
            })
            shownFailures[section] = message
            present(alert, animated: true)
            return
        }
    }
    private func select(_ kind: Kind, animated: Bool) {
        guard let target = kinds.firstIndex(of: kind), let page = pages[kind] else { return }
        if paging { pendingKind = kind; return }
        let origin = selected
        let originIndex = kinds.firstIndex(of: origin) ?? 0
        if origin == kind, pager.viewControllers?.first === page {
            finishPagingTransition(at: kind)
            showSearchFailure()
            return
        }
        let direction: UIPageViewController.NavigationDirection = target >= originIndex ? .forward : .reverse
        guard animated else {
            results.holdPagination()
            selected = kind
            pager.setViewControllers([page], direction: direction, animated: false)
            finishPagingTransition(at: kind)
            showSearchFailure()
            return
        }
        paging = true
        beginPagingTransition(to: kind)
        pager.setViewControllers([page], direction: direction,
                                 animated: !UIAccessibility.isReduceMotionEnabled) { [weak self] finished in
            guard let self else { return }
            self.selected = finished ? kind : origin
            self.finishPagingTransition(at: self.selected)
            self.paging = false
            if !self.kinds.contains(self.selected) { self.select(.songs, animated: false) }
            self.showSearchFailure()
            if let pending = self.pendingKind {
                self.pendingKind = nil
                self.select(pending, animated: true)
            }
        }
        showSearchFailure()
    }
    private func neighbor(_ page: UIViewController, step: Int) -> UIViewController? {
        guard let index = kinds.firstIndex(where: { pages[$0] === page }),
              kinds.indices.contains(index + step) else { return nil }
        return pages[kinds[index + step]]
    }

    private func playlistCount(_ kind: Kind) -> Int {
        kind == .songs ? 0 : kind == .all ? min(5, results.playlists.count) : results.playlists.count
    }
    private func playlistColumns(_ table: UITableView) -> Int {
        guard Kind.allCases[table.tag] == .playlists, appearance.preferences.playlistView == .grid else { return 1 }
        return table.bounds.width - 32 >= 327 ? 3 : 2
    }
    private func showSources(_ song: MusicSearchSong) {
        guard canPresentPageAlert else { return }
        let request = results.requestID
        results.holdPagination()
        let sources = MusicSourcesViewController(model: model, tracks: song.variants,
            appearance: appearance, originIsCurrent: { [weak results] in results?.requestID == request },
            refresh: { [weak results] in
                guard let results, results.requestID == request else { throw MusicServiceError.staleResult }
                return try await results.refreshTrack(song, variant: $0)
            })
        let navigation = PageNavigationController(themeProvider: appearance)
        navigation.setViewControllers([sources], animated: false)
        navigation.modalPresentationStyle = .pageSheet
        navigation.sheetPresentationController?.detents = [.medium(), .large()]
        navigation.sheetPresentationController?.prefersGrabberVisible = true
        present(navigation, animated: true)
    }
    private func statusView(in tableView: UITableView, section: Int) -> UIView? {
        let kind = Kind.allCases[tableView.tag]
        guard let state = footerState(kind: kind, section: section) else { return nil }
        let footer = UIStackView()
        footer.axis = .vertical; footer.spacing = 8
        if let message = state.message {
            let text = UILabel(); text.text = message; text.numberOfLines = 0
            text.textColor = UIColor(theme.muted); text.font = .systemFont(ofSize: 13)
            text.textAlignment = .center
            footer.addArrangedSubview(text)
        }
        if let label = state.label, let action = state.action {
            let button = UIButton(type: .system)
            button.setTitle(label, for: .normal); button.tintColor = UIColor(theme.ink)
            button.titleLabel?.font = .systemFont(ofSize: 14, weight: action == .category ? .medium : .regular)
            button.accessibilityIdentifier = "music.search.results.action.\(tableView.tag).\(section)"
            if action == .category {
                button.backgroundColor = UIColor(theme.chip)
                button.layer.cornerRadius = 10
                button.contentEdgeInsets = UIEdgeInsets(top: 9, left: 16, bottom: 9, right: 16)
                button.heightAnchor.constraint(greaterThanOrEqualToConstant: 40).isActive = true
            }
            button.addAction(UIAction { [weak self] _ in
                guard let self, self.isPageActive, self.results.requestID == state.request else { return }
                let type: MusicSearchSection = section == 0 ? .songs : .playlists
                switch action {
                case .failure: self.showSearchFailure(section: section, force: true)
                case .retry: self.results.retry(type)
                case .cancel: self.results.cancel(type)
                case .more: self.results.more(type)
                case .category: self.select(section == 0 ? .songs : .playlists, animated: true)
                }
            }, for: .touchUpInside)
            footer.addArrangedSubview(button)
        }
        footer.isLayoutMarginsRelativeArrangement = true
        footer.layoutMargins = UIEdgeInsets(top: 8, left: 16, bottom: 16, right: 16)
        return footer
    }

    private func footerState(kind: Kind, section: Int) -> MusicSearchFooterState? {
        guard section == 0 ? kind != .playlists : kind != .songs else { return nil }
        let loading = section == 0 ? results.songsLoading : results.playlistsLoading
        let failures = section == 0 ? results.songFailures : results.playlistFailures
        let cancelled = section == 0 ? results.songsCancelled : results.playlistsCancelled
        let noPlatforms = section == 0 ? results.songPlatformsEmpty : results.playlistPlatformsEmpty
        let loaded = section == 0 ? results.songsLoaded : results.playlistsLoaded
        let empty = section == 0 ? results.songs.isEmpty : results.playlists.isEmpty
        var message: String?
        var label: String?
        var action: MusicSearchFooterState.Action?
        if noPlatforms { message = "尚未启用搜索平台" }
        else if !failures.isEmpty {
            label = "重试"
            action = .failure
        } else if cancelled {
            message = "已取消"; label = "重试"; action = .retry
        } else if loading {
            message = section == 0 ? "单曲加载中" : "歌单加载中"
            label = "取消"; action = .cancel
        } else if loaded && empty { message = section == 0 ? "未找到歌曲" : "未找到歌单" }
        else if kind == .all, (section == 0 ? results.songs.count : results.playlists.count) > 5 {
            label = "查看更多"; action = .category
        } else if section == 0 ? results.moreSongs : results.morePlaylists {
            label = "加载更多"; action = .more
        }
        guard message != nil || label != nil else { return nil }
        return .init(message: message, label: label, action: action, request: results.requestID)
    }
    func tableView(_ tableView: UITableView, heightForFooterInSection section: Int) -> CGFloat { .leastNormalMagnitude }
}

@MainActor
final class MusicSearchPlaylistCell: UITableViewCell {
    private var tiles: [MusicBrowseCell] = []
    private var columns = 1
    private var items: [MusicPublicBrowseCollection] = []
    private var open: ((MusicPublicBrowseCollection) -> Void)?
    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        backgroundColor = .clear; selectionStyle = .none
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    override func prepareForReuse() {
        super.prepareForReuse()
        resetPressFeedback()
    }
    func resetPressFeedback() {
        for tile in tiles { tile.resetPressFeedback() }
    }
    func configure(_ items: [MusicPublicBrowseCollection], columns: Int, theme: PageTheme,
                   open: @escaping (MusicPublicBrowseCollection) -> Void) {
        self.columns = columns
        self.open = open
        let changed = self.items.map(\.id) != items.map(\.id)
        self.items = items
        if changed {
            for tile in tiles { tile.removeFromSuperview() }
            tiles = items.enumerated().map { index, _ in
                let tile = MusicBrowseCell(frame: .zero)
                tile.setOpenAction { [weak self] in
                    guard let self, self.items.indices.contains(index) else { return }
                    self.open?(self.items[index])
                }
                contentView.addSubview(tile)
                return tile
            }
        }
        for (tile, item) in zip(tiles, items) {
            tile.configure(item: .init(id: item.id, title: item.title,
                subtitle: [item.count.map { "\($0)首" }, item.creator].compactMap { $0 }.joined(separator: " · "),
                artwork: item.artwork, content: .collection(item)),
                layout: columns == 1 ? .list : .grid, theme: theme,
                showsPlayButton: false, onPlay: {})
        }
        setNeedsLayout()
    }
    override func layoutSubviews() {
        super.layoutSubviews()
        let width = (bounds.width - 32 - CGFloat(columns - 1) * 12) / CGFloat(columns)
        for (index, tile) in tiles.enumerated() {
            tile.frame = CGRect(x: 16 + CGFloat(index) * (width + 12), y: 0,
                                width: width, height: bounds.height)
        }
    }
}

@MainActor
private final class MusicSearchTableController: UIViewController {
    let table = MusicSearchResultsTableView(frame: .zero, style: .plain)
    var dataSource: UITableViewDiffableDataSource<Int, MusicSearchResultRowID>!
    var playlistRows: [Data: [MusicPublicBrowseCollection]] = [:]
    var headerStates: [Int: MusicSearchHeaderState] = [:]
    var footerStates: [Int: MusicSearchFooterState] = [:]
    var columns = 1
    var onLayout: (() -> Void)?
    override func loadView() { view = table }
    override func viewDidLayoutSubviews() { super.viewDidLayoutSubviews(); onLayout?() }
}

private enum MusicSearchResultRowID: Hashable { case heading(Int), song(Data), playlists(Data), status(Int) }

private struct MusicSearchHeaderState: Equatable {
    let title: String
    let showsPlay: Bool
    let request: UUID
}

private struct MusicSearchFooterState: Equatable {
    enum Action: Equatable { case failure, retry, cancel, more, category }
    let message: String?
    let label: String?
    let action: Action?
    let request: UUID
}

#endif
