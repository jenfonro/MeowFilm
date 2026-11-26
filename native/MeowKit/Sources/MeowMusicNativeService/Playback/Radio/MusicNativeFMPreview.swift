// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicPlatform

/// The source caches its first page; only its existing nextFMReferences call
/// asks this reader again, which marks subsequent reads as live continuations.
actor MusicNativeFMRead {
    let load: @Sendable (Bool) async throws -> MusicNativeFMResponse
    var initialConsumed = false
    var closed = false
    var loading: Task<MusicNativeFMResponse, Error>?
    var closing: Task<Void, Never>?
    init(load: @escaping @Sendable (Bool) async throws -> MusicNativeFMResponse) {
        self.load = load
    }
    func page(_ cursor: String?) async throws -> MusicNativeTrackPage {
        try musicRequire(cursor == nil, .invalidRequest)
        try musicRequire(!closed, .closed)
        try musicRequire(loading == nil, .busy)
        let isContinuation = initialConsumed
        initialConsumed = true
        let load = self.load
        let task = Task { try await load(isContinuation) }
        loading = task
        defer { loading = nil }
        let value = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
        try Task.checkCancellation(); try musicRequire(!closed, .closed)
        return .init(items: value.tracks, total: value.tracks.count, cursor: nil)
    }
    func close() async {
        if closing == nil {
            closed = true
            let loading = self.loading
            loading?.cancel()
            closing = Task { _ = await loading?.result }
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
        let value: MusicNativeFMResponse?
        do { value = try await loadFM(key, preview: true, binding: binding, epoch: own) }
        catch is CancellationError { throw CancellationError() }
        catch { value = nil } // Optional preview never becomes a page/playback error.
        try Task.checkCancellation()
        try await checkHomeBinding(binding, epoch: own)
        let authorization = MusicQueueAuthorization(parent: binding.authorization)
        guard authorization.isActive else { return nil }
        return .init(artwork: value?.artwork, authorization: authorization)
    }
    private func fmKnownSongIDs() throws -> [String] {
        let d = try read()
        var seen = Set<String>(), result: [String] = []
        for entry in d.queue.entries {
            if let id = MusicNativeSimilarData.nativeID(try entrySong(entry, d)), seen.insert(id).inserted { result.append(id) }
        }
        return result
    }
    private func loadFM(_ action: MusicHomeAction, preview: Bool, unplaySongIds: [String] = [],
                        binding: MusicNativeHomeBinding, epoch own: UUID) async throws -> MusicNativeFMResponse {
        try await checkHomeBinding(binding, epoch: own)
        let owner = accounts, port = publicPlatforms
        let value = try await work(label: "musicFM", draining: true) {
            if let owner { return try await owner.nativeFM(action, preview: preview, unplaySongIds: unplaySongIds, binding: binding) }
            return try await port.homeAction(action, preview: preview, unplaySongIds: unplaySongIds)
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
        let reader = MusicNativeFMRead { [weak self] isContinuation in
            guard let self else { throw MusicServiceError.closed }
            let unplay = isContinuation ? try await self.fmKnownSongIDs() : []
            return try await self.loadFM(action, preview: false, unplaySongIds: unplay, binding: binding, epoch: own)
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
