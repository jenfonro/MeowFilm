import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

final class MusicNativePlatformLyricsTests: XCTestCase {
    func testSearchQueueLyricsUseCanonicalTextWithoutPlaybackAttemptScriptOrAccount() async throws {
        for platform in ["kw", "mg"] {
            let scripts = MusicNativeLyricsNoScript(), accounts = NativeMusicAccountFixture(), store = NativeMusicTestLibrary()
            let peer = MusicNativeLyricsFixture.peer(platform: platform)
            let service = try await NativeMusicFixture.service(store, scripts: scripts, accounts: accounts, platform: peer)
            let page = try await service.search(platform: platform, query: "Synthetic", page: 1, limit: 30)
            let track = try XCTUnwrap(page.items.first)
            let queued = try await service.changeQueue(.replace([track.ref], index: 0), expectedRevision: 0)
            let before = store.snapshot(), scriptCalls = await scripts.calls()
            let result = try await service.lyrics(occurrenceId: XCTUnwrap(queued.currentId), sourceKey: track.sources[0].sourceKey)
            XCTAssertEqual(result.state, .ready)
            XCTAssertEqual(result.lines.map(\.text), platform == "kw" ? ["AB", "C"] : ["你好", "世界"])
            XCTAssertEqual(result.lines[0].translation, platform == "kw" ? "Translation" : "Hello")
            XCTAssertTrue(result.lines.allSatisfy { !$0.words.isEmpty })
            let after = await scripts.calls(), accountCalls = await accounts.events(), requests = await peer.calls()
            XCTAssertEqual(after, scriptCalls); XCTAssertTrue(accountCalls.isEmpty)
            XCTAssertEqual(requests.map(\.operation), ["search", "lyrics"])
            XCTAssertEqual(store.snapshot().0, before.0)
            let played = try await service.playedSongs(); XCTAssertTrue(played.isEmpty)
            await service.close()
        }
    }

    func testCancellationProtectionAndChangedOccurrenceRejectLateLyricsAndFinishProducer() async throws {
        for platform in ["kw", "mg"] {
            for reason in ["cancel", "lock", "occurrence"] {
                let gate = NativeMusicGate(), store = NativeMusicTestLibrary()
                let peer = MusicNativeLyricsFixture.peer(platform: platform, gate: gate)
                let service = try await NativeMusicFixture.service(store, platform: peer)
                let page = try await service.search(platform: platform, query: "Synthetic", page: 1, limit: 30)
                let track = try XCTUnwrap(page.items.first)
                let state = try await service.changeQueue(.replace([track.ref, track.ref], index: 0), expectedRevision: 0)
                let task = Task { try await service.lyrics(occurrenceId: XCTUnwrap(state.currentId), sourceKey: track.sources[0].sourceKey) }
                await gate.started()
                if reason == "cancel" { task.cancel() }
                else if reason == "lock" { service.revokeAccess(generation: 2) }
                else {
                    let queue = try await service.queue()
                    _ = try await service.changeQueue(.select(queue.entries[1].id), expectedRevision: state.revision)
                }
                let before = store.snapshot()
                await gate.release(); await peer.waitUntilIdle()
                do { _ = try await task.value; XCTFail("late lyric accepted") }
                catch {
                    let expected: [MusicServiceError] = reason == "cancel" ? [.cancelled] :
                        reason == "lock" ? [.cancelled, .staleAccess, .accessUnavailable] : [.targetChanged, .cancelled]
                    XCTAssertTrue(expected.contains(error as? MusicServiceError ?? .invalidResponse))
                }
                XCTAssertEqual(store.snapshot().0, before.0)
                let calls = await peer.calls(); XCTAssertEqual(calls.count, 2)
                await service.close()
            }
        }
    }

    func testCanonicalPlatformFailureNeverFallsBackToScriptsOrEmptyLyrics() async throws {
        for platform in ["kw", "mg"] {
            let scripts = MusicNativeLyricsNoScript()
            let track = MusicNativeCandidateDiscoveryFixture.track(platform, id: "123")
            let store = try MusicNativeCandidateDiscoveryFixture.store(song: NativeMusicFixture.storedSong(track))
            let peer = NativeMusicResultFixture(failure: .upstreamFailed)
            let service = try await NativeMusicFixture.service(store, scripts: scripts, platform: peer)
            let queue = try await service.queue()
            await assertMusicFailure(.upstreamFailed) {
                _ = try await service.lyrics(occurrenceId: "entry-1", sourceKey: queue.entries[0].track.sources[0].sourceKey)
            }
            let calls = await scripts.calls(), requests = await peer.calls()
            XCTAssertTrue(calls.isEmpty); XCTAssertEqual(requests.map(\.operation), ["lyrics"])
            await service.close()
        }
    }

    func testMissingOrUnusableMainTextIsNotAnEmptyLyricSuccessOrScriptFallback() async throws {
        for result in [RuntimeValue.object([:]), .object(["text": .null]), .object(["text": .array([])])] {
            let scripts = MusicNativeLyricsNoScript()
            let song = try NativeMusicFixture.storedSong(MusicNativeCandidateDiscoveryFixture.track("kw", id: "123"))
            let peer = NativeMusicResultFixture([result])
            let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: song),
                                                               scripts: scripts, platform: peer)
            let queue = try await service.queue()
            await assertMusicFailure(.upstreamInvalid) {
                _ = try await service.lyrics(occurrenceId: "entry-1", sourceKey: queue.entries[0].track.sources[0].sourceKey)
            }
            let calls = await scripts.calls(), requests = await peer.calls()
            XCTAssertTrue(calls.isEmpty); XCTAssertEqual(requests.map(\.operation), ["lyrics"])
            await service.close()
        }
    }
}

enum MusicNativeLyricsFixture {
    static func lyrics(_ platform: String) -> RuntimeValue {
        platform == "kw" ? .object([
            "text": .string("[offset:100]\n[1000,1200](1200,200,0)A(1800,400,0)B\n[2000,200](2100,100,0)C"),
            "translation": .string("[offset:100]\n[00:01.000]Translation")
        ]) : .object([
            "text": .string("[1000,1000](1000,400,0)你(1400,600,0)好\n[2000,1000](2000,500,0)世(2500,500,0)界"),
            "translation": .string("[00:01.000]Hello\n[00:02.000]World")
        ])
    }
    static func peer(platform: String, gate: NativeMusicGate? = nil) -> NativeMusicResultFixture {
        NativeMusicResultFixture([
            try! MusicPlatformFixture.page([MusicNativeCandidateDiscoveryFixture.track(platform, id: "123")], platform: platform),
            lyrics(platform)
        ], gate: gate, gatedCall: 2)
    }
}

actor MusicNativeLyricsNoScript: MusicScriptResolving {
    private var operations: [String] = []
    func sources() throws -> [MusicScriptSource] { operations.append("list"); throw MusicServiceError.sourceUnavailable }
    func source(installationID: String) throws -> MusicScriptSource { operations.append("source"); throw MusicServiceError.sourceUnavailable }
    func resolve(request: MusicScriptRequest) throws -> MusicScriptResult<MusicNativeAudio> { operations.append("resolve"); throw MusicServiceError.sourceUnavailable }
    func lyrics(request: MusicScriptRequest) throws -> MusicScriptResult<MusicNativeLyrics> { operations.append("lyric"); throw MusicServiceError.sourceUnavailable }
    func calls() -> [String] { operations }
}
