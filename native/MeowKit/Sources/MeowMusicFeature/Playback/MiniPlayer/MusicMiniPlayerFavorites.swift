// SPDX-License-Identifier: GPL-3.0-only
#if os(iOS) || os(macOS)
import Foundation
import Combine

/// A view of the existing favorites collection, not another favorites store.
/// Writes use the music owner's existing protected, revision-checked port.
@MainActor public final class MusicMiniPlayerFavorites: ObservableObject {
    @Published public private(set) var tracks: [MusicRemoteTrack] = []
    @Published public private(set) var loaded = false
    @Published public private(set) var loading = false
    @Published public private(set) var busy = false
    @Published public private(set) var error: MusicServiceError?
    @Published private var pending: (track: MusicRemoteTrack, liked: Bool)?
    private let model: MusicViewModel
    private var binding: MusicCollectionPageBinding?
    private var reads: [UUID: Task<Void, Never>] = [:]
    private var readID: UUID?
    private var write: (id: UUID, task: Task<Void, Never>)?
    private var visible = false
    private var needsReload = false

    public init(model: MusicViewModel) { self.model = model }

    @discardableResult public func setActive(_ active: Bool) -> Task<Void, Never>? {
        let active = active && model.isAvailable && !model.isClosed
        if active, visible, binding?.matches(model) == true { return nil }
        if !active, !visible, binding == nil { return nil }
        suspend()
        guard active else { return nil }
        visible = true
        binding = MusicCollectionPageBinding(owner: model)
        return reload()
    }

    public func suspend() {
        visible = false
        binding?.active = false; binding = nil; readID = nil
        needsReload = false
        for task in reads.values { task.cancel() }
        write?.task.cancel()
        // Keep physical task slots until non-cooperative operations settle.
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
        guard visible, let own = binding, own.matches(model), write == nil else { return nil }
        guard reads.count < 2 else { needsReload = true; return nil }
        needsReload = false
        let id = UUID()
        if let readID { reads[readID]?.cancel() }
        readID = id; loading = true
        let task = Task { [weak self] in
            guard let self else { return }
            defer {
                self.reads[id] = nil
                if self.readID == id { self.readID = nil; self.loading = false }
                if self.visible, self.reads.count < 2,
                   self.needsReload || (self.binding !== own && !self.loaded && !self.loading) {
                    self.reload()
                }
            }
            do {
                let snapshot = try await self.read(own)
                try own.check(self.model)
                guard self.visible, self.binding === own, self.readID == id else { return }
                self.tracks = snapshot.tracks; self.loaded = true
            } catch {
                guard own.matches(self.model), self.binding === own, self.readID == id, !Task.isCancelled else { return }
                self.error = MusicServiceError.sanitized(error)
            }
        }
        reads[id] = task
        return task
    }

    @discardableResult public func toggle(_ track: MusicRemoteTrack) -> Task<Void, Never>? {
        guard visible, loaded, !loading, write == nil,
              let own = binding, own.matches(model) else { return nil }
        let liked = !isFavorite(track), id = UUID()
        pending = (track, liked); busy = true; error = nil
        readID = nil; loading = false
        for task in reads.values { task.cancel() }
        let task = Task { [weak self] in
            guard let self else { return }
            var needsReconciliation = false
            defer {
                if self.write?.id == id {
                    self.write = nil; self.busy = false
                    if self.binding === own { self.pending = nil }
                    // Reconcile successes and ambiguous failures by reading.
                    // Never automatically repeat a possibly committed write.
                    if self.visible, needsReconciliation || self.binding !== own { self.reload() }
                }
            }
            do {
                let snapshot = try await self.read(own)
                try own.check(self.model)
                guard self.binding === own, self.visible else { return }
                let exists = snapshot.tracks.contains { Self.sameSong($0, track) }
                if exists != liked {
                    let refs = liked
                        ? [track.ref] + snapshot.tracks.map(\.ref)
                        : snapshot.tracks.filter { !Self.sameSong($0, track) }.map(\.ref)
                    let ticket = MusicCollectionEditTicket(binding: own, collection: snapshot.collection)
                    needsReconciliation = true
                    try await self.model.editCollectionPage(ticket, name: nil, refs: refs, order: nil)
                }
                try own.check(self.model)
                guard self.binding === own, self.visible else { return }
                let saved = try await self.read(own)
                try own.check(self.model)
                guard self.binding === own, self.visible else { return }
                self.tracks = saved.tracks; self.loaded = true
                needsReconciliation = false
            } catch {
                guard own.matches(self.model), self.binding === own, self.visible, !Task.isCancelled else { return }
                self.error = MusicServiceError.sanitized(error)
            }
        }
        write = (id, task)
        return task
    }

    public func clearError() { error = nil }

    private func read(_ own: MusicCollectionPageBinding) async throws -> MusicCollectionPageSnapshot {
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
