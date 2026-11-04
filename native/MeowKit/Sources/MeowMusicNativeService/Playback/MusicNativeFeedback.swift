import Foundation
import MeowMusicFeature

struct MusicNativeFeedbackAdmission {
    let attemptID: String
    let settings: MusicPlaybackFeedbackSettings
    let targets: [MusicPlaybackFeedbackTarget]
}

extension NativeMusicService: MusicPlaybackFeedbackConsuming, MusicPlaybackFeedbackSettingsServing {
    public func playbackFeedbackSettings() async throws -> MusicPlaybackFeedbackSettings {
        try MusicNativeFeedbackSettings.decode(read().playback)
    }
    public func setPlaybackFeedback(platform: MusicAccountPlatform, enabled: Bool,
                                    expectedRevision: Int) async throws -> MusicPlaybackFeedbackSettings {
        try check(); try requirePlatform(platform.rawValue)
        // Revoke before CAS, even when the write conflicts or its outcome is
        // unknown. A failed UI save must not keep an in-flight report authorized.
        playbackFeedback.revoke(platform: platform)
        return try transaction(["playback"]) { draft in
            let old = try MusicNativeFeedbackSettings.decode(draft.playback)
            try musicRequire(old.revision == expectedRevision, .staleRevision)
            let setting = old.platforms[platform] ?? .init()
            try musicRequire(setting.generation < 9_007_199_254_740_991 &&
                old.revision < 9_007_199_254_740_991, .limit)
            var stored = draft.playback["feedback"]
            if stored == .null { stored = .obj([("schema", .n(1)), ("platforms", .obj())]) }
            stored["platforms"][platform.rawValue] = .obj([("enabled", .bool(enabled)), ("generation", .n(setting.generation + 1))])
            draft.playback["feedback"] = stored
            draft.playback["revision"] = .n(old.revision + 1)
            return try MusicNativeFeedbackSettings.decode(draft.playback)
        }
    }
    public func consumePlaybackFeedback(_ sample: MusicPlaybackFeedbackSample) async {
        do {
            try check()
            guard MusicNativeFeedbackController.valid(sample), let attempt, attempt.ready,
                  attempt.id == sample.attemptID, attempt.occurrence == sample.occurrenceID,
                  sample.resourceID == "music:" + attempt.id,
                  let port = accounts as? any MusicNativeFeedbackProviding else { return }
            try attempt.authorization?.check()
            if attempt.ended && ![.ended, .failed, .stopped].contains(sample.kind) { return }
            let admission: MusicNativeFeedbackAdmission
            if let cached = playbackFeedbackAdmission, cached.attemptID == attempt.id { admission = cached }
            else {
                let draft = try read()
                guard draft.queue["currentId"].string == sample.occurrenceID,
                      let entry = draft.queue["entries"].array.first(where: { $0["id"].string == sample.occurrenceID }) else { return }
                _ = try target(entry["song"], attempt.sourceKey)
                var context = entry["context"]
                if let automatic = sample.automatic { context["automatic"] = .bool(automatic) }
                admission = .init(attemptID: attempt.id, settings: try MusicNativeFeedbackSettings.decode(draft.playback),
                    targets: try similarFeedbackTargets(entry: entry, context: context)
                        .filter { platformEnabled($0.platform.rawValue) })
                playbackFeedbackAdmission = admission
            }
            let own = epoch
            // Account authority outlives ordinary selection retirement so an
            // already accepted ending report can finish, but logout/replacement
            // cannot send a delayed report under the new identity.
            let authorization = audioCacheAttempt.flatMap {
                $0.attemptID == attempt.id && $0.audio == nil ? $0.scopeAuthorization : nil
            } ?? attempt.authorization
            playbackFeedback.consume(sample, settings: admission.settings, targets: admission.targets, accounts: port) { [weak self] platform, generation in
                try authorization?.check()
                guard let self else { throw MusicServiceError.closed }
                try await self.checkFeedbackAuthorization(epoch: own, platform: platform, generation: generation)
                try authorization?.check()
            }
        } catch { /* Storage/access/mapping failure closes feedback, not audio. */
            playbackFeedbackAdmission = nil
            playbackFeedback.invalidate(attemptID: sample.attemptID)
        }
    }
    func checkFeedbackAuthorization(epoch own: UUID, platform: MusicAccountPlatform, generation: Int) throws {
        try check(own)
        try requirePlatform(platform.rawValue)
        let setting = try MusicNativeFeedbackSettings.decode(read().playback).platforms[platform]
        try musicRequire(setting?.enabled == true && setting?.generation == generation, .staleAccess)
    }
    public func cancelPlaybackFeedback(attemptID: String?) async {
        // Selection invalidation can arrive before resolve/cancel on this actor.
        // Settle its last observed position once, never UI elapsed time. A
        // revoked owner only cancels and cannot manufacture a terminal report.
        guard (try? check()) != nil else {
            playbackFeedback.invalidate(attemptID: attemptID)
            return
        }
        playbackFeedback.finishCurrent(attemptID: attemptID, reason: .interrupt)
        playbackFeedback.invalidate(attemptID: attemptID, preservingEnds: true)
    }
}

enum MusicNativeFeedbackSettings {
    static func decode(_ playback: MJ) throws -> MusicPlaybackFeedbackSettings {
        var result: [MusicAccountPlatform: MusicPlaybackFeedbackSetting] = [:]
        let data = playback["feedback"]
        if data != .null {
            try data.requireFields(["schema", "platforms"], required: ["schema", "platforms"])
            try musicRequire(data["schema"] == .n(1) && data["platforms"].isObject &&
                data["platforms"].fields.allSatisfy { ["wy", "tx", "kg"].contains($0.0) }, .corruptDocument)
        }
        for platform in [MusicAccountPlatform.netease, .qq, .kugou] {
            let row = data["platforms"][platform.rawValue]
            if row == .null { result[platform] = .init(); continue }
            try row.requireFields(["enabled", "generation"], required: ["enabled", "generation"])
            try musicRequire(row["enabled"].isBool && row["generation"].isInteger, .corruptDocument)
            result[platform] = .init(enabled: row["enabled"].bool, generation: row["generation"].int)
        }
        return .init(revision: playback["revision"].int, platforms: result)
    }
}

enum MusicNativeFeedbackMapping {
    static func positive(_ value: String) -> Bool {
        value.range(of: "^[1-9][0-9]{0,15}$", options: .regularExpression) != nil &&
            (UInt64(value) ?? UInt64.max) <= 9_007_199_254_740_991
    }
    static func recording(_ raw: MJ, platform: MusicAccountPlatform) -> MusicPlaybackFeedbackTarget? {
        let data = MusicModel.sourceData(raw)
        guard data["source"].string == platform.rawValue else { return nil }
        let id = data["songmid"].id
        if platform == .netease { return positive(id) ? .init(platform: platform, nativeID: id) : nil }
        if platform == .qq {
            return id.range(of: "^[a-zA-Z0-9]{1,64}$", options: .regularExpression) != nil ? .init(platform: platform, nativeID: id) : nil
        }
        let hash = data["hash"].string.uppercased()
        guard hash.range(of: "^[A-F0-9]{32}$", options: .regularExpression) != nil,
              hash != String(repeating: "0", count: 32) else { return nil }
        let ids = ["albumAudioId", "album_audio_id", "mixsongid"].map { data[$0].id }.filter { !$0.isEmpty && $0 != "0" }
        let songmid = data["songmid"].id.uppercased()
        guard ids.allSatisfy(positive), Set(ids).count <= 1,
              !(songmid.range(of: "^[A-F0-9]{32}$", options: .regularExpression) != nil && songmid != hash) else { return nil }
        return .init(platform: platform, nativeID: hash, mixsongID: ids.first)
    }
    static func targets(song: MJ, context: MJ) throws -> [MusicPlaybackFeedbackTarget] {
        // The authoritative source array, including an empty one, wins. Never
        // invent another recording from delivery provider or candidate order.
        _ = try MusicModel.sources(song)
        var result: [MusicPlaybackFeedbackTarget] = []
        for platform in [MusicAccountPlatform.netease, .qq, .kugou] {
            var values: [MusicPlaybackFeedbackTarget] = []
            for row in song["sources"].array {
                guard MusicModel.compatible(song, row),
                      let value = recording(row, platform: platform), !values.contains(value) else { continue }
                values.append(value)
            }
            if platform == .kugou {
                let known = Set(values.filter { $0.mixsongID != nil }.map(\.nativeID))
                values.removeAll { $0.mixsongID == nil && known.contains($0.nativeID) }
            }
            let primary = recording(song, platform: platform)
            let preferred = context["recommendation"]["songId"].id
            let chosen = values.first { platform == .netease && $0.nativeID == preferred } ??
                values.first { $0 == primary } ??
                (values.count == 1 ? values.first : nil) ??
                (platform == .kugou && !values.isEmpty && values.allSatisfy { $0.mixsongID != nil } &&
                    Set(values.compactMap(\.mixsongID)).count == 1 ? values.sorted { $0.nativeID < $1.nativeID }.first : nil)
            guard let chosen else { continue }
            var source = "", sourceID = "", algorithm = ""
            if platform == .netease {
                let native = song["sources"].array.contains {
                    MusicModel.sourceData($0)["source"].string == "wy"
                }
                let type = context["sourceType"].string, id = context["sourceId"].id
                // No live FM/recommendation-session proof is supplied by this
                // owner. Preserve verified source attribution, not stale FM tags.
                if native && ["playlist", "album"].contains(type) && positive(id) {
                    source = type == "playlist" ? "list" : "album"; sourceID = id
                } else if native && type == "daily" && ["", "default"].contains(context["dailyBranch"].string) {
                    source = "dailySongRecommend"
                } else if ["search", "search-song"].contains(type) {
                    let query = context["searchKeyword"].string
                    if query.utf16.count <= 512 && !query.unicodeScalars.contains(where: { $0.value < 32 || (127...159).contains($0.value) }) {
                        source = "search"; sourceID = query
                    }
                } else if type == "song" { source = "song" }
                if preferred == chosen.nativeID, context["recommendation"]["alg"].string.utf16.count <= 2000 {
                    algorithm = context["recommendation"]["alg"].string
                }
            }
            result.append(.init(platform: platform, nativeID: chosen.nativeID, mixsongID: chosen.mixsongID,
                source: source, sourceID: sourceID, algorithm: algorithm, automatic: context["automatic"].bool))
        }
        return result
    }
}
