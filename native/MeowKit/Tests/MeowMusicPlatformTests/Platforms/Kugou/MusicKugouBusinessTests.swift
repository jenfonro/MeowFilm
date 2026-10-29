import XCTest
import Foundation
import MeowAccounts
import MeowMusicFeature
import MeowJSON
@testable import MeowMusicPlatform

final class MusicKugouBusinessTests: XCTestCase {
    func testDailyAndFMUseExactRoutesAndDoNotPublishPrivateContextOrContinuation() async throws {
        let s = MusicKugouTestSystem(), credential = try MusicKugouFixture.credential()
        for kind in ["daily", "fm"] {
            let result = try await s.provider.recommendations(kind: kind, page: 1, credential: credential)
            XCTAssertNil(result.nextPage); XCTAssertNil(result.expiresAt); XCTAssertEqual(result.rows.count, 1)
            let row = try MusicKugouFixture.object(result.rows[0])
            XCTAssertEqual(row["musicId"], .string(MusicKugouFixture.baseHash))
            XCTAssertEqual(row["songmid"], .string("21")); XCTAssertEqual(row["albumAudioId"], .string("22"))
            let text = String(decoding: try JSONEncoder().encode(result.rows), as: UTF8.self)
            for secret in [MusicKugouFixture.token, "synthetic-private-vip", "must-not-be-forwarded", "not-a-continuation", "synthetic-dfid"] {
                XCTAssertFalse(text.contains(secret))
            }
            XCTAssertNil(row["_recommendation"])
        }
        let requests = await s.http.recorded()
        XCTAssertEqual(requests.map(\.url.path), ["/everyday_song_recommend", "/v2/personal_recommend"])
        let daily = requests[0], fm = requests[1]
        XCTAssertEqual(try MusicKugouFixture.fields(daily)["platform"], "ios"); XCTAssertNil(daily.body)
        let body = try MusicQQWire.decode(fm.body!)
        XCTAssertEqual(body["action"] as? String, "login"); XCTAssertEqual(body["mode"] as? String, "normal")
        XCTAssertEqual(body["platform"] as? String, "ios")
        XCTAssertEqual(body["token"] as? String, MusicKugouFixture.token)
        XCTAssertEqual(body["userid"] as? String, "42"); XCTAssertEqual(body["kguid"] as? String, "42")
        let before = requests.count
        for (kind, page) in [("fm", 2), ("daily", 0), ("guess", 1), ("radar", 1)] {
            do { _ = try await s.provider.recommendations(kind: kind, page: page, credential: credential); XCTFail("Unsupported recommendation accepted") }
            catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
        }
        let after = await s.http.recorded().count
        XCTAssertEqual(before, after)
    }
    func testSongIdentityResourcesAndRepeatedHashAreNotCollapsed() throws {
        let song = try MusicKugouSong(MusicKugouFixture.song())
        XCTAssertEqual(song.hash, MusicKugouFixture.baseHash); XCTAssertEqual(song.audioID, "21")
        XCTAssertEqual(song.title, "合成歌曲"); XCTAssertEqual(song.artist, "合成歌手"); XCTAssertEqual(song.duration, 180)
        XCTAssertEqual(song.resources["flac24bit"], .init(hash: MusicKugouFixture.highHash, size: 400))
        let dto = try MusicKugouFixture.object(song.dto), native = try MusicKugouFixture.object(try XCTUnwrap(dto["_types"]))
        guard case .array(let types)? = dto["types"] else { return XCTFail("Missing types") }
        for value in types { XCTAssertEqual(Set(try MusicKugouFixture.object(value).keys), ["type", "hash", "size"]) }
        for value in native.values { XCTAssertEqual(Set(try MusicKugouFixture.object(value).keys), ["hash", "size"]) }
        var raw = MusicKugouFixture.song()
        raw["audio_info"] = ["audio_id": 99]
        XCTAssertThrowsError(try MusicKugouSong(raw)) { XCTAssertEqual($0 as? MusicServiceError, .targetChanged) }
        raw = MusicKugouFixture.song(); raw.removeValue(forKey: "Audioid")
        XCTAssertEqual(try MusicKugouSong(raw).audioID, "")
        raw["hash_high"] = String(repeating: "0", count: 32)
        XCTAssertNil(try MusicKugouSong(raw).resources["flac24bit"])
        raw["FileHash"] = MusicKugouFixture.flacHash
        XCTAssertThrowsError(try MusicKugouSong(raw))
    }
    func testReadOnlyDirectoryUsesTypeListIDAndFavoriteEvidenceNotNameAlone() async throws {
        let s = MusicKugouTestSystem()
        let rows = [MusicKugouFixture.collection(),
                    MusicKugouFixture.collection(listID: 8, special: 0),
                    MusicKugouFixture.collection(listID: 9, type: 1, owner: 99, count: 1)]
        await s.http.respond("/v7/get_all_list", [try MusicKugouFixture.directory(rows)])
        let context = try await s.provider.context(MusicKugouFixture.credential())
        let entries = try await s.provider.directory(context)
        XCTAssertEqual(entries.map(\.id), ["0:7", "0:8", "1:9"])
        XCTAssertEqual(entries.map(\.favorite), [true, false, false])
        let values = try await s.provider.collectionRows(credential: MusicKugouFixture.credential())
        XCTAssertEqual(values.count, 3)
        for value in values { XCTAssertEqual(Set(try MusicKugouFixture.object(value).keys), ["id", "title", "count", "owned"]) }
        let subscribed = try MusicKugouFixture.object(values[2])
        XCTAssertEqual(subscribed["owned"], .bool(false))
        let requests = await s.http.recorded()
        XCTAssertTrue(requests.allSatisfy { ["/v7/get_all_list", "/v3/get_my_info"].contains($0.url.path) })
    }
    func testStableDirectoryRejectsSameCountRenameOrDuplicateFavorites() async throws {
        let s = MusicKugouTestSystem()
        await s.http.respond("/v7/get_all_list", [try MusicKugouFixture.directory(),
            try MusicKugouFixture.directory([MusicKugouFixture.collection(name: "改变的名称")])])
        do { _ = try await s.provider.collectionRows(credential: MusicKugouFixture.credential()); XCTFail("Changed directory accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        await s.http.respond("/v7/get_all_list", [try MusicKugouFixture.directory([
            MusicKugouFixture.collection(), MusicKugouFixture.collection(listID: 8)])])
        do { _ = try await s.provider.collectionRows(credential: MusicKugouFixture.credential()); XCTFail("Duplicate system favorites accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
    }
    func testOwnedTracksDoubleReadPreservesFileIDOccurrencesAndSort() async throws {
        let s = MusicKugouTestSystem()
        let values = try await s.provider.tracks(collectionID: "0:7", credential: MusicKugouFixture.credential())
        XCTAssertEqual(values.count, 2)
        let first = try MusicKugouFixture.object(values[0]), second = try MusicKugouFixture.object(values[1])
        XCTAssertEqual(first["hash"], second["hash"])
        XCTAssertEqual(first["fileid"], .string("501")); XCTAssertEqual(second["fileid"], .string("502"))
        let requests = await s.http.recorded()
        XCTAssertEqual(requests.filter { $0.url.path == "/v4/get_list_all_file_v3" }.count, 2)
        XCTAssertEqual(requests.filter { $0.url.path == "/v7/get_all_list" }.count, 2)
        for request in requests where request.url.path == "/v4/get_list_all_file_v3" {
            let body = try MusicQQWire.decode(request.body!)
            XCTAssertEqual(body["listid"] as? String, "7")
            XCTAssertEqual((body["pagesize"] as? NSNumber)?.intValue, 30)
        }
    }
    func testSubscriptionsUseGlobalIDNotListIDAndNoInventedRouter() async throws {
        let s = MusicKugouTestSystem()
        await s.http.respond("/v7/get_all_list", [try MusicKugouFixture.directory([
            MusicKugouFixture.collection(listID: 9, type: 1, owner: 99, name: "订阅", special: 0, count: 1)])])
        let values = try await s.provider.tracks(collectionID: "1:9", credential: MusicKugouFixture.credential())
        XCTAssertEqual(values.count, 1)
        XCTAssertNil(try MusicKugouFixture.object(values[0])["fileid"])
        let requests = await s.http.recorded()
        let subscription = requests.filter { $0.url.path == "/pubsongs/v2/get_other_list_file_nofilt" }
        XCTAssertEqual(subscription.count, 2)
        for request in subscription {
            XCTAssertEqual(request.url.host, "gateway.kugou.com"); XCTAssertNil(request.headers["x-router"])
            XCTAssertEqual(try MusicKugouFixture.fields(request)["global_collection_id"], "synthetic-global")
        }
        await s.http.respond("/pubsongs/v2/get_other_list_file_nofilt", [
            try MusicKugouFixture.response(["status": 1, "data": ["songs": [], "list_info": ["global_collection_id": "wrong", "count": 0]]])
        ])
        do { _ = try await s.provider.tracks(collectionID: "1:9", credential: MusicKugouFixture.credential()); XCTFail("Wrong subscription accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
    }
    func testPagingCountVersionAndDuplicateFileIDAreNotPartialSuccess() async throws {
        for mode in ["version", "count", "duplicate", "early-end"] {
            let s = MusicKugouTestSystem()
            let count = mode == "duplicate" ? 2 : 31
            await s.http.respond("/v7/get_all_list", [try MusicKugouFixture.directory([MusicKugouFixture.collection(count: count)])])
            let first = (1...30).map { MusicKugouFixture.song(fileID: $0, order: $0) }
            switch mode {
            case "version":
                await s.http.respond("/v4/get_list_all_file_v3", [try MusicKugouFixture.members(first, count: 31),
                    try MusicKugouFixture.members([MusicKugouFixture.song(fileID: 31)], count: 31, version: 6)])
            case "count":
                await s.http.respond("/v4/get_list_all_file_v3", [try MusicKugouFixture.members(first, count: 31),
                    try MusicKugouFixture.members([MusicKugouFixture.song(fileID: 31)], count: 32)])
            case "duplicate":
                await s.http.respond("/v4/get_list_all_file_v3", [try MusicKugouFixture.members([
                    MusicKugouFixture.song(), MusicKugouFixture.song()])])
            default:
                await s.http.respond("/v4/get_list_all_file_v3", [try MusicKugouFixture.members(first, count: 31),
                    try MusicKugouFixture.members([], count: 31)])
            }
            do { _ = try await s.provider.tracks(collectionID: "0:7", credential: MusicKugouFixture.credential()); XCTFail("Partial \(mode) accepted") }
            catch { XCTAssertTrue([MusicServiceError.staleResult, .upstreamInvalid].contains(error as? MusicServiceError ?? .unavailable)) }
        }
    }
    func testSameCountMemberReplacementAndDirectoryChangeAreRejected() async throws {
        for changeMembers in [true, false] {
            let s = MusicKugouTestSystem()
            if changeMembers {
                await s.http.respond("/v4/get_list_all_file_v3", [
                    try MusicKugouFixture.members(),
                    try MusicKugouFixture.members([MusicKugouFixture.song(fileID: 501), MusicKugouFixture.song(fileID: 503)])
                ])
            } else {
                await s.http.respond("/v7/get_all_list", [try MusicKugouFixture.directory(),
                    try MusicKugouFixture.directory([MusicKugouFixture.collection(name: "已修改")])])
            }
            do { _ = try await s.provider.tracks(collectionID: "0:7", credential: MusicKugouFixture.credential()); XCTFail("Unstable snapshot accepted") }
            catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        }
    }
    func testCancelDuringReadDoesNotPublishASecondSnapshot() async throws {
        let s = MusicKugouTestSystem(), gate = MusicAccountTestGate()
        await s.http.hold("/v4/get_list_all_file_v3", gate)
        let task = Task { try await s.provider.tracks(collectionID: "0:7", credential: MusicKugouFixture.credential()) }
        await gate.started(); task.cancel(); await gate.release()
        do { _ = try await task.value; XCTFail("Cancelled read published") } catch {}
        let requests = await s.http.recorded()
        XCTAssertEqual(requests.filter { $0.url.path == "/v4/get_list_all_file_v3" }.count, 1)
    }
    func testDisplayMetadataCannotEchoAccountSecrets() async throws {
        for field in ["filename", "sizable_cover"] {
            let s = MusicKugouTestSystem()
            var song = MusicKugouFixture.song()
            song[field] = field == "filename" ? MusicKugouFixture.token :
                "https://imge.kugou.com/a?value=synthetic%2Dkg%2Dtoken"
            await s.http.respond("/everyday_song_recommend", [try MusicKugouFixture.response(["status": 1, "data": ["song_list": [song]]])])
            do { _ = try await s.provider.recommendations(kind: "daily", page: 1, credential: MusicKugouFixture.credential()); XCTFail("Display secret escaped") }
            catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        }
    }
}
