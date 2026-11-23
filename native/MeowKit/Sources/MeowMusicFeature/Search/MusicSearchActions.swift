// SPDX-License-Identifier: GPL-3.0-only
#if os(iOS) || os(macOS)
import Foundation
import Combine

/// Search presentation admission. All durable changes still use the existing
/// collection/queue owner, including its revision and protection checks.
@MainActor public final class MusicSearchActions: ObservableObject {
    @Published private(set) var favorites: Set<MusicReference> = []
    @Published private(set) var favoritesLoaded = false
    @Published private(set) var favoritesLoading = false
    @Published private(set) var busy = false
    @Published private(set) var error: MusicServiceError?
    @Published private(set) var actionError: String?
    private let model: MusicViewModel
    private var favoriteCollection: MusicCollection?
    private var binding: MusicCollectionPageBinding?
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
        let valid = active && model.isAvailable && !model.isClosed
        if valid, self.active, access == model.searchAccessGeneration, binding?.matches(model) == true { return }
        suspend()
        guard valid else { return }
        self.active = true
        access = model.searchAccessGeneration
        binding = MusicCollectionPageBinding(owner: model)
        refreshFavorites()
    }
    public func clearErrors() {
        actionError = nil
        error = nil
    }
    public func suspend() {
        active = false; access = nil; binding?.active = false; binding = nil
        readID = UUID(); actionID = UUID()
        readTask?.cancel(); actionTask?.cancel()
        readTask = nil
        needsRead = false
        favorites = []; favoriteCollection = nil
        favoritesLoaded = false; favoritesLoading = false; busy = actionTask != nil
        error = nil; actionError = nil
    }
    public func isFavorite(_ song: MusicSearchSong) -> Bool {
        song.variants.contains { favorites.contains(.init(kind: .library, id: $0.songId)) }
    }
    public func refreshFavorites() {
        guard active, let own = binding, own.matches(model), !busy else { return }
        guard readTask == nil else { needsRead = true; return }
        needsRead = false
        let token = UUID(); readID = token; readTask?.cancel()
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
                try own.check(self.model)
                guard self.readID == token, self.active else { return }
                self.favoriteCollection = snapshot.collection
                self.favorites = snapshot.refs; self.favoritesLoaded = true
            } catch {
                guard own.matches(self.model), self.readID == token, self.active, !Task.isCancelled else { return }
                self.error = MusicServiceError.sanitized(error)
            }
        }
        readTask = task; reads[token] = task
    }
    @discardableResult public func toggleFavorite(_ song: MusicSearchSong,
        refresh: (@MainActor () async throws -> MusicRemoteTrack)? = nil) -> Task<Void, Never>? {
        guard active, !busy, favoritesLoaded, let collection = favoriteCollection,
              let own = binding, own.matches(model) else { return nil }
        // Freeze both the recording and the user's intended state. A reload or
        // re-ranking cannot turn "remove" into "add" after the first suspension.
        let remove = isFavorite(song), token = UUID()
        let ticket = MusicCollectionEditTicket(binding: own, collection: collection)
        actionID = token; busy = true; actionError = nil; error = nil
        readID = UUID(); readTask?.cancel(); readTask = nil; favoritesLoading = false
        let task = Task { [weak self] in
            guard let self else { return }
            defer {
                self.busy = false; self.actionTask = nil
                if self.active, self.needsRead || self.binding !== own { self.refreshFavorites() }
            }
            do {
                let snapshot = try await self.favoriteSnapshot(own, collection: ticket.collection)
                try own.check(self.model)
                guard self.actionID == token, self.active else { return }
                let matching = snapshot.refs.intersection(song.variants.map { MusicReference(kind: .library, id: $0.songId) })
                if remove {
                    if !matching.isEmpty {
                        try await self.model.changeCollectionPageMembership(ticket, adding: [], removing: Array(matching))
                    }
                } else if matching.isEmpty {
                    let track: MusicRemoteTrack
                    if let refresh { track = try await refresh() } else { track = song.track }
                    try own.check(self.model)
                    guard self.actionID == token, self.active else { return }
                    try await self.model.changeCollectionPageMembership(ticket, adding: [track.ref], removing: [])
                }
                let updated = try await self.favoriteSnapshot(own)
                try own.check(self.model)
                guard self.actionID == token, self.active else { return }
                self.favoriteCollection = updated.collection
                self.favorites = updated.refs; self.favoritesLoaded = true
            } catch {
                guard own.matches(self.model), self.actionID == token, self.active, !Task.isCancelled else { return }
                self.error = MusicServiceError.sanitized(error)
            }
        }
        actionTask = task
        return task
    }
    @discardableResult func perform(_ body: @escaping @MainActor () async throws -> Void) -> Task<Void, Never>? {
        guard active, !busy, let own = binding, own.matches(model) else { return nil }
        let token = UUID(); actionID = token; busy = true; actionError = nil
        model.error = nil
        let task = Task { [weak self] in
            guard let self else { return }
            defer {
                self.busy = false; self.actionTask = nil
                if self.active, self.needsRead || self.binding !== own { self.refreshFavorites() }
            }
            do {
                try own.check(self.model)
                try await body()
                try own.check(self.model)
                guard self.active, self.actionID == token else { return }
                self.actionError = self.model.notices.error
            } catch {
                guard !Task.isCancelled, self.active, own.matches(self.model), self.actionID == token else { return }
                self.actionError = MusicServiceError.sanitized(error).localizedDescription
            }
        }
        actionTask = task
        return task
    }
    @discardableResult func perform(song: MusicSearchSong, session: MusicSearchResultsModel,
        operation: @escaping @MainActor (MusicRemoteTrack) async -> Void) -> Task<Void, Never>? {
        guard let own = binding else { return nil }
        let request = session.requestID
        return perform { [model] in
            guard session.requestID == request else { throw MusicServiceError.staleResult }
            let fresh = try await session.refreshTrack(song)
            try own.check(model)
            guard session.requestID == request else { throw MusicServiceError.staleResult }
            await operation(fresh)
        }
    }
    @discardableResult public func playAll(session: MusicSearchResultsModel) -> Task<Void, Never>? {
        guard active, !busy, let own = binding, own.matches(model) else { return nil }
        let request: MusicSearchBatchRequest
        do { request = try session.makeBatchRequest() }
        catch {
            actionError = MusicServiceError.sanitized(error).localizedDescription
            return nil
        }
        let page = session.requestID
        session.holdPagination()
        return perform { [model] in
            try own.check(model)
            guard session.requestID == page else { throw MusicServiceError.staleResult }
            await model.playSearchBatch(request)
        }
    }
    @discardableResult public func playSingle(_ song: MusicSearchSong,
                                      session: MusicSearchResultsModel) -> Task<Void, Never>? {
        guard active, !busy, let own = binding, own.matches(model) else { return nil }
        let request: MusicSearchBatchRequest
        do { request = try session.makeBatchRequest(for: song) }
        catch { actionError = MusicServiceError.sanitized(error).localizedDescription; return nil }
        let page = session.requestID
        session.holdPagination()
        return perform { [model] in
            try own.check(model)
            guard session.requestID == page else { throw MusicServiceError.staleResult }
            await model.playSearchBatch(request)
        }
    }
    private func favoriteSnapshot(_ binding: MusicCollectionPageBinding,
                                  collection requested: MusicCollection? = nil) async throws ->
        (collection: MusicCollection, refs: Set<MusicReference>) {
        // Resolve the real catalogue entry, never infer membership from a title
        // or create a second private favorites list.
        if let requested {
            let refs = try await model.readCollectionReferences(requested, binding: binding)
            return (requested, Set(refs))
        }
        let id = model.collections.first(where: { $0.kind == "favorites" })?.id ?? "favorites"
        if let current = model.collections.first(where: { $0.id == id && $0.kind == "favorites" }) {
            do {
                let refs = try await model.readCollectionReferences(current, binding: binding)
                return (current, Set(refs))
            } catch {
                try binding.check(model)
                guard [.notFound, .staleRevision].contains(MusicServiceError.sanitized(error)) else { throw error }
            }
        }
        // Read-only catalogue reconciliation, never upgrade a captured write ticket.
        try binding.check(model)
        let epoch = try model.enter(), catalogueID = model.collectionCatalogueID
        let values = try await model.requireService().collections()
        try model.check(epoch); try binding.check(model)
        guard Set(values.map { Data($0.id.utf8) }).count == values.count else {
            throw MusicServiceError.invalidResponse
        }
        guard let collection = values.first(where: { $0.id == id && $0.kind == "favorites" }) else {
            if model.collectionCatalogueID == catalogueID { model.publishCollections(values, origin: binding.id) }
            throw MusicServiceError.notFound
        }
        let refs = try await model.readCollectionReferences(collection, binding: binding)
        if model.collectionCatalogueID == catalogueID { model.publishCollections(values, origin: binding.id) }
        return (collection, Set(refs))
    }
    deinit { accountObservation?.cancel(); for read in reads.values { read.cancel() }; actionTask?.cancel() }
}
#endif
