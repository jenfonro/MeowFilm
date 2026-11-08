import Foundation
import MeowMusicFeature
import XCTest
@testable import MeowMusicNativeService

private actor MusicLyricFallbackScripts: MusicScriptResolving {
    let platform: String
    var requested: [String] = []
    var cold = false
    var loaded = Set<String>()
    func coldStart() { cold = true }
    init(_ platform: String) { self.platform = platform }
    func sources() -> [MusicScriptSource] {
        // A disabled script and an undeclared action must never be called.
        ["disabled", "failed", "no-lyric", "empty", "usable"].map { id in
            .init(installationID: id, generation: id == "disabled" || id == "failed" || cold && !loaded.contains(id) ? 0 : 1,
                enabled: id != "disabled", state: id == "disabled" ? .disabled : id == "failed" ? .failed :
                    cold && !loaded.contains(id) ? .idle : .ready, version: "1",
                contentHash: String(repeating: "a", count: 64), revisionHash: String(repeating: "b", count: 64),
                capabilities: [.init(siteID: platform, title: id, actions: id == "no-lyric" ? ["musicUrl"] : ["lyric"], qualities: [])])
        }
    }
    func source(installationID: String) throws -> MusicScriptSource {
        try XCTUnwrap(sources().first { $0.installationID == installationID })
    }
    func resolve(request: MusicScriptRequest) throws -> MusicScriptResult<MusicNativeAudio> { throw MusicServiceError.unsupported }
    func lyrics(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeLyrics> {
        requested.append(request.installationID)
        loaded.insert(request.installationID)
        let executed = sources().first { $0.installationID == request.installationID }!
        return .init(value: .init(text: request.installationID == "empty" ? "" : "[00:01.00]Script lyric"), source: executed)
    }
    func calls() -> [String] { requested }
}

final class MusicScriptLyricFallbackTests: XCTestCase {
    func testEmptyNativeLyricsUseEnabledDeclaredSamePlatformScriptsInInstallationOrderOnlyForWYKWAndMG() async throws {
        for platform in ["wy", "tx", "kg", "kw", "mg"] {
            do { try await checkFallback(platform); try await checkFallback(platform, cold: true) }
            catch { XCTFail("Unexpected lyric failure for \(platform): \(error)") }
        }
    }

    private func checkFallback(_ platform: String, cold: Bool = false) async throws {
            var track = MusicNativeCandidateDiscoveryFixture.track(platform,
                id: platform == "kg" ? String(repeating: "A", count: 32) : "123")
            if platform == "mg" {
                var info = try MJ.parse(track.data); info["copyrightId"] = .string("600123")
                track = .init(platform: platform, data: info.bytes)
            }
            let store = try MusicNativeCandidateDiscoveryFixture.store(song: MusicModel.normalize(track))
            let scripts = MusicLyricFallbackScripts(platform)
            if cold { await scripts.coldStart() }
            let responses: [MusicNativeHTTPResponse]
            switch platform {
            case "wy": responses = [.init(status: 200, data: Data(#"{"code":200,"lrc":{"lyric":""}}"#.utf8))]
            case "kg": responses = [.init(status: 200, data: Data(#"{"status":200,"errcode":200,"candidates":[]}"#.utf8))]
            case "kw":
                // Independent Python zlib/base64/XOR vector for "[00:00.00]":
                // a valid native lyric envelope with no displayable lyric text.
                responses = [.init(status: 200, data: Data(base64Encoded: "dHA9Y29udGVudA0KDQp4nPPMCQsLy8lJC3TNCfN3tLUFAC/XBVg=")!)]
            case "mg": responses = [
                .init(status: 200, data: Data(#"{"returnCode":"000000","resource":[{"songId":"123","copyrightId":"600123","lrcUrl":"https://d.musicapp.migu.cn/test.lrc"}]}"#.utf8)),
                .init(status: 200, data: Data("[00:00.00]".utf8)),
            ]
            default: responses = []
            }
            let service = try await NativeMusicFixture.service(store, scripts: scripts,
                qq: MusicNativeCandidateDiscoveryQQ(), http: MusicNativeLyricsTransport(responses))
            let queue = try await service.queue()
            let result = try await service.lyrics(occurrenceId: "entry-1", sourceKey: queue.entries[0].track.sources[0].sourceKey)
            let calls = await scripts.calls()
            if ["wy", "kw", "mg"].contains(platform) {
                XCTAssertEqual(result.lines.map(\.text), ["Script lyric"])
                XCTAssertEqual(calls, ["empty", "usable"])
            } else {
                XCTAssertEqual(result.state, .none); XCTAssertTrue(calls.isEmpty)
            }
            await service.close()
    }

    func testNativeErrorIsNotAnEmptyLyricResultAndDoesNotStartScripts() async throws {
        let track = MusicNativeCandidateDiscoveryFixture.track("wy", id: "123")
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: MusicModel.normalize(track))
        let scripts = MusicLyricFallbackScripts("wy")
        let service = try await NativeMusicFixture.service(store, scripts: scripts,
            http: MusicNativeLyricsTransport([.init(status: 503, data: Data())]))
        let queue = try await service.queue()
        await scriptExpectFailure(.upstreamFailed) {
            try await service.lyrics(occurrenceId: "entry-1", sourceKey: queue.entries[0].track.sources[0].sourceKey)
        }
        let calls = await scripts.calls(); XCTAssertTrue(calls.isEmpty)
        await service.close()
    }
}
