import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativePlatformLyricsTests: XCTestCase {
    func testSearchQueueLyricsUseNativeAPIWithoutPlaybackAttemptScriptOrAccount() async throws {
        for platform in ["kw", "mg"] {
            let store = NativeMusicMemoryDocument(), scripts = MusicNativeLyricsNoScript()
            let accounts = NativeMusicAccountFixture()
            let http = MusicNativeLyricsTransport(MusicNativeLyricsFixture.responses(platform: platform))
            let service = try await NativeMusicFixture.service(store, scripts: scripts, accounts: accounts, http: http)
            let capabilities = try await service.platforms()
            XCTAssertEqual(capabilities.filter { ["kw", "mg"].contains($0.id) }.map(\.lyrics), [true, true])
            let untouched = await http.calls(); XCTAssertTrue(untouched.isEmpty)
            let search = try await service.search(platform: platform, query: "Synthetic", page: 1, limit: 30)
            let track = try XCTUnwrap(search.items.first)
            let queued = try await service.changeQueue(.replace([track.ref], index: 0), expectedRevision: 0)
            let before = store.snapshot(), scriptCalls = await scripts.calls()
            let result = try await service.lyrics(occurrenceId: XCTUnwrap(queued.currentId), sourceKey: track.sources[0].sourceKey)
            XCTAssertEqual(result.state, .ready)
            XCTAssertEqual(result.lines.map(\.text), platform == "kw" ? ["AB", "C"] : ["你好", "世界"])
            XCTAssertEqual(result.lines[0].translation, platform == "kw" ? "Translation" : "Hello")
            XCTAssertTrue(result.lines.allSatisfy { $0.timing == "word" })
            let afterScripts = await scripts.calls(), accountCalls = await accounts.events()
            XCTAssertEqual(afterScripts, scriptCalls, "no source/list/resolve/lyric call, including on a failed Node")
            XCTAssertTrue(accountCalls.isEmpty)
            XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
            let played = try await service.playedSongs(); XCTAssertTrue(played.isEmpty)
            let calls = await http.calls(); XCTAssertEqual(calls.count, platform == "kw" ? 2 : 4)
            await service.close()
        }
    }

    func testCancellationSynchronousProtectionAndChangedOccurrenceRejectLateLyrics() async throws {
        for platform in ["kw", "mg"] {
            for reason in ["cancel", "lock", "occurrence"] {
                let store = NativeMusicMemoryDocument(), gate = NativeMusicGate()
                let http = MusicNativeLyricsTransport(MusicNativeLyricsFixture.responses(platform: platform),
                    gate: gate, gatedCall: platform == "kw" ? 2 : 3)
                let service = try await NativeMusicFixture.service(store, http: http)
                let search = try await service.search(platform: platform, query: "Synthetic", page: 1, limit: 30)
                let track = try XCTUnwrap(search.items.first)
                let queued = try await service.changeQueue(.replace([track.ref, track.ref], index: 0), expectedRevision: 0)
                let occurrence = try XCTUnwrap(queued.currentId), source = track.sources[0].sourceKey
                let task = Task { try await service.lyrics(occurrenceId: occurrence, sourceKey: source) }
                await gate.started()
                if reason == "cancel" { task.cancel() }
                else if reason == "lock" { service.revokeAccess(generation: 2) }
                else {
                    let queue = try await service.queue()
                    _ = try await service.changeQueue(.select(queue.entries[1].id), expectedRevision: queued.revision)
                }
                let beforeRelease = store.snapshot()
                await gate.release()
                do { _ = try await task.value; XCTFail("late lyrics after \(reason)") }
                catch {
                    let value = error as? MusicServiceError
                    XCTAssertTrue([.cancelled, .staleResult, .staleAccess, .accessUnavailable, .targetChanged].contains(value ?? .invalidResponse))
                }
                XCTAssertEqual(store.snapshot().0, beforeRelease.0); XCTAssertEqual(store.snapshot().2, beforeRelease.2)
                // MG must not start translation after the cancelled main-file read.
                await http.returned(count: platform == "kw" ? 2 : 3)
                let calls = await http.calls(); XCTAssertEqual(calls.count, platform == "kw" ? 2 : 3)
                await service.close()
            }
        }
    }

    func testPublicPlatformFailureRemainsFailureAndNeverFallsBackToLX() async throws {
        for platform in ["kw", "mg"] {
            let scripts = MusicNativeLyricsNoScript()
            var responses = MusicNativeLyricsFixture.responses(platform: platform)
            let failedIndex = platform == "kw" ? 1 : 2
            responses[failedIndex] = .init(status: 503, data: Data())
            let http = MusicNativeLyricsTransport(responses)
            let service = try await NativeMusicFixture.service(scripts: scripts, http: http)
            let search = try await service.search(platform: platform, query: "Synthetic", page: 1, limit: 30)
            let track = try XCTUnwrap(search.items.first)
            let queued = try await service.changeQueue(.replace([track.ref], index: 0), expectedRevision: 0)
            let prior = await scripts.calls()
            do {
                _ = try await service.lyrics(occurrenceId: XCTUnwrap(queued.currentId), sourceKey: track.sources[0].sourceKey)
                XCTFail("platform failure")
            } catch { XCTAssertEqual(error as? MusicServiceError, .upstreamFailed) }
            let calls = await http.calls(), after = await scripts.calls()
            XCTAssertEqual(calls.count, failedIndex + 1); XCTAssertEqual(after, prior)
            await service.close()
        }
    }
}

enum MusicNativeLyricsFixture {
    // Synthetic KW text encoded once using independent Python stdlib zlib,
    // base64 and XOR. No production encoder or reference executable was run.
    static let kuwo = Data(base64Encoded: "dHA9Y29udGVudA0KQ29udGVudC1UeXBlOiB0ZXh0L3BsYWluDQoNCnicFczBCoIwHAfgB+riHwrcwcNG0xxqOduPsVtqs3TgIWjl00ffA3zlQ9Z4pkU+JW0el8RdTSdFeTR0GFWR7nhM1UlHbYmc3ZrtEjAYYh4E1EBvl6CAcjXEKgDcgt31d3w3FDxksB19XD2vleAvL9r9TfK1crPydmmARP3PHIRBg7XnKct++kAuXg==")!
    // Synthetic independent signed-int64/UTF16LE vector also recorded by the
    // fixed meowmusic migu/client_test.go; never encrypted by this Swift decoder.
    static let mrc = "23345d6d70eb4614dd6d67fe21e85fa279918709c10d9fbc24fc3652996322208d46cdd8b9ea9cd84d689a7ef80b5230694439ca54dffd6bf1deb48135f7449880b41f63ad753fdabd334ae3abb54e65e21f4661891b80ff58cd20b90f5d4fe4e3e82bc7fe3eb10ac4ebf81edaed3972"
    static let expansionBomb = "eJztwYEAAAAAwyC2+Usd4QJVAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABwDUvceug=="
    static var miguIdentity: MJ { .obj([("songmid", .string("123")), ("copyrightId", .string("600123"))]) }
    static func miguResource(mrc: Bool = true) -> MJ {
        var row = MJ.obj([("songId", .string("123")), ("copyrightId", .string("600123")),
            ("lrcUrl", .string("https://d.musicapp.migu.cn/fixture.lrc")), ("trcUrl", .string("https://tyqk.migu.cn/fixture.trc"))])
        if mrc { row["mrcUrl"] = .string("https://d.musicapp.migu.cn/fixture.mrc") }
        return .obj([("returnCode", .string("000000")), ("resource", .array([row]))])
    }
    static func responses(platform: String) -> [MusicNativeHTTPResponse] {
        if platform == "kw" {
            return [.init(status: 200, data: MusicNativeKuwoSearchFixture.page([MusicNativeKuwoSearchFixture.song()], total: 1).bytes),
                    .init(status: 200, data: kuwo)]
        }
        return [.init(status: 200, data: MusicNativeMiguSearchFixture.page([[MusicNativeMiguSearchFixture.song()]], total: 1).bytes),
                .init(status: 200, data: miguResource().bytes), .init(status: 200, data: Data(mrc.utf8)),
                .init(status: 200, data: Data("[00:01.000]Hello\n[00:02.000]World".utf8))]
    }
}

actor MusicNativeLyricsTransport: MusicNativeHTTPTransport {
    private var responses: [MusicNativeHTTPResponse]
    private var sent: [MusicNativeHTTPRequest] = []
    private let gate: NativeMusicGate?
    private let gatedCall: Int
    private var completions = 0
    private var waiters: [(Int, CheckedContinuation<Void, Never>)] = []
    init(_ responses: [MusicNativeHTTPResponse], gate: NativeMusicGate? = nil, gatedCall: Int = 0) {
        self.responses = responses; self.gate = gate; self.gatedCall = gatedCall
    }
    func send(_ request: MusicNativeHTTPRequest) async throws -> MusicNativeHTTPResponse {
        sent.append(request)
        guard !responses.isEmpty else { throw MusicServiceError.upstreamFailed }
        let response = responses.removeFirst(), index = sent.count
        if index == gatedCall, let gate { await gate.wait() }
        completions += 1
        let ready = waiters.filter { $0.0 <= completions }; waiters.removeAll { $0.0 <= completions }
        ready.forEach { $0.1.resume() }
        return response
    }
    func calls() -> [MusicNativeHTTPRequest] { sent }
    func returned(count: Int) async {
        if completions < count { await withCheckedContinuation { waiters.append((count, $0)) } }
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
