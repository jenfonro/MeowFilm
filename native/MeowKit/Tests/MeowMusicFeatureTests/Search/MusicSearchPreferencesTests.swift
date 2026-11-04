import XCTest
@testable import MeowMusicFeature

@MainActor final class MusicSearchPreferencesTests: XCTestCase {
    private func isolated(_ body: (UserDefaults) throws -> Void) rethrows {
        let name = "meowfilm.tests.search." + UUID().uuidString
        let defaults = UserDefaults(suiteName: name)!
        defer { defaults.removePersistentDomain(forName: name) }
        try body(defaults)
    }
    func testDefaultsUseEnabledPlatformsAndIndependentKindSettings() {
        isolated { defaults in
            let store = MusicSearchPreferencesStore(defaults: defaults)
            XCTAssertEqual(store.preferences.songPlatforms, MusicPublicBrowsePlatform.allCases)
            XCTAssertTrue(store.preferences.searchPlaylists)
            XCTAssertEqual(store.preferences.effectivePlaylistPlatforms, MusicPublicBrowsePlatform.allCases)
            XCTAssertEqual(store.preferences.songs.pages, 1)
            XCTAssertEqual(store.preferences.songs.concurrency, 1)
            XCTAssertEqual(store.preferences.songs.cacheMinutes, 60)
            XCTAssertEqual(store.preferences.playlists, store.preferences.songs)
        }
    }
    func testPreferencesNormalizeDuplicatesButPreserveExplicitEmptySelection() {
        isolated { defaults in
            let store = MusicSearchPreferencesStore(defaults: defaults)
            XCTAssertTrue(store.update(.init(songPlatforms: [.qq, .qq, .kuwo],
                                             playlistPlatforms: [.kugou, .netease, .migu],
                                             songPages: 3, songConcurrency: 2,
                                             playlistPages: 4, playlistConcurrency: 3,
                                             playlistCacheMinutes: 120)))
            let reopened = MusicSearchPreferencesStore(defaults: defaults)
            XCTAssertEqual(reopened.preferences.songPlatforms, [.qq, .kuwo])
            XCTAssertEqual(reopened.preferences.effectivePlaylistPlatforms, [.kugou, .netease, .migu])
            XCTAssertEqual(reopened.preferences.songs.pages, 3)
            XCTAssertEqual(reopened.preferences.songs.concurrency, 2)
            XCTAssertEqual(reopened.preferences.playlists.pages, 4)
            XCTAssertEqual(reopened.preferences.playlists.concurrency, 3)
            XCTAssertEqual(reopened.preferences.playlists.cacheMinutes, 120)
            XCTAssertTrue(reopened.update(.init(songPlatforms: [], playlistPlatforms: [])))
            XCTAssertTrue(MusicSearchPreferencesStore(defaults: defaults).preferences.songPlatforms.isEmpty)
            XCTAssertTrue(MusicSearchPreferencesStore(defaults: defaults).preferences.effectivePlaylistPlatforms.isEmpty)
        }
    }
    func testEncodedPolicyKeepsIndependentSongAndPlaylistSettings() throws {
        try isolated { defaults in
            let value = MusicSearchPreferences(songPlatforms: [.qq], playlistPlatforms: [.kugou],
                songPages: 12, songConcurrency: 11, songCacheMinutes: 1440,
                playlistPages: 3, playlistConcurrency: 4, playlistCacheMinutes: 30)
            XCTAssertTrue(MusicSearchPreferencesStore(defaults: defaults).update(value))
            XCTAssertEqual(MusicSearchPreferencesStore(defaults: defaults).preferences, value)
            let data = try JSONEncoder().encode(value)
            let json = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
            XCTAssertEqual(Set(json.keys), ["songs", "playlists", "searchPlaylists"])
            let songs = try XCTUnwrap(json["songs"] as? [String: Any])
            let playlists = try XCTUnwrap(json["playlists"] as? [String: Any])
            XCTAssertEqual(Set(songs.keys), ["platforms", "pages", "concurrency", "cacheMinutes"])
            XCTAssertEqual(songs["pages"] as? Int, 12); XCTAssertEqual(songs["concurrency"] as? Int, 11)
            XCTAssertEqual(songs["cacheMinutes"] as? Int, 1440)
            XCTAssertEqual(playlists["pages"] as? Int, 3); XCTAssertEqual(playlists["cacheMinutes"] as? Int, 30)
        }
    }
    func testAdvancedValuesAboveTheFormerLimitAreAcceptedAndPersisted() {
        isolated { defaults in
            let store = MusicSearchPreferencesStore(defaults: defaults)
            let value = MusicSearchPreferences(songPlatforms: [.qq], playlistPlatforms: [.kugou],
                songPages: 11, songConcurrency: 12, songCacheMinutes: 1440,
                playlistPages: Int.max, playlistConcurrency: Int.max, playlistCacheMinutes: 1)
            XCTAssertTrue(store.update(value))
            XCTAssertEqual(store.preferences, value)
            XCTAssertEqual(MusicSearchPreferencesStore(defaults: defaults).preferences, value)
        }
    }
    func testUpdateRejectsInvalidAdvancedValuesWithoutChangingStateOrStorage() {
        isolated { defaults in
            let store = MusicSearchPreferencesStore(defaults: defaults)
            let valid = MusicSearchPreferences(songPlatforms: [.qq], playlistPlatforms: [.kugou],
                songPages: 2, songConcurrency: 3, playlistPages: 4, playlistConcurrency: 5)
            XCTAssertTrue(store.update(valid))
            let before = store.preferences
            let saved = defaults.data(forKey: MusicSearchPreferencesStore.preferencesKey)
            let invalid = [
                MusicSearchPreferences(songPages: 0),
                MusicSearchPreferences(songPages: -1),
                MusicSearchPreferences(songConcurrency: 0),
                MusicSearchPreferences(songConcurrency: -1),
                MusicSearchPreferences(playlistPages: 0),
                MusicSearchPreferences(playlistConcurrency: -1),
                MusicSearchPreferences(songCacheMinutes: 0),
                MusicSearchPreferences(songCacheMinutes: 1441),
                MusicSearchPreferences(playlistCacheMinutes: -1)
            ]
            for value in invalid {
                XCTAssertFalse(store.update(value))
                XCTAssertEqual(store.preferences, before)
                XCTAssertEqual(defaults.data(forKey: MusicSearchPreferencesStore.preferencesKey), saved)
            }
        }
    }
    func testInvalidStoredKindRejectsTheWholeDocumentInsteadOfFallingBackPerField() throws {
        try isolated { defaults in
            for change in [("pages", 0), ("pages", -1), ("concurrency", 0), ("concurrency", -1),
                           ("cacheMinutes", 0), ("cacheMinutes", 1441)] {
                let value = MusicSearchPreferences(songPlatforms: [.qq], playlistPlatforms: [.kugou],
                    songPages: 2, songConcurrency: 3, songCacheMinutes: 60)
                let encoded = try JSONEncoder().encode(value)
                var json = try XCTUnwrap(JSONSerialization.jsonObject(with: encoded) as? [String: Any])
                var songs = try XCTUnwrap(json["songs"] as? [String: Any])
                songs[change.0] = change.1
                json["songs"] = songs
                defaults.set(try JSONSerialization.data(withJSONObject: json),
                             forKey: MusicSearchPreferencesStore.preferencesKey)
                XCTAssertEqual(MusicSearchPreferencesStore(defaults: defaults).preferences,
                               MusicSearchPreferences())
                XCTAssertThrowsError(try JSONDecoder().decode(
                    MusicSearchPreferences.self, from: JSONSerialization.data(withJSONObject: json)))
            }
        }
    }
    func testPlatformPolicyDoesNotReadOrWriteKeywordHistory() {
        isolated { defaults in
            let history = Data(#"[{"text":"hello"}]"#.utf8)
            defaults.set(history, forKey: "search.history")
            let store = MusicSearchPreferencesStore(defaults: defaults)
            XCTAssertTrue(store.update(.init(songPlatforms: [.qq], searchPlaylists: false)))
            XCTAssertEqual(defaults.data(forKey: "search.history"), history)
            XCTAssertEqual(MusicSearchPreferencesStore(defaults: defaults).preferences.songPlatforms, [.qq])
        }
    }
}
