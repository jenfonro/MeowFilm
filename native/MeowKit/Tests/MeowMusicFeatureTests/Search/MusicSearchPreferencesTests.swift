import XCTest
@testable import MeowMusicFeature

@MainActor final class MusicSearchPreferencesTests: XCTestCase {
    private func isolated(_ body: (UserDefaults) throws -> Void) rethrows {
        let name = "meowfilm.tests.search." + UUID().uuidString
        let defaults = UserDefaults(suiteName: name)!
        defer { defaults.removePersistentDomain(forName: name) }
        try body(defaults)
    }
    func testDefaultsMatchThreePlatformScopeWithoutChangingPlaylistDefault() {
        isolated { defaults in
            let store = MusicSearchPreferencesStore(defaults: defaults)
            XCTAssertEqual(store.preferences.songPlatforms, [.netease, .qq, .kugou])
            XCTAssertTrue(store.preferences.searchPlaylists)
            XCTAssertEqual(store.preferences.playlistMode, "wy")
            XCTAssertEqual(store.preferences.effectivePlaylistPlatforms, [.netease])
        }
    }
    func testPreferencesNormalizeDuplicatesButPreserveExplicitEmptySelection() {
        isolated { defaults in
            let store = MusicSearchPreferencesStore(defaults: defaults)
            store.update(.init(songPlatforms: [.qq, .qq, .kuwo], playlistMode: "aggregate",
                               playlistPlatforms: [.kugou, .netease, .migu]))
            let reopened = MusicSearchPreferencesStore(defaults: defaults)
            XCTAssertEqual(reopened.preferences.songPlatforms, [.qq])
            XCTAssertEqual(reopened.preferences.effectivePlaylistPlatforms, [.netease, .kugou])
            reopened.update(.init(songPlatforms: [], playlistMode: "aggregate", playlistPlatforms: []))
            XCTAssertTrue(MusicSearchPreferencesStore(defaults: defaults).preferences.songPlatforms.isEmpty)
            XCTAssertTrue(MusicSearchPreferencesStore(defaults: defaults).preferences.effectivePlaylistPlatforms.isEmpty)
        }
    }
    func testPlatformPolicyDoesNotReadOrWriteKeywordHistory() {
        isolated { defaults in
            let history = Data(#"[{"text":"hello"}]"#.utf8)
            defaults.set(history, forKey: "search.history")
            let store = MusicSearchPreferencesStore(defaults: defaults)
            store.update(.init(songPlatforms: [.qq], searchPlaylists: false))
            XCTAssertEqual(defaults.data(forKey: "search.history"), history)
            XCTAssertEqual(MusicSearchPreferencesStore(defaults: defaults).preferences.songPlatforms, [.qq])
        }
    }
}
