import XCTest
import Foundation
import MeowAccounts
import MeowMusicFeature
import MeowJSON
@testable import MeowMusicPlatform

final class MusicKugouAudioTests: XCTestCase {
    func testExplicitOfficialQualitiesSelectTheirOwnHashSizeAndTrackerKey() async throws {
        let s = MusicKugouTestSystem(), credential = try MusicKugouFixture.credential()
        let capabilities = try await s.provider.capabilities(mid: MusicKugouFixture.baseHash, credential: credential)
        XCTAssertEqual(capabilities, ["128k", "320k", "flac", "flac24bit"])
        let cases = [
            ("128k", "128", MusicKugouFixture.baseHash), ("320k", "320", MusicKugouFixture.hqHash),
            ("flac", "flac", MusicKugouFixture.flacHash), ("flac24bit", "high", MusicKugouFixture.highHash)
        ]
        for (quality, _, _) in cases {
            let value = try await s.provider.resolve(mid: MusicKugouFixture.baseHash, quality: quality, credential: credential)
            let row = try MusicKugouFixture.object(value)
            XCTAssertEqual(Set(row.keys), ["url", "expiresAt", "qualityVerified", "cacheAllowed", "licenseExpiresAt"])
            XCTAssertEqual(row["cacheAllowed"], .bool(true))
            XCTAssertEqual(row["licenseExpiresAt"], .null, "Membership is not an audio grant")
            XCTAssertEqual(row["expiresAt"], .null); XCTAssertEqual(row["qualityVerified"], .bool(false))
            XCTAssertNil(row["headers"])
            let text = String(decoding: try JSONEncoder().encode(value), as: UTF8.self)
            XCTAssertFalse(text.contains(MusicKugouFixture.token)); XCTAssertFalse(text.contains("synthetic-private-vip"))
        }
        let requests = await s.http.recorded()
        let tracker = requests.filter { $0.url.path == "/v5/url" }
        XCTAssertEqual(tracker.count, 4)
        for (request, entry) in zip(tracker, cases) {
            let q = try MusicKugouFixture.fields(request)
            XCTAssertEqual(request.headers["x-router"], "trackercdn.kugou.com")
            XCTAssertEqual(q["quality"], entry.1); XCTAssertEqual(q["hash"], entry.2.lowercased())
            XCTAssertEqual(q["behavior"], "play"); XCTAssertEqual(q["IsFreePart"], "0")
            XCTAssertEqual(q["album_id"], "11"); XCTAssertEqual(q["album_audio_id"], "22")
            XCTAssertEqual(q["clientver"], "11430"); XCTAssertEqual(q["version"], "11430")
            let expected = MusicQQWire.md5(entry.2.lowercased() + "57ae12eb6890223e355ccfcb74edf70d1005" +
                "176905054734845483393488264506404844848" + "42")
            XCTAssertEqual(q["key"], expected)
        }
    }
    func testUnsupportedQualityAndMissingExactResourceNeverFallBack() async throws {
        let s = MusicKugouTestSystem()
        do { _ = try await s.provider.resolve(mid: MusicKugouFixture.baseHash, quality: "master", credential: MusicKugouFixture.credential()); XCTFail("Invented quality") }
        catch { XCTAssertEqual(error as? MusicServiceError, .qualityDisabled) }
        let initial = await s.http.recorded()
        XCTAssertTrue(initial.isEmpty)
        var song = MusicKugouFixture.song(); song.removeValue(forKey: "hash_flac")
        await s.http.respond("/v1/audio/audio", [try MusicKugouFixture.response(["status": 1, "data": [song]])])
        do { _ = try await s.provider.resolve(mid: MusicKugouFixture.baseHash, quality: "flac", credential: MusicKugouFixture.credential()); XCTFail("Downgraded missing resource") }
        catch { XCTAssertEqual(error as? MusicServiceError, .qualityDisabled) }
        let requests = await s.http.recorded()
        XCTAssertFalse(requests.contains { $0.url.path == "/v5/url" })
    }
    func testUnknownExpiredAndNonVIPMembershipDoesNotPreventUpstreamResolution() async throws {
        for data in [
            [:] as [String: Any],
            ["userid": 42, "is_vip": 0],
            ["userid": 42, "is_vip": 1, "vip_end_time": 1_799_999_999],
            ["userid": 42, "is_vip": 1, "vip_end_time": "not-a-date"],
            ["userid": 42, "busi_vip": [["product_type": "future-product", "is_vip": 1]]]
        ] {
            let s = MusicKugouTestSystem()
            await s.http.respond("/v1/get_union_vip", [try MusicKugouFixture.response(["status": 1, "data": data])])
            let capabilities = try await s.provider.capabilities(mid: MusicKugouFixture.baseHash, credential: MusicKugouFixture.credential())
            XCTAssertTrue(capabilities.contains("flac"))
            _ = try await s.provider.resolve(mid: MusicKugouFixture.baseHash, quality: "flac", credential: MusicKugouFixture.credential())
            let requests = await s.http.recorded()
            XCTAssertTrue(requests.contains { $0.url.path == "/v5/url" })
            XCTAssertFalse(requests.contains { $0.url.path == "/v1/get_union_vip" })
        }
    }
    func testMembershipAndMetadataIdentityCannotCrossAccountsOrHashes() async throws {
        let s = MusicKugouTestSystem()
        await s.http.respond("/v1/get_union_vip", [try MusicKugouFixture.response(["status": 1, "data": ["userid": 99, "is_vip": 1]])])
        let capabilities = try await s.provider.capabilities(mid: MusicKugouFixture.baseHash, credential: MusicKugouFixture.credential())
        XCTAssertTrue(capabilities.contains("flac"), "Unrequested membership data is not playback authority")
        await s.http.respond("/v1/get_union_vip", [])
        var song = MusicKugouFixture.song(); song["hash"] = MusicKugouFixture.hqHash
        await s.http.respond("/v1/audio/audio", [try MusicKugouFixture.response(["errcode": 0, "data": [song]])])
        do { _ = try await s.provider.resolve(mid: MusicKugouFixture.baseHash, quality: "flac", credential: MusicKugouFixture.credential()); XCTFail("Other hash metadata") }
        catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
    }
    func testTrackerRejectsTrialsUnknownPermissionHashAlbumSizeAndMissingFullEvidence() async throws {
        let s = MusicKugouTestSystem()
        let mutations: [(String, Any?)] = [
            ("hash", MusicKugouFixture.baseHash), ("fileSize", 299), ("IsFreePart", 1), ("IsFreePart", "unknown"),
            ("album_id", 999), ("album_audio_id", 999), ("privilege", 1), ("pay_type", 1), ("fail_process", 1),
            ("priv_status", 0), ("freeTrialInfo", NSNull()), ("freePart", [:] as [String: Any]),
            ("IsFreePart", nil), ("fileSize", nil), ("hash", nil), ("fileHash", MusicKugouFixture.baseHash)
        ]
        for (key, value) in mutations {
            var root = MusicKugouFixture.audio()
            root[key] = value
            await s.http.respond("/v5/url", [try MusicKugouFixture.response(root)])
            do {
                _ = try await s.provider.resolve(mid: MusicKugouFixture.baseHash, quality: "flac", credential: MusicKugouFixture.credential())
                XCTFail("Unconfirmed candidate \(key) accepted")
            } catch {
                XCTAssertTrue([MusicServiceError.invalidMediaCandidate, .targetChanged, .upstreamInvalid].contains(error as? MusicServiceError ?? .unavailable))
            }
        }
    }
    func testNestedTrackerTrialAndConflictingURLsAreNotHiddenByTopLevelSuccess() async throws {
        let s = MusicKugouTestSystem()
        let cases: [[String: Any]] = [
            ["status": 1, "data": MusicKugouFixture.audio(), "trans_param": ["free_part": 1]],
            ["status": 1, "data": MusicKugouFixture.audio(), "url": "https://fs.kugou.com/second.flac"],
            ["status": 1, "data": MusicKugouFixture.audio(), "trans_param": ["code": 403]],
            ["status": 1, "data": MusicKugouFixture.audio(), "hash": MusicKugouFixture.baseHash]
        ]
        for root in cases {
            await s.http.respond("/v5/url", [try MusicKugouFixture.response(root)])
            do { _ = try await s.provider.resolve(mid: MusicKugouFixture.baseHash, quality: "flac", credential: MusicKugouFixture.credential()); XCTFail("Contradictory tracker accepted") } catch {}
        }
    }
    func testMediaURLsCannotExportAccountTokensOrBecomeArbitraryHTTP() async throws {
        let s = MusicKugouTestSystem()
        for url in [
            "http://fs.kugou.com/synthetic.flac", "https://127.0.0.1/a", "https://localhost/a",
            "https://fs.kugou.com.evil.invalid/a", "https://user@fs.kugou.com/a", "https://fs.kugou.com:8443/a",
            "https://fs.kugou.com/a#fragment", "https://fs.kugou.com./a",
            "https://fs.kugou.com/a?key=synthetic-kg-token", "https://fs.kugou.com/a?key=synthetic%2Dkg%2Dtoken",
            "https://fs.kugou.com/a?key=synthetic-private-vip"
        ] {
            var root = MusicKugouFixture.audio(); root["url"] = [url]
            await s.http.respond("/v5/url", [try MusicKugouFixture.response(root)])
            do { _ = try await s.provider.resolve(mid: MusicKugouFixture.baseHash, quality: "flac", credential: MusicKugouFixture.credential()); XCTFail("Unsafe URL \(url)") }
            catch { XCTAssertEqual(error as? MusicServiceError, .invalidMediaCandidate) }
        }
        var root = MusicKugouFixture.audio()
        root["url"] = ["https://fs.kugou.com/synthetic.flac", "https://127.0.0.1/backup"]
        await s.http.respond("/v5/url", [try MusicKugouFixture.response(root)])
        do { _ = try await s.provider.resolve(mid: MusicKugouFixture.baseHash, quality: "flac", credential: MusicKugouFixture.credential()); XCTFail("Unsafe backup ignored") }
        catch { XCTAssertEqual(error as? MusicServiceError, .invalidMediaCandidate) }
    }
    func testMembershipExpiryDoesNotRejectUpstreamAudioButRevokeStillRejectsLateCandidate() async throws {
        for expire in [true, false] {
            let s = MusicKugouTestSystem(), gate = MusicAccountTestGate()
            await s.http.hold("/v5/url", gate)
            let task = Task { try await s.provider.resolve(mid: MusicKugouFixture.baseHash, quality: "flac", credential: MusicKugouFixture.credential()) }
            await gate.started()
            if expire { s.clock.advance(601) } else { await s.provider.revoke() }
            await gate.release()
            if expire {
                let audio = try await task.value, value = try MusicKugouFixture.object(audio)
                XCTAssertEqual(value["licenseExpiresAt"], .null)
                XCTAssertNotNil(value["url"])
            } else {
                do { _ = try await task.value; XCTFail("Revoked media grant") }
                catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
            }
        }
    }
    func testLicenseDatesHaveShanghaiSemanticsNotURLTTL() throws {
        let seconds = try XCTUnwrap(KugouMusicAccountAdapter.expiry("1800000000"))
        XCTAssertEqual(try KugouMusicAccountAdapter.expiry(1_800_000_000_000), seconds)
        XCTAssertEqual(try KugouMusicAccountAdapter.expiry("2026-10-23 00:00:00"),
                       try KugouMusicAccountAdapter.expiry("2026-10-22T16:00:00Z"))
        XCTAssertNil(try KugouMusicAccountAdapter.expiry(0))
        XCTAssertThrowsError(try KugouMusicAccountAdapter.expiry("tomorrow"))
    }
}
