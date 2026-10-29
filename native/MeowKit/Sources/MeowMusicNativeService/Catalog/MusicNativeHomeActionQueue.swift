import Foundation
import CryptoKit
import MeowMusicFeature

extension NativeMusicService: MusicHomeActionQueueServing {
    public func openHomeActionQueue(_ action: MusicHomeAction, title: String) async throws -> MusicRemoteQueueSource {
        try check(); try requirePlatform("wy"); try action.validate()
        _ = try musicText(title, maximum: 600)
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        let digest = SHA256.hash(data: try encoder.encode(action)).map { String(format: "%02x", $0) }.joined()
        let kind: String
        switch action {
        case .fm: kind = "fm"
        case .ruleFM: kind = "rule-fm"
        case .similar: kind = "similar"
        case .heartbeat: kind = "heartbeat"
        case .daily(let branch): kind = "daily-" + branch.rawValue
        case .songCollection: kind = "song-collection"
        }
        let context = MusicRemoteQueueContext(kind: action.isPersonalFM ? .fm : .recommendation,
            id: kind + ":" + digest, title: title, platform: "wy")
        if action.isPersonalFM { return try await openFMQueue(action, context: context) }
        if case .songCollection(let ids) = action {
            let own = epoch, until = now().addingTimeInterval(600), registration = UUID()
            let authorization = MusicQueueAuthorization(), fence = access, port = publicPlatforms
            let reader = MusicNativePublicBrowseQueueRead { [weak self] in
                guard let self else { throw MusicServiceError.closed }
                try await self.checkPublicQueue(epoch: own, until: until)
                let value = try await port.homeCollectionSongs(ids)
                try await self.checkPublicQueue(epoch: own, until: until)
                return value
            }
            fence.register(registration) {
                authorization.revoke()
                Task { await reader.close() }
            }
            return .init(context: context, authorization: authorization, loadPage: { try await reader.page($0) },
                validate: { [weak self] in
                    guard let self else { throw MusicServiceError.closed }
                    try await self.checkPublicQueue(epoch: own, until: until)
                }, close: { fence.remove(registration); await reader.close() })
        }
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
            try musicRequire(lease.platform == .netease, .targetChanged)
            let editor: MusicDailyStyleEditing?
            if action == .daily(branch: .style), let styles = owner as? any MusicNativeDailyStyleProviding {
                editor = .init(categories: { try await styles.dailyStyleCategories(lease) },
                               save: { try await styles.saveDailyStyle(lease, selection: $0) })
            } else { editor = nil }
            let source = MusicRemoteQueueSource(context: context, authorization: lease.authorization,
                loadPage: { cursor in
                    let page = try await owner.homeActionPage(lease, action: action, cursor: cursor)
                    try lease.authorization.check()
                    guard page.items.allSatisfy({ $0.platform == "wy" }) else { throw MusicServiceError.invalidResponse }
                    return page
                }, validate: {
                    try lease.authorization.check(); try await owner.checkAccountQueue(lease)
                }, close: { await owner.endAccountQueue(lease) }, dailyStyle: editor)
            _ = opening.finish(); return source
        } catch {
            if let lease = opening.finish() { lease.revoke(); await owner.endAccountQueue(lease) }
            throw error
        }
    }
}
extension MusicNativePlatforms {
    func homeCollectionSongs(_ declared: [String]) async throws -> [MusicNativeTrackData] {
        try MusicHomeAction.songCollection(ids: declared).validate()
        var seen = Set<String>(), rows: [String: MusicNativeTrackData] = [:]
        let ids = declared.filter { seen.insert($0).inserted }
        for offset in stride(from: 0, to: ids.count, by: 100) {
            let batch = Array(ids.dropFirst(offset).prefix(100))
            let c = "[" + batch.map { "{\"id\":" + $0 + "}" }.joined(separator: ",") + "]"
            let root = try await browseWEAPI("/api/v3/song/detail", .obj([("c", .string(c))]))
            try musicRequire(root["songs"].isArray && root["songs"].array.count <= batch.count, .upstreamInvalid)
            for row in root["songs"].array {
                let id = try MusicPlatformProtocol.numericID(row["id"])
                try musicRequire(batch.contains(id) && rows[id] == nil, .upstreamInvalid)
                rows[id] = try browseWYSong(row)
            }
        }
        return ids.compactMap { rows[$0] }
    }
}
