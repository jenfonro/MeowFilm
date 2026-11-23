#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
public final class MusicSearchViewController: SearchPageViewController,
    UITableViewDelegate
{
    private enum RowID: Hashable, Sendable { case history, suggestions, charts }
    let model: MusicViewModel
    let appearance: MusicAppearancePreferencesStore
    weak var router: (any MusicUIRouting)?
    let history: SearchHistoryStore
    let additionalCategories: [SearchResultCategory]
    let primaryTarget: SearchTargetOption
    private let table = UITableView(frame: .zero, style: .plain)
    private let historyCell = MusicSearchHistoryCell(style: .default, reuseIdentifier: "history")
    private let suggestionsCell = MusicSearchSuggestionsCell(style: .default, reuseIdentifier: "suggestions")
    private let chartsCell = MusicSearchChartRailCell(style: .default, reuseIdentifier: "charts")
    private let loading = MusicLoadingSkeletonView()
    let discoveryPage: MusicSearchDiscoveryModel?
    private var historyExpanded = false
    private var rowHeights: [RowID: CGFloat] = [:]
    private var updatingHeights = false
    private var applyingSnapshot = false, needsRender = false
    private var renderedTheme: PageTheme?
    var renderedHistory: [String] = [], renderedSuggestions: [String] = []
    private var renderedExpanded = false
    var renderedCards: [MusicSearchChartCardViewData] = []
    private var dataSource: UITableViewDiffableDataSource<Int, RowID>!

    var isMusicTarget: Bool {
        searchTargetID.isEmpty || searchTargetID == primaryTarget.id
    }

    public override func searchTargetDidChange(from previousTargetID: String) {
        guard isViewLoaded else { return }
        reconcileTarget()
    }

    public init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore, router: any MusicUIRouting,
                history: SearchHistoryStore? = nil,
                primaryTarget: SearchTargetOption = .init(id: "music", title: "音乐", placeholder: "搜索歌曲、歌手、歌单"),
                additionalCategories: [SearchResultCategory] = [], initialTargetID: String? = nil) {
        self.model = model; self.appearance = appearance; self.router = router
        self.history = history ?? model.searchHistory
        self.additionalCategories = additionalCategories; self.primaryTarget = primaryTarget
        if let provider = model.searchDiscoveryProvider, let browser = model.publicBrowser {
            discoveryPage = MusicSearchDiscoveryModel(
                provider: provider, browser: browser, hotPlayback: model.searchHotPlaybackProvider
            )
        } else { discoveryPage = nil }
        super.init(themeProvider: appearance, query: model.query, initialFocus: true)
        searchTargets = [primaryTarget] + additionalCategories.map(\.target)
        searchTargetID = initialTargetID ?? primaryTarget.id
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        if searchTargets.isEmpty { searchField.placeholder = primaryTarget.placeholder }
        onSubmit = { [weak self] in self?.submit($0) }
        table.backgroundColor = .clear
        table.delegate = self
        table.accessibilityIdentifier = "music.search.discovery"
        table.keyboardDismissMode = .onDrag
        table.separatorStyle = .none
        table.sectionHeaderTopPadding = 0
        table.contentInset.top = PageLayoutMetrics.contentGap
        table.estimatedRowHeight = 100
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
        observe(history, whenVisible: true) { [weak self] in self?.render() }
        observe(model.accessChanges) { [weak self] in self?.reconcileAccess() }
        if let discoveryPage {
            let discoveryVisuals = Publishers.MergeMany([
                discoveryPage.$hot.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                discoveryPage.$charts.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                discoveryPage.$chartContent.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                discoveryPage.$loadedComponents.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                discoveryPage.$loadingComponents.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                discoveryPage.$suggestionOffset.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                discoveryPage.$hotCanPlay.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            ]).eraseToAnyPublisher()
            observe(discoveryVisuals, whenVisible: true) { [weak self] in
                self?.render()
                self?.showDiscoveryError()
            }
            observe(discoveryPage.$componentFailures.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(), whenVisible: true) { [weak self] in
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
        if isMusicTarget {
            discoveryPage?.appear()
        } else {
            discoveryPage?.disappear()
        }
        render()
        if isMusicTarget {
            measureMemberDemand()
            showDiscoveryError()
        }
    }
    public override func pageDidFinishAppearing() {
        if isMusicTarget {
            discoveryPage?.appear()
            measureMemberDemand()
        } else {
            discoveryPage?.disappear()
        }
    }
    public override func pageDidBecomeHidden() {
        discoveryPage?.disappear()
        loading.setDisplayVisible(false)
    }

    private func reconcileTarget() {
        if isMusicTarget {
            if isPageVisible {
                discoveryPage?.appear()
            }
            render()
            if isPageVisible {
                measureMemberDemand()
                showDiscoveryError()
            }
        } else {
            discoveryPage?.disappear()
            loading.setDisplayVisible(false)
            render()
        }
    }

    public override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        updateRowHeights()
        measureMemberDemand()
    }

    public override func pageDidBecomeInactive() {
        loading.setDisplayVisible(false)
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

    func submit(_ keyword: String, animated: Bool = true) {
        let additional = additionalCategories.first { $0.target.id == searchTargetID }
        guard additional != nil || (model.isAvailable && !model.isClosed) else { return }
        model.query = keyword
        Task { await history.record(keyword) }
        query = keyword
        searchField.resignFirstResponder()
        navigationController?.pushViewController(MusicSearchResultsViewController(
            model: model, query: keyword, appearance: appearance, router: router, history: history,
            primaryTarget: primaryTarget, additionalCategories: additionalCategories,
            initialTargetID: additional?.target.id ?? primaryTarget.id
        ), animated: animated)
    }

    func retryDiscovery() {
        guard let discoveryPage else { return }
        let targets = Set(discoveryPage.failures.map(\.target))
        clearErrorSource("discovery")
        discoveryPage.retry(targets)
    }

    private func reconcileAccess() {
        discoveryPage?.setAccess(available: model.isAvailable && !model.isClosed,
                                 generation: model.accountAccessGeneration)
        if discoveryPage?.failures.isEmpty != false {
            clearErrorSource("discovery")
        }
        render()
        if isPageVisible && isMusicTarget { measureMemberDemand() }
    }

    private func showDiscoveryError() {
        guard isMusicTarget, let discoveryPage else {
            clearErrorSource("discovery")
            return
        }
        let failures = discoveryPage.failures
        guard !failures.isEmpty else {
            clearErrorSource("discovery")
            return
        }
        let access = model.accountAccessGeneration, targets = Set(failures.map(\.target))
        let message = failures.map { failure -> String in
            let operation = failure.target == .hot ? "热搜榜读取" : "音乐榜单读取"
            return failure.error.message(operation: operation, platform: MusicPublicBrowsePlatform.netease.title)
        }.joined(separator: "；")
        syncErrorSource("discovery", message: message, token: "\(access.uuidString)|\(message)", retry: { [weak self] in
            guard let self, self.isPageActive, self.isMusicTarget, self.model.accountAccessGeneration == access else { return }
            self.discoveryPage?.retry(targets)
        })
    }

    private func render() {
        guard dataSource != nil else { return }
        guard !applyingSnapshot else { needsRender = true; return }
        if history.entries.isEmpty { historyExpanded = false }
        let showsMusicDiscovery = isMusicTarget
        let historyIDs = history.entries.map(\.id)
        let themeChanged = renderedTheme != theme
        if themeChanged || historyIDs != renderedHistory || renderedExpanded != historyExpanded {
            historyCell.configure(entries: history.entries, expanded: historyExpanded, theme: theme,
            onKeyword: { [weak self] in self?.submit($0) },
            onToggle: { [weak self] in
                guard let self else { return }
                self.historyExpanded.toggle(); self.render()
            }, onClear: { [weak self] in Task { await self?.history.clear() } })
            renderedHistory = historyIDs; renderedExpanded = historyExpanded
        }
        let cards: [MusicSearchChartCardViewData]
        if showsMusicDiscovery {
            let suggestions = discoveryPage?.suggestions ?? []
            let suggestionIDs = suggestions.map { Data($0.utf8).base64EncodedString() }
            if themeChanged || suggestionIDs != renderedSuggestions {
                suggestionsCell.configure(keywords: suggestions, theme: theme,
                    onKeyword: { [weak self] in self?.submit($0) },
                    onRotate: { [weak self] in self?.discoveryPage?.rotateSuggestions() })
                renderedSuggestions = suggestionIDs
            }
            cards = chartCards()
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
        } else {
            cards = []
        }
        renderedTheme = theme
        var builder = DiffableSnapshotBuilder<Int, RowID>()
        builder.appendSection(0)
        if !history.entries.isEmpty { builder.appendItems([.history], toSection: 0) }
        if showsMusicDiscovery {
            if discoveryPage?.loadedComponents.contains(.hot) == true { builder.appendItems([.suggestions], toSection: 0) }
            if !cards.isEmpty { builder.appendItems([.charts], toSection: 0) }
        }
        let snapshot = builder.build()
        loading.configure(
            loading: showsMusicDiscovery && snapshot.itemIdentifiers.isEmpty && discoveryPage?.loadingComponents.isEmpty == false,
            visible: isPageVisible && showsMusicDiscovery,
            theme: theme
        )
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
                    .init(id: $0.nativeID, rank: $0.sourcePosition.map { $0 + 1 }, title: $0.track.title)
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
        guard isMusicTarget, isPageActive, isPageVisible, view.window != nil, chartsCell.superview != nil,
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

    nonisolated deinit {
        MainActor.assumeIsolated {
            discoveryPage?.close()
        }
    }
}
#endif
