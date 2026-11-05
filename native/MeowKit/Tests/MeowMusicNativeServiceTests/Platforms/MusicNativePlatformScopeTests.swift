import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

/// Deterministic native boundary checks only: no JS runtime, account, or network.
final class MusicNativePlatformScopeTests: XCTestCase {
    private let threePlatformScope: Set<MusicPublicBrowsePlatform> = [.netease, .qq, .kugou]

    private func service(_ store: NativeMusicMemoryDocument = .init(),
                         enabled: Set<MusicPublicBrowsePlatform> = [.netease, .qq, .kugou],
                         scripts: (any MusicScriptResolving)? = nil,
                         accounts: (any MusicNativeAccountProviding)? = nil,
                         qq: any MusicPublicQQProviding = NativeMusicQQFixture(),
                         http: any MusicNativeHTTPTransport = NativeMusicNoNetwork()) async throws -> NativeMusicService {
        // Explicit three-platform scope; scripts are omitted by default.
        let value = NativeMusicService(document: store, scripts: scripts, accounts: accounts, qq: qq,
            enabledPlatforms: enabled, transport: http, clock: { NativeMusicFixture.date }, random: { 0 })
        try await value.setAccess(available: true, generation: 1)
        return value
    }

    private func rejected<T>(_ operation: () async throws -> T,
                             file: StaticString = #filePath, line: UInt = #line) async {
        do { _ = try await operation(); XCTFail("disabled platform was accepted", file: file, line: line) }
        catch { XCTAssertEqual(error as? MusicServiceError, .unsupported, file: file, line: line) }
    }

    private func mixedSong() throws -> MJ {
        let ids = ["wy": "321", "tx": "SeedMID", "kg": String(repeating: "A", count: 32),
                   "kw": "9007199254740993", "mg": "60012345678"]
        var song = try MusicNativeCandidateDiscoveryFixture.song()
        song["sources"] = .array(try MusicPublicBrowsePlatform.allCases.flatMap {
            try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track($0.rawValue, id: ids[$0.rawValue]!))["sources"].array
        })
        return try MusicModel.cleanSong(song)
    }

    func testDefaultInventoryIsBackwardCompatibleAndConfiguredOrderIsStable() async throws {
        let unconfigured = NativeMusicService(document: NativeMusicMemoryDocument(), qq: NativeMusicQQFixture(),
            transport: NativeMusicNoNetwork())
        try await unconfigured.setAccess(available: true, generation: 1)
        let original = try await unconfigured.platforms()
        XCTAssertEqual(original.map(\.id), ["wy", "tx", "kg", "kw", "mg"])
        XCTAssertEqual(unconfigured.enabledPlatforms, Set(MusicPublicBrowsePlatform.allCases))
        await unconfigured.close()

        let configured = try await service()
        let selected = try await configured.platforms()
        XCTAssertEqual(selected, Array(original.prefix(3)))
        XCTAssertEqual(configured.enabledPlatforms, threePlatformScope)
        await configured.close()

        let empty = try await service(enabled: [])
        let inventory = try await empty.platforms()
        XCTAssertTrue(inventory.isEmpty)
        await empty.close()
    }

    func testDisabledSearchPlaylistsDetailAndWholeQueueNeverReachTransport() async throws {
        let http = NativeMusicHTTPFixture([]), store = NativeMusicMemoryDocument()
        let owner = try await service(store, http: http)
        for platform in [MusicPublicBrowsePlatform.kuwo, .migu] {
            let collection = MusicPublicBrowseCollection(platform: platform, nativeID: "9007199254740993", title: "Saved")
            let request = MusicPublicBrowseQueueRequest(collection: collection, snapshot: String(repeating: "a", count: 64))
            await rejected { try await owner.search(platform: platform.rawValue, query: "Synthetic", page: 1, limit: 30) }
            await rejected { try await owner.publicPlaylists(platform: platform, query: "Synthetic", cursor: nil) }
            await rejected { try await owner.publicDetail(collection, cursor: nil) }
            await rejected { try await owner.publicQueueTracks(request) }
            // Explicit opening now validates before issuing source authority.
            await rejected { try await owner.openPublicQueue(request) }
            // Independently supplied capabilities must still be checked at
            // both consumer boundaries; rejecting open alone is insufficient.
            let source = MusicRemoteQueueSource(context: .init(kind: .playlist,
                id: collection.id, title: "Saved", platform: platform.rawValue), loadPage: { _ in
                    XCTFail("Disabled source reached its loader"); return .init(items: [])
                })
            await rejected { try await owner.remoteQueuePage(source, cursor: nil) }
            await rejected {
                try await owner.replaceRemoteQueue(source,
                    start: .init(index: 0, songID: "saved", sourceKeys: []),
                    expectedRevision: 0, intent: .init())
            }
            await source.close()
        }
        await rejected { try await owner.search(platform: "unknown", query: "Synthetic", page: 1, limit: 30) }
        let calls = await http.calls()
        XCTAssertTrue(calls.isEmpty)
        XCTAssertNil(store.snapshot().0); XCTAssertEqual(store.snapshot().2, 0)
        await owner.close()
    }

    func testAllDiscoveryRoutesCheckTheirRealProviderBeforeIO() async throws {
        let http = NativeMusicHTTPFixture([]), accounts = MusicPlatformScopeAccounts()
        let owner = try await service(enabled: [.qq], accounts: accounts, http: http)
        let kinds: [MusicPublicBrowseChannel.Kind] = [.featured, .playlists, .rankings, .newSongs, .cube, .unsupported]
        let routes: [MusicPublicBrowseDestination] = [.home, .featured, .channels] + kinds.map {
            .channel(.init(id: $0.rawValue, title: "Saved", kind: $0, pageCode: "saved_page"), tag: "")
        }
        for route in routes { await rejected { try await owner.publicDiscovery(route, cursor: nil) } }
        for platform in [MusicAccountPlatform.netease, .kugou] {
            await rejected { try await owner.publicDiscovery(.daily(platform), cursor: nil) }
            await rejected { try await owner.recommendations(platform: platform, kind: "daily", cursor: nil) }
            await rejected { try await owner.accountCollections(platform: platform, cursor: nil) }
            await rejected { try await owner.accountTracks(platform: platform, collectionID: "favorites", cursor: nil) }
        }
        let network = await http.calls(), accountCalls = await accounts.events()
        XCTAssertTrue(network.isEmpty); XCTAssertTrue(accountCalls.isEmpty)
        _ = try await owner.publicDiscovery(.daily(.qq), cursor: nil)
        let allowed = await accounts.events()
        XCTAssertEqual(allowed, ["recommendations:tx"])
        await owner.close()
    }

    func testExplicitThreePlatformScopeStillUsesAllThreeNativeAdapters() async throws {
        let http = MusicNativeCandidateDiscoveryHTTP(), qq = MusicNativeCandidateDiscoveryQQ()
        let owner = try await service(qq: qq, http: http)
        for platform in ["wy", "tx", "kg"] {
            let page = try await owner.search(platform: platform, query: "Synthetic recording", page: 1, limit: 30)
            XCTAssertEqual(page.items.count, 1)
            XCTAssertEqual(page.items.first?.sources.first?.platform, platform)
        }
        let network = await http.calls(), qqCalls = await qq.calls()
        XCTAssertEqual(network, ["wy", "kg"]); XCTAssertEqual(qqCalls.count, 1)
        await owner.close()
    }

    func testAutomaticDiscoverySearchesOnlyEnabledPlatformsAndRetainsOldSources() async throws {
        let song = try mixedSong(), store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
        let http = MusicNativeCandidateDiscoveryHTTP(), qq = MusicNativeCandidateDiscoveryQQ()
        let accounts = MusicPlatformScopeAccounts()
        let owner = try await service(store, accounts: accounts, qq: qq, http: http)
        let before = store.snapshot().0
        let session = try await owner.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let snapshot = try await session.snapshot(discover: true, force: true)
        XCTAssertTrue(snapshot.searched); XCTAssertEqual(snapshot.scriptState, .notConfigured)
        XCTAssertEqual(Set(snapshot.options.map { $0.playback.siteId }), Set(["wy", "tx", "kg"]))
        XCTAssertFalse(snapshot.issues.contains { ["kw", "mg"].contains($0.platform) })
        let network = await http.calls(), qqCalls = await qq.calls(), accountCalls = await accounts.events()
        XCTAssertEqual(Set(network), Set(["wy", "kg"])); XCTAssertEqual(network.count, 2)
        XCTAssertEqual(qqCalls.count, 1)
        XCTAssertEqual(Set(accountCalls), Set(["options:wy", "options:tx", "options:kg"]))
        XCTAssertEqual(store.snapshot().0, before, "discovery is staged")
        try await session.commitDiscovery()
        let saved = try MusicDocument(store.snapshot().0, runID: "inspection")
        let current = saved.queue["entries"].array[0]["song"]
        XCTAssertEqual(current["songId"], song["songId"])
        for platform in ["kw", "mg"] {
            XCTAssertEqual(current["sources"].array.filter { $0["source"].string == platform },
                           song["sources"].array.filter { $0["source"].string == platform })
        }
        XCTAssertEqual(saved.queue["currentId"].string, "entry-1")
        await session.close(retainingAttemptID: nil); await owner.close()
    }

    func testOptionsFilterNativeAndScriptCapabilitiesWithoutRewritingSavedSong() async throws {
        let song = try mixedSong(), store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
        let scripts = MusicPlatformScopeScripts(), accounts = MusicPlatformScopeAccounts()
        let owner = try await service(store, scripts: scripts, accounts: accounts)
        let before = store.snapshot().0
        let availability = try await owner.sourceAvailability(for: .init(kind: .queue, id: "entry-1"))
        XCTAssertEqual(availability.scriptState, .ready)
        XCTAssertEqual(availability.options.count, 6)
        XCTAssertEqual(Set(availability.options.map(\.siteId)), Set(["wy", "tx", "kg"]))
        let session = try await owner.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let snapshot = try await session.snapshot(discover: false, force: false)
        XCTAssertEqual(snapshot.options.count, 6)
        XCTAssertEqual(Set(snapshot.options.map { $0.playback.siteId }), Set(["wy", "tx", "kg"]))
        XCTAssertEqual(Set(snapshot.track.sources.map(\.platform)), Set(["wy", "tx", "kg", "kw", "mg"]))
        XCTAssertEqual(store.snapshot().0, before); XCTAssertEqual(store.snapshot().2, 0)
        let scriptCalls = await scripts.events(), accountCalls = await accounts.events()
        XCTAssertEqual(scriptCalls, ["sources", "sources"])
        XCTAssertEqual(accountCalls.count, 6)
        XCTAssertEqual(Set(accountCalls), Set(["options:wy", "options:tx", "options:kg"]))
        await session.close(retainingAttemptID: nil); await owner.close()
    }

    func testFivePlatformScopeKeepsAccountOptionsFeedbackAndSyncAtThreeAccountPlatforms() async throws {
        let song = try mixedSong(), store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
        let accounts = MusicPlatformScopeAccounts()
        let owner = try await service(store, enabled: Set(MusicPublicBrowsePlatform.allCases), accounts: accounts)
        // Without a script host, KW/MG have public search, lyrics and saved
        // sources but no account playback candidate.
        let availability = try await owner.sourceAvailability(for: .init(kind: .queue, id: "entry-1"))
        XCTAssertEqual(availability.scriptState, .notConfigured)
        XCTAssertEqual(availability.options.count, 3)
        XCTAssertEqual(Set(availability.options.map(\.siteId)), Set(["wy", "tx", "kg"]))
        XCTAssertFalse(availability.options.contains { ["kw", "mg"].contains($0.siteId) },
                       "KW/MG have no account playback candidate")
        let optionCalls = await accounts.events()
        XCTAssertEqual(Set(optionCalls.filter { $0.hasPrefix("options:") }),
                       Set(["options:wy", "options:tx", "options:kg"]))

        // Enabled scope alone must not invent an official KW/MG account path.
        let sources = try MusicModel.sources(song)
        for platform in ["kw", "mg"] {
            let source = try XCTUnwrap(sources.first { $0.platform == platform })
            let option = MusicPlaybackOption(installationId: "official:scope", generation: 1, siteId: platform,
                sourceKey: source.sourceKey, qualities: ["128k"], title: "Saved", version: "1",
                contentHash: String(repeating: "a", count: 64))
            do {
                _ = try await owner.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: "entry-1",
                                            selection: option.selection(quality: "128k"))
                XCTFail("KW/MG have no official account playback")
            } catch { XCTAssertEqual(error as? MusicServiceError, .sourceUnavailable) }
        }
        let afterResolve = await accounts.events()
        XCTAssertFalse(afterResolve.contains { $0.hasPrefix("resolve:") })

        // Feedback settings and mapping targets stay on the three account
        // platforms even though five platforms are enabled.
        let settings = try await owner.playbackFeedbackSettings()
        XCTAssertEqual(Set(settings.platforms.keys.map(\.rawValue)), Set(["wy", "tx", "kg"]))
        let stored = try MusicDocument(store.snapshot().0, runID: "fixture").queue["entries"].array[0]["song"]
        let targets = try MusicNativeFeedbackMapping.targets(song: stored, context: .obj())
        XCTAssertEqual(Set(targets.map(\.platform)), Set(MusicAccountPlatform.allCases))

        // KW/MG sources remain durable in the queue and favorites; sync
        // selections are constrained by MusicAccountPlatform itself.
        let queue = try await owner.queue(), track = try XCTUnwrap(queue.entries.first?.track)
        XCTAssertEqual(Set(track.sources.map(\.platform)), Set(["wy", "tx", "kg", "kw", "mg"]))
        let collections = try await owner.collections()
        let favorites = try XCTUnwrap(collections.first { $0.kind == "favorites" })
        let saved = try await owner.editPlaylist(favorites, name: nil, refs: [track.ref], order: nil)
        let tracks = try await owner.tracks(in: saved)
        XCTAssertEqual(Set(tracks.first?.sources.map(\.platform) ?? []), Set(["wy", "tx", "kg", "kw", "mg"]))
        await owner.close()

        // Five-platform enablement must not block or widen account sync: a real
        // selection still syncs, and no KW/MG account identity exists to select.
        let peer = NativeSyncPeer(platform: .netease), syncStore = NativeMusicMemoryDocument()
        let syncOwner = try await service(syncStore, enabled: Set(MusicPublicBrowsePlatform.allCases), accounts: peer)
        let synced = try await syncOwner.synchronizeLibrary(NativeSyncFixture.request(.netease))
        XCTAssertEqual(synced.phase, .synced)
        XCTAssertEqual(synced.selections.map(\.platform), [.netease])
        XCTAssertEqual(MusicAccountPlatform.allCases.map(\.rawValue), ["wy", "tx", "kg"])
        await syncOwner.close()
    }

    func testDisabledPinnedResolveAndLyricsFailBeforeNativeOrScriptCalls() async throws {
        let song = try mixedSong(), store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
        let scripts = MusicPlatformScopeScripts(), accounts = MusicPlatformScopeAccounts(), http = NativeMusicHTTPFixture([])
        // Excluding QQ also checks a genuine official-account path, not just KW/MG.
        let owner = try await service(store, enabled: [.netease, .kugou], scripts: scripts, accounts: accounts, http: http)
        let before = store.snapshot().0
        let session = try await owner.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        for source in try MusicModel.sources(song) where ["tx", "kw", "mg"].contains(source.platform) {
            for installation in [MusicPlatformScopeScripts.installation, "official:scope"] {
                let option = MusicPlaybackOption(installationId: installation, generation: 1, siteId: source.platform,
                    sourceKey: source.sourceKey, qualities: ["128k"], title: "Saved", version: "1",
                    contentHash: String(repeating: "a", count: 64))
                let pinned = MusicCandidateDiscoveryOption(playback: option,
                    revisionHash: installation.hasPrefix("official:") ? nil : String(repeating: "a", count: 64))
                await rejected {
                    try await owner.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: "entry-1",
                                            selection: option.selection(quality: "128k"))
                }
                await rejected { try await session.resolve(pinned.target(quality: "128k"), attemptID: UUID().uuidString.lowercased()) }
            }
            await rejected { try await owner.lyrics(occurrenceId: "entry-1", sourceKey: source.sourceKey) }
        }
        let network = await http.calls(), scriptCalls = await scripts.events(), accountCalls = await accounts.events()
        XCTAssertTrue(network.isEmpty); XCTAssertTrue(scriptCalls.isEmpty); XCTAssertTrue(accountCalls.isEmpty)
        XCTAssertEqual(store.snapshot().0, before); XCTAssertEqual(store.snapshot().2, 0)
        await session.close(retainingAttemptID: nil); await owner.close()
    }

    func testExplicitThreePlatformScopeKeepsOfficialOptionsAndBothResolveEntrypoints() async throws {
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: mixedSong())
        let accounts = MusicPlatformScopeAccounts(), owner = try await service(store, accounts: accounts)
        let availability = try await owner.sourceAvailability(for: .init(kind: .queue, id: "entry-1"))
        XCTAssertEqual(availability.scriptState, .notConfigured)
        XCTAssertEqual(availability.options.count, 3)
        for option in availability.options {
            let candidate = try await owner.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: "entry-1",
                selection: option.selection(quality: "128k"))
            XCTAssertEqual(candidate.startPosition, 0)
            XCTAssertEqual(candidate.url.host, "media.invalid")
        }
        let session = try await owner.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let snapshot = try await session.snapshot(discover: false, force: false)
        for option in snapshot.options {
            let candidate = try await session.resolve(option.target(quality: "128k"), attemptID: UUID().uuidString.lowercased())
            XCTAssertEqual(candidate.startPosition, 0)
        }
        let calls = await accounts.events()
        for platform in ["wy", "tx", "kg"] {
            XCTAssertEqual(calls.filter { $0 == "resolve:" + platform }.count, 2)
        }
        XCTAssertEqual(store.snapshot().2, 0, "obtaining a URL is not a played-history event")
        await session.close(retainingAttemptID: nil); await owner.close()
    }

    func testEmptyScopeDoesNotProbeOrCommitPhantomDiscovery() async throws {
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: mixedSong())
        let scripts = MusicPlatformScopeScripts(), accounts = MusicPlatformScopeAccounts(), http = NativeMusicHTTPFixture([])
        let qq = MusicNativeCandidateDiscoveryQQ()
        let owner = try await service(store, enabled: [], scripts: scripts, accounts: accounts, qq: qq, http: http)
        let before = store.snapshot().0
        let availability = try await owner.sourceAvailability(for: .init(kind: .queue, id: "entry-1"))
        XCTAssertTrue(availability.options.isEmpty)
        let session = try await owner.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let snapshot = try await session.snapshot(discover: true, force: true)
        XCTAssertFalse(snapshot.searched); XCTAssertTrue(snapshot.options.isEmpty)
        XCTAssertEqual(snapshot.track.sources.count, 5)
        try await session.commitDiscovery()
        XCTAssertEqual(store.snapshot().0, before); XCTAssertEqual(store.snapshot().2, 0)
        let network = await http.calls(), qqCalls = await qq.calls()
        let scriptCalls = await scripts.events(), accountCalls = await accounts.events()
        XCTAssertTrue(network.isEmpty); XCTAssertTrue(qqCalls.isEmpty)
        XCTAssertTrue(scriptCalls.isEmpty); XCTAssertTrue(accountCalls.isEmpty)
        await session.close(retainingAttemptID: nil); await owner.close()
    }

    func testScopeChangeKeepsLibraryQueueHistoryIdentitySchemaAndBytesReadable() async throws {
        let song = try mixedSong(), seed = try MusicNativeCandidateDiscoveryFixture.store(song: song)
        var document = try MusicDocument(seed.snapshot().0, runID: "fixture")
        let id = song["songId"].string
        document.library["library"]["songs"] = .obj([(id, song)])
        var favorites = document.library["library"]["collections"].array[0]
        favorites["order"] = .strings([id])
        document.library["library"]["collections"] = .array([favorites])
        document.history["days"] = .array([.obj([("date", .string("2026-10-23")), ("songs", .array([
            .obj([("song", song), ("context", .obj()), ("at", .number(NativeMusicFixture.date.timeIntervalSince1970 * 1000))])
        ]))])])
        try document.validate()
        let bytes = try document.encode(changed: ["library", "queue", "playback", "history"])
        let store = NativeMusicMemoryDocument(bytes)
        for enabled in [threePlatformScope, Set<MusicPublicBrowsePlatform>(), Set(MusicPublicBrowsePlatform.allCases)] {
            let owner = try await service(store, enabled: enabled)
            let collections = try await owner.collections()
            let library = try await owner.tracks(in: XCTUnwrap(collections.first))
            let queue = try await owner.queue(), played = try await owner.playedSongs()
            for track in [try XCTUnwrap(library.first), try XCTUnwrap(queue.entries.first?.track), try XCTUnwrap(played.first?.track)] {
                XCTAssertEqual(track.songId, id)
                XCTAssertEqual(track.sources, try MusicModel.sources(song))
            }
            XCTAssertEqual(queue.entries.first?.id, "entry-1")
            XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, 0)
            await owner.close()
        }
    }

    func testDisabledAccountAndForeignRemoteQueuesRefuseBeforeLeasePageValidationOrFMIO() async throws {
        for platform in MusicAccountPlatform.allCases {
            let accounts = MusicPlatformScopeAccounts(), lease = MusicAccountQueueLease(platform: platform)
            // A queue from another assembly must not bypass this owner's scope.
            let source = MusicRemoteQueueSource(context: .init(kind: .fm, id: "saved", title: "Saved",
                platform: platform.rawValue), authorization: lease.authorization,
                loadPage: { try await accounts.accountQueuePage(lease, kind: .recommendation(kind: "fm"), cursor: $0) },
                validate: { try await accounts.checkAccountQueue(lease) },
                close: { await accounts.endAccountQueue(lease) })
            let seed = try MusicNativeCandidateDiscoveryFixture.store(song: mixedSong())
            var draft = try MusicDocument(seed.snapshot().0, runID: "fixture")
            draft.queue["source"] = .obj([("sourceType", .string("fm")), ("platform", .string(platform.rawValue)),
                ("remoteSession", .string(source.id.uuidString.lowercased()))])
            let store = NativeMusicMemoryDocument(try draft.encode(changed: ["queue", "playback"]))
            let owner = try await service(store, enabled: threePlatformScope.subtracting([.init(accountPlatform: platform)]),
                                          accounts: accounts)
            let before = store.snapshot()
            await rejected { try await owner.openAccountQueue(platform: platform, kind: .collection(id: "favorites"), title: "Saved") }
            await rejected { try await owner.remoteQueuePage(source, cursor: nil) }
            let queue = try await owner.queue(), track = try XCTUnwrap(queue.entries.first?.track)
            await rejected {
                try await owner.replaceRemoteQueue(source, start: .init(index: 0, track: track),
                                                   expectedRevision: queue.revision, intent: .init())
            }
            await rejected {
                try await owner.advanceFM(source, direction: .next, reason: .manual,
                                          expectedRevision: queue.revision, intent: .init())
            }
            let calls = await accounts.events()
            XCTAssertTrue(calls.isEmpty, "no begin, validate, page, or FM request for a disabled platform")
            XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
            XCTAssertTrue(source.authorization.isActive, "rejection must not revoke a foreign owner's lease")
            await source.close(); await owner.close()
        }
    }

    func testExplicitThreePlatformScopeKeepsAccountQueuesAndNativeFMEnabled() async throws {
        for platform in MusicAccountPlatform.allCases {
            let accounts = MusicPlatformScopeAccounts(), owner = try await service(accounts: accounts)
            let source = try await owner.openAccountQueue(platform: platform,
                kind: .recommendation(kind: platform == .qq ? "guess" : "fm"), title: "Synthetic")
            let page = try await owner.remoteQueuePage(source, cursor: nil)
            let track = try XCTUnwrap(page.items.first)
            XCTAssertEqual(track.sources.first?.platform, platform.rawValue)
            let state = try await owner.replaceRemoteQueue(source, start: .init(index: 0, track: track),
                                                          expectedRevision: 0, intent: .init())
            if platform != .qq {
                let next = try await owner.advanceFM(source, direction: .next, reason: .manual,
                                                    expectedRevision: state.revision, intent: .init())
                let queue = try await owner.queue()
                XCTAssertEqual(queue.entries.count, 2)
                XCTAssertEqual(queue.entries.first?.id, state.currentId)
                XCTAssertEqual(next.selectedId, queue.entries.last?.id)
            }
            let calls = await accounts.events()
            XCTAssertEqual(calls.filter { $0 == "queue-begin:" + platform.rawValue }.count, 1)
            XCTAssertEqual(calls.filter { $0 == "queue-page:" + platform.rawValue }.count, platform == .qq ? 1 : 2)
            await source.close(); await owner.close()
        }
    }

    func testSyncRejectsEntireMixedSelectionBeforeCheckpointAndRetainsOldSelection() async throws {
        for platform in MusicAccountPlatform.allCases {
            let peer = NativeSyncPeer(platform: platform), store = NativeMusicMemoryDocument()
            let original = try await service(store, accounts: peer)
            let request = NativeSyncFixture.request(platform)
            let synced = try await original.synchronizeLibrary(request)
            XCTAssertEqual(synced.phase, .synced, "all three account platforms retain native sync")
            await original.close()
            let before = store.snapshot(), callsBefore = await peer.counts()
            let owner = try await service(store, enabled: threePlatformScope.subtracting([.init(accountPlatform: platform)]), accounts: peer)
            let restored = try await owner.librarySyncState()
            XCTAssertEqual(restored.selections, request.selections, "saved preferences stay readable, not rewritten")
            let allowed = try XCTUnwrap(MusicAccountPlatform.allCases.first { $0 != platform })
            await rejected { try await owner.synchronizeLibrary(request) }
            // Enabled first: rejecting only when iterating would already have
            // written a checkpoint or contacted the first account.
            await rejected {
                try await owner.synchronizeLibrary(.init(selections: [
                    .init(platform: allowed, playlists: true), .init(platform: platform, playlists: true)
                ]))
            }
            let callsAfter = await peer.counts(), writes = await peer.calls()
            XCTAssertEqual(callsAfter.0, callsBefore.0); XCTAssertEqual(callsAfter.1, callsBefore.1)
            XCTAssertTrue(writes.isEmpty)
            XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
            await owner.close()
        }
    }

    func testFeedbackFiltersDisabledTargetsAndRechecksAuthorizationWithoutChangingSavedFlags() async throws {
        let scopes: [Set<MusicPublicBrowsePlatform>] = [threePlatformScope, [.netease], [.qq], [.kugou], []]
        for enabled in scopes {
            let seed = try MusicNativeCandidateDiscoveryFixture.store(song: mixedSong())
            var draft = try MusicDocument(seed.snapshot().0, runID: "fixture")
            draft.playback["feedback"] = .obj([("schema", .n(1)), ("platforms", .obj(
                MusicAccountPlatform.allCases.map { ($0.rawValue, .obj([("enabled", .bool(true)), ("generation", .n(1))])) }
            ))])
            let store = NativeMusicMemoryDocument(try draft.encode(changed: ["queue", "playback"]))
            let accounts = MusicPlatformScopeAccounts(), owner = try await service(store, enabled: enabled, accounts: accounts)
            let before = store.snapshot(), epoch = await owner.epoch
            let settings = try await owner.playbackFeedbackSettings()
            XCTAssertEqual(settings.platforms.count, 3)
            XCTAssertTrue(settings.platforms.values.allSatisfy { $0.enabled && $0.generation == 1 })
            for platform in MusicAccountPlatform.allCases where !enabled.contains(.init(accountPlatform: platform)) {
                await rejected { try await owner.checkFeedbackAuthorization(epoch: epoch, platform: platform, generation: 1) }
                await rejected { try await owner.setPlaybackFeedback(platform: platform, enabled: true, expectedRevision: settings.revision) }
            }
            if !enabled.isEmpty {
                let options = (try await owner.sourceAvailability(for: .init(kind: .queue, id: "entry-1"))).options
                let option = try XCTUnwrap(options.first)
                let candidate = try await owner.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: "entry-1",
                                                        selection: option.selection(quality: "128k"))
                let controller = await owner.playbackFeedback
                await owner.consumePlaybackFeedback(NativeFeedbackFixture.sample(1, at: 0, position: 0, kind: .playing,
                                                                                attempt: candidate.attemptId))
                await controller.flush()
                await owner.consumePlaybackFeedback(NativeFeedbackFixture.sample(2, at: 1, position: 1, kind: .stopped,
                                                                                running: false, attempt: candidate.attemptId))
                await controller.flush()
            }
            let calls = await accounts.events()
            XCTAssertEqual(Set(calls.filter { $0.hasPrefix("feedback-begin:") }),
                           Set(enabled.map { "feedback-begin:" + $0.rawValue }))
            XCTAssertEqual(Set(calls.filter { $0.hasPrefix("feedback-start:") }),
                           Set(enabled.map { "feedback-start:" + $0.rawValue }))
            XCTAssertEqual(Set(calls.filter { $0.hasPrefix("feedback-end:") }),
                           Set(enabled.map { "feedback-end:" + $0.rawValue }))
            XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
            await owner.close()
        }
    }
}

private actor MusicPlatformScopeAccounts: MusicNativeAccountProviding, MusicNativeAccountQueueProviding, MusicNativeFeedbackProviding {
    private var calls: [String] = []
    func events() -> [String] { calls }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) -> MusicNativeTrackPage {
        calls.append("recommendations:" + platform.rawValue)
        return .init(items: [])
    }
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) -> MusicAccountCollectionPage {
        calls.append("collections:" + platform.rawValue)
        return .init(items: [], cursor: nil)
    }
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) -> MusicNativeTrackPage {
        calls.append("tracks:" + platform.rawValue)
        return .init(items: [])
    }
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) -> [MusicPlaybackOption] {
        calls.append("options:" + platform.rawValue)
        return [.init(installationId: "official:scope", generation: 1, siteId: platform.rawValue, sourceKey: sourceKey,
            qualities: ["128k"], title: "Synthetic official", version: "1", contentHash: String(repeating: "a", count: 64))]
    }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection) -> MusicNativeAudio {
        calls.append("resolve:" + platform.rawValue)
        return .init(url: URL(string: "https://media.invalid/scoped.mp3")!, expiresAt: NativeMusicFixture.date.addingTimeInterval(60))
    }
    func beginAccountQueue(platform: MusicAccountPlatform) -> MusicAccountQueueLease {
        calls.append("queue-begin:" + platform.rawValue)
        return .init(platform: platform)
    }
    func checkAccountQueue(_ lease: MusicAccountQueueLease) throws {
        calls.append("queue-check:" + lease.platform.rawValue)
        try lease.authorization.check()
    }
    func accountQueuePage(_ lease: MusicAccountQueueLease, kind: MusicAccountQueueKind, cursor: String?) throws -> MusicNativeTrackPage {
        calls.append("queue-page:" + lease.platform.rawValue)
        try lease.authorization.check()
        let id = lease.platform == .kugou ? String(repeating: "A", count: 32) : lease.platform == .qq ? "SeedMID" : "321"
        return .init(items: [MusicNativeCandidateDiscoveryFixture.track(lease.platform.rawValue, id: id)])
    }
    func endAccountQueue(_ lease: MusicAccountQueueLease) {
        calls.append("queue-end:" + lease.platform.rawValue)
        lease.revoke()
    }
    func beginPlaybackFeedback(platform: MusicAccountPlatform,
                               authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicPlaybackFeedbackLease {
        calls.append("feedback-begin:" + platform.rawValue)
        try await authorization.check()
        return .init(platform: platform, scope: "synthetic:" + platform.rawValue, generation: UUID(),
                     authorization: authorization, send: { [weak self] value in
            guard let self else { throw MusicServiceError.closed }
            return await self.feedbackReport(value)
        })
    }
    private func feedbackReport(_ value: MusicPlaybackFeedbackReport) -> MusicPlaybackFeedbackReceipt {
        calls.append((value.event == .start ? "feedback-start:" : "feedback-end:") + value.target.platform.rawValue)
        return .confirmed
    }
}

private actor MusicPlatformScopeScripts: MusicScriptResolving {
    static let installation = "44444444-4444-4444-8444-444444444444"
    private var calls: [String] = []
    func events() -> [String] { calls }
    private func snapshot() -> MusicScriptSource {
        .init(installationID: Self.installation, generation: 1, enabled: true, ready: true, version: "1",
            contentHash: String(repeating: "a", count: 64), revisionHash: String(repeating: "a", count: 64),
            capabilities: MusicPublicBrowsePlatform.allCases.map {
                .init(siteID: $0.rawValue, title: "Synthetic script", actions: ["musicUrl", "lyric"], qualities: ["128k"])
            })
    }
    func sources() -> [MusicScriptSource] { calls.append("sources"); return [snapshot()] }
    func source(installationID: String) throws -> MusicScriptSource {
        calls.append("source")
        guard installationID == Self.installation else { throw MusicServiceError.sourceUnavailable }
        return snapshot()
    }
    func resolve(request: MusicScriptRequest) -> MusicNativeAudio {
        calls.append("resolve:" + request.siteID)
        return .init(url: URL(string: "https://media.invalid/scoped.mp3")!)
    }
    func lyrics(request: MusicScriptRequest) -> MusicNativeLyrics {
        calls.append("lyrics:" + request.siteID)
        return .init(text: "[00:01.000]Synthetic")
    }
}
