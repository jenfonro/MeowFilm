import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicPlaybackFeedbackClosePreparing {
    public nonisolated func stopPlaybackFeedbackDispatchForClose() {
        (accounts as? any MusicNativeFeedbackLifecycleProviding)?.stopPlaybackFeedbackDispatchForClose()
    }
}

struct MusicNativeFeedbackAdmission {
    let attemptID: String, occurrenceID: String
    let song: MJ
    let sourceKey: String
    let authorization: MusicQueueAuthorization?
    var ended = false
    var targets: [MusicPlaybackFeedbackTarget] = []
    var retiringReason: MusicPlaybackFeedbackReport.Reason?
}

struct MusicNativeQueueFeedbackOrigin {
    let value: MJ
    let authorization: MusicQueueAuthorization
    let action: MusicHomeAction?
    let seedIDs: [String]
}

extension NativeMusicService: MusicPlaybackFeedbackConsuming, MusicPlaybackFeedbackSettingsServing {
    func similarFeedbackTargets(entry: MJ, context: MJ, selectedSourceKey: String? = nil) throws -> [MusicPlaybackFeedbackTarget] {
        let selectedNativeID = entry["song"]["sources"].array.first { $0["sourceKey"].string == selectedSourceKey }
            .flatMap { MusicNativeFeedbackMapping.recording($0, platform: .netease)?.nativeID }
        guard let live = liveRecommendation(entryID: entry["id"].string), live.authorization.isActive else {
            return try MusicNativeFeedbackMapping.targets(song: entry["song"], context: context, preferredNativeID: selectedNativeID)
        }
        return try live.authorization.perform {
            let recommendation = live.value
            let nativeID = recommendation["songId"].id
            let targets = try MusicNativeFeedbackMapping.targets(song: entry["song"], context: context,
                preferredNativeID: selectedNativeID ?? nativeID)
            return targets.map { target in
                guard target.platform == .netease, target.nativeID == nativeID,
                      selectedNativeID == nil || selectedNativeID == nativeID else { return target }
                let kind = recommendation["source"].string
                let source = recommendation["feedbackSource"].string
                let sourceID = recommendation["feedbackSourceId"].id
                let algorithm = recommendation["alg"].string
                let proof = MusicPlaybackRecommendationProof(occurrenceID: entry["id"].string, nativeID: nativeID,
                    source: source, sourceID: sourceID, algorithm: algorithm, authorization: live.authorization)
                let originKind: MusicPlaybackFeedbackOrigin.Kind
                switch kind {
                case "fm": originKind = .fm
                case "daily": originKind = .daily
                case "heartbeat": originKind = .heartbeat
                case "moreRcmd": originKind = .recommendations
                default: originKind = .similar
                }
                return .init(platform: target.platform, nativeID: nativeID, mixsongID: target.mixsongID,
                    source: source, sourceID: sourceID, algorithm: algorithm, automatic: target.automatic,
                    recommendationProof: proof, origin: .init(kind: originKind, platform: .netease,
                        sourceID: recommendation["sourceId"].id, action: live.action, seedIDs: live.seedIDs))
            }
        }
    }
    func retainQueueFeedbackOrigins(_ values: [String: MJ], authorization: MusicQueueAuthorization,
                                    action: MusicHomeAction? = nil, seedIDs: [String] = []) {
        for (id, value) in values {
            let heartbeatSeed = value["source"].string == "heartbeat" && MusicNativeSimilarData.id(value["seedId"].id)
                ? value["seedId"].id : nil
            queueFeedbackOrigins[id] = .init(value: value, authorization: authorization,
                action: heartbeatSeed.map { .heartbeatSong(songID: $0) } ?? action,
                seedIDs: heartbeatSeed.map { [$0] } ?? seedIDs)
        }
    }

    public func playbackFeedbackSettings() async throws -> MusicPlaybackFeedbackSettings {
        try MusicNativeFeedbackSettings.decode(read().playback)
    }
    /// Startup reads the existing authoritative playback settings before account
    /// readiness can trigger recovery of a safe pending file.
    public func preparePlaybackFeedback() async throws {
        let settings = try MusicNativeFeedbackSettings.decode(read().playback)
        if let owner = accounts as? any MusicNativeFeedbackLifecycleProviding {
            do { try await owner.configurePlaybackFeedback(settings) }
            catch { /* Failed feedback storage stays closed without blocking music startup or erasing it. */ }
        }
    }
    public func setPlaybackFeedback(platform: MusicAccountPlatform, enabled: Bool,
                                    expectedRevision: Int) async throws -> MusicPlaybackFeedbackSettings {
        try check(); try requirePlatform(platform.rawValue)
        // Revoke before CAS, even when the write conflicts or its outcome is
        // unknown. A failed UI save must not keep an in-flight report authorized.
        playbackFeedback.revoke(platform: platform)
        (accounts as? any MusicNativeFeedbackLifecycleProviding)?.cancelPendingPlaybackFeedback(platform: platform)
        let result = try transaction(["playback"]) { draft in
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
        if let owner = accounts as? any MusicNativeFeedbackLifecycleProviding {
            do { try await owner.configurePlaybackFeedback(result) }
            catch { owner.cancelPendingPlaybackFeedback(platform: platform) }
        }
        if try MusicNativeFeedbackSettings.decode(read().playback) != result {
            (accounts as? any MusicNativeFeedbackLifecycleProviding)?.cancelPendingPlaybackFeedback(platform: platform)
            throw MusicServiceError.staleRevision
        }
        return result
    }
    public func consumePlaybackFeedback(_ sample: MusicPlaybackFeedbackSample) async {
        do {
            try check()
            guard !feedbackClosing else { return }
            guard sample.resourceID == "music:" + sample.attemptID else { return }
            guard MusicNativeFeedbackController.valid(sample) else {
                playbackFeedback.breakContinuity(sample)
                return
            }
            let draft = try read()
            if let previous = playbackFeedbackAdmission, previous.retiringReason != nil,
               previous.attemptID == sample.attemptID, previous.occurrenceID == sample.occurrenceID,
               playbackFeedback.ownsAttempt(sample.attemptID),
               let port = accounts as? any MusicNativeFeedbackProviding {
                if ![.failed, .unavailable, .stopped].contains(sample.kind),
                   !MusicMediaDuration.matches(expected: previous.song["duration"].number, actual: sample.duration) {
                    playbackFeedback.invalidate(); playbackFeedbackAdmission = nil
                    return
                }
                let settings = try MusicNativeFeedbackSettings.decode(draft.playback), own = epoch
                // Only this outgoing listen's captured targets may settle.
                // A retired queue/source grant cannot admit another start.
                playbackFeedback.consume(sample, settings: settings,
                    targets: previous.targets.filter { platformEnabled($0.platform.rawValue) },
                    accounts: port) { [weak self] platform, generation in
                    guard let self else { throw MusicServiceError.closed }
                    try await self.checkFeedbackAuthorization(epoch: own, platform: platform, generation: generation)
                }
                return
            }
            guard draft.queue["currentId"].string == sample.occurrenceID,
                  let entry = draft.queue["entries"].array.first(where: { $0["id"].string == sample.occurrenceID }),
                  let port = accounts as? any MusicNativeFeedbackProviding else { return }
            let prepared = preparedSources[sample.attemptID]
            let cached = preparedCacheSources[sample.attemptID]
            guard attempt?.id == sample.attemptID && attempt?.ready == true ||
                playbackFeedbackAdmission?.attemptID == sample.attemptID && playbackFeedback.ownsAttempt(sample.attemptID) ||
                prepared?.armed == true && prepared?.candidate.resourceId == sample.resourceID ||
                cached?.armed == true && cached?.playback.candidate.resourceId == sample.resourceID else { return }
            if ![.failed, .unavailable, .stopped].contains(sample.kind),
               !MusicMediaDuration.matches(expected: entry["song"]["duration"].number, actual: sample.duration) {
                playbackFeedback.invalidate(); playbackFeedbackAdmission = nil
                return
            }
            // Feedback and durable progress have separate queues. The first
            // accepted native event commits prepared media in either ordering.
            try commitPreparedSource(.init(attemptId: sample.attemptID, occurrenceId: sample.occurrenceID,
                resourceId: sample.resourceID, kind: sample.kind == .playing ? .playing : .progress,
                position: sample.position, duration: sample.duration))
            let admission: MusicNativeFeedbackAdmission
            if let attempt, attempt.ready, attempt.id == sample.attemptID, attempt.occurrence == sample.occurrenceID {
                let song = try playbackSong(.init(kind: .queue, id: sample.occurrenceID), draft)
                try attempt.authorization?.check()
                if attempt.ended && ![.ended, .failed, .stopped].contains(sample.kind) { return }
                if !playbackFeedback.ownsAttempt(attempt.id) {
                    replaceFeedbackAttempt(attemptID: attempt.id, occurrenceID: sample.occurrenceID,
                        song: song, sourceKey: attempt.sourceKey, authorization: attempt.authorization)
                }
                admission = .init(attemptID: attempt.id, occurrenceID: attempt.occurrence,
                    song: song, sourceKey: attempt.sourceKey, authorization: attempt.authorization, ended: attempt.ended)
            } else {
                // Legacy resolve/renewal can own an unready successor while the
                // old native resource still sounds. Its admitted observations
                // remain valid until an actual committed handoff, not URL work.
                guard let previous = playbackFeedbackAdmission, previous.attemptID == sample.attemptID,
                      previous.occurrenceID == sample.occurrenceID,
                      playbackFeedback.ownsAttempt(sample.attemptID),
                      MusicModel.compatible(previous.song, entry["song"]) else { return }
                try previous.authorization?.check()
                admission = previous
            }
            _ = try target(admission.song, admission.sourceKey)
            playbackFeedbackAdmission = admission
            var context = entry["context"]
            if let automatic = sample.automatic { context["automatic"] = .bool(automatic) }
            var recordingEntry = entry
            recordingEntry["song"] = admission.song
            let targets = try similarFeedbackTargets(entry: recordingEntry, context: context, selectedSourceKey: admission.sourceKey)
                .filter { platformEnabled($0.platform.rawValue) }
            let settings = try MusicNativeFeedbackSettings.decode(draft.playback)
            let own = epoch
            // Audio/source grants retire on a normal handoff. The independent
            // exact-account feedback lease owns already admitted final reports.
            playbackFeedback.consume(sample, settings: settings, targets: targets, accounts: port) { [weak self] platform, generation in
                guard let self else { throw MusicServiceError.closed }
                try await self.checkFeedbackAuthorization(epoch: own, platform: platform, generation: generation)
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
    public func finishPlaybackFeedback(attemptID: String?, reason: MusicPlaybackFeedbackReport.Reason) async {
        // Selection invalidation can arrive before resolve/cancel on this actor.
        // Settle its last observed position once, never UI elapsed time. A
        // revoked owner only cancels and cannot manufacture a terminal report.
        guard (try? check()) != nil else {
            playbackFeedback.invalidate(attemptID: attemptID)
            return
        }
        playbackFeedback.finishCurrent(attemptID: attemptID, reason: reason)
        playbackFeedback.invalidate(attemptID: attemptID, preservingEnds: true)
        if attemptID == nil || playbackFeedbackAdmission?.attemptID == attemptID { playbackFeedbackAdmission = nil }
    }
    public func rejectPlaybackFeedback(attemptID: String) async {
        playbackFeedback.invalidate(attemptID: attemptID)
        if playbackFeedbackAdmission?.attemptID == attemptID { playbackFeedbackAdmission = nil }
    }
    func replaceFeedbackAttempt(attemptID: String, occurrenceID: String, song: MJ, sourceKey: String,
                                authorization: MusicQueueAuthorization?) {
        let old = playbackFeedbackAdmission
        if let old, let reason = old.retiringReason {
            playbackFeedback.finishCurrent(attemptID: old.attemptID, reason: reason)
        }
        let previousSong = old?.song ?? song
        let previousKey = old?.sourceKey
        let sources = song["sources"].array
        let previousSource = previousSong["sources"].array.first { $0["sourceKey"].string == previousKey }
        let selectedSource = sources.first { $0["sourceKey"].string == sourceKey }
        let same = old?.occurrenceID == occurrenceID && old?.ended == false &&
            sources.contains { $0["sourceKey"].string == previousKey } &&
            previousSource.flatMap { previous in selectedSource.map {
                previous["sourceKey"] == $0["sourceKey"] || MusicModel.compatible(previous, $0)
            } } == true
        playbackFeedback.replaceAttempt(attemptID: attemptID, occurrenceID: occurrenceID, preserving: same)
        playbackFeedbackAdmission = .init(attemptID: attemptID, occurrenceID: occurrenceID,
                                          song: song, sourceKey: sourceKey, authorization: authorization)
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
    static func targets(song: MJ, context: MJ, preferredNativeID: String? = nil) throws -> [MusicPlaybackFeedbackTarget] {
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
            let chosen = values.first { platform == .netease && $0.nativeID == preferredNativeID } ??
                values.first { $0 == primary } ??
                (values.count == 1 ? values.first : nil) ??
                (platform == .kugou && !values.isEmpty && values.allSatisfy { $0.mixsongID != nil } &&
                    Set(values.compactMap(\.mixsongID)).count == 1 ? values.sorted { $0.nativeID < $1.nativeID }.first : nil)
            guard let chosen else { continue }
            var source = "", sourceID = ""
            if platform == .netease {
                let type = context["sourceType"].string, id = context["sourceId"].id
                if context["platform"].string == "wy" && ["playlist", "album"].contains(type) && positive(id) {
                    source = type == "playlist" ? "list" : "album"; sourceID = id
                } else if context["platform"].string == "wy" && type == "daily" &&
                    ["", "default"].contains(context["dailyBranch"].string) {
                    source = "dailySongRecommend"
                } else if ["search", "search-song"].contains(type) {
                    let query = context["searchKeyword"].string
                    if query.utf16.count <= 512 && !query.unicodeScalars.contains(where: { $0.value < 32 || (127...159).contains($0.value) }) {
                        source = "search"; sourceID = query
                    }
                } else if type == "song" { source = "song" }
            }
            result.append(.init(platform: platform, nativeID: chosen.nativeID, mixsongID: chosen.mixsongID,
                source: source, sourceID: sourceID, automatic: context["automatic"].bool, origin: origin(context)))
        }
        return result
    }
    static func origin(_ context: MJ) -> MusicPlaybackFeedbackOrigin? {
        let platform = MusicPublicBrowsePlatform(rawValue: context["platform"].string)
        switch context["sourceType"].string {
        case "playlist", "album":
            guard let platform else { return nil }
            return .init(kind: context["sourceType"].string == "playlist" ? .playlist : .album,
                         platform: platform, sourceID: context["sourceId"].id)
        case "search", "search-song":
            let query = context["searchKeyword"].string
            guard query.utf16.count <= 512 && !query.unicodeScalars.contains(where: {
                $0.value < 32 || (127...159).contains($0.value)
            }) else { return nil }
            return .init(kind: .search, platform: platform, searchKeyword: query)
        case "daily":
            guard platform == .netease else { return nil }
            return .init(kind: .daily, platform: platform)
        case "song": return .init(kind: .song, platform: platform)
        default: return nil
        }
    }
}
