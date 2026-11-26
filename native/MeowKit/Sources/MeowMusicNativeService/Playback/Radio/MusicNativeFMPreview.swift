// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicPlatform

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
actor MusicNativeFMRead {
    var preview: Task<MusicNativeFMResponse?, Never>?
    let load: @Sendable () async throws -> MusicNativeFMResponse
    var closed = false
    var loading: Task<MusicNativeFMResponse, Error>?
    var closing: Task<Void, Never>?
    init(preview: Task<MusicNativeFMResponse?, Never>?, load: @escaping @Sendable () async throws -> MusicNativeFMResponse) {
        self.preview = preview; self.load = load
    }
    func page(_ cursor: String?) async throws -> MusicNativeTrackPage {
        try musicRequire(cursor == nil, .invalidRequest)
        try musicRequire(!closed, .closed)
        try musicRequire(loading == nil, .busy)
        let preview = self.preview, load = self.load
        self.preview = nil
        let task = Task {
            if let preview {
                let result = await withTaskCancellationHandler { await preview.value } onCancel: { preview.cancel() }
                try Task.checkCancellation()
                guard let result else { throw MusicServiceError.upstreamFailed }
                return result
            }
            return try await load()
        }
        loading = task
        defer { loading = nil }
        let value = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
        try Task.checkCancellation(); try musicRequire(!closed, .closed)
        return .init(items: value.tracks, total: value.tracks.count, cursor: nil)
    }
    func close() async {
        if closing == nil {
            closed = true
            let preview = self.preview, loading = self.loading
            self.preview = nil
            preview?.cancel(); loading?.cancel()
            closing = Task { _ = await preview?.value; _ = await loading?.result }
        }
        await closing?.value
    }
}

extension NativeMusicService {
    public func fmPreview(_ action: MusicHomeAction) async throws -> MusicFMPreview? {
        try check(); try requirePlatform("wy")
        guard let key = action.fmConfiguration else { throw MusicServiceError.invalidRequest }
        let own = epoch, binding = try await currentHomeBinding()
        try check(own)
        for (candidate, value) in fmPreviews where value.binding != binding || !value.authorization.isActive {
            discardFMPreview(candidate)
        }
        if fmPreviews[key] == nil {
            guard !fmQueues.values.contains(where: { $0.0 == key && $0.1.isActive }) else { return nil }
            if fmPreviewOrder.count >= 6, let oldest = fmPreviewOrder.first { discardFMPreview(oldest) }
            let id = UUID(), authorization = MusicQueueAuthorization(parent: binding.authorization)
            let task = Task { [weak self] () -> MusicNativeFMResponse? in
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
            fmPreviews[key] = .init(id: id, binding: binding, authorization: authorization,
                task: task, observer: observer)
            fmPreviewOrder.append(key)
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
        catch { value = nil }
        if fmPreviews[key]?.id == id {
            fmPreviews[key]?.completed = true; fmPreviews[key]?.value = value
        }
        return value
    }
    func discardFMPreview(_ key: MusicHomeAction, id: UUID? = nil) {
        guard let entry = fmPreviews[key], id == nil || entry.id == id else { return }
        fmPreviews.removeValue(forKey: key); fmPreviewOrder.removeAll { $0 == key }
        entry.observer?.cancel()
        entry.authorization.revoke(); entry.task.cancel()
        retainSimilarDrain(Task { _ = await entry.task.value })
    }
    func clearFMPreviews() {
        for key in Array(fmPreviews.keys) { discardFMPreview(key) }
    }
    private func loadFM(_ action: MusicHomeAction, preview: Bool, binding: MusicNativeHomeBinding,
                        epoch own: UUID) async throws -> MusicNativeFMResponse {
        try await checkHomeBinding(binding, epoch: own)
        let owner = accounts, port = publicPlatforms
        let value = try await work(label: "musicFM", draining: true) {
            if let owner { return try await owner.nativeFM(action, preview: preview, binding: binding) }
            return try await port.homeAction(action, preview: preview)
        }
        try await checkHomeBinding(binding, epoch: own)
        return value
    }
    func openFMQueue(_ action: MusicHomeAction, context: MusicRemoteQueueContext) async throws -> MusicRemoteQueueSource {
        guard let key = action.fmConfiguration else { throw MusicServiceError.invalidRequest }
        let own = epoch, binding = try await currentHomeBinding()
        try await checkHomeBinding(binding, epoch: own)
        let preferenceScope = try await fmPreferenceScope(binding)
        try await checkHomeBinding(binding, epoch: own)
        for (id, value) in fmQueues where !value.1.isActive { releaseFMQueue(id) }
        let id = UUID(), authorization = MusicQueueAuthorization(parent: binding.authorization)
        var preview: Task<MusicNativeFMResponse?, Never>?
        if let entry = fmPreviews[key] {
            if case .fm = action, entry.binding == binding, entry.authorization.isActive,
               !entry.completed || entry.value?.tracks.isEmpty == false {
                preview = entry.task
                fmPreviews.removeValue(forKey: key); fmPreviewOrder.removeAll { $0 == key }
                entry.observer?.cancel(); entry.authorization.revoke()
            } else { discardFMPreview(key) }
        }
        let reader = MusicNativeFMRead(preview: preview) { [weak self] in
            guard let self else { throw MusicServiceError.closed }
            return try await self.loadFM(action, preview: false, binding: binding, epoch: own)
        }
        fmQueues[id] = (key, authorization, reader)
        let source = MusicRemoteQueueSource(context: context, authorization: authorization,
            loadPage: { try await reader.page($0) },
            validate: { [weak self] in
                guard let self else { throw MusicServiceError.closed }
                try await self.checkHomeBinding(binding, epoch: own)
            }, close: { [weak self] in
                await reader.close(); await self?.releaseFMQueue(id)
            }, homeAction: action, fmPreferenceScope: preferenceScope)
        _ = await bindQueueSource(source)
        do {
            try check(own); try authorization.check()
            return source
        } catch { await source.close(); throw error }
    }
    private func releaseFMQueue(_ id: UUID) {
        if let entry = fmQueues.removeValue(forKey: id) {
            entry.1.revoke()
            retainSimilarDrain(Task { await entry.2.close() })
        }
    }
}
