// SPDX-License-Identifier: GPL-3.0-only
#if os(iOS) || os(macOS)
import Foundation
import Combine

/// Search presentation admission. All durable changes still use the existing
/// collection/queue owner, including its revision and protection checks.
@MainActor public final class MusicSearchActions: ObservableObject {
    @Published private(set) var favorites: [MusicRemoteTrack] = []
    @Published private(set) var favoritesLoaded = false
    @Published private(set) var favoritesLoading = false
    @Published private(set) var busy = false
    @Published private(set) var error: MusicServiceError?
    @Published private(set) var actionError: String?
    private let model: MusicViewModel
    private var binding: MusicCollectionPageBinding?
    private var readTask: Task<Void, Never>?
    private var actionTask: Task<Void, Never>?
    private var readID = UUID(), actionID = UUID()
    private var active = false
    private var needsRead = false

    public init(model: MusicViewModel) { self.model = model }
    public func setActive(_ active: Bool) {
        let valid = active && model.isAvailable && !model.isClosed
        if valid, self.active, binding?.matches(model) == true { return }
        suspend()
        guard valid else { return }
        self.active = true
        binding = MusicCollectionPageBinding(owner: model)
        refreshFavorites()
    }
    public func suspend() {
        active = false; binding?.active = false; binding = nil
        readID = UUID(); actionID = UUID()
        readTask?.cancel(); actionTask?.cancel()
        needsRead = false
        favorites = []; favoritesLoaded = false; favoritesLoading = false; busy = actionTask != nil
        error = nil; actionError = nil
    }
    public func isFavorite(_ song: MusicSearchSong) -> Bool {
        MusicSearchFusion.isFavorite(song, members: favorites)
    }
    public func refreshFavorites() {
        guard active, let own = binding, own.matches(model), !busy else { return }
        guard readTask == nil else { needsRead = true; return }
        needsRead = false
        let token = UUID(); readID = token; readTask?.cancel()
        favoritesLoading = true; error = nil
        readTask = Task { [weak self] in
            guard let self else { return }
            defer {
                self.readTask = nil
                if self.readID == token { self.favoritesLoading = false }
                if self.active, !self.busy, self.needsRead || self.binding !== own { self.refreshFavorites() }
            }
            do {
                let snapshot = try await self.favoriteSnapshot(own)
                try own.check(self.model)
                guard self.readID == token, self.active else { return }
                self.favorites = snapshot.tracks; self.favoritesLoaded = true
            } catch {
                guard own.matches(self.model), self.readID == token, self.active, !Task.isCancelled else { return }
                self.error = MusicServiceError.sanitized(error)
            }
        }
    }
    @discardableResult public func toggleFavorite(_ song: MusicSearchSong,
        refresh: (@MainActor () async throws -> MusicRemoteTrack)? = nil) -> Task<Void, Never>? {
        guard active, !busy, favoritesLoaded, let own = binding, own.matches(model) else { return nil }
        // Freeze both the recording and the user's intended state. A reload or
        // re-ranking cannot turn "remove" into "add" after the first suspension.
        let remove = isFavorite(song), token = UUID()
        actionID = token; busy = true; actionError = nil; error = nil
        readID = UUID(); readTask?.cancel(); favoritesLoading = false
        let task = Task { [weak self] in
            guard let self else { return }
            defer {
                self.busy = false; self.actionTask = nil
                if self.active, self.needsRead || self.binding !== own { self.refreshFavorites() }
            }
            do {
                let snapshot = try await self.favoriteSnapshot(own)
                try own.check(self.model)
                guard self.actionID == token, self.active else { return }
                let matching = snapshot.tracks.filter { MusicSearchFusion.matchesFavorite($0, row: song) }
                var tracks = snapshot.tracks
                if remove { tracks.removeAll { matching.contains($0) } }
                else if matching.isEmpty {
                    let track: MusicRemoteTrack
                    if let refresh { track = try await refresh() } else { track = song.track }
                    try own.check(self.model)
                    guard self.actionID == token, self.active else { return }
                    tracks.append(track)
                }
                if tracks != snapshot.tracks {
                    let ticket = MusicCollectionEditTicket(binding: own, collection: snapshot.collection)
                    try await self.model.editCollectionPage(ticket, name: nil, refs: tracks.map(\.ref), order: nil)
                }
                let updated = try await self.favoriteSnapshot(own)
                try own.check(self.model)
                guard self.actionID == token, self.active else { return }
                self.favorites = updated.tracks; self.favoritesLoaded = true
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
                self.actionError = self.model.error
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
            actionError = session.songs.count > MusicSearchBatchRequest.maximumCount
                ? "最多播放2000首" : MusicServiceError.sanitized(error).localizedDescription
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
    func finishPending() async {
        while readTask != nil || actionTask != nil {
            let read = readTask, action = actionTask
            await read?.value; await action?.value
        }
    }
    private func favoriteSnapshot(_ binding: MusicCollectionPageBinding) async throws -> MusicCollectionPageSnapshot {
        // Resolve the real catalogue entry, never infer membership from a title
        // or create a second private favorites list.
        let id = model.collections.first(where: { $0.kind == "favorites" })?.id ?? "favorites"
        guard let snapshot = try await model.readCollectionPage(id: id, binding: binding),
              snapshot.collection.kind == "favorites" else { throw MusicServiceError.notFound }
        return snapshot
    }
    deinit { readTask?.cancel(); actionTask?.cancel() }
}
#endif
