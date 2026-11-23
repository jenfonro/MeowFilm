#if os(iOS)
import UIKit
import MeowUI

@MainActor
extension MusicBrowseViewController {
    func showActionErrorIfNeeded() {
        guard let page, let error = page.actionError, let id = page.actionErrorID else { return }
        enqueueError(error.message(operation: page.actionOperation, diagnosticID: page.actionDiagnosticID), acknowledged: { [weak page] in
            page?.dismissActionError(id: id)
        })
    }

    func showReadErrorIfNeeded() {
        let section = sections.first { $0.error != nil }
        let error = page?.error ?? section?.error
        guard let error, let page else {
            clearErrorSource("read")
            return
        }
        let message = error.message(operation: page.errorContext,
            diagnosticID: page.errorDiagnosticID ?? section.flatMap { page.sectionDiagnosticIDs[$0.id] })
        let request = page.actionPageID
        syncErrorSource("read", message: message, retry: { [weak self] in
            guard let self, page.actionPageID == request else { return }
            self.runPageTask { await page.retry() }
        })
    }

    func configureActions() {
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
                self.model.artworkImages.retryFailedVisible()
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

    func runMenuAction(pageID: UUID, _ action: @escaping @MainActor () async -> Void) {
        guard page?.actionPageID == pageID else { return }
        runPageTask { [weak self] in
            guard self?.page?.actionPageID == pageID else { return }
            await action()
        }
    }

    func setStatus(_ text: String?) {
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

    func selectChoice(_ id: String, section: MusicPublicBrowseSection) {
        guard let page, let intent = page.makeActionIntent() else { return }
        if section.tabs.contains(where: { $0.id == id }) {
            runPageTask { await page.selectSectionTab(id, sectionID: section.id, intent: intent) }
        } else {
            runPageTask { await page.selectSectionFilter(id, sectionID: section.id, intent: intent) }
        }
    }

    func play(_ item: MusicPublicBrowseItem, section: MusicPublicBrowseSection,
                      startingAt selected: MusicRemoteTrack? = nil) {
        guard let page else { return }
        let account = model.searchAccessGeneration
        let origin = PlaybackOrigin(page: page.actionPageID, section: section.id, item: item, preview: selected?.ref)
        perform(operation: "播放歌曲") { [weak self] in
            guard let self else { return }
            self.playbackOrigin = origin
            self.updateVisiblePlaybackLoading()
            defer {
                if self.playbackOrigin?.id == origin.id {
                    self.playbackOrigin = nil
                    self.updateVisiblePlaybackLoading()
                }
            }
            try await self.model.performPrimaryPlayback(
                presentation: self.embedded ? self.router?.primaryPlaybackPresentation : nil
            ) {
                switch item.content {
                case .track(let track):
                    if page.canPlayRemoteQueue {
                        let (source, index) = try await page.playbackSource(for: track)
                        await self.model.playRemoteQueue(source, start: .init(index: index))
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
                    guard !firstPage.items.isEmpty else { throw MusicServiceError.notFound }
                    await self.model.playRemoteQueue(source, start: .init(index: 0))
                default: break
                }
            }
        }
    }

    func perform(operation: String, kind: MusicRequestTrace.Operation = .playback,
                         _ action: @escaping @MainActor () async throws -> Void) {
        guard let page, let intent = page.makeActionIntent() else { return }
        runPageTask { await page.perform(intent: intent, operation: operation, kind: kind, action) }
    }

    func saveCollection() {
        guard let page, let collection = page.networkSaveCollection, let intent = page.makeActionIntent() else { return }
        presentPageConfirmation(title: "收藏歌单", confirmTitle: "收藏", onConfirm: { [weak self] in
            guard let self else { return }
            self.runPageTask {
                await page.perform(intent: intent, operation: "收藏歌单", kind: .library) {
                    try await self.model.saveNetworkPlaylist(collection)
                }
            }
        })
    }

    func playSource(_ source: MusicRemoteQueueSource, selected: MusicRemoteTrack?) async throws {
        var cursor: String?, offset = 0, seen = Set<String>()
        while true {
            let page = try await model.remoteQueuePage(source, cursor: cursor)
            try Task.checkCancellation()
            let index = selected.flatMap { selected in page.items.firstIndex {
                $0.songId == selected.songId
            } } ?? (selected == nil && !page.items.isEmpty ? 0 : nil)
            if let index {
                await model.playRemoteQueue(source, start: .init(index: offset + index))
                return
            }
            offset += page.items.count
            guard let next = page.cursor else { throw MusicServiceError.notFound }
            guard seen.insert(next).inserted else { throw MusicServiceError.invalidResponse }
            cursor = next
        }
    }

    func showSources(_ track: MusicRemoteTrack) {
        guard let page, canPresentPageAlert else { return }
        let request = page.actionPageID
        let sources = MusicSourcesViewController(model: model, tracks: [track], appearance: appearance,
            originIsCurrent: { [weak page] in page?.actionPageID == request })
        presentPageSheet(sources)
    }

    func configureTags(force: Bool = false) {
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

    func updateTagsHeight() {
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

}
#endif
