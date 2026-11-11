#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
public final class MusicBrowseViewController: MusicPageViewController, UICollectionViewDelegate {
    private static let sectionHorizontalInset: CGFloat = 16
    private let model: MusicViewModel
    private let page: MusicPublicBrowseViewModel?
    private weak var router: (any MusicUIRouting)?
    private let embedded: Bool
    private var sections: [MusicPublicBrowseSection] = []
    private struct ItemID: Hashable {
        let section: String
        let item: String?
    }
    private var showsSectionPlayback: Bool {
        guard let request = page?.request else { return false }
        switch request {
        case .discovery(.home): return false
        case .discovery(.channel(let channel, _)) where channel.kind == .cube: return false
        default: return true
        }
    }
    private var rows: [ItemID: (section: MusicPublicBrowseSection, item: MusicPublicBrowseItem)] = [:]
    private var dataSource: UICollectionViewDiffableDataSource<String, ItemID>!
    private lazy var tags = MusicChannelTagsView(themeProvider: appearance)
    private let tagsBackdrop = UIButton(type: .custom)
    private var tagsHeight: NSLayoutConstraint!
    private var tagsLayoutWidth: CGFloat = -1
    private var collectionTop: NSLayoutConstraint!
    private var fmPreviews: [String: MusicFMPreview] = [:]
    private var previewTasks: [String: Task<Void, Never>] = [:]
    private var previewRequests = Set<String>()
    private var previewAccess: UUID?
    private var observation: Task<Void, Never>?
    private var observationID: UUID?
    private var observationAccess: UUID?
    private var observationNeedsRestart = false
    private var shownReadError: String?
    private var renderedFilters: [MusicPublicBrowseFilter] = []
    private var renderedFilter = ""
    private var automaticPaginationHeld = false
    private var moreButton: PageHeaderButton?
    private var renderedActionsKey: BrowseActionsKey?
    private var appearanceNeedsUpdate = false
    private let statusView = UILabel()
    private let loadingSkeleton = MusicLoadingSkeletonView()
    private lazy var collection = MusicBrowseCollectionView(frame: .zero, collectionViewLayout: makeLayout())

    private struct BrowseActionsKey: Equatable {
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
            MusicArtworkView.retryFailedLoads(in: self.view)
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
            observe(page.$sections.map { _ in }.eraseToAnyPublisher()) { [weak self] in self?.render() }
            observe(page) { [weak self] in self?.renderState() }
        }
        observe(model.accessChanges) { [weak self] in self?.activatePage() }
        observe(model.accountChanges) { [weak self] platform in self?.accountChanged(platform) }
        observe(model.fmArtworkChanges) { [weak self] in self?.updateFMArtwork() }
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
        observationNeedsRestart = true
        observation?.cancel()
        for task in previewTasks.values { task.cancel() }
        previewRequests.removeAll()
        for cell in collection.visibleCells.compactMap({ $0 as? MusicBannerCarouselCell }) { cell.setActive(false) }
        page?.disappear()
    }

    public override func pageDidBecomeVisible() {
        activatePage()
        if appearanceNeedsUpdate { applyTheme() }
        updateFMArtwork()
        MusicArtworkView.retryFailedLoads(in: view)
        for cell in collection.visibleCells.compactMap({ $0 as? MusicBannerCarouselCell }) {
            cell.setActive(!tags.isExpanded)
        }
        if let page, page.needsDateRefresh { runPageTask { await page.refreshForDateIfNeeded() } }
        renderState()
    }

    public override func pageDidBecomeHidden() {
        collection.layer.removeAnimation(forKey: "music.home.refresh")
        loadingSkeleton.setDisplayVisible(false)
        automaticPaginationHeld = true
        tags.collapse()
        for cell in collection.visibleCells.compactMap({ $0 as? MusicBrowseCell }) { cell.resetPressFeedback() }
        page?.setDisplayVisible(false)
        for cell in collection.visibleCells.compactMap({ $0 as? MusicBannerCarouselCell }) { cell.setActive(false) }
    }

    public override func pageDidFinishAppearing() {
        showReadErrorIfNeeded()
        showError(page?.actionError?.localizedDescription)
    }

    public override func applyTheme() {
        super.applyTheme()
        if isViewLoaded {
            guard isPageVisible else { appearanceNeedsUpdate = true; return }
            appearanceNeedsUpdate = false
            updateVisibleCells()
            updateVisibleHeaders()
            if tagsHeight != nil { configureTags(force: true) }
            renderState()
        }
    }

    private func makeCell(in collectionView: UICollectionView, at indexPath: IndexPath, id: ItemID)
        -> UICollectionViewCell? {
        guard let row = rows[id] else { return nil }
        let cell = collectionView.dequeueReusableCell(
            withReuseIdentifier: row.section.layout == .banner ? "Banner" : MusicBrowseCell.reuseIdentifier,
            for: indexPath)
        configure(cell, section: row.section, item: row.item)
        return cell
    }

    private func configure(_ cell: UICollectionViewCell, section: MusicPublicBrowseSection,
                           item: MusicPublicBrowseItem) {
        if let cell = cell as? MusicBannerCarouselCell {
            cell.accessibilityIdentifier = "music.browse.banner.\(section.id)"
            cell.configure(section.items, theme: theme) { [weak self] item in
                if case .track = item.content { self?.play(item, section: section) }
                else { self?.open(item, section: section) }
            }
            cell.setActive(isPageVisible && !tags.isExpanded)
            return
        }
        guard let cell = cell as? MusicBrowseCell else { return }
        cell.configure(item: item, layout: section.layout, theme: theme, artworkURL: artworkURL(for: item),
                       onPreview: { [weak self] track in
            guard let self, let current = self.currentSection(for: item, id: section.id) else { return }
            self.play(item, section: current, startingAt: track)
        }) { [weak self] in
            guard let self, let current = self.currentSection(for: item, id: section.id) else { return }
            self.play(item, section: current)
        }
        cell.accessibilityIdentifier = "music.browse.item.\(item.id)"
    }

    private func makeHeader(in collectionView: UICollectionView, kind: String,
                            at indexPath: IndexPath) -> UICollectionReusableView? {
        guard let section = section(at: indexPath.section) else { return nil }
        let header = collectionView.dequeueReusableSupplementaryView(
            ofKind: kind, withReuseIdentifier: MusicBrowseHeader.reuseIdentifier, for: indexPath
        ) as! MusicBrowseHeader
        configure(header, section: section, first: indexPath.section == 0)
        return header
    }

    private func configure(_ header: MusicBrowseHeader, section: MusicPublicBrowseSection, first: Bool) {
        header.configure(section: section,
                         theme: theme, showsSectionPlayback: showsSectionPlayback, onPlay: { [weak self] in
            guard let self, let current = self.sections.first(where: { $0.id == section.id }),
                  let item = current.items.first else { return }
            self.play(item, section: current)
        }, onChannel: { [weak self] in
            if let channel = section.channel { self?.router?.selectChannel(channel) }
        }, onChoice: { [weak self] id in
            guard let self, let current = self.sections.first(where: { $0.id == section.id }) else { return }
            self.selectChoice(id, section: current)
        },
                         onRetry: { [weak self] in
            guard let self, let page = self.page else { return }
            self.runPageTask { await page.retry() }
        }, error: nil)
    }

    public func collectionView(_ collectionView: UICollectionView, didSelectItemAt indexPath: IndexPath) {
        guard let row = row(at: indexPath) else { return }
        open(row.item, section: row.section)
    }

    private func open(_ item: MusicPublicBrowseItem, section: MusicPublicBrowseSection) {
        switch item.content {
        case .collection(let value): router?.showPublicCollection(value)
        case .channel(let value): router?.selectChannel(value)
        case .daily(let value): router?.showPublicBrowse(.discovery(.daily(value)), title: item.title)
        case .action(let value):
            if value.isPersonalFM || value.isHeartbeat || value.isSimilar { play(item, section: section) }
            else { router?.showPublicBrowse(.homeAction(value, title: item.title), title: item.title) }
        case .track: play(item, section: section)
        case .unavailable: break
        }
    }

    public func collectionView(_ collectionView: UICollectionView, willDisplay cell: UICollectionViewCell,
                               forItemAt indexPath: IndexPath) {
        if isPageVisible { MusicArtworkView.retryFailedLoads(in: cell) }
        (cell as? MusicBannerCarouselCell)?.setActive(isPageVisible && !tags.isExpanded)
        guard let row = row(at: indexPath) else { return }
        let item = row.item
        if case .action(let action) = item.content, action.isPersonalFM {
            loadFMPreview(item, action: action)
        }
        guard indexPath.section == sections.count - 1, indexPath.item >= row.section.items.count - 3 else { return }
        loadAutomaticNextPage()
    }

    private func loadAutomaticNextPage() {
        guard let page, page.request != .discovery(.home),
              isPageVisible, !automaticPaginationHeld, page.error == nil, !page.isLoading,
              !page.requiresExplicitNext, !page.usesPageNavigation, page.nextCursor != nil else { return }
        runPageTask { await page.appendNext() }
    }

    public func collectionView(_ collectionView: UICollectionView, didEndDisplaying cell: UICollectionViewCell,
                               forItemAt indexPath: IndexPath) {
        (cell as? MusicBrowseCell)?.resetPressFeedback()
        (cell as? MusicBannerCarouselCell)?.setActive(false)
    }

    public func scrollViewWillBeginDragging(_ scrollView: UIScrollView) {
        automaticPaginationHeld = false
        for cell in collection.visibleCells.compactMap({ $0 as? MusicBrowseCell }) { cell.resetPressFeedback() }
    }

    public func collectionView(_ collectionView: UICollectionView,
                               contextMenuConfigurationForItemAt indexPath: IndexPath,
                               point: CGPoint) -> UIContextMenuConfiguration? {
        guard let page, let row = row(at: indexPath), case .track(let track) = row.item.content else { return nil }
        let request = page.actionPageID
        return UIContextMenuConfiguration(identifier: nil, previewProvider: nil) { [weak self] _ in
            guard let self, let page = self.page, page.actionPageID == request else { return nil }
            var actions: [UIMenuElement] = [
                UIAction(title: "下一首播放", image: UIImage(systemName: "text.line.first.and.arrowtriangle.forward")) {
                    [weak self] _ in
                    guard page.actionPageID == request else { return }
                    self?.perform(operation: "下一首播放", kind: .queue) {
                        await self?.model.insertNext(try await page.refreshedActionTrack(track))
                    }
                },
                UIAction(title: "音源", image: UIImage(systemName: "slider.horizontal.3")) { [weak self] _ in
                    guard page.actionPageID == request else { return }
                    self?.showSources(track)
                },
                UIAction(title: "添加到播放队列", image: UIImage(systemName: "text.badge.plus")) {
                    [weak self] _ in
                    guard page.actionPageID == request else { return }
                    self?.perform(operation: "添加到播放队列", kind: .queue) {
                        await self?.model.append(try await page.refreshedActionTrack(track))
                    }
                },
            ]
            actions.append(UIMenu(title: "添加到歌单", children: self.model.collections.filter {
                ["playlist", "favorites"].contains($0.kind)
            }.map { target in
                UIAction(title: target.title) { [weak self] _ in
                    guard page.actionPageID == request else { return }
                    self?.perform(operation: "添加到歌单", kind: .library) {
                        await self?.model.add(try await page.refreshedActionTrack(track), to: target)
                    }
                }
            }))
            return UIMenu(children: actions)
        }
    }

    private func activatePage() {
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

    private func accountChanged(_ platform: MusicAccountPlatform) {
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

    private func render() {
        guard isViewLoaded else { return }
        // Revocation still removes stale pixels immediately while a retained
        // page is hidden; nonempty display updates wait for the next appearance.
        guard isPageVisible || page?.sections.isEmpty != false else { return }
        let previousSections = sections
        let previousRows = rows
        sections = page?.sections ?? []
        rows = [:]
        var snapshot = NSDiffableDataSourceSnapshot<String, ItemID>()
        for section in sections {
            snapshot.appendSections([section.id])
            let items = section.layout == .banner ? Array(section.items.prefix(1)) : section.items
            let ids = items.map { ItemID(section: section.id, item: section.layout == .banner ? nil : $0.id) }
            for (id, item) in zip(ids, items) { rows[id] = (section, item) }
            snapshot.appendItems(ids, toSection: section.id)
        }
        let previous = dataSource.snapshot()
        let changedItems = Set(rows.keys.filter { id in
            guard let old = previousRows[id], let row = rows[id] else { return true }
            return old.section.layout != row.section.layout ||
                (id.item == nil ? !sameItems(old.section.items, row.section.items) : !sameItem(old.item, row.item))
        })
        let oldSections = Dictionary(uniqueKeysWithValues: previousSections.map { ($0.id, $0) })
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

    private func renderState() {
        guard isViewLoaded, isPageVisible, tagsHeight != nil else { return }
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
        showError(page?.actionError?.localizedDescription)
        showReadErrorIfNeeded()
    }

    private func row(at path: IndexPath) -> (section: MusicPublicBrowseSection, item: MusicPublicBrowseItem)? {
        dataSource?.itemIdentifier(for: path).flatMap { rows[$0] }
    }

    private func section(at index: Int) -> MusicPublicBrowseSection? {
        guard let ids = dataSource?.snapshot().sectionIdentifiers, ids.indices.contains(index) else { return nil }
        return sections.first { $0.id == ids[index] }
    }

    private func currentSection(for item: MusicPublicBrowseItem, id: String) -> MusicPublicBrowseSection? {
        guard let section = sections.first(where: { $0.id == id }),
              let current = section.items.first(where: { $0.id == item.id }), sameItem(item, current) else { return nil }
        return section
    }

    private func sameItems(_ lhs: [MusicPublicBrowseItem], _ rhs: [MusicPublicBrowseItem]) -> Bool {
        lhs.count == rhs.count && zip(lhs, rhs).allSatisfy { sameItem($0.0, $0.1) }
    }

    private func sameItem(_ lhs: MusicPublicBrowseItem, _ rhs: MusicPublicBrowseItem) -> Bool {
        guard lhs.id == rhs.id, lhs.title == rhs.title, lhs.subtitle == rhs.subtitle,
              lhs.artwork == rhs.artwork, lhs.previewSongs == rhs.previewSongs,
              lhs.recommendationReason == rhs.recommendationReason, lhs.coverLabel == rhs.coverLabel,
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

    private func sameRanking(_ lhs: MusicPublicBrowseRanking?, _ rhs: MusicPublicBrowseRanking?) -> Bool {
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

    private func sameHeader(_ lhs: MusicPublicBrowseSection, _ rhs: MusicPublicBrowseSection) -> Bool {
        lhs.title == rhs.title && lhs.avatars == rhs.avatars && lhs.channel == rhs.channel &&
            lhs.filters == rhs.filters && lhs.selectedFilter == rhs.selectedFilter &&
            lhs.tabs.map(\.id) == rhs.tabs.map(\.id) && lhs.tabs.map(\.title) == rhs.tabs.map(\.title) &&
            lhs.selectedTab == rhs.selectedTab && lhs.items.contains(where: {
                if case .track = $0.content { return true }; return false
            }) == rhs.items.contains(where: {
                if case .track = $0.content { return true }; return false
            })
    }

    private func updateVisibleCells(items: Set<ItemID>? = nil) {
        guard dataSource != nil else { return }
        for path in collection.indexPathsForVisibleItems {
            guard let id = dataSource.itemIdentifier(for: path), items?.contains(id) != false else { continue }
            if let row = row(at: path), let cell = collection.cellForItem(at: path) {
                configure(cell, section: row.section, item: row.item)
            }
        }
    }

    private func updateVisibleHeaders(sections changed: Set<String>? = nil) {
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

    private func updateFMArtwork() {
        guard dataSource != nil else { return }
        for path in collection.indexPathsForVisibleItems {
            if let row = row(at: path), case .action(let action) = row.item.content,
               action.isPersonalFM, let cell = collection.cellForItem(at: path) as? MusicBrowseCell {
                let url = artworkURL(for: row.item)
                if isPageVisible || url == nil { cell.updateArtwork(url) }
            }
        }
    }

    private func artworkURL(for item: MusicPublicBrowseItem) -> URL? {
        let current: URL?
        if case .action(let action) = item.content, action.isPersonalFM { current = model.currentFMArtwork(action) }
        else { current = nil }
        let preview = fmPreviews[item.id]
        return current ?? (preview?.authorization.isActive == true ? preview?.artwork : nil) ?? item.artwork
    }

    private func showReadErrorIfNeeded() {
        let section = sections.first { $0.error != nil }
        let error = page?.error ?? section?.error
        guard let error else { shownReadError = nil; return }
        let message = error.message(operation: page?.errorContext ?? "音乐内容读取",
            diagnosticID: page?.errorDiagnosticID ?? section.flatMap { page?.sectionDiagnosticIDs[$0.id] })
        guard canPresentPageAlert, shownReadError != message, let page else { return }
        let request = page.actionPageID
        let alert = UIAlertController(title: "错误", message: message, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "重试", style: .default) { [weak self] _ in
            guard let self, page.actionPageID == request else { return }
            self.shownReadError = nil
            self.runPageTask { await page.retry() }
        })
        shownReadError = message
        present(alert, animated: true)
    }

    private func configureActions() {
        guard !embedded, let page else { return }
        let hasError = page.error != nil || sections.contains(where: { $0.error != nil })
        let nextTitle: String? = page.request == .discovery(.home) ? nil : page.nextCursor.map { _ in
            page.usesPageNavigation ? "下一页" : "继续加载"
        }
        let key = BrowseActionsKey(
            pageID: page.actionPageID, request: page.request,
            hasError: hasError, canSave: page.canSaveCollection,
            canPrevious: page.canPrevious, nextTitle: nextTitle,
            filters: page.filters, selectedFilter: page.selectedFilter
        )
        if moreButton == nil {
            moreButton = PageHeaderButton(
                image: PageIcon.image(.more), accessibilityLabel: "更多",
                menu: UIMenu(children: [])
            )
        }
        guard let moreButton else { return }
        if renderedActionsKey == key {
            pageHeader.setTrailing([moreButton])
            return
        }
        var actions: [UIMenuElement] = [
            UIAction(title: "刷新", image: UIImage(systemName: "arrow.clockwise")) { [weak self] _ in
                guard let self, page.actionPageID == key.pageID else { return }
                self.automaticPaginationHeld = false
                MusicArtworkView.retryFailedLoads(in: self.view)
                self.runMenuAction(pageID: key.pageID) { await page.refresh() }
            },
        ]
        if hasError {
            actions.append(UIAction(title: "重试") { [weak self] _ in
                self?.runMenuAction(pageID: key.pageID) { await page.retry() }
            })
        }
        if page.canSaveCollection {
            actions.append(UIAction(title: "收藏歌单", image: UIImage(systemName: "star")) {
                [weak self] _ in
                guard page.actionPageID == key.pageID else { return }
                self?.saveCollection()
            })
        }
        if page.canPrevious {
            actions.append(UIAction(title: "上一页") { [weak self] _ in
                self?.runMenuAction(pageID: key.pageID) { await page.previous() }
            })
        }
        if let nextTitle {
            actions.append(UIAction(title: nextTitle) { [weak self] _ in
                self?.runMenuAction(pageID: key.pageID) { await page.appendNext() }
            })
        }
        if !page.filters.isEmpty {
            actions.append(UIMenu(title: "筛选", children: page.filters.map { value in
                UIAction(title: value.title, state: value.id == page.selectedFilter ? .on : .off) {
                    [weak self] _ in
                    self?.runMenuAction(pageID: key.pageID) { await page.selectFilter(value.id) }
                }
            }))
        }
        moreButton.menu = UIMenu(children: actions)
        renderedActionsKey = key
        pageHeader.setTrailing([moreButton])
    }

    private func runMenuAction(pageID: UUID, _ action: @escaping @MainActor () async -> Void) {
        guard page?.actionPageID == pageID else { return }
        runPageTask { [weak self] in
            guard self?.page?.actionPageID == pageID else { return }
            await action()
        }
    }

    private func setStatus(_ text: String?) {
        guard let text else {
            statusView.text = nil
            collection.backgroundView = nil
            return
        }
        statusView.text = text
        statusView.numberOfLines = 0
        statusView.textAlignment = .center
        statusView.textColor = UIColor(theme.muted)
        collection.backgroundView = statusView
    }

    private func selectChoice(_ id: String, section: MusicPublicBrowseSection) {
        guard let page, let intent = page.makeActionIntent() else { return }
        if section.tabs.contains(where: { $0.id == id }) {
            runPageTask { await page.selectSectionTab(id, sectionID: section.id, intent: intent) }
        } else {
            runPageTask { await page.selectSectionFilter(id, sectionID: section.id, intent: intent) }
        }
    }

    private func play(_ item: MusicPublicBrowseItem, section: MusicPublicBrowseSection,
                      startingAt selected: MusicRemoteTrack? = nil) {
        guard let page else { return }
        let account = model.searchAccessGeneration
        perform(operation: "播放歌曲") { [weak self] in
            guard let self else { return }
            try await self.model.performPrimaryPlayback(
                presentation: self.embedded ? self.router?.primaryPlaybackPresentation : nil
            ) {
                switch item.content {
                case .track(let track):
                    if page.canPlayRemoteQueue {
                        let (source, index) = try await page.playbackSource(for: track)
                        await self.model.playRemoteQueue(source, start: .init(index: index, track: track))
                    } else {
                        let tracks = section.items.compactMap { item -> MusicRemoteTrack? in
                            if case .track(let value) = item.content { return value }; return nil
                        }
                        guard let index = tracks.firstIndex(where: { $0.ref == track.ref }) else {
                            throw MusicServiceError.staleResult
                        }
                        await self.model.playDisplayed(tracks, index: index, origin: .section(title: section.title))
                    }
                case .collection(let value):
                    guard let browser = self.model.publicBrowser else { throw MusicServiceError.unavailable }
                    let detail = MusicPublicBrowseViewModel(service: browser, request: .detail(value),
                        queuePage: { [weak model = self.model] source, cursor in
                            guard let model else { throw MusicServiceError.closed }
                            return try await model.remoteQueuePage(source, cursor: cursor)
                        }, releaseSource: { [weak model = self.model] in model?.releaseRemoteQueueSource($0) })
                    detail.setAccess(available: self.model.isAvailable, generation: self.model.accountAccessGeneration)
                    detail.appear()
                    defer { detail.disappear() }
                    await detail.loadIfNeeded()
                    let (source, _) = try await detail.playbackSource()
                    try await self.playSource(source, selected: selected)
                case .action(let value):
                    guard self.model.searchAccessGeneration == account else { throw MusicServiceError.cancelled }
                    do { try await self.model.playHomeAction(value, title: item.title) }
                    catch {
                        guard self.model.searchAccessGeneration == account else { throw MusicServiceError.cancelled }
                        throw error
                    }
                case .daily(let value):
                    let source = try await self.model.openAccountQueue(
                        platform: value, kind: .recommendation(kind: "daily"), title: item.title)
                    defer { self.model.releaseRemoteQueueSource(source) }
                    let firstPage = try await self.model.remoteQueuePage(source, cursor: nil)
                    guard let first = firstPage.items.first else { throw MusicServiceError.notFound }
                    await self.model.playRemoteQueue(source, start: .init(index: 0, track: first))
                default: break
                }
            }
        }
    }

    private func perform(operation: String, kind: MusicRequestTrace.Operation = .playback,
                         _ action: @escaping @MainActor () async throws -> Void) {
        guard let page, let intent = page.makeActionIntent() else { return }
        runPageTask { await page.perform(intent: intent, operation: operation, kind: kind, action) }
    }

    private func saveCollection() {
        guard let page, let collection = page.networkSaveCollection, let intent = page.makeActionIntent() else { return }
        let alert = UIAlertController(title: "收藏歌单", message: nil, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "收藏", style: .default) { [weak self] _ in
            guard let self else { return }
            self.runPageTask {
                await page.perform(intent: intent, operation: "收藏歌单", kind: .library) {
                    try await self.model.saveNetworkPlaylist(collection)
                }
            }
        })
        present(alert, animated: true)
    }

    private func playSource(_ source: MusicRemoteQueueSource, selected: MusicRemoteTrack?) async throws {
        var cursor: String?, offset = 0, seen = Set<String>()
        while true {
            let page = try await model.remoteQueuePage(source, cursor: cursor)
            try Task.checkCancellation()
            let index = selected.flatMap { selected in page.items.firstIndex {
                $0.songId.utf8.elementsEqual(selected.songId.utf8) && $0.sources == selected.sources
            } } ?? (selected == nil && !page.items.isEmpty ? 0 : nil)
            if let index {
                await model.playRemoteQueue(source, start: .init(index: offset + index, track: page.items[index]))
                return
            }
            offset += page.items.count
            guard let next = page.cursor else { throw MusicServiceError.notFound }
            guard !page.items.isEmpty, seen.insert(next).inserted else { throw MusicServiceError.invalidResponse }
            cursor = next
        }
    }

    private func showSources(_ track: MusicRemoteTrack) {
        guard let page, canPresentPageAlert else { return }
        let request = page.actionPageID
        let sources = MusicSourcesViewController(model: model, tracks: [track], appearance: appearance,
            originIsCurrent: { [weak page] in page?.actionPageID == request },
            refresh: { [weak page] in
                guard let page, page.actionPageID == request else { throw MusicServiceError.staleResult }
                return try await page.refreshedActionTrack($0)
            })
        let wrapper = PageNavigationController(themeProvider: appearance)
        wrapper.setViewControllers([sources], animated: false)
        wrapper.modalPresentationStyle = .pageSheet
        wrapper.sheetPresentationController?.detents = [.medium(), .large()]
        wrapper.sheetPresentationController?.prefersGrabberVisible = true
        present(wrapper, animated: true)
    }

    private func configureTags(force: Bool = false) {
        let values = page?.filters ?? []
        let selected = page?.selectedFilter ?? ""
        guard force || values != renderedFilters || selected != renderedFilter else { return }
        renderedFilters = values; renderedFilter = selected
        tags.configure(filters: values, selectedID: selected, theme: theme) { [weak self] id in
            guard let self, self.isPageVisible, let page = self.page else { return }
            self.runPageTask { await page.selectFilter(id) }
        }
        updateTagsHeight()
    }

    private func updateTagsHeight() {
        guard isViewLoaded, tagsHeight != nil else { return }
        let width = view.bounds.width
        tagsLayoutWidth = width
        tags.updateLayout(width: width)
        let height = tags.preferredHeight(for: width)
        let collapsed = tags.collapsedHeight
        let wasExpanded = !tagsBackdrop.isHidden
        if abs(tagsHeight.constant - height) > 0.5 { tagsHeight.constant = height }
        if abs(collectionTop.constant - collapsed) > 0.5 { collectionTop.constant = collapsed }
        tags.isHidden = height == 0
        tagsBackdrop.isHidden = !tags.isExpanded
        collection.accessibilityElementsHidden = tags.isExpanded
        if wasExpanded != tags.isExpanded {
            for cell in collection.visibleCells.compactMap({ $0 as? MusicBannerCarouselCell }) {
                cell.setActive(isPageVisible && !tags.isExpanded)
            }
        }
    }

    private func isCurrentDisplayPreview(_ item: MusicPublicBrowseItem) -> Bool {
        guard let page, page.canLoadDisplayPreviews else { return false }
        // Published loading and the collection projection settle separately.
        // Never start optional work for a retained cell already replaced by
        // the model, even if the collection has not rendered that replacement.
        return page.sections.contains { $0.items.contains { sameItem($0, item) } }
    }

    private func loadFMPreview(_ item: MusicPublicBrowseItem, action: MusicHomeAction) {
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

    private func loadVisibleFMPreviews() {
        guard isPageVisible else { return }
        for indexPath in collection.indexPathsForVisibleItems {
            guard let row = row(at: indexPath) else { continue }
            loadVisibleFMPreview(row.item.id)
        }
    }

    private func loadVisibleFMPreview(_ id: String) {
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

    private func makeLayout() -> UICollectionViewLayout {
        UICollectionViewCompositionalLayout { [weak self] index, environment in
            guard let self, self.sections.indices.contains(index) else { return nil }
            let section = self.sections[index]
            let width = environment.container.effectiveContentSize.width
            let trackList = section.items.allSatisfy {
                if case .track = $0.content { return true }; return false
            }
            let item = NSCollectionLayoutItem(layoutSize: NSCollectionLayoutSize(
                widthDimension: .fractionalWidth(1), heightDimension: .fractionalHeight(1)))
            let group: NSCollectionLayoutGroup
            var scrolling = UICollectionLayoutSectionOrthogonalScrollingBehavior.none
            switch section.layout {
            case .featureRail:
                let card = min(216, max(112, width * 0.36))
                group = .horizontal(layoutSize: .init(widthDimension: .absolute(card),
                    heightDimension: .absolute(card / 0.775)), subitems: [item])
                scrolling = .continuous
            case .songColumns, .vipSongs:
                group = .vertical(layoutSize: .init(widthDimension: .absolute((width - 16) * 0.94),
                    heightDimension: .absolute(180)), subitem: item, count: 3)
                scrolling = .groupPaging
            case .banner:
                let bannerWidth = max(1, width - Self.sectionHorizontalInset * 2)
                group = .horizontal(layoutSize: .init(widthDimension: .absolute(bannerWidth),
                    heightDimension: .absolute(bannerWidth / 2.56)), subitems: [item])
                scrolling = .groupPagingCentered
            case .grid:
                let count = width < 359 ? 2 : 3
                group = .horizontal(layoutSize: .init(widthDimension: .fractionalWidth(1),
                    heightDimension: .absolute((width - 32 - CGFloat(count - 1) * 12) / CGFloat(count) + 42)),
                    subitem: item, count: count)
                group.interItemSpacing = .fixed(12)
            case .rankingStack:
                group = .vertical(layoutSize: .init(widthDimension: .fractionalWidth(1),
                    heightDimension: .absolute(156)), subitems: [item])
            case .artistUpdates:
                let card = max(240, (width - 32) * 0.91)
                group = .horizontal(layoutSize: .init(widthDimension: .absolute(card),
                    heightDimension: .absolute(card / 2.9)), subitems: [item])
                scrolling = .groupPaging
            case .rankedSongs:
                group = .horizontal(layoutSize: .init(widthDimension: .absolute(max(240, (width - 32) * 0.91)),
                    heightDimension: .absolute(210)), subitems: [item])
                scrolling = .groupPaging
            case .vipRankedSongs:
                group = .vertical(layoutSize: .init(widthDimension: .fractionalWidth(1),
                    heightDimension: .absolute(210)), subitems: [item])
            case .list where trackList:
                group = .vertical(layoutSize: .init(widthDimension: .fractionalWidth(1),
                    heightDimension: .absolute(64)), subitems: [item])
            case .list:
                let card = min(216, max(112, width * 0.36))
                group = .horizontal(layoutSize: .init(widthDimension: .absolute(card),
                    heightDimension: .absolute(card + 58)), subitems: [item])
                scrolling = .continuous
            case .playlistRail, .rankingSmall:
                let card = section.layout == .rankingSmall ? max(1, (width - 32) * 0.24) :
                    min(216, max(112, width * 0.36))
                group = .horizontal(layoutSize: .init(widthDimension: .absolute(card),
                    heightDimension: .absolute(card + 42)), subitems: [item])
                scrolling = .continuous
            }
            let result = NSCollectionLayoutSection(group: group)
            let headerHeight = MusicBrowseHeader.height(
                for: section, showsSectionPlayback: self.showsSectionPlayback)
            result.contentInsets = .init(top: index == 0 && headerHeight == 0 &&
                                         section.layout != .banner ? PageLayoutMetrics.contentGap : 4,
                                         leading: Self.sectionHorizontalInset, bottom: 20,
                                         trailing: Self.sectionHorizontalInset)
            result.interGroupSpacing = 9
            result.orthogonalScrollingBehavior = scrolling
            if headerHeight > 0 {
                let header = NSCollectionLayoutBoundarySupplementaryItem(
                    layoutSize: .init(widthDimension: .fractionalWidth(1), heightDimension: .absolute(headerHeight)),
                    elementKind: UICollectionView.elementKindSectionHeader, alignment: .top)
                result.boundarySupplementaryItems = [header]
            }
            return result
        }
    }

    nonisolated deinit {
        MainActor.assumeIsolated {
            observation?.cancel()
            for task in previewTasks.values { task.cancel() }
        }
    }
}

@MainActor
final class MusicBrowseCollectionView: UICollectionView {
    override func touchesShouldCancel(in view: UIView) -> Bool {
        view is UIButton || super.touchesShouldCancel(in: view)
    }
}
#endif
