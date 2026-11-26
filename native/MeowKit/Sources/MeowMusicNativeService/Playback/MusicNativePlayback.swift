import Foundation
import MeowMusicPlatform

extension NativeMusicService {
    private static let hopByHopHeaders: Set<String> = [
        "host", "connection", "keep-alive", "proxy-connection", "te", "trailer", "transfer-encoding", "upgrade", "content-length"
    ]
    // Official account quality names are not LX capabilities. Keep their
    // grouping local to playback without rewriting the durable library schema.
    static func officialQualities(_ values: [String], _ prefs: MusicPlaybackSettingsState) -> [String] {
        let policy = MusicModel.policy(prefs)
        return values.enumerated().filter {
            guard MusicSourceQualityPolicy.qualities.contains($0.element) else { return false }
            let group = MusicSourceQualityPolicy.group($0.element)
            guard policy.enabled.contains(group) else { return false }
            return group != "spatial" || policy.spatial.contains($0.element)
        }.sorted {
            let leftGroup = MusicSourceQualityPolicy.group($0.element)
            let rightGroup = MusicSourceQualityPolicy.group($1.element)
            let left = policy.order.firstIndex(of: leftGroup) ?? 100
            let right = policy.order.firstIndex(of: rightGroup) ?? 100
            if left != right { return left < right }
            if leftGroup == "spatial" {
                return (policy.spatialOrder.firstIndex(of: $0.element) ?? 100) <
                    (policy.spatialOrder.firstIndex(of: $1.element) ?? 100)
            }
            return $0.offset < $1.offset
        }.map { $0.element }
    }

    func target(_ song: MusicSongRecord, _ key: String) throws -> (MusicNativeSource, MusicSongSourceRecord) {
        guard let row = song.sources.first(where: { $0.sourceKey == key }),
              let source = MusicModel.sources(song).first(where: { $0.sourceKey == key }) else {
            throw MusicServiceError.targetChanged
        }
        return (source, row)
    }

    func validate(_ source: MusicScriptSource, site: String, quality: String? = nil, action: String = "musicUrl") throws -> MusicScriptCapability {
        try musicRequire(source.isCandidate && source.generation >= 0 &&
            (source.state == .ready ? source.generation > 0 : source.generation == 0), .sourceUnavailable)
        guard let cap = source.capabilities.first(where: { $0.siteID == site }), cap.actions.contains(action),
              quality == nil || cap.qualities.contains(quality!) else { throw MusicServiceError.sourceUnavailable }
        return cap
    }

    func sameSource(_ current: MusicScriptSource, _ previous: MusicScriptSource) throws {
        try musicRequire(current.isCandidate && current.installationID == previous.installationID &&
            current.generation == previous.generation && current.identity == previous.identity, .targetChanged)
    }

    func pinScript(_ current: MusicScriptSource, selection: MusicPlaybackSelection) throws {
        try musicRequire(current.isCandidate && current.installationID == selection.installationId &&
            current.identity == selection.scriptIdentity &&
            (current.generation == selection.generation ||
             selection.generation == 0 && current.state == .ready && current.generation > 0), .targetChanged)
    }

    func executedScript(_ executed: MusicScriptSource, from previous: MusicScriptSource) throws {
        try musicRequire(executed.state == .ready && executed.generation > 0 &&
            executed.installationID == previous.installationID && executed.identity == previous.identity &&
            (previous.generation == 0 || executed.generation == previous.generation), .targetChanged)
    }

    func media(_ value: MusicNativeAudio) throws -> MusicNativeAudio {
        try value.playbackAuthorization?.check()
        let url = value.url
        try musicRequire(["https", "http"].contains(url.scheme?.lowercased() ?? "") &&
            url.host?.isEmpty == false, .invalidMediaCandidate)
        var headers: [String: String] = [:]
        var seen = Set<String>()
        for (rawKey, rawVal) in value.headers {
            let key = rawKey.trimmingCharacters(in: .whitespacesAndNewlines)
            let lower = key.lowercased()
            if Self.hopByHopHeaders.contains(lower) { continue }
            try musicRequire(!key.isEmpty && key.utf8.allSatisfy({ $0 > 32 && $0 < 127 }) &&
                seen.insert(lower).inserted &&
                !rawVal.unicodeScalars.contains(where: { $0.value < 32 || $0.value == 127 }), .invalidMediaCandidate)
            headers[key] = rawVal
        }
        if let expires = value.expiresAt { try musicRequire(expires > now() && expires.timeIntervalSince1970.isFinite, .invalidMediaCandidate) }
        return .init(url: url, headers: headers, expiresAt: value.expiresAt, fileCachePolicy: value.fileCachePolicy,
                     delivery: value.delivery, playbackAuthorization: value.playbackAuthorization, duration: value.duration)
    }

    public func cancel(attemptId: String?) async throws {
        try check()
        if attemptId == nil || playbackFeedbackAdmission?.retiringReason == nil ||
            playbackFeedbackAdmission?.attemptID != attemptId {
            playbackFeedback.finishCurrent(attemptID: attemptId, reason: .interrupt)
            playbackFeedback.invalidate(attemptID: attemptId, preservingEnds: true)
        }
        if let attemptId { discardSource(attemptID: attemptId) } else { clearPreparedSources() }
        if attemptId == nil || attempt?.id == attemptId {
            retireAudioCacheAttempt()
            attempt = nil; cancelWork("playback")
        }
    }

    public func record(_ fact: MusicPlaybackFact) async throws -> Bool {
        try check()
        try musicRequire(fact.position.isFinite && (0...604_800).contains(fact.position) &&
            (fact.duration == nil || fact.duration!.isFinite && (0...604_800).contains(fact.duration!)))
        try commitPreparedSource(fact)
        guard let current = attempt, current.ready, current.id == fact.attemptId, current.occurrence == fact.occurrenceId,
              fact.resourceId == "music:" + current.id else { throw MusicServiceError.staleResult }
        try current.authorization?.check()
        let fileAuthorization = audioCacheAttempt.flatMap {
            $0.attemptID == current.id && $0.audio == nil ? $0.authorization : nil
        }
        if current.ended { return false }
        let recordPlay = fact.kind == .playing && !current.started
        let sections: Set<MusicDomainSection> = recordPlay ? [.history, .playback] : fact.kind == .failed ? [.queue, .playback] : [.playback]
        let commit = { [self] in try transaction(sections) { d in
            try musicRequire(d.queue.currentID == current.occurrence, .staleResult)
            guard let entry = d.queue.entries.first(where: { $0.id == current.occurrence }) else { throw MusicServiceError.staleResult }
            if recordPlay {
                let date = historyDayFormatter.string(from: now())
                let atMs = Int64((now().timeIntervalSince1970 * 1000).rounded())
                let item = MusicHistoryDaySongRecord(songID: entry.songID, playedAtMs: atMs, context: entry.context)
                if let index = d.history.days.firstIndex(where: { $0.date == date }) {
                    var day = d.history.days[index]
                    day.songs = [item] + day.songs.filter { $0.songID != item.songID }
                    day.updatedAtMs = atMs
                    d.history.days[index] = day
                } else {
                    d.history.days.append(MusicHistoryDayRecord(date: date, dayID: date, updatedAtMs: atMs, songs: [item]))
                }
                d.history.days.sort { $0.date > $1.date }
            }
            d.playback.positionOccurrenceID = current.occurrence
            d.playback.positionSeconds = fact.kind == .ended ? 0 : fact.position
            if fact.kind == .failed {
                d.queue.failed = Array(Set(d.queue.failed + [current.occurrence]))
                d.queue.revision += 1
            }
            return true
        } }
        let accepted: Bool
        if let authorization = current.authorization { accepted = try authorization.perform(commit) }
        else if let fileAuthorization { accepted = try fileAuthorization.perform(commit) }
        else { accepted = try commit() }
        if recordPlay { attempt?.started = true }
        if [.ended, .failed, .stopped].contains(fact.kind) {
            attempt?.ended = true
            if playbackFeedbackAdmission?.attemptID == fact.attemptId { playbackFeedbackAdmission?.ended = true }
        }
        if accepted {
            if fact.kind == .playing { attempt?.isPlaying = true }
            else if [.paused, .ended, .failed, .stopped].contains(fact.kind) { attempt?.isPlaying = false }
            attempt?.latestFact = fact
            audioCacheAcceptedFact(fact)
            if recordPlay {
                let song = try self.song(.init(kind: .queue, id: current.occurrence), read())
                await presentationCache?.played(songID: song.songID,
                    artwork: MusicModel.artworkURL(song.coverURL), confirmedAt: now())
            }
        }
        return accepted
    }

    public func lyrics(occurrenceId: String, sourceKey: String) async throws -> MusicLyricsSnapshot {
        let own = epoch, d = try read()
        try musicRequire(d.queue.currentID == occurrenceId, .targetChanged)
        let original = try song(.init(kind: .queue, id: occurrenceId), d)
        let song = try await sharedSongMetadata(original), (source, infoRow) = try target(song, sourceKey)
        try check(own)
        try musicRequire(try read().queue.currentID == occurrenceId, .targetChanged)
        if !d.playback.lyricsEnabled { return .init(occurrenceId: occurrenceId, state: .disabled, lines: []) }
        try requirePlatform(source.platform)
        let serial = UUID(); lyricEpoch = serial; cancelWork("lyrics")
        var lines: [MusicLyricLine]
        if ["wy", "tx", "kg", "kw", "mg"].contains(source.platform) {
            let port = publicPlatforms
            let payload = try await work(label: "lyrics") { try await port.lyrics(platform: source.platform, info: infoRow) }
            lines = try MusicNativeLyricsParser.parse(payload, duration: song.effectiveDuration)
            if lines.isEmpty, ["wy", "kw", "mg"].contains(source.platform) {
                lines = try await scriptLyrics(platform: source.platform, sourceKey: sourceKey, info: infoRow,
                                               duration: song.effectiveDuration)
            }
        } else {
            guard let chosen = attempt, chosen.occurrence == occurrenceId, chosen.sourceKey == sourceKey,
                  !chosen.selection.installationId.hasPrefix("official:"), let scripts else { throw MusicServiceError.sourceRequired }
            let selection = chosen.selection
            let live = try await work(label: "lyrics") { try await scripts.source(installationID: selection.installationId) }
            _ = try validate(live, site: source.platform, action: "lyric")
            guard let executed = chosen.scriptSource else { throw MusicServiceError.sourceUnavailable }
            try sameSource(live, executed)
            let infoBytes = try MusicWireCodec.buildMusicInfoData(infoRow, cleanedLocator: true)
            let request = MusicScriptRequest(source: live, siteID: source.platform,
                sourceKey: sourceKey, quality: selection.quality, musicInfo: infoBytes)
            let execution = try await work(label: "lyrics") { try await scripts.lyrics(request: request) }
            try executedScript(execution.source, from: live)
            _ = try validate(execution.source, site: source.platform, action: "lyric")
            try musicRequire(attempt?.id == chosen.id, .staleResult)
            lines = try MusicNativeLyricsParser.parse(execution.value, duration: song.effectiveDuration)
        }
        let current = try read()
        try musicRequire(serial == lyricEpoch && current.queue.currentID == occurrenceId &&
            current.playback.lyricsEnabled, .staleResult)
        return .init(occurrenceId: occurrenceId, state: lines.isEmpty ? .none : .ready, lines: lines)
    }

    private func scriptLyrics(platform: String, sourceKey: String, info: MusicSongSourceRecord, duration: Double) async throws -> [MusicLyricLine] {
        guard let scripts else { return [] }
        let snapshots: [MusicScriptSource]
        do { snapshots = try await work(label: "lyrics") { try await scripts.sources() } }
        catch MusicServiceError.sourceUnavailable { return [] }
        let infoBytes = try MusicWireCodec.buildMusicInfoData(info, cleanedLocator: true)
        for snapshot in snapshots where snapshot.isCandidate &&
            snapshot.capabilities.contains(where: { $0.siteID == platform && $0.actions.contains("lyric") }) {
            _ = try validate(snapshot, site: platform, action: "lyric")
            let request = MusicScriptRequest(source: snapshot,
                siteID: platform, sourceKey: sourceKey, quality: "", musicInfo: infoBytes)
            let execution: MusicScriptResult<MusicNativeLyrics>
            do { execution = try await work(label: "lyrics") { try await scripts.lyrics(request: request) } }
            catch MusicServiceError.sourceUnavailable { continue }
            try executedScript(execution.source, from: snapshot)
            let lines = try MusicNativeLyricsParser.parse(execution.value, duration: duration)
            if !lines.isEmpty { return lines }
        }
        return []
    }
}
