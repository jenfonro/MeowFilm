import Foundation

@MainActor
extension MusicPublicBrowseViewModel {
    /// UIKit reports actual visible cards. Retained pages/tabs share the same
    /// content snapshot, but configuring a cell alone never starts a read.
    func setVisibleArtworkItems(_ items: [MusicPublicBrowseItem]) {
        let current = sections.flatMap(\.items)
        artworkDemand = Set(items.compactMap { item in
            guard displayVisible, item.artwork == nil, let ref = item.coverRef,
                  current.contains(where: { $0.id == item.id && $0.coverRef == ref && $0.artwork == nil }) else { return nil }
            return ref
        })
        let unresolved = artworkDemand.filter { artworkReads[$0] == nil }
        if !unresolved.isEmpty {
            let known = (sections + loadedPages.values.flatMap { $0.page.sections }).flatMap {
                $0.items + $0.tabs.flatMap(\.items)
            }.compactMap { item -> MusicPublicBrowseCollection? in
                if case .collection(let value) = item.content { return value }; return nil
            } + loadedPages.values.compactMap(\.collection)
            for ref in unresolved {
                if let cover = known.first(where: {
                    $0.platform == ref.platform && $0.kind == ref.kind && $0.nativeID == ref.id && $0.artwork != nil
                })?.artwork { artworkReads[ref] = .settled(cover) }
            }
        }
        publishArtwork()
        loadArtworkDemand()
    }

    func loadArtworkDemand() {
        guard !closed, visible, displayVisible, contentIsCurrent, canLoadDisplayPreviews, !isLoading,
              artworkTask == nil, let reader = service as? any MusicPublicPlaylistSummaryReading else { return }
        let missing = artworkDemand.filter { artworkReads[$0] == nil }
            .sorted { ($0.platform.rawValue, $0.id) < ($1.platform.rawValue, $1.id) }
        guard let first = missing.first else { return }
        let batch = Array(missing.filter { $0.platform == first.platform }.prefix(30))
        let own = artworkEpoch
        for ref in batch { artworkReads[ref] = .loading }
        artworkTask = Task { [weak self] in
            let result: Result<[MusicPublicBrowseCollection], Error>
            do { result = .success(try await reader.publicPlaylistSummaries(platform: first.platform, ids: batch.map(\.id))) }
            catch { result = .failure(error) }
            guard let self, !Task.isCancelled, !self.closed, self.artworkEpoch == own else { return }
            for ref in batch {
                switch result {
                case .success(let rows):
                    self.artworkReads[ref] = .settled(rows.first {
                        $0.platform == ref.platform && $0.kind == ref.kind && $0.nativeID == ref.id && $0.artwork != nil
                    }?.artwork)
                case .failure(let error): self.artworkReads[ref] = .failed(MusicServiceError.sanitized(error))
                }
            }
            self.artworkTask = nil
            self.publishArtwork()
            self.loadArtworkDemand()
        }
    }

    func publishArtwork() {
        guard displayVisible else { return }
        let covers = artworkReads.compactMapValues { state -> URL? in
            if case .settled(let url) = state { return url }; return nil
        }
        guard !covers.isEmpty else { return }
        let needsFill: (MusicPublicBrowseSection) -> Bool = { section in
            (section.items + section.tabs.flatMap(\.items)).contains {
                $0.artwork == nil && $0.coverRef.flatMap { covers[$0] } != nil
            }
        }
        if sections.contains(where: needsFill) { sections = sections.map { $0.fillingArtwork(covers) } }
        for key in Array(loadedPages.keys) {
            guard var value = loadedPages[key], value.page.sections.contains(where: needsFill) else { continue }
            value.page = .init(sections: value.page.sections.map { $0.fillingArtwork(covers) },
                cursor: value.page.cursor, filters: value.page.filters, selectedFilter: value.page.selectedFilter,
                notice: value.page.notice, consumed: value.page.consumed)
            loadedPages[key] = value
        }
    }

    func retireArtwork() {
        artworkEpoch = UUID()
        artworkTask?.cancel(); artworkTask = nil
        artworkDemand = []; artworkReads = [:]
    }

    func observeSongs() {
        guard !closed, visible, songObservation == nil,
              let repository = service as? any MusicSongRepositoryReading else { return }
        songObservation = Task { [weak self] in
            let changes = await repository.songChanges()
            await self?.refreshSongProjections(repository)
            for await changed in changes {
                guard !Task.isCancelled else { return }
                await self?.refreshSongProjections(repository, changed: changed)
            }
        }
    }

    func refreshSongProjections(_ repository: any MusicSongRepositoryReading, changed: Set<String>? = nil) async {
        guard !Task.isCancelled, !closed, visible else { return }
        let projection = UUID(), page = pageEpoch
        let songs: [String: MusicRemoteTrack]
        let referenced = (sections + loadedPages.values.flatMap { $0.page.sections })
            .reduce(into: Set<String>()) { $0.formUnion($1.songIDs) }
        let ids = changed.map { referenced.intersection($0) } ?? referenced
        guard !ids.isEmpty else { return }
        songProjectionID = projection
        do { songs = try await repository.repositoryTracks(songIDs: ids) }
        catch { return } // A display read never reverses a committed mutation.
        guard !Task.isCancelled, !closed, visible,
              pageEpoch == page, songProjectionID == projection else { return }
        sections = sections.map { $0.projected(songs) }
        for key in Array(loadedPages.keys) {
            guard var value = loadedPages[key] else { continue }
            value.page = .init(sections: value.page.sections.map { $0.projected(songs) },
                cursor: value.page.cursor, filters: value.page.filters, selectedFilter: value.page.selectedFilter,
                notice: value.page.notice, consumed: value.page.consumed)
            loadedPages[key] = value
        }
    }
}

private extension MusicPublicBrowseSection {
    func fillingArtwork(_ covers: [MusicPublicBrowseItem.CoverReference: URL]) -> Self {
        .init(id: id, title: title, items: items.map { $0.fillingArtwork(covers) }, layout: layout,
            avatars: avatars, channel: channel, filters: filters, selectedFilter: selectedFilter, error: error,
            tabs: tabs.map { .init(id: $0.id, title: $0.title, items: $0.items.map { $0.fillingArtwork(covers) },
                                   loadItems: $0.loadItems) }, selectedTab: selectedTab)
    }
}

private extension MusicPublicBrowseItem {
    func fillingArtwork(_ covers: [CoverReference: URL]) -> Self {
        guard artwork == nil, let ref = coverRef, let cover = covers[ref] else { return self }
        let next: MusicPublicBrowseContent
        if case .collection(let value) = content, value.platform == ref.platform,
           value.kind == ref.kind, value.nativeID == ref.id, value.artwork == nil {
            next = .collection(.init(platform: value.platform, nativeID: value.nativeID, kind: value.kind,
                title: value.title, creator: value.creator, artwork: cover, count: value.count, playCount: value.playCount))
        } else { next = content }
        return .init(id: id, title: title, subtitle: subtitle, artwork: cover, content: next,
            ranking: ranking, previewSongs: previewSongs,
            coverLabel: coverLabel, requiresAccount: requiresAccount, kind: kind, label: label,
            presentation: presentation, coverRef: coverRef, contextText: contextText, playCount: playCount)
    }
}
