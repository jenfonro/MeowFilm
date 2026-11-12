#if os(iOS)
import XCTest
import UIKit
import MediaPlayer
import Combine
import MeowContracts
import MeowPlayback
@testable import MeowMusicFeature

/// Real UIKit decoding, shared image caching and the existing Now Playing
/// writer, using synthetic HTTPS artwork and a silent local media file.
@MainActor
final class MusicSystemMediaMetadataTests: XCTestCase {
    func testWarmEntryCoverStaysVisibleAcrossMetadataDiscoveryAndPlaybackPreparation() async throws {
        let cover = SystemMetadataArtworkFixture(data: png(.red))
        let alternate = SystemMetadataArtworkFixture(data: png(.blue))
        defer { cover.close(); alternate.close() }
        let images = images()
        let warmed = try await images.read(cover.url).0
        let service = SystemMusicMetadataService()
        let fixture = try SystemMusicMetadataFixture(images: images, service: service)
        defer { fixture.dispose() }
        let original = MusicNativeSource(sourceKey: "tx:cover", platform: "tx", nativeId: "cover")
        let discovered = MusicNativeSource(sourceKey: "wy:cover", platform: "wy", nativeId: "cover")
        func song(_ sources: [MusicNativeSource]) -> MusicRemoteTrack {
            .init(ref: .init(kind: .queue, id: "same"), songId: "same", title: "Same",
                  sources: sources, artwork: cover.url)
        }
        let first = song([original])
        await service.setMetadata(first, candidates: [.init(source: original, artwork: cover.url)])
        try fixture.select(first)
        await wait("Warm artwork appears") { fixture.content.artwork === warmed }
        var published: [UIImage?] = []
        let observation = fixture.content.artworkChanges.sink { published.append(fixture.content.artwork) }
        fixture.model.beginPlaybackMetadata()
        await fixture.model.playbackMetadata.flush()
        await updates()
        XCTAssertTrue(fixture.content.artwork === warmed)

        let enriched = song([discovered, original])
        await service.setMetadata(enriched, candidates: [
            .init(source: discovered, artwork: alternate.url), .init(source: original, artwork: cover.url)])
        fixture.model.playbackState.queue = [.init(id: "same", track: enriched)]
        fixture.model.beginPlaybackMetadata()
        await fixture.model.playbackMetadata.flush()
        await fixture.content.flushArtwork()
        await updates()
        XCTAssertTrue(fixture.content.artwork === warmed)
        XCTAssertTrue(published.allSatisfy { $0 === warmed }, "Both player surfaces must retain the decoded entry image")
        XCTAssertEqual(cover.requestCount, 1)
        XCTAssertEqual(alternate.requestCount, 0, "Discovering another platform cannot fetch a replacement cover")
        withExtendedLifetime(observation) {}
        await fixture.model.close()
    }

    func testBackgroundFirstArtworkReachesSystemAndLyricsReuseTheSameImage() async throws {
        let started = expectation(description: "Current artwork requested")
        let cover = SystemMetadataArtworkFixture(data: png(.red), held: true, started: { started.fulfill() })
        defer { cover.close() }
        let fixture = try SystemMusicMetadataFixture(images: images())
        defer { fixture.dispose() }
        defer { NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil) }
        let song = track("first", artwork: cover.url)
        let attempt = try fixture.select(song)
        await fulfillment(of: [started], timeout: 3)
        NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
        let colors = fixture.defaults.data(forKey: MusicAppearancePreferencesStore.songColorsKey)
        cover.finish()
        await fixture.content.flushArtwork()
        await wait("Background artwork published") {
            fixture.playback.nowPlayingMetadata?.artwork != nil && self.systemArtwork != nil
        }
        let data = try XCTUnwrap(fixture.playback.nowPlayingMetadata?.artwork?.data)
        XCTAssertNotNil(UIImage(data: data))
        let published = try XCTUnwrap(systemArtwork)
        XCTAssertGreaterThan(published.image(at: CGSize(width: 64, height: 64))?.size.width ?? 0, 0)
        XCTAssertEqual(fixture.defaults.data(forKey: MusicAppearancePreferencesStore.songColorsKey), colors,
                       "Background system encoding must not sample the foreground palette")

        fixture.model.metadataState.artworkURL = song.artwork
        fixture.model.metadataState.occurrenceID = fixture.model.current?.id
        fixture.model.metadataState.lyricLines = [
            .init(index: 0, time: 0, end: 12, text: "原文一", translation: "Translation one"),
            .init(index: 1, time: 12, end: 30, text: "原文二", translation: "Translation two")
        ]
        fixture.model.clock.position = 15
        await wait("Original lyric and song/artist published") {
            fixture.playback.nowPlayingMetadata?.title == "原文二"
        }
        XCTAssertEqual(fixture.playback.nowPlayingMetadata?.artist, "歌曲 first / 歌手")
        XCTAssertEqual(MPNowPlayingInfoCenter.default().nowPlayingInfo?[MPMediaItemPropertyTitle] as? String, "原文二")
        XCTAssertEqual(MPNowPlayingInfoCenter.default().nowPlayingInfo?[MPMediaItemPropertyArtist] as? String, "歌曲 first / 歌手")
        fixture.playback.resume(attempt: attempt)
        await wait("Silent local media playing") { fixture.playback.snapshot.phase == .playing }
        fixture.playback.pause(attempt: attempt)
        XCTAssertEqual(fixture.playback.snapshot.phase, .paused)
        XCTAssertEqual(fixture.playback.nowPlayingMetadata?.title, "原文二")
        fixture.model.clock.position = 4
        await wait("Position returns to preceding lyric") { fixture.playback.nowPlayingMetadata?.title == "原文一" }
        fixture.appearance.set(\.lockLyric, false)
        await wait("Disabled lyrics restore song and artist") {
            fixture.playback.nowPlayingMetadata?.title == "歌曲 first" && fixture.playback.nowPlayingMetadata?.artist == "歌手"
        }
        XCTAssertEqual(cover.requestCount, 1)
        XCTAssertEqual(fixture.playback.nowPlayingMetadata?.artwork?.data, data)
        XCTAssertTrue(systemArtwork === published, "Text, progress and pause/resume retain the encoded system image")
        await fixture.model.close()
    }

    func testBackgroundTrackSwitchRejectsPendingPreviousImage() async throws {
        let oldStarted = expectation(description: "Previous song artwork requested")
        let nextStarted = expectation(description: "Current song artwork requested")
        let old = SystemMetadataArtworkFixture(data: png(.red), held: true, started: { oldStarted.fulfill() })
        let next = SystemMetadataArtworkFixture(data: png(.blue), held: true, started: { nextStarted.fulfill() })
        defer { old.close(); next.close() }
        let fixture = try SystemMusicMetadataFixture(images: images())
        defer { fixture.dispose() }
        defer { NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil) }
        NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
        try fixture.select(track("old", artwork: old.url))
        await fulfillment(of: [oldStarted], timeout: 3)
        try fixture.select(track("next", artwork: next.url))
        await fulfillment(of: [nextStarted], timeout: 3)
        XCTAssertNil(fixture.appearance.displayedArtwork)
        next.finish()
        await wait("Current background image published") {
            fixture.playback.nowPlayingMetadata?.title == "歌曲 next" && fixture.playback.nowPlayingMetadata?.artwork != nil
        }
        let current = fixture.playback.nowPlayingMetadata
        old.finish()
        await fixture.content.flushArtwork()
        await updates()
        XCTAssertEqual(fixture.playback.nowPlayingMetadata, current)
        XCTAssertEqual(fixture.playback.nowPlayingMetadata?.title, "歌曲 next")
        await fixture.model.close()
    }

    func testBackgroundSwitchUsesWarmImageAndMissingOrFailedCoverClearsThePreviousSong() async throws {
        let first = SystemMetadataArtworkFixture(data: png(.red))
        let next = SystemMetadataArtworkFixture(data: png(.blue))
        let cold = SystemMetadataArtworkFixture(data: png(.green))
        let failed = SystemMetadataArtworkFixture(data: Data([1, 2, 3]))
        defer { first.close(); next.close(); cold.close(); failed.close() }
        let sharedImages = images()
        let warm = try await sharedImages.read(next.url).0
        let fixture = try SystemMusicMetadataFixture(images: sharedImages)
        defer { fixture.dispose() }
        defer { NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil) }
        NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
        try fixture.select(track("first", artwork: first.url))
        await wait("First background cover published") { fixture.playback.nowPlayingMetadata?.artwork != nil }
        let old = fixture.playback.nowPlayingMetadata?.artwork
        try fixture.select(track("next", artwork: next.url))
        XCTAssertNil(fixture.appearance.displayedArtwork)
        await wait("Warm next cover published") {
            fixture.playback.nowPlayingMetadata?.title == "歌曲 next" && fixture.playback.nowPlayingMetadata?.artwork != nil
        }
        XCTAssertTrue(fixture.content.artwork === warm)
        XCTAssertNotEqual(fixture.playback.nowPlayingMetadata?.artwork, old)
        XCTAssertEqual(next.requestCount, 1, "Background cache hits must not start another transport")
        try fixture.select(track("cold", artwork: cold.url))
        XCTAssertNil(fixture.appearance.displayedArtwork)
        await wait("Uncached background next cover published") {
            fixture.playback.nowPlayingMetadata?.title == "歌曲 cold" && fixture.playback.nowPlayingMetadata?.artwork != nil
        }
        XCTAssertGreaterThanOrEqual(cold.requestCount, 1)
        try fixture.select(track("missing", artwork: nil))
        await wait("Missing cover clears system image") {
            fixture.playback.nowPlayingMetadata?.title == "歌曲 missing" && self.systemArtwork == nil
        }
        XCTAssertNil(fixture.playback.nowPlayingMetadata?.artwork)
        try fixture.select(track("failed", artwork: failed.url))
        await wait("Invalid image requested") { failed.requestCount >= 1 }
        await fixture.content.flushArtwork()
        await wait("Failed cover leaves current metadata without old artwork") {
            fixture.playback.nowPlayingMetadata?.title == "歌曲 failed"
        }
        XCTAssertNil(fixture.playback.nowPlayingMetadata?.artwork)
        XCTAssertNil(systemArtwork)
        await fixture.model.close()
    }

    func testPendingArtworkCannotPublishAfterAccountRevocationCloseOrFilmTakeover() async throws {
        defer { NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil) }
        for boundary in ["account", "modelClose", "metadataClose", "film"] {
            let started = expectation(description: boundary + " artwork requested")
            let cover = SystemMetadataArtworkFixture(data: png(.green), held: true, started: { started.fulfill() })
            let fixture = try SystemMusicMetadataFixture(images: images())
            defer { fixture.dispose(); cover.close() }
            try fixture.select(track(boundary, artwork: cover.url))
            NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
            await fulfillment(of: [started], timeout: 3)
            switch boundary {
            case "account": fixture.model.setAvailable(false)
            case "modelClose": await fixture.model.close()
            case "metadataClose": fixture.bridge.close()
            default:
                let film = fixture.playback.playAttempt(
                    resource: try .init(id: "film", url: fixture.media, title: "Film"), owner: "film", autoplay: false)
                XCTAssertTrue(fixture.playback.setNowPlayingMetadata(.init(title: "Film"), attempt: film))
            }
            XCTAssertNil(fixture.appearance.displayedArtwork, boundary)
            cover.finish()
            await fixture.content.flushArtwork()
            await updates()
            XCTAssertNil(fixture.appearance.displayedArtwork, boundary)
            if boundary == "film" {
                XCTAssertEqual(fixture.playback.nowPlayingMetadata?.title, "Film")
                XCTAssertEqual(MPNowPlayingInfoCenter.default().nowPlayingInfo?[MPMediaItemPropertyTitle] as? String, "Film")
            } else {
                XCTAssertNil(fixture.playback.nowPlayingMetadata, boundary)
            }
            XCTAssertNil(systemArtwork, boundary)
            await fixture.model.close()
        }
    }

    func testSameSongNewAttemptRejectsOldLoadAndOldOccurrenceLyrics() async throws {
        let started = expectation(description: "Old attempt artwork requested")
        let cover = SystemMetadataArtworkFixture(data: png(.purple), held: true, started: { started.fulfill() })
        defer { cover.close() }
        let fixture = try SystemMusicMetadataFixture(images: images())
        defer { fixture.dispose() }
        let song = track("same", artwork: cover.url)
        try fixture.select(song)
        await fulfillment(of: [started], timeout: 3)
        fixture.model.metadataState.artworkURL = song.artwork
        fixture.model.metadataState.occurrenceID = fixture.model.current?.id
        fixture.model.metadataState.lyricLines = [.init(index: 0, time: 0, end: 30, text: "旧歌词", translation: "Old")]
        await wait("Old occurrence lyric published") { fixture.playback.nowPlayingMetadata?.title == "旧歌词" }
        let new = fixture.playback.playAttempt(
            resource: try .init(id: "music:same", url: fixture.media, title: "same"), owner: "music", autoplay: false)
        XCTAssertNotEqual(fixture.model.activeNativeAttemptID, new.id)
        cover.finish()
        await fixture.content.flushArtwork()
        await updates()
        XCTAssertNil(fixture.playback.nowPlayingMetadata, "The model has not admitted this new native attempt")
        XCTAssertNil(fixture.appearance.displayedArtwork)
        try fixture.select(track("replacement", artwork: nil))
        await wait("Old lyric cannot replace the new occurrence title") {
            fixture.playback.nowPlayingMetadata?.title == "歌曲 replacement"
        }
        XCTAssertEqual(fixture.playback.nowPlayingMetadata?.artist, "歌手")
        await fixture.model.close()
    }

    func testContentToggleCancelsSuspendedResumeAndExplicitPlayStillWorks() async throws {
        let service = SystemMusicMetadataService()
        let fixture = try SystemMusicMetadataFixture(images: images(), service: service)
        defer { fixture.dispose() }
        var played = 0
        let lease = try fixture.playback.bind(owner: "music") { event in
            if event.kind == .played { played += 1 }
            fixture.model.onPlaybackEvent(event)
        }
        defer { lease.cancel() }
        let attempt = try fixture.select(track("toggle", artwork: nil))
        await wait("Local media ready without autoplay") { fixture.playback.snapshot.phase == .paused }
        let started = expectation(description: "Content resume source admission is held")
        await service.holdNextExclusionRead { started.fulfill() }
        defer { Task { await service.releaseExclusionRead() } }

        fixture.content.togglePlayback()
        await fulfillment(of: [started], timeout: 3)
        let pending = try XCTUnwrap(fixture.model.playPauseCommandTask?.task)
        fixture.content.togglePlayback()
        await wait("Second content toggle cancels the pending resume") { pending.isCancelled }
        await service.releaseExclusionRead()
        await pending.value
        await updates()
        XCTAssertEqual(fixture.playback.snapshot.phase, .paused)
        XCTAssertEqual(fixture.model.phase, .paused)
        XCTAssertEqual(played, 0, "Late source admission must not revive the cancelled UI intent")
        XCTAssertEqual(fixture.playback.currentAttempt, attempt)

        fixture.content.togglePlayback()
        await wait("A later explicit content play resumes the same attempt") {
            fixture.playback.snapshot.phase == .playing
        }
        XCTAssertEqual(played, 1)
        XCTAssertEqual(fixture.playback.currentAttempt, attempt)
        let reads = await service.exclusionReadCount
        XCTAssertEqual(reads, 2)
        XCTAssertNil(fixture.model.error)
        await fixture.model.close()
    }

    func testContentReleaseCancelsNewResumeAfterOlderToggleFinishes() async throws {
        let service = SystemMusicMetadataService()
        let fixture = try SystemMusicMetadataFixture(images: images(), service: service)
        defer { fixture.dispose() }
        var played = 0
        let prepared = expectation(description: "Local media prepared without autoplay")
        let lease = try fixture.playback.bind(owner: "music") { event in
            if event.kind == .played { played += 1 }
            fixture.model.onPlaybackEvent(event)
            if event.kind == .prepared { prepared.fulfill() }
        }
        defer { lease.cancel() }
        let attempt = try fixture.select(track("release", artwork: nil))
        await fulfillment(of: [prepared], timeout: 3)
        // autoplay:false publishes paused before readiness. Establish the
        // native pause baseline before testing cancellation of resume intents.
        XCTAssertTrue(fixture.playback.pause(attempt: attempt))
        XCTAssertEqual(fixture.playback.snapshot.phase, .paused)
        XCTAssertEqual(played, 0)
        var content: MusicPlayerContentModel? = MusicPlayerContentModel(
            model: fixture.model, playback: fixture.playback, appearance: fixture.appearance,
            presentation: MusicMiniPlayerPresentation(), artworkImages: images())
        weak var retainedContent = content
        let oldStarted = expectation(description: "Old UI resume admission is held")
        await service.holdNextExclusionRead { oldStarted.fulfill() }
        defer { Task { await service.releaseExclusionRead() } }
        content?.togglePlayback()
        await fulfillment(of: [oldStarted], timeout: 3)
        let old = try XCTUnwrap(fixture.model.playPauseCommandTask)
        content?.togglePlayback()
        await wait("Second content toggle cancels the old command") { old.task.isCancelled }

        let newStarted = expectation(description: "New UI resume admission is held")
        await service.holdNextExclusionRead { newStarted.fulfill() }
        content?.togglePlayback()
        await wait("New explicit resume is queued behind the old command") {
            guard let pending = fixture.model.playPauseCommandTask else { return false }
            return pending.id != old.id && !pending.task.isCancelled
        }
        let pending = try XCTUnwrap(fixture.model.playPauseCommandTask?.task)
        await service.releaseExclusionRead()
        await old.task.value
        await updates()
        XCTAssertFalse(pending.isCancelled, "The new explicit resume must survive the older command's completion")
        await fulfillment(of: [newStarted], timeout: 3)
        let reads = await service.exclusionReadCount
        XCTAssertEqual(reads, 2, "Both source admissions must enter their respective held reads")
        content = nil
        XCTAssertNil(retainedContent, "A pending UI wrapper must not retain its Content owner")
        await wait("Content release cancels the newer command despite the old defer") { pending.isCancelled }
        await service.releaseExclusionRead()
        await pending.value
        await updates()
        XCTAssertEqual(fixture.playback.snapshot.phase, .paused)
        XCTAssertEqual(played, 0, "Neither late admission may outlive its cancelled UI command")
        XCTAssertEqual(fixture.playback.currentAttempt, attempt)
        await fixture.model.close()
    }

    private var systemArtwork: MPMediaItemArtwork? {
        MPNowPlayingInfoCenter.default().nowPlayingInfo?[MPMediaItemPropertyArtwork] as? MPMediaItemArtwork
    }

    private func images() -> MusicArtworkImages {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [SystemMetadataArtworkProtocol.self]
        return MusicArtworkImages(http: MusicArtworkHTTP(configuration: configuration))
    }

    private func track(_ id: String, artwork: URL?) -> MusicRemoteTrack {
        .init(ref: .init(kind: .queue, id: id), songId: id, title: "歌曲 " + id, artist: "歌手", artwork: artwork)
    }

    private func png(_ color: UIColor) -> Data {
        UIGraphicsImageRenderer(size: CGSize(width: 24, height: 24)).pngData { context in
            color.setFill(); context.fill(CGRect(x: 0, y: 0, width: 24, height: 24))
        }
    }

    private func updates() async {
        await withCheckedContinuation { continuation in RunLoop.main.perform { continuation.resume() } }
    }

    private func wait(_ description: String, until ready: () -> Bool) async {
        let deadline = Date().addingTimeInterval(3)
        while !ready(), Date() < deadline { try? await Task.sleep(for: .milliseconds(10)) }
        XCTAssertTrue(ready(), description)
    }
}

@MainActor
private final class SystemMusicMetadataFixture {
    let playback: PlaybackController
    let model: MusicViewModel
    let appearance: MusicAppearancePreferencesStore
    let content: MusicPlayerContentModel
    let bridge: MusicSystemMetadataCoordinator
    let defaults: UserDefaults
    let media: URL
    private let suite: String

    init(images: MusicArtworkImages, service: SystemMusicMetadataService = SystemMusicMetadataService()) throws {
        let suite = "MusicSystemMediaMetadataTests." + UUID().uuidString
        let defaults = UserDefaults(suiteName: suite)!
        let appearance = MusicAppearancePreferencesStore(defaults: defaults)
        let playback = PlaybackController()
        let model = MusicViewModel(service: service, playback: playback)
        let bridge = MusicSystemMetadataCoordinator(model: model, playback: playback, appearance: appearance)
        let content = MusicPlayerContentModel(model: model, playback: playback, appearance: appearance,
            presentation: MusicMiniPlayerPresentation(), artworkImages: images)
        let media = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".wav")
        self.suite = suite; self.defaults = defaults; self.appearance = appearance
        self.playback = playback; self.model = model; self.bridge = bridge; self.content = content; self.media = media
        var data = Data()
        func text(_ value: String) { data.append(contentsOf: value.utf8) }
        func number<T: FixedWidthInteger>(_ value: T) {
            var little = value.littleEndian
            withUnsafeBytes(of: &little) { data.append(contentsOf: $0) }
        }
        let length: UInt32 = 30 * 8000 * 2
        text("RIFF"); number(36 + length); text("WAVEfmt "); number(UInt32(16))
        number(UInt16(1)); number(UInt16(1)); number(UInt32(8000)); number(UInt32(16000))
        number(UInt16(2)); number(UInt16(16)); text("data"); number(length)
        data.append(Data(count: Int(length))); try data.write(to: media)
    }

    @discardableResult func select(_ track: MusicRemoteTrack) throws -> PlayAttempt {
        let resource = try PlaybackResource(id: "music:" + track.songId, url: media, title: track.title)
        let attempt = playback.playAttempt(resource: resource, owner: "music", autoplay: false)
        _ = playback.setVolume(0, attempt: attempt)
        model.nativeAttempt = .init(id: attempt.id, resourceID: resource.id)
        let selection = MusicPlaybackSelection(installationId: "official:fixture", generation: 1,
            siteId: "wy", sourceKey: "wy:" + track.songId, quality: "128k")
        model.candidate = .init(attemptId: track.songId, occurrenceId: track.songId, resourceId: resource.id,
            url: media, title: track.title, requestedQuality: "128k", resolvedSelection: selection)
        model.lastSelection = selection
        model.playbackState.queue = [.init(id: track.songId, track: track)]
        model.playbackState.state = .init(currentId: track.songId, total: 1)
        model.playbackState.phase = .paused
        model.clock.duration = 30
        model.clock.position = 0
        return attempt
    }

    func dispose() {
        bridge.close(); playback.stop()
        defaults.removePersistentDomain(forName: suite)
        try? FileManager.default.removeItem(at: media)
    }
}

private actor SystemMusicMetadataService: MusicServing, MusicSourceExclusionServing {
    private var metadata: MusicMetadataCandidates?
    private var nextExclusionRead: (@Sendable () -> Void)?
    private var pendingExclusionRead: CheckedContinuation<[MusicSourceExclusion], Never>?
    private(set) var exclusionReadCount = 0

    func holdNextExclusionRead(_ started: @escaping @Sendable () -> Void) { nextExclusionRead = started }
    func releaseExclusionRead() {
        let pending = pendingExclusionRead; pendingExclusionRead = nil
        pending?.resume(returning: [])
    }
    func sourceExclusions() async -> [MusicSourceExclusion] {
        exclusionReadCount += 1
        guard let started = nextExclusionRead else { return [] }
        nextExclusionRead = nil
        // Deliberately ignore cancellation so the late reply exercises admission.
        return await withCheckedContinuation { pendingExclusionRead = $0; started() }
    }
    func excludeSource(for reference: MusicReference, provider: String, platform: String) throws {
        throw MusicServiceError.unsupported
    }
    func restoreSourceExclusion(id: String) throws { throw MusicServiceError.unsupported }
    func state() throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func platforms() -> [MusicPlatform] { [] }
    func search(platform: String, query: String, page: Int, limit: Int) throws -> MusicSearchPage { throw MusicServiceError.unsupported }
    func collections() -> [MusicCollection] { [] }
    func tracks(in collection: MusicCollection) -> [MusicRemoteTrack] { [] }
    func createPlaylist(name: String, refs: [MusicReference]) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func removePlaylist(_ collection: MusicCollection) throws { throw MusicServiceError.unsupported }
    func queue() throws -> MusicQueueSnapshot { throw MusicServiceError.unsupported }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) throws -> MusicAdvanceResult { throw MusicServiceError.unsupported }
    func updateSettings(_ change: MusicSettingsChange) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func sourceAvailability(for ref: MusicReference) -> MusicSourceAvailability { .init(options: [], scriptState: .notConfigured) }
    func metadataPreferences() throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
    func updateMetadataPreferences(_ change: MusicMetadataPreferencesChange) throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
    func setMetadata(_ track: MusicRemoteTrack, candidates: [MusicMetadataCandidate]) {
        metadata = .init(occurrenceID: track.ref.id, queueRevision: 0,
            preferences: .init(revision: 0, platforms: [.netease, .qq], enabledPlatforms: [.netease, .qq]),
            track: track, contentRevision: nil, contentFingerprint: track.songId, candidates: candidates)
    }
    func metadataCandidates(occurrenceID: String) throws -> MusicMetadataCandidates {
        guard let metadata, metadata.occurrenceID == occurrenceID else { throw MusicServiceError.unsupported }
        return metadata
    }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) throws -> MusicPlaybackCandidate { throw MusicServiceError.unsupported }
    func cancel(attemptId: String?) {}
    func record(_ fact: MusicPlaybackFact) -> Bool { false }
    func lyrics(occurrenceId: String, sourceKey: String) throws -> MusicLyricsSnapshot { throw MusicServiceError.unsupported }
    func playedSongs() -> [MusicPlayedSong] { [] }
    func playedPlaylists() -> [MusicPlayedPlaylist] { [] }
    func restoreHistory(id: String, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
}

private final class SystemMetadataArtworkFixture: @unchecked Sendable {
    let url = URL(string: "https://system-artwork.invalid/\(UUID().uuidString).png")!
    private let data: Data
    private let started: (@Sendable () -> Void)?
    private let lock = NSLock()
    private var held: Bool
    private var reads = 0
    private var pending: [ObjectIdentifier: URLProtocol] = [:]
    var requestCount: Int { lock.lock(); defer { lock.unlock() }; return reads }

    init(data: Data, held: Bool = false, started: (@Sendable () -> Void)? = nil) {
        self.data = data; self.held = held; self.started = started
        SystemMetadataArtworkProtocol.register(self)
    }

    func load(_ instance: URLProtocol) {
        lock.lock(); reads += 1; let first = reads == 1, wait = held
        if wait { pending[ObjectIdentifier(instance)] = instance }
        lock.unlock()
        if first { started?() }
        if !wait { respond(instance) }
    }

    func stop(_ instance: URLProtocol) {
        lock.lock(); pending[ObjectIdentifier(instance)] = nil; lock.unlock()
    }

    func finish() {
        lock.lock(); held = false; let instances = Array(pending.values); pending.removeAll(); lock.unlock()
        for instance in instances { respond(instance) }
    }

    private func respond(_ instance: URLProtocol) {
        let response = HTTPURLResponse(url: url, statusCode: 200, httpVersion: "HTTP/1.1",
            headerFields: ["Content-Type": "image/png"])!
        instance.client?.urlProtocol(instance, didReceive: response, cacheStoragePolicy: .notAllowed)
        instance.client?.urlProtocol(instance, didLoad: data)
        instance.client?.urlProtocolDidFinishLoading(instance)
    }

    func close() { finish(); SystemMetadataArtworkProtocol.remove(url) }
}

private final class SystemMetadataArtworkProtocol: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    private static var fixtures: [URL: SystemMetadataArtworkFixture] = [:]
    private var fixture: SystemMetadataArtworkFixture?
    static func register(_ fixture: SystemMetadataArtworkFixture) {
        lock.lock(); fixtures[fixture.url] = fixture; lock.unlock()
    }
    static func remove(_ url: URL) { lock.lock(); fixtures[url] = nil; lock.unlock() }
    override class func canInit(with request: URLRequest) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return request.url.flatMap { fixtures[$0] } != nil
    }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        Self.lock.lock(); fixture = request.url.flatMap { Self.fixtures[$0] }; Self.lock.unlock()
        fixture?.load(self)
    }
    override func stopLoading() { fixture?.stop(self) }
}
#endif
