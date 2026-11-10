#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
public final class MusicCollectionViewController: MusicPageViewController, UITableViewDelegate {
    private enum Content {
        case local(MusicCollectionViewModel)
        case remote(MusicPublicBrowseCollection, MusicPublicBrowseViewModel)
    }

    private enum RowID: Hashable {
        case local(MusicReference)
        case remote(section: String, item: String)
    }

    private struct Row {
        let track: MusicRemoteTrack
        let filteredIndex: Int
        let originalIndex: Int?
    }

    private let model: MusicViewModel
    private let content: Content
    private let favorites: MusicMiniPlayerFavorites?
    private let table = UITableView(frame: .zero, style: .plain)
    private lazy var detailHeader = MusicCollectionDetailHeaderView(themeProvider: themeProvider)
    private let statusFooter = MusicCollectionStatusFooterView()
    private weak var activeEditor: MusicCollectionEditorViewController?
    private var dataSource: UITableViewDiffableDataSource<Int, RowID>!
    private var rows: [RowID: Row] = [:]
    private var orderedRows: [(id: RowID, row: Row)] = []
    private var remoteFilter = ""
    private var searchButton: PageHeaderButton?
    private var deleteButton: PageHeaderButton?
    private var refreshButton: PageHeaderButton?
    private var shownReadError: String?
    private var automaticPaginationHeld = false
    private var cachedLocalTracks: [MusicRemoteTrack]?
    private var cachedLocalQuery = ""
    private var localOriginalIndices: [MusicReference: Int] = [:]
    private var rowActionsRevision = UUID()
    private var renderedRowActions: RowActionsKey?
    private struct RowActionsKey: Equatable {
        let presentation: UUID
        let filter: UUID?
        let remoteFilter: String
        let canEdit: Bool
    }

    private var localPage: MusicCollectionViewModel? {
        guard case .local(let page) = content else { return nil }
        return page
    }

    private var remotePage: MusicPublicBrowseViewModel? {
        guard case .remote(_, let page) = content else { return nil }
        return page
    }

    private var requestedRemoteCollection: MusicPublicBrowseCollection? {
        guard case .remote(let collection, _) = content else { return nil }
        return collection
    }

    public convenience init(
        model: MusicViewModel,
        collectionID: String,
        appearance: MusicAppearancePreferencesStore,
        router: any MusicUIRouting
    ) {
        let page = MusicCollectionViewModel(
            model: model,
            collectionID: collectionID,
            currentQueueCollectionID: { model.state.sourceCollectionID }
        )
        self.init(model: model, content: .local(page), appearance: appearance, router: router)
    }

    convenience init(
        model: MusicViewModel,
        page: MusicCollectionViewModel,
        appearance: MusicAppearancePreferencesStore,
        router: any MusicUIRouting
    ) {
        self.init(model: model, content: .local(page), appearance: appearance, router: router)
    }

    public convenience init(
        model: MusicViewModel,
        collection: MusicPublicBrowseCollection,
        browser: any MusicPublicBrowsing,
        appearance: MusicAppearancePreferencesStore,
        router: any MusicUIRouting
    ) {
        let page = MusicPublicBrowseViewModel(
            service: browser,
            request: .detail(collection),
            queuePage: { [weak model] source, cursor in
                guard let model else { throw MusicServiceError.closed }
                return try await model.remoteQueuePage(source, cursor: cursor)
            },
            releaseSource: { [weak model] source in
                model?.releaseRemoteQueueSource(source)
            }
        )
        self.init(
            model: model,
            content: .remote(collection, page),
            appearance: appearance,
            router: router
        )
    }

    private init(
        model: MusicViewModel,
        content: Content,
        appearance: MusicAppearancePreferencesStore,
        router: any MusicUIRouting
    ) {
        self.model = model
        self.content = content
        if case .remote = content {
            favorites = model.favorites
        } else {
            favorites = nil
        }
        _ = router
        super.init(appearance: appearance)
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        pageTitle = nil

        table.backgroundColor = .clear
        table.accessibilityIdentifier = "music.collection.tracks"
        table.separatorStyle = .none
        table.contentInsetAdjustmentBehavior = .never
        table.delegate = self
        table.rowHeight = 72
        table.estimatedRowHeight = 72
        table.keyboardDismissMode = .onDrag
        table.register(MusicCollectionTrackCell.self,
                       forCellReuseIdentifier: MusicCollectionTrackCell.reuseIdentifier)
        dataSource = UITableViewDiffableDataSource(tableView: table) { [weak self] view, path, id in
            guard let self, let row = self.rows[id] else { return nil }
            let cell = view.dequeueReusableCell(
                withIdentifier: MusicCollectionTrackCell.reuseIdentifier,
                for: path
            ) as! MusicCollectionTrackCell
            self.configure(cell, row: row)
            return cell
        }
        table.refreshControl = UIRefreshControl()
        table.refreshControl?.addAction(UIAction { [weak self] _ in
            self?.refresh()
        }, for: .valueChanged)
        view.addSubview(table)
        table.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            table.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            table.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            table.topAnchor.constraint(equalTo: contentTopAnchor),
            table.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor),
        ])
        view.addSubview(detailHeader.actionBar)

        detailHeader.onPlay = { [weak self] in self?.playAll() }
        detailHeader.onSave = { [weak self] in self?.saveRemoteCollection() }
        detailHeader.onEdit = { [weak self] in self?.openEditor() }
        detailHeader.onRename = { [weak self] in self?.rename() }
        detailHeader.onSearchChange = { [weak self] query in
            self?.updateSearch(query)
        }
        detailHeader.onSearchVisibilityChange = { [weak self] in
            guard let self else { return }
            self.searchButton?.accessibilityLabel = self.detailHeader.isSearchVisible
                ? "关闭歌单内搜索" : "歌单内搜索"
            self.updateHeaderFrame()
        }
        detailHeader.frame = CGRect(x: 0, y: 0, width: view.bounds.width, height: 0.01)
        table.tableHeaderView = detailHeader

        statusFooter.onPrevious = { [weak self] in self?.loadPreviousRemotePage() }
        statusFooter.onNext = { [weak self] in self?.loadNextRemotePage() }
        statusFooter.frame = CGRect(x: 0, y: 0, width: view.bounds.width, height: 0.01)
        table.tableFooterView = statusFooter

        let search = PageHeaderButton(
            image: PageIcon.image(.search), accessibilityLabel: "歌单内搜索"
        ) { [weak self] in self?.toggleSearch() }
        search.accessibilityIdentifier = "music.collection.search"
        searchButton = search
        let delete = PageHeaderButton(
            image: PageIcon.image(.remove), accessibilityLabel: "删除歌单"
        ) { [weak self] in self?.requestCollectionRemoval() }
        delete.accessibilityIdentifier = "music.collection.delete"
        deleteButton = delete
        let refresh = PageHeaderButton(
            image: UIImage(systemName: "arrow.clockwise") ?? PageIcon.image(.playing),
            accessibilityLabel: "刷新歌单"
        ) { [weak self] in self?.refreshFromNavigation() }
        refresh.accessibilityIdentifier = "music.collection.refresh"
        refreshButton = refresh

        switch content {
        case .local(let page):
            observe(page.$tracks.combineLatest(page.$searchQuery)
                .removeDuplicates { lhs, rhs in
                    lhs.0 == rhs.0 && lhs.1.utf8.elementsEqual(rhs.1.utf8)
                }
                .map { _ in }.eraseToAnyPublisher()) { [weak self] in self?.render() }
            observe(page) { [weak self] in self?.renderState() }
        case .remote(_, let page):
            observe(page.$sections.map { _ in }.eraseToAnyPublisher()) { [weak self] in self?.render() }
            observe(page) { [weak self] in self?.renderState() }
            observe(model.accessChanges) { [weak self] in self?.activateRemotePage() }
            if let favorites {
                observe(favorites) { [weak self] in
                    self?.updateVisibleCells(force: true)
                    self?.showFavoriteError()
                }
            }
        }
        observe(model.playbackChanges) { [weak self] in
            self?.updateVisibleCells()
            self?.renderHeader()
        }
        observe(model.libraryChanges) { [weak self] in
            self?.rowActionsRevision = UUID()
            self?.renderNavigationActions()
            self?.updateVisibleCells(force: true)
        }
        render()
    }

    public override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        updateHeaderFrame()
        updateFooterFrame()
    }

    public override func pageDidBecomeActive() {
        switch content {
        case .local(let page):
            page.appear()
            render()
        case .remote:
            activateRemotePage()
        }
    }

    public override func pageDidBecomeVisible() {
        if remotePage != nil { automaticPaginationHeld = false }
        renderState()
        MusicArtworkView.retryFailedLoads(in: view)
    }

    public override func pageDidBecomeHidden() {
        statusFooter.setDisplayVisible(false)
        if remotePage != nil { automaticPaginationHeld = true }
    }

    public override func pageDidFinishAppearing() {
        switch content {
        case .local(let page):
            if page.isMissing || page.error != nil || page.actionError != nil {
                renderState()
            }
        case .remote:
            showRemoteReadErrorIfNeeded()
            showRemoteActionError()
            showFavoriteError()
        }
    }

    public override func pageDidBecomeInactive() {
        statusFooter.setDisplayVisible(false)
        if let editor = activeEditor,
           navigationController?.viewControllers.contains(where: { $0 === editor }) == true {
            return
        }
        switch content {
        case .local(let page):
            page.disappear()
        case .remote(_, let page):
            page.disappear()
        }
    }

    public override func applyTheme() {
        super.applyTheme()
        guard isViewLoaded else { return }
        detailHeader.applyTheme(theme)
        statusFooter.applyTheme(theme)
        table.refreshControl?.tintColor = UIColor(theme.accent)
        (table.backgroundView as? UILabel)?.textColor = UIColor(theme.muted)
        updateVisibleCells(force: true)
    }

    private func activateRemotePage() {
        guard isPageActive, let page = remotePage else { return }
        let available = model.isAvailable && !model.isClosed
        page.setAccess(available: available, generation: model.accountAccessGeneration)
        guard available else {
            render()
            return
        }
        page.appear()
        if !page.loaded, !page.isLoading, page.error == nil {
            runPageTask { [weak self] in await self?.remotePage?.loadIfNeeded() }
        }
        render()
    }

    private func render() {
        guard dataSource != nil else { return }
        let next: [(id: RowID, row: Row)]
        switch content {
        case .local(let page):
            let tracksChanged = cachedLocalTracks != page.tracks
            if !tracksChanged, cachedLocalQuery.utf8.elementsEqual(page.searchQuery.utf8) {
                renderState()
                return
            }
            if tracksChanged {
                localOriginalIndices = Dictionary(
                    page.tracks.enumerated().map { ($0.element.ref, $0.offset) },
                    uniquingKeysWith: { first, _ in first }
                )
                cachedLocalTracks = page.tracks
            }
            next = MusicCollectionViewModel.filter(page.tracks, query: page.searchQuery)
                .enumerated().map { filteredIndex, track in
                    (
                        .local(track.ref),
                        Row(
                            track: track,
                            filteredIndex: filteredIndex,
                            originalIndex: localOriginalIndices[track.ref]
                        )
                    )
                }
            cachedLocalQuery = page.searchQuery
        case .remote(_, let page):
            next = page.sections.flatMap { section in
                section.items.compactMap { item -> (RowID, MusicRemoteTrack)? in
                    guard case .track(let track) = item.content else { return nil }
                    return (.remote(section: section.id, item: item.id), track)
                }
            }.filter {
                remoteFilter.isEmpty || MusicRemoteCollectionFilter.matches($0.1, query: remoteFilter)
            }.enumerated().map { filteredIndex, value in
                (
                    value.0,
                    Row(track: value.1, filteredIndex: filteredIndex, originalIndex: nil)
                )
            }
        }
        applyRows(next)
        renderState()
    }

    private func applyRows(_ next: [(id: RowID, row: Row)]) {
        let previous = rows
        orderedRows = next
        rows = Dictionary(uniqueKeysWithValues: next.map { ($0.id, $0.row) })
        var snapshot = NSDiffableDataSourceSnapshot<Int, RowID>()
        snapshot.appendSections([0])
        snapshot.appendItems(next.map(\.id))
        let changed = next.compactMap { value -> RowID? in
            guard let old = previous[value.id] else { return nil }
            return old.track != value.row.track ||
                old.filteredIndex != value.row.filteredIndex ||
                old.originalIndex != value.row.originalIndex ? value.id : nil
        }
        if dataSource.snapshot().itemIdentifiers != snapshot.itemIdentifiers {
            dataSource.apply(snapshot, animatingDifferences: false)
        } else if !changed.isEmpty {
            snapshot.reconfigureItems(changed)
            dataSource.apply(snapshot, animatingDifferences: false)
        }
    }

    private func configure(_ cell: MusicCollectionTrackCell, row: Row) {
        updateRowActionsRevision()
        switch content {
        case .local(let page):
            configureLocal(cell, row: row, page: page)
        case .remote(_, let page):
            configureRemote(cell, row: row, page: page)
        }
    }

    private func configureLocal(
        _ cell: MusicCollectionTrackCell,
        row: Row,
        page: MusicCollectionViewModel
    ) {
        let track = row.track
        let active = model.current?.track.songId.utf8.elementsEqual(track.songId.utf8) == true
        let presentationID = page.presentationID
        let filterID = page.filterID
        cell.configure(
            track: track,
            active: active,
            isFavorite: page.isFavorite(track.ref),
            favoriteEnabled: page.canAct && page.favoritesLoaded,
            actionsEnabled: page.canAct,
            theme: theme,
            actionRevision: rowActionsRevision,
            menu: row.originalIndex.map {
                localTrackMenu(
                    track: track,
                    originalIndex: $0,
                    presentationID: presentationID,
                    page: page
                )
            },
            filteredIndex: row.filteredIndex,
            originalIndex: row.originalIndex,
            onPlay: { [weak page] in
                page?.playFiltered(
                    at: row.filteredIndex,
                    presentationID: presentationID,
                    filterID: filterID
                )
            },
            onFavorite: { [weak page] in
                page?.toggleFavorite(track.ref, presentationID: presentationID)
            },
            onAppend: { [weak page] in
                guard let originalIndex = row.originalIndex else { return }
                page?.enqueue(at: originalIndex, next: false, presentationID: presentationID)
            }
        )
    }

    private func configureRemote(
        _ cell: MusicCollectionTrackCell,
        row: Row,
        page: MusicPublicBrowseViewModel
    ) {
        let track = row.track
        let active = model.current.map { MusicMiniPlayerFavorites.sameSong($0.track, track) } == true
        let enabled = canActRemotely(page)
        cell.configure(
            track: track,
            active: active,
            isFavorite: favorites?.isFavorite(track) == true,
            favoriteEnabled: enabled && favorites?.loaded == true &&
                favorites?.loading == false && favorites?.busy == false,
            actionsEnabled: enabled,
            theme: theme,
            actionRevision: rowActionsRevision,
            menu: remoteTrackMenu(track, page: page),
            filteredIndex: row.filteredIndex,
            originalIndex: nil,
            onPlay: { [weak self] in self?.playRemote(track, page: page) },
            onFavorite: { [weak self] in self?.toggleRemoteFavorite(track, page: page) },
            onAppend: { [weak self] in self?.enqueueRemote(track, next: false, page: page) }
        )
    }

    public func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        tableView.deselectRow(at: indexPath, animated: true)
        guard let row = row(at: indexPath) else { return }
        switch content {
        case .local(let page):
            page.playFiltered(
                at: row.filteredIndex,
                presentationID: page.presentationID,
                filterID: page.filterID
            )
        case .remote(_, let page):
            playRemote(row.track, page: page)
        }
    }

    public func tableView(
        _ tableView: UITableView,
        trailingSwipeActionsConfigurationForRowAt indexPath: IndexPath
    ) -> UISwipeActionsConfiguration? {
        guard let page = localPage, let row = row(at: indexPath) else { return nil }
        let track = row.track
        let presentationID = page.presentationID
        let favorite = UIContextualAction(
            style: .normal,
            title: page.isFavorite(track.ref) ? "取消收藏" : "收藏"
        ) { [weak page] _, _, finish in
            guard let page else {
                finish(false)
                return
            }
            page.toggleFavorite(track.ref, presentationID: presentationID)
            finish(true)
        }
        favorite.backgroundColor = UIColor(theme.accent)
        var actions = [favorite]
        if page.canEdit, let originalIndex = row.originalIndex {
            let remove = UIContextualAction(style: .destructive, title: "移除") {
                [weak self] _, _, finish in
                self?.confirmLocalRemoval(
                    track: track,
                    index: originalIndex,
                    presentationID: presentationID,
                    page: page
                )
                finish(true)
            }
            actions.append(remove)
        }
        return UISwipeActionsConfiguration(actions: actions)
    }

    public func tableView(
        _ tableView: UITableView,
        contextMenuConfigurationForRowAt indexPath: IndexPath,
        point: CGPoint
    ) -> UIContextMenuConfiguration? {
        guard let row = row(at: indexPath) else { return nil }
        switch content {
        case .local(let page):
            guard let originalIndex = row.originalIndex else { return nil }
            let presentationID = page.presentationID
            return UIContextMenuConfiguration(identifier: nil, previewProvider: nil) { [weak self] _ in
                self?.localTrackMenu(
                    track: row.track,
                    originalIndex: originalIndex,
                    presentationID: presentationID,
                    page: page
                )
            }
        case .remote(_, let page):
            guard canActRemotely(page) else { return nil }
            return UIContextMenuConfiguration(identifier: nil, previewProvider: nil) { [weak self] _ in
                self?.remoteTrackMenu(row.track, page: page)
            }
        }
    }

    public func tableView(
        _ tableView: UITableView,
        willDisplay cell: UITableViewCell,
        forRowAt indexPath: IndexPath
    ) {
        if isPageVisible { MusicArtworkView.retryFailedLoads(in: cell) }
        guard remotePage != nil, remoteFilter.isEmpty,
              indexPath.row >= max(0, orderedRows.count - 3) else { return }
        loadAutomaticRemoteNextPage()
    }

    public func scrollViewWillBeginDragging(_ scrollView: UIScrollView) {
        if remotePage != nil { automaticPaginationHeld = false }
    }

    public func scrollViewDidScroll(_ scrollView: UIScrollView) {
        updateScrollChrome()
    }

    private func renderState() {
        guard isViewLoaded else { return }
        updateRowActionsRevision()
        updateVisibleCells(force: true)
        renderHeader()
        renderNavigationActions()
        switch content {
        case .local(let page):
            renderLocalState(page)
        case .remote(_, let page):
            renderRemoteState(page)
        }
    }

    private func renderLocalState(_ page: MusicCollectionViewModel) {
        if page.collection == nil, detailHeader.isSearchVisible {
            detailHeader.setSearchVisible(false)
        }
        if page.isMissing {
            configureFooter()
            table.backgroundView = nil
            if canPresentPageAlert, navigationController?.topViewController === self {
                navigationController?.popViewController(animated: true)
            }
            return
        }
        if page.isLoading, orderedRows.isEmpty {
            table.backgroundView = nil
            configureFooter(text: page.collection == nil ? nil : "加载中", loading: page.collection == nil)
        } else if page.collection != nil, orderedRows.isEmpty {
            table.backgroundView = nil
            configureFooter(text: page.searchQuery.isEmpty ? "暂无歌曲" : "无匹配歌曲")
        } else {
            table.backgroundView = nil
            configureFooter()
        }
        showError(page.actionError)
        guard let error = page.error else {
            shownReadError = nil
            return
        }
        let message = error.localizedDescription
        guard canPresentPageAlert, shownReadError != message else { return }
        let presentation = page.presentationID
        let alert = UIAlertController(title: "错误", message: message, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "重试", style: .default) { [weak self, weak page] _ in
            guard let self, let page, page.presentationID == presentation else { return }
            self.shownReadError = nil
            page.reload(preservingContent: true)
        })
        shownReadError = message
        present(alert, animated: true)
    }

    private func renderRemoteState(_ page: MusicPublicBrowseViewModel) {
        var text: String?
        var previousTitle: String?
        var nextTitle: String?
        if !remoteFilter.isEmpty {
            if orderedRows.isEmpty, page.loaded { text = "无匹配歌曲" }
        } else if page.isLoading {
            if !orderedRows.isEmpty { text = "加载中" }
        } else if page.error != nil {
            if orderedRows.isEmpty { text = "加载失败" }
            nextTitle = "重试"
        } else if page.usesPageNavigation {
            previousTitle = page.canPrevious ? "上一页" : nil
            nextTitle = page.nextCursor == nil ? nil : "下一页"
        } else if page.requiresExplicitNext, page.nextCursor != nil {
            nextTitle = "继续加载"
        } else if orderedRows.isEmpty, page.loaded {
            text = "暂无歌曲"
        }
        configureFooter(text: text, previousTitle: previousTitle, nextTitle: nextTitle,
                        loading: page.isLoading && orderedRows.isEmpty && !page.loaded)
        table.backgroundView = nil
        showRemoteActionError()
        showRemoteReadErrorIfNeeded()
        if orderedRows.isEmpty, page.loaded, !page.requiresExplicitNext {
            loadAutomaticRemoteNextPage()
        }
    }

    private func renderHeader() {
        guard isViewLoaded else { return }
        switch content {
        case .local(let page):
            let collection = page.collection
            let showsEdit = collection.map {
                ["playlist", "favorites"].contains($0.kind)
            } ?? false
            let detail = collection.map {
                MusicCollectionDetailContent(
                    title: $0.title,
                    owner: "我的歌单",
                    count: $0.count > 0 ? $0.count : page.tracks.count,
                    artwork: $0.artwork ?? page.tracks.first?.artwork
                )
            }
            detailHeader.configure(
                content: detail,
                filteredCount: orderedRows.count,
                searchQuery: page.searchQuery,
                playTitle: page.playAllTitle,
                isPlaying: page.isPlayingCollection,
                canPlay: page.canAct && !orderedRows.isEmpty,
                showsSave: false,
                canSave: false,
                showsEdit: showsEdit,
                canEdit: page.canEdit,
                showsRename: collection?.kind == "playlist",
                canRename: page.canRename,
                theme: theme
            )
        case .remote(let requested, let page):
            let collection = page.collection ?? requested
            let total = page.total ?? collection.count ?? page.tracks.count
            let owner = collection.creator.isEmpty ? collection.platform.title : collection.creator
            let current = isCurrentRemoteCollection(page)
            let playing = current && (model.phase == .playing || model.phase == .buffering)
            detailHeader.configure(
                content: MusicCollectionDetailContent(
                    title: collection.title,
                    owner: owner,
                    count: total,
                    artwork: collection.artwork ?? page.tracks.first?.artwork
                ),
                filteredCount: remoteFilter.isEmpty ? total : orderedRows.count,
                searchQuery: remoteFilter,
                playTitle: playing ? "暂停播放" : current ? "继续播放" : "播放全部",
                isPlaying: playing,
                canPlay: canActRemotely(page) && !orderedRows.isEmpty,
                showsSave: true,
                canSave: canActRemotely(page) && page.canSaveCollection,
                showsEdit: true,
                canEdit: canActRemotely(page) && !page.tracks.isEmpty,
                showsRename: false,
                canRename: false,
                theme: theme
            )
        }
        updateHeaderFrame()
    }

    private func renderNavigationActions() {
        guard isViewLoaded else { return }
        switch content {
        case .local(let page):
            let hasCollection = page.collection != nil
            searchButton?.isEnabled = hasCollection
            refreshButton?.isEnabled = !page.isLoading && !page.isActing && !page.isMissing
            if page.collection?.kind == "playlist" {
                deleteButton?.isEnabled = page.canRename
                pageHeader.setTrailing([searchButton, deleteButton, refreshButton].compactMap { $0 })
            } else {
                pageHeader.setTrailing([searchButton, refreshButton].compactMap { $0 })
            }
        case .remote:
            searchButton?.isEnabled = true
            if let page = remotePage {
                refreshButton?.isEnabled = !page.isLoading && !page.isActing &&
                    model.isAvailable && !model.isClosed
            }
            pageHeader.setTrailing([searchButton, refreshButton].compactMap { $0 })
        }
    }

    private func updateVisibleCells(force: Bool = false) {
        guard dataSource != nil else { return }
        for path in table.indexPathsForVisibleRows ?? [] {
            guard let id = dataSource.itemIdentifier(for: path), let row = rows[id],
                  let cell = table.cellForRow(at: path) as? MusicCollectionTrackCell else { continue }
            let active: Bool
            switch content {
            case .local:
                active = model.current?.track.songId.utf8.elementsEqual(row.track.songId.utf8) == true
            case .remote:
                active = model.current.map {
                    MusicMiniPlayerFavorites.sameSong($0.track, row.track)
                } == true
            }
            if force || cell.representedActive != active {
                configure(cell, row: row)
            }
        }
    }

    private func updateRowActionsRevision() {
        let key: RowActionsKey
        switch content {
        case .local(let page):
            key = .init(presentation: page.presentationID, filter: page.filterID,
                        remoteFilter: "", canEdit: page.canEdit)
        case .remote(_, let page):
            key = .init(presentation: page.actionPageID, filter: nil,
                        remoteFilter: remoteFilter, canEdit: false)
        }
        guard key != renderedRowActions else { return }
        renderedRowActions = key
        rowActionsRevision = UUID()
    }

    private func updateHeaderFrame() {
        guard isViewLoaded else { return }
        let width = table.bounds.width > 0 ? table.bounds.width : view.bounds.width
        let height = max(0.01, detailHeader.preferredHeight(for: width))
        if abs(detailHeader.frame.width - width) > 0.5 ||
            abs(detailHeader.frame.height - height) > 0.5 {
            detailHeader.frame = CGRect(x: 0, y: 0, width: width, height: height)
            table.tableHeaderView = detailHeader
        } else {
            detailHeader.setNeedsLayout()
        }
        updateScrollChrome()
    }

    /// The card fades over its own height; the action bar follows it, then pins below navigation.
    private func updateScrollChrome() {
        guard isViewLoaded else { return }
        let heroHeight = detailHeader.heroHeight(for: detailHeader.bounds.width)
        let offset = table.contentOffset.y
        let progress = heroHeight > 1 ? offset / heroHeight : 0
        detailHeader.setCollapseProgress(progress)
        let collapsed = detailHeader.hasContent && progress >= 0.98
        let nextTitle = collapsed ? collectionTitle : nil
        if pageHeader.title != nextTitle { pageHeader.title = nextTitle }
        let bar = detailHeader.actionBar
        bar.isHidden = !detailHeader.hasContent
        guard detailHeader.hasContent else { return }
        // The table starts below the app-owned header. Its own coordinate
        // space is zero at the header's bottom edge.
        let pin = table.frame.minY
        let heroBottom = table.convert(
            CGPoint(x: 0, y: detailHeader.frame.minY + heroHeight),
            to: view
        ).y
        let frame = CGRect(x: 0, y: max(pin, heroBottom),
                           width: view.bounds.width, height: bar.preferredHeight)
        if bar.frame != frame { bar.frame = frame }
        bar.updateBackgroundPosition()
    }

    private var collectionTitle: String? {
        switch content {
        case .local(let page): page.collection?.title
        case .remote(let requested, let page): (page.collection ?? requested).title
        }
    }

    private func configureFooter(
        text: String? = nil,
        previousTitle: String? = nil,
        nextTitle: String? = nil,
        loading: Bool = false
    ) {
        statusFooter.configure(
            text: text,
            previousTitle: previousTitle,
            nextTitle: nextTitle,
            loading: loading,
            visible: isPageVisible,
            theme: theme
        )
        updateFooterFrame()
    }

    private func updateFooterFrame() {
        guard isViewLoaded else { return }
        let width = table.bounds.width > 0 ? table.bounds.width : view.bounds.width
        let height = statusFooter.preferredHeight
        guard abs(statusFooter.frame.width - width) > 0.5 ||
              abs(statusFooter.frame.height - height) > 0.5 else {
            statusFooter.setNeedsLayout()
            return
        }
        statusFooter.frame = CGRect(x: 0, y: 0, width: width, height: height)
        table.tableFooterView = statusFooter
    }

    private func row(at path: IndexPath) -> Row? {
        dataSource.itemIdentifier(for: path).flatMap { rows[$0] }
    }

    private func updateSearch(_ query: String) {
        switch content {
        case .local(let page):
            page.searchQuery = query
        case .remote:
            remoteFilter = query
        }
        render()
    }

    private func playAll() {
        switch content {
        case .local(let page):
            guard !page.filteredTracks.isEmpty else { return }
            page.playFiltered(
                presentationID: page.presentationID,
                filterID: page.filterID,
                toggleCurrent: true
            )
        case .remote(_, let page):
            let current = isCurrentRemoteCollection(page)
            if current {
                let playing = model.phase == .playing || model.phase == .buffering
                performRemote(
                    page: page,
                    operation: playing ? "暂停播放" : "继续播放"
                ) { [weak self] in
                    await self?.model.togglePlayback()
                }
                return
            }
            guard let first = orderedRows.first?.row.track else { return }
            playRemote(first, page: page)
        }
    }

    private func localTrackMenu(
        track: MusicRemoteTrack,
        originalIndex: Int,
        presentationID: UUID,
        page: MusicCollectionViewModel
    ) -> UIMenu {
        var elements: [UIMenuElement] = [
            UIAction(
                title: "下一首播放",
                image: UIImage(systemName: "text.line.first.and.arrowtriangle.forward")
            ) { [weak page] _ in
                page?.enqueue(at: originalIndex, next: true, presentationID: presentationID)
            },
            UIAction(
                title: "添加到播放队列",
                image: PageIcon.image(.append)
            ) { [weak page] _ in
                page?.enqueue(at: originalIndex, next: false, presentationID: presentationID)
            },
        ]
        let targets = model.collections.filter {
            ["playlist", "favorites"].contains($0.kind) && $0.id != page.collectionID
        }
        if !targets.isEmpty {
            elements.append(UIMenu(
                title: "添加到歌单",
                image: UIImage(systemName: "folder"),
                children: targets.map { target in
                    UIAction(title: target.title) { [weak page] _ in
                        page?.add([track.ref], to: target, presentationID: presentationID)
                    }
                }
            ))
        }
        if page.canEdit {
            elements.append(UIAction(
                title: "从歌单移除",
                image: PageIcon.image(.remove),
                attributes: .destructive
            ) { [weak self, weak page] _ in
                guard let page else { return }
                self?.confirmLocalRemoval(
                    track: track,
                    index: originalIndex,
                    presentationID: presentationID,
                    page: page
                )
            })
        }
        return UIMenu(children: elements)
    }

    private func remoteTrackMenu(
        _ track: MusicRemoteTrack,
        page: MusicPublicBrowseViewModel
    ) -> UIMenu {
        let request = page.actionPageID
        var elements: [UIMenuElement] = [
            UIAction(
                title: "下一首播放",
                image: UIImage(systemName: "text.line.first.and.arrowtriangle.forward")
            ) { [weak self, weak page] _ in
                guard let page, page.actionPageID == request else { return }
                self?.enqueueRemote(track, next: true, page: page)
            },
            UIAction(
                title: "音源",
                image: UIImage(systemName: "slider.horizontal.3")
            ) { [weak self, weak page] _ in
                guard let page, page.actionPageID == request else { return }
                self?.showRemoteSources(track, page: page)
            },
            UIAction(
                title: "添加到播放队列",
                image: PageIcon.image(.append)
            ) { [weak self, weak page] _ in
                guard let page, page.actionPageID == request else { return }
                self?.enqueueRemote(track, next: false, page: page)
            },
        ]
        let targets = model.collections.filter { ["playlist", "favorites"].contains($0.kind) }
        if !targets.isEmpty {
            elements.append(UIMenu(
                title: "添加到歌单",
                image: UIImage(systemName: "folder"),
                children: targets.map { target in
                    UIAction(title: target.title) { [weak self, weak page] _ in
                        guard let page, page.actionPageID == request else { return }
                        self?.addRemote(track, to: target, page: page)
                    }
                }
            ))
        }
        return UIMenu(children: elements)
    }

    private func confirmLocalRemoval(
        track: MusicRemoteTrack,
        index: Int,
        presentationID: UUID,
        page: MusicCollectionViewModel
    ) {
        guard page.presentationID == presentationID else { return }
        view.endEditing(true)
        let alert = UIAlertController(
            title: "从歌单移除这首歌？",
            message: track.title,
            preferredStyle: .alert
        )
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "移除", style: .destructive) { [weak page] _ in
            guard let page, page.tracks.indices.contains(index),
                  page.tracks[index].ref == track.ref else { return }
            page.deleteMembers(at: IndexSet(integer: index), presentationID: presentationID)
        })
        present(alert, animated: true)
    }

    private func canActRemotely(_ page: MusicPublicBrowseViewModel) -> Bool {
        page === remotePage && page.loaded && page.contentIsCurrent &&
            !page.isLoading && !page.isActing && model.isAvailable && !model.isClosed
    }

    private func isCurrentRemoteCollection(_ page: MusicPublicBrowseViewModel) -> Bool {
        guard remoteFilter.isEmpty, let source = page.remoteSource,
              let current = model.current else { return false }
        return current.remoteSessionID == source.id
    }

    private func playRemote(
        _ track: MusicRemoteTrack,
        page: MusicPublicBrowseViewModel
    ) {
        let query = remoteFilter
        performRemote(page: page, operation: "播放歌曲") { [weak self, weak page] in
            guard let self, let page else { return }
            try await self.model.performPrimaryPlayback(presentation: nil) {
                let (source, index) = try await page.playbackSource(for: track)
                if query.isEmpty {
                    await self.model.playRemoteQueue(
                        source,
                        start: .init(index: index, track: track)
                    )
                } else {
                    let filteredIndex = try await page.filteredQueueIndex(
                        source: source,
                        index: index,
                        track: track,
                        query: query
                    )
                    let filtered = try await self.model.filteredRemoteQueue(source, query: query)
                    defer { self.model.releaseRemoteQueueSource(filtered) }
                    guard self.remoteFilter == query else { throw MusicServiceError.cancelled }
                    await self.model.playRemoteQueue(
                        filtered,
                        start: .init(index: filteredIndex, track: track)
                    )
                }
            }
        }
    }

    private func toggleRemoteFavorite(
        _ track: MusicRemoteTrack,
        page: MusicPublicBrowseViewModel
    ) {
        guard canActRemotely(page), let favorites, favorites.loaded,
              !favorites.loading, !favorites.busy else { return }
        favorites.toggle(track)
    }

    private func enqueueRemote(
        _ track: MusicRemoteTrack,
        next: Bool,
        page: MusicPublicBrowseViewModel
    ) {
        performRemote(
            page: page,
            operation: next ? "下一首播放" : "添加到播放队列",
            kind: .queue
        ) { [weak self, weak page] in
            guard let self, let page else { return }
            let refreshed = try await page.refreshedActionTrack(track)
            if next {
                await self.model.insertNext(refreshed)
            } else {
                await self.model.append(refreshed)
            }
        }
    }

    private func addRemote(
        _ track: MusicRemoteTrack,
        to collection: MusicCollection,
        page: MusicPublicBrowseViewModel
    ) {
        performRemote(page: page, operation: "添加到歌单", kind: .library) {
            [weak self, weak page] in
            guard let self, let page else { return }
            await self.model.add(try await page.refreshedActionTrack(track), to: collection)
        }
    }

    private func showRemoteSources(
        _ track: MusicRemoteTrack,
        page: MusicPublicBrowseViewModel
    ) {
        guard canPresentPageAlert, page === remotePage else { return }
        let request = page.actionPageID
        let sources = MusicSourcesViewController(
            model: model,
            tracks: [track],
            appearance: appearance,
            originIsCurrent: { [weak page] in page?.actionPageID == request },
            refresh: { [weak page] in
                guard let page, page.actionPageID == request else {
                    throw MusicServiceError.staleResult
                }
                return try await page.refreshedActionTrack($0)
            }
        )
        let wrapper = PageNavigationController(themeProvider: appearance)
        wrapper.setViewControllers([sources], animated: false)
        wrapper.modalPresentationStyle = .pageSheet
        wrapper.sheetPresentationController?.detents = [.medium(), .large()]
        wrapper.sheetPresentationController?.prefersGrabberVisible = true
        present(wrapper, animated: true)
    }

    private func saveRemoteCollection() {
        guard let page = remotePage, let collection = page.networkSaveCollection,
              let intent = page.makeActionIntent() else { return }
        let alert = UIAlertController(title: "收藏歌单", message: nil, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "收藏", style: .default) {
            [weak self, weak page] _ in
            guard let self, let page, page === self.remotePage else { return }
            self.runPageTask {
                await page.perform(
                    intent: intent,
                    operation: "收藏歌单",
                    kind: .library
                ) {
                    try await self.model.saveNetworkPlaylist(collection)
                }
            }
        })
        present(alert, animated: true)
    }

    private func performRemote(
        page: MusicPublicBrowseViewModel,
        operation: String,
        kind: MusicRequestTrace.Operation = .playback,
        _ action: @escaping @MainActor () async throws -> Void
    ) {
        guard page === remotePage, let intent = page.makeActionIntent() else { return }
        runPageTask { [weak self, weak page] in
            guard let self, let page, page === self.remotePage else { return }
            await page.perform(intent: intent, operation: operation, kind: kind, action)
        }
    }

    private func loadAutomaticRemoteNextPage() {
        guard let page = remotePage, isPageVisible, !automaticPaginationHeld,
              remoteFilter.isEmpty, page.error == nil, page.loaded,
              !page.isLoading, !page.isActing, !page.requiresExplicitNext,
              !page.usesPageNavigation, page.nextCursor != nil else { return }
        runPageTask { [weak self, weak page] in
            guard let self, let page, page === self.remotePage else { return }
            await page.appendNext()
        }
    }

    private func loadPreviousRemotePage() {
        guard remoteFilter.isEmpty, let page = remotePage, page.canPrevious else { return }
        runPageTask { [weak self, weak page] in
            guard let self, let page, page === self.remotePage else { return }
            await page.previous()
            self.scrollToTop()
        }
    }

    private func loadNextRemotePage() {
        guard remoteFilter.isEmpty, let page = remotePage else { return }
        runPageTask { [weak self, weak page] in
            guard let self, let page, page === self.remotePage else { return }
            if page.error != nil {
                await page.retry()
            } else {
                await page.appendNext()
            }
            if page.usesPageNavigation { self.scrollToTop() }
        }
    }

    private func scrollToTop() {
        guard !orderedRows.isEmpty else { return }
        table.setContentOffset(
            CGPoint(x: 0, y: -table.adjustedContentInset.top),
            animated: false
        )
    }

    private func showRemoteReadErrorIfNeeded() {
        guard let page = remotePage, let error = page.error else {
            shownReadError = nil
            return
        }
        let message = error.message(
            operation: page.errorContext,
            diagnosticID: page.errorDiagnosticID
        )
        guard canPresentPageAlert, shownReadError != message else { return }
        let request = page.actionPageID
        let alert = UIAlertController(title: "错误", message: message, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "重试", style: .default) {
            [weak self, weak page] _ in
            guard let self, let page, page === self.remotePage,
                  page.actionPageID == request else { return }
            self.shownReadError = nil
            self.runPageTask { await page.retry() }
        })
        shownReadError = message
        present(alert, animated: true)
    }

    private func showRemoteActionError() {
        guard let page = remotePage else { return }
        let message = page.actionError?.message(
            operation: page.actionOperation,
            diagnosticID: page.actionDiagnosticID
        )
        _ = showError(message)
    }

    private func showFavoriteError() {
        guard remotePage != nil, let favorites else { return }
        _ = showError(favorites.error?.localizedDescription) { [weak favorites] in
            favorites?.clearError()
        }
    }

    private func openEditor() {
        guard navigationController?.topViewController === self else { return }
        view.endEditing(true)
        let editor: MusicCollectionEditorViewController
        switch content {
        case .local(let page):
            guard let draft = page.beginEditing(presentationID: page.presentationID) else { return }
            editor = MusicCollectionEditorViewController(
                page: page,
                draft: draft,
                owner: model,
                origin: self,
                appearance: appearance
            )
        case .remote(let requested, let page):
            guard canActRemotely(page), !page.tracks.isEmpty else { return }
            editor = MusicCollectionEditorViewController(
                page: page,
                title: page.collection?.title ?? requested.title,
                owner: model,
                origin: self,
                appearance: appearance
            )
        }
        activeEditor = editor
        navigationController?.pushViewController(editor, animated: true)
    }

    private func rename() {
        guard let page = localPage,
              let ticket = page.requestRename(presentationID: page.presentationID) else { return }
        view.endEditing(true)
        let alert = UIAlertController(title: "重命名歌单", message: nil, preferredStyle: .alert)
        alert.addTextField { [weak page] field in field.text = page?.collection?.title }
        alert.addAction(UIAlertAction(title: "取消", style: .cancel) { [weak page] _ in
            page?.cancelRename(ticket)
        })
        alert.addAction(UIAlertAction(title: "保存", style: .default) {
            [weak page, weak alert] _ in
            let value = alert?.textFields?.first?.text?
                .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            guard let page else { return }
            guard !value.isEmpty else {
                page.cancelRename(ticket)
                return
            }
            page.rename(value, ticket: ticket)
        })
        present(alert, animated: true)
    }

    private func removeCollection() {
        guard let page = localPage, page.canRename else { return }
        view.endEditing(true)
        let presentationID = page.presentationID
        let alert = UIAlertController(
            title: "删除歌单？",
            message: page.collection?.title,
            preferredStyle: .alert
        )
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "删除", style: .destructive) { [weak page] _ in
            page?.removeCollection(presentationID: presentationID)
        })
        present(alert, animated: true)
    }

    private func refresh() {
        automaticPaginationHeld = false
        MusicArtworkView.retryFailedLoads(in: view)
        switch content {
        case .local(let page):
            let task = page.reload(preservingContent: true)
            runPageTask { [weak self] in
                await task?.value
                if !Task.isCancelled { self?.table.refreshControl?.endRefreshing() }
            }
        case .remote(_, let page):
            runPageTask { [weak self, weak page] in
                guard let self, let page, page === self.remotePage else { return }
                await page.refresh()
                if !Task.isCancelled { self.table.refreshControl?.endRefreshing() }
            }
        }
    }

    private func toggleSearch() {
        detailHeader.setSearchVisible(!detailHeader.isSearchVisible, focus: true)
    }

    private func requestCollectionRemoval() {
        removeCollection()
    }

    private func refreshFromNavigation() {
        refresh()
    }
}
#endif
