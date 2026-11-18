import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
@testable import MeowMusicNativeService

@MainActor final class MusicNativeSyncMergeTests: XCTestCase {
    private func mappingStore(_ songs: [MJ]) throws -> NativeMusicTestLibrary {
        var document = try MusicDocument(nil, runID: "mapping")
        document.library["library"]["songs"] = .obj(songs.map { ($0["songId"].string, $0) })
        var favorites = document.library["library"]["collections"].array[0]
        favorites["order"] = .strings(songs.map { $0["songId"].string })
        document.library["library"]["collections"] = .array([favorites])
        return NativeMusicTestLibrary(try document.encode(changed: ["library"]))
    }

    private func missingMappings(failure: MusicServiceError) async throws ->
        (service: NativeMusicService, http: MusicNativeDiscoveryPlatforms, search: MusicNativeCandidateDiscoveryQQ) {
        var document = try MusicDocument(nil, runID: "mapping")
        let first = try NativeMusicFixture.storedSong(MusicNativeCandidateDiscoveryFixture.track("kg", id: "A", title: "First recording")),
            second = try NativeMusicFixture.storedSong(MusicNativeCandidateDiscoveryFixture.track("kg", id: "B", title: "Second recording"))
        document.library["library"]["songs"] = .obj([
            (first["songId"].string, first), (second["songId"].string, second)
        ])
        var favorites = document.library["library"]["collections"].array[0]
        favorites["order"] = .strings([first["songId"].string, second["songId"].string])
        document.library["library"]["collections"] = .array([favorites])
        let store = NativeMusicTestLibrary(try document.encode(changed: ["library"]))
        let wy = NativeSyncPeer(rows: [NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites)]),
            qq = NativeSyncPeer(platform: .qq, rows: [NativeSyncFixture.collection("600", name: "Favorites", kind: .favorites)])
        let accounts = MultiPlatformSyncPeer([.netease: wy, .qq: qq])
        let http = MusicNativeDiscoveryPlatforms(failures: ["wy": failure])
        let search = MusicNativeCandidateDiscoveryQQ()
        let routes = NativeMusicFixture.routes(primary: search, others: http)
        let platform = try MusicPlatformFixture { request in
            do { return try await routes.respond(request) }
            catch let error as MusicServiceError where error == .connectionFailed {
                throw MusicPlatformFixture.Failure.remote("NETWORK_ERROR")
            }
        }
        let service = NativeMusicService(database: store,
            accounts: accounts,
            platformClient: platform.makeClient(),
            clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        await wy.clearCalls(); await qq.clearCalls()
        return (service, http, search)
    }

    func testPlatformRateLimitStopsOnlyThatPlatformsRemainingMissingMappings() async throws {
        let fixture = try await missingMappings(failure: .rateLimited)
        let request = MusicLibrarySyncRequest(selections: [
            .init(platform: .netease, favorites: true), .init(platform: .qq, favorites: true)
        ])
        let state = try await fixture.service.synchronizeLibrary(request)
        XCTAssertEqual(state.phase, .partial)
        let calls = await fixture.http.calls()
        XCTAssertEqual(calls.filter { $0 == "wy" }.count, 1)
        XCTAssertEqual(state.issues.filter { $0.platform == .netease && $0.collectionID == nil }.count, 1)
        let searched = await fixture.search.calls()
        XCTAssertEqual(searched.count, 2)
        _ = try await fixture.service.synchronizeLibrary(request)
        let retried = await fixture.http.calls(), cachedOtherPlatform = await fixture.search.calls()
        XCTAssertEqual(retried.filter { $0 == "wy" }.count, 2, "A failed platform must be retried in the next round")
        XCTAssertEqual(cachedOtherPlatform.count, 2, "Successful empty checks stay cached")
        await fixture.service.close()
    }

    func testOfflineConnectionAndTimeoutFailuresStopOnlyTheAffectedPlatform() async throws {
        for failure in [MusicServiceError.networkOffline, .connectionFailed, .timeout, .upstreamFailed] {
            let fixture = try await missingMappings(failure: failure)
            let state = try await fixture.service.synchronizeLibrary(.init(selections: [
                .init(platform: .netease, favorites: true), .init(platform: .qq, favorites: true)
            ]))
            let calls = await fixture.http.calls(), other = await fixture.search.calls()
            XCTAssertEqual(state.phase, .partial)
            XCTAssertEqual(calls.filter { $0 == "wy" }.count, 1, failure.rawValue)
            XCTAssertEqual(other.count, 2, failure.rawValue)
            XCTAssertEqual(state.issues.filter { $0.platform == .netease && $0.collectionID == nil }.count, 1)
            await fixture.service.close()
        }
    }

    func testMappingAuthenticationAndVerificationFailuresStillThrow() async throws {
        for failure in [MusicServiceError.authRequired, .sessionExpired, .verificationRequired] {
            let fixture = try await missingMappings(failure: failure)
            do {
                _ = try await fixture.service.synchronizeLibrary(.init(selections: [
                    .init(platform: .netease, favorites: true), .init(platform: .qq, favorites: true)
                ]))
                XCTFail("Authentication failure was converted into partial mapping")
            } catch { XCTAssertEqual(error as? MusicServiceError, failure) }
            let calls = await fixture.http.calls(), other = await fixture.search.calls()
            XCTAssertEqual(calls.filter { $0 == "wy" }.count, 1)
            XCTAssertTrue(other.isEmpty)
            await fixture.service.close()
        }
    }

    private func record(_ cache: any MusicSourceDiscoveryCaching, song: MJ, source: MJ? = nil) async {
        let at = NativeMusicFixture.date.timeIntervalSince1970 * 1000
        await cache.observe(songID: song["songId"].string,
            observation: .init(startedAt: at, checkedAt: at, platform: "wy",
                query: song["title"].string + " " + song["artist"].string, scope: .recording,
                outcome: source == nil ? .notFound : .found),
            sourceKeys: source.map { [$0["sourceKey"].string] } ?? [])
    }

    func testCachedMappingSourcesAreUpsertedAndPushedWithoutSearching() async throws {
        let song = try MusicNativeCandidateDiscoveryFixture.song()
        let source = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "321"))["sources"].array[0]
        let cache = MusicNativeMemorySourceDiscoveryCache()
        await record(cache, song: song, source: source)
        let store = try mappingStore([MusicModel.mergeSources(song, adding: [source])])
        let peer = NativeSyncPeer(rows: [NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites)])
        let http = MusicNativeDiscoveryPlatforms()
        let service = try await NativeMusicFixture.service(store, accounts: peer, platform: http, sourceDiscovery: cache)
        let state = try await service.synchronizeLibrary(NativeSyncFixture.request(favorites: true, playlists: false))
        XCTAssertEqual(state.phase, .synced)
        let calls = await http.calls(), writes = await peer.calls()
        XCTAssertTrue(calls.isEmpty)
        XCTAssertEqual(writes.map(\.action), [.add])
        XCTAssertEqual(writes.flatMap(\.members).map(\.nativeID), ["321"])
        let saved = try NativeSyncFixture.document(store).library["library"]["songs"][song["songId"].string]
        XCTAssertEqual(Set(try MusicModel.sources(saved).map(\.platform)), ["wy", "tx"])
        XCTAssertEqual(saved["songId"], song["songId"])
        await service.close()
    }

    func testPlatformFailureStillAllowsRemainingCompatibleCachedMappings() async throws {
        let tracks = [
            MusicNativeCandidateDiscoveryFixture.track(id: "FirstMID", title: "First recording"),
            MusicNativeCandidateDiscoveryFixture.track(id: "SecondMID", title: "Second recording")
        ]
        let first = try NativeMusicFixture.storedSong(tracks[0]), second = try NativeMusicFixture.storedSong(tracks[1])
        let cached = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "321", title: "Second recording"))
        let cache = MusicNativeMemorySourceDiscoveryCache()
        await record(cache, song: second, source: cached["sources"].array[0])
        let store = try mappingStore([first, MusicModel.mergeSources(second, adding: cached["sources"].array)])
        let peer = NativeSyncPeer(rows: [NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites)])
        let http = MusicNativeDiscoveryPlatforms(failures: ["wy": .rateLimited])
        let service = try await NativeMusicFixture.service(store, accounts: peer, platform: http, sourceDiscovery: cache)
        let state = try await service.synchronizeLibrary(NativeSyncFixture.request(favorites: true, playlists: false))
        XCTAssertEqual(state.phase, .partial)
        let calls = await http.calls(), writes = await peer.calls()
        XCTAssertEqual(calls, ["wy"])
        XCTAssertEqual(writes.flatMap(\.members).map(\.nativeID), ["321"])
        let saved = try NativeSyncFixture.document(store).library["library"]["songs"][second["songId"].string]
        XCTAssertEqual(Set(try MusicModel.sources(saved).map(\.platform)), ["wy", "tx"])
        await service.close()
    }

    func testConflictingCachedNativeIDDoesNotOverwriteAnotherSongOrBlockOtherMappings() async throws {
        let first = MusicNativeCandidateDiscoveryFixture.track()
        let other = MusicNativeCandidateDiscoveryFixture.track("wy", id: "321", title: "Other recording")
        let song = try NativeMusicFixture.storedSong(first), known = try NativeMusicFixture.storedSong(other)
        let cached = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "321"))
        let cache = MusicNativeMemorySourceDiscoveryCache()
        await record(cache, song: song, source: cached["sources"].array[0])
        let store = try mappingStore([song, known])
        let peer = NativeSyncPeer(rows: [NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites)])
        let http = MusicNativeDiscoveryPlatforms()
        let service = try await NativeMusicFixture.service(store, accounts: peer, platform: http, sourceDiscovery: cache)
        let registered = try await service.adoptSongs([try MusicModel.normalize(first), try MusicModel.normalize(other)], authoritative: true)
        XCTAssertEqual(registered.map { $0["songId"].string }, [song["songId"].string, known["songId"].string])
        let before = try NativeSyncFixture.document(store).library["library"]["songs"]
        let state = try await service.synchronizeLibrary(NativeSyncFixture.request(favorites: true, playlists: false))
        XCTAssertEqual(state.phase, .partial)
        XCTAssertTrue(state.issues.contains { $0.code == "unmapped" })
        let saved = try NativeSyncFixture.document(store).library["library"]["songs"]
        XCTAssertEqual(saved[song["songId"].string], before[song["songId"].string])
        XCTAssertEqual(saved[known["songId"].string], before[known["songId"].string])
        let calls = await http.calls(), writes = await peer.calls()
        XCTAssertTrue(calls.isEmpty)
        XCTAssertEqual(writes.flatMap(\.members).map(\.nativeID), ["321"])
        await service.close()
    }

    func testCachedEmptyMappingAvoidsNetworkAndKeepsUnmappedPartial() async throws {
        let song = try MusicNativeCandidateDiscoveryFixture.song()
        let cache = MusicNativeMemorySourceDiscoveryCache()
        await record(cache, song: song)
        let peer = NativeSyncPeer(rows: [NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites)])
        let http = MusicNativeDiscoveryPlatforms()
        let service = try await NativeMusicFixture.service(mappingStore([song]),
            accounts: peer,
            platform: http,
            sourceDiscovery: cache)
        let state = try await service.synchronizeLibrary(NativeSyncFixture.request(favorites: true, playlists: false))
        XCTAssertEqual(state.phase, .partial)
        XCTAssertTrue(state.issues.contains { $0.code == "unmapped" })
        let calls = await http.calls(), writes = await peer.calls()
        XCTAssertTrue(calls.isEmpty); XCTAssertTrue(writes.isEmpty)
        await service.close()
    }

    func testEmptyMappingCheckIsReusedForThirtyDays() async throws {
        let song = try MusicNativeCandidateDiscoveryFixture.song()
        let cache = MusicNativeMemorySourceDiscoveryCache()
        await record(cache, song: song)
        let day: TimeInterval = 24 * 60 * 60
        for (age, searches) in [(25 * day, 0), (31 * day, 1)] {
            let peer = NativeSyncPeer(rows: [NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites)])
            let http = MusicNativeDiscoveryPlatforms(empty: ["wy"]), at = NativeMusicFixture.date.addingTimeInterval(age)
            let service = try await NativeMusicFixture.service(mappingStore([song]),
            accounts: peer,
            platform: http,
            sourceDiscovery: cache,
            clock: { at })
            _ = try await service.synchronizeLibrary(NativeSyncFixture.request(favorites: true, playlists: false))
            let calls = await http.calls()
            XCTAssertEqual(calls.count, searches, "sync rechecks an empty platform only after 30 days")
            await service.close()
        }
    }

    func testExcludedSourceIsNeverMappedFromCacheOrSearch() async throws {
        let song = try MusicNativeCandidateDiscoveryFixture.song()
        let source = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "321"))["sources"].array[0]
        for cached in [true, false] {
            let cache = MusicNativeMemorySourceDiscoveryCache()
            if cached { await record(cache, song: song, source: source) }
            let store = try mappingStore([MusicModel.mergeSources(song, adding: cached ? [source] : [])])
            let peer = NativeSyncPeer(rows: [NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites)])
            let http = MusicNativeDiscoveryPlatforms()
            let service = try await NativeMusicFixture.service(store, accounts: peer, platform: http, sourceDiscovery: cache)
            try await service.excludeRecordingSource(song, sourceKey: source["sourceKey"].string)
            let state = try await service.synchronizeLibrary(NativeSyncFixture.request(favorites: true, playlists: false))
            XCTAssertEqual(state.phase, .partial)
            let saved = try NativeSyncFixture.document(store).library["library"]["songs"][song["songId"].string]
            XCTAssertEqual(try MusicModel.sources(saved).map(\.platform), ["tx", "wy"])
            let calls = await http.calls(), writes = await peer.calls()
            XCTAssertEqual(calls.count, cached ? 0 : 1); XCTAssertTrue(writes.isEmpty)
            await service.close()
        }
    }

    func testMappingCheckpointKeepsFactsAndCurrentDecisionsAfterCacheOrNetworkMapping() async throws {
        for cached in [true, false] {
            let primary = try MusicNativeCandidateDiscoveryFixture.song()
            let existing = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(id: "existing"))
            let song = try MusicModel.mergeSources(primary, adding: existing["sources"].array)
            let source = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "321"))["sources"].array[0]
            let cache = MusicNativeMemorySourceDiscoveryCache()
            if cached { await record(cache, song: song, source: source) }
            let stored = try MusicModel.mergeSources(song, adding: cached ? [source] : [])
            let store = try mappingStore([stored])
            var initial = try NativeSyncFixture.document(store)
            initial.library["library"]["songs"][song["songId"].string] = stored
            store.replace(try initial.encode(changed: ["library"]))
            let peer = NativeSyncPeer(rows: [NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites)])
            let http = MusicNativeDiscoveryPlatforms()
            let service = try await NativeMusicFixture.service(store, accounts: peer, platform: http, sourceDiscovery: cache)
            let document = try await service.read(), epoch = await service.epoch
            let request = NativeSyncFixture.request(favorites: true, playlists: false)
            var round = MusicNativeSyncRound(request: request, epoch: epoch, port: peer,
                library: document.library["library"], ledger: try MusicNativeSyncLedger.load(document.library),
                revision: document.library["revision"].int)
            round.sessions["wy"] = try await peer.beginLibrarySync(platform: .netease)
            try await service.syncMapMissing(&round)
            let id = song["songId"].string, key = source["sourceKey"].string
            let mapped = try await service.read().song(id)
            XCTAssertTrue(mapped["sources"].array.contains { $0["sourceKey"].string == key })
            let calls = await http.calls()
            XCTAssertEqual(calls, cached ? [] : ["wy"])

            // Mapping has completed, but its business transaction is still pending.
            try await service.excludeRecordingSource(primary, sourceKey: key)
            try await service.excludeRecordingSource(primary, sourceKey: existing["sources"].array[0]["sourceKey"].string)
            try await service.syncCheckpoint(&round)
            let saved = try MJ.parse(XCTUnwrap(store.songData(id: id)))
            let current = try await service.read().song(id)
            XCTAssertTrue(saved["sources"].array.contains { $0["sourceKey"].string == key })
            XCTAssertEqual(current["sources"], saved["sources"],
                "Checkpoint preserves current repository facts; exclusions filter the push, not source ownership")
            XCTAssertEqual(saved["songId"], song["songId"])
            let exclusions = try await service.recordingSourceExclusions(saved)
            XCTAssertEqual(exclusions, Set([key, existing["sources"].array[0]["sourceKey"].string]))
            await peer.endLibrarySync(try XCTUnwrap(round.sessions["wy"]))
            await service.close()
        }
    }

    func testIncompatibleCachedMappingSourcesAreIgnoredWithoutRequeryingSuccessfulPlatform() async throws {
        let song = try MusicNativeCandidateDiscoveryFixture.song()
        let unrelated = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "321", artist: "Other artist"))
        let cache = MusicNativeMemorySourceDiscoveryCache()
        await record(cache, song: song, source: unrelated["sources"].array[0])
        let store = try mappingStore([song])
        let peer = NativeSyncPeer(rows: [NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites)])
        let http = MusicNativeDiscoveryPlatforms()
        let service = try await NativeMusicFixture.service(store, accounts: peer, platform: http, sourceDiscovery: cache)
        let state = try await service.synchronizeLibrary(NativeSyncFixture.request(favorites: true, playlists: false))
        XCTAssertEqual(state.phase, .partial)
        let saved = try NativeSyncFixture.document(store).library["library"]["songs"][song["songId"].string]
        XCTAssertEqual(try MusicModel.sources(saved).map(\.platform), ["tx"])
        let calls = await http.calls()
        XCTAssertTrue(calls.isEmpty)
        await service.close()
    }

    func testSuccessfulEmptyMappingSearchFeedsThePlaybackDiscoveryCache() async throws {
        let track = MusicNativeCandidateDiscoveryFixture.track(title: "Unmatched recording")
        let store = try mappingStore([NativeMusicFixture.storedSong(track)]), peer = NativeSyncPeer(rows: [
            NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites)
        ])
        let cache = MusicNativeMemorySourceDiscoveryCache()
        let http = MusicNativeDiscoveryPlatforms(), qq = MusicNativeCandidateDiscoveryQQ()
        let service = NativeMusicService(database: store,
            accounts: peer,
            platformClient: try NativeMusicFixture.routes(primary: qq, others: http).makeClient(),
            sourceDiscovery: cache,
            enabledPlatforms: [.netease, .qq],
            clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        let state = try await service.synchronizeLibrary(NativeSyncFixture.request(favorites: true, playlists: false))
        XCTAssertEqual(state.phase, .partial)
        let collections = try await service.collections(), favorites = try XCTUnwrap(collections.first { $0.kind == "favorites" })
        let queued = try await service.changeQueue(.collection(id: favorites.id, revision: favorites.revision, index: 0),
                                                   expectedRevision: 0)
        let session = try await service.beginCandidateDiscovery(occurrenceID: XCTUnwrap(queued.currentId),
                                                               expectedQueueRevision: queued.revision)
        let snapshot = try await session.snapshot(discover: true, force: false)
        XCTAssertFalse(snapshot.searched)
        XCTAssertEqual(snapshot.track.sources.map(\.platform), ["tx"])
        let calls = await http.calls(), qqCalls = await qq.calls()
        XCTAssertEqual(calls, ["wy"]); XCTAssertTrue(qqCalls.isEmpty)
        await session.close(retainingAttemptID: nil); await service.close()
    }

    func testMissingMappingSearchHasNoFormerSixtySongCutoff() async throws {
        let tracks = (0..<65).map { MusicNativeCandidateDiscoveryFixture.track(id: "Missing\($0)", title: "Unmatched \($0)") }
        let peer = NativeSyncPeer(rows: [NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites)])
        let http = MusicNativeDiscoveryPlatforms()
        let service = try await NativeMusicFixture.service(mappingStore(tracks.map(NativeMusicFixture.storedSong)), accounts: peer, platform: http)
        let state = try await service.synchronizeLibrary(NativeSyncFixture.request(favorites: true, playlists: false))
        let calls = await http.calls()
        XCTAssertEqual(calls.filter { $0 == "wy" }.count, 65)
        XCTAssertFalse(state.issues.contains { $0.code == "limit" })
        await service.close()
    }

    private func libraryWrapper(ledger: MusicNativeSyncLedger) throws -> MJ {
        var wrapper = MusicModel.library
        wrapper["library"]["accounts"]["wy"] = .string("42")
        wrapper["sync"] = try ledger.json()
        return wrapper
    }

    private func validFence(members: [MusicLibrarySyncMember]) -> MusicNativeSyncFence {
        .init(platform: .netease, accountID: "42", scope: "synthetic-install:local-account", localID: "favorites",
              localRevision: 0, mutation: .init(.init(action: .order, collectionID: "501",
                                               before: NativeSyncFixture.collection("501", members: members),
                                               members: members)))
    }

    private func syncSnapshotFixture(peer: NativeSyncPeer,
                                     selection: MusicLibrarySyncSelection) async throws
        -> (service: NativeMusicService, round: MusicNativeSyncRound) {
        let service = try await NativeMusicFixture.service(NativeSyncFixture.store(), accounts: peer)
        let document = try await service.read()
        let epoch = await service.epoch
        var round = MusicNativeSyncRound(
            request: .init(selections: [selection]), epoch: epoch, port: peer,
            library: document.library["library"],
            ledger: try MusicNativeSyncLedger.load(document.library),
            revision: document.library["revision"].int)
        round.ledger.selections = [selection]
        round.ledger.phase = .pulling
        round.sessions[MusicAccountPlatform.netease.rawValue] = try await peer.beginLibrarySync(platform: .netease)
        return (service, round)
    }

    private func paddedMember(nativeID: String, occurrence: String, paddingBytes: Int) throws -> MusicLibrarySyncMember {
        let track = MJ.obj([
            ("songmid", .string(nativeID)), ("name", .string("Synthetic")),
            ("singer", .string("Fixture")), ("duration", .n(180)),
            ("padding", .string(String(repeating: "a", count: paddingBytes)))
        ]).bytes
        return .init(nativeID: nativeID, occurrenceID: occurrence, track: track)
    }

    private func validTrack(_ track: Data) throws {
        let raw = try MJ.parse(track)
        XCTAssertTrue(raw.isObject)
        _ = try MusicModel.normalize(.init(platform: "wy", data: track))
    }

    private func minimumTrackMembers(count: Int) throws -> [MusicLibrarySyncMember] {
        let track = Data(#"{"songmid":"101","name":"S"}"#.utf8)
        try validTrack(track)
        return (0..<count).map { .init(nativeID: "101", occurrenceID: "o-\($0)", track: track) }
    }

    private func oversizeTrackMembers(count: Int) throws -> [MusicLibrarySyncMember] {
        try (0..<count).map { try paddedMember(nativeID: "101", occurrence: "o-\($0)", paddingBytes: 12 * 1024) }
    }

    private func verifiedOversizeTrackMembers(count: Int) throws -> [MusicLibrarySyncMember] {
        let members = try oversizeTrackMembers(count: count)
        try validTrack(members[0].track)
        XCTAssertLessThanOrEqual(members.map(\.track).map(\.count).max() ?? 0, 16 * 1024)
        XCTAssertGreaterThan(members.reduce(0) { $0 + $1.track.count }, 4 * 1024 * 1024)
        return members
    }

    func testSnapshotSingleCollectionBeyondTwentyThousandPassesWithinByteBudget() async throws {
        let members = try minimumTrackMembers(count: 20_001)
        let selection = MusicLibrarySyncSelection(platform: .netease, favorites: true, playlists: false)
        let peer = NativeSyncPeer(platform: .netease, rows: [
            NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites, members: members)
        ])
        let fixture = try await syncSnapshotFixture(peer: peer, selection: selection)
        let round = fixture.round
        let snapshot = try await fixture.service.syncSnapshot(
            try XCTUnwrap(round.sessions[MusicAccountPlatform.netease.rawValue]), selection: selection, round: round)
        XCTAssertEqual(snapshot.collections.map(\.id), ["500"])
        XCTAssertEqual(snapshot.collections.first?.members.count, 20_001)
        XCTAssertEqual(snapshot.collections.first?.members.first?.nativeID, "101")
        XCTAssertEqual(snapshot.collections.first?.members.last?.occurrenceID, "o-20000")
        await peer.endLibrarySync(try XCTUnwrap(round.sessions[MusicAccountPlatform.netease.rawValue]))
        await fixture.service.close()
    }

    func testSnapshotAggregateBeyondTwentyThousandPassesWithinByteBudget() async throws {
        let first = try minimumTrackMembers(count: 10_001)
        let second = try minimumTrackMembers(count: 10_001)
        let selection = MusicLibrarySyncSelection(platform: .netease, favorites: true, playlists: true)
        let peer = NativeSyncPeer(platform: .netease, rows: [
            NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites, members: first),
            NativeSyncFixture.collection("501", name: "Other", kind: .playlist, members: second)
        ])
        let fixture = try await syncSnapshotFixture(peer: peer, selection: selection)
        let round = fixture.round
        let snapshot = try await fixture.service.syncSnapshot(
            try XCTUnwrap(round.sessions[MusicAccountPlatform.netease.rawValue]), selection: selection, round: round)
        XCTAssertEqual(snapshot.collections.map(\.id), ["500", "501"])
        XCTAssertEqual(snapshot.collections.map(\.members.count), [10_001, 10_001])
        XCTAssertEqual(snapshot.collections.first?.members.first?.nativeID, "101")
        XCTAssertEqual(snapshot.collections.first?.members.last?.occurrenceID, "o-10000")
        XCTAssertEqual(snapshot.collections.last?.members.first?.nativeID, "101")
        XCTAssertEqual(snapshot.collections.last?.members.last?.occurrenceID, "o-10000")
        await peer.endLibrarySync(try XCTUnwrap(round.sessions[MusicAccountPlatform.netease.rawValue]))
        await fixture.service.close()
    }

    func testSnapshotDoesNotRepeatPerHopBudgetAcrossAcquiredTracksOrRetainUnusedPadding() async throws {
        let members = try verifiedOversizeTrackMembers(count: 350)
        let selection = MusicLibrarySyncSelection(platform: .netease, favorites: true, playlists: false)
        let peer = NativeSyncPeer(platform: .netease, rows: [
            NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites, members: members)
        ])
        let fixture = try await syncSnapshotFixture(peer: peer, selection: selection)
        let round = fixture.round
        let snapshot = try await fixture.service.syncSnapshot(
            try XCTUnwrap(round.sessions[MusicAccountPlatform.netease.rawValue]), selection: selection, round: round)
        XCTAssertEqual(snapshot.collections.first?.members.count, 350)
        let document = try await fixture.service.read()
        XCTAssertEqual((try document.fixtureSongs()).fields.count, 1)
        XCTAssertFalse(document.library.json.contains("padding"))
        await peer.endLibrarySync(try XCTUnwrap(round.sessions[MusicAccountPlatform.netease.rawValue]))
        await fixture.service.close()
    }

    func testFenceMembersAndBeforeBeyondTwentyThousandDecode() throws {
        let members = try minimumTrackMembers(count: 20_001)
        var ledger = MusicNativeSyncLedger()
        ledger.scopes["wy"] = "synthetic-install:local-account"
        ledger.fences = [validFence(members: members)]
        let wrapper = try libraryWrapper(ledger: ledger)
        XCTAssertLessThan(try wrapper["sync"].bytes.count, 5 * 1024 * 1024)
        let value = try MusicNativeSyncLedger.load(wrapper)
        XCTAssertEqual(value.fences.count, 1)
        XCTAssertEqual(value.fences.first?.mutation.members.count, 20_001)
        XCTAssertEqual(value.fences.first?.mutation.before?.members.count, 20_001)
        XCTAssertEqual(value.fences.first?.mutation.members.first?.nativeID, "101")
        XCTAssertEqual(value.fences.first?.mutation.members.last?.occurrenceID, "o-20000")
        XCTAssertEqual(value.fences.first?.mutation.before?.members.first?.nativeID, "101")
        XCTAssertEqual(value.fences.first?.mutation.before?.members.last?.occurrenceID, "o-20000")
    }

    func testLedgerMutationPreservesOpaqueExtensionsAndRetainedFenceIdentity() throws {
        let extra = try MJ.parse(Data(#"{"decimal":1.234567890123456789,"huge":1e999,"integer":9007199254740993,"é":1,"e\u0301":2}"#.utf8))
        var ledger = MusicNativeSyncLedger()
        ledger.scopes["wy"] = "synthetic-install:local-account"
        ledger.selections = [.init(platform: .netease, playlists: true)]
        ledger.automatic.selections = ledger.selections
        ledger.fences = [validFence(members: [NativeSyncFixture.member("101")]),
                         validFence(members: [NativeSyncFixture.member("102")])]
        var wrapper = try libraryWrapper(ledger: ledger)
        wrapper["sync"]["extra"] = extra
        wrapper["sync"]["automatic"]["extra"] = extra
        var selections = wrapper["sync"]["selections"].array
        selections[0]["extra"] = extra
        wrapper["sync"]["selections"] = .array(selections)
        wrapper["sync"]["automatic"]["selections"] = .array(selections)
        var fences = wrapper["sync"]["fences"].array
        fences[0]["extra"] = .string("retired")
        fences[1]["extra"] = extra
        wrapper["sync"]["fences"] = .array(fences)
        var loaded = try MusicNativeSyncLedger.load(wrapper)
        loaded.phase = .pulling; loaded.automatic.revision += 1
        loaded.fences.removeFirst()
        let saved = try loaded.json()
        XCTAssertEqual(saved["extra"].stable, extra.stable)
        XCTAssertEqual(saved["automatic"]["extra"].stable, extra.stable)
        XCTAssertEqual(saved["selections"].array[0]["extra"].stable, extra.stable)
        XCTAssertEqual(saved["automatic"]["selections"].array[0]["extra"].stable, extra.stable)
        XCTAssertEqual(saved["fences"].array.count, 1)
        XCTAssertEqual(saved["fences"].array[0]["extra"].stable, extra.stable)
        XCTAssertEqual(saved["phase"].string, "pulling")
        XCTAssertEqual(saved["automatic"]["revision"].int, 1)
    }

    func testFenceMembersAndBeforeBeyondFormerBudgetRoundTrip() throws {
        let padding = String(repeating: "o", count: 8192)
        let members = (0..<350).map {
            MusicLibrarySyncMember(nativeID: "101", occurrenceID: "o-\($0)-" + padding,
                                   track: NativeSyncFixture.member("101").track)
        }
        var ledger = MusicNativeSyncLedger()
        ledger.scopes["wy"] = "synthetic-install:local-account"
        ledger.fences = [validFence(members: members)]
        let encoded = try ledger.json()
        XCTAssertGreaterThan(encoded.bytes.count, 5 * 1024 * 1024)
        let loaded = try MusicNativeSyncLedger.load(try libraryWrapper(ledger: ledger))
        XCTAssertEqual(loaded.fences.first?.mutation.members.count, members.count)
        XCTAssertEqual(loaded.fences.first?.mutation.members.last?.occurrenceID, members.last?.occurrenceID)
    }

    func testLedgerPreservesAllPendingFencesAndIssues() throws {
        var ledger = MusicNativeSyncLedger()
        ledger.scopes["wy"] = "synthetic-install:local-account"
        ledger.fences = (0..<513).map { _ in validFence(members: [NativeSyncFixture.member("101")]) }
        ledger.issues = (0..<129).map { .init(collectionID: "collection-\($0)", code: "unconfirmed") }
        let loaded = try MusicNativeSyncLedger.load(try libraryWrapper(ledger: ledger))
        XCTAssertEqual(loaded.fences.count, 513)
        XCTAssertEqual(loaded.issues, ledger.issues)
    }

    func testFenceDuplicateOccurrencesAndMissingWriteIdentityStillFail() throws {
        let duplicate = (0..<2).map { _ in NativeSyncFixture.member("101", occurrence: "same") }
        var duplicateLedger = MusicNativeSyncLedger()
        duplicateLedger.scopes["wy"] = "synthetic-install:local-account"
        duplicateLedger.fences = [validFence(members: duplicate)]
        XCTAssertThrowsError(try MusicNativeSyncLedger.load(try libraryWrapper(ledger: duplicateLedger))) {
            XCTAssertEqual($0 as? MusicServiceError, .corruptDocument)
        }

        let valid = NativeSyncFixture.member("101", occurrence: "Invalid")
        let invalid = MusicLibrarySyncMember(nativeID: "", occurrenceID: "Invalid", track: valid.track)
        var invalidLedger = MusicNativeSyncLedger()
        invalidLedger.scopes["wy"] = "synthetic-install:local-account"
        invalidLedger.fences = [
            .init(platform: .netease, accountID: "42", scope: "synthetic-install:local-account", localID: "favorites",
                  localRevision: 0, mutation: .init(.init(action: .order, collectionID: "501",
                                                   before: NativeSyncFixture.collection("501", members: [invalid]),
                                                   members: [invalid])))
        ]
        XCTAssertThrowsError(try MusicNativeSyncLedger.load(try libraryWrapper(ledger: invalidLedger))) {
            XCTAssertEqual($0 as? MusicServiceError, .corruptDocument)
        }
    }

    func testKGRepeatedHashOccurrencesRemainDistinctAndOrderRetainsAllFileIDs() async throws {
        let a = String(repeating: "A", count: 32), b = String(repeating: "B", count: 32)
        let members = [NativeSyncFixture.member(a, platform: .kugou, occurrence: "11"),
                       NativeSyncFixture.member(b, platform: .kugou, occurrence: "12"),
                       NativeSyncFixture.member(a, platform: .kugou, occurrence: "13")]
        let peer = NativeSyncPeer(platform: .kugou, rows: [NativeSyncFixture.collection("0:7", members: members)])
        let store = try NativeSyncFixture.store(), service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await service.synchronizeLibrary(NativeSyncFixture.request(.kugou))
        var calls = await peer.calls(); XCTAssertTrue(calls.isEmpty) // A,B,A must not become A,A,B.
        let lists = try await service.collections(), local = try XCTUnwrap(lists.first { $0.kind == "playlist" })
        let tracks = try await service.tracks(in: local); XCTAssertEqual(tracks.count, 2)
        _ = try await service.editPlaylist(local, name: nil, refs: nil, order: tracks.reversed().map(\.songId))
        _ = try await service.synchronizeLibrary(NativeSyncFixture.request(.kugou))
        calls = await peer.calls()
        XCTAssertEqual(calls.map(\.action), [.order])
        XCTAssertEqual(calls[0].members.map(\.occurrenceID), ["12", "11", "13"])
        let saved = try NativeSyncFixture.document(store).library["library"]["collections"].array.last!
        XCTAssertEqual(saved["bindings"].array[0]["baseline"].strings, ["12", "11", "13"])
    }

    func testUnselectedKindCannotConfirmDeletionFenceAndRecoveryIsReadOnly() async throws {
        let peer = NativeSyncPeer(rows: [NativeSyncFixture.collection(),
            NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites)])
        let store = try NativeSyncFixture.store(), service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await service.synchronizeLibrary(NativeSyncFixture.request())
        let lists = try await service.collections()
        try await service.removePlaylist(XCTUnwrap(lists.first { $0.kind == "playlist" }))
        await peer.configure(fault: .acknowledgedWithoutChange)
        _ = try await service.synchronizeLibrary(NativeSyncFixture.request())
        let before = try MusicNativeSyncLedger.load(NativeSyncFixture.document(store).library)
        XCTAssertEqual(before.fences.first?.mutation.action, .delete)
        await peer.configure()
        let recovery = try await service.synchronizeLibrary(NativeSyncFixture.request(favorites: true, playlists: false))
        XCTAssertEqual(recovery.phase, .blocked)
        let after = try MusicNativeSyncLedger.load(NativeSyncFixture.document(store).library)
        XCTAssertEqual(after.fences.count, 1)
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 1)
    }

    func testRemotePlaylistRemovalRetiresOnlyManagedBindingWhileSubscriptionStaysReadOnly() async throws {
        let a = NativeSyncFixture.member("101"), b = NativeSyncFixture.member("102")
        let peer = NativeSyncPeer(rows: [NativeSyncFixture.collection(members: [a])],
            subscriptions: [.init(remoteID: "502", relationID: "502", name: "Subscribed")])
        let store = try NativeSyncFixture.store([b]), service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await service.synchronizeLibrary(NativeSyncFixture.request())
        var lists = try await service.collections()
        let writable = try XCTUnwrap(lists.first { $0.title == "Remote" })
        _ = try await service.editPlaylist(writable, name: nil, refs: try await NativeSyncFixture.refs([a, b], service: service), order: nil)
        await peer.replace([])
        let removed = try await service.synchronizeLibrary(NativeSyncFixture.request())
        XCTAssertEqual(removed.phase, .synced)
        let tombstone = try XCTUnwrap(NativeSyncFixture.document(store).library["library"]["collections"].array.first { $0["id"].string == writable.id })
        XCTAssertTrue(tombstone["deleted"].bool); XCTAssertTrue(tombstone["bindings"].array.isEmpty)
        XCTAssertFalse(tombstone["intents"].fields.contains { $0.1["pending"].bool })
        lists = try await service.collections()
        let readonly = try XCTUnwrap(lists.first { $0.title == "Subscribed" })
        XCTAssertEqual(readonly.networkReference?.remoteID, "502"); XCTAssertFalse(readonly.canEdit)
        do { _ = try await service.editPlaylist(readonly, name: nil, refs: [], order: nil); XCTFail("network members are read-only") }
        catch { XCTAssertEqual(error as? MusicServiceError, .unsupported) }
        do { try await service.removePlaylist(readonly); XCTFail("must not delete the author's playlist") }
        catch { XCTAssertEqual(error as? MusicServiceError, .unsupported) }
        let settled = try await service.synchronizeLibrary(NativeSyncFixture.request())
        XCTAssertEqual(settled.phase, .synced)
        let calls = await peer.calls(); XCTAssertTrue(calls.isEmpty)
        let managed = try NativeSyncFixture.document(store).library["library"]["collections"].array
        XCTAssertFalse(managed.contains { $0["id"].string == readonly.id })
        let subscriptions = await peer.subscriptionContents()
        XCTAssertEqual(subscriptions.map(\.remoteID), ["502"])
    }

    func testRemoteLongPlaylistNameAndLocatorCanBeImportedUpdatedAndColdReadWithoutRemoteWrites() async throws {
        let member = NativeSyncFixture.member("101")
        let remoteID = "remote-" + String(repeating: "定", count: 800)
        let name = "Remote " + String(repeating: "长", count: 800)
        let renamed = name + " updated"
        let peer = NativeSyncPeer(rows: [NativeSyncFixture.collection(remoteID, name: name, members: [member])])
        let store = try NativeSyncFixture.store(), service = try await NativeMusicFixture.service(store, accounts: peer)
        let imported = try await service.synchronizeLibrary(NativeSyncFixture.request())
        XCTAssertEqual(imported.phase, .synced)
        let firstLists = try await service.collections()
        let first = try XCTUnwrap(firstLists.first { $0.kind == "playlist" })
        XCTAssertEqual(first.title, name)
        let firstDocument = try NativeSyncFixture.document(store)
        let firstBinding = try XCTUnwrap(firstDocument.library["library"]["collections"].array
            .first { $0["id"].string == first.id }?["bindings"].array.first)
        XCTAssertEqual(firstBinding["id"].string, remoteID)
        let firstTracks = try await service.tracks(in: first)
        let songID = try XCTUnwrap(firstTracks.first?.songId)

        await peer.replace([NativeSyncFixture.collection(remoteID, name: renamed, members: [member])])
        let updated = try await service.synchronizeLibrary(NativeSyncFixture.request())
        XCTAssertEqual(updated.phase, .synced)
        let updatedLists = try await service.collections()
        let current = try XCTUnwrap(updatedLists.first { $0.id == first.id })
        XCTAssertEqual(current.title, renamed)
        let currentTracks = try await service.tracks(in: current)
        XCTAssertEqual(currentTracks.map(\.songId), [songID])
        let writes = await peer.calls()
        XCTAssertTrue(writes.isEmpty)
        await service.close()

        let before = await peer.counts()
        let cold = try await NativeMusicFixture.service(store, accounts: peer)
        let coldLists = try await cold.collections()
        let restored = try XCTUnwrap(coldLists.first { $0.id == first.id })
        XCTAssertEqual(restored.title, renamed)
        let restoredTracks = try await cold.tracks(in: restored)
        XCTAssertEqual(restoredTracks.map(\.songId), [songID])
        let coldDocument = try NativeSyncFixture.document(store)
        let coldBinding = try XCTUnwrap(coldDocument.library["library"]["collections"].array
            .first { $0["id"].string == first.id }?["bindings"].array.first)
        XCTAssertEqual(coldBinding["id"].string, remoteID)
        let after = await peer.counts()
        XCTAssertEqual(after.0, before.0); XCTAssertEqual(after.1, before.1)
        await cold.close()
    }

    func testPendingLocalRemovalWinsRemotePresenceAndQQOrderNeverDeletesAndReadds() async throws {
        let a = NativeSyncFixture.member("A", platform: .qq), b = NativeSyncFixture.member("B", platform: .qq)
        let peer = NativeSyncPeer(platform: .qq, rows: [NativeSyncFixture.collection(members: [a, b], order: false)])
        let store = try NativeSyncFixture.store(), service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await service.synchronizeLibrary(NativeSyncFixture.request(.qq))
        var lists = try await service.collections(), local = try XCTUnwrap(lists.first { $0.kind == "playlist" })
        var tracks = try await service.tracks(in: local)
        _ = try await service.editPlaylist(local, name: nil, refs: nil, order: tracks.reversed().map(\.songId))
        let order = try await service.synchronizeLibrary(NativeSyncFixture.request(.qq))
        XCTAssertTrue(order.issues.contains { $0.code == "orderUnsupported" })
        var calls = await peer.calls(); XCTAssertTrue(calls.isEmpty)
        lists = try await service.collections(); local = try XCTUnwrap(lists.first { $0.kind == "playlist" })
        tracks = try await service.tracks(in: local)
        _ = try await service.editPlaylist(local, name: nil, refs: [tracks[0].ref], order: nil)
        _ = try await service.synchronizeLibrary(NativeSyncFixture.request(.qq))
        calls = await peer.calls(); XCTAssertEqual(calls.map(\.action), [.remove])
        let remaining = await peer.contents()
        XCTAssertEqual(remaining[0].members.map(\.nativeID), ["B"])
    }

    func testCurrentReferenceMapRetainsIdentityAndReverseMapIsRejectedWithoutWrites() async throws {
        for reversed in [false, true] {
            let member = NativeSyncFixture.member("101")
            let store = try NativeSyncFixture.store([member])
            var doc = try NativeSyncFixture.document(store), lib = doc.library["library"]
            let songID = try XCTUnwrap(lib["songs"].fields.first?.0)
            lib["accounts"]["wy"] = .string("42")
            var favorites = lib["collections"].array[0]
            favorites["order"] = .strings([songID])
            favorites["bindings"] = .array([.obj([("platform", .string("wy")), ("accountId", .string("42")),
                ("id", .string("500")), ("writable", .bool(true)), ("name", .string("Favorites")),
                ("baseline", .strings(["101"])), ("map", .obj(reversed ? [(songID, .string("101"))] : [("101", .string(songID))]))])])
            lib["collections"] = .array([favorites]); doc.library["library"] = lib
            let original = store.snapshot().0
            try store.replaceSnapshot(expected: original, replacement: doc.encode(changed: ["library"]))
            let peer = NativeSyncPeer(rows: [NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites, members: [member])])
            let service = try await NativeMusicFixture.service(store, accounts: peer)
            if reversed {
                let before = store.snapshot()
                do {
                    _ = try await service.synchronizeLibrary(NativeSyncFixture.request(favorites: true, playlists: false))
                    XCTFail("Reverse map must not be converted")
                } catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument) }
                XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
                let counts = await peer.counts(), calls = await peer.calls()
                XCTAssertEqual(counts.0, 0); XCTAssertEqual(counts.1, 0); XCTAssertTrue(calls.isEmpty)
                await service.close()
                continue
            }
            _ = try await service.synchronizeLibrary(NativeSyncFixture.request(favorites: true, playlists: false))
            let saved = try NativeSyncFixture.document(store)
            let c = saved.library["library"]["collections"].array[0]
            XCTAssertEqual(c["order"].strings, [songID]); XCTAssertEqual(c["bindings"].array[0]["map"]["101"].string, songID)
            let calls = await peer.calls(); XCTAssertTrue(calls.isEmpty)
            await service.close()
        }
    }

    func testMalformedCurrentAreasCannotDispatchSyncOrRewriteRecords() async throws {
        let valid = try NativeSyncFixture.store([NativeSyncFixture.member("101")])
        var raw = try MJ.parse(XCTUnwrap(valid.snapshot().0))
        raw["areas"] = .array([])
        let original = raw.bytes, store = NativeMusicTestLibrary(original)
        let peer = NativeSyncPeer()
        do {
            let service = try await NativeMusicFixture.service(store, accounts: peer)
            defer { Task { await service.close() } }
            _ = try await service.synchronizeLibrary(NativeSyncFixture.request())
            XCTFail("A malformed current document was accepted")
        } catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument) }
        XCTAssertEqual(store.snapshot().0, original); XCTAssertEqual(store.snapshot().2, 0)
        let counts = await peer.counts(), calls = await peer.calls()
        XCTAssertEqual(counts.0, 0); XCTAssertEqual(counts.1, 0); XCTAssertTrue(calls.isEmpty)
    }

    func testSyncPreservesUnknownSelectedCollectionFields() async throws {
        let valid = try NativeSyncFixture.store()
        for extra in [MJ.obj(), .obj([("value", .string("extra"))])] {
            var document = try NativeSyncFixture.document(valid)
            var collections = document.library["library"]["collections"].array
            collections[0]["extra"] = extra
            let unselected = try MJ.parse(Data(#"{"id":"unselected","kind":"playlist","order":{},"bindings":"unused","extra":1e999}"#.utf8))
            let unknown = try MJ.parse(Data(#"{"id":"future","kind":"future","order":false,"extra":1.234567890123456789}"#.utf8))
            document.library["library"]["collections"] = .array(collections + [unselected, unknown])
            let bytes = try document.encode(changed: ["library"]), store = NativeMusicTestLibrary(bytes)
            let peer = NativeSyncPeer(), service = try await NativeMusicFixture.service(store, accounts: peer)
            _ = try await service.synchronizeLibrary(NativeSyncFixture.request(favorites: true, playlists: false))
            let saved = try NativeSyncFixture.document(store)
            let selected = try XCTUnwrap(saved.library["library"]["collections"].array.first { $0["id"] == collections[0]["id"] })
            XCTAssertEqual(selected["extra"], extra)
            XCTAssertEqual(Array(saved.library["library"]["collections"].array.suffix(2)), [unselected, unknown])
            await service.close()
        }
    }

    func testAnchorMergeAndMalformedFenceFailClosed() throws {
        XCTAssertEqual(MusicNativeSyncLogic.mergeOrder(["A", "C", "E"], ["A", "B", "C", "D"]), ["A", "B", "C", "D", "E"])
        XCTAssertEqual(MusicNativeSyncLogic.mergeOrder(["C", "A"], ["A", "B", "C"]), ["C", "A", "B"])
        var wrapper = MusicModel.library, ledger = MusicNativeSyncLedger()
        wrapper["library"]["accounts"]["wy"] = .string("42"); ledger.scopes["wy"] = "s"
        ledger.fences = [.init(platform: .netease, accountID: "42", scope: "s", localID: "favorites",
            localRevision: 0, mutation: .init(.init(action: .delete)))]
        wrapper["sync"] = try ledger.json()
        XCTAssertThrowsError(try MusicNativeSyncLedger.load(wrapper)) { XCTAssertEqual($0 as? MusicServiceError, .corruptDocument) }
    }
}

actor MultiPlatformSyncPeer: MusicNativeAccountProviding, MusicNativeLibrarySyncProviding {
    let peers: [MusicAccountPlatform: NativeSyncPeer]
    init(_ peers: [MusicAccountPlatform: NativeSyncPeer]) { self.peers = peers }
    func beginLibrarySync(platform: MusicAccountPlatform) async throws -> MusicLibrarySyncSession {
        guard let peer = peers[platform] else { throw MusicServiceError.unsupported }
        return try await peer.beginLibrarySync(platform: platform)
    }
    func checkLibrarySync(_ session: MusicLibrarySyncSession) async throws {
        guard let peer = peers[session.platform] else { throw MusicServiceError.unsupported }
        try await peer.checkLibrarySync(session)
    }
    func endLibrarySync(_ session: MusicLibrarySyncSession) async {
        guard let peer = peers[session.platform] else { return }
        await peer.endLibrarySync(session)
    }
    func readLibrarySync(_ session: MusicLibrarySyncSession,
                         kinds: [MusicLibrarySyncKind]) async throws -> MusicLibrarySyncSnapshot {
        guard let peer = peers[session.platform] else { throw MusicServiceError.unsupported }
        return try await peer.readLibrarySync(session, kinds: kinds)
    }
    func readLibrarySubscriptions(_ session: MusicLibrarySyncSession) async throws -> MusicLibrarySyncSnapshot {
        guard let peer = peers[session.platform] else { throw MusicServiceError.unsupported }
        return try await peer.readLibrarySubscriptions(session)
    }
    func writeLibrarySync(_ session: MusicLibrarySyncSession,
                          mutation: MusicLibrarySyncMutation) async throws -> MusicLibrarySyncWriteResult {
        guard let peer = peers[session.platform] else { throw MusicServiceError.unsupported }
        return try await peer.writeLibrarySync(session, mutation: mutation)
    }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) throws -> MusicNativeTrackPage {
        throw MusicServiceError.unavailable
    }
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) throws -> MusicAccountCollectionPage {
        throw MusicServiceError.unavailable
    }
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) throws -> MusicNativeTrackPage {
        throw MusicServiceError.unavailable
    }
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) throws -> [MusicPlaybackOption] {
        throw MusicServiceError.unavailable
    }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String,
                       selection: MusicPlaybackSelection) throws -> MusicNativeAudio {
        throw MusicServiceError.unavailable
    }
}
