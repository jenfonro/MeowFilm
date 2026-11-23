#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
public final class MusicSearchResultsViewController: SearchPageViewController,
    UIPageViewControllerDataSource, UIPageViewControllerDelegate, UITableViewDelegate
{
    enum Kind: Hashable {
        case all, songs, playlists, additional(String)
        static let music: [Kind] = [.all, .songs, .playlists]
        var isAdditional: Bool { if case .additional = self { return true }; return false }
        var title: String {
            switch self { case .all: return "综合"; case .songs: return "单曲"; case .playlists: return "歌单"; case .additional: return "" }
        }
    }
    let model: MusicViewModel
    let appearance: MusicAppearancePreferencesStore
    weak var router: (any MusicUIRouting)?
    let preferences: MusicSearchPreferencesStore
    let history: SearchHistoryStore
    let additionalCategories: [SearchResultCategory]
    let primaryTarget: SearchTargetOption
    var additionalContents: [String: any SearchResultContent] = [:]
    let results: MusicSearchResultsModel
    let actions: MusicSearchActions
    private let categories = MusicSearchResultTabsView()
    private let pager = UIPageViewController(transitionStyle: .scroll, navigationOrientation: .horizontal)
    private weak var pagerScrollView: UIScrollView?
    private var pagerOffsetObservation: NSKeyValueObservation?
    private var categoriesHeight: NSLayoutConstraint!
    var pages: [Kind: MusicSearchTableController] = [:]
    var selected = Kind.all
    var paging = false
    private var pagingOriginIndex: Int?
    private var pagingTargetIndex: Int?
    private var pendingKind: Kind?
    var songRows: [Data: (song: MusicSearchSong, index: Int)] = [:]
    var renderedSongs: [MusicSearchSong] = []
    var renderedPlaylists: [MusicPublicBrowseCollection] = []
    private var renderedRequest: UUID?
    var submittedKeyword: String
    var kinds: [Kind] {
        if !additionalCategories.isEmpty { return Kind.music + additionalCategories.map { .additional($0.target.id) } }
        return results.preferences.searchPlaylists ? Kind.music : [.songs]
    }
    private func controller(for kind: Kind) -> UIViewController? {
        if case .additional(let id) = kind { return additionalContents[id]?.controller }
        return pages[kind]
    }

    public init(model: MusicViewModel, query: String, appearance: MusicAppearancePreferencesStore,
         router: (any MusicUIRouting)?, history: SearchHistoryStore,
         primaryTarget: SearchTargetOption = .init(id: "music", title: "音乐", placeholder: "搜索歌曲、歌手、歌单"),
         additionalCategories: [SearchResultCategory] = [], initialTargetID: String? = nil) {
        submittedKeyword = query.trimmingCharacters(in: .whitespacesAndNewlines)
        self.model = model; self.appearance = appearance; self.router = router
        self.history = history
        self.additionalCategories = additionalCategories; self.primaryTarget = primaryTarget
        selected = additionalCategories.first(where: { $0.target.id == initialTargetID }).map { .additional($0.target.id) } ?? .all
        preferences = model.searchPreferences
        actions = MusicSearchActions(model: model)
        results = MusicSearchResultsModel(owner: model)
        super.init(themeProvider: appearance, query: query)
        searchTargets = [primaryTarget] + additionalCategories.map(\.target)
        searchTargetID = initialTargetID ?? primaryTarget.id
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        if searchTargets.isEmpty { searchField.placeholder = primaryTarget.placeholder }
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
        additionalContents = Dictionary(
            additionalCategories.map { ($0.target.id, $0.makeContent()) },
            uniquingKeysWith: { first, _ in first }
        )
        for (index, kind) in Kind.music.enumerated() {
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
            page.table.register(MusicSearchStatusCell.self, forCellReuseIdentifier: MusicSearchStatusCell.reuseIdentifier)
            page.dataSource = UITableViewDiffableDataSource(tableView: page.table) { [weak self] view, path, id in
                self?.makeCell(in: view, at: path, id: id)
            }
            page.onLayout = { [weak self, weak page] in
                guard let self, let page, page.columns != self.playlistColumns(page.table) else { return }
                self.render()
            }
            pages[kind] = page
        }
        let resultChanges = Publishers.MergeMany([
            results.$requestID.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            results.$preferences.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            results.$songs.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            results.$playlists.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            results.$songsLoading.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            results.$playlistsLoading.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            results.$songsCancelled.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            results.$playlistsCancelled.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            results.$songsLoaded.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            results.$playlistsLoaded.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            results.$songFailures.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            results.$playlistFailures.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
        ]).eraseToAnyPublisher()
        observe(resultChanges, whenVisible: true) { [weak self] in self?.render() }
        let actionVisuals = Publishers.Merge(
            actions.$favorites.removeDuplicates().dropFirst().map { _ -> Void in },
            actions.$busy.removeDuplicates().dropFirst().map { _ -> Void in }
        ).eraseToAnyPublisher()
        observe(actionVisuals, whenVisible: true) { [weak self] in self?.updateVisibleSongs() }
        let actionErrors = Publishers.Merge(
            actions.$actionError.removeDuplicates().dropFirst().map { _ -> Void in },
            actions.$error.removeDuplicates().dropFirst().map { _ -> Void in }
        ).eraseToAnyPublisher()
        observe(actionErrors, whenVisible: true) { [weak self] in self?.showActionError() }
        observe(model.searchAccessChanges) { [weak self] in self?.reconcileAccess() }
        observe(model.libraryChanges) { [weak self] in
            self?.actions.refreshFavorites()
            if self?.isPageVisible == true { self?.updateVisibleSongs() }
        }
        observe(model.playbackChanges, whenVisible: true) { [weak self] in self?.updateVisibleSongs() }
        observe(appearance.$preferences.map(\.playlistView).removeDuplicates().dropFirst().eraseToAnyPublisher(), whenVisible: true) {
            [weak self] _ in self?.render()
        }
        select(selected, animated: false)
    }

    public override func pageDidBecomeActive() {
        let preferences = self.preferences
        runPageTask { await preferences.reload() }
        _ = results.setAccess(available: model.isAvailable && !model.isClosed,
                              generation: model.searchAccessGeneration)
        actions.setActive(true)
        activateSelectedContent()
        render()
    }

    public override func pageDidBecomeInactive() {
        results.disappear(); actions.suspend()
        for content in additionalContents.values { content.close() }
        updateLoadingSkeletons()
    }
    public override func pageDidBecomeHidden() {
        results.holdPagination()
        finishPagingTransition(at: selected)
        resetPlaylistPressFeedback()
        updatePlaylistDisplayVisibility(visible: false)
    }
    public override func pageDidBecomeVisible() {
        updateLoadingSkeletons()
        updateVisibleSongs()
        updatePlaylistDisplayVisibility(visible: true)
        showActionError(); showSearchFailure()
    }

    public override func applyTheme() {
        super.applyTheme()
        categories.applyTheme(theme)
        for content in additionalContents.values { content.applyTheme(theme) }
        updateVisibleSongs()
        for page in pages.values {
            for path in page.table.indexPathsForVisibleRows ?? [] {
                guard let id = page.dataSource.itemIdentifier(for: path),
                      let cell = page.table.cellForRow(at: path) else { continue }
                if case .playlists = id { configurePlaylistCell(cell, id: id, page: page) }
                else if case .status(let section) = id,
                        let statusCell = cell as? MusicSearchStatusCell {
                    configureStatusCell(statusCell, in: page.table, section: section)
                }
                else if case .heading(let section) = id,
                        let cell = cell as? MusicSearchSectionHeaderCell {
                    configureHeaderCell(cell, in: page.table, section: section)
                }
            }
        }
    }

    public override func shouldBeginInteractiveBack(at point: CGPoint, in view: UIView) -> Bool {
        // UIPageViewController keeps the visible page at the middle offset.
        // Its selected kind, not that implementation offset, owns admission.
        kinds.firstIndex(of: selected) == 0 && !paging
    }

    public func pageViewController(_ pageViewController: UIPageViewController, viewControllerBefore page: UIViewController)
        -> UIViewController? { neighbor(page, step: -1) }
    public func pageViewController(_ pageViewController: UIPageViewController, viewControllerAfter page: UIViewController)
        -> UIViewController? { neighbor(page, step: 1) }
    public func pageViewController(_ pageViewController: UIPageViewController,
                            willTransitionTo pendingViewControllers: [UIViewController]) {
        guard let page = pendingViewControllers.first,
              let kind = kinds.first(where: { controller(for: $0) === page }) else { return }
        paging = true
        beginPagingTransition(to: kind)
    }
    public func pageViewController(_ pageViewController: UIPageViewController, didFinishAnimating finished: Bool,
                            previousViewControllers: [UIViewController], transitionCompleted completed: Bool) {
        if completed, let page = pager.viewControllers?.first,
           let kind = kinds.first(where: { controller(for: $0) === page }) {
            selected = kind
            synchronizeSearchTarget()
        }
        finishPagingTransition(at: selected)
        paging = false
        if let pending = pendingKind { pendingKind = nil; select(pending, animated: true); return }
        if !kinds.contains(selected) { select(.songs, animated: false) }
        else { activateSelectedContent() }
        showSearchFailure()
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
        updateLoadingSkeletons()
        updatePlaylistDisplayVisibility(visible: isPageVisible)
    }
    private func synchronizeSearchTarget() {
        if case .additional(let id) = selected { searchTargetID = id }
        else { searchTargetID = primaryTarget.id }
    }
    private func activateSelectedContent() {
        guard isPageActive, !submittedKeyword.isEmpty else { return }
        for (id, content) in additionalContents {
            if selected != .additional(id) { content.cancel() }
        }
        if case .additional(let id) = selected {
            results.holdPagination()
            additionalContents[id]?.submit(submittedKeyword)
        } else if model.isAvailable, !model.isClosed {
            if !results.query.utf8.elementsEqual(submittedKeyword.utf8) {
                results.disappear()
                results.submit(submittedKeyword, preferences: preferences.preferences)
                results.appear()
                results.holdPagination()
            } else { results.appear() }
        }
    }
    private func updateLoadingSkeletons() {
        for (kind, page) in pages {
            for skeleton in page.skeletons.values {
                skeleton.setDisplayVisible(isPageVisible && selected == kind)
            }
        }
    }
    private func submit() {
        let query = self.query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty else { return }
        let additional = additionalCategories.first { $0.target.id == searchTargetID }
        guard additional != nil || (model.isAvailable && !model.isClosed) else { return }
        if !submittedKeyword.utf8.elementsEqual(query.utf8) {
            results.disappear()
            for content in additionalContents.values { content.cancel() }
        }
        submittedKeyword = query
        Task { await history.record(query) }
        model.query = query
        actions.suspend()
        actions.setActive(true)
        select(additional.map { .additional($0.target.id) } ?? (kinds.contains(.all) ? .all : .songs), animated: false)
    }
    private func reconcileAccess() {
        let available = model.isAvailable && !model.isClosed
        let keyword = submittedKeyword
        guard results.setAccess(available: available, generation: model.searchAccessGeneration) else { return }
        if isPageActive, available, !selected.isAdditional, !keyword.isEmpty {
            results.submit(keyword, preferences: preferences.preferences)
            results.holdPagination()
        }
        actions.setActive(model.isAvailable && !model.isClosed)
        render()
    }
    private func render() {
        guard isViewLoaded else { return }
        if !paging, !kinds.contains(selected) { select(.songs, animated: false) }
        let titles = kinds.map { kind -> String in
            if case .additional(let id) = kind { return additionalCategories.first { $0.target.id == id }?.target.title ?? "" }
            return kind.title
        }
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
            songRows = Dictionary(
                results.songs.enumerated().map {
                    (Data($0.element.id.utf8), (song: $0.element, index: $0.offset))
                },
                uniquingKeysWith: { first, _ in first }
            )
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
            let previousItems = Set(previous.itemIdentifiers)
            var changed: [MusicSearchResultRowID] = []
            if updatePlaylists {
                let previousRows = page.playlistRows
                page.playlistRows = [:]
                page.playlistRowIDs = []
                let count = playlistCount(kind)
                for start in stride(from: 0, to: count, by: columns) {
                    let items = Array(results.playlists[start..<min(start + columns, count)])
                    let id = Data(items[0].id.utf8)
                    guard page.playlistRows[id] == nil else { continue }
                    page.playlistRows[id] = items
                    page.playlistRowIDs.append(id)
                    if let oldItems = previousRows[id],
                       requestChanged || page.columns != columns || oldItems != items {
                        changed.append(.playlists(id))
                    }
                }
            }
            var builder = DiffableSnapshotBuilder<Int, MusicSearchResultRowID>()
            builder.appendSection(0)
            if headers[0] != nil { builder.appendItems([.heading(0)], toSection: 0) }
            if kind != .playlists {
                let songs = kind == .all ? Array(results.songs.prefix(5)) : results.songs
                let songIDs = songs.map { MusicSearchResultRowID.song(Data($0.id.utf8)) }
                let admitted = builder.appendItems(songIDs, toSection: 0)
                if updateSongs {
                    changed += admitted.filter {
                        if case .song(let id) = $0 { return previousItems.contains($0) && changedSongs.contains(id) }
                        return false
                    }
                }
            }
            if footers[0] != nil { builder.appendItems([.status(0)], toSection: 0) }
            builder.appendSection(1)
            if headers[1] != nil { builder.appendItems([.heading(1)], toSection: 1) }
            if kind != .songs {
                let playlistIDs = page.playlistRowIDs.map(MusicSearchResultRowID.playlists)
                _ = builder.appendItems(playlistIDs, toSection: 1)
            }
            if footers[1] != nil { builder.appendItems([.status(1)], toSection: 1) }
            for section in 0...1 {
                if headers[section] != nil, page.headerStates[section] != nil,
                   headers[section] != page.headerStates[section] {
                    changed.append(.heading(section))
                }
                if footers[section] != nil, page.footerStates[section] != nil,
                   footers[section] != page.footerStates[section] {
                    changed.append(.status(section))
                }
            }
            page.columns = columns
            page.headerStates = headers
            page.footerStates = footers
            let reconfigured = changed.filter { previousItems.contains($0) }
            let snapshot = builder.build(reconfiguring: reconfigured)
            if previous.sectionIdentifiers != snapshot.sectionIdentifiers ||
                previous.itemIdentifiers != snapshot.itemIdentifiers || !reconfigured.isEmpty {
                page.dataSource.apply(snapshot, animatingDifferences: false)
            }
        }
        showActionError()
        showSearchFailure()
    }

    func select(_ kind: Kind, animated: Bool) {
        guard let target = kinds.firstIndex(of: kind), let page = controller(for: kind) else { return }
        if paging { pendingKind = kind; return }
        let origin = selected
        let originIndex = kinds.firstIndex(of: origin) ?? 0
        if origin == kind, pager.viewControllers?.first === page {
            synchronizeSearchTarget()
            activateSelectedContent()
            finishPagingTransition(at: kind)
            showSearchFailure()
            return
        }
        let direction: UIPageViewController.NavigationDirection = target >= originIndex ? .forward : .reverse
        guard animated else {
            results.holdPagination()
            selected = kind
            synchronizeSearchTarget()
            pager.setViewControllers([page], direction: direction, animated: false)
            finishPagingTransition(at: kind)
            activateSelectedContent()
            showSearchFailure()
            return
        }
        paging = true
        beginPagingTransition(to: kind)
        pager.setViewControllers([page], direction: direction,
                                 animated: !UIAccessibility.isReduceMotionEnabled) { [weak self] finished in
            guard let self else { return }
            self.selected = finished ? kind : origin
            if finished { self.synchronizeSearchTarget() }
            self.finishPagingTransition(at: self.selected)
            self.paging = false
            if let pending = self.pendingKind {
                self.pendingKind = nil
                self.select(pending, animated: true)
                return
            }
            if !self.kinds.contains(self.selected) { self.select(.songs, animated: false) }
            else { self.activateSelectedContent() }
            self.showSearchFailure()
        }
        showSearchFailure()
    }
    private func neighbor(_ page: UIViewController, step: Int) -> UIViewController? {
        guard let index = kinds.firstIndex(where: { controller(for: $0) === page }),
              kinds.indices.contains(index + step) else { return nil }
        return controller(for: kinds[index + step])
    }

}
#endif
