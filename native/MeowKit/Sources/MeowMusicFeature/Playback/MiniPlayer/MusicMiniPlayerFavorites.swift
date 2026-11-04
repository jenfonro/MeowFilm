// SPDX-License-Identifier: GPL-3.0-only
#if os(iOS) || os(macOS)
import Foundation
import Combine

/// Product-owned projection of the existing favorites collection, not another store.
/// Writes use the music owner's existing protected, revision-checked port.
@MainActor public final class MusicMiniPlayerFavorites: ObservableObject {
    @Published public private(set) var tracks: [MusicRemoteTrack] = []
    @Published public private(set) var loaded = false
    @Published public private(set) var loading = false
    @Published public private(set) var busy = false
    @Published public private(set) var error: MusicServiceError?
    @Published private var pending: (track: MusicRemoteTrack, liked: Bool)?
    private weak var model: MusicViewModel?
    private struct CollectionIdentity: Equatable {
        let id: String
        let revision: Int
        init(_ collection: MusicCollection) { id = collection.id; revision = collection.revision }
    }
    private var collectionIdentity: CollectionIdentity?
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
        if collectionIdentity?.id != next.id { tracks = []; loaded = false }
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
        needsReload = false
        for task in reads.values { task.cancel() }
        write?.task.cancel()
        // Keep cancelled work tracked until non-cooperative operations settle.
        tracks = []; loaded = false; loading = false; pending = nil; error = nil
        busy = write != nil
    }

    public func isFavorite(_ track: MusicRemoteTrack) -> Bool {
        if let pending, Self.sameSong(pending.track, track) { return pending.liked }
        return tracks.contains { Self.sameSong($0, track) }
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
        guard let model, visible, loaded, !loading, write == nil,
              let own = binding, own.matches(model) else { return nil }
        let liked = !isFavorite(track), id = UUID()
        pending = (track, liked); busy = true; error = nil
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
                let snapshot = try await self.read(own, model: model)
                try own.check(model)
                guard self.binding === own, self.visible else { return }
                let exists = snapshot.tracks.contains { Self.sameSong($0, track) }
                if exists != liked {
                    let refs = liked
                        ? [track.ref] + snapshot.tracks.map(\.ref)
                        : snapshot.tracks.filter { !Self.sameSong($0, track) }.map(\.ref)
                    let ticket = MusicCollectionEditTicket(binding: own, collection: snapshot.collection)
                    needsReconciliation = true
                    try await model.editCollectionPage(ticket, name: nil, refs: refs, order: nil)
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

    private func accept(_ snapshot: MusicCollectionPageSnapshot, model: MusicViewModel) -> Bool {
        guard let collection = model.collections.first(where: { $0.kind == "favorites" }),
              CollectionIdentity(collection) == CollectionIdentity(snapshot.collection) else { return false }
        collectionIdentity = CollectionIdentity(snapshot.collection)
        tracks = snapshot.tracks; loaded = true; needsReload = false
        return true
    }

    private func read(_ own: MusicCollectionPageBinding, model: MusicViewModel) async throws -> MusicCollectionPageSnapshot {
        let id = model.collections.first { $0.kind == "favorites" }?.id ?? "favorites"
        guard let snapshot = try await model.readCollectionPage(id: id, binding: own),
              snapshot.collection.kind == "favorites" else { throw MusicServiceError.notFound }
        return snapshot
    }

    deinit {
        for task in reads.values { task.cancel() }
        write?.task.cancel()
    }
}
#endif
