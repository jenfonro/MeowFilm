// SPDX-License-Identifier: GPL-3.0-only
#if os(iOS) || os(macOS)
import Foundation
import Combine

@MainActor public final class MusicSearchActions: ObservableObject {
    @Published private(set) var favorites: Set<MusicReference> = []
    @Published private(set) var favoritesLoaded = false
    @Published private(set) var favoritesLoading = false
    @Published private(set) var busy = false
    @Published private(set) var error: MusicServiceError?
    @Published private(set) var actionError: String?
    private let model: MusicViewModel
    private var favoriteCollection: MusicCollection?
    private var epoch = UUID()
    private var readTask: Task<Void, Never>?
    private var reads: [UUID: Task<Void, Never>] = [:]
    private var actionTask: Task<Void, Never>?
    private var accountObservation: AnyCancellable?
    private var access: UUID?
    private var readID = UUID(), actionID = UUID()
    private var active = false
    private var needsRead = false

    public init(model: MusicViewModel) {
        self.model = model
        accountObservation = model.accountChanges.sink { [weak self] _ in
            guard let self else { return }
            self.setActive(self.active)
        }
    }
    public func setActive(_ active: Bool) {
        let valid = active && !model.isClosed
        if valid, self.active, access == model.searchAccessGeneration { return }
        suspend()
        guard valid else { return }
        self.active = true
        access = model.searchAccessGeneration
        epoch = UUID()
        refreshFavorites()
    }
    public func clearErrors() { actionError = nil; error = nil }
    public func suspend() {
        active = false; access = nil; epoch = UUID()
        readID = UUID(); actionID = UUID()
        readTask?.cancel(); actionTask?.cancel(); readTask = nil
        needsRead = false
        favorites = []; favoriteCollection = nil
        favoritesLoaded = false; favoritesLoading = false; busy = actionTask != nil
        error = nil; actionError = nil
    }
    public func isFavorite(_ song: MusicSearchSong) -> Bool {
        song.variants.contains { favorites.contains(.init(kind: .library, id: $0.songId)) }
    }
    public func refreshFavorites() {
        guard active, !model.isClosed, !busy else { return }
        guard readTask == nil else { needsRead = true; return }
        needsRead = false
        let token = UUID(), own = epoch; readID = token; readTask?.cancel()
        favoritesLoading = true; error = nil
        let task = Task { [weak self] in
            guard let self else { return }
            defer {
                self.reads[token] = nil
                if self.readID == token {
                    self.readTask = nil; self.favoritesLoading = false
                    if self.active, !self.busy, self.needsRead { self.refreshFavorites() }
                }
            }
            do {
                let snapshot = try await self.favoriteSnapshot(own)
                guard !Task.isCancelled, self.readID == token, self.active, !self.model.isClosed, self.epoch == own else { return }
                self.favoriteCollection = snapshot.collection
                self.favorites = snapshot.refs; self.favoritesLoaded = true
            } catch {
                guard !Task.isCancelled, self.readID == token, self.active, !self.model.isClosed, self.epoch == own else { return }
                self.error = MusicServiceError.sanitized(error)
            }
        }
        readTask = task; reads[token] = task
    }
    @discardableResult public func toggleFavorite(_ song: MusicSearchSong,
        refresh: (@MainActor () async throws -> MusicRemoteTrack)? = nil) -> Task<Void, Never>? {
        guard active, !model.isClosed, !busy, favoritesLoaded, let collection = favoriteCollection else { return nil }
        let remove = isFavorite(song), token = UUID(), own = epoch
        actionID = token; busy = true; actionError = nil; error = nil
        readID = UUID(); readTask?.cancel(); readTask = nil; favoritesLoading = false
        let task = Task { [weak self] in
            guard let self else { return }
            defer {
                self.busy = false; self.actionTask = nil
                if self.active, self.needsRead || self.epoch != own { self.refreshFavorites() }
            }
            do {
                let snapshot = try await self.favoriteSnapshot(own, collection: collection)
                guard !Task.isCancelled, self.actionID == token, self.active, !self.model.isClosed, self.epoch == own else { return }
                let matching = snapshot.refs.intersection(song.variants.map { MusicReference(kind: .library, id: $0.songId) })
                if remove {
                    if !matching.isEmpty {
                        try await self.model.changeCollectionPageMembership(collection, adding: [], removing: Array(matching))
                    }
                } else if matching.isEmpty {
                    let track: MusicRemoteTrack
                    if let refresh { track = try await refresh() } else { track = song.track }
                    guard !Task.isCancelled, self.actionID == token, self.active, !self.model.isClosed, self.epoch == own else { return }
                    try await self.model.changeCollectionPageMembership(collection, adding: [track.ref], removing: [])
                }
                let updated = try await self.favoriteSnapshot(own)
                guard !Task.isCancelled, self.actionID == token, self.active, !self.model.isClosed, self.epoch == own else { return }
                self.favoriteCollection = updated.collection
                self.favorites = updated.refs; self.favoritesLoaded = true
            } catch {
                guard !Task.isCancelled, self.actionID == token, self.active, !self.model.isClosed, self.epoch == own else { return }
                self.error = MusicServiceError.sanitized(error)
            }
        }
        actionTask = task
        return task
    }
    @discardableResult func perform(_ body: @escaping @MainActor () async throws -> Void) -> Task<Void, Never>? {
        guard active, !model.isClosed, !busy else { return nil }
        let token = UUID(), own = epoch; actionID = token; busy = true; actionError = nil
        model.error = nil
        let task = Task { [weak self] in
            guard let self else { return }
            defer {
                self.busy = false; self.actionTask = nil
                if self.active, self.needsRead || self.epoch != own { self.refreshFavorites() }
            }
            do {
                try Task.checkCancellation()
                guard self.active, !self.model.isClosed, self.epoch == own else { return }
                try await body()
                guard !Task.isCancelled, self.active, !self.model.isClosed, self.epoch == own, self.actionID == token else { return }
                self.actionError = self.model.notices.error
            } catch {
                guard !Task.isCancelled, self.active, !self.model.isClosed, self.epoch == own, self.actionID == token else { return }
                self.actionError = MusicServiceError.sanitized(error).localizedDescription
            }
        }
        actionTask = task
        return task
    }
    @discardableResult func perform(song: MusicSearchSong, session: MusicSearchResultsModel,
        operation: @escaping @MainActor (MusicRemoteTrack) async -> Void) -> Task<Void, Never>? {
        let request = session.requestID
        return perform {
            guard session.requestID == request else { throw MusicServiceError.staleResult }
            let fresh = try await session.refreshTrack(song)
            guard session.requestID == request else { throw MusicServiceError.staleResult }
            await operation(fresh)
        }
    }
    @discardableResult public func playAll(session: MusicSearchResultsModel) -> Task<Void, Never>? {
        guard active, !model.isClosed, !busy else { return nil }
        let request: MusicSearchBatchRequest
        do { request = try session.makeBatchRequest() }
        catch { actionError = MusicServiceError.sanitized(error).localizedDescription; return nil }
        let page = session.requestID
        session.holdPagination()
        return perform { [model] in
            guard session.requestID == page else { throw MusicServiceError.staleResult }
            await model.playSearchBatch(request)
        }
    }
    @discardableResult public func playSingle(_ song: MusicSearchSong,
                                      session: MusicSearchResultsModel) -> Task<Void, Never>? {
        guard active, !model.isClosed, !busy else { return nil }
        let request: MusicSearchBatchRequest
        do { request = try session.makeBatchRequest(for: song) }
        catch { actionError = MusicServiceError.sanitized(error).localizedDescription; return nil }
        let page = session.requestID
        session.holdPagination()
        return perform { [model] in
            guard session.requestID == page else { throw MusicServiceError.staleResult }
            await model.playSearchBatch(request)
        }
    }
    private func favoriteSnapshot(_ own: UUID,
                                  collection requested: MusicCollection? = nil) async throws ->
        (collection: MusicCollection, refs: Set<MusicReference>) {
        if let requested {
            let refs = try await model.readCollectionReferences(requested)
            return (requested, Set(refs))
        }
        let id = model.collections.first(where: { $0.kind == "favorites" })?.id ?? "favorites"
        if let current = model.collections.first(where: { $0.id == id && $0.kind == "favorites" }) {
            do {
                let refs = try await model.readCollectionReferences(current)
                return (current, Set(refs))
            } catch {
                guard [.notFound, .staleRevision].contains(MusicServiceError.sanitized(error)) else { throw error }
            }
        }
        let mEpoch = try model.enter(), catalogueID = model.collectionCatalogueID
        let values = try await model.requireService().collections()
        try model.check(mEpoch)
        guard let collection = values.first(where: { $0.id == id && $0.kind == "favorites" }) else {
            if model.collectionCatalogueID == catalogueID { model.publishCollections(values, origin: own) }
            throw MusicServiceError.notFound
        }
        let refs = try await model.readCollectionReferences(collection)
        if model.collectionCatalogueID == catalogueID { model.publishCollections(values, origin: own) }
        return (collection, Set(refs))
    }
    deinit { accountObservation?.cancel(); for read in reads.values { read.cancel() }; actionTask?.cancel() }
}
#endif
