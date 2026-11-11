import Foundation
import MeowMusicFeature

extension NativeMusicService {
    // Official account quality names are not LX capabilities. Keep their
    // grouping local to playback without rewriting the durable library schema.
    static func officialQualities(_ values: [String], _ prefs: MJ) -> [String] {
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
    func target(_ song: MJ, _ key: String) throws -> (MusicNativeSource, MJ) {
        guard let row = song["sources"].array.first(where: {
                  $0["sourceKey"].string.utf8.elementsEqual(key.utf8)
              }),
              let source = try MusicModel.sources(song).first(where: {
                  $0.sourceKey.utf8.elementsEqual(key.utf8)
              }) else {
            throw MusicServiceError.targetChanged
        }
        return (source, try MusicModel.locator(MusicModel.sourceData(row)))
    }
    func validate(_ source: MusicScriptSource, site: String, quality: String? = nil, action: String = "musicUrl") throws -> MusicScriptCapability {
        try musicRequire(source.isCandidate && source.generation >= 0 &&
            (source.state == .ready ? source.generation > 0 : source.generation == 0) &&
            source.generation <= 9_007_199_254_740_991 && source.capabilities.count <= 5, .sourceUnavailable)
        _ = try musicText(source.installationID, maximum: 128); _ = try musicText(source.version, maximum: 128)
        try musicRequire(musicHex(source.contentHash) && musicHex(source.revisionHash), .invalidResponse)
        try musicRequire(Set(source.capabilities.map(\.siteID)).count == source.capabilities.count, .invalidResponse)
        guard let cap = source.capabilities.first(where: { $0.siteID == site }), cap.actions.contains(action),
              quality == nil || cap.qualities.contains(quality!) else { throw MusicServiceError.sourceUnavailable }
        try musicRequire(cap.qualities.count <= 32 && Set(cap.qualities).count == cap.qualities.count, .invalidResponse)
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
                // A disabled built-in source never becomes an official candidate.
                guard let platform = MusicAccountPlatform(rawValue: row.platform),
                      Self.officialEnabled(row.platform, d.playback) else { continue }
                do {
                    let values = try await work { try await accounts.nativeOptions(platform: platform, nativeID: row.nativeId, sourceKey: row.sourceKey) }
                    try musicRequire(values.count <= 1, .invalidResponse)
                    for value in values {
                        try musicRequire(value.installationId.hasPrefix("official:") && value.siteId == row.platform && value.sourceKey == row.sourceKey &&
                            value.generation > 0 && value.generation <= 9_007_199_254_740_991 && musicHex(value.contentHash) &&
                            value.qualities.count <= 10 && Set(value.qualities).count == value.qualities.count &&
                            value.qualities.allSatisfy { ["128k", "192k", "320k", "flac", "flac24bit", "master",
                                                         "jyeffect", "dolby", "sky", "vivid"].contains($0) }, .invalidResponse)
                        let qualities = value.qualities
                        if !qualities.isEmpty { result.append(.init(installationId: value.installationId, generation: value.generation,
                            siteId: value.siteId, sourceKey: row.sourceKey, qualities: qualities, title: value.title,
                            version: value.version, contentHash: value.contentHash)) }
                    }
                } catch {
                    try check(own)
                    let failure = MusicServiceError.sanitized(error)
                    // A signed-out account is not an alternative-source failure.
                    // Only explicit selection performs media resolution.
                    guard failure.isAuthenticationFailure || failure == .unavailable ||
                        MusicCandidateDiscoveryFailures.mayTryAnother(failure) ||
                        MusicCandidateDiscoveryFailures.isContentFailure(failure) else { throw failure }
                }
            }
        }
        try check(own)
        let current = try read()
        _ = try self.song(ref, current)
        try musicRequire(current.playback["revision"] == d.playback["revision"], .staleRevision)
        result = try result.filter { try !sourceExcluded(song, provider: $0.installationId, platform: $0.siteId) }
        return .init(options: result, scriptState: scriptState)
    }
    func media(_ value: MusicNativeAudio) throws -> MusicNativeAudio {
        try value.playbackAuthorization?.check()
        let url = value.url
        try musicRequire(["https", "http"].contains(url.scheme?.lowercased() ?? "") &&
            url.host?.isEmpty == false && url.user == nil && url.password == nil && url.fragment == nil &&
            url.absoluteString.utf8.count <= 8192, .invalidMediaCandidate)
        var headers: [String: String] = [:]
        for (key, value) in value.headers {
            let name = key.lowercased()
            try musicRequire(["referer", "user-agent"].contains(name) && headers[name] == nil && value.utf8.count <= 2048 &&
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
        let d = try read()
        try musicRequire(d.queue["currentId"].string == occurrenceId, .targetChanged)
        let song = try self.song(.init(kind: .queue, id: occurrenceId), d), (source, info) = try target(song, selection.sourceKey)
        try admitSource(song, selection: selection)
        try musicRequire(source.platform == selection.siteId, .targetChanged)
        try requirePlatform(source.platform)
        try musicRequire(!selection.installationId.hasPrefix("official:") ||
            Self.officialEnabled(source.platform, d.playback), .sourceUnavailable)
        retireCachedPlaybackRecovery()
        retireAudioCacheAttempt()
        cancelWork("playback"); spent.insert(attemptId)
        attempt = Attempt(id: attemptId, occurrence: occurrenceId, sourceKey: selection.sourceKey, selection: selection)
        let own = epoch, start = d.playback["position"]["seconds"].number
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
                let request = MusicScriptRequest(source: snapshot,
                    siteID: source.platform, sourceKey: source.sourceKey, quality: selection.quality, musicInfo: info.bytes)
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
            try musicRequire(attempt?.id == attemptId && current.queue["currentId"].string == occurrenceId, .staleResult)
            attempt?.selection = resolvedSelection
            _ = try target(self.song(.init(kind: .queue, id: occurrenceId), current), selection.sourceKey)
            try musicRequire(!selection.installationId.hasPrefix("official:") ||
                Self.officialEnabled(source.platform, current.playback), .sourceUnavailable)
            let valid = try media(audio)
            try validateDelivery(valid, song: song, playback: current.playback, automaticQuality: false)
            let candidate = MusicPlaybackCandidate(attemptId: attemptId, occurrenceId: occurrenceId, resourceId: "music:" + attemptId,
                url: valid.url, headers: valid.headers, title: song["title"].string, startPosition: start,
                requestedQuality: selection.quality, resolvedSelection: resolvedSelection,
                expiresAt: valid.expiresAt.map { $0.timeIntervalSince1970 * 1000 },
                delivery: valid.delivery, playbackAuthorization: valid.playbackAuthorization)
            _ = try candidate.resource()
            try await stageAudioCache(audio: valid, song: song, selection: resolvedSelection, attemptID: attemptId)
            try await admitRecordingSource(song, sourceKey: selection.sourceKey)
            try check(own)
            try musicRequire(attempt?.id == attemptId, .staleResult)
            let latest = try read()
            try musicRequire(latest.queue["currentId"].string == occurrenceId &&
                latest.queue["revision"] == d.queue["revision"] &&
                latest.playback["revision"] == d.playback["revision"], .staleRevision)
            try admitSource(song, selection: selection)
            try pinPlaybackRecovery(candidate: candidate, song: song, state: latest)
            let publish = {
                self.attempt?.authorization = valid.playbackAuthorization
                self.attempt?.ready = true
            }
            if let authorization = valid.playbackAuthorization { try authorization.perform(publish) }
            else { publish() }
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
            let song = try song(.init(kind: .queue, id: fact.occurrenceId), read())
            try musicRequire(MusicMediaDuration.matches(expected: song["duration"].number, actual: fact.duration), .invalidMediaCandidate)
        }
        try commitPreparedSource(fact)
        guard let current = attempt, current.ready, current.id == fact.attemptId, current.occurrence == fact.occurrenceId,
              fact.resourceId == "music:" + current.id else { throw MusicServiceError.staleResult }
        try current.authorization?.check()
        let fileAuthorization = audioCacheAttempt.flatMap {
            $0.attemptID == current.id && $0.audio == nil ? $0.authorization : nil
        }
        if current.ended { return false }
        let record = fact.kind == .playing && !current.started
        let areas: Set<String> = record ? ["history", "playback"] : fact.kind == .failed ? ["queue", "playback"] : ["playback"]
        let commit = { [self] in try transaction(areas) { d in
            try musicRequire(d.queue["currentId"].string == current.occurrence, .staleResult)
            guard let entry = d.queue["entries"].array.first(where: { $0["id"].string == current.occurrence }) else { throw MusicServiceError.staleResult }
            if record {
                let formatter = DateFormatter(); formatter.calendar = Calendar(identifier: .gregorian)
                formatter.locale = Locale(identifier: "en_US_POSIX"); formatter.dateFormat = "yyyy-MM-dd"
                let date = formatter.string(from: now()), item = MJ.obj([("song", entry["song"]), ("context", entry["context"]), ("at", .number(now().timeIntervalSince1970 * 1000))])
                var days = d.history["days"].array
                let index = days.firstIndex { $0["date"].string == date }
                var day = index.map { days[$0] } ?? .obj([("date", .string(date)), ("songs", .array([]))])
                var song = entry["song"], old: [MJ] = []
                let keys = Set(try MusicModel.sources(song).map(\.sourceKey))
                for previous in day["songs"].array {
                    let oldKeys = Set(try MusicModel.sources(previous["song"]).map(\.sourceKey))
                    if !keys.isDisjoint(with: oldKeys) || previous["song"]["songId"] == song["songId"] && MusicModel.compatible(previous["song"], song) {
                        var sources = song["sources"].array
                        for source in previous["song"]["sources"].array where !sources.contains(where: { $0["sourceKey"] == source["sourceKey"] }) { sources.append(source) }
                        song["sources"] = .array(sources)
                    } else { old.append(previous) }
                }
                var merged = item; merged["song"] = song; day["songs"] = .array([merged] + old)
                if let index { days[index] = day } else { days.append(day) }
                d.history["days"] = .array(Array(days.sorted { $0["date"].string > $1["date"].string }.prefix(7)))
            }
            d.playback["position"] = .obj([("occurrenceId", .string(current.occurrence)), ("seconds", .number(fact.kind == .ended ? 0 : fact.position))])
            if fact.kind == .failed {
                d.queue["failed"] = .strings(Array(Set(d.queue["failed"].strings + [current.occurrence])))
                d.queue["revision"] = .n(d.queue["revision"].int + 1)
            }
            return true
        } }
        let accepted: Bool
        if let authorization = current.authorization { accepted = try authorization.perform(commit) }
        else if let fileAuthorization { accepted = try fileAuthorization.perform(commit) }
        else { accepted = try commit() }
        // Publish flags only after a successful CAS.
        if record { attempt?.started = true }
        if [.ended, .failed, .stopped].contains(fact.kind) {
            attempt?.ended = true
            if playbackFeedbackAdmission?.attemptID == fact.attemptId { playbackFeedbackAdmission?.ended = true }
        }
        if accepted {
            if fact.kind == .playing { attempt?.isPlaying = true }
            else if [.paused, .ended, .failed, .stopped].contains(fact.kind) { attempt?.isPlaying = false }
            attempt?.latestFact = fact
            audioCacheAcceptedFact(fact)
        }
        return accepted
    }
    public func lyrics(occurrenceId: String, sourceKey: String) async throws -> MusicLyricsSnapshot {
        let d = try read()
        try musicRequire(d.queue["currentId"].string == occurrenceId, .targetChanged)
        let song = try self.song(.init(kind: .queue, id: occurrenceId), d), (source, info) = try target(song, sourceKey)
        if !d.playback["lyricsEnabled"].bool { return .init(occurrenceId: occurrenceId, state: .disabled, lines: []) }
        try requirePlatform(source.platform)
        let serial = UUID(); lyricEpoch = serial; cancelWork("lyrics")
        var lines: [MusicLyricLine]
        if ["wy", "tx", "kg", "kw", "mg"].contains(source.platform) {
            let port = publicPlatforms
            // Native lyrics come first and never start Node. Only an empty wy/kw/mg
            // result tries enabled same-platform scripts; tx/kg stay native-only.
            let payload = try await work(label: "lyrics") { try await port.lyrics(platform: source.platform, info: info) }
            lines = try MusicNativeLyricsParser.parse(payload, duration: song["duration"].number)
            if lines.isEmpty, ["wy", "kw", "mg"].contains(source.platform) {
                lines = try await scriptLyrics(platform: source.platform, sourceKey: sourceKey, info: info,
                                               duration: song["duration"].number)
            }
        } else {
            // Only non-public script platforms use their explicitly selected LX.
            // Public-platform failures never fall through to scripts or another song.
            guard let chosen = attempt, chosen.occurrence == occurrenceId, chosen.sourceKey == sourceKey,
                  !chosen.selection.installationId.hasPrefix("official:"), let scripts else { throw MusicServiceError.sourceRequired }
            let selection = chosen.selection
            let live = try await work(label: "lyrics") { try await scripts.source(installationID: selection.installationId) }
            _ = try validate(live, site: source.platform, action: "lyric")
            guard let executed = chosen.scriptSource else { throw MusicServiceError.sourceUnavailable }
            try sameSource(live, executed)
            let request = MusicScriptRequest(source: live, siteID: source.platform,
                sourceKey: sourceKey, quality: selection.quality, musicInfo: info.bytes)
            let execution = try await work(label: "lyrics") { try await scripts.lyrics(request: request) }
            try executedScript(execution.source, from: live)
            let current = try await work(label: "lyrics") { try await scripts.source(installationID: live.installationID) }
            try sameSource(current, live); _ = try validate(current, site: source.platform, action: "lyric")
            try musicRequire(attempt?.id == chosen.id, .staleResult)
            lines = try MusicNativeLyricsParser.parse(execution.value, duration: song["duration"].number)
        }
        let current = try read()
        try musicRequire(serial == lyricEpoch && current.queue["currentId"].string == occurrenceId &&
            current.playback["lyricsEnabled"].bool, .staleResult)
        return .init(occurrenceId: occurrenceId, state: lines.isEmpty ? .none : .ready, lines: lines)
    }

    /// Enabled scripts in install order. An empty result or an undeclared or
    /// unavailable action continues; any other failure keeps its own meaning.
    private func scriptLyrics(platform: String, sourceKey: String, info: MJ, duration: Double) async throws -> [MusicLyricLine] {
        guard let scripts else { return [] }
        let snapshots: [MusicScriptSource]
        do { snapshots = try await work(label: "lyrics") { try await scripts.sources() } }
        catch MusicServiceError.sourceUnavailable { return [] }
        for snapshot in snapshots where snapshot.isCandidate &&
            snapshot.capabilities.contains(where: { $0.siteID == platform && $0.actions.contains("lyric") }) {
            _ = try validate(snapshot, site: platform, action: "lyric")
            let request = MusicScriptRequest(source: snapshot,
                siteID: platform, sourceKey: sourceKey, quality: "", musicInfo: info.bytes)
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
