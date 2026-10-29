#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class MusicSearchViewController: SearchPageViewController,
    UITableViewDataSource, UITableViewDelegate
{
    private let model: MusicViewModel
    private let appearance: MusicAppearancePreferencesStore
    private weak var router: (any MusicUIRouting)?
    private let history = SearchHistoryStore()
    private let table = UITableView(frame: .zero, style: .plain)
    private var discovery: MusicSearchDiscoverySnapshot?
    private var discoveryLoading = false
    private var discoveryRequest = UUID()
    private var discoveryAccess: UUID?
    private var suggestionOffset = 0

    public init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore, router: any MusicUIRouting) {
        self.model = model; self.appearance = appearance; self.router = router
        super.init(themeProvider: appearance, query: model.query, initialFocus: true)
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        searchBar.placeholder = "搜索歌曲、歌手、歌单"
        onSubmit = { [weak self] in self?.submit($0) }
        table.backgroundColor = .clear
        table.dataSource = self; table.delegate = self
        table.separatorStyle = .none
        table.register(UITableViewCell.self, forCellReuseIdentifier: "keyword")
        table.register(MusicSearchChartsCell.self, forCellReuseIdentifier: "charts")
        view.addSubview(table); table.pinEdges(to: view)
        observe(history) { [weak self] in self?.render() }
        observe(model) { [weak self] in self?.reconcileAccess() }
    }

    public override func pageDidBecomeActive() {
        history.reload()
        reconcileAccess()
        render()
    }

    public override func pageDidBecomeInactive() {
        discoveryRequest = UUID()
    }

    public override func applyTheme() {
        super.applyTheme()
        if isViewLoaded { render() }
    }

    public override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        if table.tableHeaderView?.bounds.width != table.bounds.width { configureSuggestions() }
    }

    public func numberOfSections(in tableView: UITableView) -> Int { 3 }
    public func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int {
        switch section {
        case 0: return history.entries.count
        case 1: return min(20, discovery?.hot.count ?? 0)
        default: return discovery?.charts.isEmpty == false ? 1 : 0
        }
    }

    public func tableView(_ tableView: UITableView, titleForHeaderInSection section: Int) -> String? {
        switch section {
        case 0: return history.entries.isEmpty ? nil : "搜索历史"
        case 1: return discovery?.hot.isEmpty == false ? "热搜榜" : nil
        default: return discovery?.charts.isEmpty == false ? "音乐榜单" : nil
        }
    }

    public func tableView(_ tableView: UITableView, heightForRowAt indexPath: IndexPath) -> CGFloat {
        if indexPath.section == 2 {
            let count = discovery?.charts.map { min(20, $0.tracks.count) }.max() ?? 0
            return CGFloat(max(288, count * 40 + 60))
        }
        return 42
    }

    public func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        if indexPath.section == 2 {
            let cell = tableView.dequeueReusableCell(withIdentifier: "charts", for: indexPath) as! MusicSearchChartsCell
            cell.configure(discovery?.charts ?? [], theme: theme) { [weak self] chart, index in
                guard let self else { return }
                self.runPageTask { await self.model.play(chart.tracks, index: index) }
            }
            return cell
        }
        let cell = tableView.dequeueReusableCell(withIdentifier: "keyword", for: indexPath)
        var configuration = cell.defaultContentConfiguration()
        configuration.text = indexPath.section == 0 ? history.entries[indexPath.row].text :
            "\(indexPath.row + 1). \(discovery!.hot[indexPath.row].keyword)"
        configuration.textProperties.color = UIColor(theme.ink)
        configuration.textProperties.font = .systemFont(ofSize: 14)
        cell.contentConfiguration = configuration
        cell.backgroundColor = .clear
        return cell
    }

    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        if indexPath.section == 0 { submit(history.entries[indexPath.row].text) }
        else if indexPath.section == 1 { submit(discovery!.hot[indexPath.row].keyword) }
    }

    public func tableView(_ tableView: UITableView, viewForHeaderInSection section: Int) -> UIView? {
        guard let title = self.tableView(tableView, titleForHeaderInSection: section) else { return nil }
        let header = UITableViewHeaderFooterView(reuseIdentifier: nil)
        var configuration = header.defaultContentConfiguration()
        configuration.text = title
        configuration.textProperties.font = .systemFont(ofSize: 17, weight: .semibold)
        configuration.textProperties.color = UIColor(theme.ink)
        header.contentConfiguration = configuration
        if section == 0 {
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
        searchBar.text = keyword
        searchBar.resignFirstResponder()
        navigationController?.pushViewController(MusicSearchResultsViewController(
            model: model, query: keyword, appearance: appearance, router: router
        ), animated: true)
    }

    private func loadDiscovery() {
        guard isPageActive, !discoveryLoading,
              discovery == nil || discovery!.validUntil <= Date(),
              let provider = model.searchDiscoveryProvider else { return }
        let access = model.accountAccessGeneration
        let own = discoveryRequest
        runPageTask { [weak self] in
            guard let self else { return }
            guard !Task.isCancelled, self.isPageActive, !self.discoveryLoading,
                  self.discoveryRequest == own,
                  self.model.accountAccessGeneration == access else { return }
            self.discoveryLoading = true
            defer {
                self.discoveryLoading = false
                if self.isPageActive, self.discoveryRequest != own { self.loadDiscovery() }
            }
            do {
                let result = try await provider.searchDiscovery()
                guard !Task.isCancelled, self.isPageActive, self.discoveryRequest == own,
                      self.model.accountAccessGeneration == access else { return }
                self.discovery = result
                self.render()
            } catch {
                if !Task.isCancelled, self.discoveryRequest == own, self.model.accountAccessGeneration == access {
                    self.showError(MusicServiceError.sanitized(error).localizedDescription)
                }
            }
        }
    }

    private func reconcileAccess() {
        guard model.isAvailable, !model.isClosed else {
            discoveryAccess = nil
            discoveryRequest = UUID()
            discovery = nil
            render()
            return
        }
        let access = model.accountAccessGeneration
        if discoveryAccess != access {
            discoveryAccess = access
            discoveryRequest = UUID()
            discovery = nil
        }
        loadDiscovery()
    }

    private func render() {
        guard isViewLoaded else { return }
        table.reloadData()
        configureSuggestions()
    }

    private func configureSuggestions() {
        let suggestions = discovery?.suggestions ?? []
        guard !suggestions.isEmpty, table.bounds.width > 0 else { table.tableHeaderView = nil; return }
        let values = (0..<min(4, suggestions.count)).map { suggestions[(suggestionOffset + $0) % suggestions.count] }
        let font = UIFont.systemFont(ofSize: 13)
        let sizes = values.map { CGSize(width: ($0 as NSString).size(withAttributes: [.font: font]).width + 24,
                                      height: 34) }
        let rows = MusicChannelTagRows(width: table.bounds.width - 32, items: sizes)
        let header = UIView(frame: CGRect(x: 0, y: 0, width: table.bounds.width, height: rows.size.height + 62))
        let change = UIButton(type: .system)
        change.setTitle("换一批", for: .normal)
        change.tintColor = UIColor(theme.muted)
        change.frame = CGRect(x: header.bounds.width - 76, y: 0, width: 60, height: 44)
        change.addAction(UIAction { [weak self] _ in
            self?.suggestionOffset += 4
            self?.configureSuggestions()
        }, for: .touchUpInside)
        header.addSubview(change)
        for (index, value) in values.enumerated() {
            let button = UIButton(type: .system)
            button.setTitle(value, for: .normal)
            button.tintColor = UIColor(theme.ink)
            button.backgroundColor = UIColor(theme.control)
            button.layer.cornerRadius = 17
            button.frame = rows.frames[index].offsetBy(dx: 16, dy: 46)
            button.addAction(UIAction { [weak self] _ in self?.submit(value) }, for: .touchUpInside)
            header.addSubview(button)
        }
        table.tableHeaderView = header
    }

}

@MainActor
private final class MusicSearchResultsViewController: SearchPageViewController,
    UIPageViewControllerDataSource, UIPageViewControllerDelegate, UITableViewDataSource, UITableViewDelegate
{
    private enum Kind: CaseIterable, Hashable { case all, songs, playlists }
    private let model: MusicViewModel
    private let appearance: MusicAppearancePreferencesStore
    private weak var router: (any MusicUIRouting)?
    private let preferences = MusicSearchPreferencesStore()
    private let results: MusicSearchResultsModel
    private let actions: MusicSearchActions
    private let categories = UISegmentedControl(items: ["综合", "单曲", "歌单"])
    private let pager = UIPageViewController(transitionStyle: .scroll, navigationOrientation: .horizontal)
    private var pages: [Kind: MusicSearchTableController] = [:]
    private var selected = Kind.all
    private var paging = false
    private var pendingKind: Kind?
    private var kinds: [Kind] { results.preferences.searchPlaylists ? Kind.allCases : [.songs] }

    init(model: MusicViewModel, query: String, appearance: MusicAppearancePreferencesStore,
         router: (any MusicUIRouting)?) {
        self.model = model; self.appearance = appearance; self.router = router
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
        searchBar.placeholder = "搜索歌曲、歌手、歌单"
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
            categories.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 8),
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
            page.table.dataSource = self; page.table.delegate = self
            page.table.backgroundColor = .clear; page.table.separatorStyle = .none
            page.table.register(MusicTrackCell.self, forCellReuseIdentifier: MusicTrackCell.reuseIdentifier)
            page.table.register(MusicCollectionCell.self, forCellReuseIdentifier: MusicCollectionCell.reuseIdentifier)
            page.table.register(MusicSearchPlaylistCell.self, forCellReuseIdentifier: "Playlists")
            pages[kind] = page
        }
        observe(results) { [weak self] in self?.render() }
        observe(actions) { [weak self] in self?.render() }
        observe(model) { [weak self] in self?.reconcileAccess() }
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

    override func applyTheme() {
        super.applyTheme()
        categories.selectedSegmentTintColor = UIColor(theme.surface)
        categories.setTitleTextAttributes([.foregroundColor: UIColor(theme.ink)], for: .normal)
        for page in pages.values { page.table.reloadData() }
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

    func numberOfSections(in tableView: UITableView) -> Int { 2 }
    func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int {
        let kind = Kind.allCases[tableView.tag]
        if section == 0 { return kind == .playlists ? 0 : kind == .all ? min(5, results.songs.count) : results.songs.count }
        let count = playlistCount(kind)
        let columns = playlistColumns(tableView)
        return (count + columns - 1) / columns
    }
    func tableView(_ tableView: UITableView, titleForHeaderInSection section: Int) -> String? {
        let kind = Kind.allCases[tableView.tag]
        guard section == 0 ? kind != .playlists : kind != .songs else { return nil }
        return section == 0 ? "单曲" : "歌单"
    }
    func tableView(_ tableView: UITableView, viewForHeaderInSection section: Int) -> UIView? {
        guard let title = self.tableView(tableView, titleForHeaderInSection: section) else { return nil }
        let header = UITableViewHeaderFooterView(reuseIdentifier: nil)
        var content = header.defaultContentConfiguration()
        content.text = title
        content.textProperties.font = .systemFont(ofSize: 18, weight: .semibold)
        content.textProperties.color = UIColor(theme.ink)
        header.contentConfiguration = content
        if section == 0, !results.songs.isEmpty {
            let play = musicSystemButton(symbol: "play.circle.fill", accessibilityLabel: "播放全部") { [weak self] in
                guard let self else { return }
                self.actions.playAll(session: self.results)
            }
            play.tintColor = UIColor(theme.accent)
            play.frame = CGRect(x: 62, y: 0, width: 44, height: 44)
            header.contentView.addSubview(play)
        }
        return header
    }
    func tableView(_ tableView: UITableView, heightForHeaderInSection section: Int) -> CGFloat {
        self.tableView(tableView, titleForHeaderInSection: section) == nil ? .leastNormalMagnitude : 44
    }
    func tableView(_ tableView: UITableView, heightForRowAt indexPath: IndexPath) -> CGFloat {
        if indexPath.section == 0 { return 72 }
        let columns = playlistColumns(tableView)
        return columns == 1 ? 80 : (tableView.bounds.width - 32 - CGFloat(columns - 1) * 12) /
            CGFloat(columns) + 60
    }
    func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        if indexPath.section == 0 {
            let song = results.songs[indexPath.row]
            let cell = tableView.dequeueReusableCell(withIdentifier: MusicTrackCell.reuseIdentifier,
                                                     for: indexPath) as! MusicTrackCell
            cell.configure(track: song.track, active: model.current.map {
                MusicSearchFusion.sameRecording($0.track, song.track)
            } == true,
                           theme: theme, index: indexPath.row)
            cell.accessoryType = actions.isFavorite(song) ? .checkmark : .none
            return cell
        }
        let columns = playlistColumns(tableView)
        let start = indexPath.row * columns
        let items = Array(results.playlists[start..<min(start + columns, playlistCount(Kind.allCases[tableView.tag]))])
        let cell = tableView.dequeueReusableCell(withIdentifier: "Playlists", for: indexPath) as! MusicSearchPlaylistCell
        cell.configure(items, columns: columns, theme: theme,
                       open: { [weak self] in self?.router?.showPublicCollection($0) },
                       play: { [weak self] in self?.playPlaylist($0) })
        return cell
    }
    func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        if indexPath.section == 0 { actions.playSingle(results.songs[indexPath.row], session: results) }
    }
    func tableView(_ tableView: UITableView, trailingSwipeActionsConfigurationForRowAt indexPath: IndexPath)
        -> UISwipeActionsConfiguration? {
        guard indexPath.section == 0 else { return nil }
        let song = results.songs[indexPath.row]
        let favorite = UIContextualAction(style: .normal,
            title: actions.isFavorite(song) ? "取消收藏" : "收藏") { [weak self] _, _, finish in
            guard let self else { finish(false); return }
            self.actions.toggleFavorite(song) { try await self.results.refreshTrack(song) }
            finish(true)
        }
        favorite.backgroundColor = UIColor(theme.accent)
        return UISwipeActionsConfiguration(actions: [favorite])
    }
    func tableView(_ tableView: UITableView, contextMenuConfigurationForRowAt indexPath: IndexPath,
                   point: CGPoint) -> UIContextMenuConfiguration? {
        guard indexPath.section == 0 else { return nil }
        let song = results.songs[indexPath.row]
        return UIContextMenuConfiguration(identifier: nil, previewProvider: nil) { [weak self] _ in
            guard let self else { return nil }
            var menu: [UIMenuElement] = [
                UIAction(title: "下一首播放") { [weak self] _ in
                    guard let self else { return }
                    self.actions.perform(song: song, session: self.results) { [weak model = self.model] in
                        await model?.insertNext($0)
                    }
                },
                UIAction(title: "添加到播放队列") { [weak self] _ in
                    guard let self else { return }
                    self.actions.perform(song: song, session: self.results) { [weak model = self.model] in
                        await model?.append($0)
                    }
                },
                UIAction(title: "音源") { [weak self] _ in self?.showSources(song) },
            ]
            menu.append(UIMenu(title: "添加到歌单", children: self.model.collections.filter {
                $0.kind != "favorites"
            }.map { target in
                UIAction(title: target.title) { [weak self] _ in
                    guard let self else { return }
                    self.actions.perform(song: song, session: self.results) { [weak model = self.model] in
                        await model?.add($0, to: target)
                    }
                }
            }))
            return UIMenu(children: menu)
        }
    }
    func tableView(_ tableView: UITableView, willDisplay cell: UITableViewCell, forRowAt indexPath: IndexPath) {
        guard Kind.allCases[tableView.tag] != .all else { return }
        if indexPath.section == 0, indexPath.row >= results.songs.count - 3 { results.more(.songs, automatic: true) }
        if indexPath.section == 1, indexPath.row * playlistColumns(tableView) >= results.playlists.count - 3 {
            results.more(.playlists, automatic: true)
        }
    }
    func scrollViewWillBeginDragging(_ scrollView: UIScrollView) { results.resumePagination() }

    private func submit() {
        let query = searchBar.text?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        guard !query.isEmpty, model.isAvailable, !model.isClosed else { return }
        model.query = query
        actions.suspend()
        results.submit(query, preferences: preferences.preferences)
        actions.setActive(true)
    }
    private func reconcileAccess() {
        results.setAccess(available: model.isAvailable && !model.isClosed,
                          generation: model.accountAccessGeneration)
        actions.setActive(model.isAvailable && !model.isClosed)
        actions.refreshFavorites()
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
        for page in pages.values { page.table.reloadData() }
        _ = showError(actions.actionError ?? actions.error?.localizedDescription)
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
        guard isPageActive, presentedViewController == nil else { return }
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

    func tableView(_ tableView: UITableView, viewForFooterInSection section: Int) -> UIView? {
        let kind = Kind.allCases[tableView.tag]
        guard section == 0 ? kind != .playlists : kind != .songs else { return nil }
        let type: MusicSearchSection = section == 0 ? .songs : .playlists
        let loading = section == 0 ? results.songsLoading : results.playlistsLoading
        let failures = section == 0 ? results.songFailures : results.playlistFailures
        let cancelled = section == 0 ? results.songsCancelled : results.playlistsCancelled
        let noPlatforms = section == 0 ? results.songPlatformsEmpty : results.playlistPlatformsEmpty
        let loaded = section == 0 ? results.songsLoaded : results.playlistsLoaded
        let empty = section == 0 ? results.songs.isEmpty : results.playlists.isEmpty
        var message: String?
        var label: String?
        var action: (() -> Void)?
        if noPlatforms { message = "尚未启用搜索平台" }
        else if !failures.isEmpty {
            message = failures.map { $0.error.message(operation: section == 0 ? "单曲搜索" : "歌单搜索",
                platform: $0.platform.title, diagnosticID: $0.diagnosticID) }.joined(separator: "；")
            label = section == 1 && failures.contains(where: { $0.error == .staleResult }) ? "重新搜索歌单" : "重试"
            action = { [weak results] in
                if section == 1 && failures.contains(where: { $0.error == .staleResult }) { results?.restartPlaylists() }
                else { results?.retry(type) }
            }
        } else if cancelled {
            message = "已取消"; label = "重试"; action = { [weak results] in results?.retry(type) }
        } else if loading {
            message = section == 0 ? "单曲加载中" : "歌单加载中"
            label = "取消"; action = { [weak results] in results?.cancel(type) }
        } else if loaded && empty { message = section == 0 ? "未找到歌曲" : "未找到歌单" }
        else if kind == .all, (section == 0 ? results.songs.count : results.playlists.count) > 5 {
            label = "查看更多"; action = { [weak self] in self?.select(section == 0 ? .songs : .playlists, animated: true) }
        } else if section == 0 ? results.moreSongs : results.morePlaylists {
            label = "加载更多"; action = { [weak results] in results?.more(type) }
        }
        let footer = UIStackView()
        footer.axis = .vertical; footer.spacing = 8
        if let message {
            let text = UILabel(); text.text = message; text.numberOfLines = 0
            text.textColor = UIColor(theme.muted); text.font = .systemFont(ofSize: 13)
            text.textAlignment = .center
            footer.addArrangedSubview(text)
        }
        if let label, let action {
            let button = UIButton(type: .system)
            button.setTitle(label, for: .normal); button.tintColor = UIColor(theme.ink)
            button.addAction(UIAction { [weak self] _ in
                if self?.isPageActive == true { action() }
            }, for: .touchUpInside)
            footer.addArrangedSubview(button)
        }
        footer.isLayoutMarginsRelativeArrangement = true
        footer.layoutMargins = UIEdgeInsets(top: 8, left: 16, bottom: 16, right: 16)
        return footer.arrangedSubviews.isEmpty ? nil : footer
    }
    func tableView(_ tableView: UITableView, heightForFooterInSection section: Int) -> CGFloat {
        guard let footer = self.tableView(tableView, viewForFooterInSection: section) else { return .leastNormalMagnitude }
        return max(52, footer.systemLayoutSizeFitting(CGSize(width: tableView.bounds.width, height: 0),
            withHorizontalFittingPriority: .required, verticalFittingPriority: .fittingSizeLevel).height)
    }
}

@MainActor
private final class MusicSearchPlaylistCell: UITableViewCell {
    private var tiles: [MusicBrowseCell] = []
    private var columns = 1
    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        backgroundColor = .clear; selectionStyle = .none
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    func configure(_ items: [MusicPublicBrowseCollection], columns: Int, theme: PageTheme,
                   open: @escaping (MusicPublicBrowseCollection) -> Void,
                   play: @escaping (MusicPublicBrowseCollection) -> Void) {
        self.columns = columns
        for tile in tiles { tile.removeFromSuperview() }
        tiles = items.map { item in
            let tile = MusicBrowseCell(frame: .zero)
            tile.configure(item: .init(id: item.id, title: item.title,
                subtitle: [item.count.map { "\($0)首" }, item.creator].compactMap { $0 }.joined(separator: " · "),
                artwork: item.artwork, content: .collection(item)),
                layout: columns == 1 ? .list : .grid, theme: theme, onPlay: { play(item) })
            tile.setOpenAction { open(item) }
            contentView.addSubview(tile)
            return tile
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
    override func loadView() { view = table }
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
