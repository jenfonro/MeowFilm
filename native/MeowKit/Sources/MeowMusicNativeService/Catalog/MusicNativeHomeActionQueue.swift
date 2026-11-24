import Foundation
import MeowMusicFeature
import MeowMusicPlatform

extension NativeMusicService: MusicHomeActionQueueServing {
    public func openHomeActionQueue(_ action: MusicHomeAction, title: String) async throws -> MusicRemoteQueueSource {
        try check(); try requirePlatform("wy")
        let digest = try musicSHA256Hex(encoding: action)
        let kind: String
        switch action {
        case .fm: kind = "fm"
        case .ruleFM: kind = "rule-fm"
        case .similar, .similarArtist: kind = "similar"
        case .heartbeat, .heartbeatSong: kind = "heartbeat"
        case .daily(let branch): kind = "daily-" + branch.rawValue
        case .songCollection: kind = "song-collection"
        }
        let context = MusicRemoteQueueContext(kind: action.isPersonalFM ? .fm : .recommendation,
            id: kind + ":" + digest, title: title, platform: "wy")
        if action.isPersonalFM { return try await openFMQueue(action, context: context) }
        if case .songCollection(let ids) = action {
            let own = epoch, registration = UUID()
            let authorization = MusicQueueAuthorization(), fence = access, port = publicPlatforms
            let reader = MusicNativePublicBrowseQueueRead(total: ids.count) { [weak self] intent in
                guard let self else { throw MusicServiceError.closed }
                try await self.check(own)
                let value = try await port.homeCollectionSongs(ids)
                try await self.check(own)
                return try await self.adoptQueueTracks(value, epoch: own, authorization: authorization, intent: intent)
            }
            fence.register(registration) {
                authorization.revoke()
                Task { await reader.close() }
            }
            publicQueueSources[registration] = (context.id, authorization, reader)
            let source = MusicRemoteQueueSource(context: context, authorization: authorization,
                loadReferences: { try await reader.page($0, intent: $1) },
                validate: { [weak self] in
                    guard let self else { throw MusicServiceError.closed }
                    try await self.check(own)
                }, close: { [weak self] in
                    fence.remove(registration); await reader.close(); await self?.endPublicQueueSource(registration)
                }, homeAction: action)
            _ = await bindQueueSource(source)
            do {
                try check(own); try authorization.check()
                return source
            } catch { await source.close(); throw error }
        }
        if case .similar = action { return try await openPublicSimilarQueue(action, context: context) }
        if case .similarArtist = action { return try await openPublicSimilarQueue(action, context: context) }
        guard let owner = accounts as? any MusicNativeHomeActionQueueProviding else { throw MusicServiceError.authRequired }
        let opening = MusicAccountQueueOpening(), own = epoch
        do {
            let lease = try await work(label: "homeAction") {
                let value = try await owner.beginAccountQueue(platform: .netease)
                guard opening.receive(value) else {
                    value.revoke(); await owner.endAccountQueue(value); throw MusicServiceError.cancelled
                }
                try Task.checkCancellation(); return value
            }
            try check(own); try lease.authorization.check()
            let editor: MusicDailyStyleEditing?
            if action == .daily(branch: .style), let styles = owner as? any MusicNativeDailyStyleProviding {
                let scope = try await work(label: "homeAction") { try await styles.dailyStyleScope(lease) }
                try check(own); try lease.authorization.check()
                editor = .init(scope: scope, categories: { try await styles.dailyStyleCategories(lease) },
                               save: { try await styles.saveDailyStyle(lease, selection: $0) })
            } else { editor = nil }
            let source = MusicRemoteQueueSource(context: context, authorization: lease.authorization,
                loadPage: { cursor in
                    let page = try await owner.homeActionPage(lease, action: action, cursor: cursor)
                    try lease.authorization.check()
                    return page
                }, validate: {
                    try lease.authorization.check(); try await owner.checkAccountQueue(lease)
                }, close: { await owner.endAccountQueue(lease) }, dailyStyle: editor, homeAction: action)
            _ = await bindQueueSource(source)
            try check(own); try lease.authorization.check()
            _ = opening.finish(); return source
        } catch {
            if let lease = opening.finish() { lease.revoke(); await owner.endAccountQueue(lease) }
            throw error
        }
    }
    private func openPublicSimilarQueue(_ action: MusicHomeAction, context: MusicRemoteQueueContext) async throws -> MusicRemoteQueueSource {
        let own = epoch, binding = try await currentHomeBinding(), registration = UUID()
        try await checkHomeBinding(binding, epoch: own)
        let authorization = MusicQueueAuthorization(parent: binding.authorization), fence = access, port = publicPlatforms
        let reader = MusicNativePublicBrowseQueueRead { [weak self] intent in
            guard let self else { throw MusicServiceError.closed }
            try await self.checkHomeBinding(binding, epoch: own)
            let value = try await port.similar(action)
            try await self.checkHomeBinding(binding, epoch: own)
            return try await self.adoptQueueTracks(value.items, epoch: own, authorization: authorization, intent: intent)
        }
        fence.register(registration) { authorization.revoke(); Task { await reader.close() } }
        publicQueueSources[registration] = (context.id, authorization, reader)
        let source = MusicRemoteQueueSource(context: context, authorization: authorization,
            loadReferences: { try await reader.page($0, intent: $1) },
            validate: { [weak self] in
                guard let self else { throw MusicServiceError.closed }
                try await self.checkHomeBinding(binding, epoch: own)
            }, close: { [weak self] in
                fence.remove(registration); await reader.close(); authorization.revoke()
                await self?.endPublicQueueSource(registration)
            }, homeAction: action)
        _ = await bindQueueSource(source)
        do {
            try check(own); try authorization.check()
            return source
        } catch { await source.close(); throw error }
    }
}
