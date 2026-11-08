import XCTest
import MeowFilmFeature

final class FilmSidebarSourcesTests: XCTestCase {
    func testHomeVideosKeepDirectoryOrderAndExactKeysWithoutDetectionGate() {
        let composed = FilmSourceKey(installationId: "cat", siteKey: "\u{00E9}")
        let decomposed = FilmSourceKey(installationId: "cat", siteKey: "e\u{0301}")
        let other = FilmSourceKey(installationId: "other", siteKey: "\u{00E9}")
        func source(_ key: FilmSourceKey, options: FilmVideoOptions = .init(),
                    detection: FilmSourceDetection? = nil) -> FilmVideoSource {
            .init(key: key, name: "同名视频源", api: "/fixture", actions: [], methods: [:],
                  options: options, detection: detection)
        }
        let failed = FilmSourceDetection(result: "invalid", checkedAt: 1, playbackResolved: false,
                                         steps: [.init("home", "failed", "fixture")])
        let snapshot = FilmSourceSnapshot(videos: [
            source(other, detection: failed),
            source(.init(installationId: "hidden", siteKey: "a"), options: .init(homeVisible: false)),
            source(composed),
            source(.init(installationId: "disabled", siteKey: "a"), options: .init(enabled: false)),
            source(decomposed),
        ], runtimeError: "OFFLINE")
        XCTAssertEqual(snapshot.homeVideos.map(\.key), [other, composed, decomposed])
        XCTAssertEqual(Set(snapshot.homeVideos.map(\.key)).count, 3)
        XCTAssertEqual(snapshot.homeVideos.first?.detection, failed)
        XCTAssertTrue(FilmSourceSnapshot().homeVideos.isEmpty)
    }
}
