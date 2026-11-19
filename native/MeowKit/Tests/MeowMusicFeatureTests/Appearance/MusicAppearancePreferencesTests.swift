// Reference: iWebPlus b900c259 app-colors.js, player-lyric-controls.test.cjs.
// Deterministic native tests only; no account, network, script or player fixture.
import XCTest
import MeowUI
import MeowStorage
import MeowStorageTestSupport
@testable import MeowMusicFeature

@MainActor final class MusicAppearancePreferencesTests: XCTestCase {
    private func makeStore(_ database: SQLiteDatabase) async throws -> MusicAppearancePreferencesStore {
        let store = MusicAppearancePreferencesStore(database: database)
        try await store.bind(database: database)
        return store
    }
    private func rows(_ database: SQLiteDatabase) async throws -> [SQLiteRow] {
        try await database.snapshot { try $0.query("SELECT * FROM ios_music_appearance ORDER BY singleton") }
    }
    private let red: [UInt8] = [240, 45, 55, 255]
    private let green: [UInt8] = [40, 220, 120, 255]
    private func song(_ id: String) -> MusicAppearanceSongIdentity { .init(songID: "song:" + id) }

    func testCustomColorPreviewDoesNotPersistAndCancellationRestoresTheSavedPalette() async throws {
        let database = try storageTestDatabase(self)
        let store = try await makeStore(database)
        storageTestTrue(await store.selectFixedPalette(.preset(.mint)))
        let saved = try await rows(database)
        store.previewFixedColor(0xff0077)
        XCTAssertEqual(store.pagePalette, .custom(0xff0077))
        XCTAssertEqual(store.preferences.colorPalette, .preset(.mint))
        storageTestEqual(try await rows(database), saved)
        storageTestEqual(try await makeStore(database).pagePalette, .preset(.mint))
        store.endColorPreview()
        XCTAssertEqual(store.pagePalette, .preset(.mint))
        store.previewFixedColor(0x40ab93)
        storageTestTrue(await store.selectFixedPalette(.custom(0x40ab93)))
        store.endColorPreview()
        XCTAssertEqual(store.pagePalette, .custom(0x40ab93))
        let reopened = try await makeStore(database)
        XCTAssertEqual(reopened.preferences.colorPalette, .custom(0x40ab93))
        XCTAssertEqual(reopened.pagePalette, .custom(0x40ab93))
        let savedRows = try await rows(database)
        XCTAssertEqual(try savedRows.first?.text("colorPalette"), "#40AB93")
        await store.close(); await reopened.close()
    }

    func testSongModeStillUsesItsOwnCacheAfterCustomColorAndLateCoverCannotOverwriteFixedColor() async throws {
        let database = try storageTestDatabase(self)
        let store = try await makeStore(database)
        store.selectSong(song("custom-roundtrip"))
        let pending = try XCTUnwrap(store.beginCover(for: song("custom-roundtrip"), artworkID: "cover"))
        XCTAssertTrue(store.applyCover(rgba: green, ticket: pending))
        storageTestTrue(await store.selectFixedPalette(.custom(0x2143aa)))
        XCTAssertFalse(store.applyCover(rgba: red, ticket: pending))
        storageTestTrue(await store.set(\.colorMode, .song))
        XCTAssertEqual(store.pagePalette, .preset(.mint))
        storageTestTrue(await store.set(\.colorMode, .fixed))
        XCTAssertEqual(store.pagePalette, .custom(0x2143aa))
        await store.close()
    }

    func testDefaultsMatchReferenceAndDoNotNeedAMusicService() async throws {
        let database = try storageTestDatabase(self)
        let store = try await makeStore(database)
        XCTAssertEqual(store.preferences, .init())
        XCTAssertEqual(store.effectivePalette, .preset(.lilac))
        XCTAssertEqual(MusicPlayerAppearance.allCases, [.starlight])
        XCTAssertEqual(store.preferences.playlistView, .grid)
        XCTAssertTrue(store.preferences.lockLyric)
        XCTAssertTrue(store.preferences.lyricTranslation)
        XCTAssertTrue(store.preferences.lyricBackground)
        storageTestEqual(try await rows(database), [])
        await store.close()
    }

    func testPreferencesPersistIndependentlyOfOtherSettings() async throws {
        let database = try storageTestDatabase(self)
        _ = try await database.transaction { try $0.execute("INSERT INTO ios_search_history(text,used_at) VALUES('preserved',1)") }
        let store = try await makeStore(database)
        storageTestTrue(await store.set(\.playlistView, .list))
        storageTestTrue(await store.selectFixedPalette(.preset(.mint)))
        storageTestTrue(await store.set(\.lockLyric, false))
        storageTestTrue(await store.set(\.lyricTranslation, false))
        storageTestTrue(await store.set(\.lyricBackground, false))
        let reopened = try await makeStore(database)
        XCTAssertEqual(reopened.preferences, .init(playlistView: .list, colorMode: .fixed, colorPalette: .preset(.mint),
                                                   lockLyric: false, lyricTranslation: false, lyricBackground: false))
        XCTAssertEqual(reopened.effectivePalette, .preset(.mint))
        await reopened.reload()
        XCTAssertEqual(reopened.preferences.playlistView, .list)
        XCTAssertFalse(reopened.preferences.lyricTranslation)
        storageTestEqual(try await database.snapshot { try $0.query("SELECT text FROM ios_search_history").first?.text("text") }, "preserved")
        await store.close(); await reopened.close()
    }

    func testFieldUpdatesRebaseRatherThanLoseAnotherOwnersEdit() async throws {
        let database = try storageTestDatabase(self)
        let first = try await makeStore(database), second = try await makeStore(database)
        storageTestTrue(await first.set(\.lyricBackground, false))
        storageTestTrue(await second.selectFixedPalette(.preset(.rose)))
        storageTestTrue(await first.set(\.lockLyric, false))
        let reopened = try await makeStore(database)
        XCTAssertFalse(reopened.preferences.lockLyric)
        XCTAssertFalse(reopened.preferences.lyricBackground)
        XCTAssertEqual(reopened.effectivePalette, .preset(.rose))
        await first.close(); await second.close(); await reopened.close()
    }

    func testNativeLyricChoicePersists() async throws {
        let database = try storageTestDatabase(self)
        let store = try await makeStore(database)
        storageTestTrue(await store.set(\.colorMode, .fixed))
        storageTestTrue(await store.set(\.lyricTranslation, false))
        let reopened = try await makeStore(database)
        XCTAssertFalse(reopened.preferences.lyricTranslation)
        XCTAssertEqual(reopened.preferences.colorMode, .fixed)
        storageTestTrue(await reopened.set(\.lyricTranslation, true))
        storageTestTrue(try await makeStore(database).preferences.lyricTranslation)
        await store.close(); await reopened.close()
    }

    func testCorruptCurrentPreferencesAreNotSilentlyOverwritten() async throws {
        let database = try storageTestDatabase(self)
        let store = try await makeStore(database)
        storageTestTrue(await store.set(\.lockLyric, false))
        _ = try await database.transaction { try $0.execute("UPDATE ios_music_appearance SET colorPalette='invalid'") }
        let before = try await rows(database)
        await store.reload()
        XCTAssertNotNil(store.persistenceError)
        storageTestFalse(await store.set(\.lockLyric, true))
        storageTestEqual(try await rows(database), before)
        await store.resetPreferences()
        XCTAssertNil(store.persistenceError)
        XCTAssertEqual(store.preferences, .init())
        storageTestEqual(try await rows(database), [])
        await store.close()
    }

    func testMalformedCurrentPreferencesAreNotSilentlyDefaulted() throws {
        let current = try JSONEncoder().encode(MusicAppearancePreferences())
        let fields = try XCTUnwrap(try JSONSerialization.jsonObject(with: current) as? [String: Any])
        for key in ["playlistView", "playerTheme", "colorMode", "colorPalette"] {
            var malformed = fields; malformed[key] = "invalid"
            let bytes = try JSONSerialization.data(withJSONObject: malformed)
            XCTAssertThrowsError(try JSONDecoder().decode(MusicAppearancePreferences.self, from: bytes))
        }
        var missing = fields; missing.removeValue(forKey: "lyricTranslation")
        XCTAssertThrowsError(try JSONDecoder().decode(MusicAppearancePreferences.self,
                                                     from: JSONSerialization.data(withJSONObject: missing)))
    }

    func testLateCoverCannotRepaintAnotherSongOrANewerCoverOrFixedMode() async throws {
        let database = try storageTestDatabase(self)
        let store = try await makeStore(database)
        store.selectSong(song("a"))
        let old = try XCTUnwrap(store.beginCover(for: song("a"), artworkID: "first"))
        store.selectSong(song("b"))
        XCTAssertFalse(store.applyCover(rgba: red, ticket: old))
        XCTAssertNil(store.beginCover(for: song("a"), artworkID: "wrong-song"))
        let first = try XCTUnwrap(store.beginCover(for: song("b"), artworkID: "old-cover"))
        let second = try XCTUnwrap(store.beginCover(for: song("b"), artworkID: "display-cover"))
        XCTAssertFalse(store.applyCover(rgba: red, ticket: first))
        XCTAssertTrue(store.applyCover(rgba: green, ticket: second))
        XCTAssertEqual(store.effectivePalette, .preset(.mint))
        let pending = try XCTUnwrap(store.beginCover(for: song("b"), artworkID: "late"))
        storageTestTrue(await store.selectFixedPalette(.preset(.lemon)))
        XCTAssertFalse(store.applyCover(rgba: red, ticket: pending))
        XCTAssertNil(store.beginCover(for: song("b"), artworkID: "fixed"))
        XCTAssertEqual(store.effectivePalette, .preset(.lemon))
        storageTestTrue(await store.set(\.colorMode, .song))
        XCTAssertEqual(store.effectivePalette, .preset(.mint))
        await store.close()
    }

    func testCancellationPlaceholderAndMissingCoverKeepTheFallbackWithoutErrors() async throws {
        let database = try storageTestDatabase(self)
        let store = try await makeStore(database)
        store.selectSong(song("a"))
        let fallback = store.effectivePalette
        XCTAssertNil(store.beginCover(for: song("a"), artworkID: "placeholder", isPlaceholder: true))
        XCTAssertNil(store.beginCover(for: song("a"), artworkID: ""))
        let pending = try XCTUnwrap(store.beginCover(for: song("a"), artworkID: "cover"))
        XCTAssertFalse(store.applyCover(rgba: [0, 0, 0, 0], ticket: pending))
        store.cancelCover()
        XCTAssertFalse(store.applyCover(rgba: green, ticket: pending))
        XCTAssertEqual(store.effectivePalette, fallback)
        XCTAssertNil(store.persistenceError)
        store.selectSong(nil)
        XCTAssertEqual(store.effectivePalette, fallback)
        await store.close()
    }

    func testPrimingIsNotPlaybackAndCacheIsBoundedInReferenceOrder() async throws {
        let database = try storageTestDatabase(self)
        let store = try await makeStore(database)
        for index in 0..<130 { store.primeCover(rgba: green, for: song("\(index)")) }
        XCTAssertEqual(store.effectivePalette, .preset(.lilac))
        await store.close()
        let colors = try await database.snapshot { try $0.query("SELECT * FROM ios_music_song_palettes ORDER BY used_at,song_key") }
        XCTAssertEqual(colors.count, 128)
        XCTAssertEqual(try colors.first?.text("song_key"), "song:2")
        let next = try await makeStore(database)
        next.selectSong(song("2"))
        XCTAssertEqual(next.effectivePalette, .preset(.mint))
        next.primeCover(rgba: red, for: song("2"))
        XCTAssertEqual(next.effectivePalette, .preset(.mint))
        await next.close()
        let reopened = try await makeStore(database)
        XCTAssertEqual(reopened.effectivePalette, .preset(.mint))
        reopened.selectSong(song("2"))
        XCTAssertEqual(reopened.effectivePalette, .preset(.coral))
        reopened.selectSong(nil)
        XCTAssertEqual(reopened.effectivePalette, .preset(.coral))
        await reopened.close()
    }

    func testLongCacheKeysNeverOverwritePreferences() async throws {
        let database = try storageTestDatabase(self)
        let store = try await makeStore(database)
        store.primeCover(rgba: green, for: song(String(repeating: "x", count: 2050)))
        await store.close()
        storageTestEqual(try await rows(database), [])
        storageTestEqual(try await database.snapshot { try $0.query("SELECT * FROM ios_music_song_palettes") }, [])
        XCTAssertEqual(store.preferences, .init())
    }

    func testLyricsAreDisplayOnlyAndRecordPreviewIsAlwaysOriginalOnly() {
        var preferences = MusicAppearancePreferences()
        XCTAssertNil(preferences.translation("译文", expanded: false))
        XCTAssertEqual(preferences.translation("译文", expanded: true), "译文")
        XCTAssertFalse(preferences.showsLyricBackground(expanded: false))
        XCTAssertTrue(preferences.showsLyricBackground(expanded: true))
        XCTAssertEqual(preferences.lyricAccessibilityLabel(text: "Original", translation: "<b>译文</b>", expanded: true),
                       "Original\n<b>译文</b>")
        preferences.lyricTranslation = false
        preferences.lyricBackground = false
        XCTAssertNil(preferences.translation("译文", expanded: true))
        XCTAssertFalse(preferences.showsLyricBackground(expanded: true))
        XCTAssertEqual(preferences.lyricAccessibilityLabel(text: "Original", translation: "译文", expanded: true), "Original")
        XCTAssertEqual(preferences.translationActionLabel, "显示翻译歌词")
        XCTAssertEqual(preferences.backgroundActionLabel, "显示歌词背景")
    }

    func testLockScreenLyricsPreserveSongAndFavoriteTitleAndRestoreImmediatelyWhenDisabled() {
        var preferences = MusicAppearancePreferences()
        let lyric = preferences.lockScreenMetadata(songTitle: "Song", songArtist: "Artist",
                                                   lyric: "Original line", isFavorite: true)
        XCTAssertEqual(lyric.title, "Original line")
        XCTAssertEqual(lyric.artist, "Song ♡︎ / Artist")
        XCTAssertEqual(lyric.album, "我的曲库")
        preferences.lockLyric = false
        let plain = preferences.lockScreenMetadata(songTitle: "Song", songArtist: "Artist",
                                                   lyric: "Original line", isFavorite: true)
        XCTAssertEqual(plain.title, "Song ♡︎")
        XCTAssertEqual(plain.artist, "Artist")
        preferences.lockLyric = true
        let empty = preferences.lockScreenMetadata(songTitle: "Song", songArtist: "Artist", lyric: "", isFavorite: false)
        XCTAssertEqual(empty.title, "Song")
        XCTAssertEqual(empty.artist, "Artist")
    }

    func testLockScreenMetadataDoesNotAddASeparatorWhenArtistIsMissing() {
        let preferences = MusicAppearancePreferences()
        let lyric = preferences.lockScreenMetadata(songTitle: "Song", songArtist: "",
                                                   lyric: "Original line", isFavorite: true)
        XCTAssertEqual(lyric.title, "Original line")
        XCTAssertEqual(lyric.artist, "Song ♡︎")
        let plain = preferences.lockScreenMetadata(songTitle: "Song", songArtist: "", lyric: nil, isFavorite: false)
        XCTAssertEqual(plain.title, "Song")
        XCTAssertEqual(plain.artist, "")
    }
}
