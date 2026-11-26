import Foundation
import MeowDebug
import MeowMusicPlatform

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

extension NativeMusicService {
    public func openAccountQueue(platform: MusicAccountPlatform, kind: MusicAccountQueueKind,
                                 title: String) async throws -> MusicRemoteQueueSource {
        try check(); try requirePlatform(platform.rawValue)
        if platform == .netease, kind == .recommendation(kind: "fm") {
            let action = try await rememberedFM()
            return try await openHomeActionQueue(action, title: title)
        }
        guard let accounts else { throw MusicServiceError.unavailable }
        let context: MusicRemoteQueueContext
        switch kind {
        case .collection(let id):
            context = .init(kind: id == "favorites" ? .favorites : .playlist, id: id, title: title, platform: platform.rawValue)
        case .recommendation(let kind):
            context = .init(kind: kind == "fm" ? .fm : .recommendation, id: kind, title: title, platform: platform.rawValue)
        }
        let opening = MusicAccountQueueOpening(), own = epoch
        do {
            let lease = try await work {
                let value = try await accounts.beginAccountQueue(platform: platform)
                guard opening.receive(value) else {
                    value.revoke(); await accounts.endAccountQueue(value); throw MusicServiceError.cancelled
                }
                if Task.isCancelled { throw MusicServiceError.cancelled }
                return value
            }
            try check(); try lease.authorization.check()
            try musicRequire(lease.platform == platform, .targetChanged)
            let source = MusicRemoteQueueSource(context: context, authorization: lease.authorization, loadPage: { cursor in
                try lease.authorization.check()
                let page = try await accounts.accountQueuePage(lease, kind: kind, cursor: cursor)
                try lease.authorization.check()
                return page
            }, validate: {
                try lease.authorization.check(); try await accounts.checkAccountQueue(lease); try lease.authorization.check()
            }, close: { await accounts.endAccountQueue(lease) },
                homeAction: platform == .netease && kind == .recommendation(kind: "daily") ? .daily(branch: .default) : nil)
            _ = await bindQueueSource(source)
            try check(own); try lease.authorization.check()
            _ = opening.finish()
            return source
        } catch {
            if let lease = opening.finish() { lease.revoke(); await accounts.endAccountQueue(lease) }
            throw error
        }
    }

    public func remoteQueuePage(_ source: MusicRemoteQueueSource, cursor: String?) async throws -> MusicBrowsePage {
        try check(); try requirePlatform(source.context.platform)
        let own = epoch
        _ = await bindQueueSource(source)
        try check(own); try source.authorization.check()
        let page = try await work { try await source.referencePage(cursor: cursor) }
        try source.authorization.check()
        let result = try metadataReferencePage(page)
        try check(own); try source.authorization.check()
        return result
    }

    func remoteContext(_ source: MusicRemoteQueueSource) throws -> MusicPlayContextRecord {
        let c = source.context
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
        return MusicPlayContextRecord(
            sourceTitle: c.title,
            playlist: c.title,
            sourceType: type,
            sourceId: c.id,
            platform: c.platform,
            dailyBranch: dailyBranch?.rawValue,
            remoteSession: source.id.uuidString.lowercased()
        )
    }

    public func replaceRemoteQueue(_ source: MusicRemoteQueueSource, start: MusicRemoteQueueStart,
                                   expectedRevision: Int, intent: MusicQueueAuthorization) async throws -> MusicPlaybackState {
        try intent.check(); try source.authorization.check()
        try check(); try requirePlatform(source.context.platform)
        let own = epoch
        try musicRequire(try read().queue.revision == expectedRevision, .staleRevision)
        let context = try remoteContext(source)
        let hadActiveFM = source.context.kind == .fm && fmPlayback?.source.authorization.isActive == true
        let modeSelection = similarMode.modeSelection
        let action = source.homeAction
        if source.context.kind == .fm {
            try musicRequire(action?.isPersonalFM == true, .invalidRequest)
        }
        let preference: MusicFMSelectionRecord?
        if source.context.kind == .fm, let scope = source.fmPreferenceScope, let config = action?.fmConfiguration {
            preference = try MusicFMSelectionRecord.fromAction(fmScope: scope, action: config)
        } else { preference = nil }
        _ = await bindQueueSource(source)
        try check(own); try intent.check(); try source.authorization.check()
        let references = try await work { try await source.completeReferences(intent: intent) }
        try musicRequire(references.indices.contains(start.index) && !references.isEmpty, .notFound)
        try await work { try await source.validate() }
        try check(own); try intent.check(); try source.authorization.check()
        try musicRequire(try read().queue.revision == expectedRevision, .staleRevision)
        var live: [String: MusicRecommendationMetadata] = [:]
        let result = try source.authorization.perform {
            try intent.perform {
                try transaction([.queue, .playback, .history]) { d in
                    try musicRequire(d.queue.revision == expectedRevision, .staleRevision)
                    if source.context.kind == .fm && !hadActiveFM {
                        try musicRequire(similarMode.modeSelection == modeSelection, .targetChanged)
                    }
                    let old = d.queue
                    let songs = try repositorySongs(references.map(\.songID), d)
                    let rows = try makeEntries(songs, state: &d, context: context)
                    for i in rows.indices {
                        if let value = references[i].recommendation {
                            live[rows[i].id] = try MusicWireCodec.parseRecommendation(from: value)
                        }
                    }
                    try archive(old, into: &d)
                    d.queue.entries = rows
                    d.queue.currentID = rows[start.index].id
                    d.queue.priority = []; d.queue.history = []; d.queue.failed = []
                    d.queue.source = MusicSourceRecord.fromContext(context, title: source.context.title)
                    similarWillChangeQueue(.replace, queue: &d.queue)
                    if source.context.kind == .fm { selectContinuationMode(&d) }
                    if let preference {
                        if let idx = d.playback.fmSelections.firstIndex(where: { $0.fmScope == preference.fmScope }) {
                            d.playback.fmSelections[idx] = preference
                        } else {
                            d.playback.fmSelections.append(preference)
                            d.playback.fmSelections.sort { $0.fmScope < $1.fmScope }
                        }
                    }
                    d.queue.revision = expectedRevision + 1; d.resetPosition()
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
            installFMPlayback(source: source, action: action!, continueEnabled: true, ownsSource: false)
            emitSimilar()
            DebugRuntime.log(.musicPlayback, sub: "recommendations", event: hadActiveFM ? "fm.switched" : "fm.started", level: .info, data: .object([
                "entries": .int(result.total),
                "replacedPreviousFM": .bool(hadActiveFM)
            ]))
        }
        return result
    }
}
