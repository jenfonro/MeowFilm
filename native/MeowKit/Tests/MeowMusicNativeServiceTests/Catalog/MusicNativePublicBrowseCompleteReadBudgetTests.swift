import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

private enum MusicNativePublicBrowseBudgetFixture {
    static let artwork = "https://p1.music.126.net/" + String(repeating: "a", count: 3500) + ".jpg"
    static let kgArtwork = "https://imge.kugou.com/" + String(repeating: "b", count: 3500) + ".jpg"

    static func wySong(id: Int) -> MJ {
        .obj([("id", .n(100_000 + id)), ("name", .string("Budget Song")), ("ar", .array([.obj([("name", .string("Budget Artist"))])])),
            ("al", .obj([("id", .n(301)), ("name", .string("Budget Album")), ("picUrl", .string(artwork))])),
            ("dt", .n(180000)), ("h", .obj([("size", .n(4096))]))])
    }

    static func kgSong(id: Int) -> MJ {
        let hash = String(format: "%032X", id)
        return .obj([("hash", .string(hash)),
            ("name", .string("Budget Artist - Budget Song")),
            ("singerinfo", .array([.obj([("name", .string("Budget Artist"))])])),
            ("album_id", .n(301)), ("albuminfo", .obj([("id", .n(301)), ("name", .string("Budget Album"))])),
            ("album_audio_id", .n(100_000 + id)), ("Duration", .n(180)), ("cover", .string(kgArtwork))])
    }

    static func wyHead(count: Int) -> MJ {
        let ids: [MJ] = (1...count).map { .obj([("id", .n(100_000 + $0))]) }
        return .obj([("code", .n(200)), ("playlist", .obj([("id", .n(41)), ("name", .string("Budget playlist")),
            ("creator", .obj([("userId", .n(99)), ("nickname", .string("Creator"))])),
            ("trackCount", .n(count)), ("trackUpdateTime", .n(1)),
            ("trackIds", .array(ids)), ("tracks", .array([]))]))])
    }
}

private actor WYCompleteBudgetHTTP: MusicNativeHTTPTransport {
    private(set) var headCalls = 0
    private(set) var songCalls = 0

    func send(_ request: MusicNativeHTTPRequest) throws -> MusicNativeHTTPResponse {
        switch request.url.path {
        case "/eapi/v6/playlist/detail":
            headCalls += 1
            return .init(status: 200, data: MusicNativePublicBrowseBudgetFixture.wyHead(count: 2000).bytes)
        case "/weapi/v3/song/detail":
            let rows = ((songCalls * 30)..<min((songCalls + 1) * 30, 2000)).map {
                MusicNativePublicBrowseBudgetFixture.wySong(id: $0 + 1)
            }
            songCalls += 1
            return .init(status: 200, data: MJ.obj([("code", .n(200)), ("songs", .array(rows))]).bytes)
        default:
            throw XCTSkip("Unexpected WY request: \(request.url.path)")
        }
    }
}

private actor KGCompleteBudgetHTTP: MusicNativeHTTPTransport {
    private(set) var offsets: [Int] = []

    func send(_ request: MusicNativeHTTPRequest) throws -> MusicNativeHTTPResponse {
        XCTAssertEqual(request.url.path, "/pubsongs/v2/get_other_list_file_nofilt")
        let fields = Dictionary(uniqueKeysWithValues: try XCTUnwrap(URLComponents(url: request.url,
            resolvingAgainstBaseURL: false)?.queryItems).map { ($0.name, $0.value ?? "") })
        let offset = try XCTUnwrap(Int(fields["begin_idx"] ?? ""))
        let limit = try XCTUnwrap(Int(fields["pagesize"] ?? ""))
        XCTAssertEqual(limit, 100)
        offsets.append(offset)
        let end = min(offset + 100, 2000)
        let rows = (offset..<end).map { MusicNativePublicBrowseBudgetFixture.kgSong(id: $0 + 1) }
        var data = MJ.obj([("count", .n(2000)), ("songs", .array(rows))])
        if offset == 0 {
            data["list_info"] = .obj([("global_collection_id", .string("budget-list")),
                ("name", .string("Budget playlist")), ("imgurl", .string(MusicNativePublicBrowseBudgetFixture.kgArtwork)),
                ("count", .n(2000)), ("list_ver", .n(1))])
        }
        return .init(status: 200, data: MJ.obj([("status", .n(1)), ("error_code", .n(0)), ("data", data)]).bytes)
    }
}

final class MusicNativePublicBrowseCompleteReadBudgetTests: XCTestCase {
    private let wyCollection = MusicPublicBrowseCollection(platform: .netease, nativeID: "41", title: "Budget playlist")
    private let kgCollection = MusicPublicBrowseCollection(platform: .kugou, nativeID: "budget-list", title: "Budget playlist")

    func testWYCompleteReadFailsAtCumulativeTrackDataBudget() async throws {
        let http = WYCompleteBudgetHTTP()
        let service = try await NativeMusicFixture.service(http: http)
        let firstSong = try MusicNativePlatforms.browseWYSong(
            MusicNativePublicBrowseBudgetFixture.wySong(id: 1))
        let lastSong = try MusicNativePlatforms.browseWYSong(
            MusicNativePublicBrowseBudgetFixture.wySong(id: 2000))
        XCTAssertEqual(firstSong.data.count, lastSong.data.count)
        let pageCost = firstSong.data.count * 30
        let maximum = MusicRemoteQueueSource.maximumBytes
        let failingPage = maximum / pageCost + 1
        XCTAssertGreaterThanOrEqual(2000 / 30, failingPage)
        XCTAssertGreaterThan(2000 * firstSong.data.count, maximum)

        let request = MusicPublicBrowseQueueRequest(collection: wyCollection, snapshot: String(repeating: "0", count: 64))
        do {
            _ = try await service.publicQueueTracks(request)
            XCTFail("Complete read exceeded its track-data budget")
        } catch { XCTAssertEqual(error as? MusicServiceError, .limit) }

        let songCalls = await http.songCalls
        XCTAssertEqual(songCalls, failingPage)
        let headCalls = await http.headCalls
        XCTAssertEqual(headCalls, 1)
        await service.close()
    }

    func testKGCompleteReadFailsAtCumulativeTrackDataBudget() async throws {
        let http = KGCompleteBudgetHTTP()
        let service = try await NativeMusicFixture.service(http: http)
        let platforms = await service.publicPlatforms
        let row = try await platforms.browseKGPage("budget-list", offset: 0, limit: 100)
        XCTAssertEqual(row.tracks.count, 100)
        let firstTrackData = try XCTUnwrap(row.tracks.first).data
        XCTAssertTrue(row.tracks.allSatisfy { $0.data.count == firstTrackData.count })
        let lastSong = try await platforms.kugouSong(
            MusicNativePublicBrowseBudgetFixture.kgSong(id: 2000))
        XCTAssertEqual(lastSong.data.count, firstTrackData.count)
        let pageCost = row.tracks.reduce(0) { $0 + $1.data.count }
        let maximum = MusicRemoteQueueSource.maximumBytes
        let failingPage = maximum / pageCost + 1
        XCTAssertGreaterThanOrEqual(2000 / 100, failingPage)
        XCTAssertGreaterThan(2000 * firstTrackData.count, maximum)
        let checkpoint = await http.offsets
        XCTAssertEqual(checkpoint, [0])

        let request = MusicPublicBrowseQueueRequest(collection: kgCollection, snapshot: String(repeating: "0", count: 64))
        do {
            _ = try await service.publicQueueTracks(request)
            XCTFail("Complete read exceeded its track-data budget")
        } catch { XCTAssertEqual(error as? MusicServiceError, .limit) }

        let allOffsets = await http.offsets
        XCTAssertEqual(Array(allOffsets.dropFirst(checkpoint.count)),
            Array(stride(from: 0, through: (failingPage - 1) * 100, by: 100)))
        XCTAssertEqual(allOffsets.dropFirst(checkpoint.count).count, failingPage)
        XCTAssertEqual(allOffsets.count, checkpoint.count + failingPage)
        await service.close()
    }
}
