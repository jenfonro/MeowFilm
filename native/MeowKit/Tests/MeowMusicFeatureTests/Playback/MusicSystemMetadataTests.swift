#if os(iOS) || os(macOS)
import XCTest
import MeowContracts
@testable import MeowPlayback
@testable import MeowMusicFeature

final class MusicSystemMetadataTests: XCTestCase {
    private func track(_ id: String = "song", cover: String = "cover") -> MusicRemoteTrack {
        .init(ref: .init(kind: .queue, id: id), songId: id, title: "歌曲", artist: "歌手",
              artwork: URL(string: "https://artwork.invalid/\(cover).png"))
    }

    @MainActor private func store() -> (MusicAppearancePreferencesStore, UserDefaults, String) {
        let name = "MusicSystemMetadataTests." + UUID().uuidString
        let defaults = UserDefaults(suiteName: name)!
        return (MusicAppearancePreferencesStore(defaults: defaults), defaults, name)
    }

    @MainActor func testUsesOriginalActiveLineAndFavoriteSongTitleNotTranslationOrDisplayVisibility() {
        let lines: [MusicLyricLine] = [
            .init(index: 4, time: 3, end: 12, text: "原文一", translation: "Translation one"),
            .init(index: 9, time: 12, end: 30, text: "原文二", translation: "Translation two")
        ]
        var preferences = MusicAppearancePreferences()
        preferences.lyricBackground = false; preferences.lyricTranslation = false
        let value = MusicSystemMetadataCoordinator.metadata(track: track(), lines: lines, position: 15,
            preferences: preferences, favorite: true, artwork: nil)
        XCTAssertEqual(value.title, "原文二")
        XCTAssertEqual(value.artist, "歌曲 ♡︎")
        XCTAssertEqual(value.album, "我的曲库")
        preferences.lockLyric = false
        let disabled = MusicSystemMetadataCoordinator.metadata(track: track(), lines: lines, position: 15,
            preferences: preferences, favorite: true, artwork: nil)
        XCTAssertEqual(disabled.title, "歌曲 ♡︎")
        XCTAssertEqual(disabled.artist, "meowfilm")
        let beforeLine = MusicSystemMetadataCoordinator.metadata(track: track(), lines: lines, position: 0,
            preferences: .init(), favorite: false, artwork: nil)
        XCTAssertEqual(beforeLine.title, "歌曲")
        XCTAssertEqual(beforeLine.artist, "meowfilm")
    }

    func testAdmissionRequiresExactMusicAttemptResourceOccurrenceAndPlayableSnapshot() throws {
        let entry = MusicQueueEntry(id: "occurrence", track: track())
        let attempt = PlayAttempt(id: UUID(), owner: "music", resourceID: "resource")
        let access = UUID()
        let snapshot = PlaybackSnapshot(owner: "music", resourceID: "resource", phase: .paused)
        let valid = MusicSystemMetadataBinding(entry: entry, attempt: attempt, activeAttemptID: attempt.id,
            activeResourceID: "resource", activeOccurrenceID: entry.id, access: access, snapshot: snapshot)
        XCTAssertNotNil(valid)
        XCTAssertTrue(try XCTUnwrap(valid).matches(track()))
        XCTAssertFalse(try XCTUnwrap(valid).matches(track("another")))
        XCTAssertFalse(try XCTUnwrap(valid).matches(track(cover: "changed")))
        XCTAssertNil(MusicSystemMetadataBinding(entry: entry, attempt: attempt, activeAttemptID: UUID(),
            activeResourceID: "resource", activeOccurrenceID: entry.id, access: access, snapshot: snapshot))
        XCTAssertNil(MusicSystemMetadataBinding(entry: entry, attempt: attempt, activeAttemptID: attempt.id,
            activeResourceID: "other", activeOccurrenceID: entry.id, access: access, snapshot: snapshot))
        XCTAssertNil(MusicSystemMetadataBinding(entry: entry, attempt: attempt, activeAttemptID: attempt.id,
            activeResourceID: "resource", activeOccurrenceID: "other", access: access, snapshot: snapshot))
        let foreign = PlayAttempt(id: attempt.id, owner: "film", resourceID: attempt.resourceID)
        XCTAssertNil(MusicSystemMetadataBinding(entry: entry, attempt: foreign, activeAttemptID: attempt.id,
            activeResourceID: "resource", activeOccurrenceID: entry.id, access: access, snapshot: snapshot))
        for phase in [PlaybackPhase.idle, .failed] {
            XCTAssertNil(MusicSystemMetadataBinding(entry: entry, attempt: attempt, activeAttemptID: attempt.id,
                activeResourceID: "resource", activeOccurrenceID: entry.id, access: access,
                snapshot: .init(owner: "music", resourceID: "resource", phase: phase)))
        }
        let newAccess = MusicSystemMetadataBinding(entry: entry, attempt: attempt, activeAttemptID: attempt.id,
            activeResourceID: "resource", activeOccurrenceID: entry.id, access: UUID(), snapshot: snapshot)
        XCTAssertNotEqual(valid, newAccess)
    }

    @MainActor func testDisplayedArtworkWorksInFixedModeAndRejectsOtherSongCoverAndRevokedTicket() throws {
        let (appearance, defaults, name) = store()
        defer { defaults.removePersistentDomain(forName: name) }
        appearance.set(\.colorMode, .fixed)
        let song = track(), key = MusicAppearanceSongIdentity(track: track())
        appearance.selectSystemArtwork(song)
        let ticket = try XCTUnwrap(appearance.beginDisplayedArtwork(for: key, artworkID: song.artwork!.absoluteString))
        XCTAssertTrue(appearance.applyDisplayedArtwork(Data([1]), ticket: ticket))
        let value = MusicSystemMetadataCoordinator.metadata(track: song, lines: [], position: 0,
            preferences: appearance.preferences, favorite: false, artwork: appearance.displayedArtwork)
        XCTAssertEqual(value.artwork?.data, Data([1]))
        let changed = MusicSystemMetadataCoordinator.metadata(track: track(cover: "changed"), lines: [],
            position: 0, preferences: appearance.preferences, favorite: false, artwork: appearance.displayedArtwork)
        XCTAssertNil(changed.artwork)
        appearance.setSystemArtworkEnabled(false)
        XCTAssertNil(appearance.displayedArtwork)
        XCTAssertFalse(appearance.applyDisplayedArtwork(Data([2]), ticket: ticket))
        appearance.selectSystemArtwork(song)
        XCTAssertFalse(appearance.applyDisplayedArtwork(Data([3]), ticket: ticket), "Unlock never upgrades a stale callback")
        let fresh = try XCTUnwrap(appearance.beginDisplayedArtwork(for: key, artworkID: song.artwork!.absoluteString))
        XCTAssertTrue(appearance.applyDisplayedArtwork(Data([4]), ticket: fresh))
        appearance.selectSystemArtwork(track("next"))
        XCTAssertNil(appearance.beginDisplayedArtwork(for: key, artworkID: song.artwork!.absoluteString))
        XCTAssertFalse(appearance.applyDisplayedArtwork(Data([5]), ticket: fresh))
    }

    @MainActor func testTwoSurfacesShareOneEncodingAndPaletteModeDoesNotRemoveMetadataImage() throws {
        let (appearance, defaults, name) = store()
        defer { defaults.removePersistentDomain(forName: name) }
        let song = track()
        appearance.selectSystemArtwork(song)
        let mini = MusicNowPlayingArtworkState(), full = MusicNowPlayingArtworkState()
        mini.activate(.init(track: song), appearance: appearance)
        full.activate(.init(track: song), appearance: appearance)
        var encodes = 0
        XCTAssertTrue(mini.captureArtwork(revision: mini.samplingRevision) { encodes += 1; return Data([1]) })
        XCTAssertTrue(full.captureArtwork(revision: full.samplingRevision) { encodes += 1; return Data([2]) })
        XCTAssertEqual(encodes, 1)
        appearance.set(\.colorMode, .fixed)
        mini.preferencesChanged(); full.preferencesChanged()
        XCTAssertEqual(appearance.displayedArtwork?.data, Data([1]))
        mini.deactivate()
        XCTAssertFalse(mini.captureArtwork(revision: mini.samplingRevision) { XCTFail("inactive"); return nil })
        XCTAssertTrue(full.captureArtwork(revision: full.samplingRevision) { XCTFail("already encoded"); return nil })
    }

    @MainActor func testRevocationRejectsPendingImageButCanReusePreviouslyDisplayedImageWithoutReload() throws {
        let (appearance, defaults, name) = store()
        defer { defaults.removePersistentDomain(forName: name) }
        let song = track()
        appearance.selectSystemArtwork(song)
        let pending = MusicNowPlayingArtworkState()
        pending.activate(.init(track: song), appearance: appearance)
        appearance.setSystemArtworkEnabled(false); pending.artworkContextChanged()
        appearance.selectSystemArtwork(song); pending.artworkContextChanged()
        XCTAssertFalse(pending.captureArtwork(revision: pending.samplingRevision) { XCTFail("late load"); return Data([1]) })
        XCTAssertTrue(pending.deliverImage(revision: pending.samplingRevision) {})
        pending.artworkContextChanged()
        XCTAssertFalse(pending.captureArtwork(revision: pending.samplingRevision) { XCTFail("late success cannot upgrade admission"); return Data([1]) })
        pending.deactivate()
        let displayed = MusicNowPlayingArtworkState()
        displayed.activate(.init(track: song), appearance: appearance)
        XCTAssertTrue(displayed.deliverImage(revision: displayed.samplingRevision) {})
        XCTAssertTrue(displayed.captureArtwork(revision: displayed.samplingRevision) { Data([2]) })
        appearance.setSystemArtworkEnabled(false); displayed.artworkContextChanged()
        XCTAssertNil(appearance.displayedArtwork)
        appearance.selectSystemArtwork(song); displayed.artworkContextChanged()
        XCTAssertTrue(displayed.captureArtwork(revision: displayed.samplingRevision) { Data([2]) })
        XCTAssertTrue(displayed.isReady, "The AsyncImage mount is retained; no second URL request")
    }

    @MainActor func testBackgroundRevokesRenderRevisionAndResumeKeepsOneAdmittedImage() throws {
        let (appearance, defaults, name) = store()
        defer { defaults.removePersistentDomain(forName: name) }
        let song = track(), state = MusicNowPlayingArtworkState()
        appearance.selectSystemArtwork(song)
        state.activate(.init(track: song), appearance: appearance)
        let old = state.samplingRevision
        state.suspendRendering()
        XCTAssertNotEqual(state.samplingRevision, old)
        XCTAssertTrue(state.isReady, "Background does not create a new URL request")
        XCTAssertFalse(state.deliverImage(revision: old) { XCTFail("Old image delivered") })
        XCTAssertFalse(state.captureArtwork(revision: old) { XCTFail("Old bitmap encoded"); return Data([1]) })
        XCTAssertFalse(state.captureDisplay(revision: old, encode: { Data([1]) }) { _ in XCTFail("Old cache committed") })
        XCTAssertFalse(state.sample(revision: old) { XCTFail("Old pixels sampled"); return [255, 0, 0, 255] })
        state.becameActive()
        let fresh = state.samplingRevision
        XCTAssertNotEqual(fresh, old)
        XCTAssertTrue(state.deliverImage(revision: fresh) {})
        XCTAssertFalse(state.deliverImage(revision: fresh) { XCTFail("Duplicate delivery") })
        XCTAssertTrue(state.captureArtwork(revision: fresh) { Data([2]) })
        XCTAssertEqual(appearance.displayedArtwork?.data, Data([2]))
        state.deactivate()
        XCTAssertFalse(state.captureArtwork(revision: fresh) { XCTFail("Unmounted"); return nil })
    }

    @MainActor func testRepeatedSameURLImageChangesCannotLoseReplacementRevision() throws {
        let (appearance, defaults, name) = store()
        defer { defaults.removePersistentDomain(forName: name) }
        let song = track(), state = MusicNowPlayingArtworkState()
        appearance.selectSystemArtwork(song)
        state.activate(.init(track: song), appearance: appearance)
        XCTAssertTrue(state.deliverImage(revision: state.samplingRevision) {})
        XCTAssertTrue(state.captureArtwork(revision: state.samplingRevision) { Data([1]) })
        let first = state.samplingRevision
        state.imageChanged()
        let second = state.samplingRevision
        state.imageChanged()
        let latest = state.samplingRevision
        XCTAssertFalse(state.captureArtwork(revision: first) { XCTFail("Old bytes"); return Data([8]) })
        XCTAssertFalse(state.captureArtwork(revision: second) { XCTFail("Intermediate bytes"); return Data([9]) })
        XCTAssertTrue(state.needsArtwork(revision: latest))
        XCTAssertTrue(state.deliverImage(revision: latest) {})
        XCTAssertTrue(state.captureArtwork(revision: latest) { Data([3]) })
        XCTAssertEqual(appearance.displayedArtwork?.data, Data([3]), "Same URL is not proof of same image bytes")
        XCTAssertFalse(state.needsArtwork(revision: latest))
        state.deactivate()
    }

    @MainActor func testCoordinatorIsPassiveAndCloseCancelsDeferredSubscriptions() async {
        let (appearance, defaults, name) = store()
        defer { defaults.removePersistentDomain(forName: name) }
        let service = MusicCollectionService()
        let model = MusicViewModel(service: service, player: MusicCollectionPlayer())
        let playback = PlaybackController()
        let bridge = MusicSystemMetadataCoordinator(model: model, playback: playback, appearance: appearance)
        let pending = bridge.pendingUpdate
        XCTAssertNil(playback.currentAttempt)
        XCTAssertNil(playback.player.currentItem)
        bridge.close(); bridge.close()
        await pending?.value
        appearance.set(\.lockLyric, false)
        model.setAvailable(false)
        XCTAssertNil(bridge.pendingUpdate, "Closed subscriptions must not schedule another update")
        XCTAssertNil(playback.currentAttempt)
        XCTAssertNil(playback.nowPlayingMetadata)
        await model.close()
    }
}
#endif
