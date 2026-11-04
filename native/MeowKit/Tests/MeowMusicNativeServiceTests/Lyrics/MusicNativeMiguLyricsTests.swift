import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeMiguLyricsTests: XCTestCase {
    func testIndependentSignedInt64MRCVectorPreservesAbsoluteWordTimes() throws {
        let input = Data(MusicNativeLyricsFixture.mrc.utf8)
        XCTAssertEqual(try MusicNativeMiguLyrics.decrypt(input), "[1000,800]你(1000,300)好(1300,500)\n[2000,500]世界(2000,500)\n")
        let text = try MusicNativeMiguLyrics.mrc(input)
        XCTAssertEqual(text, "[1000,800](1000,300,0)你(1300,500,0)好\n[2000,500](2000,500,0)世界")
        let rows = try MusicNativeLyricsParser.parse(.init(text: text, translation: "[00:01.000]Hello\n[00:02.000]World"))
        XCTAssertEqual(rows.map(\.text), ["你好", "世界"]); XCTAssertEqual(rows.map(\.translation), ["Hello", "World"])
        XCTAssertEqual(rows.map(\.timing), ["word", "word"])
        XCTAssertEqual(rows[0].words[0].time, 1); XCTAssertEqual(rows[0].words[0].duration, 0.3, accuracy: 0.000001)
        XCTAssertEqual(rows[0].words[1].time, 1.3, accuracy: 0.000001)
        XCTAssertEqual(rows[0].words[1].duration, 0.5, accuracy: 0.000001)
        XCTAssertEqual(rows[1].words[0].time, 2); XCTAssertEqual(rows[1].words[0].duration, 0.5)
    }

    func testFreshExactResourceIgnoresCallerURLsAndCarriesNoAccountAuthority() async throws {
        let transport = MusicNativeLyricsTransport([
            .init(status: 200, data: MusicNativeLyricsFixture.miguResource(mrc: false).bytes),
            .init(status: 200, data: Data("\u{feff}[offset:100]\n[00:01.00]Native".utf8)),
            .init(status: 200, data: Data("[offset:100]\n[00:01.00]翻译".utf8))
        ])
        let port = MusicNativePlatforms(transport: transport, qq: NativeMusicQQFixture(), clock: { NativeMusicFixture.date })
        var info = MusicNativeLyricsFixture.miguIdentity
        info["mrcUrl"] = .string("http://127.0.0.1/private")
        info["lrcUrl"] = .string("https://evil.invalid/private")
        let value = try await port.lyrics(platform: "mg", info: info), rows = try MusicNativeLyricsParser.parse(value)
        XCTAssertEqual(rows[0].text, "Native"); XCTAssertEqual(rows[0].translation, "翻译")
        XCTAssertEqual(rows[0].time, 1.1, accuracy: 0.000001); XCTAssertEqual(value.romanization, "")
        let calls = await transport.calls(); XCTAssertEqual(calls.count, 3)
        XCTAssertEqual(calls[0].url.absoluteString, MusicNativeMiguLyrics.resourceURL)
        XCTAssertEqual(calls[0].method, "POST"); XCTAssertEqual(calls[0].body, Data("resourceId=123".utf8))
        XCTAssertEqual(calls[0].headers["Content-Type"], "application/x-www-form-urlencoded")
        XCTAssertEqual(calls[1].url.absoluteString, "https://d.musicapp.migu.cn/fixture.lrc")
        XCTAssertEqual(calls[2].url.absoluteString, "https://tyqk.migu.cn/fixture.trc")
        for call in calls {
            XCTAssertNil(call.headers["Cookie"]); XCTAssertNil(call.headers["Authorization"])
            XCTAssertTrue(MusicNativeMiguLyrics.permits(call))
        }
        XCTAssertEqual(calls[1].headers, MusicNativeMiguLyrics.fileHeaders)
        XCTAssertEqual(calls[2].headers, MusicNativeMiguLyrics.fileHeaders)
    }

    func testWrongResourceCopyrightAmbiguousRowsOrUntrustedURLCannotReachFileRequest() async throws {
        var wrongSong = MusicNativeLyricsFixture.miguResource(), wrongCopyright = wrongSong, ambiguous = wrongSong
        var missing = wrongSong, hostile = wrongSong
        wrongSong["resource"] = .array([.obj([("songId", .string("124")), ("copyrightId", .string("600123"))])])
        wrongCopyright["resource"] = .array([.obj([("songId", .string("123")), ("copyrightId", .string("600124"))])])
        ambiguous["resource"] = .array(ambiguous["resource"].array + ambiguous["resource"].array)
        missing["resource"] = .array([.obj([("songId", .string("123")), ("copyrightId", .string("600123"))])])
        var hostileRow = hostile["resource"].array[0]; hostileRow["mrcUrl"] = .string("https://evil.invalid/fixture.mrc")
        hostile["resource"] = .array([hostileRow])
        for root in [wrongSong, wrongCopyright, ambiguous, missing, hostile] {
            let transport = MusicNativeLyricsTransport([.init(status: 200, data: root.bytes)])
            let port = MusicNativePlatforms(transport: transport, qq: NativeMusicQQFixture(), clock: { NativeMusicFixture.date })
            do { _ = try await port.lyrics(platform: "mg", info: MusicNativeLyricsFixture.miguIdentity); XCTFail("wrong resource") } catch {}
            let calls = await transport.calls(); XCTAssertEqual(calls.count, 1)
        }
        var conflictingIdentity = MusicNativeLyricsFixture.miguIdentity; conflictingIdentity["songId"] = .string("124")
        let conflictingTransport = MusicNativeLyricsTransport([])
        let conflictingPort = MusicNativePlatforms(transport: conflictingTransport, qq: NativeMusicQQFixture(),
            clock: { NativeMusicFixture.date })
        do {
            _ = try await conflictingPort.lyrics(platform: "mg", info: conflictingIdentity)
            XCTFail("conflicting identity must not reach the resource request")
        } catch { XCTAssertEqual(error as? MusicServiceError, .lyricIdentityMismatch) }
        let conflictingCalls = await conflictingTransport.calls()
        XCTAssertEqual(conflictingCalls.count, 0)

        var identity = MusicNativeLyricsFixture.miguIdentity; identity["songId"] = .string("124")
        XCTAssertThrowsError(try MusicNativeMiguLyrics.Identity(identity)) {
            XCTAssertEqual($0 as? MusicServiceError, .lyricIdentityMismatch)
        }
    }

    func testMalformedCipherTimingAndChosenMRCFailureAreNotRetriedOrReplacedWithLRC() async throws {
        for input in ["", "invalid", String(repeating: "x", count: 32),
                      String(MusicNativeLyricsFixture.mrc.dropLast()), "+" + String(MusicNativeLyricsFixture.mrc.dropFirst())] {
            XCTAssertThrowsError(try MusicNativeMiguLyrics.mrc(Data(input.utf8)))
        }
        for bad in ["[1000,100]A(999,100)", "[1000,100]A(1000,101)", "[1000,100]A(1000,100)tail",
                    "[1000,100]A(99999999999999999999,100)", "[1000,100]A(1000,100)\0", "<html>denied</html>"] {
            XCTAssertThrowsError(try MusicNativeMiguLyrics.parseMRC(bad))
        }
        let transport = MusicNativeLyricsTransport([
            .init(status: 200, data: MusicNativeLyricsFixture.miguResource().bytes),
            .init(status: 200, data: Data("invalid MRC".utf8))
        ])
        let port = MusicNativePlatforms(transport: transport, qq: NativeMusicQQFixture(), clock: { NativeMusicFixture.date })
        do { _ = try await port.lyrics(platform: "mg", info: MusicNativeLyricsFixture.miguIdentity); XCTFail("must fail") }
        catch { XCTAssertEqual(error as? MusicServiceError, .invalidLyricResponse) }
        let calls = await transport.calls(); XCTAssertEqual(calls.count, 2)
        XCTAssertTrue(calls[1].url.path.hasSuffix(".mrc"), "MRC failure does not silently select LRC")
    }

    func testClosedTransportOnlyAdmitsExactAnonymousResourceAndLyricFileRoutes() async throws {
        let resource = try MusicNativeMiguLyrics.resourceRequest(id: "123")
        let file = try MusicNativeMiguLyrics.fileRequest("http://d.musicapp.migu.cn/fixture.mrc")
        XCTAssertEqual(file.url.scheme, "https")
        let transport = URLSessionMusicNativeHTTPTransport(); transport.close()
        for good in [resource, file] {
            XCTAssertTrue(MusicNativeMiguLyrics.permits(good))
            do { _ = try await transport.send(good); XCTFail("closed") }
            catch { XCTAssertEqual(error as? MusicServiceError, .closed) }
            for header in ["Cookie", "Authorization", "Referer"] {
                var headers = good.headers; headers[header] = "synthetic\r\ninjection"
                let bad = MusicNativeHTTPRequest(url: good.url, method: good.method, headers: headers, body: good.body)
                XCTAssertFalse(MusicNativeMiguLyrics.permits(bad))
                do { _ = try await transport.send(bad); XCTFail("invalid") }
                catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
            }
        }
        for link in ["https://evil.invalid/a.lrc", "https://d.musicapp.migu.cn.evil.invalid/a.lrc",
                     "https://user@d.musicapp.migu.cn/a.lrc", "https://d.musicapp.migu.cn:443/a.lrc",
                     "https://d.musicapp.migu.cn/api/private", "https://d.musicapp.migu.cn/a.lrc#fragment",
                     "https://d.musicapp.migu.cn/%2e%2e/a.lrc", "http://127.0.0.1/a.lrc"] {
            XCTAssertThrowsError(try MusicNativeMiguLyrics.fileRequest(link))
        }
        let badForm = MusicNativeHTTPRequest(url: resource.url, method: resource.method, headers: resource.headers,
            body: Data("resourceId=123&resourceId=124".utf8))
        XCTAssertFalse(MusicNativeMiguLyrics.permits(badForm))
        XCTAssertFalse(MusicNativeMiguLyrics.permits(.init(url: file.url, method: "POST", headers: file.headers)))
    }

    func testRevocationOrCancellationAfterResourceDoesNotFetchAnyLyricFile() async throws {
        for cancel in [false, true] {
            let gate = NativeMusicGate()
            let transport = MusicNativeLyricsTransport([.init(status: 200, data: MusicNativeLyricsFixture.miguResource().bytes)],
                gate: gate, gatedCall: 1)
            let port = MusicNativePlatforms(transport: transport, qq: NativeMusicQQFixture(), clock: { NativeMusicFixture.date })
            let task = Task { try await port.lyrics(platform: "mg", info: MusicNativeLyricsFixture.miguIdentity) }
            await gate.started()
            if cancel { task.cancel() } else { await port.revoke() }
            await gate.release()
            do { _ = try await task.value; XCTFail("late resource") }
            catch {
                if !cancel { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
                else { XCTAssertTrue(error is CancellationError) }
            }
            let calls = await transport.calls(); XCTAssertEqual(calls.count, 1)
        }
    }
}
