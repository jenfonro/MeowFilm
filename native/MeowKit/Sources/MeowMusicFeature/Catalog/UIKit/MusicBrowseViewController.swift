#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
public final class MusicBrowseViewController: MusicPageViewController,
    UICollectionViewDelegate, UISearchResultsUpdating
{
    private let model: MusicViewModel
    private let page: MusicPublicBrowseViewModel?
    private weak var router: (any MusicUIRouting)?
    private let embedded: Bool
    private var sections: [MusicPublicBrowseSection] = []
    private struct ItemID: Hashable {
        let section: String
        let item: String?
    }
    private var rows: [ItemID: (section: MusicPublicBrowseSection, item: MusicPublicBrowseItem)] = [:]
    private var dataSource: UICollectionViewDiffableDataSource<String, ItemID>!
    private var filter = ""
    private let search = UISearchController(searchResultsController: nil)
    private let tags = UIScrollView()
    private var tagsHeight: NSLayoutConstraint!
    private var fmPreviews: [String: MusicFMPreview] = [:]
    private var previewTasks: [String: Task<Void, Never>] = [:]
    private var previewRequests = Set<String>()
    private var previewAccess: UUID?
    private var collectionInfo: MusicPublicBrowseCollection?
    private var observation: Task<Void, Never>?
    private var observationID: UUID?
    private var observationAccess: UUID?
    private var observationNeedsRestart = false
    private var shownReadError: String?
    private var renderedFilters: [MusicPublicBrowseFilter] = []
    private var renderedFilter = ""
    private var automaticPaginationHeld = false
    private lazy var collection = UICollectionView(frame: .zero, collectionViewLayout: makeLayout())

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
        self.title = title
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        collection.backgroundColor = .clear
        collection.delegate = self
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
            self.runPageTask { [weak self] in
                await page.refresh()
                self?.collection.refreshControl?.endRefreshing()
            }
        }, for: .valueChanged)
        tags.showsHorizontalScrollIndicator = false
        view.addSubview(tags); view.addSubview(collection)
        tags.translatesAutoresizingMaskIntoConstraints = false
        collection.translatesAutoresizingMaskIntoConstraints = false
        tagsHeight = tags.heightAnchor.constraint(equalToConstant: 0)
        NSLayoutConstraint.activate([
            tags.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            tags.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            tags.topAnchor.constraint(equalTo: view.topAnchor), tagsHeight,
            collection.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            collection.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            collection.topAnchor.constraint(equalTo: tags.bottomAnchor),
            collection.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        if !embedded {
            search.searchResultsUpdater = self
            search.obscuresBackgroundDuringPresentation = false
            search.searchBar.placeholder = "歌单内搜索"
            if let page, case .detail = page.request { navigationItem.searchController = search }
        }
        if let page {
            observe(page.$sections.map { _ in }.eraseToAnyPublisher()) { [weak self] in self?.render() }
            observe(page.$collection.removeDuplicates().map { _ in }.eraseToAnyPublisher()) {
                [weak self] in self?.render()
            }
            observe(page) { [weak self] in self?.renderState() }
        }
        observe(model.accessChanges) { [weak self] in self?.activatePage() }
        observe(model.accountChanges) { [weak self] platform in self?.accountChanged(platform) }
        observe(model.playbackChanges) { [weak self] in self?.updateFMArtwork() }
        observe(model.metadataChanges) { [weak self] in self?.updateFMArtwork() }
        render()
    }

    public override func pageDidBecomeActive() { activatePage() }

    public override func pageDidBecomeInactive() {
        observationNeedsRestart = true
        observation?.cancel()
        for task in previewTasks.values { task.cancel() }
        previewRequests.removeAll()
        for cell in collection.visibleCells.compactMap({ $0 as? MusicBannerCarouselCell }) { cell.setActive(false) }
        page?.disappear()
    }

    public override func pageDidBecomeVisible() {
        for cell in collection.visibleCells.compactMap({ $0 as? MusicBannerCarouselCell }) { cell.setActive(true) }
        if let page, page.needsDateRefresh { runPageTask { await page.refreshForDateIfNeeded() } }
        renderState()
    }

    public override func pageDidBecomeHidden() {
        automaticPaginationHeld = true
        for cell in collection.visibleCells.compactMap({ $0 as? MusicBannerCarouselCell }) { cell.setActive(false) }
    }

    public override func pageDidFinishAppearing() {
        showReadErrorIfNeeded()
        showError(page?.actionError?.localizedDescription)
    }

    public override func applyTheme() {
        super.applyTheme()
        if isViewLoaded {
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
            cell.configure(section.items, theme: theme) { [weak self] item in
                if case .track = item.content { self?.play(item, section: section) }
                else { self?.open(item, section: section) }
            }
            cell.setActive(isPageVisible)
            return
        }
        guard let cell = cell as? MusicBrowseCell else { return }
        let fm: MusicHomeAction? = {
            if case .action(let action) = item.content, action.isPersonalFM { return action }; return nil
        }()
        let preview = fmPreviews[item.id]
        let artwork = fm.flatMap { model.currentFMArtwork($0) } ??
            (preview?.authorization.isActive == true ? preview?.artwork : nil) ?? item.artwork
        cell.configure(item: item, layout: section.layout, theme: theme, artworkURL: artwork,
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
                         collection: first && !embedded ? collectionInfo : nil,
                         theme: theme, onPlay: { [weak self] in
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
            if value.isPersonalFM { play(item, section: section) }
            else { router?.showPublicBrowse(.homeAction(value, title: item.title), title: item.title) }
        case .track: play(item, section: section)
        case .unavailable: break
        }
    }

    public func collectionView(_ collectionView: UICollectionView, willDisplay cell: UICollectionViewCell,
                               forItemAt indexPath: IndexPath) {
        (cell as? MusicBannerCarouselCell)?.setActive(isPageVisible)
        guard let row = row(at: indexPath) else { return }
        let item = row.item
        if case .action(let action) = item.content, action.isPersonalFM {
            loadFMPreview(item, action: action)
        }
        guard let page, indexPath.section == sections.count - 1,
              indexPath.item >= row.section.items.count - 3,
              isPageVisible, !automaticPaginationHeld, filter.isEmpty, page.error == nil,
              !page.requiresExplicitNext, !page.usesPageNavigation, page.nextCursor != nil else { return }
        runPageTask { await page.appendNext() }
    }

    public func collectionView(_ collectionView: UICollectionView, didEndDisplaying cell: UICollectionViewCell,
                               forItemAt indexPath: IndexPath) {
        (cell as? MusicBannerCarouselCell)?.setActive(false)
    }

    public func updateSearchResults(for searchController: UISearchController) {
        filter = searchController.searchBar.text?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        render()
    }

    public func scrollViewWillBeginDragging(_ scrollView: UIScrollView) { automaticPaginationHeld = false }

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
                await page.restoreHomeIfNeeded()
                await page.loadIfNeeded()
                guard !Task.isCancelled, self?.isPageActive == true else { return }
                await page.observeHomeChanges()
            }
        } else if !observationNeedsRestart, !page.loaded, !page.isLoading, page.error == nil {
            runPageTask { await page.loadIfNeeded() }
        }
        render()
        loadVisibleFMPreviews()
        for cell in collection.visibleCells.compactMap({ $0 as? MusicBannerCarouselCell }) {
            cell.setActive(isPageVisible)
        }
    }

    private func accountChanged(_ platform: MusicAccountPlatform) {
        if platform == .netease, !fmPreviews.isEmpty || !previewRequests.isEmpty || !previewTasks.isEmpty {
            fmPreviews.removeAll(); previewRequests.removeAll()
            for task in previewTasks.values { task.cancel() }
            updateFMArtwork()
            loadVisibleFMPreviews()
        }
        guard let page, page.invalidateAccount(platform) else { return }
        render()
        if isPageActive, model.isAvailable, !model.isClosed {
            runPageTask { await page.loadIfNeeded() }
        }
    }

    private func render() {
        guard isViewLoaded else { return }
        let previousCollection = collectionInfo
        let previousSections = sections
        let previousRows = rows
        collectionInfo = page?.collection
        sections = (page?.sections ?? []).map { section in
            guard !filter.isEmpty else { return section }
            let items = section.items.filter {
                if case .track(let track) = $0.content { return MusicRemoteCollectionFilter.matches(track, query: filter) }
                return true
            }
            return .init(id: section.id, title: section.title, items: items, layout: section.layout,
                         avatars: section.avatars, channel: section.channel, filters: section.filters,
                         selectedFilter: section.selectedFilter, error: section.error,
                         tabs: section.tabs, selectedTab: section.selectedTab)
        }
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
        if previous.sectionIdentifiers != snapshot.sectionIdentifiers ||
            previous.itemIdentifiers != snapshot.itemIdentifiers {
            dataSource.apply(snapshot, animatingDifferences: false)
        }
        if previousCollection != collectionInfo || previousSections.count != sections.count ||
            zip(previousSections, sections).contains(where: { pair in
                pair.0.id != pair.1.id || pair.0.layout != pair.1.layout ||
                    pair.0.tabs.isEmpty != pair.1.tabs.isEmpty
            }) {
            collection.collectionViewLayout.invalidateLayout()
        }
        let changedItems = Set(rows.keys.filter { id in
            guard let old = previousRows[id], let row = rows[id] else { return true }
            return old.section.layout != row.section.layout ||
                (id.item == nil ? !sameItems(old.section.items, row.section.items) : !sameItem(old.item, row.item))
        })
        let oldSections = Dictionary(uniqueKeysWithValues: previousSections.map { ($0.id, $0) })
        var changedHeaders = Set(sections.filter { section in
            guard let old = oldSections[section.id] else { return true }
            return !sameHeader(old, section)
        }.map(\.id))
        if previousCollection != collectionInfo, !embedded, let first = sections.first {
            changedHeaders.insert(first.id)
        }
        updateVisibleCells(items: changedItems)
        updateVisibleHeaders(sections: changedHeaders)
        renderState()
    }

    private func renderState() {
        guard isViewLoaded, tagsHeight != nil else { return }
        configureTags()
        if sections.isEmpty || sections.allSatisfy({ $0.items.isEmpty }) {
            setStatus(page?.isLoading == true ? "加载中" : page?.loaded == true ? "暂无内容" : nil)
        } else {
            collection.backgroundView = nil
        }
        configureActions()
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
               action.isPersonalFM, let cell = collection.cellForItem(at: path) {
                configure(cell, section: row.section, item: row.item)
            }
        }
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
        var actions: [UIMenuElement] = [
            UIAction(title: "刷新", image: UIImage(systemName: "arrow.clockwise")) { [weak self] _ in
                self?.automaticPaginationHeld = false
                self?.runPageTask { await page.refresh() }
            },
        ]
        if page.error != nil || sections.contains(where: { $0.error != nil }) {
            actions.append(UIAction(title: "重试") { [weak self] _ in
                self?.runPageTask { await page.retry() }
            })
        }
        if page.canSaveCollection {
            actions.append(UIAction(title: "保存到我的歌单", image: UIImage(systemName: "square.and.arrow.down")) {
                [weak self] _ in self?.saveCollection()
            })
        }
        if page.dailyStyle != nil, page.remoteSource != nil {
            actions.append(UIAction(title: "选择推荐风格") { [weak self] _ in self?.openDailyStyle() })
        }
        if page.canPrevious {
            actions.append(UIAction(title: "上一页") { [weak self] _ in self?.runPageTask { await page.previous() } })
        }
        if page.nextCursor != nil {
            actions.append(UIAction(title: page.usesPageNavigation ? "下一页" : "继续加载") { [weak self] _ in
                self?.runPageTask { await page.appendNext() }
            })
        }
        if case .homeAction(.daily, _) = page.request {
            actions.append(UIAction(title: "每日推荐") { [weak self] _ in
                self?.runPageTask { await page.selectDailyBranch(.default) }
            })
            actions.append(UIAction(title: "风格推荐") { [weak self] _ in
                self?.runPageTask { await page.selectDailyBranch(.style) }
            })
        }
        if !page.filters.isEmpty {
            actions.append(UIMenu(title: "筛选", children: page.filters.map { value in
                UIAction(title: value.title, state: value.id == page.selectedFilter ? .on : .off) {
                    [weak self] _ in self?.runPageTask { await page.selectFilter(value.id) }
                }
            }))
        }
        navigationItem.rightBarButtonItem = UIBarButtonItem(
            image: UIImage(systemName: "ellipsis.circle"), menu: UIMenu(children: actions))
    }

    private func setStatus(_ text: String?) {
        guard let text else { collection.backgroundView = nil; return }
        let label = UILabel()
        label.text = text; label.numberOfLines = 0; label.textAlignment = .center
        label.textColor = UIColor(theme.muted)
        collection.backgroundView = label
    }

    private func selectChoice(_ id: String, section: MusicPublicBrowseSection) {
        guard let page, let intent = page.makeActionIntent() else { return }
        if section.tabs.contains(where: { $0.id == id }) {
            page.selectSectionTab(id, sectionID: section.id, intent: intent)
        } else {
            runPageTask { await page.selectSectionFilter(id, sectionID: section.id, intent: intent) }
        }
    }

    private func play(_ item: MusicPublicBrowseItem, section: MusicPublicBrowseSection,
                      startingAt selected: MusicRemoteTrack? = nil) {
        guard let page else { return }
        let query = filter
        perform(operation: "播放歌曲") { [weak self] in
            guard let self else { return }
            try await self.model.performPrimaryPlayback(
                presentation: self.embedded ? self.router?.primaryPlaybackPresentation : nil
            ) {
                switch item.content {
                case .track(let track):
                    if page.canSaveCollection {
                        let (source, index) = try await page.playbackSource(for: track)
                        if query.isEmpty {
                            await self.model.playRemoteQueue(source, start: .init(index: index, track: track))
                        } else {
                            let filteredIndex = try await page.filteredQueueIndex(
                                source: source, index: index, track: track, query: query)
                            let filtered = try await self.model.filteredRemoteQueue(source, query: query)
                            defer { self.model.releaseRemoteQueueSource(filtered) }
                            guard self.filter == query else { throw MusicServiceError.cancelled }
                            await self.model.playRemoteQueue(filtered, start: .init(index: filteredIndex, track: track))
                        }
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
                    try await self.model.playHomeAction(value, title: item.title)
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
        guard let page, let intent = page.makeActionIntent() else { return }
        let alert = UIAlertController(title: "保存到我的歌单", message: nil, preferredStyle: .alert)
        alert.addTextField { [weak self] in $0.text = self?.collectionInfo?.title ?? self?.title }
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        alert.addAction(UIAlertAction(title: "保存", style: .default) { [weak self, weak alert] _ in
            let name = alert?.textFields?.first?.text?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            guard let self, !name.isEmpty else { return }
            self.runPageTask {
                await page.perform(intent: intent, operation: "收藏歌单", kind: .library) {
                    let (source, _) = try await page.playbackSource()
                    try await self.model.saveRemoteCollection(source, name: name)
                }
            }
        })
        present(alert, animated: true)
    }

    private func playSource(_ source: MusicRemoteQueueSource, selected: MusicRemoteTrack?) async throws {
        var cursor: String?, offset = 0, seen = Set<String>()
        for _ in 0..<100 {
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
            guard !page.items.isEmpty, offset < 2000, seen.insert(next).inserted else { throw MusicServiceError.limit }
            cursor = next
        }
        throw MusicServiceError.limit
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
        for item in tags.subviews where item is UIButton { item.removeFromSuperview() }
        tagsHeight.constant = values.count > 1 ? 44 : 0
        let font = UIFont.systemFont(ofSize: 13)
        var x: CGFloat = 16
        for value in values where values.count > 1 {
            let button = UIButton(type: .system)
            button.setTitle(value.title, for: .normal)
            button.titleLabel?.font = font
            button.tintColor = UIColor(value.id == page?.selectedFilter ? theme.accent : theme.ink)
            let width = (value.title as NSString).size(withAttributes: [.font: font]).width + 24
            button.frame = CGRect(x: x, y: 5, width: width, height: 34)
            button.backgroundColor = UIColor(theme.chip); button.layer.cornerRadius = 17
            button.addAction(UIAction { [weak self] _ in
                guard let self, let page = self.page else { return }
                self.runPageTask { await page.selectFilter(value.id) }
            }, for: .touchUpInside)
            tags.addSubview(button)
            x += width + 8
        }
        tags.contentSize = CGSize(width: x + 8, height: 44)
    }

    private func loadFMPreview(_ item: MusicPublicBrowseItem, action: MusicHomeAction) {
        guard isPageActive, model.isAvailable, !model.isClosed,
              !previewRequests.contains(item.id), previewTasks[item.id] == nil,
              let provider = model.publicBrowser as? any MusicFMPreviewServing else { return }
        let access = model.accountAccessGeneration
        previewRequests.insert(item.id)
        previewTasks[item.id] = Task { [weak self] in
            defer {
                if let self {
                    self.previewTasks[item.id] = nil
                    self.loadVisibleFMPreview(item.id)
                }
            }
            do {
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
        for indexPath in collection.indexPathsForVisibleItems {
            guard let row = row(at: indexPath) else { continue }
            loadVisibleFMPreview(row.item.id)
        }
    }

    private func loadVisibleFMPreview(_ id: String) {
        guard isPageActive else { return }
        for indexPath in collection.indexPathsForVisibleItems {
            guard let row = row(at: indexPath) else { continue }
            let item = row.item
            guard item.id == id, case .action(let action) = item.content,
                  action.isPersonalFM else { continue }
            loadFMPreview(item, action: action)
            return
        }
    }

    private func openDailyStyle() {
        guard canPresentPageAlert, let page, let source = page.remoteSource else { return }
        let editor = MusicDailyStyleViewController(page: page, source: source, appearance: appearance)
        let wrapper = PageNavigationController(themeProvider: appearance)
        wrapper.setViewControllers([editor], animated: false)
        wrapper.modalPresentationStyle = .pageSheet
        wrapper.sheetPresentationController?.detents = [.medium(), .large()]
        present(wrapper, animated: true)
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
                group = .horizontal(layoutSize: .init(widthDimension: .fractionalWidth(1),
                    heightDimension: .absolute((width - 32) / 2.56)), subitems: [item])
                scrolling = .groupPagingCentered
            case .grid:
                let count = width < 359 ? 2 : 3
                group = .horizontal(layoutSize: .init(widthDimension: .fractionalWidth(1),
                    heightDimension: .absolute((width - 32 - CGFloat(count - 1) * 12) / CGFloat(count) + 58)),
                    subitem: item, count: count)
                group.interItemSpacing = .fixed(12)
            case .rankingStack:
                group = .vertical(layoutSize: .init(widthDimension: .fractionalWidth(1),
                    heightDimension: .absolute(156)), subitems: [item])
            case .rankedSongs, .vipRankedSongs, .artistUpdates:
                group = .horizontal(layoutSize: .init(widthDimension: .absolute(max(240, (width - 32) * 0.91)),
                    heightDimension: .absolute(210)), subitems: [item])
                scrolling = .groupPaging
            case .list where trackList:
                group = .vertical(layoutSize: .init(widthDimension: .fractionalWidth(1),
                    heightDimension: .absolute(64)), subitems: [item])
            case .list, .playlistRail, .rankingSmall:
                let card = section.layout == .rankingSmall ? max(1, (width - 32) * 0.24) :
                    min(216, max(112, width * 0.36))
                group = .horizontal(layoutSize: .init(widthDimension: .absolute(card),
                    heightDimension: .absolute(card + 58)), subitems: [item])
                scrolling = .continuous
            }
            let result = NSCollectionLayoutSection(group: group)
            result.contentInsets = .init(top: 4, leading: 16, bottom: 20, trailing: 16)
            result.interGroupSpacing = 9
            result.orthogonalScrollingBehavior = scrolling
            let hero = index == 0 && !self.embedded && self.collectionInfo != nil
            let header = NSCollectionLayoutBoundarySupplementaryItem(
                layoutSize: .init(widthDimension: .fractionalWidth(1),
                                  heightDimension: .absolute(hero ? 190 : section.tabs.isEmpty ? 44 : 80)),
                elementKind: UICollectionView.elementKindSectionHeader, alignment: .top)
            result.boundarySupplementaryItems = [header]
            return result
        }
    }

    isolated deinit {
        observation?.cancel()
        for task in previewTasks.values { task.cancel() }
    }
}
#endif
