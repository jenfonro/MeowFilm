// Reference: iWebPlus b900c259 app-colors.js, player-lyric-controls.test.cjs.
// Deterministic native tests only; no account, network, script or player fixture.
import XCTest
import MeowUI
@testable import MeowMusicFeature

@MainActor final class MusicAppearancePreferencesTests: XCTestCase {
    private func isolated(_ body: (UserDefaults) throws -> Void) rethrows {
        let suite = "meowfilm.tests.appearance." + UUID().uuidString
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        try body(defaults)
    }
    private let red: [UInt8] = [240, 45, 55, 255]
    private let green: [UInt8] = [40, 220, 120, 255]
    private func song(_ id: String) -> MusicAppearanceSongIdentity { .init(platform: "wy", musicID: id) }

    func testCustomColorPreviewDoesNotPersistAndCancellationRestoresTheSavedPalette() throws {
        try isolated { defaults in
            let store = MusicAppearancePreferencesStore(defaults: defaults)
            store.selectFixedPalette(.preset(.mint))
            let saved = defaults.data(forKey: MusicAppearancePreferencesStore.preferencesKey)
            store.previewFixedColor(0xff0077)
            XCTAssertEqual(store.pagePalette, .custom(0xff0077))
            XCTAssertEqual(store.preferences.colorPalette, .preset(.mint))
            XCTAssertEqual(defaults.data(forKey: MusicAppearancePreferencesStore.preferencesKey), saved)
            XCTAssertEqual(MusicAppearancePreferencesStore(defaults: defaults).pagePalette, .preset(.mint))
            store.endColorPreview()
            XCTAssertEqual(store.pagePalette, .preset(.mint))
            store.previewFixedColor(0x40ab93)
            XCTAssertTrue(store.selectFixedPalette(.custom(0x40ab93)))
            store.endColorPreview()
            XCTAssertEqual(store.pagePalette, .custom(0x40ab93))
            let reopened = MusicAppearancePreferencesStore(defaults: defaults)
            XCTAssertEqual(reopened.preferences.colorPalette, .custom(0x40ab93))
            XCTAssertEqual(reopened.pagePalette, .custom(0x40ab93))
            let bytes = try XCTUnwrap(defaults.data(forKey: MusicAppearancePreferencesStore.preferencesKey))
            XCTAssertTrue(String(decoding: bytes, as: UTF8.self).contains("#40AB93"))
        }
    }

    func testSongModeStillUsesItsOwnCacheAfterCustomColorAndLateCoverCannotOverwriteFixedColor() throws {
        try isolated { defaults in
            let store = MusicAppearancePreferencesStore(defaults: defaults)
            store.selectSong(song("custom-roundtrip"))
            let pending = try XCTUnwrap(store.beginCover(for: song("custom-roundtrip"), artworkID: "cover"))
            XCTAssertTrue(store.applyCover(rgba: green, ticket: pending))
            store.selectFixedPalette(.custom(0x2143aa))
            XCTAssertFalse(store.applyCover(rgba: red, ticket: pending))
            store.set(\.colorMode, .song)
            XCTAssertEqual(store.pagePalette, .preset(.mint))
            store.set(\.colorMode, .fixed)
            XCTAssertEqual(store.pagePalette, .custom(0x2143aa))
        }
    }

    func testDefaultsMatchReferenceAndDoNotNeedAMusicService() {
        isolated { defaults in
            let store = MusicAppearancePreferencesStore(defaults: defaults)
            XCTAssertEqual(store.preferences, .init())
            XCTAssertEqual(store.effectivePalette, .preset(.lilac))
            XCTAssertEqual(MusicPlayerAppearance.allCases, [.starlight])
            XCTAssertEqual(store.preferences.playlistView, .grid)
            XCTAssertTrue(store.preferences.lockLyric)
            XCTAssertTrue(store.preferences.lyricTranslation)
            XCTAssertTrue(store.preferences.lyricBackground)
            XCTAssertNil(defaults.object(forKey: MusicAppearancePreferencesStore.preferencesKey), "Reading defaults is not an import/write")
        }
    }

    func testPreferencesPersistIndependentlyOfOtherSettings() {
        isolated { defaults in
            let store = MusicAppearancePreferencesStore(defaults: defaults)
            store.set(\.playlistView, .list)
            store.selectFixedPalette(.preset(.mint))
            store.set(\.lockLyric, false)
            store.set(\.lyricTranslation, false)
            store.set(\.lyricBackground, false)
            let reopened = MusicAppearancePreferencesStore(defaults: defaults)
            XCTAssertEqual(reopened.preferences, .init(playlistView: .list, colorMode: .fixed, colorPalette: .preset(.mint),
                                                       lockLyric: false, lyricTranslation: false, lyricBackground: false))
            XCTAssertNil(defaults.string(forKey: "unrelated.setting"))
            XCTAssertEqual(reopened.effectivePalette, .preset(.mint))
            defaults.set("grid", forKey: "unrelated.setting")
            reopened.reload()
            XCTAssertEqual(reopened.preferences.playlistView, .list)
            XCTAssertEqual(defaults.string(forKey: "unrelated.setting"), "grid")
            XCTAssertFalse(reopened.preferences.lyricTranslation)
        }
    }

    func testFieldUpdatesRebaseRatherThanLoseAnotherOwnersEdit() {
        isolated { defaults in
            let first = MusicAppearancePreferencesStore(defaults: defaults)
            let second = MusicAppearancePreferencesStore(defaults: defaults)
            first.set(\.lyricBackground, false)
            second.selectFixedPalette(.preset(.rose))
            first.set(\.lockLyric, false)
            let reopened = MusicAppearancePreferencesStore(defaults: defaults)
            XCTAssertFalse(reopened.preferences.lockLyric)
            XCTAssertFalse(reopened.preferences.lyricBackground)
            XCTAssertEqual(reopened.effectivePalette, .preset(.rose))
        }
    }

    func testNativeLyricChoicePersists() {
        isolated { defaults in
            let store = MusicAppearancePreferencesStore(defaults: defaults)
            store.set(\.colorMode, .fixed)
            store.set(\.lyricTranslation, false)
            let reopened = MusicAppearancePreferencesStore(defaults: defaults)
            XCTAssertFalse(reopened.preferences.lyricTranslation)
            XCTAssertEqual(reopened.preferences.colorMode, .fixed)
            reopened.set(\.lyricTranslation, true)
            XCTAssertTrue(MusicAppearancePreferencesStore(defaults: defaults).preferences.lyricTranslation)
        }
    }

    func testFutureOrCorruptDocumentsAreNotSilentlyOverwritten() {
        for payload in [
            #"{"version":2,"preferences":{"colorPalette":"mint"}}"#,
            "{broken"
        ] {
            isolated { defaults in
                let bytes = Data(payload.utf8)
                defaults.set(bytes, forKey: MusicAppearancePreferencesStore.preferencesKey)
                defaults.set("untouched", forKey: "music.library.test-marker")
                let store = MusicAppearancePreferencesStore(defaults: defaults)
                XCTAssertNotNil(store.persistenceError)
                XCTAssertFalse(store.set(\.lockLyric, false))
                XCTAssertEqual(defaults.data(forKey: MusicAppearancePreferencesStore.preferencesKey), bytes)
                store.resetPreferences()
                XCTAssertNil(store.persistenceError)
                XCTAssertEqual(store.preferences, .init())
                XCTAssertEqual(defaults.string(forKey: "music.library.test-marker"), "untouched")
            }
        }
    }

    func testUnknownPreferenceValuesFallbackIndependently() throws {
        let bytes = Data(#"{"playlistView":"unknown","playerTheme":"missing","colorMode":"invalid","colorPalette":"missing","lockLyric":false,"lyricBackground":false}"#.utf8)
        let preferences = try JSONDecoder().decode(MusicAppearancePreferences.self, from: bytes)
        XCTAssertEqual(preferences.playlistView, .grid)
        XCTAssertEqual(preferences.playerTheme, .starlight)
        XCTAssertEqual(preferences.colorMode, .song)
        XCTAssertEqual(preferences.colorPalette, .preset(.lilac))
        XCTAssertFalse(preferences.lockLyric)
        XCTAssertFalse(preferences.lyricBackground)
        XCTAssertTrue(preferences.lyricTranslation)
    }

    func testLateCoverCannotRepaintAnotherSongOrANewerCoverOrFixedMode() throws {
        try isolated { defaults in
            let store = MusicAppearancePreferencesStore(defaults: defaults)
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
            store.selectFixedPalette(.preset(.lemon))
            XCTAssertFalse(store.applyCover(rgba: red, ticket: pending))
            XCTAssertNil(store.beginCover(for: song("b"), artworkID: "fixed"))
            XCTAssertEqual(store.effectivePalette, .preset(.lemon))
            store.set(\.colorMode, .song)
            XCTAssertEqual(store.effectivePalette, .preset(.mint))
        }
    }

    func testCancellationPlaceholderAndMissingCoverKeepTheFallbackWithoutErrors() throws {
        try isolated { defaults in
            let store = MusicAppearancePreferencesStore(defaults: defaults)
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
        }
    }

    func testPrimingIsNotPlaybackAndCacheIsBoundedInReferenceOrder() throws {
        try isolated { defaults in
            let store = MusicAppearancePreferencesStore(defaults: defaults)
            for index in 0..<130 { store.primeCover(rgba: green, for: song("\(index)")) }
            XCTAssertEqual(store.effectivePalette, .preset(.lilac))
            let bytes = try XCTUnwrap(defaults.data(forKey: MusicAppearancePreferencesStore.songColorsKey))
            let json = try XCTUnwrap(try JSONSerialization.jsonObject(with: bytes) as? [String: Any])
            let colors = try XCTUnwrap(json["colors"] as? [[String]])
            XCTAssertEqual(colors.count, 128)
            XCTAssertEqual(colors.first, ["wy:2", "mint"])
            store.selectSong(song("2")) // A cache hit does not reorder the Map.
            XCTAssertEqual(store.effectivePalette, .preset(.mint))
            store.primeCover(rgba: red, for: song("2")) // A changed sample DOES.
            XCTAssertEqual(store.effectivePalette, .preset(.mint))
            let reopened = MusicAppearancePreferencesStore(defaults: defaults)
            XCTAssertEqual(reopened.effectivePalette, .preset(.mint))
            reopened.selectSong(song("2"))
            XCTAssertEqual(reopened.effectivePalette, .preset(.coral))
            reopened.selectSong(nil)
            XCTAssertEqual(reopened.effectivePalette, .preset(.coral))
        }
    }

    func testCacheFilteringAndLongKeysNeverOverwritePreferences() {
        isolated { defaults in
            let key = MusicAppearancePreferencesStore.songColorsKey
            defaults.set(Data(#"{"version":1,"last":"missing","colors":[["wy:a","mint"],["wy:a","rose"],["wy:b","missing"],["bad"],4]}"#.utf8), forKey: key)
            let store = MusicAppearancePreferencesStore(defaults: defaults)
            XCTAssertEqual(store.effectivePalette, .preset(.lilac))
            store.selectSong(song("a"))
            XCTAssertEqual(store.effectivePalette, .preset(.rose))
            store.primeCover(rgba: green, for: song(String(repeating: "x", count: 2050)))
            XCTAssertNil(defaults.object(forKey: MusicAppearancePreferencesStore.preferencesKey))
            XCTAssertEqual(store.preferences, .init())
        }
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
