// SPDX-License-Identifier: GPL-3.0-only
#if os(iOS) || os(macOS)
import Foundation
import Combine

/// Product-owned projection of the existing favorites collection, not another store.
/// Writes use the music owner's existing protected, revision-checked port.
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
    private var binding: MusicCollectionPageBinding?
    private var reads: [UUID: Task<Void, Never>] = [:]
    private var readID: UUID?
    private var write: (id: UUID, task: Task<Void, Never>)?
    private var visible = false
    private var needsReload = false

    public init(model: MusicViewModel) { self.model = model }

    func reconcile() {
        guard let model, model.isAvailable, !model.isClosed,
              let collection = model.collections.first(where: { $0.kind == "favorites" }) else {
            if visible || binding != nil { suspend() }
            return
        }
        if !visible || binding?.matches(model) != true {
            setActive(true)
            return
        }
        let next = CollectionIdentity(collection)
        guard collectionIdentity != next else { return }
        if collectionIdentity?.id != next.id { references = []; favoriteCollection = nil; loaded = false }
        collectionIdentity = next
        if write != nil { needsReload = true }
        else { reload() }
    }

    @discardableResult public func setActive(_ active: Bool) -> Task<Void, Never>? {
        guard let model else { suspend(); return nil }
        let active = active && model.isAvailable && !model.isClosed
        if active, visible, binding?.matches(model) == true { return nil }
        if !active, !visible, binding == nil { return nil }
        suspend()
        guard active else { return nil }
        visible = true
        binding = MusicCollectionPageBinding(owner: model)
        collectionIdentity = model.collections.first { $0.kind == "favorites" }.map(CollectionIdentity.init)
        return reload()
    }

    public func suspend() {
        visible = false
        binding?.active = false; binding = nil; readID = nil
        collectionIdentity = nil
        favoriteCollection = nil
        needsReload = false
        for task in reads.values { task.cancel() }
        write?.task.cancel()
        // Keep cancelled work tracked until non-cooperative operations settle.
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
        // Catalog aliases are explicit. Do not conflate recordings by title.
        return lhs.sources.contains { left in
            !left.sourceKey.isEmpty && rhs.sources.contains {
                left.sourceKey.utf8.elementsEqual($0.sourceKey.utf8)
            }
        }
    }

    @discardableResult public func reload() -> Task<Void, Never>? {
        guard let model, visible, let own = binding, own.matches(model), write == nil else { return nil }
        needsReload = false
        let id = UUID()
        if let readID { reads[readID]?.cancel() }
        readID = id; loading = true
        let task = Task { [weak self, weak model] in
            guard let self else { return }
            defer {
                self.reads[id] = nil
                if self.readID == id { self.readID = nil; self.loading = false }
                if self.visible,
                   self.needsReload || (self.binding !== own && !self.loaded && !self.loading) {
                    self.reload()
                }
            }
            guard let model else { return }
            do {
                let snapshot = try await self.read(own, model: model)
                try own.check(model)
                guard self.visible, self.binding === own, self.readID == id else { return }
                if !self.accept(snapshot, model: model) { self.needsReload = true }
            } catch {
                guard own.matches(model), self.binding === own, self.readID == id, !Task.isCancelled else { return }
                self.error = MusicServiceError.sanitized(error)
            }
        }
        reads[id] = task
        return task
    }

    @discardableResult public func toggle(_ track: MusicRemoteTrack) -> Task<Void, Never>? {
        guard let model, visible, loaded, !loading, write == nil, let collection = favoriteCollection,
              let own = binding, own.matches(model) else { return nil }
        let liked = !isFavorite(track), id = UUID()
        let ref = MusicReference(kind: .library, id: track.songId)
        let ticket = MusicCollectionEditTicket(binding: own, collection: collection)
        pending = (ref, liked); busy = true; error = nil
        readID = nil; loading = false
        for task in reads.values { task.cancel() }
        let task = Task { [weak self, weak model] in
            guard let self else { return }
            var needsReconciliation = false
            defer {
                if self.write?.id == id {
                    self.write = nil; self.busy = false
                    if self.binding === own { self.pending = nil }
                    // Reconcile successes and ambiguous failures by reading.
                    // Never automatically repeat a possibly committed write.
                    if self.visible, needsReconciliation || self.needsReload || self.binding !== own { self.reload() }
                }
            }
            guard let model else { return }
            do {
                let snapshot = try await self.read(own, model: model, collection: ticket.collection)
                try own.check(model)
                guard self.binding === own, self.visible else { return }
                let exists = snapshot.refs.contains(ref)
                if exists != liked {
                    needsReconciliation = true
                    try await model.changeCollectionPageMembership(ticket,
                        adding: liked ? [track.ref] : [], removing: liked ? [] : [ref])
                }
                try own.check(model)
                guard self.binding === own, self.visible else { return }
                let saved = try await self.read(own, model: model)
                try own.check(model)
                guard self.binding === own, self.visible else { return }
                needsReconciliation = !self.accept(saved, model: model)
            } catch {
                guard own.matches(model), self.binding === own, self.visible, !Task.isCancelled else { return }
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

    private func read(_ own: MusicCollectionPageBinding, model: MusicViewModel,
                      collection requested: MusicCollection? = nil) async throws ->
        (collection: MusicCollection, refs: Set<MusicReference>) {
        if let requested {
            let refs = try await model.readCollectionReferences(requested, binding: own)
            return (requested, Set(refs))
        }
        let id = model.collections.first { $0.kind == "favorites" }?.id ?? "favorites"
        if let current = model.collections.first(where: { $0.id == id && $0.kind == "favorites" }) {
            do {
                let refs = try await model.readCollectionReferences(current, binding: own)
                return (current, Set(refs))
            } catch {
                try own.check(model)
                guard [.notFound, .staleRevision].contains(MusicServiceError.sanitized(error)) else { throw error }
            }
        }
        // A lost acknowledgement can leave the catalogue behind the saved refs.
        try own.check(model)
        let epoch = try model.enter(), catalogueID = model.collectionCatalogueID
        let values = try await model.requireService().collections()
        try model.check(epoch); try own.check(model)
        guard values.count <= 2000, Set(values.map { Data($0.id.utf8) }).count == values.count else {
            throw MusicServiceError.invalidResponse
        }
        guard let collection = values.first(where: { $0.id == id && $0.kind == "favorites" }) else {
            if model.collectionCatalogueID == catalogueID { model.publishCollections(values, origin: own.id) }
            throw MusicServiceError.notFound
        }
        let refs = try await model.readCollectionReferences(collection, binding: own)
        if model.collectionCatalogueID == catalogueID { model.publishCollections(values, origin: own.id) }
        return (collection, Set(refs))
    }

    deinit {
        for task in reads.values { task.cancel() }
        write?.task.cancel()
    }
}
#endif
