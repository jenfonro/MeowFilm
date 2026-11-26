// SPDX-License-Identifier: GPL-3.0-only
#if os(iOS) || os(macOS)
import Foundation
import Combine

@MainActor public final class MusicMiniPlayerFavorites: ObservableObject {
    @Published public private(set) var references: Set<MusicReference> = []
    @Published public private(set) var loaded = false
    @Published public private(set) var loading = false
    @Published public private(set) var busy = false
    @Published public private(set) var error: MusicServiceError?
    @Published private var pending: (ref: MusicReference, liked: Bool)?
    private weak var model: MusicViewModel?
    private struct CollectionIdentity: Equatable {
        let id: String
        let revision: Int
        init(_ collection: MusicCollection) { id = collection.id; revision = collection.revision }
    }
    private var collectionIdentity: CollectionIdentity?
    private var favoriteCollection: MusicCollection?
    private var epoch = UUID()
    private var reads: [UUID: Task<Void, Never>] = [:]
    private var readID: UUID?
    private var write: (id: UUID, task: Task<Void, Never>)?
    private var visible = false
    private var needsReload = false

    public init(model: MusicViewModel) { self.model = model }

    public var favoriteChanges: AnyPublisher<Void, Never> {
        Publishers.MergeMany([
            $references.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            $pending.dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            $loaded.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            $loading.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
            $busy.removeDuplicates().dropFirst().map { _ -> Void in }.eraseToAnyPublisher(),
        ]).eraseToAnyPublisher()
    }

    func reconcile() {
        guard let model, !model.isClosed,
              let collection = model.collections.first(where: { $0.kind == "favorites" }) else {
            if visible { suspend() }
            return
        }
        if !visible { setActive(true); return }
        let next = CollectionIdentity(collection)
        guard collectionIdentity != next else { return }
        if collectionIdentity?.id != next.id { references = []; favoriteCollection = nil; loaded = false }
        collectionIdentity = next
        if write != nil { needsReload = true } else { reload() }
    }

    @discardableResult public func setActive(_ active: Bool) -> Task<Void, Never>? {
        guard let model else { suspend(); return nil }
        let active = active && !model.isClosed
        if active == visible { return nil }
        suspend()
        guard active else { return nil }
        visible = true; epoch = UUID()
        collectionIdentity = model.collections.first { $0.kind == "favorites" }.map(CollectionIdentity.init)
        return reload()
    }

    public func suspend() {
        visible = false; epoch = UUID(); readID = nil
        collectionIdentity = nil; favoriteCollection = nil; needsReload = false
        for task in reads.values { task.cancel() }
        write?.task.cancel()
        references = []; loaded = false; loading = false; pending = nil; error = nil
        busy = write != nil
    }

    public func isFavorite(_ track: MusicRemoteTrack) -> Bool {
        let ref = MusicReference(kind: .library, id: track.songId)
        if let pending, pending.ref == ref { return pending.liked }
        return references.contains(ref)
    }

    static func sameSong(_ lhs: MusicRemoteTrack, _ rhs: MusicRemoteTrack) -> Bool {
        if !lhs.songId.isEmpty, lhs.songId.utf8.elementsEqual(rhs.songId.utf8) { return true }
        if lhs.ref == rhs.ref { return true }
        return lhs.sources.contains { left in
            !left.sourceKey.isEmpty && rhs.sources.contains { left.sourceKey.utf8.elementsEqual($0.sourceKey.utf8) }
        }
    }

    @discardableResult public func reload() -> Task<Void, Never>? {
        guard let model, visible, !model.isClosed, write == nil else { return nil }
        needsReload = false
        let id = UUID(), own = epoch
        if let readID { reads[readID]?.cancel() }
        readID = id; loading = true
        let task = Task { [weak self, weak model] in
            guard let self else { return }
            defer {
                self.reads[id] = nil
                if self.readID == id { self.readID = nil; self.loading = false }
                if self.visible, self.needsReload || (self.epoch != own && !self.loaded && !self.loading) {
                    self.reload()
                }
            }
            guard let model else { return }
            do {
                let snapshot = try await self.read(own, model: model)
                guard !Task.isCancelled, self.visible, !model.isClosed, self.epoch == own, self.readID == id else { return }
                if !self.accept(snapshot, model: model) { self.needsReload = true }
            } catch {
                guard !Task.isCancelled, self.visible, !model.isClosed, self.epoch == own, self.readID == id else { return }
                self.error = MusicServiceError.sanitized(error)
            }
        }
        reads[id] = task
        return task
    }

    @discardableResult public func toggle(_ track: MusicRemoteTrack) -> Task<Void, Never>? {
        guard let model, visible, !model.isClosed, loaded, !loading, write == nil,
              let collection = favoriteCollection else { return nil }
        let liked = !isFavorite(track), id = UUID(), own = epoch
        let ref = MusicReference(kind: .library, id: track.songId)
        pending = (ref, liked); busy = true; error = nil
        readID = nil; loading = false
        for task in reads.values { task.cancel() }
        let task = Task { [weak self, weak model] in
            guard let self else { return }
            var needsReconciliation = false
            defer {
                if self.write?.id == id {
                    self.write = nil; self.busy = false
                    if self.epoch == own { self.pending = nil }
                    if self.visible, needsReconciliation || self.needsReload || self.epoch != own { self.reload() }
                }
            }
            guard let model else { return }
            do {
                let snapshot = try await self.read(own, model: model, collection: collection)
                guard !Task.isCancelled, self.epoch == own, self.visible, !model.isClosed else { return }
                if snapshot.refs.contains(ref) != liked {
                    needsReconciliation = true
                    try await model.changeCollectionPageMembership(collection,
                        adding: liked ? [track.ref] : [], removing: liked ? [] : [ref])
                }
                guard !Task.isCancelled, self.epoch == own, self.visible, !model.isClosed else { return }
                let saved = try await self.read(own, model: model)
                guard !Task.isCancelled, self.epoch == own, self.visible, !model.isClosed else { return }
                needsReconciliation = !self.accept(saved, model: model)
            } catch {
                guard !Task.isCancelled, self.epoch == own, self.visible, !model.isClosed else { return }
                self.error = MusicServiceError.sanitized(error)
            }
        }
        write = (id, task)
        return task
    }

    public func clearError() { error = nil }

    func flush() async {
        while !reads.isEmpty || write != nil {
            let pending = Array(reads.values) + [write?.task].compactMap { $0 }
            for task in pending { await task.value }
        }
    }

    private func accept(_ snapshot: (collection: MusicCollection, refs: Set<MusicReference>), model: MusicViewModel) -> Bool {
        guard let collection = model.collections.first(where: { $0.kind == "favorites" }),
              CollectionIdentity(collection) == CollectionIdentity(snapshot.collection) else { return false }
        collectionIdentity = CollectionIdentity(snapshot.collection)
        favoriteCollection = snapshot.collection
        references = snapshot.refs; loaded = true; needsReload = false
        return true
    }

    private func read(_ own: UUID, model: MusicViewModel,
                      collection requested: MusicCollection? = nil) async throws ->
        (collection: MusicCollection, refs: Set<MusicReference>) {
        if let requested {
            let refs = try await model.readCollectionReferences(requested)
            return (requested, Set(refs))
        }
        let id = model.collections.first { $0.kind == "favorites" }?.id ?? "favorites"
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

    deinit {
        for task in reads.values { task.cancel() }
        write?.task.cancel()
    }
}
#endif
