#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
public final class MusicSearchViewController: SearchPageViewController,
    UITableViewDelegate
{
    private enum RowID: Hashable { case history(Data), hot(Data), charts }
    private let model: MusicViewModel
    private let appearance: MusicAppearancePreferencesStore
    private weak var router: (any MusicUIRouting)?
    private let history = SearchHistoryStore()
    private let table = UITableView(frame: .zero, style: .plain)
    private var discovery: MusicSearchDiscoverySnapshot?
    private var discoveryLoading = false
    private var discoveryRequest = UUID()
    private var discoveryAccess: UUID?
    private var discoveryTask: Task<Void, Never>?
    private var discoveryError: String?
    private var shownDiscoveryError: String?
    private var dataSource: UITableViewDiffableDataSource<Int, RowID>!

    public init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore, router: any MusicUIRouting) {
        self.model = model; self.appearance = appearance; self.router = router
        super.init(themeProvider: appearance, query: model.query, initialFocus: true)
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        searchField.placeholder = "搜索歌曲、歌手、歌单"
        onSubmit = { [weak self] in self?.submit($0) }
        table.backgroundColor = .clear
        table.delegate = self
        table.separatorStyle = .none
        table.sectionHeaderTopPadding = 0
        table.contentInset.top = PageLayoutMetrics.contentGap
        table.register(UITableViewCell.self, forCellReuseIdentifier: "keyword")
        table.register(MusicSearchChartsCell.self, forCellReuseIdentifier: "charts")
        dataSource = UITableViewDiffableDataSource(tableView: table) { [weak self] view, path, id in
            guard let self else { return nil }
            let cell = view.dequeueReusableCell(withIdentifier: id == .charts ? "charts" : "keyword", for: path)
            self.configure(cell, id: id)
            return cell
        }
        view.addSubview(table); table.pinEdges(to: view)
        observe(history) { [weak self] in self?.render() }
        observe(model.accessChanges) { [weak self] in self?.reconcileAccess() }
        render()
    }

    public override func pageDidBecomeActive() {
        reconcileAccess()
        render()
    }
    public override func pageDidBecomeVisible() { showDiscoveryError() }
    public override func pageDidFinishAppearing() {
        showDiscoveryError()
    }

    public override func pageDidBecomeInactive() {
        discoveryRequest = UUID()
        discoveryTask?.cancel(); discoveryTask = nil
    }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded, dataSource != nil else { return }
        for path in table.indexPathsForVisibleRows ?? [] {
            if let id = dataSource.itemIdentifier(for: path), let cell = table.cellForRow(at: path) {
                configure(cell, id: id)
            }
        }
        for section in 0..<table.numberOfSections {
            guard let header = table.headerView(forSection: section) else { continue }
            var configuration = header.defaultContentConfiguration()
            configuration.text = self.tableView(table, titleForHeaderInSection: section)
            configuration.textProperties.font = .systemFont(ofSize: 17, weight: .semibold)
            configuration.textProperties.color = UIColor(theme.ink)
            header.contentConfiguration = configuration
            for button in header.contentView.subviews.compactMap({ $0 as? UIButton }) {
                button.tintColor = UIColor(theme.muted)
            }
        }
    }

    public func tableView(_ tableView: UITableView, titleForHeaderInSection section: Int) -> String? {
        switch sectionID(section) {
        case 0: return "搜索历史"
        case 1: return "热搜榜"
        case 2: return "音乐榜单"
        default: return nil
        }
    }

    public func tableView(_ tableView: UITableView, heightForRowAt indexPath: IndexPath) -> CGFloat {
        if dataSource.itemIdentifier(for: indexPath) == .charts {
            let count = discovery?.charts.map { min(20, $0.tracks.count) }.max() ?? 0
            return CGFloat(max(288, count * 40 + 60))
        }
        return 42
    }
    public func tableView(_ tableView: UITableView, heightForHeaderInSection section: Int) -> CGFloat { 44 }

    private func configure(_ cell: UITableViewCell, id: RowID) {
        if let cell = cell as? MusicSearchChartsCell {
            cell.configure(discovery?.charts ?? [], theme: theme) { [weak self] chart, index in
                guard let self else { return }
                self.runPageTask { await self.model.play(chart.tracks, index: index) }
            }
            return
        }
        var configuration = cell.defaultContentConfiguration()
        switch id {
        case .history(let id): configuration.text = history.entries.first { Data($0.text.utf8) == id }?.text
        case .hot(let id):
            if let index = discovery?.hot.firstIndex(where: { Data($0.keyword.utf8) == id }),
               let hot = discovery?.hot[index] {
                configuration.text = "\(index + 1). \(hot.keyword)"
            }
        case .charts: break
        }
        configuration.textProperties.color = UIColor(theme.ink)
        configuration.textProperties.font = .systemFont(ofSize: 14)
        cell.contentConfiguration = configuration
        cell.backgroundColor = .clear
    }

    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        switch dataSource.itemIdentifier(for: indexPath) {
        case .history(let id):
            if let entry = history.entries.first(where: { Data($0.text.utf8) == id }) { submit(entry.text) }
        case .hot(let id):
            if let hot = discovery?.hot.first(where: { Data($0.keyword.utf8) == id }) { submit(hot.keyword) }
        default: break
        }
    }

    public func tableView(_ tableView: UITableView, viewForHeaderInSection section: Int) -> UIView? {
        guard let title = self.tableView(tableView, titleForHeaderInSection: section) else { return nil }
        let header = UITableViewHeaderFooterView(reuseIdentifier: nil)
        var configuration = header.defaultContentConfiguration()
        configuration.text = title
        configuration.textProperties.font = .systemFont(ofSize: 17, weight: .semibold)
        configuration.textProperties.color = UIColor(theme.ink)
        header.contentConfiguration = configuration
        if sectionID(section) == 0 {
            let clear = UIButton(type: .system)
            clear.setTitle("清空", for: .normal); clear.tintColor = UIColor(theme.muted)
            clear.frame = CGRect(x: max(0, table.bounds.width - 76), y: 0, width: 60, height: 44)
            clear.autoresizingMask = [.flexibleLeftMargin]
            clear.addAction(UIAction { [weak self] _ in self?.history.clear() }, for: .touchUpInside)
            header.contentView.addSubview(clear)
        }
        return header
    }

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

    private func loadDiscovery(force: Bool = false) {
        guard isPageActive, model.isAvailable, !model.isClosed, !discoveryLoading,
              force || discovery == nil || discovery!.validUntil <= Date(),
              let provider = model.searchDiscoveryProvider else { return }
        let access = model.accountAccessGeneration
        let own = discoveryRequest
        discoveryLoading = true; discoveryError = nil; shownDiscoveryError = nil
        discoveryTask = Task { [weak self] in
            defer {
                if let self, self.discoveryRequest == own {
                    self.discoveryLoading = false; self.discoveryTask = nil
                }
            }
            do {
                let result = try await provider.searchDiscovery()
                guard !Task.isCancelled, let self, self.isPageActive, self.discoveryRequest == own,
                      self.model.isAvailable, !self.model.isClosed,
                      self.model.accountAccessGeneration == access else { return }
                self.discovery = result
                let failures = result.failures.map { failure -> String in
                    let operation: String
                    switch failure.component {
                    case .hot: operation = "热搜榜读取"
                    case .charts: operation = "音乐榜单读取"
                    }
                    return failure.error.message(operation: operation, platform: MusicPublicBrowsePlatform.netease.title)
                }
                self.discoveryError = failures.isEmpty ? nil : failures.joined(separator: "；")
                self.render(discoveryChanged: true)
                self.showDiscoveryError()
            } catch {
                if !Task.isCancelled, let self, self.discoveryRequest == own,
                   self.model.accountAccessGeneration == access {
                    self.discoveryError = MusicServiceError.sanitized(error).localizedDescription
                    self.showDiscoveryError()
                }
            }
        }
    }

    func retryDiscovery() { loadDiscovery(force: true) }

    private func reconcileAccess() {
        guard model.isAvailable, !model.isClosed else {
            discoveryAccess = nil
            discoveryRequest = UUID()
            discoveryTask?.cancel(); discoveryTask = nil; discoveryLoading = false
            discoveryError = nil; shownDiscoveryError = nil
            discovery = nil
            render()
            return
        }
        let access = model.accountAccessGeneration
        if discoveryAccess != access {
            discoveryAccess = access
            discoveryRequest = UUID()
            discoveryTask?.cancel(); discoveryTask = nil; discoveryLoading = false
            discoveryError = nil; shownDiscoveryError = nil
            discovery = nil
            render()
        }
        loadDiscovery()
    }

    private func showDiscoveryError() {
        guard canPresentPageAlert, let discoveryError, shownDiscoveryError != discoveryError else { return }
        let own = discoveryRequest
        let alert = UIAlertController(title: "错误", message: discoveryError, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "重试", style: .default) { [weak self] _ in
            guard let self, self.discoveryRequest == own else { return }
            self.retryDiscovery()
        })
        shownDiscoveryError = discoveryError
        present(alert, animated: true)
    }

    private func render(discoveryChanged: Bool = false) {
        guard dataSource != nil else { return }
        var snapshot = NSDiffableDataSourceSnapshot<Int, RowID>()
        if !history.entries.isEmpty {
            snapshot.appendSections([0])
            snapshot.appendItems(history.entries.map { .history(Data($0.text.utf8)) }, toSection: 0)
        }
        if let discovery, !discovery.hot.isEmpty {
            snapshot.appendSections([1])
            snapshot.appendItems(discovery.hot.prefix(20).map { .hot(Data($0.keyword.utf8)) }, toSection: 1)
        }
        if discovery?.charts.isEmpty == false {
            snapshot.appendSections([2])
            snapshot.appendItems([.charts], toSection: 2)
        }
        let previous = dataSource.snapshot()
        if discoveryChanged {
            snapshot.reconfigureItems(snapshot.itemIdentifiers.filter { previous.itemIdentifiers.contains($0) })
        }
        if previous.sectionIdentifiers != snapshot.sectionIdentifiers ||
            previous.itemIdentifiers != snapshot.itemIdentifiers || discoveryChanged {
            dataSource.apply(snapshot, animatingDifferences: false)
        }
    }

    private func sectionID(_ index: Int) -> Int? {
        let ids = dataSource?.snapshot().sectionIdentifiers ?? []
        return ids.indices.contains(index) ? ids[index] : nil
    }

    isolated deinit { discoveryTask?.cancel() }
}

@MainActor
private final class MusicSearchResultsViewController: SearchPageViewController,
    UIPageViewControllerDataSource, UIPageViewControllerDelegate, UITableViewDelegate
{
    private enum Kind: CaseIterable, Hashable { case all, songs, playlists }
    private let model: MusicViewModel
    private let appearance: MusicAppearancePreferencesStore
    private weak var router: (any MusicUIRouting)?
    private let preferences = MusicSearchPreferencesStore()
    private let history: SearchHistoryStore
    private let results: MusicSearchResultsModel
    private let actions: MusicSearchActions
    private let categories = UISegmentedControl(items: ["综合", "单曲", "歌单"])
    private let pager = UIPageViewController(transitionStyle: .scroll, navigationOrientation: .horizontal)
    private var pages: [Kind: MusicSearchTableController] = [:]
    private var selected = Kind.all
    private var paging = false
    private var pendingKind: Kind?
    private var songRows: [Data: (song: MusicSearchSong, index: Int)] = [:]
    private var shownFailures: [Int: String] = [:]
    private var kinds: [Kind] { results.preferences.searchPlaylists ? Kind.allCases : [.songs] }

    init(model: MusicViewModel, query: String, appearance: MusicAppearancePreferencesStore,
         router: (any MusicUIRouting)?, history: SearchHistoryStore) {
        self.model = model; self.appearance = appearance; self.router = router
        self.history = history
        actions = MusicSearchActions(model: model)
        results = MusicSearchResultsModel(readSongs: { [weak model] platform, query, page in
            guard let model else { throw MusicServiceError.closed }
            return try await model.searchPage(platform: platform.rawValue, query: query, page: page)
        }, readPlaylists: { [weak model] platform, query, cursor in
            guard let model, model.isAvailable, !model.isClosed, let browser = model.publicBrowser else {
                throw MusicServiceError.unavailable
            }
            let access = model.accountAccessGeneration
            let value = try await browser.publicPlaylists(platform: platform, query: query, cursor: cursor)
            guard access == model.accountAccessGeneration else { throw MusicServiceError.staleAccess }
            return value
        })
        super.init(themeProvider: appearance, query: query)
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        searchField.placeholder = "搜索歌曲、歌手、歌单"
        onSubmit = { [weak self] _ in self?.submit() }
        categories.selectedSegmentIndex = 0
        categories.addAction(UIAction { [weak self] _ in
            guard let self, self.kinds.indices.contains(self.categories.selectedSegmentIndex) else { return }
            self.select(self.kinds[self.categories.selectedSegmentIndex], animated: true)
        }, for: .valueChanged)
        view.addSubview(categories)
        categories.translatesAutoresizingMaskIntoConstraints = false
        addChild(pager)
        pager.dataSource = self; pager.delegate = self
        view.addSubview(pager.view)
        pager.view.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            categories.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 16),
            categories.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -16),
            categories.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor,
                                            constant: PageLayoutMetrics.contentGap),
            categories.heightAnchor.constraint(equalToConstant: 36),
            pager.view.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            pager.view.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            pager.view.topAnchor.constraint(equalTo: categories.bottomAnchor, constant: 8),
            pager.view.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        pager.didMove(toParent: self)
        for (index, kind) in Kind.allCases.enumerated() {
            let page = MusicSearchTableController()
            page.table.tag = index
            page.table.delegate = self
            page.table.backgroundColor = .clear; page.table.separatorStyle = .none
            page.table.sectionHeaderTopPadding = 0
            page.table.register(MusicTrackCell.self, forCellReuseIdentifier: MusicTrackCell.reuseIdentifier)
            page.table.register(MusicCollectionCell.self, forCellReuseIdentifier: MusicCollectionCell.reuseIdentifier)
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
        observe(model.accessChanges) { [weak self] in self?.reconcileAccess() }
        observe(model.libraryChanges) { [weak self] in self?.actions.refreshFavorites() }
        observe(model.playbackChanges) { [weak self] in self?.updateVisibleSongs() }
        observe(appearance.$preferences.map(\.playlistView).removeDuplicates().dropFirst().eraseToAnyPublisher()) {
            [weak self] _ in self?.render()
        }
        select(selected, animated: false)
    }

    override func pageDidBecomeActive() {
        preferences.reload()
        results.setAccess(available: model.isAvailable && !model.isClosed,
                          generation: model.accountAccessGeneration)
        results.appear(); actions.setActive(true)
        if results.query.isEmpty { submit() }
        render()
    }

    override func pageDidBecomeInactive() { results.disappear(); actions.suspend() }
    override func pageDidBecomeHidden() { results.holdPagination() }
    override func pageDidBecomeVisible() { showActionError(); showSearchFailure() }
    override func pageDidFinishAppearing() {
        showActionError(); showSearchFailure()
    }

    override func applyTheme() {
        super.applyTheme()
        categories.selectedSegmentTintColor = UIColor(theme.surface)
        categories.setTitleTextAttributes([.foregroundColor: UIColor(theme.ink)], for: .normal)
        updateVisibleSongs(force: true)
        for page in pages.values {
            for path in page.table.indexPathsForVisibleRows ?? [] {
                guard let id = page.dataSource.itemIdentifier(for: path),
                      let cell = page.table.cellForRow(at: path) else { continue }
                if case .playlists = id { configurePlaylistCell(cell, id: id, page: page) }
                else if case .status(let section) = id { configureStatusCell(cell, in: page.table, section: section) }
            }
        }
        updateHeaders()
    }

    func pageViewController(_ pageViewController: UIPageViewController, viewControllerBefore page: UIViewController)
        -> UIViewController? { neighbor(page, step: -1) }
    func pageViewController(_ pageViewController: UIPageViewController, viewControllerAfter page: UIViewController)
        -> UIViewController? { neighbor(page, step: 1) }
    func pageViewController(_ pageViewController: UIPageViewController,
                            willTransitionTo pendingViewControllers: [UIViewController]) { paging = true }
    func pageViewController(_ pageViewController: UIPageViewController, didFinishAnimating finished: Bool,
                            previousViewControllers: [UIViewController], transitionCompleted completed: Bool) {
        paging = false
        if completed, let page = pager.viewControllers?.first,
           let kind = kinds.first(where: { pages[$0] === page }) { selected = kind }
        render()
        if let pending = pendingKind { pendingKind = nil; select(pending, animated: true) }
    }

    func tableView(_ tableView: UITableView, titleForHeaderInSection section: Int) -> String? {
        let kind = Kind.allCases[tableView.tag]
        guard section == 0 ? kind != .playlists : kind != .songs else { return nil }
        return section == 0 ? "单曲" : "歌单"
    }
    func tableView(_ tableView: UITableView, viewForHeaderInSection section: Int) -> UIView? {
        guard self.tableView(tableView, titleForHeaderInSection: section) != nil else { return nil }
        let header = UITableViewHeaderFooterView(reuseIdentifier: nil)
        configureHeader(header, in: tableView, section: section)
        return header
    }
    private func configureHeader(_ header: UITableViewHeaderFooterView, in tableView: UITableView, section: Int) {
        guard let title = self.tableView(tableView, titleForHeaderInSection: section) else { return }
        var content = header.defaultContentConfiguration()
        content.text = title
        content.textProperties.font = .systemFont(ofSize: 18, weight: .semibold)
        content.textProperties.color = UIColor(theme.ink)
        header.contentConfiguration = content
        let existing = header.contentView.subviews.first { $0.accessibilityIdentifier == "music.search.playAll" }
        if section == 0, !results.songs.isEmpty, existing == nil {
            let play = musicSystemButton(symbol: "play.circle.fill", accessibilityLabel: "播放全部") { [weak self] in
                guard let self else { return }
                self.actions.playAll(session: self.results)
            }
            play.tintColor = UIColor(theme.accent)
            play.accessibilityIdentifier = "music.search.playAll"
            play.frame = CGRect(x: 62, y: 0, width: 44, height: 44)
            header.contentView.addSubview(play)
        } else if results.songs.isEmpty { existing?.removeFromSuperview() }
        existing?.tintColor = UIColor(theme.accent)
    }
    func tableView(_ tableView: UITableView, heightForHeaderInSection section: Int) -> CGFloat {
        self.tableView(tableView, titleForHeaderInSection: section) == nil ? .leastNormalMagnitude : 44
    }
    func tableView(_ tableView: UITableView, heightForRowAt indexPath: IndexPath) -> CGFloat {
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
    private func makeCell(in tableView: UITableView, at indexPath: IndexPath,
                          id: MusicSearchResultRowID) -> UITableViewCell? {
        if case .song(let id) = id, let row = songRows[id] {
            let cell = tableView.dequeueReusableCell(withIdentifier: MusicTrackCell.reuseIdentifier,
                                                     for: indexPath) as! MusicTrackCell
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
            menu.append(UIMenu(title: "添加到歌单", children: self.model.collections.filter {
                $0.kind != "favorites"
            }.map { target in
                UIAction(title: target.title) { [weak self] _ in
                    guard let self, self.results.requestID == request else { return }
                    self.actions.perform(song: song, session: self.results) { [weak model = self.model] in
                        await model?.add($0, to: target)
                    }
                }
            }))
            return UIMenu(children: menu)
        }
    }
    func tableView(_ tableView: UITableView, willDisplay cell: UITableViewCell, forRowAt indexPath: IndexPath) {
        guard isPageVisible, Kind.allCases[tableView.tag] == selected, selected != .all else { return }
        if case .song? = rowID(in: tableView, at: indexPath), indexPath.row >= results.songs.count - 3 {
            results.more(.songs, automatic: true)
        }
        if case .playlists? = rowID(in: tableView, at: indexPath),
           indexPath.row * playlistColumns(tableView) >= results.playlists.count - 3 {
            results.more(.playlists, automatic: true)
        }
    }
    func scrollViewWillBeginDragging(_ scrollView: UIScrollView) { results.resumePagination() }

    private func submit() {
        let query = self.query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty, model.isAvailable, !model.isClosed else { return }
        preferences.reload()
        history.record(query)
        model.query = query
        actions.suspend()
        results.submit(query, preferences: preferences.preferences)
        actions.setActive(true)
    }
    private func reconcileAccess() {
        results.setAccess(available: model.isAvailable && !model.isClosed,
                          generation: model.accountAccessGeneration)
        actions.setActive(model.isAvailable && !model.isClosed)
        render()
    }
    private func render() {
        guard isViewLoaded else { return }
        if categories.numberOfSegments != kinds.count {
            categories.removeAllSegments()
            for (index, kind) in kinds.enumerated() {
                categories.insertSegment(withTitle: kind == .all ? "综合" : kind == .songs ? "单曲" : "歌单",
                                         at: index, animated: false)
            }
        }
        if !paging, !kinds.contains(selected) { select(.songs, animated: false) }
        categories.selectedSegmentIndex = kinds.firstIndex(of: selected) ?? 0
        let previousSongs = songRows
        songRows = Dictionary(uniqueKeysWithValues: results.songs.enumerated().map {
            (Data($0.element.id.utf8), (song: $0.element, index: $0.offset))
        })
        let changedSongs = Set(songRows.keys.filter {
            guard let previous = previousSongs[$0], let row = songRows[$0] else { return false }
            return previous.song.track != row.song.track || previous.index != row.index
        })
        for (kind, page) in pages {
            let previous = page.dataSource.snapshot()
            let oldIDs = Set(previous.itemIdentifiers)
            let previousPlaylists = page.playlistRows
            let previousFooters = page.footerStates
            let oldColumns = page.columns
            page.columns = playlistColumns(page.table)
            page.playlistRows = [:]
            page.footerStates = [:]
            var snapshot = NSDiffableDataSourceSnapshot<Int, MusicSearchResultRowID>()
            snapshot.appendSections([0, 1])
            if kind != .playlists {
                let songs = kind == .all ? Array(results.songs.prefix(5)) : results.songs
                snapshot.appendItems(songs.map { .song(Data($0.id.utf8)) }, toSection: 0)
            }
            let count = playlistCount(kind)
            for start in stride(from: 0, to: count, by: page.columns) {
                let items = Array(results.playlists[start..<min(start + page.columns, count)])
                let id = Data(items[0].id.utf8)
                page.playlistRows[id] = items
                snapshot.appendItems([.playlists(id)], toSection: 1)
            }
            for section in 0...1 {
                if let footer = footerState(kind: kind, section: section) {
                    page.footerStates[section] = footer
                    snapshot.appendItems([.status(section)], toSection: section)
                }
            }
            let changed = snapshot.itemIdentifiers.filter { id in
                guard oldIDs.contains(id) else { return false }
                switch id {
                case .song(let id): return changedSongs.contains(id)
                case .playlists(let id):
                    return oldColumns != page.columns || previousPlaylists[id] != page.playlistRows[id]
                case .status(let section): return previousFooters[section] != page.footerStates[section]
                }
            }
            snapshot.reconfigureItems(changed)
            if previous.sectionIdentifiers != snapshot.sectionIdentifiers ||
                previous.itemIdentifiers != snapshot.itemIdentifiers || !changed.isEmpty {
                page.dataSource.apply(snapshot, animatingDifferences: false)
            }
        }
        updateHeaders()
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

    private func configureSongCell(_ cell: MusicTrackCell, row: (song: MusicSearchSong, index: Int)) {
        let active = model.current.map { MusicSearchFusion.sameRecording($0.track, row.song.track) } == true
        cell.configure(track: row.song.track, active: active, theme: theme, index: row.index)
        cell.accessoryType = actions.isFavorite(row.song) ? .checkmark : .none
        cell.accessibilityTraits = active ? [.button, .selected] : .button
    }

    private func updateVisibleSongs(force: Bool = false) {
        for page in pages.values {
            for path in page.table.indexPathsForVisibleRows ?? [] {
                guard case .song(let id)? = page.dataSource.itemIdentifier(for: path), let row = songRows[id],
                      let cell = page.table.cellForRow(at: path) as? MusicTrackCell else { continue }
                let active = model.current.map { MusicSearchFusion.sameRecording($0.track, row.song.track) } == true
                if force || cell.accessibilityTraits.contains(.selected) != active {
                    configureSongCell(cell, row: row)
                } else { cell.accessoryType = actions.isFavorite(row.song) ? .checkmark : .none }
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
        }, play: { [weak self] in
            guard let self, self.results.requestID == request else { return }
            self.playPlaylist($0)
        })
    }

    private func updateHeaders() {
        for page in pages.values {
            for section in 0...1 {
                if let header = page.table.headerView(forSection: section) {
                    configureHeader(header, in: page.table, section: section)
                }
            }
        }
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
            let restart = section == 1 && failures.contains { $0.error == .staleResult }
            let request = results.requestID
            let alert = UIAlertController(title: "错误", message: message, preferredStyle: .alert)
            alert.addAction(UIAlertAction(title: "取消", style: .cancel))
            alert.addAction(UIAlertAction(title: restart ? "重新搜索歌单" : "重试", style: .default) {
                [weak self] _ in
                guard let self, self.results.requestID == request else { return }
                self.shownFailures[section] = nil
                if restart { self.results.restartPlaylists() }
                else { self.results.retry(section == 0 ? .songs : .playlists) }
            })
            shownFailures[section] = message
            present(alert, animated: true)
            return
        }
    }
    private func select(_ kind: Kind, animated: Bool) {
        if paging { pendingKind = kind; return }
        let forward = (kinds.firstIndex(of: kind) ?? 0) >= (kinds.firstIndex(of: selected) ?? 0)
        selected = kind
        if let page = pages[kind] {
            paging = animated
            pager.setViewControllers([page], direction: forward ? .forward : .reverse, animated: animated) {
                [weak self] _ in
                guard let self else { return }
                self.paging = false
                if let pending = self.pendingKind { self.pendingKind = nil; self.select(pending, animated: true) }
            }
        }
        categories.selectedSegmentIndex = kinds.firstIndex(of: kind) ?? 0
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
        let sources = MusicSourcesViewController(model: model, tracks: Array(song.variants.prefix(10)),
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
    private func playPlaylist(_ item: MusicPublicBrowseCollection) {
        guard let browser = model.publicBrowser else { return }
        let request = results.requestID, access = model.accountAccessGeneration
        results.holdPagination()
        actions.perform { [weak self] in
            guard let self, self.results.requestID == request, self.model.accountAccessGeneration == access else {
                throw MusicServiceError.staleResult
            }
            let page = MusicPublicBrowseViewModel(service: browser, request: .detail(item),
                queuePage: { [weak model = self.model] source, cursor in
                    guard let model else { throw MusicServiceError.closed }
                    return try await model.remoteQueuePage(source, cursor: cursor)
                }, releaseSource: { [weak model = self.model] in model?.releaseRemoteQueueSource($0) })
            page.setAccess(available: true, generation: access)
            page.appear()
            defer { page.disappear() }
            await page.loadIfNeeded()
            try Task.checkCancellation()
            guard self.results.requestID == request, self.model.accountAccessGeneration == access else {
                throw MusicServiceError.staleResult
            }
            if let error = page.error { throw error }
            let (source, _) = try await page.playbackSource()
            let firstPage = try await self.model.remoteQueuePage(source, cursor: nil)
            guard let first = firstPage.items.first else { throw MusicServiceError.contentUnavailable }
            await self.model.playRemoteQueue(source, start: .init(index: 0, track: first))
        }
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
            label = section == 1 && failures.contains(where: { $0.error == .staleResult }) ? "重新搜索歌单" : "重试"
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
private final class MusicSearchPlaylistCell: UITableViewCell {
    private var tiles: [MusicBrowseCell] = []
    private var columns = 1
    private var items: [MusicPublicBrowseCollection] = []
    private var open: ((MusicPublicBrowseCollection) -> Void)?
    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        backgroundColor = .clear; selectionStyle = .none
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    func configure(_ items: [MusicPublicBrowseCollection], columns: Int, theme: PageTheme,
                   open: @escaping (MusicPublicBrowseCollection) -> Void,
                   play: @escaping (MusicPublicBrowseCollection) -> Void) {
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
                layout: columns == 1 ? .list : .grid, theme: theme, onPlay: { play(item) })
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
    let table = UITableView(frame: .zero, style: .plain)
    var dataSource: UITableViewDiffableDataSource<Int, MusicSearchResultRowID>!
    var playlistRows: [Data: [MusicPublicBrowseCollection]] = [:]
    var footerStates: [Int: MusicSearchFooterState] = [:]
    var columns = 1
    var onLayout: (() -> Void)?
    override func loadView() { view = table }
    override func viewDidLayoutSubviews() { super.viewDidLayoutSubviews(); onLayout?() }
}

private enum MusicSearchResultRowID: Hashable { case song(Data), playlists(Data), status(Int) }

private struct MusicSearchFooterState: Equatable {
    enum Action: Equatable { case failure, retry, cancel, more, category }
    let message: String?
    let label: String?
    let action: Action?
    let request: UUID
}

@MainActor
private final class MusicSearchChartsCell: UITableViewCell {
    private let scroll = UIScrollView()
    private let stack = UIStackView()
    private var charts: [MusicSearchDiscoveryChart] = []
    private var widths: [NSLayoutConstraint] = []
    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        backgroundColor = .clear; selectionStyle = .none
        scroll.showsHorizontalScrollIndicator = false; scroll.isPagingEnabled = true
        stack.axis = .horizontal; stack.spacing = 12
        contentView.addSubview(scroll); scroll.pinEdges(to: contentView)
        scroll.addSubview(stack)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    func configure(_ charts: [MusicSearchDiscoveryChart], theme: PageTheme,
                   play: @escaping (MusicSearchDiscoveryChart, Int) -> Void) {
        self.charts = charts
        widths.removeAll()
        for item in stack.arrangedSubviews { stack.removeArrangedSubview(item); item.removeFromSuperview() }
        for chart in charts {
            let column = UIStackView()
            column.axis = .vertical; column.spacing = 0
            let heading = UIButton(type: .system)
            heading.setTitle(chart.collection.title, for: .normal)
            heading.tintColor = UIColor(theme.ink)
            heading.addAction(UIAction { _ in if !chart.tracks.isEmpty { play(chart, 0) } }, for: .touchUpInside)
            column.addArrangedSubview(heading)
            for (index, track) in chart.tracks.prefix(20).enumerated() {
                let button = UIButton(type: .system)
                button.setTitle("\(index + 1). \(track.title)", for: .normal)
                button.contentHorizontalAlignment = .leading
                button.tintColor = UIColor(theme.ink)
                button.heightAnchor.constraint(equalToConstant: 40).isActive = true
                button.addAction(UIAction { _ in play(chart, index) }, for: .touchUpInside)
                column.addArrangedSubview(button)
            }
            column.addArrangedSubview(UIView())
            stack.addArrangedSubview(column)
            let width = column.widthAnchor.constraint(equalToConstant: max(0, bounds.width - 32))
            width.isActive = true
            widths.append(width)
        }
        setNeedsLayout()
    }
    override func layoutSubviews() {
        super.layoutSubviews()
        let width = max(0, bounds.width - 32)
        for constraint in widths { constraint.constant = width }
        stack.frame = CGRect(x: 16, y: 0, width: CGFloat(charts.count) * (width + 12), height: bounds.height)
        scroll.contentSize = CGSize(width: stack.frame.maxX + 16, height: bounds.height)
    }
}
#endif
