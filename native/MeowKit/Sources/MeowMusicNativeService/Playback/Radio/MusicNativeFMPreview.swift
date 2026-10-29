// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicFeature

struct MusicNativeFMPreviewEntry {
    let id: UUID
    let binding: MusicNativeHomeBinding
    let authorization: MusicQueueAuthorization
    let task: Task<MusicNativeFMResponse?, Never>
    var observer: Task<Void, Never>?
    var completed = false
    var value: MusicNativeFMResponse?
}

/// The source caches its first page; only its existing nextFMBatch call asks
/// this reader again. Consuming a preview can therefore never repeat batch one.
private actor MusicNativeFMRead {
    var preview: Task<MusicNativeFMResponse?, Never>?
    let load: @Sendable () async throws -> MusicNativeFMResponse
    var closed = false
    init(preview: Task<MusicNativeFMResponse?, Never>?, load: @escaping @Sendable () async throws -> MusicNativeFMResponse) {
        self.preview = preview; self.load = load
    }
    func page(_ cursor: String?) async throws -> MusicNativeTrackPage {
        try musicRequire(cursor == nil, .invalidRequest)
        try musicRequire(!closed, .closed)
        let value: MusicNativeFMResponse
        if let task = preview {
            preview = nil
            let result = await withTaskCancellationHandler { await task.value } onCancel: { task.cancel() }
            try Task.checkCancellation()
            guard let result else { throw MusicServiceError.upstreamFailed }
            value = result
        } else { value = try await load() }
        try Task.checkCancellation(); try musicRequire(!closed, .closed)
        return .init(items: value.tracks, total: value.tracks.count, cursor: nil)
    }
    func close() { closed = true; preview?.cancel(); preview = nil }
}

extension NativeMusicService: MusicFMPreviewServing {
    public func fmPreview(_ action: MusicHomeAction) async throws -> MusicFMPreview? {
        try check(); try requirePlatform("wy"); try action.validate()
        guard let key = action.fmConfiguration else { throw MusicServiceError.invalidRequest }
        let own = epoch, binding = try await currentHomeBinding()
        try check(own)
        guard accounts is any MusicNativeFMProviding else { throw MusicServiceError.unsupported }
        // Includes failed and in-flight entries. Account changes cannot leak a
        // previous cover or make a previous first batch playable.
        for (candidate, value) in fmPreviews where value.binding != binding || !value.authorization.isActive {
            discardFMPreview(candidate)
        }
        if fmPreviews[key] == nil {
            guard !fmQueues.values.contains(where: { $0.0 == key && $0.1.isActive }) else { return nil }
            if fmPreviewOrder.count >= 6, let oldest = fmPreviewOrder.first { discardFMPreview(oldest) }
            let id = UUID(), authorization = MusicQueueAuthorization(parent: binding.authorization)
            let previous = fmPreviewTail
            // Optional previews use one lane, leaving an account-operation slot
            // for a user action. Still at most six bounded pending/failed reads.
            let task = Task { [weak self] () -> MusicNativeFMResponse? in
                _ = await previous?.value
                guard let self, !Task.isCancelled, binding.authorization.isActive else { return nil }
                return await self.readFMPreview(key, id: id, binding: binding, epoch: own)
            }
            let events = binding.authorization.revocations()
            let observer = Task { [weak self] in
                for await _ in events {
                    guard !Task.isCancelled else { return }
                    await self?.discardFMPreview(key, id: id); return
                }
            }
            access.register(id) { authorization.revoke(); task.cancel() }
            fmPreviews[key] = .init(id: id, binding: binding, authorization: authorization,
                task: task, observer: observer)
            fmPreviewOrder.append(key); fmPreviewTail = task
        }
        guard let entry = fmPreviews[key] else { return nil }
        let value = await entry.task.value
        try await checkHomeBinding(binding, epoch: own)
        guard fmPreviews[key]?.id == entry.id, entry.authorization.isActive else { return nil }
        return .init(artwork: value?.artwork, authorization: entry.authorization)
    }
    private func readFMPreview(_ key: MusicHomeAction, id: UUID, binding: MusicNativeHomeBinding,
                               epoch own: UUID) async -> MusicNativeFMResponse? {
        let value: MusicNativeFMResponse?
        do { value = try await loadFM(key, preview: true, binding: binding, epoch: own) }
        catch { value = nil } // Optional preview never becomes a page/playback error.
        if fmPreviews[key]?.id == id {
            fmPreviews[key]?.completed = true; fmPreviews[key]?.value = value
        }
        return value
    }
    func discardFMPreview(_ key: MusicHomeAction, id: UUID? = nil) {
        guard let entry = fmPreviews[key], id == nil || entry.id == id else { return }
        fmPreviews.removeValue(forKey: key); fmPreviewOrder.removeAll { $0 == key }
        access.remove(entry.id); entry.observer?.cancel()
        entry.authorization.revoke(); entry.task.cancel()
    }
    func clearFMPreviews() {
        for key in Array(fmPreviews.keys) { discardFMPreview(key) }
        fmPreviewTail?.cancel(); fmPreviewTail = nil
    }
    private func loadFM(_ action: MusicHomeAction, preview: Bool, binding: MusicNativeHomeBinding,
                        epoch own: UUID) async throws -> MusicNativeFMResponse {
        try await checkHomeBinding(binding, epoch: own)
        guard let owner = accounts as? any MusicNativeFMProviding else { throw MusicServiceError.unsupported }
        let value = try await work(label: "musicFM") {
            try await owner.nativeFM(action, preview: preview, binding: binding)
        }
        try await checkHomeBinding(binding, epoch: own)
        try musicRequire(value.tracks.count <= 5 && value.tracks.allSatisfy { $0.platform == "wy" }, .invalidResponse)
        if preview, case .ruleFM = action { try musicRequire(value.tracks.isEmpty, .invalidResponse) }
        _ = try value.tracks.map(MusicModel.normalize)
        return value
    }
    func openFMQueue(_ action: MusicHomeAction, context: MusicRemoteQueueContext) async throws -> MusicRemoteQueueSource {
        guard let key = action.fmConfiguration else { throw MusicServiceError.invalidRequest }
        let own = epoch, binding = try await currentHomeBinding()
        try await checkHomeBinding(binding, epoch: own)
        guard accounts is any MusicNativeFMProviding else { throw MusicServiceError.unsupported }
        for (id, value) in fmQueues where !value.1.isActive { releaseFMQueue(id) }
        try musicRequire(fmQueues.count < 8, .busy)
        let id = UUID(), authorization = MusicQueueAuthorization(parent: binding.authorization)
        var preview: Task<MusicNativeFMResponse?, Never>?
        if let entry = fmPreviews[key] {
            if case .fm = action, entry.binding == binding, entry.authorization.isActive,
               !entry.completed || entry.value?.tracks.isEmpty == false {
                preview = entry.task
                // Transfer the pending first batch, not its display fence.
                fmPreviews.removeValue(forKey: key); fmPreviewOrder.removeAll { $0 == key }
                access.remove(entry.id); entry.observer?.cancel(); entry.authorization.revoke()
            } else { discardFMPreview(key) }
        }
        let transferred = preview
        access.register(id) { authorization.revoke(); transferred?.cancel() }
        fmQueues[id] = (key, authorization)
        let reader = MusicNativeFMRead(preview: preview) { [weak self] in
            guard let self else { throw MusicServiceError.closed }
            return try await self.loadFM(action, preview: false, binding: binding, epoch: own)
        }
        return .init(context: context, authorization: authorization,
            loadPage: { try await reader.page($0) },
            validate: { [weak self] in
                guard let self else { throw MusicServiceError.closed }
                try await self.checkHomeBinding(binding, epoch: own)
            }, close: { [weak self] in
                transferred?.cancel(); await reader.close(); await self?.releaseFMQueue(id)
            })
    }
    private func releaseFMQueue(_ id: UUID) {
        access.remove(id); fmQueues.removeValue(forKey: id)?.1.revoke()
    }
}
