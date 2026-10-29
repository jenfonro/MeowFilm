import Foundation
import XCTest
@testable import MeowPlayback

final class HLSPlaylistTests: XCTestCase {
    func testMasterAudioSubtitlesIFramesAndRelativeSignedURIsAreRegisteredNotExposed() throws {
        let base = URL(string: "https://source.fixture.invalid/path/master.m3u8?synthetic=private")!
        let registry = try HLSResourceRegistry(url: base)
        let input = """
        #EXTM3U
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="a",NAME="音频",URI="audio/list.m3u8?synthetic=one,two"
        #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="s",NAME="字幕",URI="/sub/list.m3u8"
        #EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=1000,URI="iframes.m3u8"
        #EXT-X-STREAM-INF:BANDWIDTH=1000000,AUDIO="a",SUBTITLES="s"
        video/main.m3u8

        """
        let output = String(decoding: try HLSPlaylist.rewrite(Data(input.utf8), base: base, inheritsHeaders: true, registry: registry), as: UTF8.self)
        XCTAssertFalse(output.contains("synthetic")); XCTAssertFalse(output.contains("source.fixture.invalid"))
        XCTAssertTrue(output.contains("GROUP-ID=\"a\",NAME=\"音频\""))
        let paths = try pathsInPlaylist(output)
        XCTAssertEqual(paths.count, 4)
        let resources = try paths.map { try XCTUnwrap(registry.lookup(path: $0)) }
        XCTAssertTrue(resources.allSatisfy { $0.kind == .playlist && $0.permitsHeaders })
        XCTAssertTrue(resources.contains { $0.url.absoluteString == "https://source.fixture.invalid/path/audio/list.m3u8?synthetic=one,two" })
        XCTAssertTrue(resources.contains { $0.url.path == "/sub/list.m3u8" })
    }

    func testMapKeyAndImplicitByteRangeShareStableMediaMapping() throws {
        let base = try hlsPlan().url, registry = try HLSResourceRegistry(url: base)
        let input = """
        #EXTM3U
        #EXT-X-VERSION:6
        #EXT-X-TARGETDURATION:6
        #EXT-X-KEY:METHOD=AES-128,URI="key.bin",IV=0x00000000000000000000000000000001
        #EXT-X-MAP:URI="media.mp4",BYTERANGE="100@0"
        #EXTINF:6,
        #EXT-X-BYTERANGE:200@100
        media.mp4
        #EXTINF:6,
        #EXT-X-BYTERANGE:200
        media.mp4
        #EXT-X-ENDLIST

        """
        let output = String(decoding: try HLSPlaylist.rewrite(Data(input.utf8), base: base, inheritsHeaders: true, registry: registry), as: UTF8.self)
        XCTAssertTrue(output.contains("BYTERANGE=\"100@0\"")); XCTAssertTrue(output.contains("#EXT-X-BYTERANGE:200\n"))
        XCTAssertTrue(output.contains("IV=0x00000000000000000000000000000001"))
        let resources = try pathsInPlaylist(output).map { try XCTUnwrap(registry.lookup(path: $0)) }
        XCTAssertEqual(resources.map(\.kind), [.key, .media, .media, .media])
        XCTAssertEqual(resources[1].id, resources[2].id); XCTAssertEqual(resources[2].id, resources[3].id)
    }

    func testLiveSnapshotsRetainPreviousWindowAndPinnedResourcesButPruneExpiredMappings() throws {
        let base = try hlsPlan().url, registry = try HLSResourceRegistry(url: base)
        func rewrite(_ name: String) throws -> HLSResource {
            let bytes = Data("#EXTM3U\n#EXTINF:4,\n\(name).ts\n".utf8)
            let output = String(decoding: try HLSPlaylist.rewrite(bytes, base: base, inheritsHeaders: true, registry: registry), as: UTF8.self)
            return try XCTUnwrap(registry.lookup(path: XCTUnwrap(pathsInPlaylist(output).first)))
        }
        let first = try rewrite("one"); registry.retain(first)
        let second = try rewrite("two")
        _ = try rewrite("three")
        XCTAssertNotNil(registry.lookup(path: registry.path(first)))
        XCTAssertNotNil(registry.lookup(path: registry.path(second)))
        registry.release(first)
        XCTAssertNil(registry.lookup(path: registry.path(first)))
        _ = try rewrite("four")
        XCTAssertNil(registry.lookup(path: registry.path(second)))
    }

    func testForeignCDNReceivesNoOriginGrantAndUnknownExtensionsNeverPassThrough() throws {
        let base = try hlsPlan().url, registry = try HLSResourceRegistry(url: base)
        let output = String(decoding: try HLSPlaylist.rewrite(
            Data("#EXTM3U\n#EXTINF:4,\nhttps://cdn.fixture.invalid/segment.ts\n".utf8),
            base: base, inheritsHeaders: true, registry: registry), as: UTF8.self)
        let path = try XCTUnwrap(pathsInPlaylist(output).first)
        XCTAssertFalse(try XCTUnwrap(registry.lookup(path: path)).permitsHeaders)
        for tag in [
            "#EXT-X-KEY:METHOD=SAMPLE-AES,KEYFORMAT=\"com.apple.streamingkeydelivery\",URI=\"skd://key\"",
            "#EXT-X-PART:DURATION=1,URI=\"part.m4s\"",
            "#EXT-X-CONTENT-STEERING:SERVER-URI=\"https://other.invalid/route.json\"",
            "#EXT-X-DEFINE:NAME=\"host\",VALUE=\"https://other.invalid\"",
            "#EXT-X-DATERANGE:ID=\"ad\",CLASS=\"com.apple.hls.interstitial\",X-ASSET-LIST=\"https://other.invalid/ad\"",
            "#EXT-X-UNKNOWN:URI=\"https://other.invalid/escape\""
        ] {
            XCTAssertThrowsError(try HLSPlaylist.rewrite(Data("#EXTM3U\n\(tag)\n".utf8), base: base,
                                                       inheritsHeaders: true, registry: registry)) {
                XCTAssertEqual($0 as? PlaybackNetworkError, .hlsUnsupportedFeature)
            }
        }
    }

    func testMalformedOversizedAndUnsafePlaylistsCannotCreateRemoteEscape() throws {
        let base = try hlsPlan().url
        for input in ["not a playlist", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\n",
                      "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"one\",URI=\"two\"\n",
                      "#EXTM3U\n#EXT-X-BYTERANGE:9223372036854775807@1\nx.mp4\n",
                      "#EXTM3U\n#EXTINF:4,\nhttp://source.fixture.invalid/insecure.ts\n",
                      "#EXTM3U\n#EXTINF:4,\nhttp://127.0.0.1/internal\n",
                      "#EXTM3U\n#EXTINF:4,\nfile:///private/resource\n",
                      "#EXTM3U\n" + String(repeating: "x", count: HLSPlaylist.maximumInputBytes)] {
            let registry = try HLSResourceRegistry(url: base)
            XCTAssertThrowsError(try HLSPlaylist.rewrite(Data(input.utf8), base: base, inheritsHeaders: true, registry: registry))
        }
    }
}

func pathsInPlaylist(_ text: String) throws -> [String] {
    var paths: [String] = []
    for line in text.split(separator: "\n") {
        if line.hasPrefix("/") { paths.append(String(line)) }
        else if let start = line.range(of: "URI=\"")?.upperBound,
                let end = line[start...].firstIndex(of: "\"") { paths.append(String(line[start..<end])) }
    }
    return paths
}
