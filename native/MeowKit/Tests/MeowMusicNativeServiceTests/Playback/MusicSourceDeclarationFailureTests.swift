import MeowRuntime
import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

private let declarationIdentity = MusicScriptIdentity(version: "1",
    contentHash: String(repeating: "a", count: 64), revisionHash: String(repeating: "a", count: 64))

private actor DeclarationFailureScripts: MusicScriptResolving {
    var failure: MusicServiceError?
    var requests: [String] = []
    var lookups: [String] = []
    func fail(_ value: MusicServiceError?) { failure = value; requests = []; lookups = [] }
    private func snapshot(_ id: String) -> MusicScriptSource {
        .init(installationID: id, generation: 1, enabled: true, state: .ready, version: "1",
            contentHash: String(repeating: "a", count: 64), revisionHash: String(repeating: "a", count: 64),
            capabilities: [.init(siteID: "tx", title: id, actions: ["musicUrl"], qualities: ["flac", "128k"])])
    }
    func sources() throws -> [MusicScriptSource] {
        if let failure { throw failure }
        return [snapshot("a"), snapshot("b")]
    }
    func source(installationID: String) throws -> MusicScriptSource {
        lookups.append(installationID)
        if installationID == "a", let failure { throw failure }
        return snapshot(installationID)
    }
    func resolve(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeAudio> {
        requests.append(request.installationID + ":" + request.quality)
        return .init(value: .init(url: URL(string: "https://media.invalid/" + request.installationID + "." + request.quality)!,
            delivery: .init(quality: request.quality, evidence: .officialResponse, duration: 180)), source: request.source)
    }
    func lyrics(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeLyrics> { .init(value: .init(text: ""), source: request.source) }
    func calls() -> (requests: [String], lookups: [String]) { (requests, lookups) }
}

final class MusicSourceDeclarationFailureTests: XCTestCase {
    private let local: [MusicServiceError] = [.timeout, .upstreamFailed, .connectionFailed, .networkOffline,
        .rateLimited, .busy, .requestRejected, .scriptFailed, .scriptRuntimeUnavailable]
    private let global: [MusicServiceError] = [.authRequired, .sessionExpired, .verificationRequired,
        .permissionDenied, .invalidResponse, .invalidRequest, .storageFailed, .corruptDocument,
        .storageConflict, .targetChanged, .staleRevision, .staleAccess, .accessUnavailable, .closed, .cancelled]

    private func acquiredRecords(_ song: MJ) -> [String: MusicSourceDiscoveryEntry] {
        Dictionary(grouping: song["sources"].array, by: { $0["source"].string }).mapValues { rows in
            .init(checkedAt: 0, sources: rows.map {
                .init(sourceKey: $0["sourceKey"].string, source: $0["source"].string, musicInfo: $0["musicInfo"].json)
            }, observations: [])
        }
    }

    func testCachedDeclarationLocalFailuresKeepAutomaticAndQualityAtSameTier() async throws {
        for failure in local {
            for automatic in [false, true] {
                try await cached(failure, intent: automatic ? .automatic : .quality("flac"), succeeds: true)
            }
        }
    }
    func testCachedDeclarationGlobalFailuresStopBeforeHealthyPeer() async throws {
        for failure in global { try await cached(failure, intent: .quality("flac"), succeeds: false) }
    }
    func testCachedExactDeclarationFailureNeverSubstitutesPeer() async throws {
        for failure in local { try await cached(failure, intent: nil, succeeds: false) }
    }
    private func cached(_ failure: MusicServiceError, intent: MusicSourceIntent?, succeeds: Bool) async throws {
        let scripts = DeclarationFailureScripts(), song = try MusicNativeCandidateDiscoveryFixture.song()
        let librarySong = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "321"))
        let acquired = try MusicModel.mergeSources(song, adding: librarySong["sources"].array)
        let key = song["sources"].array[0]["sourceKey"].string
        let qq = MusicNativeCandidateDiscoveryQQ(), http = MusicNativeCandidateDiscoveryHTTP()
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
        var document = try MusicDocument(store.read(), runID: "declaration")
        document.library["library"]["songs"] = .obj([(librarySong["songId"].string, librarySong)])
        var favorites = document.library["library"]["collections"].array[0]
        favorites["order"] = .strings([librarySong["songId"].string])
        document.library["library"]["collections"] = .array([favorites])
        store.replace(try document.encode(changed: ["library"]))
        let service = try await NativeMusicFixture.service(store,
            scripts: scripts, qq: qq, http: http, sourceDiscovery: cache)
        // Acquired library and queue metadata share facts without inventing a query.
        _ = try await service.queue()
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        func selection(_ id: String) -> MusicPlaybackSelection {
            .init(installationId: id, generation: 1, siteId: "tx", sourceKey: key, quality: "flac",
                  scriptIdentity: declarationIdentity)
        }
        for id in ["a", "b"] {
            let prepared = try await session.match(intent: .exact(selection(id)))
            await service.discardSource(attemptID: prepared.candidate.attemptId)
        }
        let recordsBefore = await cache.entries(fingerprint: MusicModel.fingerprint(song), duration: 180)
        XCTAssertEqual(recordsBefore, acquiredRecords(acquired))
        let stateBefore = try await service.state()
        await scripts.fail(failure)
        do {
            let result = try await session.match(intent: intent ?? .exact(selection("a")))
            XCTAssertTrue(succeeds)
            XCTAssertEqual(result.selection.installationId, "b")
            XCTAssertEqual(result.selection.quality, "flac")
            await service.discardSource(attemptID: result.candidate.attemptId)
        } catch {
            XCTAssertFalse(succeeds)
            XCTAssertEqual(MusicServiceError.sanitized(error), failure)
        }
        let calls = await scripts.calls()
        XCTAssertTrue(calls.requests.isEmpty, "A cached peer must need no new URL request")
        XCTAssertFalse(calls.lookups.contains("b") && !succeeds, "Global/exact failures cannot consume the peer")
        let searches = await qq.calls(), otherSearches = await http.calls()
        let exclusions = try await service.sourceExclusions(), history = try await service.playedSongs()
        let records = await cache.entries(fingerprint: MusicModel.fingerprint(song), duration: 180)
        let state = try await service.state()
        XCTAssertTrue(searches.isEmpty); XCTAssertTrue(otherSearches.isEmpty)
        XCTAssertTrue(exclusions.isEmpty); XCTAssertTrue(history.isEmpty)
        XCTAssertEqual(records, recordsBefore, "Declaration failure cannot erase or rewrite acquired recording facts")
        XCTAssertEqual(state, stateBefore)
        if succeeds {
            await scripts.fail(nil)
            let retried = try await session.match(intent: .exact(selection("a")))
            XCTAssertEqual(retried.selection.installationId, "a", "A declaration outage must not blacklist its URL")
            let retryCalls = await scripts.calls()
            XCTAssertTrue(retryCalls.requests.isEmpty)
            await service.discardSource(attemptID: retried.candidate.attemptId)
        }
        await service.close()
    }

    func testListingLocalFailuresKeepHealthyOfficialSameTier() async throws {
        for failure in local { try await listing(failure, succeeds: true) }
    }
    func testListingGlobalFailuresCannotResolveOfficialPeer() async throws {
        for failure in global { try await listing(failure, succeeds: false) }
    }
    private func listing(_ failure: MusicServiceError, succeeds: Bool) async throws {
        let scripts = DeclarationFailureScripts(), clock = MusicRecoveryTestClock()
        await scripts.fail(failure)
        let accounts = MusicRecoveryTestAccounts(platform: .qq, nativeID: "101", clock: clock)
        let song = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("tx", id: "101"))
        let qq = MusicNativeCandidateDiscoveryQQ(), http = MusicNativeCandidateDiscoveryHTTP()
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { clock.now() })
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: song),
            scripts: scripts, accounts: accounts, qq: qq, http: http, sourceDiscovery: cache, clock: { clock.now() })
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let recordsBefore = await cache.entries(fingerprint: MusicModel.fingerprint(song), duration: 180)
        XCTAssertEqual(recordsBefore, [:])
        let stateBefore = try await service.state()
        do {
            let result = try await session.match(intent: .quality("flac"))
            XCTAssertTrue(succeeds)
            XCTAssertTrue(result.selection.installationId.hasPrefix("official:"))
            XCTAssertEqual(result.selection.quality, "flac")
            await service.discardSource(attemptID: result.candidate.attemptId)
        } catch {
            XCTAssertFalse(succeeds)
            XCTAssertEqual(MusicServiceError.sanitized(error), failure)
        }
        let counts = await accounts.counts(), calls = await scripts.calls()
        XCTAssertEqual(counts.resolves, succeeds ? 1 : 0)
        XCTAssertTrue(calls.requests.isEmpty)
        let searches = await qq.calls(), otherSearches = await http.calls()
        let exclusions = try await service.sourceExclusions(), history = try await service.playedSongs()
        let records = await cache.entries(fingerprint: MusicModel.fingerprint(song), duration: 180)
        let state = try await service.state()
        XCTAssertTrue(searches.isEmpty); XCTAssertTrue(otherSearches.isEmpty)
        XCTAssertTrue(exclusions.isEmpty); XCTAssertTrue(history.isEmpty)
        XCTAssertEqual(records, acquiredRecords(song), "Listing imports the known seed without manufacturing a search")
        XCTAssertEqual(state, stateBefore)
        await service.close()
    }
}

private actor ResolutionDeclarationScripts: MusicScriptResolving {
    let declarationCall: Int?
    let failure: MusicServiceError
    let audioFailure: Bool
    private var lookups = 0, injected = 0
    private var requests: [MusicScriptRequest] = []
    init(declarationCall: Int?, failure: MusicServiceError, audioFailure: Bool) {
        self.declarationCall = declarationCall; self.failure = failure; self.audioFailure = audioFailure
    }
    private func snapshot() -> MusicScriptSource {
        .init(installationID: "a", generation: 1, enabled: true, state: .ready, version: "1",
            contentHash: String(repeating: "a", count: 64), revisionHash: String(repeating: "a", count: 64),
            capabilities: [.init(siteID: "tx", title: "a", actions: ["musicUrl"], qualities: ["flac"])])
    }
    func sources() -> [MusicScriptSource] { [snapshot()] }
    func source(installationID: String) throws -> MusicScriptSource {
        lookups += 1
        if lookups == declarationCall { injected += 1; throw failure }
        return snapshot()
    }
    func resolve(request: MusicScriptRequest) throws -> MusicScriptResult<MusicNativeAudio> {
        requests.append(request)
        if audioFailure && requests.count == 1 { injected += 1; throw failure }
        return .init(value: .init(url: URL(string: "https://media.invalid/recording/\(requests.count)")!,
                     delivery: .init(quality: request.quality, evidence: .officialResponse, duration: 180)), source: request.source)
    }
    func lyrics(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeLyrics> { .init(value: .init(text: ""), source: request.source) }
    func calls() -> (lookups: Int, injected: Int, requests: [MusicScriptRequest]) { (lookups, injected, requests) }
}

extension MusicSourceDeclarationFailureTests {
    private enum ResolutionEntry: CaseIterable { case automatic, quality, exact, legacyAutomatic, legacyManual }
    private enum FailureStage { case before, after, audio }

    func testNewResolutionDeclarationPermissionStopsBeforeAndAfterAudio() async throws {
        for entry in [ResolutionEntry.automatic, .quality, .exact] {
            for stage in [FailureStage.before, .after] {
                try await resolutionDeclaration(entry, stage: stage, failure: .permissionDenied)
            }
        }
    }
    func testLegacyConsumerDeclarationPermissionStopsBeforeAndAfterAudio() async throws {
        for entry in [ResolutionEntry.legacyAutomatic, .legacyManual] {
            for stage in [FailureStage.before, .after] {
                try await resolutionDeclaration(entry, stage: stage, failure: .permissionDenied)
            }
        }
    }
    func testAudioPermissionStillAllowsSameTierPeerExceptExact() async throws {
        for entry in ResolutionEntry.allCases {
            try await resolutionDeclaration(entry, stage: .audio, failure: .permissionDenied)
        }
    }
    func testResolutionDeclarationTimeoutStillAllowsSameTierPeerExceptExact() async throws {
        for entry in ResolutionEntry.allCases {
            for stage in [FailureStage.before, .after] {
                try await resolutionDeclaration(entry, stage: stage, failure: .timeout)
            }
        }
    }

    private func resolutionDeclaration(_ entry: ResolutionEntry, stage: FailureStage,
                                       failure: MusicServiceError) async throws {
        let primary = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(id: "first"))
        let peer = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(id: "second"))
        let song = try MusicModel.mergeSources(primary, adding: peer["sources"].array)
        let key = primary["sources"].array[0]["sourceKey"].string
        // Exact resolves its own declaration once to construct the option, then
        // enters the same before/after audio pin checks as automatic selection.
        let offset = entry == .exact ? 1 : 0
        let call: Int? = stage == .audio ? nil : offset + (stage == .before ? 1 : 2)
        let scripts = ResolutionDeclarationScripts(declarationCall: call, failure: failure, audioFailure: stage == .audio)
        let qq = MusicNativeCandidateDiscoveryQQ(), http = MusicNativeCandidateDiscoveryHTTP()
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: song),
            scripts: scripts, qq: qq, http: http, sourceDiscovery: cache)
        _ = try await service.queue()
        let recordsBefore = await cache.entries(fingerprint: MusicModel.fingerprint(song), duration: 180)
        XCTAssertEqual(recordsBefore, acquiredRecords(song))
        let stateBefore = try await service.state()
        let exact = entry == .exact || entry == .legacyManual
        let succeeds = !exact && (stage == .audio || failure == .timeout)
        do {
            let selected: MusicPlaybackSelection
            switch entry {
            case .automatic, .quality, .exact:
                let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
                let intent: MusicSourceIntent = entry == .automatic ? .automatic : entry == .quality ? .quality("flac") :
                    .exact(.init(installationId: "a", generation: 1, siteId: "tx", sourceKey: key, quality: "flac",
                                 scriptIdentity: declarationIdentity))
                let result = try await session.match(intent: intent)
                selected = result.selection
                await service.discardSource(attemptID: result.candidate.attemptId)
            case .legacyAutomatic, .legacyManual:
                let consumer = MusicCandidateDiscoveryConsumer(provider: service)
                let mode: MusicCandidateDiscoveryMode
                if entry == .legacyManual {
                    let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
                    let snapshot = try await session.snapshot(discover: false, force: false)
                    mode = .manual(try XCTUnwrap(snapshot.options.first).target(quality: "flac"))
                    await session.close(retainingAttemptID: nil)
                } else { mode = .automatic() }
                let result = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1, mode: mode)
                selected = result.target.selection
                try await service.cancel(attemptId: result.candidate.attemptId)
            }
            XCTAssertTrue(succeeds, "Declaration permission must abort, not consume the next recording")
            XCTAssertEqual(selected.quality, "flac")
        } catch {
            XCTAssertFalse(succeeds)
            XCTAssertEqual(error as? MusicServiceError, failure, "Consumers must expose the original code, not a stage wrapper")
        }
        let calls = await scripts.calls()
        XCTAssertEqual(calls.injected, 1, "The fault must reach the requested pre/post/audio boundary")
        if succeeds {
            XCTAssertEqual(calls.requests.count, stage == .before ? 1 : 2)
            XCTAssertEqual(Set(calls.requests.map(\.sourceKey)).count, calls.requests.count)
        } else {
            XCTAssertEqual(calls.requests.count, stage == .before ? 0 : 1)
            XCTAssertEqual(calls.lookups, stage == .audio ? offset + 1 : call!)
        }
        XCTAssertTrue(calls.requests.allSatisfy { $0.quality == "flac" })
        let searches = await qq.calls(), otherSearches = await http.calls()
        let exclusions = try await service.sourceExclusions(), history = try await service.playedSongs()
        let records = await cache.entries(fingerprint: MusicModel.fingerprint(song), duration: 180)
        let rejected = try await service.recordingSourceExclusions(song)
        let state = try await service.state()
        XCTAssertTrue(searches.isEmpty); XCTAssertTrue(otherSearches.isEmpty)
        XCTAssertTrue(exclusions.isEmpty); XCTAssertTrue(rejected.isEmpty)
        XCTAssertTrue(history.isEmpty)
        XCTAssertEqual(records, recordsBefore, "Declaration and audio failures cannot rewrite acquired recording facts")
        XCTAssertEqual(state, stateBefore)
        await service.close()
    }
}

// Exercise the actual HTTP adapter, including request's internal declaration
// reads. This peer does not implement MusicScriptResolving.
private actor InternalDeclarationRuntime {
    static let id = "11111111-1111-4111-8111-111111111111"
    static let identity = MusicScriptIdentity(version: "1", contentHash: String(repeating: "a", count: 64),
        revisionHash: String(repeating: "b", count: 64))
    private var detailFailureAt: Int?
    private var audioFailure = false
    private var failureCode = "PERMISSION_DENIED"
    private var details = 0, injected = 0
    private var resolutions: [RuntimeValue] = []
    func arm(detail: Int?, audio: Bool, code: String) {
        detailFailureAt = detail; audioFailure = audio; failureCode = code
        details = 0; injected = 0; resolutions = []
    }
    func metrics() -> (details: Int, injected: Int, resolutions: [RuntimeValue]) {
        (details, injected, resolutions)
    }
    private func failed() -> RuntimeValue {
        injected += 1
        return MusicScriptHTTPTestRuntime.failure(failureCode)
    }
    private func item() -> RuntimeValue {
        .object([
            "installationId": .string(Self.id), "name": .string("Declaration fixture"), "version": .string("1"),
            "origin": .object(["kind": .string("file")]), "enabled": .bool(true),
            "state": .string("ready"), "generation": .number(1),
            "contentHash": .string(String(repeating: "a", count: 64)),
            "executionHash": .string(String(repeating: "b", count: 64)),
            "sites": .object(["tx": .object([
                "actions": .array([.string("musicUrl")]), "qualitys": .array([.string("flac")]),
            ])]),
        ])
    }
    func respond(_ method: String, _ parameters: RuntimeValue) -> RuntimeValue {
        switch method {
        case "sources.list": return MusicScriptHTTPTestRuntime.success(.object(["items": .array([item()])]))
        case "sources.detail":
            details += 1
            if details == detailFailureAt { return failed() }
            return MusicScriptHTTPTestRuntime.success(.object(["item": item()]))
        case "resolve":
            resolutions.append(parameters)
            if audioFailure && resolutions.count == 1 { return failed() }
            return MusicScriptHTTPTestRuntime.success(.object([
                "installationId": .string(Self.id), "generation": .number(1),
                "contentHash": .string(Self.identity.contentHash),
                "platform": parameters.scriptField("platform")!, "quality": parameters.scriptField("quality")!,
                "url": .string("https://media.invalid/recording/\(resolutions.count).flac"), "headers": .object([:]),
            ]))
        default: preconditionFailure("Unexpected fixture method")
        }
    }
}

extension MusicSourceDeclarationFailureTests {
    func testRealAdapterInternalDetailPermissionStopsEveryPlaybackEntry() async throws {
        for entry in ResolutionEntry.allCases {
            for afterAudio in [false, true] {
                try await internalDeclaration(entry, audioFailure: false, afterAudio: afterAudio)
            }
        }
    }
    func testRealAdapterResolvePermissionKeepsContentSemanticsAtEveryPlaybackEntry() async throws {
        for entry in ResolutionEntry.allCases { try await internalDeclaration(entry, audioFailure: true) }
    }

    func testRealAdapterCancellationStorageAndTargetErrorsStopEveryPlaybackEntry() async throws {
        let failures: [(String, MusicServiceError)] = [
            ("CANCELLED", .cancelled), ("STORAGE_ERROR", .storageFailed),
            ("CORRUPT_STORAGE", .corruptDocument), ("TARGET_CHANGED", .targetChanged),
        ]
        for entry in ResolutionEntry.allCases {
            for (code, failure) in failures {
                for stage in [FailureStage.before, .after, .audio] {
                    try await internalDeclaration(entry, audioFailure: stage == .audio,
                        afterAudio: stage == .after, code: code, failure: failure)
                }
            }
        }
    }

    private func internalDeclaration(_ entry: ResolutionEntry, audioFailure: Bool, afterAudio: Bool = false,
        code: String = "PERMISSION_DENIED", failure: MusicServiceError = .permissionDenied) async throws {
        let primary = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(id: "first"))
        let peer = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(id: "second"))
        let song = try MusicModel.mergeSources(primary, adding: peer["sources"].array)
        let key = primary["sources"].array[0]["sourceKey"].string
        let runtime = InternalDeclarationRuntime()
        let runtimeSession = RuntimeSession(launcher: MusicScriptHTTPTestRuntime { await runtime.respond($0, $1) })
        let scripts = NativeMusicScriptService(connection: { try await runtimeSession.connection() })
        await scripts.start()
        let qq = MusicNativeCandidateDiscoveryQQ(), http = MusicNativeCandidateDiscoveryHTTP()
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: song),
            scripts: scripts, qq: qq, http: http, sourceDiscovery: cache)
        _ = try await service.queue()
        let recordsBefore = await cache.entries(fingerprint: MusicModel.fingerprint(song), duration: 180)
        XCTAssertEqual(recordsBefore, acquiredRecords(song))
        let stateBefore = try await service.state()
        var manual: MusicCandidateDiscoveryMode?
        if entry == .legacyManual {
            let discovery = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let snapshot = try await discovery.snapshot(discover: false, force: false)
            manual = .manual(try XCTUnwrap(snapshot.options.first).target(quality: "flac"))
            await discovery.close(retainingAttemptID: nil)
        }
        // Complete directory reads no longer perform per-installation detail.
        // Only exact constructs its target with an extra detail after arming.
        let internalDetail = (entry == .exact ? 3 : 2) + (afterAudio ? 1 : 0)
        await runtime.arm(detail: audioFailure ? nil : internalDetail, audio: audioFailure, code: code)
        let succeeds = audioFailure && failure == .permissionDenied && entry != .exact && entry != .legacyManual
        do {
            let selection: MusicPlaybackSelection
            switch entry {
            case .automatic, .quality, .exact:
                let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
                let intent: MusicSourceIntent = entry == .automatic ? .automatic : entry == .quality ? .quality("flac") :
                    .exact(.init(installationId: InternalDeclarationRuntime.id, generation: 1,
                                 siteId: "tx", sourceKey: key, quality: "flac",
                                 scriptIdentity: InternalDeclarationRuntime.identity))
                let result = try await session.match(intent: intent)
                selection = result.selection
                await service.discardSource(attemptID: result.candidate.attemptId)
            case .legacyAutomatic, .legacyManual:
                let consumer = MusicCandidateDiscoveryConsumer(provider: service)
                let result = try await consumer.resolve(occurrenceID: "entry-1", expectedQueueRevision: 1,
                                                        mode: manual ?? .automatic())
                selection = result.target.selection
                try await service.cancel(attemptId: result.candidate.attemptId)
            }
            XCTAssertTrue(succeeds, "Global/declaration failures must abort: \(entry) \(code)")
            XCTAssertEqual(selection.quality, "flac")
            XCTAssertEqual(selection.installationId, InternalDeclarationRuntime.id)
        } catch {
            XCTAssertFalse(succeeds, "Actual resolve permission must allow the healthy same-tier recording: \(entry)")
            if entry == .legacyAutomatic && failure != .permissionDenied {
                let wrapped = try XCTUnwrap(error as? MusicCandidateDiscoveryFailure)
                XCTAssertEqual(wrapped.failure, failure)
                XCTAssertEqual(wrapped.attempts, 1)
                XCTAssertEqual(wrapped.issues.last,
                    MusicCandidateDiscoveryIssue(stage: .resolution, platform: "tx", failure: failure))
            } else {
                XCTAssertEqual(error as? MusicServiceError, failure, "Expose the original code, not the stage")
            }
        }
        let calls = await runtime.metrics()
        XCTAssertEqual(calls.injected, 1)
        XCTAssertEqual(calls.resolutions.count, audioFailure ? (succeeds ? 2 : 1) : (afterAudio ? 1 : 0))
        if !audioFailure { XCTAssertEqual(calls.details, internalDetail, "No next recording after declaration denial") }
        XCTAssertTrue(calls.resolutions.allSatisfy { $0.scriptField("quality") == .string("flac") })
        if succeeds && calls.resolutions.count == 2 {
            XCTAssertNotEqual(calls.resolutions[0].scriptField("musicInfo"), calls.resolutions[1].scriptField("musicInfo"),
                              "Content denial must continue to a different recording")
        }
        let searches = await qq.calls(), otherSearches = await http.calls()
        let exclusions = try await service.sourceExclusions(), history = try await service.playedSongs()
        let records = await cache.entries(fingerprint: MusicModel.fingerprint(song), duration: 180)
        let rejected = try await service.recordingSourceExclusions(song)
        let state = try await service.state()
        XCTAssertTrue(searches.isEmpty); XCTAssertTrue(otherSearches.isEmpty)
        XCTAssertTrue(exclusions.isEmpty); XCTAssertTrue(rejected.isEmpty)
        XCTAssertTrue(history.isEmpty)
        XCTAssertEqual(records, recordsBefore,
                       "Internal declaration failures cannot create search evidence or unrelated recording facts")
        XCTAssertEqual(state, stateBefore)
        await service.close(); try await scripts.close(); await runtimeSession.close()
    }
}
