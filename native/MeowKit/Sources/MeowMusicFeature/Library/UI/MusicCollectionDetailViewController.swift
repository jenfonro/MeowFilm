#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
public final class MusicCollectionViewController: MusicPageViewController, UITableViewDelegate {
    enum Content {
        case local(MusicCollectionViewModel)
        case remote(MusicPublicBrowseCollection?, MusicPublicBrowseViewModel)
    }

    enum RowID: Hashable {
        case local(MusicReference)
        case remote(section: String, item: String)
    }

    struct Row {
        let track: MusicRemoteTrack
        let filteredIndex: Int
        let originalIndex: Int?
    }

    let model: MusicViewModel
    let content: Content
    let favorites: MusicMiniPlayerFavorites?
    let table = UITableView(frame: .zero, style: .plain)
    lazy var detailHeader = MusicCollectionDetailHeaderView(themeProvider: themeProvider)
    let statusFooter = MusicCollectionStatusFooterView()
    weak var activeEditor: MusicCollectionEditorViewController?
    var dataSource: UITableViewDiffableDataSource<Int, RowID>!
    var rows: [RowID: Row] = [:]
    var orderedRows: [(id: RowID, row: Row)] = []
    var remoteFilter = ""
    var searchButton: PageHeaderButton?
    var deleteButton: PageHeaderButton?
    var refreshButton: PageHeaderButton?
    let dailyBranches = SettingsSegmentedControl(items: ["默认推荐", "风格推荐"])
    var isRecommendation: Bool { remotePage?.request.dailyPlatform != nil }
    var recommendationTitle: String {
        remotePage?.request.dailyBranch == .style ? "风格推荐" : "每日推荐"
    }
    var shownReadError: String?
    var shownActionError: String?
    var automaticPaginationHeld = false
    var cachedLocalTracks: [MusicRemoteTrack]?
    var cachedLocalQuery = ""
    var localOriginalIndices: [MusicReference: Int] = [:]
    var rowActionsRevision = UUID()
    var renderedRowActions: RowActionsKey?
    struct RowActionsKey: Equatable {
        let presentation: UUID
        let filter: UUID?
        let remoteFilter: String
        let canEdit: Bool
    }

    var localPage: MusicCollectionViewModel? {
        guard case .local(let page) = content else { return nil }
        return page
    }

    var remotePage: MusicPublicBrowseViewModel? {
        guard case .remote(_, let page) = content else { return nil }
        return page
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

    public convenience init(
        model: MusicViewModel,
        recommendation request: MusicPublicBrowseRequest,
        browser: any MusicPublicBrowsing,
        appearance: MusicAppearancePreferencesStore,
        router: any MusicUIRouting
    ) {
        precondition(request.dailyPlatform != nil)
        let page = MusicPublicBrowseViewModel(
            service: browser, request: request,
            queuePage: { [weak model] source, cursor in
                guard let model else { throw MusicServiceError.closed }
                return try await model.remoteQueuePage(source, cursor: cursor)
            },
            releaseSource: { [weak model] in model?.releaseRemoteQueueSource($0) }
        )
        self.init(model: model, content: .remote(nil, page), appearance: appearance, router: router)
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

        if remotePage?.request.dailyPlatform == .netease {
            dailyBranches.style = .header
            dailyBranches.accessibilityIdentifier = "music.daily.branches"
            dailyBranches.selectedSegmentIndex = remotePage?.request.dailyBranch == .style ? 1 : 0
            dailyBranches.applyTheme(theme)
            dailyBranches.addAction(UIAction { [weak self] _ in self?.selectDailyBranch() }, for: .valueChanged)
            pageHeader.setTitleView(dailyBranches)
        }
        detailHeader.onStyle = { [weak self] in self?.openDailyStyle() }
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
        remotePage?.setDisplayVisible(true)
        if let page = remotePage, page.needsDateRefresh {
            runPageTask { await page.refreshForDateIfNeeded() }
        }
        renderState()
        detailHeader.resumePendingArtworkLoad()
        for case let cell as MusicCollectionTrackCell in table.visibleCells {
            cell.resumePendingArtworkLoad()
        }
        model.artworkImages.retryFailedVisible()
    }

    public override func pageDidBecomeHidden() {
        statusFooter.setDisplayVisible(false)
        remotePage?.setDisplayVisible(false)
        if remotePage != nil { automaticPaginationHeld = true }
        detailHeader.cancelPendingArtworkLoad()
        for case let cell as MusicCollectionTrackCell in table.visibleCells {
            cell.cancelPendingArtworkLoad()
        }
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
        detailHeader.cancelPendingArtworkLoad()
        for case let cell as MusicCollectionTrackCell in table.visibleCells {
            cell.cancelPendingArtworkLoad()
        }
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
        dailyBranches.applyTheme(theme)
        detailHeader.applyTheme(theme)
        statusFooter.applyTheme(theme)
        table.refreshControl?.tintColor = UIColor(theme.accent)
        (table.backgroundView as? UILabel)?.textColor = UIColor(theme.muted)
        updateVisibleCells(force: true)
    }

    func activateRemotePage() {
        guard isPageActive, let page = remotePage else { return }
        let available = model.isAvailable && !model.isClosed
        page.setAccess(available: available, generation: model.accountAccessGeneration)
        guard available else {
            render()
            return
        }
        page.appear()
        page.setDisplayVisible(isPageVisible)
        if !page.loaded, !page.isLoading, page.error == nil {
            runPageTask { [weak self] in await self?.remotePage?.loadIfNeeded() }
        }
        render()
    }

    func render() {
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

    func renderState() {
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

    func renderLocalState(_ page: MusicCollectionViewModel) {
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
        if let actionError = page.actionError, !actionError.isEmpty {
            if shownActionError != actionError {
                shownActionError = actionError
                enqueueError(actionError)
            }
        } else {
            shownActionError = nil
        }
        guard let error = page.error else {
            shownReadError = nil
            return
        }
        let message = error.localizedDescription
        guard canPresentPageAlert, shownReadError != message else { return }
        let presentation = page.presentationID
        shownReadError = message
        showRetryableError(message) { [weak self, weak page] in
            guard let self, let page, page.presentationID == presentation else { return }
            self.shownReadError = nil
            page.reload(preservingContent: true)
        }
    }

    func renderRemoteState(_ page: MusicPublicBrowseViewModel) {
        var text: String?
        var previousTitle: String?
        var nextTitle: String?
        if !remoteFilter.isEmpty {
            if orderedRows.isEmpty, page.loaded { text = "无匹配歌曲" }
        } else if page.isLoading {
            if !orderedRows.isEmpty { text = "加载中" }
        } else if page.error != nil {
            // Read errors and retry action are presented via unified alert overlay (`showRemoteReadErrorIfNeeded`).
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
        // State notifications may arrive before the rows render. Only continue
        // an actually empty source page, not a pending table snapshot.
        if page.tracks.isEmpty, page.loaded, !page.requiresExplicitNext {
            loadAutomaticRemoteNextPage()
        }
    }

    func renderHeader() {
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
                theme: theme,
                images: model.artworkImages
            )
        case .remote(let requested, let page):
            let collection = page.collection ?? requested
            let total = page.total ?? collection?.count ?? page.tracks.count
            let owner = collection.map { $0.creator.isEmpty ? $0.platform.title : $0.creator } ?? ""
            let current = isCurrentRemoteCollection(page)
            let playing = current && (model.phase == .playing || model.phase == .buffering)
            detailHeader.configure(
                content: MusicCollectionDetailContent(
                    title: collection?.title ?? recommendationTitle,
                    owner: owner,
                    count: total,
                    artwork: collection?.artwork ?? page.tracks.first?.artwork
                ),
                filteredCount: remoteFilter.isEmpty ? total : orderedRows.count,
                searchQuery: remoteFilter,
                playTitle: playing ? "暂停播放" : current ? "继续播放" : "播放全部",
                isPlaying: playing,
                canPlay: canActRemotely(page) && !orderedRows.isEmpty,
                showsSave: page.canSaveCollection,
                canSave: canActRemotely(page) && page.canSaveCollection,
                showsEdit: true,
                canEdit: canActRemotely(page) && !page.tracks.isEmpty,
                showsRename: false,
                canRename: false,
                theme: theme,
                images: model.artworkImages,
                recommendation: isRecommendation
            )
            let selectedStyle = page.dailyStyle?.tags.map(\.name).joined(separator: " · ") ?? ""
            let styleTitle: String? = page.request.dailyBranch == .style
                ? (selectedStyle.isEmpty ? "选择推荐风格" : selectedStyle) : nil
            detailHeader.configureStyle(title: styleTitle,
                enabled: canActRemotely(page) && page.remoteSource?.dailyStyle != nil)
            dailyBranches.selectedSegmentIndex = page.request.dailyBranch == .style ? 1 : 0
            dailyBranches.isEnabled = !page.isActing && model.isAvailable && !model.isClosed
        }
        updateHeaderFrame()
    }

    func renderNavigationActions() {
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

    func updateVisibleCells(force: Bool = false) {
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

    func updateRowActionsRevision() {
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

    func updateHeaderFrame() {
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

    /// The information fades over its own height; the action bar follows it, then pins below navigation.
    func updateScrollChrome() {
        guard isViewLoaded else { return }
        let heroHeight = detailHeader.heroHeight(for: detailHeader.bounds.width)
        let offset = table.contentOffset.y
        let progress = heroHeight > 1 ? offset / heroHeight : 0
        detailHeader.setCollapseProgress(progress)
        let collapsed = detailHeader.hasContent && progress >= 0.98
        let nextTitle = isRecommendation ? recommendationTitle : collapsed ? collectionTitle : nil
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

    var collectionTitle: String? {
        switch content {
        case .local(let page): page.collection?.title
        case .remote(let requested, let page): (page.collection ?? requested)?.title ?? recommendationTitle
        }
    }

    func configureFooter(
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

    func updateFooterFrame() {
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

    func row(at path: IndexPath) -> Row? {
        dataSource.itemIdentifier(for: path).flatMap { rows[$0] }
    }

}
#endif
