#if os(iOS)
import UIKit
import MeowUI

@MainActor
extension MusicCollectionViewController {
    func updateSearch(_ query: String) {
        switch content {
        case .local(let page):
            page.searchQuery = query
        case .remote:
            remoteFilter = query
        }
        render()
    }

    func prepareDailyBranchSwitch() {
        view.endEditing(true)
        detailHeader.setSearchVisible(false)
        remoteFilter = ""
        table.setContentOffset(.zero, animated: false)
        automaticPaginationHeld = false
        clearErrorSource("read")
    }

    func selectDailyBranch() {
        guard let page = remotePage, !page.isActing else { return }
        let branch: MusicHomeAction.DailyBranch = dailyBranches.selectedSegmentIndex == 1 ? .style : .default
        guard page.request.dailyBranch != branch else { return }
        prepareDailyBranchSwitch()
        runPageTask { await page.selectDailyBranch(branch) }
    }

    func openDailyStyle() {
        guard canPresentPageAlert, let page = remotePage, canActRemotely(page),
              let source = page.remoteSource, source.dailyStyle != nil else { return }
        let editor = MusicDailyStyleViewController(page: page, source: source,
                                                  receipts: model.settingsReceipts, appearance: appearance)
        presentPageSheet(editor, prefersGrabberVisible: false)
    }

    func playAll() {
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

    func localTrackMenu(
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

    func remoteTrackMenu(
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

    func confirmLocalRemoval(
        track: MusicRemoteTrack,
        index: Int,
        presentationID: UUID,
        page: MusicCollectionViewModel
    ) {
        guard page.presentationID == presentationID else { return }
        view.endEditing(true)
        presentPageConfirmation(title: "从歌单移除这首歌？", message: track.title, confirmTitle: "移除", destructive: true, onConfirm: { [weak page] in
            guard let page, page.tracks.indices.contains(index),
                  page.tracks[index].ref == track.ref else { return }
            page.deleteMembers(at: IndexSet(integer: index), presentationID: presentationID)
        })
    }

    func canActRemotely(_ page: MusicPublicBrowseViewModel) -> Bool {
        page === remotePage && page.loaded && page.contentIsCurrent &&
            !page.isLoading && !page.isActing && model.isAvailable && !model.isClosed
    }

    func isCurrentRemoteCollection(_ page: MusicPublicBrowseViewModel) -> Bool {
        guard remoteFilter.isEmpty, let source = page.remoteSource,
              let current = model.current else { return false }
        return current.remoteSessionID == source.id
    }

    func playRemote(
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
                        start: .init(index: index)
                    )
                } else {
                    let filteredIndex = try await page.filteredQueueIndex(
                        source: source,
                        index: index,
                        query: query
                    )
                    let filtered = try await self.model.filteredRemoteQueue(source, query: query)
                    defer { self.model.releaseRemoteQueueSource(filtered) }
                    guard self.remoteFilter == query else { throw MusicServiceError.cancelled }
                    await self.model.playRemoteQueue(
                        filtered,
                        start: .init(index: filteredIndex)
                    )
                }
            }
        }
    }

    func toggleRemoteFavorite(
        _ track: MusicRemoteTrack,
        page: MusicPublicBrowseViewModel
    ) {
        guard canActRemotely(page), let favorites, favorites.loaded,
              !favorites.loading, !favorites.busy else { return }
        favorites.toggle(track)
    }

    func enqueueRemote(
        _ track: MusicRemoteTrack,
        next: Bool,
        page: MusicPublicBrowseViewModel
    ) {
        performRemote(
            page: page,
            operation: next ? "下一首播放" : "添加到播放队列",
            kind: .queue
        ) { [weak self, weak page] in
            guard let self, page != nil else { return }
            if next {
                await self.model.insertNext(track)
            } else {
                await self.model.append(track)
            }
        }
    }

    func addRemote(
        _ track: MusicRemoteTrack,
        to collection: MusicCollection,
        page: MusicPublicBrowseViewModel
    ) {
        performRemote(page: page, operation: "添加到歌单", kind: .library) {
            [weak self, weak page] in
            guard let self, page != nil else { return }
            await self.model.add(track, to: collection)
        }
    }

    func showRemoteSources(
        _ track: MusicRemoteTrack,
        page: MusicPublicBrowseViewModel
    ) {
        guard canPresentPageAlert, page === remotePage else { return }
        let request = page.actionPageID
        let sources = MusicSourcesViewController(
            model: model,
            tracks: [track],
            appearance: appearance,
            originIsCurrent: { [weak page] in page?.actionPageID == request }
        )
        presentPageSheet(sources)
    }

    func saveRemoteCollection() {
        guard let page = remotePage, let collection = page.networkSaveCollection,
              let intent = page.makeActionIntent() else { return }
        presentPageConfirmation(title: "收藏歌单", confirmTitle: "收藏", onConfirm: { [weak self, weak page] in
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
    }

    func performRemote(
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

    func loadAutomaticRemoteNextPage() {
        guard let page = remotePage, isPageVisible, !automaticPaginationHeld,
              remoteFilter.isEmpty, page.error == nil, page.loaded,
              !page.isLoading, !page.isActing, !page.requiresExplicitNext,
              !page.usesPageNavigation, page.nextCursor != nil else { return }
        runPageTask { [weak self, weak page] in
            guard let self, let page, page === self.remotePage else { return }
            await page.appendNext()
        }
    }

    func loadPreviousRemotePage() {
        guard remoteFilter.isEmpty, let page = remotePage, page.canPrevious else { return }
        runPageTask { [weak self, weak page] in
            guard let self, let page, page === self.remotePage else { return }
            await page.previous()
            self.scrollToTop()
        }
    }

    func loadNextRemotePage() {
        guard remoteFilter.isEmpty, let page = remotePage else { return }
        runPageTask { [weak self, weak page] in
            guard let self, let page, page === self.remotePage else { return }
            await page.appendNext()
            if page.usesPageNavigation { self.scrollToTop() }
        }
    }

    func scrollToTop() {
        guard !orderedRows.isEmpty else { return }
        table.setContentOffset(
            CGPoint(x: 0, y: -table.adjustedContentInset.top),
            animated: false
        )
    }

    func showRemoteReadErrorIfNeeded() {
        guard let page = remotePage, let error = page.error else {
            clearErrorSource("read")
            return
        }
        let message = error.message(
            operation: page.errorContext,
            diagnosticID: page.errorDiagnosticID
        )
        let request = page.actionPageID
        syncErrorSource("read", message: message, retry: { [weak self, weak page] in
            guard let self, let page, page === self.remotePage,
                  page.actionPageID == request else { return }
            self.runPageTask { await page.retry() }
        })
    }

    func showRemoteActionError() {
        guard let page = remotePage, let error = page.actionError, let id = page.actionErrorID else { return }
        let message = error.message(
            operation: page.actionOperation,
            diagnosticID: page.actionDiagnosticID
        )
        enqueueError(message, acknowledged: { [weak page] in page?.dismissActionError(id: id) })
    }

    func showFavoriteError() {
        guard remotePage != nil, let favorites else { return }
        enqueueError(favorites.error?.localizedDescription, acknowledged: { [weak favorites] in
            favorites?.clearError()
        })
    }

    func openEditor() {
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
                title: page.collection?.title ?? requested?.title ?? recommendationTitle,
                owner: model,
                origin: self,
                appearance: appearance
            )
        }
        activeEditor = editor
        navigationController?.pushViewController(editor, animated: true)
    }

    func rename() {
        guard let page = localPage,
              let ticket = page.requestRename(presentationID: page.presentationID) else { return }
        view.endEditing(true)
        presentPageInputAlert(
            title: "重命名歌单",
            fields: [PageAlertTextField(text: page.collection?.title)],
            confirmTitle: "保存",
            onConfirm: { [weak page] values in
                let value = (values.first ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                guard let page else { return }
                guard !value.isEmpty else {
                    page.cancelRename(ticket)
                    return
                }
                page.rename(value, ticket: ticket)
            },
            onCancel: { [weak page] in
                page?.cancelRename(ticket)
            }
        )
    }

    func removeCollection() {
        guard let page = localPage, page.canRename else { return }
        view.endEditing(true)
        let presentationID = page.presentationID
        presentPageConfirmation(
            title: "删除歌单？",
            message: page.collection?.title,
            confirmTitle: "删除",
            destructive: true,
            onConfirm: { [weak page] in
                page?.removeCollection(presentationID: presentationID)
            }
        )
    }

    func refresh() {
        automaticPaginationHeld = false
        model.artworkImages.retryFailedVisible()
        switch content {
        case .local(let page):
            let task = page.reload(preservingContent: true)
            runPageTask { [weak self] in
                defer { self?.table.refreshControl?.endRefreshing() }
                await task?.value
            }
        case .remote(_, let page):
            runPageTask { [weak self, weak page] in
                defer { self?.table.refreshControl?.endRefreshing() }
                guard let self, let page, page === self.remotePage else { return }
                await page.refresh()
            }
        }
    }

    func toggleSearch() {
        detailHeader.setSearchVisible(!detailHeader.isSearchVisible, focus: true)
    }

    func requestCollectionRemoval() {
        removeCollection()
    }

    func refreshFromNavigation() {
        refresh()
    }
}
#endif
