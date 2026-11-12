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
        XCTAssertEqual(value.artist, "歌曲 ♡︎ / 歌手")
        XCTAssertEqual(value.album, "我的曲库")
        preferences.lockLyric = false
        let disabled = MusicSystemMetadataCoordinator.metadata(track: track(), lines: lines, position: 15,
            preferences: preferences, favorite: true, artwork: nil)
        XCTAssertEqual(disabled.title, "歌曲 ♡︎")
        XCTAssertEqual(disabled.artist, "歌手")
        let beforeLine = MusicSystemMetadataCoordinator.metadata(track: track(), lines: lines, position: 0,
            preferences: .init(), favorite: false, artwork: nil)
        XCTAssertEqual(beforeLine.title, "歌曲")
        XCTAssertEqual(beforeLine.artist, "歌手")
    }

    @MainActor func testTimelineSeekAndEmptyLyricsRestoreSongAndArtist() {
        let lines: [MusicLyricLine] = [
            .init(index: 0, time: 3, end: 12, text: "原文一", translation: "Translation one"),
            .init(index: 1, time: 12, end: 30, text: "原文二", translation: "Translation two")
        ]
        for (position, title) in [(Double(3), "原文一"), (15, "原文二"), (4, "原文一"), (0, "歌曲")] {
            let value = MusicSystemMetadataCoordinator.metadata(track: track(), lines: lines, position: position,
                preferences: .init(), favorite: false, artwork: nil)
            XCTAssertEqual(value.title, title)
            XCTAssertEqual(value.artist, position < 3 ? "歌手" : "歌曲 / 歌手")
        }
        let instrumental = MusicSystemMetadataCoordinator.metadata(track: track(), lines: [], position: 15,
            preferences: .init(), favorite: false, artwork: nil)
        XCTAssertEqual(instrumental.title, "歌曲")
        XCTAssertEqual(instrumental.artist, "歌手")
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
        XCTAssertTrue(mini.captureArtwork(revision: mini.artworkRevision) { encodes += 1; return Data([1]) })
        XCTAssertTrue(full.captureArtwork(revision: full.artworkRevision) { encodes += 1; return Data([2]) })
        XCTAssertEqual(encodes, 1)
        appearance.set(\.colorMode, .fixed)
        mini.preferencesChanged(); full.preferencesChanged()
        XCTAssertEqual(appearance.displayedArtwork?.data, Data([1]))
        mini.deactivate()
        XCTAssertFalse(mini.captureArtwork(revision: mini.artworkRevision) { XCTFail("inactive"); return nil })
        XCTAssertTrue(full.captureArtwork(revision: full.artworkRevision) { XCTFail("already encoded"); return nil })
    }

    @MainActor func testRevocationRejectsPendingImageButCanReusePreviouslyDisplayedImageWithoutReload() throws {
        let (appearance, defaults, name) = store()
        defer { defaults.removePersistentDomain(forName: name) }
        let song = track()
        appearance.selectSystemArtwork(song)
        let pending = MusicNowPlayingArtworkState()
        pending.activate(.init(track: song), appearance: appearance)
        let pendingRevision = pending.artworkRevision
        appearance.setSystemArtworkEnabled(false); pending.artworkContextChanged()
        appearance.selectSystemArtwork(song); pending.artworkContextChanged()
        XCTAssertFalse(pending.captureArtwork(revision: pendingRevision) { XCTFail("late load"); return Data([1]) })
        XCTAssertTrue(pending.deliverImage(revision: pending.samplingRevision) {})
        pending.artworkContextChanged()
        XCTAssertFalse(pending.captureArtwork(revision: pendingRevision) { XCTFail("late success cannot upgrade admission"); return Data([1]) })
        pending.deactivate()
        let displayed = MusicNowPlayingArtworkState()
        displayed.activate(.init(track: song), appearance: appearance)
        XCTAssertTrue(displayed.deliverImage(revision: displayed.samplingRevision) {})
        XCTAssertTrue(displayed.captureArtwork(revision: displayed.artworkRevision) { Data([2]) })
        appearance.setSystemArtworkEnabled(false); displayed.artworkContextChanged()
        XCTAssertNil(appearance.displayedArtwork)
        appearance.selectSystemArtwork(song); displayed.artworkContextChanged()
        XCTAssertTrue(displayed.captureArtwork(revision: displayed.artworkRevision) { Data([2]) })
        XCTAssertTrue(displayed.isReady, "The AsyncImage mount is retained; no second URL request")
    }

    @MainActor func testBackgroundRevokesRenderRevisionAndResumeKeepsOneAdmittedImage() throws {
        let (appearance, defaults, name) = store()
        defer { defaults.removePersistentDomain(forName: name) }
        let song = track(), state = MusicNowPlayingArtworkState()
        appearance.selectSystemArtwork(song)
        state.activate(.init(track: song), appearance: appearance)
        let old = state.samplingRevision
        let imageRevision = state.artworkRevision
        state.suspendRendering()
        XCTAssertNotEqual(state.samplingRevision, old)
        XCTAssertTrue(state.isReady, "Background does not create a new URL request")
        XCTAssertFalse(state.deliverImage(revision: old) { XCTFail("Old image delivered") })
        XCTAssertEqual(state.artworkRevision, imageRevision)
        XCTAssertTrue(state.captureArtwork(revision: imageRevision) { Data([1]) })
        XCTAssertEqual(appearance.displayedArtwork?.data, Data([1]))
        XCTAssertFalse(state.captureDisplay(revision: old, encode: { Data([1]) }) { _ in XCTFail("Old cache committed") })
        XCTAssertFalse(state.sample(revision: old) { XCTFail("Old pixels sampled"); return [255, 0, 0, 255] })
        let background = state.samplingRevision
        XCTAssertFalse(state.deliverImage(revision: background) { XCTFail("Background view delivered") })
        XCTAssertFalse(state.captureDisplay(revision: background, encode: { Data([1]) }) { _ in XCTFail("Background display rendered") })
        XCTAssertFalse(state.sample(revision: background) { XCTFail("Background pixels sampled"); return [255, 0, 0, 255] })
        state.becameActive()
        let fresh = state.samplingRevision
        XCTAssertNotEqual(fresh, old)
        XCTAssertTrue(state.deliverImage(revision: fresh) {})
        XCTAssertFalse(state.deliverImage(revision: fresh) { XCTFail("Duplicate delivery") })
        XCTAssertTrue(state.sample(revision: fresh) { [240, 45, 55, 255] })
        XCTAssertFalse(state.sample(revision: fresh) { XCTFail("Duplicate sampling"); return nil })
        XCTAssertTrue(state.captureArtwork(revision: state.artworkRevision) { XCTFail("Already encoded"); return Data([2]) })
        XCTAssertEqual(appearance.displayedArtwork?.data, Data([1]))
        let admitted = state.artworkRevision
        state.deactivate()
        XCTAssertFalse(state.captureArtwork(revision: admitted) { XCTFail("Unmounted"); return nil })
    }

    @MainActor func testBackgroundActivationAndSongChangePublishOnlyCurrentSystemArtwork() throws {
        let (appearance, defaults, name) = store()
        defer { defaults.removePersistentDomain(forName: name) }
        let state = MusicNowPlayingArtworkState(), first = track(), next = track("next", cover: "next")
        appearance.selectSystemArtwork(first)
        state.activate(.init(track: first), appearance: appearance, rendering: false)
        let old = state.artworkRevision
        XCTAssertTrue(state.captureArtwork(revision: old) { Data([1]) })
        XCTAssertFalse(state.sample(revision: state.samplingRevision) { XCTFail("Background sampling"); return nil })
        appearance.selectSystemArtwork(next)
        state.activate(.init(track: next), appearance: appearance, rendering: false)
        XCTAssertNil(appearance.displayedArtwork)
        XCTAssertFalse(state.captureArtwork(revision: old) { XCTFail("Old song"); return Data([1]) })
        XCTAssertTrue(state.captureArtwork(revision: state.artworkRevision) { Data([2]) })
        XCTAssertEqual(appearance.displayedArtwork?.data, Data([2]))
        appearance.setSystemArtworkEnabled(false)
        XCTAssertFalse(state.captureArtwork(revision: state.artworkRevision) { XCTFail("Revoked"); return Data([3]) })
        XCTAssertNil(appearance.displayedArtwork)
    }

    @MainActor func testSystemCoverTargetChangeRequiresNewAdmissionAndRepeatedSelectionKeepsEncoding() throws {
        let (appearance, defaults, name) = store()
        defer { defaults.removePersistentDomain(forName: name) }
        let state = MusicNowPlayingArtworkState(), next = track(cover: "replacement")
        appearance.selectSystemArtwork(track())
        state.activate(.init(track: next), appearance: appearance, rendering: false)
        let old = state.artworkRevision, context = appearance.displayedArtworkRevision
        XCTAssertFalse(state.captureArtwork(revision: old) { XCTFail("Old system cover target"); return Data([1]) })
        appearance.selectSystemArtwork(next)
        XCTAssertNotEqual(appearance.displayedArtworkRevision, context)
        state.artworkContextChanged()
        XCTAssertFalse(state.captureArtwork(revision: old) { XCTFail("Old producer"); return Data([1]) })
        XCTAssertTrue(state.captureArtwork(revision: state.artworkRevision) { Data([2]) })
        let admitted = appearance.displayedArtworkRevision
        appearance.selectSystemArtwork(next)
        XCTAssertEqual(appearance.displayedArtworkRevision, admitted)
        XCTAssertTrue(state.captureArtwork(revision: state.artworkRevision) { XCTFail("Already encoded"); return Data([3]) })
        XCTAssertEqual(appearance.displayedArtwork?.data, Data([2]))
    }

    @MainActor func testArtworkFailureAndOversizedImageDoNotRetainPreviousSong() throws {
        let (appearance, defaults, name) = store()
        defer { defaults.removePersistentDomain(forName: name) }
        let state = MusicNowPlayingArtworkState()
        appearance.selectSystemArtwork(track())
        state.activate(.init(track: track()), appearance: appearance, rendering: false)
        XCTAssertTrue(state.captureArtwork(revision: state.artworkRevision) { Data([1]) })
        let next = track("next")
        appearance.selectSystemArtwork(next)
        state.activate(.init(track: next), appearance: appearance, rendering: false)
        XCTAssertNil(appearance.displayedArtwork)
        XCTAssertFalse(state.captureArtwork(revision: state.artworkRevision) { nil })
        XCTAssertFalse(state.captureArtwork(revision: state.artworkRevision) { Data() })
        XCTAssertFalse(state.captureArtwork(revision: state.artworkRevision) {
            Data(repeating: 0, count: PlaybackNowPlayingArtwork.maximumBytes + 1)
        })
        XCTAssertNil(appearance.displayedArtwork)
    }

    @MainActor func testRepeatedSameURLImageChangesCannotLoseReplacementRevision() throws {
        let (appearance, defaults, name) = store()
        defer { defaults.removePersistentDomain(forName: name) }
        let song = track(), state = MusicNowPlayingArtworkState()
        appearance.selectSystemArtwork(song)
        state.activate(.init(track: song), appearance: appearance)
        XCTAssertTrue(state.deliverImage(revision: state.samplingRevision) {})
        XCTAssertTrue(state.captureArtwork(revision: state.artworkRevision) { Data([1]) })
        let first = state.artworkRevision
        state.imageChanged()
        let second = state.artworkRevision
        state.imageChanged()
        let latest = state.artworkRevision
        XCTAssertFalse(state.captureArtwork(revision: first) { XCTFail("Old bytes"); return Data([8]) })
        XCTAssertFalse(state.captureArtwork(revision: second) { XCTFail("Intermediate bytes"); return Data([9]) })
        XCTAssertTrue(state.needsArtwork(revision: latest))
        XCTAssertTrue(state.deliverImage(revision: state.samplingRevision) {})
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
