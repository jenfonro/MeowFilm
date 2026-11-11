import Foundation
import MeowMusicFeature

/// A timed-out/cancelled `work` may discard its return value before the account
/// owner cooperates. Retain the provisional lease until this caller actually
/// accepts it, so both early and late cancellation release the exact binding.
final class MusicAccountQueueOpening: @unchecked Sendable {
    private let lock = NSLock()
    private var lease: MusicAccountQueueLease?
    private var finished = false
    func receive(_ value: MusicAccountQueueLease) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard !finished else { return false }
        lease = value; return true
    }
    func finish() -> MusicAccountQueueLease? {
        lock.lock(); defer { lock.unlock() }
        finished = true
        let value = lease; lease = nil; return value
    }
}

extension NativeMusicService: MusicRemoteQueueServing {
    public func openAccountQueue(platform: MusicAccountPlatform, kind: MusicAccountQueueKind,
                                 title: String) async throws -> MusicRemoteQueueSource {
        try check(); try requirePlatform(platform.rawValue)
        if platform == .netease, kind == .recommendation(kind: "fm") {
            let action = try await rememberedFM()
            return try await openHomeActionQueue(action, title: title)
        }
        guard let owner = accounts as? any MusicNativeAccountQueueProviding else { throw MusicServiceError.unavailable }
        _ = try musicText(title, maximum: 600)
        let context: MusicRemoteQueueContext
        switch kind {
        case .collection(let id):
            _ = try musicText(id, maximum: 64)
            context = .init(kind: id == "favorites" ? .favorites : .playlist, id: id, title: title, platform: platform.rawValue)
        case .recommendation(let kind):
            let allowed = platform == .qq ? ["guess", "radar"] : ["daily", "fm"]
            try musicRequire(allowed.contains(kind))
            context = .init(kind: kind == "fm" ? .fm : .recommendation, id: kind, title: title, platform: platform.rawValue)
        }
        let opening = MusicAccountQueueOpening()
        do {
            let lease = try await work {
                let value = try await owner.beginAccountQueue(platform: platform)
                guard opening.receive(value) else {
                    value.revoke(); await owner.endAccountQueue(value); throw MusicServiceError.cancelled
                }
                if Task.isCancelled { throw MusicServiceError.cancelled }
                return value
            }
            try check(); try lease.authorization.check()
            try musicRequire(lease.platform == platform, .targetChanged)
            let source = MusicRemoteQueueSource(context: context, authorization: lease.authorization, loadPage: { cursor in
                try lease.authorization.check()
                let page = try await owner.accountQueuePage(lease, kind: kind, cursor: cursor)
                try lease.authorization.check()
                guard page.items.allSatisfy({ $0.platform == platform.rawValue }) else { throw MusicServiceError.invalidResponse }
                return page
            }, validate: {
                try lease.authorization.check(); try await owner.checkAccountQueue(lease); try lease.authorization.check()
            }, close: { await owner.endAccountQueue(lease) },
                homeAction: platform == .netease && kind == .recommendation(kind: "daily") ? .daily(branch: .default) : nil)
            _ = opening.finish()
            return source
        } catch {
            if let lease = opening.finish() { lease.revoke(); await owner.endAccountQueue(lease) }
            throw error
        }
    }
    public func remoteQueuePage(_ source: MusicRemoteQueueSource, cursor: String?) async throws -> MusicBrowsePage {
        try check(); try requirePlatform(source.context.platform)
        let own = epoch
        let page = try await work { try await source.page(cursor: cursor) }
        try source.authorization.check()
        let songs = try page.items.map(MusicModel.normalize)
        try await retainSongMetadata(songs)
        try check(own); try source.authorization.check()
        // Display handles remain short-lived; full queue collection below uses
        // the source snapshot, not the 600-handle cache or just visible rows.
        // Keep its source keys: MusicRemoteQueueStart pins that raw identity.
        return .init(items: try songs.map { try remember($0) },
                     cursor: page.cursor, dailyStyle: page.dailyStyle)
    }
    func remoteContext(_ source: MusicRemoteQueueSource) throws -> MJ {
        let c = source.context
        _ = try musicText(c.title, maximum: 600); _ = try musicText(c.id, maximum: 256)
        _ = try musicText(c.platform, maximum: 32)
        // Account queues use "daily"; home actions use daily-<branch>:<SHA256>.
        // Only the exact NetEase recommendation descriptors carry this branch.
        let dailyBranch: MusicHomeAction.DailyBranch?
        if c.kind == .recommendation && c.platform == "wy" {
            dailyBranch = c.id == "daily" ? .default :
                [MusicHomeAction.DailyBranch.default, .style].first { branch in
                    let prefix = "daily-" + branch.rawValue + ":"
                    return c.id.hasPrefix(prefix) && musicHex(String(c.id.dropFirst(prefix.count)))
                }
        } else { dailyBranch = nil }
        let type = c.kind == .recommendation ? (c.id == "daily" || dailyBranch != nil ? "daily" :
            c.id.hasPrefix("similar:") || c.id.hasPrefix("heartbeat:") ? "similar" : "collection") :
            c.kind == .favorites ? "playlist" : c.kind.rawValue
        var context = MJ.obj([("sourceTitle", .string(c.title)), ("playlist", .string(c.title)),
            ("sourceType", .string(type)), ("sourceId", .string(c.id)),
            ("platform", .string(c.platform)), ("remoteSession", .string(source.id.uuidString.lowercased()))])
        if let dailyBranch { context["dailyBranch"] = .string(dailyBranch.rawValue) }
        return context
    }
    public func replaceRemoteQueue(_ source: MusicRemoteQueueSource, start: MusicRemoteQueueStart,
                                   expectedRevision: Int, intent: MusicQueueAuthorization) async throws -> MusicPlaybackState {
        try intent.check(); try source.authorization.check()
        try check(); try requirePlatform(source.context.platform)
        let own = epoch
        try musicRequire(try read().queue["revision"].int == expectedRevision, .staleRevision)
        let context = try remoteContext(source)
        let oldFM = fmPlayback
        let modeSelection = similarMode.modeSelection
        let changingFM = source.context.kind == .fm && oldFM?.source.authorization.isActive == true
        let action = source.homeAction
        if source.context.kind == .fm { try musicRequire(action?.isPersonalFM == true, .invalidRequest) }
        let preference: (scope: String, value: MJ)?
        if source.context.kind == .fm, accounts is any MusicNativeFMProviding {
            let binding = try await currentHomeBinding()
            let scope = try await fmPreferenceScope(binding)
            try await checkHomeBinding(binding, epoch: own)
            preference = (scope, try MJ.parse(JSONEncoder().encode(action!.fmConfiguration!)))
        } else { preference = nil }
        let raw = try await work { try await source.completeTracks(intent: intent) }
        try musicRequire(raw.allSatisfy { $0.platform == source.context.platform }, .invalidResponse)
        let songs = try raw.map(MusicModel.normalize)
        let recommendations = try raw.map(MusicNativeSimilarData.recommendation)
        try musicRequire(songs.indices.contains(start.index) && !songs.isEmpty, .notFound)
        let selected = songs[start.index]
        let selectedKeys = try MusicModel.sources(selected).map(\.sourceKey)
        try musicRequire(musicSame(selected["songId"].string, start.songID) &&
            selectedKeys.count == start.sourceKeys.count &&
            zip(selectedKeys, start.sourceKeys).allSatisfy { musicSame($0.0, $0.1) }, .targetChanged)
        try await work { try await source.validate() }
        try check(own); try intent.check(); try source.authorization.check()
        try musicRequire(try read().queue["revision"].int == expectedRevision, .staleRevision)
        try await retainSongMetadata(songs)
        try check(own)
        var live: [String: MJ] = [:]
        let result = try source.authorization.perform {
            try intent.perform {
                try similarQueueTransaction(["queue", "playback", "history"]) { d in
                    try musicRequire(d.queue["revision"].int == expectedRevision, .staleRevision)
                    if source.context.kind == .fm && !changingFM {
                        try musicRequire(similarMode.modeSelection == modeSelection, .targetChanged)
                    }
                    let old = d.queue
                    let rows = try makeEntries(songs, queue: &d.queue, context: context)
                    for i in rows.indices {
                        if let value = recommendations[i] { live[rows[i]["id"].string] = value }
                    }
                    if !changingFM { archive(old, into: &d) }
                    d.queue["entries"] = .array((changingFM ? old["entries"].array : []) + rows); d.queue["currentId"] = rows[start.index]["id"]
                    for key in ["priority", "history", "failed"] { d.queue[key] = .array([]) }
                    d.queue["source"] = context; d.queue["source"]["title"] = .string(source.context.title)
                    similarWillChangeQueue(.replace, queue: &d.queue)
                    if source.context.kind == .fm && !changingFM { selectContinuationMode(&d) }
                    if let preference { d.playback["fmSelections"][preference.scope] = preference.value }
                    d.queue["revision"] = .n(expectedRevision + 1); d.resetPosition()
                    return stateDTO(d)
                }
            }
        }
        let seeds: [String]
        switch source.homeAction {
        case .similar(let ids, _), .similarArtist(let ids, _): seeds = ids
        default: seeds = []
        }
        retainQueueFeedbackOrigins(live, authorization: source.authorization, action: source.homeAction, seedIDs: seeds)
        invalidatePlayback(reason: .ui)
        similarDidChangeQueue(.replace)
        if source.context.kind == .fm {
            fmPlayback = .init(source: source, action: action!, continueEnabled: changingFM ? oldFM!.continueEnabled : true)
            emitSimilar()
        }
        return result
    }
}
