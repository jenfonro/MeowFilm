import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

private enum MetadataNativeFixture {
    static let all: [MusicPublicBrowsePlatform] = [.netease, .qq, .kugou, .kuwo, .migu]
    static let threePlatformScope: Set<MusicPublicBrowsePlatform> = [.netease, .qq, .kugou]

    static func row(_ platform: MusicPublicBrowsePlatform, _ id: String,
                    title: String = "Recording", duration: Int = 180) throws -> MJ {
        let hosts: [MusicPublicBrowsePlatform: String] = [
            .netease: "p1.music.126.net", .qq: "y.gtimg.cn", .kugou: "imge.kugou.com",
            .kuwo: "img.kuwo.cn", .migu: "d.musicapp.migu.cn"
        ]
        let cover = "https://" + hosts[platform]! + "/" + id + ".jpg"
        var info = MJ.obj([("source", .string(platform.rawValue)), ("songmid", .string(id)),
            ("name", .string(title)), ("singer", .string("Fixture")),
            ("albumName", .string("Synthetic album")), ("duration", .n(duration)), ("img", .string(cover))])
        if platform == .kugou {
            info["hash"] = .string(String(repeating: "a", count: 30) + String(id.suffix(2)))
            info["albumAudioId"] = .string(id)
        }
        return .obj([("sourceKey", .string(try MusicModel.sourceKey(info))), ("source", .string(platform.rawValue)),
            ("musicInfo", info)])
    }

    static func song(_ rows: [MJ]) throws -> MJ {
        let primary = try row(.qq, "AudioMID")
        return .obj([("songId", .string(UUID().uuidString.lowercased())), ("title", .string("Recording")),
            ("artist", .string("Fixture")), ("album", .string("Synthetic album")), ("duration", .n(180)),
            ("baselineDuration", .n(180)),
            ("cover_url", primary["musicInfo"]["img"]), ("sources", .array(rows))])
    }

    static func document(song: MJ? = nil, playback: MJ = MusicModel.playback) throws -> Data {
        var d = try MusicDocument(nil, runID: "metadata-fixture")
        d.playback = playback
        if let song {
            d.library["library"]["songs"][song["songId"].string] = song
            d.queue["entries"] = .array([
                .obj([("id", .string("entry-1")), ("songId", song["songId"])]),
                .obj([("id", .string("entry-2")), ("songId", song["songId"])])
            ])
            d.queue["serial"] = .n(2); d.queue["revision"] = .n(3)
            d.queue["currentId"] = .string("entry-1")
            d.playback["position"] = .obj([("occurrenceId", .string("entry-1")), ("seconds", .n(23))])
        }
        return try d.encode(changed: ["library", "queue", "playback", "history"])
    }

    static func service(_ store: NativeMusicTestLibrary,
                        enabled: Set<MusicPublicBrowsePlatform> = threePlatformScope,
                        probe: MetadataNetworkProbe = MetadataNetworkProbe()) async throws -> NativeMusicService {
        let service = NativeMusicService(database: store,
            scripts: probe,
            platformClient: try NativeMusicFixture.routes(primary: probe, others: probe).makeClient(),
            enabledPlatforms: enabled,
            clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        return service
    }
}

/// Every possible provider entry point is observable. Candidate enumeration
/// must not probe scripts, search a title, fetch artwork or resolve audio.
private actor MetadataNetworkProbe: MusicPlatformFixtureResponding, MusicScriptResolving {
    private var events: [String] = []
    func respond(_ request: MusicPlatformFixture.Request) throws -> RuntimeValue {
        events.append(request.operation); throw MusicServiceError.unavailable
    }
    func sources() throws -> [MusicScriptSource] {
        events.append("scripts"); throw MusicServiceError.unavailable
    }
    func source(installationID: String) throws -> MusicScriptSource {
        events.append("script"); throw MusicServiceError.unavailable
    }
    func resolve(request: MusicScriptRequest) throws -> MusicScriptResult<MusicNativeAudio> {
        events.append("resolve"); throw MusicServiceError.unavailable
    }
    func lyrics(request: MusicScriptRequest) throws -> MusicScriptResult<MusicNativeLyrics> {
        events.append("script-lyrics"); throw MusicServiceError.unavailable
    }
    func calls() -> [String] { events }
}

final class MusicNativeMetadataPreferencesTests: XCTestCase {
    private func expect(_ expected: MusicServiceError, file: StaticString = #filePath, line: UInt = #line,
                        _ action: () async throws -> Void) async {
        do { try await action(); XCTFail("Expected \(expected)", file: file, line: line) }
        catch { XCTAssertEqual(error as? MusicServiceError, expected, file: file, line: line) }
    }

    func testMissingFieldsUseOriginalOrderWithoutCreatingDocumentOrEnablingExcludedPlatforms() async throws {
        let store = NativeMusicTestLibrary(), probe = MetadataNetworkProbe()
        let service = try await MetadataNativeFixture.service(store, probe: probe)
        let value = try await service.metadataPreferences()
        XCTAssertEqual(value.platforms, [.netease, .qq, .kugou])
        XCTAssertEqual(value.enabledPlatforms, MetadataNativeFixture.threePlatformScope)
        XCTAssertEqual(value.revision, 0)
        XCTAssertNil(store.snapshot().0); XCTAssertEqual(store.snapshot().2, 0)
        let calls = await probe.calls(); XCTAssertTrue(calls.isEmpty)
        await service.close()
    }

    func testVisiblePatchPreservesUnavailableSlotsDisabledStateAndEveryOtherRecord() async throws {
        var playback = MusicModel.playback
        playback["metadataPlatforms"] = .strings(["kw", "tx", "mg", "wy", "kg"])
        playback["metadataEnabled"] = .strings(["kw", "tx", "wy"])
        playback["retainedPreference"] = .string("unrelated-value")
        let bytes = try MetadataNativeFixture.document(playback: playback)
        let before = try MJ.parse(bytes), store = NativeMusicTestLibrary(bytes)
        let service = try await MetadataNativeFixture.service(store)
        let value = try await service.updateMetadataPreferences(.init(ifRevision: 0,
            platforms: [.kugou, .netease, .qq], enabledPlatforms: [.kugou]))
        XCTAssertEqual(value.platforms, [.kugou, .netease, .qq])
        XCTAssertEqual(value.enabledPlatforms, [.kugou]); XCTAssertEqual(value.revision, 1)
        let after = try MJ.parse(XCTUnwrap(store.snapshot().0))
        for record in ["library", "queue", "history"] {
            XCTAssertEqual(after["areas"][record], before["areas"][record], record)
        }
        XCTAssertEqual(Set(after["areas"].fields.map(\.0)), Set(before["areas"].fields.map(\.0)))
        var expected = before["areas"]["playback"]
        expected["metadataPlatforms"] = .strings(["kw", "kg", "mg", "wy", "tx"])
        expected["metadataEnabled"] = .strings(["kg", "kw"]); expected["revision"] = .n(1)
        XCTAssertEqual(after["areas"]["playback"], expected)
        let full = try await MetadataNativeFixture.service(store, enabled: Set(MetadataNativeFixture.all))
        let restored = try await full.metadataPreferences()
        XCTAssertEqual(restored.platforms, [.kuwo, .kugou, .migu, .netease, .qq])
        XCTAssertEqual(restored.enabledPlatforms, [.kuwo, .kugou])
        await full.close(); await service.close()
    }

    func testMetadataAndExistingSettingsShareRevisionAndDoNotEraseEachOther() async throws {
        let store = NativeMusicTestLibrary(), service = try await MetadataNativeFixture.service(store)
        _ = try await service.updateMetadataPreferences(.init(ifRevision: 0,
            platforms: [.kugou, .qq, .netease], enabledPlatforms: []))
        let envelope = try MJ.parse(XCTUnwrap(store.snapshot().0))
        XCTAssertEqual(envelope["areas"]["playback"]["schema"], .n(1))
        await expect(.staleRevision) { _ = try await service.updateSettings(.init(ifRevision: 0, lyricsEnabled: false)) }
        let state = try await service.updateSettings(.init(ifRevision: 1, lyricsEnabled: false))
        XCTAssertEqual(state.settingsRevision, 2)
        let restored = try await service.metadataPreferences()
        XCTAssertEqual(restored.revision, 2); XCTAssertEqual(restored.platforms, [.kugou, .qq, .netease])
        XCTAssertTrue(restored.enabledPlatforms.isEmpty)
        let before = store.snapshot()
        await expect(.staleRevision) {
            _ = try await service.updateMetadataPreferences(.init(ifRevision: 1, enabledPlatforms: [.qq]))
        }
        let noOp = try await service.updateMetadataPreferences(.init(ifRevision: 2))
        XCTAssertEqual(noOp, restored); XCTAssertEqual(store.snapshot().0, before.0)
        XCTAssertEqual(store.snapshot().2, before.2)
        await service.close()
    }

    func testEmptyEnabledIsAuthoritativeAndDoesNotFallBackToPrimaryAudioMapping() async throws {
        var playback = MusicModel.playback
        playback["metadataEnabled"] = .array([])
        let song = try MetadataNativeFixture.song([MetadataNativeFixture.row(.qq, "AudioMID")])
        let bytes = try MetadataNativeFixture.document(song: song, playback: playback)
        let store = NativeMusicTestLibrary(bytes), service = try await MetadataNativeFixture.service(store)
        let result = try await service.metadataCandidates(occurrenceID: "entry-1")
        XCTAssertEqual(result.preferences.platforms, [.netease, .qq, .kugou])
        XCTAssertTrue(result.preferences.enabledPlatforms.isEmpty)
        XCTAssertTrue(result.candidates.isEmpty); XCTAssertEqual(result.track.sources.count, 1)
        XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, 0)
        await service.close()
    }

    func testMalformedOptionalFieldsAreNotSilentlyResetOrOverwritten() async throws {
        let invalid: [(String, MJ)] = [
            ("metadataPlatforms", .array([])), ("metadataPlatforms", .strings(["wy", "tx", "kg"])),
            ("metadataPlatforms", .strings(["wy", "tx", "kg", "kw", "kw"])),
            ("metadataPlatforms", .strings(["wy", "tx", "kg", "kw", "other"])),
            ("metadataPlatforms", .null), ("metadataEnabled", .bool(false)),
            ("metadataEnabled", .strings(["wy", "wy"])), ("metadataEnabled", .strings(["unknown"])),
            ("metadataEnabled", .array([.n(0)]))
        ]
        for (field, value) in invalid {
            var playback = MusicModel.playback; playback[field] = value
            let bytes = try MetadataNativeFixture.document(playback: playback)
            let store = NativeMusicTestLibrary(bytes), service = try await MetadataNativeFixture.service(store)
            await expect(.corruptDocument) { _ = try await service.metadataPreferences() }
            await expect(.corruptDocument) {
                _ = try await service.updateMetadataPreferences(.init(ifRevision: 0,
                    platforms: [.netease, .qq, .kugou], enabledPlatforms: []))
            }
            XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, 0)
            await service.close()
        }
    }

    func testInvalidPatchesAndStorageFailureDoNotPartiallyCommit() async throws {
        let store = NativeMusicTestLibrary(), service = try await MetadataNativeFixture.service(store)
        for change in [
            MusicMetadataPreferencesChange(ifRevision: -1, enabledPlatforms: []),
            .init(ifRevision: 0, platforms: [.qq, .qq, .netease]),
            .init(ifRevision: 0, platforms: [.qq]),
            .init(ifRevision: 0, platforms: [.netease, .qq, .kuwo]),
            .init(ifRevision: 0, enabledPlatforms: [.migu])
        ] {
            await expect(.invalidRequest) { _ = try await service.updateMetadataPreferences(change) }
        }
        for failure in [MusicServiceError.storageConflict, .storageFailed] {
            store.fail(failure)
            await expect(failure) {
                _ = try await service.updateMetadataPreferences(.init(ifRevision: 0, enabledPlatforms: []))
            }
        }
        XCTAssertNil(store.snapshot().0); XCTAssertEqual(store.snapshot().2, 0)
        store.fail(nil)
        let value = try await service.metadataPreferences(); XCTAssertEqual(value.revision, 0)
        await service.close()

        var playback = MusicModel.playback; playback["revision"] = .n(9_007_199_254_740_991)
        let maxStore = NativeMusicTestLibrary(try MetadataNativeFixture.document(playback: playback))
        let maxService = try await MetadataNativeFixture.service(maxStore)
        await expect(.limit) {
            _ = try await maxService.updateMetadataPreferences(.init(ifRevision: 9_007_199_254_740_991, enabledPlatforms: []))
        }
        XCTAssertEqual(maxStore.snapshot().2, 0); await maxService.close()
    }

    func testCandidatesUseMetadataOrderAndProductScopeWithoutCountOrCompatibilityGate() async throws {
        var rows = try MetadataNativeFixture.all.flatMap { platform in
            try ["03", "01", "02"].map { try MetadataNativeFixture.row(platform, $0) }
        }
        rows.append(try MetadataNativeFixture.row(.qq, "04"))
        rows.append(try MetadataNativeFixture.row(.kugou, "04", title: "Corrected title", duration: 240))
        rows.append(try MetadataNativeFixture.row(.netease, "04", title: "Recording (Live)"))
        var playback = MusicModel.playback
        playback["metadataPlatforms"] = .strings(["kg", "wy", "kw", "tx", "mg"])
        playback["metadataEnabled"] = .strings(["kg", "wy", "kw", "mg"])
        let song = try MetadataNativeFixture.song(rows)
        let bytes = try MetadataNativeFixture.document(song: song, playback: playback)
        XCTAssertNoThrow(try MusicDocument(bytes, runID: "fixture"))
        let store = NativeMusicTestLibrary(bytes), probe = MetadataNetworkProbe()
        let service = try await MetadataNativeFixture.service(store, probe: probe)
        let result = try await service.metadataCandidates(occurrenceID: "entry-1")
        XCTAssertEqual(result.candidates.map(\.source.platform), Array(repeating: "kg", count: 4) + Array(repeating: "wy", count: 4))
        let expected = ["kg", "wy"].flatMap { platform in
            rows.filter { $0["source"].string == platform }.sorted { $0["sourceKey"].string < $1["sourceKey"].string }
        }
        XCTAssertEqual(result.candidates.map(\.source.sourceKey), expected.map { $0["sourceKey"].string })
        XCTAssertEqual(result.candidates.map { $0.artwork?.absoluteString },
                       expected.map { Optional($0["musicInfo"]["img"].string) })
        XCTAssertEqual(result.track.sources.count, 18)
        XCTAssertTrue(result.track.sources.contains { $0.platform == "tx" && $0.nativeId == "04" })
        XCTAssertEqual(result.track.ref, .init(kind: .queue, id: "entry-1"))
        XCTAssertEqual(result.queueRevision, 3)
        let attempt = await service.attempt; XCTAssertNil(attempt)
        let calls = await probe.calls(); XCTAssertTrue(calls.isEmpty)
        XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, 0)
        await service.close()

    }

    func testEveryKnownFivePlatformCandidateKeepsItsOwnArtwork() async throws {
        var rows = try MetadataNativeFixture.all.flatMap { p in
            try ["03", "01", "02"].map { try MetadataNativeFixture.row(p, $0) }
        }
        // Missing row artwork must not fall through to another platform or
        // the primary audio artwork; a valid independent CDN URL is retained.
        rows[1]["musicInfo"]["img"] = .string("")
        rows[2]["musicInfo"]["img"] = .string("https://independent-cdn.invalid/image.jpg")
        let store = NativeMusicTestLibrary(try MetadataNativeFixture.document(song: MetadataNativeFixture.song(rows)))
        let service = try await MetadataNativeFixture.service(store, enabled: Set(MetadataNativeFixture.all))
        let result = try await service.metadataCandidates(occurrenceID: "entry-1")
        XCTAssertEqual(result.candidates.count, 15)
        XCTAssertEqual(result.candidates.map(\.source.platform), ["wy", "tx", "kg", "kw", "mg"].flatMap { Array(repeating: $0, count: 3) })
        XCTAssertNil(result.candidates[0].artwork)
        XCTAssertEqual(result.candidates[1].artwork, URL(string: "https://independent-cdn.invalid/image.jpg"))
        XCTAssertEqual(result.candidates[3].artwork?.host, "y.gtimg.cn")
        XCTAssertEqual(result.candidates[9].artwork?.host, "img.kuwo.cn")
        XCTAssertEqual(result.candidates[12].artwork?.host, "d.musicapp.migu.cn")
        XCTAssertFalse(result.candidates.compactMap { $0.artwork?.absoluteString }
            .contains("https://y.gtimg.cn/AudioMID.jpg"), "primary audio artwork is not a candidate cover")
        await service.close()
    }

    func testCandidateArtworkAcceptsIndependentCDNsAndRejectsUnsafeURLsWithoutFallback() async throws {
        for unsafe in ["https://user@independent-cdn.invalid/cover.jpg",
                       "https://independent-cdn.invalid:444/cover.jpg",
                       "https://independent-cdn.invalid/cover.jpg#fragment",
                       "file:///cover.jpg", "javascript:alert(1)"] {
            let rows = try MetadataNativeFixture.all.flatMap { platform -> [MJ] in
                var accepted = try MetadataNativeFixture.row(platform, "01")
                var rejected = try MetadataNativeFixture.row(platform, "02")
                accepted["musicInfo"]["img"] = .string("http://independent-cdn.invalid/\(platform.rawValue).jpg?param=800y800&extra=a%2Fb")
                rejected["musicInfo"]["img"] = .string(unsafe)
                return [accepted, rejected]
            }
            let bytes = try MetadataNativeFixture.document(song: MetadataNativeFixture.song(rows))
            let store = NativeMusicTestLibrary(bytes), probe = MetadataNetworkProbe()
            let service = try await MetadataNativeFixture.service(store, enabled: Set(MetadataNativeFixture.all), probe: probe)
            let result = try await service.metadataCandidates(occurrenceID: "entry-1")
            XCTAssertEqual(result.candidates.count, 10)
            for platform in MetadataNativeFixture.all {
                let candidates = result.candidates.filter { $0.source.platform == platform.rawValue }
                XCTAssertEqual(candidates.count, 2)
                let accepted = try XCTUnwrap(candidates.first), rejected = try XCTUnwrap(candidates.last)
                XCTAssertEqual(accepted.artwork?.absoluteString,
                               "https://independent-cdn.invalid/\(platform.rawValue).jpg?param=800y800&extra=a%2Fb")
                XCTAssertNil(rejected.artwork, "\(platform.rawValue): \(unsafe)")
            }
            let calls = await probe.calls(); XCTAssertTrue(calls.isEmpty)
            XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, 0)
            await service.close()
        }
    }

    func testExplicitEmptySourcesDoNotResurrectSongLevelPrimaryOrWrite() async throws {
        let song = try MetadataNativeFixture.song([])
        XCTAssertEqual(song["sources"], .array([]))
        let bytes = try MetadataNativeFixture.document(song: song)
        let store = NativeMusicTestLibrary(bytes), service = try await MetadataNativeFixture.service(store)
        let result = try await service.metadataCandidates(occurrenceID: "entry-1")
        XCTAssertTrue(result.candidates.isEmpty)
        XCTAssertTrue(result.track.sources.isEmpty)
        XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, 0)
        await service.close()
    }

    func testMissingSourcesFailAtDocumentReadBoundaryWithoutWriting() async throws {
        var song = try MetadataNativeFixture.song([])
        song.remove("sources")
        let bytes = try MetadataNativeFixture.document(song: song)
        let store = NativeMusicTestLibrary(bytes), service = try await MetadataNativeFixture.service(store)
        await expect(.corruptDocument) { _ = try await service.metadataCandidates(occurrenceID: "entry-1") }
        XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, 0)
        await service.close()
    }

    func testOccurrencesAndPolicyReadOnePermanentRepositoryWithoutQueueRewrite() async throws {
        let row = try MetadataNativeFixture.row(.qq, "AudioMID")
        let song = try MetadataNativeFixture.song([row])
        let store = NativeMusicTestLibrary(try MetadataNativeFixture.document(song: song))
        let service = try await MetadataNativeFixture.service(store)
        let first = try await service.metadataCandidates(occurrenceID: "entry-1")
        _ = try await service.changeQueue(.select("entry-2"), expectedRevision: 3)
        _ = try await service.updateMetadataPreferences(.init(ifRevision: 0, platforms: [.qq, .netease, .kugou]))
        let second = try await service.metadataCandidates(occurrenceID: "entry-2")
        XCTAssertEqual(first.track.songId, second.track.songId)
        XCTAssertEqual(first.track.sources, second.track.sources)
        XCTAssertEqual(second.queueRevision, 4)
        let current = try await service.read()
        XCTAssertTrue(current.queue["entries"].array.allSatisfy { $0["song"] == .null && $0["songId"].string == first.track.songId })
        XCTAssertEqual((try current.fixtureSongs()).fields.count, 1)
        XCTAssertEqual(second.preferences.platforms, [.qq, .netease, .kugou])
        await expect(.targetChanged) { _ = try await service.metadataCandidates(occurrenceID: "entry-1") }
        await service.close()
        let cold = try await MetadataNativeFixture.service(store)
        let restored = try await cold.metadataCandidates(occurrenceID: "entry-2")
        XCTAssertEqual(restored.track.songId, first.track.songId)
        XCTAssertEqual(restored.track.artwork, second.track.artwork)
        await cold.close()
    }

    func testArtworkSelectionKeepsEntranceUntilExplicitPolicyChangeAndPersistsAcrossColdRead() async throws {
        let primary = try MetadataNativeFixture.row(.qq, "AudioMID")
        var song = try MetadataNativeFixture.song([primary])
        song["artworkSourceKey"] = primary["sourceKey"]
        let store = NativeMusicTestLibrary(try MetadataNativeFixture.document(song: song))
        let service = try await MetadataNativeFixture.service(store)
        let original = try await service.queue()
        let peer = try MetadataNativeFixture.row(.netease, "Peer")
        let incoming = MJ.obj([("title", song["title"]), ("artist", song["artist"]), ("duration", song["duration"]),
            ("cover_url", peer["musicInfo"]["img"]), ("sources", .array([peer]))])
        _ = try await service.adoptSongs([incoming], authoritative: true)
        let beforeChange = try await service.repositoryTracks(songIDs: [song["songId"].string])
        XCTAssertEqual(beforeChange[song["songId"].string]?.artwork?.absoluteString, primary["musicInfo"]["img"].string)
        _ = try await service.updateMetadataPreferences(.init(ifRevision: 0, platforms: [.netease, .kugou, .qq]))
        let selected = try await service.repositoryTracks(songIDs: [song["songId"].string])
        XCTAssertEqual(selected[song["songId"].string]?.artwork?.absoluteString, peer["musicInfo"]["img"].string)
        let after = try await service.queue()
        XCTAssertEqual(after.revision, original.revision); XCTAssertEqual(after.currentId, original.currentId)
        XCTAssertEqual(after.entries.map(\.id), original.entries.map(\.id))
        XCTAssertEqual(Set(after.entries.map { $0.track.songId }), [song["songId"].string])
        XCTAssertTrue(after.entries.allSatisfy { $0.track.artwork == selected[song["songId"].string]?.artwork })
        let failedURL = try XCTUnwrap(selected[song["songId"].string]?.artwork)
        let fallback = try await service.failedArtwork(songID: song["songId"].string, url: failedURL, attempted: [failedURL])
        XCTAssertEqual(fallback?.absoluteString, primary["musicInfo"]["img"].string)
        // The old request cannot overwrite the now-current fallback.
        let late = try await service.succeededArtwork(songID: song["songId"].string, url: failedURL, replacing: failedURL)
        XCTAssertEqual(late, fallback)
        let beforeUnsafe = store.snapshot()
        let credentialURL = try XCTUnwrap(URL(string: primary["musicInfo"]["img"].string + "?access_token=synthetic"))
        let unsafe = try await service.succeededArtwork(songID: song["songId"].string, url: credentialURL,
                                                        replacing: try XCTUnwrap(fallback))
        XCTAssertEqual(unsafe, fallback)
        XCTAssertEqual(store.snapshot().0, beforeUnsafe.0); XCTAssertEqual(store.snapshot().2, beforeUnsafe.2)
        let beforeFailure = store.snapshot()
        store.fail(.storageConflict)
        await expect(.storageConflict) {
            _ = try await service.updateMetadataPreferences(.init(ifRevision: 1, enabledPlatforms: [.netease]))
        }
        XCTAssertEqual(store.snapshot().0, beforeFailure.0); XCTAssertEqual(store.snapshot().2, beforeFailure.2)
        store.fail(nil)
        await service.close()
        let cold = try await MetadataNativeFixture.service(store)
        let restored = try await cold.queue()
        XCTAssertEqual(restored.revision, original.revision)
        XCTAssertEqual(restored.entries.map { $0.track.songId }, after.entries.map { $0.track.songId })
        XCTAssertTrue(restored.entries.allSatisfy { $0.track.artwork == fallback })
        await cold.close()
    }

    func testProtectionRevocationAndCancelledCallerDoNotReadOrWrite() async throws {
        let store = NativeMusicTestLibrary(try MetadataNativeFixture.document(song: MetadataNativeFixture.song([])))
        let service = try await MetadataNativeFixture.service(store)
        service.revokeAccess(generation: 2)
        let before = store.snapshot()
        await expect(.accessUnavailable) { _ = try await service.metadataPreferences() }
        await expect(.accessUnavailable) { _ = try await service.metadataCandidates(occurrenceID: "entry-1") }
        await expect(.accessUnavailable) { _ = try await service.updateMetadataPreferences(.init(ifRevision: 0, enabledPlatforms: [])) }
        XCTAssertEqual(store.snapshot().1, before.1); XCTAssertEqual(store.snapshot().2, 0)
        try await service.setAccess(available: false, generation: 2)
        try await service.setAccess(available: true, generation: 3)
        let gate = NativeMusicGate()
        let task = Task {
            await gate.wait()
            return try await service.updateMetadataPreferences(.init(ifRevision: 0, enabledPlatforms: []))
        }
        await gate.started(); task.cancel(); await gate.release()
        await expect(.cancelled) { _ = try await task.value }
        XCTAssertEqual(store.snapshot().2, 0)
        await expect(.invalidRequest) { _ = try await service.metadataCandidates(occurrenceID: "") }
        await service.close()
        await expect(.closed) { _ = try await service.metadataPreferences() }
    }

    func testCommittedPolicyChangeRevokesAnAlreadyRunningLyricRequest() async throws {
        let song = try MetadataNativeFixture.song([MetadataNativeFixture.row(.qq, "AudioMID")])
        let store = NativeMusicTestLibrary(try MetadataNativeFixture.document(song: song))
        let gate = NativeMusicGate()
        let qq = NativeMusicResultFixture([.object(["text": .string("[00:01.000]Synthetic")])], gate: gate)
        let service = try await NativeMusicFixture.service(store, platform: qq)
        let snapshot = try await service.metadataCandidates(occurrenceID: "entry-1")
        let source = try XCTUnwrap(snapshot.candidates.first?.source)
        let request = Task { try await service.lyrics(occurrenceId: "entry-1", sourceKey: source.sourceKey) }
        await gate.started()
        _ = try await service.updateMetadataPreferences(.init(ifRevision: 0, enabledPlatforms: []))
        await gate.release()
        await expect(.cancelled) { _ = try await request.value }
        await qq.waitUntilIdle()
        let state = try await service.state()
        XCTAssertEqual(state.currentId, "entry-1"); XCTAssertEqual(state.position, 0)
        await service.close()
    }
}
