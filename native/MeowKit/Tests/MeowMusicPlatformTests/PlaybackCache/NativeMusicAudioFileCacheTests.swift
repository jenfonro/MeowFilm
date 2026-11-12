import Foundation
import Darwin
import XCTest
import MeowMusicFeature
@testable import MeowMusicPlatform

/// These are deterministic injected-I/O tests, NOT evidence of an upstream
/// account grant, actual CDN transfer, or native codec support.
final class NativeMusicAudioFileCacheTests: XCTestCase {
    func testSequentialPrefixSeekWaitsAndReadCancellationDoesNotChangeDownload() async throws {
        let f = try AudioCacheTestFixture()
        defer { f.cleanup() }
        let gate = AudioCacheTestGate(), transport = PrefixCacheTransport(gate: gate)
        let cache = f.cache(transport: transport), request = f.request()
        let source = try await cache.preparePlayback(.init(identity: request.identity, audio: request.audio,
            expectedDuration: request.expectedDuration, authorization: request.authorization,
            playbackAuthorization: request.playbackAuthorization))
        await fulfillment(of: [gate.entered], timeout: 3)
        let information = try await source.information()
        XCTAssertEqual(information.length, 80)
        XCTAssertEqual(source.progress.received, 20)
        let beginning = try await source.read(offset: 0, count: 10)
        XCTAssertEqual(beginning, Data((0..<10).map(UInt8.init)))
        let reached = expectation(description: "uncached target must wait")
        reached.isInverted = true
        let pending = Task {
            let data = try await source.read(offset: 60, count: 10)
            reached.fulfill(); return data
        }
        await fulfillment(of: [reached], timeout: 0.05)
        pending.cancel()
        do { _ = try await pending.value; XCTFail("Cancelled read completed") }
        catch { XCTAssertTrue(error is CancellationError) }
        let back = try await source.read(offset: 5, count: 10)
        XCTAssertEqual(back, Data((5..<15).map(UInt8.init)))
        XCTAssertEqual(source.progress.received, 20)
        let calls = await transport.count
        XCTAssertEqual(calls, 1)
        await gate.release()
        let forward = try await source.read(offset: 60, count: 10)
        XCTAssertEqual(forward, Data((60..<70).map(UInt8.init)))
        source.confirmPlayback(at: f.clock.now())
        let version = await source.result()
        XCTAssertNotNil(version); XCTAssertTrue(source.progress.complete)
        let afterPublication = try await source.read(offset: 0, count: 10)
        XCTAssertEqual(afterPublication, beginning)
        source.retire(); await cache.close()
    }

    func testClearKeepsCurrentSequentialReadButPreventsItsPublication() async throws {
        let f = try AudioCacheTestFixture()
        defer { f.cleanup() }
        let gate = AudioCacheTestGate(), transport = PrefixCacheTransport(gate: gate)
        let cache = f.cache(transport: transport), request = f.request()
        let source = try await cache.preparePlayback(.init(identity: request.identity, audio: request.audio,
            expectedDuration: request.expectedDuration, authorization: request.authorization,
            playbackAuthorization: request.playbackAuthorization))
        await fulfillment(of: [gate.entered], timeout: 3)
        await cache.clear()
        await gate.release()
        let bytes = try await source.read(offset: 60, count: 10)
        XCTAssertEqual(bytes.count, 10)
        source.confirmPlayback(at: f.clock.now())
        let version = await source.result(), hit = await f.hit(cache)
        XCTAssertNil(version); XCTAssertNil(hit); XCTAssertFalse(source.progress.complete)
        source.retire(); await cache.close()
    }

    func testFailedNewMarkerPreparationRollsBackOnlyTheNewlyCreatedFile() async throws {
        let f = try AudioCacheTestFixture()
        defer { f.cleanup() }
        XCTAssertThrowsError(try NativeMusicAudioCacheDirectory(url: f.root, prepareMarker: { _ in
            throw NativeMusicAudioCacheFailure.miss
        }))
        XCTAssertEqual(try f.names(), [])
        let retry = f.cache()
        await retry.storeAfterPlayback(f.request())
        let hit = await f.hit(retry)
        XCTAssertNotNil(hit, "A failed initialization must not poison the next cache attempt")
        await retry.close()
    }

    func testPreexistingEmptyMarkerIsNotAdoptedOrRepaired() throws {
        let f = try AudioCacheTestFixture()
        defer { f.cleanup() }
        try FileManager.default.createDirectory(at: f.root, withIntermediateDirectories: false,
                                                attributes: [.posixPermissions: 0o700])
        let marker = f.root.appendingPathComponent(".owner")
        try Data().write(to: marker)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: marker.path)
        XCTAssertThrowsError(try NativeMusicAudioCacheDirectory(url: f.root))
        XCTAssertEqual(try f.names(), [".owner"])
        XCTAssertEqual(try Data(contentsOf: marker), Data())
    }

    func testMarkerPreparationFailureCannotUnlinkAReplacementFile() throws {
        let f = try AudioCacheTestFixture()
        defer { f.cleanup() }
        let marker = f.root.appendingPathComponent(".owner"), replacement = Data("not-our-marker".utf8)
        XCTAssertThrowsError(try NativeMusicAudioCacheDirectory(url: f.root, prepareMarker: { _ in
            try FileManager.default.removeItem(at: marker)
            try replacement.write(to: marker)
            throw NativeMusicAudioCacheFailure.miss
        }))
        XCTAssertEqual(try Data(contentsOf: marker), replacement)
    }

    func testProductionCachesBaseCanonicalizationStillRejectsDescendantSymlinks() async throws {
        let f = try AudioCacheTestFixture()
        defer { f.cleanup() }
        let base = try FileManager.default.url(for: .cachesDirectory, in: .userDomainMask,
                                               appropriateFor: nil, create: true)
        let root = base.appendingPathComponent("meow-audio-cache-test-" + UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let transport = AudioCacheTestTransport()
        let cache = NativeMusicAudioFileCache(rootURL: root, clock: { f.clock.now() },
            transport: transport, inspector: AudioCacheTestInspector())
        await cache.storeAfterPlayback(f.request())
        let hit = await f.hit(cache)
        XCTAssertNotNil(hit, "System /var aliases must not make every production cache fill a miss")
        await cache.close()
        try FileManager.default.removeItem(at: root)
        try FileManager.default.createSymbolicLink(at: root, withDestinationURL: f.parent)
        let refused = NativeMusicAudioFileCache(rootURL: root, clock: { f.clock.now() },
            transport: transport, inspector: AudioCacheTestInspector())
        await refused.storeAfterPlayback(f.request())
        await refused.close()
        let count = await transport.count
        XCTAssertEqual(count, 1, "Caller descendants still use NOFOLLOW")
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: f.parent.path), [])
    }

    func testConstructionAndDeniedRequestsNeverCreateDirectoryOrTransfer() async throws {
        let f = try AudioCacheTestFixture()
        defer { f.cleanup() }
        let transport = AudioCacheTestTransport(), cache = f.cache(transport: transport)
        let production = NativeMusicAudioFileCache(rootURL: f.root)
        XCTAssertFalse(FileManager.default.fileExists(atPath: f.root.path))
        await production.close()
        var requests = [
            f.request(policy: .denied),
            f.request(duration: 0),
            f.request(duration: .nan),
            f.request(confirmedAt: f.clock.now().addingTimeInterval(1)),
            f.request(policy: .init(allowed: true, licenseExpiresAt: f.clock.now())),
            f.request(quality: "master"),
            f.request(platform: "kw"),
            f.request(url: URL(string: "https://untrusted.invalid/media")!),
            f.request(urlExpiry: f.clock.now().addingTimeInterval(-1))
        ]
        let revoked = MusicQueueAuthorization(); revoked.revoke()
        requests.append(f.request(authorization: revoked))
        requests.append(f.request(playbackAuthorization: revoked))
        for request in requests { await cache.storeAfterPlayback(request) }
        let count = await transport.count
        XCTAssertEqual(count, 0)
        XCTAssertFalse(FileManager.default.fileExists(atPath: f.root.path))
        await cache.close()
        XCTAssertFalse(FileManager.default.fileExists(atPath: f.root.path))
    }

    func testPublicProductionInitializerRefusesNonCachesRootBeforeTransport() async throws {
        let f = try AudioCacheTestFixture()
        defer { f.cleanup() }
        let transport = AudioCacheTestTransport()
        let cache = NativeMusicAudioFileCache(rootURL: f.root, clock: { f.clock.now() },
            transport: transport, inspector: AudioCacheTestInspector())
        await cache.storeAfterPlayback(f.request())
        let count = await transport.count
        XCTAssertEqual(count, 0)
        XCTAssertFalse(FileManager.default.fileExists(atPath: f.root.path))
        await cache.close()
    }

    func testFileExpiryUsesRealPlayNotCompletionOrURLExpiryAndPersistsWithoutURLHeaders() async throws {
        let f = try AudioCacheTestFixture()
        defer { f.cleanup() }
        let gate = AudioCacheTestGate(), inspector = AudioCacheTestInspector(gate: gate)
        let cache = f.cache(inspector: inspector)
        let played = f.clock.now(), license = played.addingTimeInterval(2 * 86_400)
        let request = f.request(url: URL(string: "https://isure.stream.qqmusic.qq.com/file?key=synthetic-signed-url")!,
                                urlExpiry: played.addingTimeInterval(5),
                                policy: .init(allowed: true, licenseExpiresAt: license))
        let task = Task { await cache.storeAfterPlayback(request) }
        await fulfillment(of: [gate.entered], timeout: 3)
        f.clock.advance(60)
        await gate.release(); await task.value
        let hit = await f.hit(cache)
        XCTAssertEqual(hit?.expiresAt, license)
        XCTAssertEqual(hit?.measuredQuality, "flac")
        XCTAssertTrue(try XCTUnwrap(hit).fileURL.path.hasPrefix(f.root.path + "/"))
        let index = try String(contentsOf: f.root.appendingPathComponent("index.json"), encoding: .utf8)
        for text in ["synthetic-signed-url", "synthetic-cookie", "synthetic-bearer", "qqmusic.qq.com", "expiresAtURL"] {
            XCTAssertFalse(index.contains(text))
        }
        await cache.close()
        let reopened = f.cache()
        f.clock.advance(3600)
        let again = await f.hit(reopened)
        XCTAssertEqual(again?.expiresAt, license, "Read/reopen must not renew lifetime")
        f.clock.set(license)
        let expired = await f.hit(reopened)
        XCTAssertNil(expired)
        await reopened.close()
    }

    func testLookupDoesNotRenewAndOnlyExactRealPlayTouchExtendsThirtyDays() async throws {
        let f = try AudioCacheTestFixture()
        defer { f.cleanup() }
        let cache = f.cache(), request = f.request(), initial = f.clock.now()
        await cache.storeAfterPlayback(request)
        f.clock.advance(10 * 86_400)
        let before = await f.hit(cache)
        XCTAssertEqual(before?.expiresAt, initial.addingTimeInterval(30 * 86_400))
        let version = try XCTUnwrap(before).version
        let mismatch = f.request(signature: "different").identity
        await cache.touch(mismatch, version: version, confirmedAt: f.clock.now(), authorization: request.authorization)
        await cache.touch(request.identity, version: version, confirmedAt: f.clock.now().addingTimeInterval(1), authorization: request.authorization)
        let untouched = await f.hit(cache)
        XCTAssertEqual(untouched?.expiresAt, before?.expiresAt)
        await cache.touch(request.identity, version: version, confirmedAt: f.clock.now(), authorization: request.authorization)
        let renewed = await f.hit(cache)
        XCTAssertEqual(renewed?.expiresAt, initial.addingTimeInterval(40 * 86_400))
        let declined = await cache.lookup(scope: request.identity.scope, songID: request.identity.songID,
                                         authorization: request.authorization, accepts: { _ in false })
        XCTAssertNil(declined)
        let otherScope = await cache.lookup(scope: "different", songID: request.identity.songID,
                                           authorization: request.authorization, accepts: { _ in true })
        XCTAssertNil(otherScope)
        request.authorization.revoke()
        let revoked = await f.hit(cache, authorization: request.authorization)
        XCTAssertNil(revoked)
        await cache.close()
    }

    func testLateOldVersionTouchAndRemovalCannotAffectReplacementFile() async throws {
        let f = try AudioCacheTestFixture()
        defer { f.cleanup() }
        let cache = f.cache()
        let old = f.request(signature: "recording-v1")
        let storedOld = await cache.storeAfterPlayback(old)
        let oldVersion = try XCTUnwrap(storedOld)
        f.clock.advance(60)
        let replacement = f.request(signature: "recording-v2")
        let storedReplacement = await cache.storeAfterPlayback(replacement)
        let replacementVersion = try XCTUnwrap(storedReplacement)
        XCTAssertNotEqual(oldVersion, replacementVersion)
        let foundBefore = await f.hit(cache)
        let before = try XCTUnwrap(foundBefore)

        f.clock.advance(7 * 86_400)
        await cache.touch(old.identity, version: oldVersion, confirmedAt: f.clock.now(),
                          authorization: old.authorization)
        await cache.remove(old.identity, version: oldVersion)

        let foundAfter = await f.hit(cache)
        let after = try XCTUnwrap(foundAfter)
        XCTAssertEqual(after.version, replacementVersion)
        XCTAssertEqual(after.identity, replacement.identity)
        XCTAssertEqual(after.expiresAt, before.expiresAt,
                       "an old playback callback cannot renew its replacement")
        XCTAssertTrue(FileManager.default.fileExists(atPath: after.fileURL.path))
        await cache.close()
    }

    func testEvictionUsesOldestRealPlayAndIncludesReservationBeforeWriting() async throws {
        let f = try AudioCacheTestFixture()
        defer { f.cleanup() }
        let cache = f.cache(capacity: 64)
        await cache.storeAfterPlayback(f.request(song: "a"))
        f.clock.advance(1)
        await cache.storeAfterPlayback(f.request(song: "b"))
        f.clock.advance(1)
        let a = await f.hit(cache, song: "a")
        XCTAssertNotNil(a) // A read must not turn this into an LRU cache.
        await cache.storeAfterPlayback(f.request(song: "c"))
        let evicted = await f.hit(cache, song: "a"), b = await f.hit(cache, song: "b"), c = await f.hit(cache, song: "c")
        XCTAssertNil(evicted); XCTAssertNotNil(b); XCTAssertNotNil(c)
        let bytes = try FileManager.default.contentsOfDirectory(at: f.root, includingPropertiesForKeys: [.fileSizeKey])
            .filter { ["wav", "part"].contains($0.pathExtension) }
            .reduce(0) { try $0 + ($1.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0) }
        XCTAssertEqual(bytes, 64)
        await cache.close()
    }

    func testConcurrentReservationsCannotExceedTotalBudget() async throws {
        let f = try AudioCacheTestFixture()
        defer { f.cleanup() }
        let gate = AudioCacheTestGate(), transport = AudioCacheTestTransport(beforeEOF: gate)
        let cache = f.cache(transport: transport, capacity: 48)
        let first = Task { await cache.storeAfterPlayback(f.request(song: "a")) }
        await fulfillment(of: [gate.entered], timeout: 3)
        // Its HTTP header is valid, but a second 32-byte reservation will not
        // fit while the first physical transfer/decoder still owns its bytes.
        await cache.storeAfterPlayback(f.request(song: "b"))
        await gate.release(); await first.value
        let a = await f.hit(cache, song: "a"), b = await f.hit(cache, song: "b")
        XCTAssertNotNil(a); XCTAssertNil(b)
        await cache.close()
    }

    func testDistinctFillsShareByteBudgetAndFinishIndependently() async throws {
        let f = try AudioCacheTestFixture()
        defer { f.cleanup() }
        let gate = AudioCacheTestGate(expectedEntries: 9), transport = AudioCacheTestTransport()
        let cache = f.cache(transport: transport, inspector: AudioCacheTestInspector(gate: gate), capacity: 9 * 32)
        let tasks = (0..<9).map { index in
            Task { await cache.storeAfterPlayback(f.request(song: "song-\(index)")) }
        }
        await fulfillment(of: [gate.entered], timeout: 3)
        let transfers = await transport.count
        XCTAssertEqual(transfers, 9)
        await gate.release()
        for task in tasks { await task.value }
        for index in 0..<9 {
            let hit = await f.hit(cache, song: "song-\(index)")
            XCTAssertNotNil(hit)
        }
        await cache.close()
    }

    func testSongSwitchCancelsEvenExactByteCountUntilTransportEOF() async throws {
        let f = try AudioCacheTestFixture()
        defer { f.cleanup() }
        let gate = AudioCacheTestGate(), transport = AudioCacheTestTransport(beforeEOF: gate)
        let cache = f.cache(transport: transport)
        let request = f.request()
        let task = Task { await cache.storeAfterPlayback(request) }
        await fulfillment(of: [gate.entered], timeout: 3)
        request.playbackAuthorization.revoke()
        await gate.release(); await task.value
        let hit = await f.hit(cache)
        XCTAssertNil(hit)
        XCTAssertFalse(try f.names().contains { $0.hasSuffix(".part") })
        await cache.close()
    }

    func testCompletedBodyMayFinishValidationAfterSongSwitchWithoutRenewingPlayTime() async throws {
        let f = try AudioCacheTestFixture()
        defer { f.cleanup() }
        let gate = AudioCacheTestGate(), cache = f.cache(inspector: AudioCacheTestInspector(gate: gate))
        let at = f.clock.now()
        let request = f.request()
        let task = Task { await cache.storeAfterPlayback(request) }
        await fulfillment(of: [gate.entered], timeout: 3)
        request.playbackAuthorization.revoke()
        f.clock.advance(80)
        await gate.release(); await task.value
        let hit = await f.hit(cache)
        XCTAssertEqual(hit?.expiresAt, at.addingTimeInterval(30 * 86_400))
        await cache.close()
    }

    func testRetiringOneRequestCannotCancelItsSuccessor() async throws {
        let f = try AudioCacheTestFixture()
        defer { f.cleanup() }
        let gate = AudioCacheTestGate(), cache = f.cache(transport: AudioCacheTestTransport(beforeEOF: gate))
        let old = f.request(song: "old"), next = f.request(song: "next")
        let task = Task { await cache.storeAfterPlayback(old) }
        await fulfillment(of: [gate.entered], timeout: 3)
        await cache.storeAfterPlayback(next)
        old.playbackAuthorization.revoke()
        await gate.release(); await task.value
        let oldHit = await f.hit(cache, song: "old"), nextHit = await f.hit(cache, song: "next")
        XCTAssertNil(oldHit); XCTAssertNotNil(nextHit)
        XCTAssertTrue(next.playbackAuthorization.isActive)
        await cache.close()
    }

    func testAllStrongCancellationsFenceLateValidationAndPublication() async throws {
        for action in ["cancel", "clear", "close", "revoke", "parent", "caller"] {
            let f = try AudioCacheTestFixture()
            let cancellation = ["cancel", "close"].contains(action) ? expectation(description: "producer cancellation") : nil
            let gate = AudioCacheTestGate()
            let cache = f.cache(inspector: AudioCacheTestInspector(gate: gate, cancellation: cancellation))
            let parent = MusicQueueAuthorization(), authorization = MusicQueueAuthorization(parent: parent)
            let task = Task { await cache.storeAfterPlayback(f.request(authorization: authorization)) }
            await fulfillment(of: [gate.entered], timeout: 3)
            var drain: Task<Void, Never>?
            switch action {
            case "cancel": drain = Task { await cache.cancelPending() }
            case "clear": await cache.clear()
            case "close": drain = Task { await cache.close() }
            case "revoke": authorization.revoke()
            case "parent": parent.revoke()
            default: task.cancel()
            }
            if let cancellation { await fulfillment(of: [cancellation], timeout: 3) }
            // The intentionally uncooperative inspector returns success after
            // cancellation. The publication boundary must independently refuse.
            await gate.release(); await drain?.value; await task.value
            await cache.close()
            let reopened = f.cache(), hit = await f.hit(reopened)
            XCTAssertNil(hit, action)
            XCTAssertFalse(try f.names().contains { ["part", "wav"].contains(URL(fileURLWithPath: $0).pathExtension) }, action)
            await reopened.close(); f.cleanup()
        }
    }

    func testClearAndCancelFenceConfirmedRequestsNotYetEnteredIntoActor() async throws {
        for clear in [false, true] {
            let f = try AudioCacheTestFixture()
            let transport = AudioCacheTestTransport(), cache = f.cache(transport: transport)
            let queued = f.request()
            if clear { await cache.clear() } else { await cache.cancelPending() }
            await cache.storeAfterPlayback(queued)
            let count = await transport.count
            XCTAssertEqual(count, 0)
            f.clock.advance(1)
            await cache.storeAfterPlayback(f.request())
            let hit = await f.hit(cache)
            XCTAssertNotNil(hit, "A genuinely newer playing event can start a fresh capture")
            await cache.close(); f.cleanup()
        }
    }

    func testLicenseExpiryWhileInspectingAndElapsedDeadlineAreMisses() async throws {
        let f = try AudioCacheTestFixture()
        defer { f.cleanup() }
        let gate = AudioCacheTestGate(), cache = f.cache(inspector: AudioCacheTestInspector(gate: gate))
        let task = Task {
            await cache.storeAfterPlayback(f.request(policy: .init(allowed: true,
                licenseExpiresAt: f.clock.now().addingTimeInterval(1))))
        }
        await fulfillment(of: [gate.entered], timeout: 3)
        f.clock.advance(2)
        await gate.release(); await task.value
        let hit = await f.hit(cache)
        XCTAssertNil(hit)
        let monotonic = AudioCacheMonotonicClock()
        let deadline = NativeMusicAudioCacheFence(authorization: MusicQueueAuthorization(),
                                                 timing: .init(now: { monotonic.now() }))
        monotonic.advance(120)
        XCTAssertThrowsError(try deadline.check())
        await cache.close()
    }

    func testCrashOrphansAreReclaimedWithoutInterpretingTheirBytesAsCompleteAudio() async throws {
        let f = try AudioCacheTestFixture()
        defer { f.cleanup() }
        let cache = f.cache()
        await cache.storeAfterPlayback(f.request())
        await cache.close()
        let orphan = UUID().uuidString.lowercased() + ".part"
        let unpublished = UUID().uuidString.lowercased() + ".wav"
        for name in [orphan, unpublished] {
            let url = f.root.appendingPathComponent(name)
            try Data([1, 2, 3]).write(to: url)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
        }
        let reopened = f.cache(), hit = await f.hit(reopened)
        XCTAssertNotNil(hit)
        XCTAssertFalse(try f.names().contains(orphan))
        XCTAssertFalse(try f.names().contains(unpublished))
        await reopened.close()
    }

    func testInspectedDescriptorCannotBeReplacedByAnotherFileAtPublication() async throws {
        let f = try AudioCacheTestFixture()
        defer { f.cleanup() }
        let gate = AudioCacheTestGate(), cache = f.cache(inspector: AudioCacheTestInspector(gate: gate))
        let task = Task { await cache.storeAfterPlayback(f.request()) }
        await fulfillment(of: [gate.entered], timeout: 3)
        let names = try f.names()
        let temporary = try XCTUnwrap(names.first { $0.hasSuffix(".part") })
        let url = f.root.appendingPathComponent(temporary)
        try FileManager.default.removeItem(at: url)
        try Data(repeating: 0x99, count: 32).write(to: url)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
        await gate.release(); await task.value
        let hit = await f.hit(cache)
        XCTAssertNil(hit)
        await cache.close()
    }

    func testDirectoryOwnershipAndExclusiveWriterRefuseOtherDataAndConcurrentOwners() async throws {
        let f = try AudioCacheTestFixture()
        defer { f.cleanup() }
        try FileManager.default.createDirectory(at: f.root, withIntermediateDirectories: false,
                                                attributes: [.posixPermissions: 0o700])
        let userData = f.root.appendingPathComponent("playlist.json"), original = Data("user-data".utf8)
        try original.write(to: userData)
        let refusing = f.cache()
        await refusing.storeAfterPlayback(f.request())
        await refusing.clear(); await refusing.close()
        XCTAssertEqual(try Data(contentsOf: userData), original)
        XCTAssertEqual(try f.names(), ["playlist.json"])

        let second = try AudioCacheTestFixture()
        defer { second.cleanup() }
        let first = second.cache(), concurrent = second.cache()
        await first.storeAfterPlayback(second.request())
        let blocked = await second.hit(concurrent)
        XCTAssertNil(blocked)
        await first.close()
        let recovered = await second.hit(concurrent)
        XCTAssertNotNil(recovered)
        await concurrent.close()
    }

    func testSymlinkAncestorLeafAndHardlinkedFilesNeverTouchOutsideData() async throws {
        let f = try AudioCacheTestFixture()
        defer { f.cleanup() }
        let outside = f.parent.appendingPathComponent("outside")
        try FileManager.default.createDirectory(at: outside, withIntermediateDirectories: false)
        let sentinel = outside.appendingPathComponent("sentinel"), original = Data("keep".utf8)
        try original.write(to: sentinel)
        try FileManager.default.createSymbolicLink(at: f.root, withDestinationURL: outside)
        let cache = f.cache()
        await cache.storeAfterPlayback(f.request()); await cache.clear(); await cache.close()
        XCTAssertEqual(try Data(contentsOf: sentinel), original)
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: outside.path), ["sentinel"])
        let nested = NativeMusicAudioFileCache(rootURL: f.root.appendingPathComponent("child"),
            clock: { f.clock.now() }, transport: AudioCacheTestTransport(), inspector: AudioCacheTestInspector(),
            capacity: 256 * 1024 * 1024)
        await nested.storeAfterPlayback(f.request()); await nested.close()
        XCTAssertFalse(FileManager.default.fileExists(atPath: outside.appendingPathComponent("child").path))

        let g = try AudioCacheTestFixture()
        defer { g.cleanup() }
        let own = g.cache()
        await own.storeAfterPlayback(g.request())
        let saved = await g.hit(own)
        let hit = try XCTUnwrap(saved)
        await own.close()
        let outsideLink = g.parent.appendingPathComponent("linked-user-file")
        try FileManager.default.linkItem(at: hit.fileURL, to: outsideLink)
        let bytes = try Data(contentsOf: outsideLink)
        let reopened = g.cache(), rejected = await g.hit(reopened)
        XCTAssertNil(rejected)
        await reopened.clear(); await reopened.close()
        XCTAssertEqual(try Data(contentsOf: outsideLink), bytes)
        XCTAssertTrue(FileManager.default.fileExists(atPath: hit.fileURL.path), "Do not unlink an unowned hardlinked file")
    }

    func testIndexTraversalAndReplacementSymlinkCannotEscapeOwnedDirectory() async throws {
        let f = try AudioCacheTestFixture()
        defer { f.cleanup() }
        let cache = f.cache()
        await cache.storeAfterPlayback(f.request())
        await cache.close()
        let outside = f.parent.appendingPathComponent("user.wav"), original = Data("outside-original".utf8)
        try original.write(to: outside)
        let index = f.root.appendingPathComponent("index.json")
        var object = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: index)) as? [String: Any])
        var entries = try XCTUnwrap(object["entries"] as? [[String: Any]])
        entries[0]["file"] = "../user.wav"; object["entries"] = entries
        try JSONSerialization.data(withJSONObject: object).write(to: index)
        let reopened = f.cache(), rejected = await f.hit(reopened)
        XCTAssertNil(rejected)
        await reopened.clear(); await reopened.close()
        XCTAssertEqual(try Data(contentsOf: outside), original)
        // Replacing index itself with a symlink must not read/write its target.
        try FileManager.default.removeItem(at: index)
        try FileManager.default.createSymbolicLink(at: index, withDestinationURL: outside)
        let hostile = f.cache()
        await hostile.storeAfterPlayback(f.request()); await hostile.clear(); await hostile.close()
        XCTAssertEqual(try Data(contentsOf: outside), original)
    }

    #if os(iOS) && !targetEnvironment(simulator)
    func testDeviceCacheIndexDirectoryAndMediaAreProtectedUntilFirstUserAuthentication() async throws {
        let f = try AudioCacheTestFixture()
        defer { f.cleanup() }
        let cache = f.cache()
        await cache.storeAfterPlayback(f.request())
        let saved = await f.hit(cache), hit = try XCTUnwrap(saved)
        for url in [f.root, f.root.appendingPathComponent("index.json"), hit.fileURL] {
            let fd = Darwin.open(url.path, O_RDONLY | O_NOFOLLOW | O_CLOEXEC)
            XCTAssertGreaterThanOrEqual(fd, 0)
            if fd >= 0 {
                XCTAssertEqual(fcntl(fd, F_GETPROTECTIONCLASS),
                               NativeMusicAudioCacheProtection.completeUntilFirstUserAuthentication)
                Darwin.close(fd)
            }
        }
        await cache.close()
    }
    #endif
}

// Shared only by these three dedicated native cache regression files.
final class AudioCacheTestClock: @unchecked Sendable {
    private let lock = NSLock()
    private var value = Date(timeIntervalSince1970: 1_700_000_000)
    func now() -> Date { lock.lock(); defer { lock.unlock() }; return value }
    func advance(_ seconds: TimeInterval) { lock.lock(); value.addTimeInterval(seconds); lock.unlock() }
    func set(_ date: Date) { lock.lock(); value = date; lock.unlock() }
}

actor AudioCacheTestGate {
    nonisolated let entered = XCTestExpectation(description: "cache phase entered")
    private var released = false
    private var waiters: [CheckedContinuation<Void, Never>] = []
    init(expectedEntries: Int = 1) { entered.expectedFulfillmentCount = expectedEntries }
    func hold() async {
        entered.fulfill()
        if released { return }
        await withCheckedContinuation { waiters.append($0) }
    }
    func release() { released = true; waiters.forEach { $0.resume() }; waiters.removeAll() }
}

actor AudioCacheTestTransport: NativeMusicAudioCacheTransport {
    private(set) var count = 0
    let bytes: Data
    let beforeEOF: AudioCacheTestGate?
    let declaredLength: Int?
    let successfulEOF: Bool
    let status: Int
    let omitLength: Bool
    init(bytes: Data = Data(repeating: 0x42, count: 32), beforeEOF: AudioCacheTestGate? = nil,
         declaredLength: Int? = nil, successfulEOF: Bool = true, status: Int = 200, omitLength: Bool = false) {
        self.bytes = bytes; self.beforeEOF = beforeEOF; self.declaredLength = declaredLength
        self.successfulEOF = successfulEOF
        self.status = status; self.omitLength = omitLength
    }
    func transfer(_ request: NativeMusicAudioCacheHTTPRequest, into sink: NativeMusicAudioCacheSink) async throws {
        count += 1
        let ordinal = count
        let count = declaredLength ?? bytes.count
        var headers = ["Content-Type": "audio/wav"]
        if !omitLength { headers["Content-Length"] = String(count) }
        if status == 206 { headers["Content-Range"] = "bytes 0-\(count - 1)/\(count)" }
        try await sink.accept(.init(url: request.url, status: status, headers: headers))
        // Streaming chunks, never a destination path supplied by the transport.
        for start in stride(from: 0, to: bytes.count, by: 8192) {
            try sink.write(bytes.subdata(in: start..<min(start + 8192, bytes.count)))
        }
        if ordinal == 1 { await beforeEOF?.hold() }
        if successfulEOF { try sink.finishBody() }
    }
}

struct AudioCacheTestInspector: NativeMusicAudioCacheInspecting {
    let gate: AudioCacheTestGate?
    let result: NativeMusicAudioCacheInspection
    let cancellation: XCTestExpectation?
    init(gate: AudioCacheTestGate? = nil, result: NativeMusicAudioCacheInspection = AudioCacheTestFixture.measurement(),
         cancellation: XCTestExpectation? = nil) {
        self.gate = gate; self.result = result; self.cancellation = cancellation
    }
    func inspect(_ file: NativeMusicAudioCacheFile, expectedDuration: Double) async throws -> NativeMusicAudioCacheInspection {
        await withTaskCancellationHandler {
            await gate?.hold()
            return result // Intentionally ignores cancellation to test final fencing.
        } onCancel: { cancellation?.fulfill() }
    }
}

struct AudioCacheTestFixture: Sendable {
    let parent: URL
    let root: URL
    let clock = AudioCacheTestClock()
    init() throws {
        let temporary = FileManager.default.temporaryDirectory
        guard let pointer = realpath(temporary.path, nil) else { throw NativeMusicAudioCacheFailure.miss }
        defer { free(pointer) }
        parent = URL(fileURLWithPath: String(cString: pointer), isDirectory: true)
            .appendingPathComponent("meow-audio-cache-test-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: parent, withIntermediateDirectories: false)
        root = parent.appendingPathComponent("cache", isDirectory: true)
    }
    func cleanup() { try? FileManager.default.removeItem(at: parent) }
    func names() throws -> [String] { try FileManager.default.contentsOfDirectory(atPath: root.path).sorted() }
    func cache(transport: any NativeMusicAudioCacheTransport = AudioCacheTestTransport(),
               inspector: any NativeMusicAudioCacheInspecting = AudioCacheTestInspector(),
               capacity: Int64 = 256 * 1024 * 1024,
               timing: NativeMusicAudioCacheTiming = .init()) -> NativeMusicAudioFileCache {
        NativeMusicAudioFileCache(rootURL: root, clock: { clock.now() },
                                  transport: transport, inspector: inspector, capacity: capacity, timing: timing)
    }
    func request(song: String = "one", quality: String = "flac", platform: String = "tx", signature: String = "recording-v1",
                 duration: Double = 60, confirmedAt: Date? = nil,
                 url: URL = URL(string: "https://isure.stream.qqmusic.qq.com/file")!, urlExpiry: Date? = nil,
                 policy: MusicAudioFileCachePolicy = .init(allowed: true),
                 authorization: MusicQueueAuthorization = MusicQueueAuthorization(),
                 playbackAuthorization: MusicQueueAuthorization = MusicQueueAuthorization()) -> MusicAudioFileCacheRequest {
        .init(identity: .init(scope: "test-account-scope", songID: song, sourceKey: "official:" + platform,
                              platform: platform, nativeID: song, requestedQuality: quality, playbackSignature: signature, provider: "official:" + platform),
              audio: .init(url: url, headers: ["Cookie": "synthetic-cookie", "Authorization": "synthetic-bearer"],
                           expiresAt: urlExpiry, fileCachePolicy: policy),
              expectedDuration: duration, confirmedAt: confirmedAt ?? clock.now(), authorization: authorization,
              playbackAuthorization: playbackAuthorization)
    }
    func hit(_ cache: NativeMusicAudioFileCache, song: String = "one",
             authorization: MusicQueueAuthorization = MusicQueueAuthorization()) async -> MusicAudioFileCacheHit? {
        await cache.lookup(scope: "test-account-scope", songID: song, authorization: authorization, accepts: { _ in true })
    }
    static func measurement(container: String = "wav", codec: String = "pcm", duration: Double = 60,
                            depth: Int? = 16, bitrate: Double = 1_411_200, lossless: Bool = true,
                            complete: Bool = true, protected: Bool = false) -> NativeMusicAudioCacheInspection {
        .init(container: container, codec: codec, duration: duration, decodedFrames: Int64(duration * 44_100),
              sampleRate: 44_100, channels: 2, bitrate: bitrate, bitDepth: depth,
              lossless: lossless, complete: complete, protectedContent: protected)
    }
}

private actor PrefixCacheTransport: NativeMusicAudioCacheTransport {
    let gate: AudioCacheTestGate
    var count = 0
    init(gate: AudioCacheTestGate) { self.gate = gate }
    func transfer(_ request: NativeMusicAudioCacheHTTPRequest, into sink: NativeMusicAudioCacheSink) async throws {
        count += 1
        try await sink.accept(.init(url: request.url, status: 200, headers: ["Content-Length": "80", "Content-Type": "audio/wav"]))
        try sink.write(Data((0..<20).map(UInt8.init)))
        await gate.hold()
        try sink.write(Data((20..<80).map(UInt8.init)))
        try sink.finishBody()
    }
}
