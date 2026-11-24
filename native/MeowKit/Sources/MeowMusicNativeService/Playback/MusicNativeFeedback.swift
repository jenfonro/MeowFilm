import Foundation
import MeowDebug
import MeowMusicFeature

extension NativeMusicService: MusicPlaybackFeedbackClosePreparing {
    public nonisolated func stopPlaybackFeedbackDispatchForClose() {
        (accounts as? any MusicNativeFeedbackLifecycleProviding)?.stopPlaybackFeedbackDispatchForClose()
    }
}

struct MusicNativeFeedbackAdmission {
    let attemptID: String, occurrenceID: String
    let song: MusicSongRecord
    let sourceKey: String
    let authorization: MusicQueueAuthorization?
    var ended = false
    var targets: [MusicPlaybackFeedbackTarget] = []
    var retiringReason: MusicPlaybackFeedbackReport.Reason?
}

struct MusicNativeQueueFeedbackOrigin {
    let value: MusicRecommendationMetadata
    let authorization: MusicQueueAuthorization
    let action: MusicHomeAction?
    let seedIDs: [String]
}

extension NativeMusicService: MusicPlaybackFeedbackConsuming, MusicPlaybackFeedbackSettingsServing {
    func similarFeedbackTargets(entry: MusicQueueEntryRecord, song: MusicSongRecord, context: MusicPlayContextRecord,
                                automatic: Bool, selectedSourceKey: String? = nil) throws -> [MusicPlaybackFeedbackTarget] {
        let selectedNativeID = song.sources.first { $0.sourceKey == selectedSourceKey }
            .flatMap { MusicNativeFeedbackMapping.recording($0, platform: .netease)?.nativeID }
        guard let live = liveRecommendation(entryID: entry.id), live.authorization.isActive else {
            return try MusicNativeFeedbackMapping.targets(song: song, context: context, automatic: automatic, preferredNativeID: selectedNativeID)
        }
        return try live.authorization.perform {
            let recommendation = live.value
            let nativeID = recommendation.songID
            let targets = try MusicNativeFeedbackMapping.targets(song: song, context: context, automatic: automatic,
                preferredNativeID: selectedNativeID ?? nativeID)
            return targets.map { target in
                guard target.platform == .netease, target.nativeID == nativeID,
                      selectedNativeID == nil || selectedNativeID == nativeID else { return target }
                let proof = MusicPlaybackRecommendationProof(occurrenceID: entry.id, nativeID: nativeID,
                    source: recommendation.feedbackSource, sourceID: recommendation.feedbackSourceID,
                    algorithm: recommendation.algorithm, authorization: live.authorization)
                let originKind: MusicPlaybackFeedbackOrigin.Kind
                switch recommendation.source {
                case "fm": originKind = .fm
                case "daily": originKind = .daily
                case "heartbeat": originKind = .heartbeat
                case "moreRcmd": originKind = .recommendations
                default: originKind = .similar
                }
                return .init(platform: target.platform, nativeID: nativeID, mixsongID: target.mixsongID,
                    source: recommendation.feedbackSource, sourceID: recommendation.feedbackSourceID,
                    algorithm: recommendation.algorithm, automatic: target.automatic,
                    recommendationProof: proof, origin: .init(kind: originKind, platform: .netease,
                        sourceID: recommendation.sourceID, action: live.action, seedIDs: live.seedIDs))
            }
        }
    }

    func retainQueueFeedbackOrigins(_ values: [String: MusicRecommendationMetadata], authorization: MusicQueueAuthorization,
                                    action: MusicHomeAction? = nil, seedIDs: [String] = []) {
        for (id, value) in values {
            let heartbeatSeed = value.source == "heartbeat" && !value.seedID.isEmpty ? value.seedID : nil
            queueFeedbackOrigins[id] = .init(value: value, authorization: authorization,
                action: heartbeatSeed.map { .heartbeatSong(songID: $0) } ?? action,
                seedIDs: heartbeatSeed.map { [$0] } ?? seedIDs)
        }
    }

    public func playbackFeedbackSettings() async throws -> MusicPlaybackFeedbackSettings {
        try MusicNativeFeedbackSettings.decode(read().playback)
    }

    public func preparePlaybackFeedback() async throws {
        let settings = try MusicNativeFeedbackSettings.decode(read().playback)
        if let owner = accounts as? any MusicNativeFeedbackLifecycleProviding {
            do { try await owner.configurePlaybackFeedback(settings) }
            catch {
                if !Task.isCancelled {
                    DebugRuntime.logError(.musicPlayback, sub: "feedback", event: "prepare.failed", error: error)
                }
            }
        }
    }

    public func setPlaybackFeedback(platform: MusicAccountPlatform, enabled: Bool,
                                    expectedRevision: Int) async throws -> MusicPlaybackFeedbackSettings {
        try check(); try requirePlatform(platform.rawValue)
        let result = try transaction([.playback]) { draft in
            let old = try MusicNativeFeedbackSettings.decode(draft.playback)
            try musicRequire(old.revision == expectedRevision, .staleRevision)
            let setting = old.platforms[platform] ?? .init()
            try musicRequire(setting.generation < musicMaxSafeInteger &&
                old.revision < musicMaxSafeInteger, .limit)
            let updated = MusicFeedbackPlatformRecord(platform: platform.rawValue, enabled: enabled, generation: setting.generation + 1)
            if let index = draft.playback.feedback.firstIndex(where: { $0.platform == platform.rawValue }) {
                draft.playback.feedback[index] = updated
            } else {
                draft.playback.feedback.append(updated)
            }
            draft.playback.revision = old.revision + 1
            return try MusicNativeFeedbackSettings.decode(draft.playback)
        }
        playbackFeedback.revoke(platform: platform)
        if let owner = accounts as? any MusicNativeFeedbackLifecycleProviding {
            owner.cancelPendingPlaybackFeedback(platform: platform)
            do { try await owner.configurePlaybackFeedback(result) }
            catch {
                DebugRuntime.logError(.musicPlayback, sub: "feedback", event: "configure.failed", error: error)
                owner.cancelPendingPlaybackFeedback(platform: platform)
            }
        }
        DebugRuntime.log(.musicPlayback, sub: "feedback", event: "settings.updated", level: .info, data: .object([
            "platform": .string(platform.rawValue),
            "enabled": .bool(enabled),
            "revision": .int(result.revision)
        ]))
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
                   !MusicMediaDuration.matches(expected: previous.song.effectiveDuration, actual: sample.duration) {
                    playbackFeedback.invalidate(); playbackFeedbackAdmission = nil
                    return
                }
                let settings = try MusicNativeFeedbackSettings.decode(draft.playback), own = epoch
                playbackFeedback.consume(sample, settings: settings,
                    targets: previous.targets.filter { platformEnabled($0.platform.rawValue) },
                    accounts: port) { [weak self] platform, generation in
                    guard let self else { throw MusicServiceError.closed }
                    try await self.checkFeedbackAuthorization(epoch: own, platform: platform, generation: generation)
                }
                return
            }
            guard draft.queue.currentID == sample.occurrenceID,
                  let entry = draft.queue.entries.first(where: { $0.id == sample.occurrenceID }),
                  let port = accounts as? any MusicNativeFeedbackProviding else { return }
            let prepared = preparedSources[sample.attemptID]
            let cached = preparedCacheSources[sample.attemptID]
            guard attempt?.id == sample.attemptID && attempt?.ready == true ||
                playbackFeedbackAdmission?.attemptID == sample.attemptID && playbackFeedback.ownsAttempt(sample.attemptID) ||
                prepared?.armed == true && prepared?.candidate.resourceId == sample.resourceID ||
                cached?.armed == true && cached?.playback.candidate.resourceId == sample.resourceID else { return }
            if ![.failed, .unavailable, .stopped].contains(sample.kind),
               !MusicMediaDuration.matches(expected: try draft.song(entry.songID).effectiveDuration, actual: sample.duration) {
                playbackFeedback.invalidate(); playbackFeedbackAdmission = nil
                return
            }
            try commitPreparedSource(.init(attemptId: sample.attemptID, occurrenceId: sample.occurrenceID,
                resourceId: sample.resourceID, kind: sample.kind == .playing ? .playing : .progress,
                position: sample.position, duration: sample.duration))
            let admission: MusicNativeFeedbackAdmission
            if let attempt, attempt.ready, attempt.id == sample.attemptID, attempt.occurrence == sample.occurrenceID {
                let song = try self.song(.init(kind: .queue, id: sample.occurrenceID), draft)
                try attempt.authorization?.check()
                if attempt.ended && ![.ended, .failed, .stopped].contains(sample.kind) { return }
                if !playbackFeedback.ownsAttempt(attempt.id) {
                    let isSeamlessHandoff = attempt.recovery != nil && attempt.started &&
                        playbackFeedbackAdmission?.retiringReason == nil
                    replaceFeedbackAttempt(attemptID: attempt.id, occurrenceID: sample.occurrenceID,
                        song: song, sourceKey: attempt.sourceKey, authorization: attempt.authorization,
                        preservingListen: isSeamlessHandoff)
                }
                admission = .init(attemptID: attempt.id, occurrenceID: attempt.occurrence,
                    song: song, sourceKey: attempt.sourceKey, authorization: attempt.authorization, ended: attempt.ended)
            } else {
                guard let previous = playbackFeedbackAdmission, previous.attemptID == sample.attemptID,
                      previous.occurrenceID == sample.occurrenceID,
                      playbackFeedback.ownsAttempt(sample.attemptID),
                      previous.song.songID == entry.songID else { return }
                try previous.authorization?.check()
                admission = previous
            }
            _ = try target(admission.song, admission.sourceKey)
            playbackFeedbackAdmission = admission
            let targets = try similarFeedbackTargets(
                entry: entry,
                song: admission.song,
                context: entry.context,
                automatic: sample.automatic ?? false,
                selectedSourceKey: admission.sourceKey
            ).filter { platformEnabled($0.platform.rawValue) }
            let settings = try MusicNativeFeedbackSettings.decode(draft.playback)
            let own = epoch
            playbackFeedback.consume(sample, settings: settings, targets: targets, accounts: port) { [weak self] platform, generation in
                guard let self else { throw MusicServiceError.closed }
                try await self.checkFeedbackAuthorization(epoch: own, platform: platform, generation: generation)
            }
        } catch {
            if !Task.isCancelled, MusicServiceError.sanitized(error) != .cancelled {
                DebugRuntime.logError(.musicPlayback, sub: "feedback", event: "consume.error", error: error)
            }
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

    func replaceFeedbackAttempt(attemptID: String, occurrenceID: String, song: MusicSongRecord, sourceKey: String,
                                authorization: MusicQueueAuthorization?, preservingListen: Bool) {
        let old = playbackFeedbackAdmission
        if let old, let reason = old.retiringReason {
            playbackFeedback.finishCurrent(attemptID: old.attemptID, reason: reason)
        }
        let same = preservingListen && old?.retiringReason == nil && old?.ended == false &&
            old?.occurrenceID == occurrenceID && old?.song.songID == song.songID
        playbackFeedback.replaceAttempt(attemptID: attemptID, occurrenceID: occurrenceID, preserving: same)
        playbackFeedbackAdmission = .init(attemptID: attemptID, occurrenceID: occurrenceID,
                                          song: song, sourceKey: sourceKey, authorization: authorization)
    }
}

enum MusicNativeFeedbackSettings {
    static func decode(_ playback: MusicPlaybackSettingsState) throws -> MusicPlaybackFeedbackSettings {
        try musicRequire((0...musicMaxSafeInteger).contains(playback.revision), .corruptStorage)
        var result: [MusicAccountPlatform: MusicPlaybackFeedbackSetting] = [:]
        let byPlatform = Dictionary(uniqueKeysWithValues: playback.feedback.map { ($0.platform, $0) })
        for platform in [MusicAccountPlatform.netease, .qq, .kugou] {
            guard let row = byPlatform[platform.rawValue] else {
                result[platform] = .init()
                continue
            }
            try musicRequire((0...musicMaxSafeInteger).contains(row.generation), .corruptStorage)
            result[platform] = .init(enabled: row.enabled, generation: row.generation)
        }
        return .init(revision: playback.revision, platforms: result)
    }
}

enum MusicNativeFeedbackMapping {
    static func recording(_ row: MusicSongSourceRecord, platform: MusicAccountPlatform) -> MusicPlaybackFeedbackTarget? {
        let sourcePlatform = row.infoSource.isEmpty ? row.platform : row.infoSource
        guard sourcePlatform == platform.rawValue else { return nil }
        let id = platform == .kugou ? (row.hash ?? "") : row.resolvedSongmid
        guard !id.isEmpty else { return nil }
        let mixsong = row.resolvedAlbumAudioID
        return .init(platform: platform, nativeID: id,
                     mixsongID: platform == .kugou && !mixsong.isEmpty ? mixsong : nil)
    }

    static func targets(song: MusicSongRecord, context: MusicPlayContextRecord, automatic: Bool = false,
                        preferredNativeID: String? = nil) throws -> [MusicPlaybackFeedbackTarget] {
        _ = try MusicModel.sources(song)
        var result: [MusicPlaybackFeedbackTarget] = []
        for platform in [MusicAccountPlatform.netease, .qq, .kugou] {
            var values: [MusicPlaybackFeedbackTarget] = []
            for row in song.sources {
                guard let value = recording(row, platform: platform), !values.contains(value) else { continue }
                values.append(value)
            }
            if platform == .kugou {
                let known = Set(values.filter { $0.mixsongID != nil }.map(\.nativeID))
                values.removeAll { $0.mixsongID == nil && known.contains($0.nativeID) }
            }
            let primary = song.sources.first.flatMap { recording($0, platform: platform) }
            let chosen = values.first { platform == .netease && $0.nativeID == preferredNativeID } ??
                values.first { $0 == primary } ??
                (values.count == 1 ? values.first : nil) ??
                (platform == .kugou && !values.isEmpty && values.allSatisfy { $0.mixsongID != nil } &&
                    Set(values.compactMap(\.mixsongID)).count == 1 ? values.sorted { $0.nativeID < $1.nativeID }.first : nil)
            guard let chosen else { continue }
            var source = "", sourceID = ""
            if platform == .netease {
                let type = context.sourceType ?? "", id = context.sourceId ?? ""
                if context.platform == "wy" && ["playlist", "album"].contains(type) && !id.isEmpty {
                    source = type == "playlist" ? "list" : "album"; sourceID = id
                } else if context.platform == "wy" && type == "daily" &&
                    ["", "default"].contains(context.dailyBranch ?? "") {
                    source = "dailySongRecommend"
                } else if ["search", "search-song"].contains(type) {
                    source = "search"; sourceID = context.searchKeyword ?? ""
                } else if type == "song" { source = "song" }
            }
            result.append(.init(platform: platform, nativeID: chosen.nativeID, mixsongID: chosen.mixsongID,
                source: source, sourceID: sourceID, automatic: automatic, origin: origin(context)))
        }
        return result
    }

    static func origin(_ context: MusicPlayContextRecord) -> MusicPlaybackFeedbackOrigin? {
        let platform = context.platform.flatMap(MusicPublicBrowsePlatform.init(rawValue:))
        switch context.sourceType ?? "" {
        case "playlist", "album":
            guard let platform else { return nil }
            return .init(kind: context.sourceType == "playlist" ? .playlist : .album,
                         platform: platform, sourceID: context.sourceId ?? "")
        case "search", "search-song":
            return .init(kind: .search, platform: platform, searchKeyword: context.searchKeyword ?? "")
        case "daily":
            guard platform == .netease else { return nil }
            return .init(kind: .daily, platform: platform)
        case "song": return .init(kind: .song, platform: platform)
        default: return nil
        }
    }
}
