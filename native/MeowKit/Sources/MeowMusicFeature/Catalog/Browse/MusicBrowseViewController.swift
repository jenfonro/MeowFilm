#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
public final class MusicBrowseViewController: MusicPageViewController, UICollectionViewDelegate {
    static let sectionHorizontalInset: CGFloat = 16
    let model: MusicViewModel
    let page: MusicPublicBrowseViewModel?
    weak var router: (any MusicUIRouting)?
    let embedded: Bool
    var sections: [MusicPublicBrowseSection] = []
    struct ItemID: Hashable, Sendable {
        let section: String
        let item: String?
    }
    struct PlaybackOrigin {
        let id = UUID()
        let page: UUID
        let section: String
        let item: MusicPublicBrowseItem
        let preview: MusicReference?
    }
    // The page owns the action lifetime; this identifies its originating control.
    var playbackOrigin: PlaybackOrigin?
    var showsSectionPlayback: Bool {
        guard let request = page?.request else { return false }
        switch request {
        case .discovery(.home): return false
        case .discovery(.channel(let channel, _)) where channel.kind == .cube: return false
        default: return true
        }
    }
    var rows: [ItemID: (section: MusicPublicBrowseSection, item: MusicPublicBrowseItem)] = [:]
    var dataSource: UICollectionViewDiffableDataSource<String, ItemID>!
    lazy var tags = MusicChannelTagsView(themeProvider: appearance)
    let tagsBackdrop = UIButton(type: .custom)
    var tagsHeight: NSLayoutConstraint!
    var tagsLayoutWidth: CGFloat = -1
    var collectionTop: NSLayoutConstraint!
    var fmPreviews: [String: MusicFMPreview] = [:]
    var previewTasks: [String: Task<Void, Never>] = [:]
    var previewRequests = Set<String>()
    var previewAccess: UUID?
    var observation: Task<Void, Never>?
    var observationID: UUID?
    var observationAccess: UUID?
    var observationNeedsRestart = false
    var renderedFilters: [MusicPublicBrowseFilter] = []
    var renderedFilter = ""
    var automaticPaginationHeld = false
    var moreButton: PageHeaderButton?
    var renderedActionsKey: BrowseActionsKey?
    let statusView = UILabel()
    let loadingSkeleton = MusicLoadingSkeletonView()
    lazy var collection = MusicBrowseCollectionView(frame: .zero, collectionViewLayout: makeLayout())

    struct BrowseActionsKey: Equatable {
        let pageID: UUID
        let request: MusicPublicBrowseRequest
        let hasError: Bool
        let canSave: Bool
        let canPrevious: Bool
        let nextTitle: String?
        let filters: [MusicPublicBrowseFilter]
        let selectedFilter: String
    }

    public init(title: String, model: MusicViewModel, browser: (any MusicPublicBrowsing)?,
                request: MusicPublicBrowseRequest, appearance: MusicAppearancePreferencesStore,
                router: any MusicUIRouting, embedded: Bool = false) {
        self.model = model; self.router = router; self.embedded = embedded
        page = browser.map {
            MusicPublicBrowseViewModel(service: $0, request: request,
                queuePage: { [weak model] source, cursor in
                    guard let model else { throw MusicServiceError.closed }
                    return try await model.remoteQueuePage(source, cursor: cursor)
                }, releaseSource: { [weak model] in model?.releaseRemoteQueueSource($0) })
        }
        super.init(appearance: appearance)
        pageTitle = title
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        collection.backgroundColor = .clear
        collection.delegate = self
        collection.delaysContentTouches = false
        collection.canCancelContentTouches = true
        collection.alwaysBounceVertical = true
        collection.register(MusicBrowseCell.self, forCellWithReuseIdentifier: MusicBrowseCell.reuseIdentifier)
        collection.register(MusicBrowseHeader.self,
                            forSupplementaryViewOfKind: UICollectionView.elementKindSectionHeader,
                            withReuseIdentifier: MusicBrowseHeader.reuseIdentifier)
        collection.register(MusicBannerCarouselCell.self, forCellWithReuseIdentifier: "Banner")
        dataSource = UICollectionViewDiffableDataSource(collectionView: collection) { [weak self] view, path, id in
            self?.makeCell(in: view, at: path, id: id)
        }
        dataSource.supplementaryViewProvider = { [weak self] view, kind, path in
            self?.makeHeader(in: view, kind: kind, at: path)
        }
        collection.refreshControl = UIRefreshControl()
        collection.refreshControl?.addAction(UIAction { [weak self] _ in
            guard let self else { return }
            guard let page = self.page else { self.collection.refreshControl?.endRefreshing(); return }
            self.automaticPaginationHeld = false
            self.model.artworkImages.retryFailedVisible()
            self.runPageTask { [weak self] in
                await page.refresh()
                self?.collection.refreshControl?.endRefreshing()
            }
        }, for: .valueChanged)
        tags.onExpansionChanged = { [weak self] in self?.updateTagsHeight() }
        tagsBackdrop.backgroundColor = UIColor(PageTheme.scrim)
        tagsBackdrop.accessibilityLabel = "收起标签"
        tagsBackdrop.accessibilityIdentifier = "music.channel.tags.dismiss"
        tagsBackdrop.isHidden = true
        tagsBackdrop.addAction(UIAction { [weak self] _ in self?.tags.collapse() }, for: .touchUpInside)
        view.addSubview(collection); view.addSubview(tagsBackdrop); view.addSubview(tags)
        tags.translatesAutoresizingMaskIntoConstraints = false
        tagsBackdrop.translatesAutoresizingMaskIntoConstraints = false
        collection.translatesAutoresizingMaskIntoConstraints = false
        tagsHeight = tags.heightAnchor.constraint(equalToConstant: 0)
        collectionTop = collection.topAnchor.constraint(equalTo: contentTopAnchor)
        NSLayoutConstraint.activate([
            tags.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            tags.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            tags.topAnchor.constraint(equalTo: contentTopAnchor), tagsHeight,
            tagsBackdrop.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            tagsBackdrop.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            tagsBackdrop.topAnchor.constraint(equalTo: contentTopAnchor),
            tagsBackdrop.bottomAnchor.constraint(equalTo: view.bottomAnchor),
            collection.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            collection.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            collectionTop,
            collection.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        if let page {
            observe(page.$sections.dropFirst().map { _ in }.eraseToAnyPublisher(), whenVisible: true) { [weak self] in self?.render() }
            let stateChanges = Publishers.MergeMany([
                page.$isLoading.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                page.$loaded.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                page.$isActing.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                page.$filters.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                page.$selectedFilter.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                page.$error.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                page.$actionError.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                page.$nextCursor.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                page.$requiresExplicitNext.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                page.$usesPageNavigation.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
                page.$collection.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            ])
            observe(stateChanges.eraseToAnyPublisher(), whenVisible: true) { [weak self] in self?.renderState() }
        }
        observe(model.accessChanges) { [weak self] in self?.activatePage() }
        observe(model.accountChanges) { [weak self] platform in self?.accountChanged(platform) }
        observe(model.fmArtworkChanges, whenVisible: true) { [weak self] in self?.updateFMArtwork() }
        render()
    }

    public override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        if abs(view.bounds.width - tagsLayoutWidth) > 0.5 { updateTagsHeight() }
    }

    public override func pageDidBecomeActive() { activatePage() }

    public override func pageDidBecomeInactive() {
        collection.layer.removeAnimation(forKey: "music.home.refresh")
        loadingSkeleton.setDisplayVisible(false)
        for cell in collection.visibleCells.compactMap({ $0 as? MusicBrowseCell }) { cell.setDisplayVisible(false) }
        for case let header as MusicBrowseHeader in collection.visibleSupplementaryViews(
            ofKind: UICollectionView.elementKindSectionHeader) { header.cancelPendingAvatarLoads() }
        observationNeedsRestart = true
        observation?.cancel()
        for task in previewTasks.values { task.cancel() }
        previewRequests.removeAll()
        for cell in collection.visibleCells.compactMap({ $0 as? MusicBannerCarouselCell }) { cell.setActive(false) }
        page?.disappear()
        playbackOrigin = nil
    }

    public override func pageDidBecomeVisible() {
        activatePage()
        updateFMArtwork()
        model.artworkImages.retryFailedVisible()
        updateVisiblePlaybackLoading()
        for case let header as MusicBrowseHeader in collection.visibleSupplementaryViews(
            ofKind: UICollectionView.elementKindSectionHeader) { header.resumePendingAvatarLoads() }
        for cell in collection.visibleCells.compactMap({ $0 as? MusicBannerCarouselCell }) {
            cell.setActive(!tags.isExpanded)
        }
        if let page, page.needsDateRefresh { runPageTask { await page.refreshForDateIfNeeded() } }
        renderState()
    }

    public override func pageDidBecomeHidden() {
        collection.layer.removeAnimation(forKey: "music.home.refresh")
        collection.refreshControl?.endRefreshing()
        loadingSkeleton.setDisplayVisible(false)
        automaticPaginationHeld = true
        tags.collapse()
        for cell in collection.visibleCells.compactMap({ $0 as? MusicBrowseCell }) {
            cell.resetPressFeedback()
            cell.setDisplayVisible(false)
        }
        for case let header as MusicBrowseHeader in collection.visibleSupplementaryViews(
            ofKind: UICollectionView.elementKindSectionHeader) { header.cancelPendingAvatarLoads() }
        page?.setDisplayVisible(false)
        for cell in collection.visibleCells.compactMap({ $0 as? MusicBannerCarouselCell }) { cell.setActive(false) }
    }

    public override func applyTheme() {
        super.applyTheme()
        if isViewLoaded {
            statusView.textColor = UIColor(theme.muted)
            loadingSkeleton.configure(
                loading: (page?.sections.allSatisfy({ $0.items.isEmpty }) ?? true) && page?.isLoading == true && page?.loaded == false,
                visible: isPageVisible,
                theme: theme
            )
            collection.refreshControl?.tintColor = UIColor(theme.accent)
            updateVisibleCells()
            updateVisibleHeaders()
            if tagsHeight != nil { configureTags(force: true) }
        }
    }

    func activatePage() {
        guard isPageActive, let page else { return }
        let access = model.accountAccessGeneration
        let available = model.isAvailable && !model.isClosed
        page.setAccess(available: available, generation: access)
        if !available {
            observationNeedsRestart = false
            observation?.cancel()
            render(); return
        }
        if observation != nil, observationAccess != access {
            observationNeedsRestart = true
            observation?.cancel()
        }
        if previewAccess != access {
            previewAccess = access
            fmPreviews.removeAll(); previewRequests.removeAll()
            for task in previewTasks.values { task.cancel() }
        }
        page.appear()
        page.setDisplayVisible(isPageVisible)
        guard isPageVisible else {
            render()
            return
        }
        if observation == nil {
            let id = UUID()
            observationID = id
            observationAccess = access
            observationNeedsRestart = false
            observation = Task { [weak self] in
                defer {
                    if let self, self.observationID == id {
                        let restart = self.observationNeedsRestart && self.isPageActive
                        self.observationID = nil
                        self.observationAccess = nil
                        self.observationNeedsRestart = false
                        self.observation = nil
                        if restart { self.activatePage() }
                    }
                }
                if self?.isPageVisible == true { await page.restoreHomeIfNeeded() }
                if self?.isPageVisible == true { await page.loadIfNeeded() }
                guard !Task.isCancelled, self?.isPageActive == true else { return }
                await page.observeHomeChanges()
            }
        } else if !observationNeedsRestart, !page.isLoading {
            runPageTask { [weak self] in
                guard self?.isPageVisible == true else { return }
                await page.restoreHomeIfNeeded()
                if self?.isPageVisible == true { await page.loadIfNeeded() }
            }
        }
        render()
        loadVisibleFMPreviews()
        for cell in collection.visibleCells.compactMap({ $0 as? MusicBannerCarouselCell }) {
            cell.setActive(isPageVisible && !tags.isExpanded)
        }
    }

    func accountChanged(_ platform: MusicAccountPlatform) {
        if platform == .netease, !fmPreviews.isEmpty || !previewRequests.isEmpty || !previewTasks.isEmpty {
            fmPreviews.removeAll(); previewRequests.removeAll()
            for task in previewTasks.values { task.cancel() }
            updateFMArtwork()
            if isPageVisible { loadVisibleFMPreviews() }
        }
        guard let page, page.invalidateAccount(platform) else { return }
        render()
        if isPageActive, isPageVisible, model.isAvailable, !model.isClosed {
            runPageTask { [weak self] in
                if self?.isPageVisible == true { await page.loadIfNeeded() }
            }
        }
    }

    func render() {
        guard isViewLoaded else { return }
        // Revocation still removes stale pixels immediately while a retained
        // page is hidden; nonempty display updates wait for the next appearance.
        guard isPageVisible || page?.sections.isEmpty != false else { return }
        let previousSections = sections
        let previousRows = rows
        var uniqueSections: [MusicPublicBrowseSection] = []
        var seenSections: Set<String> = []
        rows = [:]
        var builder = DiffableSnapshotBuilder<String, ItemID>()
        for section in page?.sections ?? [] {
            guard seenSections.insert(section.id).inserted else { continue }
            uniqueSections.append(section)
            let items = section.layout == .banner ? Array(section.items.prefix(1)) : section.items
            var ids: [ItemID] = []
            ids.reserveCapacity(items.count)
            for item in items {
                let id = ItemID(section: section.id, item: section.layout == .banner ? nil : item.id)
                guard rows[id] == nil else { continue }
                rows[id] = (section, item)
                ids.append(id)
            }
            _ = builder.appendSection(section.id, items: ids)
        }
        let snapshot = builder.build()
        sections = uniqueSections
        let previous = dataSource.snapshot()
        let changedItems = Set(rows.keys.filter { id in
            guard let old = previousRows[id], let row = rows[id] else { return true }
            return old.section.layout != row.section.layout ||
                (id.item == nil ? !sameItems(old.section.items, row.section.items) : !sameItem(old.item, row.item))
        })
        let oldSections = Dictionary(previousSections.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        let changedHeaders = Set(sections.filter { section in
            guard let old = oldSections[section.id] else { return true }
            return !sameHeader(old, section)
        }.map(\.id))
        // Fade only replacement of displayed home content. Appending a page
        // or publishing loading state must not animate the retained feed.
        let animate = page?.request == .discovery(.home) && !previousRows.isEmpty && !rows.isEmpty &&
            isPageVisible && collection.window != nil && !UIAccessibility.isReduceMotionEnabled &&
            (previous.itemIdentifiers != snapshot.itemIdentifiers.filter { previousRows[$0] != nil } ||
             previous.sectionIdentifiers != snapshot.sectionIdentifiers.filter { oldSections[$0] != nil } ||
             changedItems.contains { previousRows[$0] != nil } ||
             changedHeaders.contains { oldSections[$0] != nil })
        if animate {
            let transition = CATransition()
            transition.type = .fade
            transition.duration = 0.25
            transition.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
            collection.layer.add(transition, forKey: "music.home.refresh")
        } else if rows.isEmpty || UIAccessibility.isReduceMotionEnabled {
            collection.layer.removeAnimation(forKey: "music.home.refresh")
        }
        if previous.sectionIdentifiers != snapshot.sectionIdentifiers ||
            previous.itemIdentifiers != snapshot.itemIdentifiers {
            dataSource.apply(snapshot, animatingDifferences: false)
        }
        if previousSections.count != sections.count ||
            zip(previousSections, sections).contains(where: { pair in
                pair.0.id != pair.1.id || pair.0.layout != pair.1.layout ||
                    MusicBrowseHeader.height(for: pair.0, showsSectionPlayback: showsSectionPlayback) !=
                    MusicBrowseHeader.height(for: pair.1, showsSectionPlayback: showsSectionPlayback)
            }) {
            collection.collectionViewLayout.invalidateLayout()
        }
        updateVisibleCells(items: changedItems)
        updateVisibleHeaders(sections: changedHeaders)
        renderState()
        if animate { collection.layoutIfNeeded() }
    }

    func renderState() {
        guard isViewLoaded, isPageVisible, tagsHeight != nil else { return }
        updateVisiblePlaybackLoading()
        configureTags()
        if page?.sections.allSatisfy({ $0.items.isEmpty }) ?? true {
            if page?.isLoading == true, page?.loaded == false {
                loadingSkeleton.configure(loading: true, visible: isPageVisible, theme: theme)
                collection.backgroundView = loadingSkeleton
            } else {
                setStatus(page?.isLoading == true ? "加载中" : page?.loaded == true && page?.nextCursor == nil ? "暂无内容" : nil)
            }
            loadAutomaticNextPage()
        } else {
            collection.backgroundView = nil
        }
        configureActions()
        loadVisibleFMPreviews()
        updateVisibleArtworkDemand()
        showActionErrorIfNeeded()
        showReadErrorIfNeeded()
    }

    func row(at path: IndexPath) -> (section: MusicPublicBrowseSection, item: MusicPublicBrowseItem)? {
        dataSource?.itemIdentifier(for: path).flatMap { rows[$0] }
    }

    func section(at index: Int) -> MusicPublicBrowseSection? {
        guard let ids = dataSource?.snapshot().sectionIdentifiers, ids.indices.contains(index) else { return nil }
        return sections.first { $0.id == ids[index] }
    }

    func currentSection(for item: MusicPublicBrowseItem, id: String) -> MusicPublicBrowseSection? {
        guard let section = sections.first(where: { $0.id == id }),
              let current = section.items.first(where: { $0.id == item.id }), sameItem(item, current) else { return nil }
        return section
    }

    func sameItems(_ lhs: [MusicPublicBrowseItem], _ rhs: [MusicPublicBrowseItem]) -> Bool {
        lhs.count == rhs.count && zip(lhs, rhs).allSatisfy { sameItem($0.0, $0.1) }
    }

    func sameItem(_ lhs: MusicPublicBrowseItem, _ rhs: MusicPublicBrowseItem) -> Bool {
        guard lhs.id == rhs.id, lhs.title == rhs.title, lhs.subtitle == rhs.subtitle,
              lhs.artwork == rhs.artwork, lhs.previewSongs == rhs.previewSongs,
              lhs.recommendationReason == rhs.recommendationReason, lhs.coverLabel == rhs.coverLabel,
              lhs.requiresAccount == rhs.requiresAccount, lhs.kind == rhs.kind, lhs.label == rhs.label,
              lhs.presentation == rhs.presentation, lhs.coverRef == rhs.coverRef,
              lhs.contextText == rhs.contextText, lhs.playCount == rhs.playCount,
              sameRanking(lhs.ranking, rhs.ranking) else { return false }
        switch (lhs.content, rhs.content) {
        case (.collection(let lhs), .collection(let rhs)): return lhs == rhs
        case (.track(let lhs), .track(let rhs)): return lhs == rhs
        case (.channel(let lhs), .channel(let rhs)): return lhs == rhs
        case (.daily(let lhs), .daily(let rhs)): return lhs == rhs
        case (.action(let lhs), .action(let rhs)): return lhs == rhs
        case (.unavailable, .unavailable): return true
        default: return false
        }
    }

    func sameRanking(_ lhs: MusicPublicBrowseRanking?, _ rhs: MusicPublicBrowseRanking?) -> Bool {
        switch (lhs, rhs) {
        case (nil, nil): return true
        case (.some(let lhs), .some(let rhs)):
            return lhs.songArtwork == rhs.songArtwork && lhs.updateFrequency == rhs.updateFrequency &&
                lhs.preview.count == rhs.preview.count && zip(lhs.preview, rhs.preview).allSatisfy {
                    $0.0.id == $0.1.id && $0.0.title == $0.1.title && $0.0.artist == $0.1.artist &&
                        $0.0.artwork == $0.1.artwork && $0.0.rank == $0.1.rank && $0.0.lastRank == $0.1.lastRank
                }
        default: return false
        }
    }

    func sameHeader(_ lhs: MusicPublicBrowseSection, _ rhs: MusicPublicBrowseSection) -> Bool {
        lhs.title == rhs.title && lhs.avatars == rhs.avatars && lhs.channel == rhs.channel &&
            lhs.filters == rhs.filters && lhs.selectedFilter == rhs.selectedFilter &&
            lhs.tabs.map(\.id) == rhs.tabs.map(\.id) && lhs.tabs.map(\.title) == rhs.tabs.map(\.title) &&
            lhs.selectedTab == rhs.selectedTab && lhs.items.contains(where: {
                if case .track = $0.content { return true }; return false
            }) == rhs.items.contains(where: {
                if case .track = $0.content { return true }; return false
            })
    }

    func updateVisibleCells(items: Set<ItemID>? = nil) {
        guard dataSource != nil else { return }
        for path in collection.indexPathsForVisibleItems {
            guard let id = dataSource.itemIdentifier(for: path), items?.contains(id) != false else { continue }
            if let row = row(at: path), let cell = collection.cellForItem(at: path) {
                configure(cell, section: row.section, item: row.item)
            }
        }
    }

    func configurePlaybackLoading(_ cell: MusicBrowseCell, section: MusicPublicBrowseSection,
                                          item: MusicPublicBrowseItem) {
        let loading: MusicBrowseCell.PlaybackLoading?
        if let origin = playbackOrigin, page?.isActing == true, page?.actionPageID == origin.page,
           origin.section == section.id, sameItem(origin.item, item) {
            loading = origin.preview.map { .preview($0) } ?? .card
        } else { loading = nil }
        cell.setPlaybackLoading(loading, visible: isPageVisible)
    }

    func updateVisiblePlaybackLoading() {
        guard dataSource != nil else { return }
        for path in collection.indexPathsForVisibleItems {
            if let row = row(at: path), let cell = collection.cellForItem(at: path) as? MusicBrowseCell {
                configurePlaybackLoading(cell, section: row.section, item: row.item)
            }
        }
    }

    func updateVisibleHeaders(sections changed: Set<String>? = nil) {
        guard dataSource != nil else { return }
        let kind = UICollectionView.elementKindSectionHeader
        for path in collection.indexPathsForVisibleSupplementaryElements(ofKind: kind) {
            if let section = section(at: path.section),
               changed?.contains(section.id) != false,
               let header = collection.supplementaryView(forElementKind: kind, at: path) as? MusicBrowseHeader {
                configure(header, section: section, first: path.section == 0)
            }
        }
    }

    func updateFMArtwork() {
        guard dataSource != nil else { return }
        for path in collection.indexPathsForVisibleItems {
            if let row = row(at: path), case .action(let action) = row.item.content,
               action.isPersonalFM, let cell = collection.cellForItem(at: path) as? MusicBrowseCell {
                let url = artworkURL(for: row.item)
                if isPageVisible || url == nil { cell.updateArtwork(url) }
            }
        }
    }

    func artworkURL(for item: MusicPublicBrowseItem) -> URL? {
        let current: URL?
        if case .action(let action) = item.content, action.isPersonalFM { current = model.currentFMArtwork(action) }
        else { current = nil }
        let preview = fmPreviews[item.id]
        return current ?? (preview?.authorization.isActive == true ? preview?.artwork : nil) ?? item.artwork
    }

    func isCurrentDisplayPreview(_ item: MusicPublicBrowseItem) -> Bool {
        guard let page, page.canLoadDisplayPreviews else { return false }
        // Published loading and the collection projection settle separately.
        // Never start optional work for a retained cell already replaced by
        // the model, even if the collection has not rendered that replacement.
        return page.sections.contains { $0.items.contains { sameItem($0, item) } }
    }

    func loadFMPreview(_ item: MusicPublicBrowseItem, action: MusicHomeAction) {
        guard isPageActive, isPageVisible, model.isAvailable, !model.isClosed,
              item.artwork == nil, model.currentFMArtwork(action) == nil,
              !previewRequests.contains(item.id), previewTasks[item.id] == nil,
              isCurrentDisplayPreview(item),
              let provider = model.publicBrowser as? any MusicFMPreviewServing else { return }
        let access = model.accountAccessGeneration
        previewTasks[item.id] = Task { [weak self] in
            defer {
                if let self {
                    self.previewTasks[item.id] = nil
                    self.loadVisibleFMPreview(item.id)
                }
            }
            do {
                guard !Task.isCancelled, self?.isPageVisible == true, self?.isCurrentDisplayPreview(item) == true,
                      self?.model.accountAccessGeneration == access else { return }
                self?.previewRequests.insert(item.id)
                let value = try await provider.fmPreview(action)
                guard let self, !Task.isCancelled, self.isPageActive,
                      self.model.accountAccessGeneration == access else { return }
                self.fmPreviews[item.id] = value
                self.updateFMArtwork()
                if let value {
                    for await _ in value.authorization.revocations() {
                        guard !Task.isCancelled else { return }
                        if self.fmPreviews[item.id]?.authorization === value.authorization {
                            self.fmPreviews[item.id] = nil
                            if self.isPageActive { self.updateFMArtwork() }
                        }
                        break
                    }
                }
            } catch { /* Optional display art never creates a playback error. */ }
        }
    }

    func loadVisibleFMPreviews() {
        guard isPageVisible else { return }
        for indexPath in collection.indexPathsForVisibleItems {
            guard let row = row(at: indexPath) else { continue }
            loadVisibleFMPreview(row.item.id)
        }
    }

    func loadVisibleFMPreview(_ id: String) {
        guard isPageActive, isPageVisible else { return }
        for indexPath in collection.indexPathsForVisibleItems {
            guard let row = row(at: indexPath) else { continue }
            let item = row.item
            guard item.id == id, case .action(let action) = item.content,
                  action.isPersonalFM else { continue }
            loadFMPreview(item, action: action)
            return
        }
    }

    nonisolated deinit {
        MainActor.assumeIsolated {
            observation?.cancel()
            for task in previewTasks.values { task.cancel() }
        }
    }

}
#endif
