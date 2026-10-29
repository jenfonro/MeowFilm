#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class MusicBrowseViewController: MusicPageViewController,
    UICollectionViewDataSource, UICollectionViewDelegate, UISearchResultsUpdating
{
    private let model: MusicViewModel
    private let page: MusicPublicBrowseViewModel?
    private weak var router: (any MusicUIRouting)?
    private let embedded: Bool
    private var sections: [MusicPublicBrowseSection] = []
    private var sourceSections: [MusicPublicBrowseSection] = []
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
        collection.dataSource = self
        collection.delegate = self
        collection.register(MusicBrowseCell.self, forCellWithReuseIdentifier: MusicBrowseCell.reuseIdentifier)
        collection.register(MusicBrowseHeader.self,
                            forSupplementaryViewOfKind: UICollectionView.elementKindSectionHeader,
                            withReuseIdentifier: MusicBrowseHeader.reuseIdentifier)
        collection.register(MusicBannerCarouselCell.self, forCellWithReuseIdentifier: "Banner")
        collection.refreshControl = UIRefreshControl()
        collection.refreshControl?.addAction(UIAction { [weak self] _ in
            guard let self, let page = self.page else { return }
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
        if let page { observe(page) { [weak self] in self?.render() } }
        observe(model) { [weak self] in self?.activatePage() }
        render()
    }

    public override func pageDidBecomeActive() { activatePage() }

    public override func pageDidBecomeInactive() {
        observationNeedsRestart = true
        observation?.cancel()
        for task in previewTasks.values { task.cancel() }
        previewRequests.removeAll()
        for cell in collection.visibleCells.compactMap({ $0 as? MusicBannerCarouselCell }) { cell.setActive(false) }
        // Immutable displayed rows remain in this UIKit controller so pop
        // reveals its real previous pixels. The model revokes page authority.
        page?.disappear(preservingContent: embedded)
    }

    public override func applyTheme() {
        super.applyTheme()
        if isViewLoaded {
            collection.reloadData()
            if tagsHeight != nil { configureTags() }
        }
    }

    public func numberOfSections(in collectionView: UICollectionView) -> Int { sections.count }
    public func collectionView(_ collectionView: UICollectionView, numberOfItemsInSection section: Int) -> Int {
        sections[section].layout == .banner ? (sections[section].items.isEmpty ? 0 : 1) : sections[section].items.count
    }

    public func collectionView(_ collectionView: UICollectionView, cellForItemAt indexPath: IndexPath)
        -> UICollectionViewCell {
        let section = sections[indexPath.section], item = section.items[indexPath.item]
        if section.layout == .banner {
            let cell = collectionView.dequeueReusableCell(withReuseIdentifier: "Banner", for: indexPath)
                as! MusicBannerCarouselCell
            cell.configure(section.items, theme: theme) { [weak self] item in
                if case .track = item.content { self?.play(item, section: section) }
                else { self?.open(item, section: section) }
            }
            return cell
        }
        let cell = collectionView.dequeueReusableCell(withReuseIdentifier: MusicBrowseCell.reuseIdentifier,
                                                      for: indexPath) as! MusicBrowseCell
        let fm: MusicHomeAction? = {
            if case .action(let action) = item.content, action.isPersonalFM { return action }; return nil
        }()
        let preview = fmPreviews[item.id]
        let artwork = fm.flatMap { model.currentFMArtwork($0) } ??
            (preview?.authorization.isActive == true ? preview?.artwork : nil) ?? item.artwork
        cell.configure(item: item, layout: section.layout, theme: theme, artworkURL: artwork,
                       onPreview: { [weak self] track in
            self?.play(item, section: section, startingAt: track)
        }) { [weak self] in
            self?.play(item, section: section)
        }
        cell.accessibilityIdentifier = "music.browse.item.\(item.id)"
        return cell
    }

    public func collectionView(_ collectionView: UICollectionView,
                               viewForSupplementaryElementOfKind kind: String,
                               at indexPath: IndexPath) -> UICollectionReusableView {
        let header = collectionView.dequeueReusableSupplementaryView(
            ofKind: kind, withReuseIdentifier: MusicBrowseHeader.reuseIdentifier, for: indexPath
        ) as! MusicBrowseHeader
        let section = sections[indexPath.section]
        header.configure(section: section,
                         collection: indexPath.section == 0 && !embedded ? collectionInfo : nil,
                         theme: theme, onPlay: { [weak self] in
            if let item = section.items.first { self?.play(item, section: section) }
        }, onChannel: { [weak self] in
            if let channel = section.channel { self?.router?.selectChannel(channel) }
        }, onChoice: { [weak self] id in self?.selectChoice(id, section: section) },
                         onRetry: { [weak self] in
            guard let self, let page = self.page else { return }
            self.runPageTask { await page.retry() }
        }, error: section.error.map {
            $0.message(operation: page?.errorContext ?? "音乐内容读取",
                       diagnosticID: page?.sectionDiagnosticIDs[section.id])
        })
        return header
    }

    public func collectionView(_ collectionView: UICollectionView, didSelectItemAt indexPath: IndexPath) {
        let section = sections[indexPath.section], item = section.items[indexPath.item]
        open(item, section: section)
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
        (cell as? MusicBannerCarouselCell)?.setActive(isPageActive)
        let item = sections[indexPath.section].items[indexPath.item]
        if case .action(let action) = item.content, action.isPersonalFM {
            loadFMPreview(item, action: action)
        }
        guard let page, indexPath.section == sections.count - 1,
              indexPath.item >= sections[indexPath.section].items.count - 3,
              !page.requiresExplicitNext, !page.usesPageNavigation, page.nextCursor != nil else { return }
        runPageTask { await page.appendNext() }
    }

    public func collectionView(_ collectionView: UICollectionView, didEndDisplaying cell: UICollectionViewCell,
                               forItemAt indexPath: IndexPath) {
        (cell as? MusicBannerCarouselCell)?.setActive(false)
        if let cell = cell as? MusicBrowseCell {
            previewTasks[cell.itemID]?.cancel()
            previewRequests.remove(cell.itemID)
        }
    }

    public func updateSearchResults(for searchController: UISearchController) {
        filter = searchController.searchBar.text?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        render()
    }

    public func collectionView(_ collectionView: UICollectionView,
                               contextMenuConfigurationForItemAt indexPath: IndexPath,
                               point: CGPoint) -> UIContextMenuConfiguration? {
        guard case .track(let track) = sections[indexPath.section].items[indexPath.item].content else { return nil }
        return UIContextMenuConfiguration(identifier: nil, previewProvider: nil) { [weak self] _ in
            guard let self, let page = self.page else { return nil }
            var actions: [UIMenuElement] = [
                UIAction(title: "下一首播放", image: UIImage(systemName: "text.line.first.and.arrowtriangle.forward")) {
                    [weak self] _ in self?.perform(operation: "下一首播放", kind: .queue) {
                        await self?.model.insertNext(try await page.refreshedActionTrack(track))
                    }
                },
                UIAction(title: "音源", image: UIImage(systemName: "slider.horizontal.3")) { [weak self] _ in
                    self?.showSources(track)
                },
                UIAction(title: "添加到播放队列", image: UIImage(systemName: "text.badge.plus")) {
                    [weak self] _ in self?.perform(operation: "添加到播放队列", kind: .queue) {
                        await self?.model.append(try await page.refreshedActionTrack(track))
                    }
                },
            ]
            actions.append(UIMenu(title: "添加到歌单", children: self.model.collections.filter {
                ["playlist", "favorites"].contains($0.kind)
            }.map { target in
                UIAction(title: target.title) { [weak self] _ in
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
            sourceSections = []; sections = []; collectionInfo = nil; render(); return
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
        } else if !observationNeedsRestart, !page.loaded, !page.isLoading {
            runPageTask { await page.loadIfNeeded() }
        }
        render()
        loadVisibleFMPreviews()
        for cell in collection.visibleCells.compactMap({ $0 as? MusicBannerCarouselCell }) { cell.setActive(true) }
    }

    private func render() {
        guard isViewLoaded else { return }
        if let page, !page.sections.isEmpty || page.loaded {
            sourceSections = page.sections
            collectionInfo = page.collection
        }
        sections = sourceSections.map { section in
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
        configureTags()
        collection.reloadData()
        if let error = page?.error {
            setStatus(error.message(operation: page?.errorContext ?? "音乐内容读取",
                                    diagnosticID: page?.errorDiagnosticID))
        } else if sections.isEmpty || sections.allSatisfy({ $0.items.isEmpty }) {
            setStatus(page?.isLoading == true ? "加载中" : page?.loaded == true ? "暂无内容" : nil)
        } else {
            collection.backgroundView = nil
        }
        configureActions()
        showError(page?.actionError?.localizedDescription)
    }

    private func configureActions() {
        guard !embedded, let page else { return }
        var actions: [UIMenuElement] = [
            UIAction(title: "刷新", image: UIImage(systemName: "arrow.clockwise")) { [weak self] _ in
                self?.runPageTask { await page.refresh() }
            },
        ]
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
        guard let page, isPageActive, presentedViewController == nil else { return }
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

    private func configureTags() {
        for item in tags.subviews where item is UIButton { item.removeFromSuperview() }
        let values = page?.filters ?? []
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
        guard isPageActive, !previewRequests.contains(item.id), previewTasks[item.id] == nil,
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
                self.collection.reloadData()
                if let value {
                    for await _ in value.authorization.revocations() {
                        guard !Task.isCancelled else { return }
                        if self.fmPreviews[item.id]?.authorization === value.authorization {
                            self.fmPreviews[item.id] = nil
                            if self.isPageActive { self.collection.reloadData() }
                        }
                        break
                    }
                }
            } catch { /* Optional display art never creates a playback error. */ }
        }
    }

    private func loadVisibleFMPreviews() {
        for indexPath in collection.indexPathsForVisibleItems {
            guard sections.indices.contains(indexPath.section),
                  sections[indexPath.section].items.indices.contains(indexPath.item) else { continue }
            loadVisibleFMPreview(sections[indexPath.section].items[indexPath.item].id)
        }
    }

    private func loadVisibleFMPreview(_ id: String) {
        guard isPageActive else { return }
        for indexPath in collection.indexPathsForVisibleItems {
            guard sections.indices.contains(indexPath.section),
                  sections[indexPath.section].items.indices.contains(indexPath.item) else { continue }
            let item = sections[indexPath.section].items[indexPath.item]
            guard item.id == id, case .action(let action) = item.content,
                  action.isPersonalFM else { continue }
            loadFMPreview(item, action: action)
            return
        }
    }

    private func openDailyStyle() {
        guard let page, let source = page.remoteSource else { return }
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
                                  heightDimension: .absolute((hero ? 190 : section.tabs.isEmpty ? 44 : 80) +
                                                            (section.error == nil ? 0 : 64))),
                elementKind: UICollectionView.elementKindSectionHeader, alignment: .top)
            result.boundarySupplementaryItems = [header]
            return result
        }
    }

    deinit {
        observation?.cancel()
        for task in previewTasks.values { task.cancel() }
    }
}
#endif
