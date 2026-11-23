import Foundation
import MeowMusicFeature

extension NativeMusicService {
    static func officialQualities(_ values: [String], _ prefs: MusicPlaybackSettingsState) -> [String] {
        let policy = MusicModel.policy(prefs)
        let groups = ["128k": "standard", "192k": "high", "320k": "high", "flac": "lossless",
                      "flac24bit": "hires", "master": "master", "jyeffect": "enhanced",
                      "dolby": "spatial", "sky": "spatial", "vivid": "spatial"]
        return values.enumerated().filter {
            guard let group = groups[$0.element], policy.enabled.contains(group) else { return false }
            return group != "spatial" || policy.spatial.contains($0.element)
        }.sorted {
            let left = policy.order.firstIndex(of: groups[$0.element]!) ?? 100
            let right = policy.order.firstIndex(of: groups[$1.element]!) ?? 100
            if left != right { return left < right }
            if groups[$0.element] == "spatial" {
                return (policy.spatialOrder.firstIndex(of: $0.element) ?? 100) <
                    (policy.spatialOrder.firstIndex(of: $1.element) ?? 100)
            }
            return $0.offset < $1.offset
        }.map { $0.element }
    }

    func target(_ song: MusicSongRecord, _ key: String) throws -> (MusicNativeSource, MusicSongSourceRecord) {
        guard let row = song.sources.first(where: { $0.sourceKey.utf8.elementsEqual(key.utf8) }),
              let source = try MusicModel.sources(song).first(where: { $0.sourceKey.utf8.elementsEqual(key.utf8) }) else {
            throw MusicServiceError.targetChanged
        }
        return (source, row)
    }

    func validate(_ source: MusicScriptSource, site: String, quality: String? = nil, action: String = "musicUrl") throws -> MusicScriptCapability {
        try musicRequire(source.isCandidate && source.generation >= 0 &&
            (source.state == .ready ? source.generation > 0 : source.generation == 0) &&
            source.generation <= musicMaxSafeInteger && source.capabilities.count <= 5, .sourceUnavailable)
        _ = try musicText(source.installationID, maximum: 128); _ = try musicText(source.version)
        try musicRequire(musicHex(source.contentHash) && musicHex(source.revisionHash), .invalidResponse)
        try musicRequire(Set(source.capabilities.map(\.siteID)).count == source.capabilities.count, .invalidResponse)
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

    public func sourceAvailability(for ref: MusicReference) async throws -> MusicSourceAvailability {
        let own = epoch, d = try read(), song = try song(ref, d)
        let excluded = try recordingSourceExclusions(song)
        try check(own)
        let rows = try MusicModel.sources(song).filter { platformEnabled($0.platform) && !excluded.contains($0.sourceKey) }
        var result: [MusicPlaybackOption] = []
        var scriptState: MusicSourceAvailability.ScriptState = scripts == nil ? .notConfigured : .ready
        if let scripts, !rows.isEmpty {
            let snapshots: [MusicScriptSource]
            do { snapshots = try await work { try await scripts.sources() } }
            catch {
                try check(own)
                let failure = MusicServiceError.sanitized(error)
                guard failure == .unavailable || MusicCandidateDiscoveryFailures.mayTryAnother(failure) else { throw failure }
                scriptState = .unavailable(failure); snapshots = []
            }
            result += try scriptOptions(song: song, playback: d.playback, snapshots: snapshots,
                                        excluded: excluded, manual: true).map(\.playback)
        }
        if let accounts {
            for row in rows {
                guard let platform = MusicAccountPlatform(rawValue: row.platform),
                      Self.officialEnabled(row.platform, d.playback) else { continue }
                do {
                    let values = try await work { try await accounts.nativeOptions(platform: platform, nativeID: row.nativeId, sourceKey: row.sourceKey) }
                    for value in values {
                        let qualities = value.qualities
                        if !qualities.isEmpty {
                            result.append(.init(installationId: value.installationId, generation: value.generation,
                                siteId: value.siteId, sourceKey: row.sourceKey, qualities: qualities, title: value.title,
                                version: value.version, contentHash: value.contentHash))
                        }
                    }
                } catch {
                    try check(own)
                    let failure = MusicServiceError.sanitized(error)
                    guard failure.isAuthenticationFailure || failure == .unavailable ||
                        MusicCandidateDiscoveryFailures.mayTryAnother(failure) ||
                        MusicCandidateDiscoveryFailures.isContentFailure(failure) else { throw failure }
                }
            }
        }
        try check(own)
        let current = try read()
        _ = try self.song(ref, current)
        try musicRequire(current.playback.revision == d.playback.revision, .staleRevision)
        result = try result.filter { try !sourceExcluded(song, provider: $0.installationId, platform: $0.siteId) }
        return .init(options: result, scriptState: scriptState)
    }

    func media(_ value: MusicNativeAudio) throws -> MusicNativeAudio {
        try value.playbackAuthorization?.check()
        let url = value.url
        try musicRequire(["https", "http"].contains(url.scheme?.lowercased() ?? "") &&
            url.host?.isEmpty == false && url.user == nil && url.password == nil && url.fragment == nil, .invalidMediaCandidate)
        var headers: [String: String] = [:]
        for (key, value) in value.headers {
            let name = key.lowercased()
            try musicRequire(["referer", "user-agent"].contains(name) && headers[name] == nil &&
                !value.unicodeScalars.contains(where: { $0.value < 32 || $0.value == 127 }), .invalidMediaCandidate)
            headers[name] = value
        }
        if let expires = value.expiresAt { try musicRequire(expires > now() && expires.timeIntervalSince1970.isFinite, .invalidMediaCandidate) }
        return .init(url: url, headers: headers, expiresAt: value.expiresAt, fileCachePolicy: value.fileCachePolicy,
                     delivery: value.delivery, playbackAuthorization: value.playbackAuthorization, duration: value.duration)
    }

    public func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) async throws -> MusicPlaybackCandidate {
        try check()
        try musicRequire(UUID(uuidString: attemptId)?.uuidString.lowercased() == attemptId && !spent.contains(attemptId), .staleResult)
        let own = epoch, d = try read()
        try musicRequire(d.queue.currentID == occurrenceId, .targetChanged)
        let ref = MusicReference(kind: .queue, id: occurrenceId)
        let song = try self.song(ref, d), (source, _) = try target(song, selection.sourceKey)
        let songID = song.songID
        try musicRequire(!spent.contains(attemptId), .staleResult)
        try admitSource(song, selection: selection)
        try musicRequire(source.platform == selection.siteId, .targetChanged)
        try requirePlatform(source.platform)
        try musicRequire(!selection.installationId.hasPrefix("official:") ||
            Self.officialEnabled(source.platform, d.playback), .sourceUnavailable)
        retireCachedPlaybackRecovery()
        retireAudioCacheAttempt()
        cancelWork("playback"); spent.insert(attemptId)
        attempt = Attempt(id: attemptId, occurrence: occurrenceId, sourceKey: selection.sourceKey,
                          selection: selection)
        let start = d.playback.positionSeconds
        do {
            let audio: MusicNativeAudio
            let resolvedSelection: MusicPlaybackSelection
            if selection.installationId.hasPrefix("official:") {
                guard let platform = MusicAccountPlatform(rawValue: source.platform), let accounts else {
                    throw MusicServiceError.sourceUnavailable
                }
                audio = try await work(label: "playback") { try await accounts.nativeResolve(platform: platform, nativeID: source.nativeId, selection: selection) }
                resolvedSelection = selection
            } else {
                guard let scripts else { throw MusicServiceError.sourceUnavailable }
                let snapshot = try await work(label: "playback") { try await scripts.source(installationID: selection.installationId) }
                _ = try validate(snapshot, site: source.platform, quality: selection.quality)
                try pinScript(snapshot, selection: selection)
                try musicRequire(attempt?.id == attemptId, .staleResult)
                let (_, infoRow) = try target(repositorySong(songID, read()), selection.sourceKey)
                let infoBytes = try MusicWireCodec.buildMusicInfoData(infoRow, cleanedLocator: true)
                let request = MusicScriptRequest(source: snapshot,
                    siteID: source.platform, sourceKey: source.sourceKey, quality: selection.quality, musicInfo: infoBytes)
                let execution = try await work(label: "playback") { try await scripts.resolve(request: request) }
                try executedScript(execution.source, from: snapshot)
                let current = try await work(label: "playback") { try await scripts.source(installationID: snapshot.installationID) }
                try sameSource(current, execution.source); _ = try validate(current, site: source.platform, quality: selection.quality)
                try musicRequire(attempt?.id == attemptId, .staleResult)
                attempt?.scriptSource = current
                resolvedSelection = .init(installationId: current.installationID, generation: current.generation,
                    siteId: selection.siteId, sourceKey: selection.sourceKey, quality: selection.quality,
                    scriptIdentity: current.identity)
                audio = execution.value
            }
            try check(own)
            let current = try read()
            try musicRequire(attempt?.id == attemptId && current.queue.currentID == occurrenceId, .staleResult)
            attempt?.selection = resolvedSelection
            let currentSong = try repositorySong(songID, current)
            _ = try target(currentSong, selection.sourceKey)
            try musicRequire(!selection.installationId.hasPrefix("official:") ||
                Self.officialEnabled(source.platform, current.playback), .sourceUnavailable)
            let valid = try media(audio)
            try validateDelivery(valid, song: currentSong, playback: current.playback, automaticQuality: false)
            let authorization = valid.playbackAuthorization.map { MusicQueueAuthorization(parent: $0) } ?? MusicQueueAuthorization()
            audioCacheAttempt = try await prepareAudioCache(audio: valid, song: currentSong,
                selection: resolvedSelection, attemptID: attemptId, playbackAuthorization: authorization)
            observeAudioCacheCapture(audioCacheAttempt)
            try await admitRecordingSource(currentSong, sourceKey: selection.sourceKey)
            try check(own)
            try musicRequire(attempt?.id == attemptId, .staleResult)
            let latest = try read()
            try musicRequire(latest.queue.currentID == occurrenceId &&
                latest.queue.revision == d.queue.revision &&
                latest.playback.revision == d.playback.revision, .staleRevision)
            let latestSong = try repositorySong(songID, latest)
            _ = try target(latestSong, selection.sourceKey)
            try admitSource(latestSong, selection: resolvedSelection)
            try validateDelivery(valid, song: latestSong, playback: latest.playback, automaticQuality: false)
            let candidate = MusicPlaybackCandidate(attemptId: attemptId, occurrenceId: occurrenceId, resourceId: "music:" + attemptId,
                url: valid.url, headers: valid.headers, title: latestSong.title, startPosition: start,
                requestedQuality: selection.quality, resolvedSelection: resolvedSelection,
                expiresAt: valid.expiresAt.map { $0.timeIntervalSince1970 * 1000 },
                delivery: valid.delivery, playbackAuthorization: authorization, byteSource: audioCacheAttempt?.source)
            _ = try candidate.resource()
            try pinPlaybackRecovery(candidate: candidate, song: latestSong, state: latest)
            let publish = {
                self.attempt?.authorization = authorization
                self.attempt?.ready = true
            }
            try authorization.perform(publish)
            return candidate
        } catch {
            if attempt?.id == attemptId { retireAudioCacheAttempt(); attempt = nil }
            throw MusicServiceError.sanitized(error)
        }
    }

    public func cancel(attemptId: String?) async throws {
        try check()
        if attemptId == nil || playbackFeedbackAdmission?.retiringReason == nil ||
            playbackFeedbackAdmission?.attemptID != attemptId {
            playbackFeedback.finishCurrent(attemptID: attemptId, reason: .interrupt)
            playbackFeedback.invalidate(attemptID: attemptId, preservingEnds: true)
        }
        if let attemptId { discardSource(attemptID: attemptId) } else { clearPreparedSources() }
        if let recovery = cachedPlaybackRecovery,
           attemptId == nil || attemptId == recovery.previousAttemptID || attemptId == attempt?.id {
            let owned = attempt?.cacheRecoveryID == recovery.id
            retireCachedPlaybackRecovery()
            if owned, let id = attempt?.id { cancelCandidateAttempt(id) }
        }
        if attemptId == nil || attempt?.id == attemptId {
            retireAudioCacheAttempt()
            attempt = nil; cancelWork("playback")
        }
    }

    public func record(_ fact: MusicPlaybackFact) async throws -> Bool {
        try check()
        try musicRequire(fact.position.isFinite && (0...604_800).contains(fact.position) &&
            (fact.duration == nil || fact.duration!.isFinite && (0...604_800).contains(fact.duration!)))
        if ![.failed, .stopped].contains(fact.kind) {
            let state = try read()
            guard let entry = state.queue.entries.first(where: { $0.id == fact.occurrenceId })
                else { throw MusicServiceError.staleResult }
            let song = try entrySong(entry, state)
            try musicRequire(MusicMediaDuration.matches(expected: song.effectiveDuration, actual: fact.duration), .invalidMediaCandidate)
        }
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
                let formatter = DateFormatter(); formatter.calendar = Calendar(identifier: .gregorian)
                formatter.locale = Locale(identifier: "en_US_POSIX"); formatter.dateFormat = "yyyy-MM-dd"
                let date = formatter.string(from: now())
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
            let current = try await work(label: "lyrics") { try await scripts.source(installationID: live.installationID) }
            try sameSource(current, live); _ = try validate(current, site: source.platform, action: "lyric")
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
            let current = try await work(label: "lyrics") { try await scripts.source(installationID: snapshot.installationID) }
            try sameSource(current, execution.source)
            let lines = try MusicNativeLyricsParser.parse(execution.value, duration: duration)
            if !lines.isEmpty { return lines }
        }
        return []
    }
}
