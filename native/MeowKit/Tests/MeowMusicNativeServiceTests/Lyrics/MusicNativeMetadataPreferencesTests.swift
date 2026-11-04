import Foundation
import XCTest
import MeowMusicFeature
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
        return .obj([("songId", .string("song:metadata-fixture")), ("title", .string("Recording")),
            ("artist", .string("Fixture")), ("album", .string("Synthetic album")), ("duration", .n(180)),
            ("cover_url", primary["musicInfo"]["img"]), ("sources", .array(rows))])
    }

    static func document(song: MJ? = nil, playback: MJ = MusicModel.playback) throws -> Data {
        var d = try MusicDocument(nil, runID: "metadata-fixture")
        d.playback = playback
        if let song {
            d.queue["entries"] = .array([
                .obj([("id", .string("entry-1")), ("song", song)]),
                .obj([("id", .string("entry-2")), ("song", song)])
            ])
            d.queue["serial"] = .n(2); d.queue["revision"] = .n(3)
            d.queue["currentId"] = .string("entry-1")
            d.playback["position"] = .obj([("occurrenceId", .string("entry-1")), ("seconds", .n(23))])
        }
        return try d.encode(changed: ["library", "queue", "playback", "history"])
    }

    static func service(_ store: NativeMusicMemoryDocument,
                        enabled: Set<MusicPublicBrowsePlatform> = threePlatformScope,
                        probe: MetadataNetworkProbe = MetadataNetworkProbe()) async throws -> NativeMusicService {
        let service = NativeMusicService(document: store, scripts: probe, qq: probe,
            enabledPlatforms: enabled, transport: probe, clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        return service
    }
}

/// Every possible provider entry point is observable. Candidate enumeration
/// must not probe scripts, search a title, fetch artwork or resolve audio.
private actor MetadataNetworkProbe: MusicNativeHTTPTransport, MusicPublicQQProviding, MusicScriptResolving {
    private var events: [String] = []
    func send(_ request: MusicNativeHTTPRequest) throws -> MusicNativeHTTPResponse {
        events.append("http"); throw MusicServiceError.unavailable
    }
    func search(query: String, page: Int, limit: Int) throws -> MusicNativeTrackPage {
        events.append("search"); throw MusicServiceError.unavailable
    }
    func lyrics(mid: String) throws -> MusicNativeLyrics {
        events.append("lyrics"); throw MusicServiceError.unavailable
    }
    func sources() throws -> [MusicScriptSource] {
        events.append("scripts"); throw MusicServiceError.unavailable
    }
    func source(installationID: String) throws -> MusicScriptSource {
        events.append("script"); throw MusicServiceError.unavailable
    }
    func resolve(request: MusicScriptRequest) throws -> MusicNativeAudio {
        events.append("resolve"); throw MusicServiceError.unavailable
    }
    func lyrics(request: MusicScriptRequest) throws -> MusicNativeLyrics {
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
        let store = NativeMusicMemoryDocument(), probe = MetadataNetworkProbe()
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
        playback["retainedPreference"] = .string("legacy-value")
        let bytes = try MetadataNativeFixture.document(song: MetadataNativeFixture.song([]), playback: playback)
        let before = try MJ.parse(bytes), store = NativeMusicMemoryDocument(bytes)
        let service = try await MetadataNativeFixture.service(store)
        let value = try await service.updateMetadataPreferences(.init(ifRevision: 0,
            platforms: [.kugou, .netease, .qq], enabledPlatforms: [.kugou]))
        XCTAssertEqual(value.platforms, [.kugou, .netease, .qq])
        XCTAssertEqual(value.enabledPlatforms, [.kugou]); XCTAssertEqual(value.revision, 1)
        let after = try MJ.parse(XCTUnwrap(store.snapshot().0))
        for record in ["library", "queue", "history"] {
            XCTAssertEqual(after["records"][MusicModel.key(record)], before["records"][MusicModel.key(record)], record)
        }
        XCTAssertEqual(Set(after["records"].fields.map(\.0)), Set(before["records"].fields.map(\.0)))
        var expected = before["records"][MusicModel.key("playback")]
        expected["metadataPlatforms"] = .strings(["kw", "kg", "mg", "wy", "tx"])
        expected["metadataEnabled"] = .strings(["kg", "kw"]); expected["revision"] = .n(1)
        XCTAssertEqual(after["records"][MusicModel.key("playback")], expected)
        let full = try await MetadataNativeFixture.service(store, enabled: Set(MetadataNativeFixture.all))
        let restored = try await full.metadataPreferences()
        XCTAssertEqual(restored.platforms, [.kuwo, .kugou, .migu, .netease, .qq])
        XCTAssertEqual(restored.enabledPlatforms, [.kuwo, .kugou])
        await full.close(); await service.close()
    }

    func testMetadataAndExistingSettingsShareRevisionAndDoNotEraseEachOther() async throws {
        let store = NativeMusicMemoryDocument(), service = try await MetadataNativeFixture.service(store)
        _ = try await service.updateMetadataPreferences(.init(ifRevision: 0,
            platforms: [.kugou, .qq, .netease], enabledPlatforms: []))
        let envelope = try MJ.parse(XCTUnwrap(store.snapshot().0))
        XCTAssertEqual(envelope["version"], .n(1), "Optional fields must not require a new envelope/schema")
        XCTAssertEqual(envelope["records"][MusicModel.key("playback")]["schema"], .n(1))
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
        let store = NativeMusicMemoryDocument(bytes), service = try await MetadataNativeFixture.service(store)
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
            let store = NativeMusicMemoryDocument(bytes), service = try await MetadataNativeFixture.service(store)
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
        let store = NativeMusicMemoryDocument(), service = try await MetadataNativeFixture.service(store)
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
        let maxStore = NativeMusicMemoryDocument(try MetadataNativeFixture.document(playback: playback))
        let maxService = try await MetadataNativeFixture.service(maxStore)
        await expect(.limit) {
            _ = try await maxService.updateMetadataPreferences(.init(ifRevision: 9_007_199_254_740_991, enabledPlatforms: []))
        }
        XCTAssertEqual(maxStore.snapshot().2, 0); await maxService.close()
    }

    func testCandidatesUseMetadataOrderCompatibilityAndProductPolicyWithoutNetwork() async throws {
        var rows = try MetadataNativeFixture.all.flatMap { platform in
            try ["03", "01", "02"].map { try MetadataNativeFixture.row(platform, $0) }
        }
        // The compact model accepts at most 16 source rows. Keep this case at
        // the boundary so candidate enumeration is exercised on the maximum
        // document shape, not only 15 rows.
        rows.append(try MetadataNativeFixture.row(.qq, "04"))
        XCTAssertEqual(rows.count, 16)
        var playback = MusicModel.playback
        playback["metadataPlatforms"] = .strings(["kg", "wy", "kw", "tx", "mg"])
        playback["metadataEnabled"] = .strings(["kg", "wy", "kw", "mg"])
        let song = try MetadataNativeFixture.song(rows)
        let bytes = try MetadataNativeFixture.document(song: song, playback: playback)
        XCTAssertNoThrow(try MusicDocument(bytes, runID: "fixture"))
        let store = NativeMusicMemoryDocument(bytes), probe = MetadataNetworkProbe()
        let service = try await MetadataNativeFixture.service(store, probe: probe)
        let result = try await service.metadataCandidates(occurrenceID: "entry-1")
        XCTAssertEqual(result.candidates.map(\.source.platform), ["kg", "kg", "wy", "wy"])
        let expected = try [
            MetadataNativeFixture.row(.kugou, "01"), MetadataNativeFixture.row(.kugou, "02"),
            MetadataNativeFixture.row(.netease, "01"), MetadataNativeFixture.row(.netease, "02")
        ]
        XCTAssertEqual(result.candidates.map(\.source.sourceKey), expected.map { $0["sourceKey"].string })
        XCTAssertEqual(result.candidates.map { $0.artwork?.absoluteString },
                       expected.map { Optional($0["musicInfo"]["img"].string) })
        XCTAssertEqual(result.track.sources.count, 16)
        XCTAssertTrue(result.track.sources.contains { $0.platform == "tx" && $0.nativeId == "04" })
        XCTAssertEqual(result.track.ref, .init(kind: .queue, id: "entry-1"))
        XCTAssertNil(result.contentRevision); XCTAssertEqual(result.queueRevision, 3)
        let attempt = await service.attempt; XCTAssertNil(attempt)
        let calls = await probe.calls(); XCTAssertTrue(calls.isEmpty)
        XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, 0)
        await service.close()

        // A compact source row must carry the key derived from its own native
        // locator, and a conflicting native recording fails document validation
        // before candidates are enumerated.
        let valid = try MetadataNativeFixture.row(.qq, "AudioMID")
        var wrongKey = valid
        wrongKey["sourceKey"] = .string("not-the-native-key")
        let wrongKeyBytes = try MetadataNativeFixture.document(
            song: MetadataNativeFixture.song([valid, wrongKey]))
        XCTAssertThrowsError(try MusicDocument(wrongKeyBytes, runID: "fixture")) {
            XCTAssertEqual($0 as? MusicServiceError, .nativeIDConflict)
        }

        var conflict = valid
        conflict["musicInfo"]["duration"] = .n(240)
        let conflictBytes = try MetadataNativeFixture.document(song: MetadataNativeFixture.song([conflict]))
        XCTAssertThrowsError(try MusicDocument(conflictBytes, runID: "fixture")) {
            XCTAssertEqual($0 as? MusicServiceError, .nativeIDConflict)
        }

        let live = try MetadataNativeFixture.row(.qq, "LiveMID", title: "Recording (Live)")
        let liveBytes = try MetadataNativeFixture.document(song: MetadataNativeFixture.song([valid, live]))
        XCTAssertThrowsError(try MusicDocument(liveBytes, runID: "fixture")) {
            XCTAssertEqual($0 as? MusicServiceError, .nativeIDConflict)
        }

        // normalizeSong rejects a seventeenth source row before candidate
        // enumeration.
        let overflow = rows + [try MetadataNativeFixture.row(.qq, "05")]
        XCTAssertEqual(overflow.count, 17)
        let overflowBytes = try MetadataNativeFixture.document(song: MetadataNativeFixture.song(overflow))
        XCTAssertThrowsError(try MusicDocument(overflowBytes, runID: "fixture")) {
            XCTAssertEqual($0 as? MusicServiceError, .invalidImport)
        }
        // MusicModel.sources keeps its separate .limit guard for callers that
        // bypass document validation.
        var bypass = song
        bypass["sources"] = .array(overflow)
        XCTAssertThrowsError(try MusicModel.sources(bypass)) {
            XCTAssertEqual($0 as? MusicServiceError, .limit)
        }
    }

    func testFivePlatformCapAndCandidateArtworkNeverInheritThePrimaryCover() async throws {
        var rows = try MetadataNativeFixture.all.flatMap { p in
            try ["03", "01", "02"].map { try MetadataNativeFixture.row(p, $0) }
        }
        // Missing or invalid row artwork must not fall through to another
        // platform or the primary audio artwork.
        rows[1]["musicInfo"]["img"] = .string("")
        rows[2]["musicInfo"]["img"] = .string("https://untrusted.invalid/image.jpg")
        let store = NativeMusicMemoryDocument(try MetadataNativeFixture.document(song: MetadataNativeFixture.song(rows)))
        let service = try await MetadataNativeFixture.service(store, enabled: Set(MetadataNativeFixture.all))
        let result = try await service.metadataCandidates(occurrenceID: "entry-1")
        XCTAssertEqual(result.candidates.count, 10)
        XCTAssertEqual(result.candidates.map(\.source.platform), ["wy", "wy", "tx", "tx", "kg", "kg", "kw", "kw", "mg", "mg"])
        XCTAssertNil(result.candidates[0].artwork)
        XCTAssertNil(result.candidates[1].artwork)
        XCTAssertEqual(result.candidates[2].artwork?.host, "y.gtimg.cn")
        XCTAssertEqual(result.candidates[6].artwork?.host, "img.kuwo.cn")
        XCTAssertEqual(result.candidates[8].artwork?.host, "d.musicapp.migu.cn")
        XCTAssertFalse(result.candidates.compactMap { $0.artwork?.absoluteString }
            .contains("https://y.gtimg.cn/AudioMID.jpg"), "primary audio artwork is not a candidate cover")
        await service.close()
    }

    func testExplicitEmptySourcesDoNotResurrectSongLevelPrimaryOrWrite() async throws {
        let primary = try MetadataNativeFixture.row(.qq, "AudioMID")["musicInfo"]
        var song = try MetadataNativeFixture.song([])
        // A song-level primary locator is not a source after the compact
        // schema migration; only an explicit source list is authoritative.
        song["musicInfo"] = primary
        XCTAssertEqual(song["sources"], .array([]))
        let bytes = try MetadataNativeFixture.document(song: song)
        let store = NativeMusicMemoryDocument(bytes), service = try await MetadataNativeFixture.service(store)
        let result = try await service.metadataCandidates(occurrenceID: "entry-1")
        XCTAssertTrue(result.candidates.isEmpty)
        XCTAssertTrue(result.track.sources.isEmpty)
        XCTAssertNil(result.contentRevision)
        XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, 0)
        await service.close()
    }

    func testMissingSourcesFailAtDocumentReadBoundaryWithoutWriting() async throws {
        let primary = try MetadataNativeFixture.row(.qq, "AudioMID")["musicInfo"]
        var song = try MetadataNativeFixture.song([])
        song["musicInfo"] = primary
        song.remove("sources")
        XCTAssertEqual(song["musicInfo"]["songmid"].string, "AudioMID")
        let bytes = try MetadataNativeFixture.document(song: song)
        let store = NativeMusicMemoryDocument(bytes), service = try await MetadataNativeFixture.service(store)
        await expect(.corruptDocument) { _ = try await service.metadataCandidates(occurrenceID: "entry-1") }
        XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, 0)
        await service.close()
    }

    func testFingerprintExcludesOccurrenceAndSettingsButIncludesHiddenLocatorAndCover() async throws {
        let row = try MetadataNativeFixture.row(.qq, "AudioMID")
        let song = try MetadataNativeFixture.song([row])
        let store = NativeMusicMemoryDocument(try MetadataNativeFixture.document(song: song))
        let service = try await MetadataNativeFixture.service(store)
        let first = try await service.metadataCandidates(occurrenceID: "entry-1")
        _ = try await service.changeQueue(.select("entry-2"), expectedRevision: 3)
        _ = try await service.updateMetadataPreferences(.init(ifRevision: 0, platforms: [.qq, .netease, .kugou]))
        let second = try await service.metadataCandidates(occurrenceID: "entry-2")
        XCTAssertEqual(first.contentFingerprint, second.contentFingerprint)
        XCTAssertEqual(first.contentRevision, second.contentRevision)
        await expect(.targetChanged) { _ = try await service.metadataCandidates(occurrenceID: "entry-1") }
        await service.close()
        for variant in ["locator", "cover"] {
            var changed = song
            switch variant {
            case "locator":
                var source = row, info = row["musicInfo"]; info["albumId"] = .string("ChangedAlbum")
                source["musicInfo"] = info; changed["sources"] = .array([source])
            default: changed["cover_url"] = .string("https://p1.music.126.net/changed.jpg")
            }
            let other = try await MetadataNativeFixture.service(.init(try MetadataNativeFixture.document(song: changed)))
            let result = try await other.metadataCandidates(occurrenceID: "entry-1")
            XCTAssertNotEqual(first.contentFingerprint, result.contentFingerprint, variant)
            await other.close()
        }
    }

    func testProtectionRevocationAndCancelledCallerDoNotReadOrWrite() async throws {
        let store = NativeMusicMemoryDocument(try MetadataNativeFixture.document(song: MetadataNativeFixture.song([])))
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
        let store = NativeMusicMemoryDocument(try MetadataNativeFixture.document(song: song))
        let qq = NativeMusicQQFixture(), gate = NativeMusicGate()
        let service = try await NativeMusicFixture.service(store, qq: qq)
        let snapshot = try await service.metadataCandidates(occurrenceID: "entry-1")
        let source = try XCTUnwrap(snapshot.candidates.first?.source)
        await qq.suspend(gate)
        let request = Task { try await service.lyrics(occurrenceId: "entry-1", sourceKey: source.sourceKey) }
        await gate.started()
        _ = try await service.updateMetadataPreferences(.init(ifRevision: 0, enabledPlatforms: []))
        await expect(.cancelled) { _ = try await request.value }
        await gate.release()
        let state = try await service.state()
        XCTAssertEqual(state.currentId, "entry-1"); XCTAssertEqual(state.position, 0)
        await service.close()
    }
}
