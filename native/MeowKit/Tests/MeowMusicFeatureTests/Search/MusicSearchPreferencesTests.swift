import XCTest
import MeowStorage
import MeowStorageTestSupport
@testable import MeowMusicFeature

@MainActor final class MusicSearchPreferencesTests: XCTestCase {
    private func store(_ database: SQLiteDatabase) async -> MusicSearchPreferencesStore {
        let store = MusicSearchPreferencesStore(database: database)
        await store.reload()
        return store
    }
    private func rows(_ database: SQLiteDatabase) async throws -> [[SQLiteRow]] {
        try await database.snapshot { db in
            [try db.query("SELECT * FROM ios_music_search_preferences ORDER BY singleton"),
             try db.query("SELECT * FROM ios_music_search_platforms ORDER BY kind,ordinal")]
        }
    }
    func testDefaultsUseEnabledPlatformsAndIndependentKindSettings() async throws {
        let database = try storageTestDatabase(self), store = await store(database)
        XCTAssertEqual(store.preferences.songPlatforms, MusicPublicBrowsePlatform.allCases)
        XCTAssertTrue(store.preferences.searchPlaylists)
        XCTAssertEqual(store.preferences.effectivePlaylistPlatforms, MusicPublicBrowsePlatform.allCases)
        XCTAssertEqual(store.preferences.songs.pages, 1)
        XCTAssertEqual(store.preferences.songs.concurrency, 1)
        XCTAssertEqual(store.preferences.songs.cacheMinutes, 60)
        XCTAssertEqual(store.preferences.playlists, store.preferences.songs)
        storageTestEqual(try await rows(database), [[], []])
    }
    func testPreferencesNormalizeDuplicatesButPreserveExplicitEmptySelection() async throws {
        let database = try storageTestDatabase(self), owner = await store(database)
        storageTestTrue(await owner.update(.init(songPlatforms: [.qq, .qq, .kuwo],
            playlistPlatforms: [.kugou, .netease, .migu], songPages: 3, songConcurrency: 2,
            playlistPages: 4, playlistConcurrency: 3, playlistCacheMinutes: 120)))
        let reopened = await store(database)
        XCTAssertEqual(reopened.preferences.songPlatforms, [.qq, .kuwo])
        XCTAssertEqual(reopened.preferences.effectivePlaylistPlatforms, [.kugou, .netease, .migu])
        XCTAssertEqual(reopened.preferences.songs.pages, 3)
        XCTAssertEqual(reopened.preferences.songs.concurrency, 2)
        XCTAssertEqual(reopened.preferences.playlists.pages, 4)
        XCTAssertEqual(reopened.preferences.playlists.concurrency, 3)
        XCTAssertEqual(reopened.preferences.playlists.cacheMinutes, 120)
        storageTestTrue(await reopened.update(.init(songPlatforms: [], playlistPlatforms: [])))
        storageTestTrue(await store(database).preferences.songPlatforms.isEmpty)
        storageTestTrue(await store(database).preferences.effectivePlaylistPlatforms.isEmpty)
    }
    func testEncodedPolicyKeepsIndependentSongAndPlaylistSettings() async throws {
        let database = try storageTestDatabase(self), owner = await store(database)
        let value = MusicSearchPreferences(songPlatforms: [.qq], playlistPlatforms: [.kugou],
            songPages: 12, songConcurrency: 11, songCacheMinutes: 1440,
            playlistPages: 3, playlistConcurrency: 4, playlistCacheMinutes: 30)
        storageTestTrue(await owner.update(value))
        storageTestEqual(await store(database).preferences, value)
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(value)) as? [String: Any])
        XCTAssertEqual(Set(json.keys), ["songs", "playlists", "searchPlaylists"])
        let songs = try XCTUnwrap(json["songs"] as? [String: Any]), playlists = try XCTUnwrap(json["playlists"] as? [String: Any])
        XCTAssertEqual(Set(songs.keys), ["platforms", "pages", "concurrency", "cacheMinutes"])
        XCTAssertEqual(songs["pages"] as? Int, 12); XCTAssertEqual(songs["concurrency"] as? Int, 11)
        XCTAssertEqual(songs["cacheMinutes"] as? Int, 1440)
        XCTAssertEqual(playlists["pages"] as? Int, 3); XCTAssertEqual(playlists["cacheMinutes"] as? Int, 30)
    }
    func testAdvancedValuesAboveTheFormerLimitAreAcceptedAndPersisted() async throws {
        let database = try storageTestDatabase(self), owner = await store(database)
        let value = MusicSearchPreferences(songPlatforms: [.qq], playlistPlatforms: [.kugou],
            songPages: 11, songConcurrency: 12, songCacheMinutes: 1440,
            playlistPages: Int.max, playlistConcurrency: Int.max, playlistCacheMinutes: 1)
        storageTestTrue(await owner.update(value))
        XCTAssertEqual(owner.preferences, value)
        storageTestEqual(await store(database).preferences, value)
    }
    func testUpdateRejectsInvalidAdvancedValuesWithoutChangingStateOrStorage() async throws {
        let database = try storageTestDatabase(self), owner = await store(database)
        let valid = MusicSearchPreferences(songPlatforms: [.qq], playlistPlatforms: [.kugou],
            songPages: 2, songConcurrency: 3, playlistPages: 4, playlistConcurrency: 5)
        storageTestTrue(await owner.update(valid))
        let saved = try await rows(database)
        let invalid = [MusicSearchPreferences(songPages: 0), .init(songPages: -1), .init(songConcurrency: 0),
            .init(songConcurrency: -1), .init(playlistPages: 0), .init(playlistConcurrency: -1),
            .init(songCacheMinutes: 0), .init(songCacheMinutes: 1441), .init(playlistCacheMinutes: -1)]
        for value in invalid {
            storageTestFalse(await owner.update(value))
            XCTAssertEqual(owner.preferences, valid)
            storageTestEqual(try await rows(database), saved)
        }
    }
    func testInvalidStoredKindIsReportedAndCanBeExplicitlyReplaced() async throws {
        let database = try storageTestDatabase(self), owner = await store(database)
        storageTestTrue(await owner.update(.init(songPlatforms: [.qq], playlistPlatforms: [.kugou])))
        let valid = owner.preferences
        _ = try await database.transaction { try $0.execute("UPDATE ios_music_search_platforms SET platform='invalid' WHERE kind='song'") }
        let saved = try await rows(database)
        await owner.reload()
        XCTAssertNotNil(owner.persistenceError)
        XCTAssertEqual(owner.preferences, valid)
        storageTestEqual(try await rows(database), saved)
        let replacement = MusicSearchPreferences(songPlatforms: [.netease])
        storageTestTrue(await owner.update(replacement))
        storageTestEqual(await store(database).preferences, replacement)
    }
    func testCurrentConstraintsRequireEveryKindField() async throws {
        let database = try storageTestDatabase(self), owner = await store(database)
        storageTestTrue(await owner.update(.init(songPlatforms: [.qq])))
        let saved = try await rows(database)
        for change in ["song_pages=0", "song_pages=-1", "song_concurrency=0", "song_concurrency=-1",
                       "song_cache_minutes=0", "song_cache_minutes=1441", "searchPlaylists=NULL"] {
            await storageTestThrows { try await database.transaction { try $0.execute("UPDATE ios_music_search_preferences SET " + change) } }
            storageTestEqual(try await rows(database), saved)
        }
    }
    func testPlatformPolicyDoesNotReadOrWriteKeywordHistory() async throws {
        let database = try storageTestDatabase(self), owner = await store(database)
        _ = try await database.transaction { try $0.execute("INSERT INTO ios_search_history(text,used_at) VALUES('hello',1)") }
        storageTestTrue(await owner.update(.init(songPlatforms: [.qq], searchPlaylists: false)))
        storageTestEqual(try await database.snapshot { try $0.query("SELECT text FROM ios_search_history").first?.text("text") }, "hello")
        storageTestEqual(await store(database).preferences.songPlatforms, [.qq])
    }
    func testReloadReadsCurrentPolicyWithoutRewritingRows() async throws {
        let database = try storageTestDatabase(self), owner = await store(database)
        let value = MusicSearchPreferences(songPlatforms: [.qq], playlistPlatforms: [], songPages: 3, playlistConcurrency: 2)
        storageTestTrue(await owner.update(value))
        let saved = try await rows(database)
        await owner.reload()
        XCTAssertEqual(owner.preferences, value)
        storageTestEqual(try await rows(database), saved)
    }
    func testWholePolicyUpdateRejectsStaleOwnerWithoutLosingCommittedSettings() async throws {
        let database = try storageTestDatabase(self), first = await store(database), second = await store(database)
        let value = MusicSearchPreferences(songPlatforms: [.qq])
        storageTestTrue(await first.update(value))
        storageTestFalse(await second.update(.init(songPlatforms: [.kugou])))
        XCTAssertNotNil(second.persistenceError)
        storageTestEqual(await store(database).preferences, value)
    }
}
