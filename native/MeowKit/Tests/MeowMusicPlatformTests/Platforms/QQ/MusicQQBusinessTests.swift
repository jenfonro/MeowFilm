import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicPlatform

private actor MusicQQSongListHTTP: MusicAccountHTTPTransport {
    private let base: MusicAccountTestHTTP
    private let count: Int
    private let repeatsPage: Bool
    private let wideRows: Bool
    private var requests: [MusicAccountHTTPRequest] = []
    init(clock: MusicAccountTestClock, count: Int = 2001, repeatsPage: Bool = false, wideRows: Bool = false) {
        base = .init(clock: clock); self.count = count
        self.repeatsPage = repeatsPage; self.wideRows = wideRows
    }
    func recorded() -> [MusicAccountHTTPRequest] { requests }
    func send(_ request: MusicAccountHTTPRequest) async throws -> MusicAccountHTTPResponse {
        requests.append(request)
        guard try MusicAccountTestHTTP.method(request) == "CgiGetDiss" else { return try await base.send(request) }
        let params = try MusicQQWire.object(MusicQQWire.object(MusicAccountTestHTTP.fields(request)["req_0"])["param"])
        let offset = try MusicQQWire.integer(params["song_begin"])
        guard try MusicQQWire.integer(params["song_num"]) == 100 else { throw MusicServiceError.invalidRequest }
        let end = min(offset + 100, count)
        let rows = (offset..<end).map { position -> [String: Any] in
            let index = repeatsPage ? position % 100 : position
            var row = MusicAccountFixture.song("BULK\(index)", id: index + 101)
            if wideRows {
                row["title"] = String(repeating: "t", count: 1024)
                row["singer"] = (0..<3).map { _ in ["name": String(repeating: "s", count: 256)] }
                var album = row["album"] as? [String: Any] ?? [:]
                album["name"] = String(repeating: "a", count: 1024); row["album"] = album
            }
            return row
        }
        return try MusicAccountFixture.rpc([
            "songlist": rows, "total_song_num": count, "songlist_size": rows.count, "hasmore": end < count ? 1 : 0,
            "dirinfo": ["tid": 501, "dirId": 7, "creator": ["musicid": 101]]
        ])
    }
}

final class MusicQQBusinessTests: XCTestCase {
    func testCollectionSongDoubleReadKeepsMoreThanTwoThousandSongsAndExactOffsets() async throws {
        let clock = MusicAccountTestClock(), http = MusicQQSongListHTTP(clock: clock)
        let provider = QQMusicAccountAdapter(transport: http, now: { clock.now() })
        let rows = try await provider.tracks(collectionID: "501", credential: MusicAccountFixture.credential())
        XCTAssertEqual(rows.count, 2001)
        let ids = try rows.map { try MusicQQWire.string(MusicQQWire.decode(JSONEncoder().encode($0))["songmid"]) }
        XCTAssertEqual(ids, (0..<2001).map { "BULK\($0)" })
        let requests = await http.recorded()
        let scans = try requests.filter { try MusicAccountTestHTTP.method($0) == "CgiGetDiss" }
        let offsets = try scans.map {
            try MusicQQWire.integer(MusicQQWire.object(
                MusicQQWire.object(MusicAccountTestHTTP.fields($0)["req_0"])["param"])["song_begin"])
        }
        let expected = Array(stride(from: 0, to: 2001, by: 100))
        XCTAssertEqual(offsets, expected + expected)
        await provider.revoke()
    }

    func testCollectionSongReadStillRejectsRepeatedPagesAndCumulativeByteOverflow() async throws {
        for wideRows in [false, true] {
            let clock = MusicAccountTestClock()
            let http = MusicQQSongListHTTP(clock: clock, repeatsPage: !wideRows, wideRows: wideRows)
            let provider = QQMusicAccountAdapter(transport: http, now: { clock.now() })
            do {
                _ = try await provider.tracks(collectionID: "501", credential: MusicAccountFixture.credential())
                XCTFail("invalid full song list")
            } catch { XCTAssertEqual(error as? MusicServiceError, wideRows ? .limit : .upstreamInvalid) }
            await provider.revoke()
        }
    }

    func testRadarRecommendationsContinueBeyondFormerTwoHundredPages() async throws {
        let clock = MusicAccountTestClock(), http = MusicAccountTestHTTP(clock: clock)
        await http.replies("GetRadarSong", try MusicAccountFixture.rpc([
            "HasMore": true, "VecSongs": [["Track": MusicAccountFixture.song()]]
        ]))
        let provider = QQMusicAccountAdapter(transport: http, now: { clock.now() })
        for page in [200, 201] {
            let result = try await provider.recommendations(kind: "radar", page: page,
                                                            credential: MusicAccountFixture.credential())
            XCTAssertEqual(result.rows.count, 1); XCTAssertEqual(result.nextPage, page + 1)
        }
        let requests = await http.recorded()
        let radar = try requests.filter { try MusicAccountTestHTTP.method($0) == "GetRadarSong" }
        let pages = try radar.map {
            try MusicQQWire.integer(MusicQQWire.object(
                MusicQQWire.object(MusicAccountTestHTTP.fields($0)["req_0"])["param"])["Page"])
        }
        XCTAssertEqual(pages, [200, 201])
        await provider.revoke()
    }

    func testRadarStillRejectsEmptyContinuationAndUnrepresentableNextPage() async throws {
        let clock = MusicAccountTestClock(), http = MusicAccountTestHTTP(clock: clock)
        let provider = QQMusicAccountAdapter(transport: http, now: { clock.now() })
        await http.replies("GetRadarSong", try MusicAccountFixture.rpc(["HasMore": true, "VecSongs": []]))
        do {
            _ = try await provider.recommendations(kind: "radar", page: 201, credential: MusicAccountFixture.credential())
            XCTFail("non-progressing response")
        } catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        await http.replies("GetRadarSong", try MusicAccountFixture.rpc([
            "HasMore": true, "VecSongs": [["Track": MusicAccountFixture.song()]]
        ]))
        do {
            _ = try await provider.recommendations(kind: "radar", page: MusicPlatformJSON.maximumInteger,
                                                    credential: MusicAccountFixture.credential())
            XCTFail("inexact JSON page number")
        } catch { XCTAssertEqual(error as? MusicServiceError, .limit) }
        await provider.revoke()
    }
}
