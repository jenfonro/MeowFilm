#if os(iOS)
import Foundation
import XCTest
import AVFoundation
import MediaPlayer
import MeowContracts
@testable import MeowPlayback
@testable import MeowMusicFeature

/// These tests post through NotificationCenter into the iOS adapter. Listener
/// readiness is controlled and media is a local WAV, not a real OS interruption
/// or an upstream HLS/physical-headphone acceptance test.
@MainActor
final class PlaybackSystemSessionTests: XCTestCase {
    func testInterruptionEndDuringHLSPreparationKeepsNewOwnerPausedUntilExplicitPlay() async throws {
        try await interruptedHandoff(endedBeforeReady: true)
    }

    func testHLSReadyAndExplicitPlayCannotBypassActiveInterruption() async throws {
        try await interruptedHandoff(endedBeforeReady: false)
    }

    private func interruptedHandoff(endedBeforeReady: Bool) async throws {
        let file = try silentFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let notifications = NotificationCenter(), startup = HeldSystemHLSStartup()
        let controller = PlaybackController(systemNotifications: notifications, startScopedHLS: startup.start)
        let musicPlayed = expectation(description: "Music is playing before interruption")
        let filmReady = expectation(description: "Replacement is ready")
        let filmPlayed = expectation(description: "Explicit replacement playback")
        var played: [PlayAttempt] = []
        let music = try controller.bind(owner: "music") { event in
            if event.kind == .played { played.append(event.attempt); musicPlayed.fulfill() }
        }
        let film = try controller.bind(owner: "film") { event in
            if event.kind == .prepared { filmReady.fulfill() }
            if event.kind == .played { played.append(event.attempt); filmPlayed.fulfill() }
        }
        defer { controller.stop(); music.cancel(); film.cancel() }
        let old = controller.playAttempt(resource: try .init(id: "music", url: file, title: "Music"), owner: "music")
        _ = controller.setVolume(0, attempt: old)
        await fulfillment(of: [musicPlayed], timeout: 5)
        postInterruption(.began, to: notifications)
        XCTAssertEqual(controller.snapshot.phase, .paused)
        XCTAssertEqual(controller.player.rate, 0)

        let next = controller.playAttempt(resource: try .init(id: "film",
            url: URL(string: "https://media.invalid/scoped.m3u8")!, title: "Film",
            headers: ["X-Media-Test": "synthetic"]), owner: "film")
        _ = controller.setVolume(0, attempt: next)
        XCTAssertTrue(startup.isWaiting)
        XCTAssertNil(controller.player.currentItem, "The interruption end must arrive before installing the replacement item")
        if endedBeforeReady { postInterruption(.ended, shouldResume: true, to: notifications) }
        try startup.complete(with: file)
        await fulfillment(of: [filmReady], timeout: 5)
        XCTAssertEqual(controller.player.rate, 0)
        XCTAssertEqual(played, [old], "The new owner cannot inherit the old owner's automatic-resume permission")
        XCTAssertFalse(controller.systemCommand(.play, generation: old.id))
        XCTAssertTrue(controller.systemCommand(.play, generation: next.id))
        if !endedBeforeReady {
            XCTAssertEqual(controller.player.rate, 0, "Explicit play cannot bypass an interruption that has not ended")
            XCTAssertEqual(played, [old])
            postInterruption(.ended, shouldResume: true, to: notifications)
        }
        await fulfillment(of: [filmPlayed], timeout: 5)
        XCTAssertEqual(controller.currentAttempt, next)
        XCTAssertEqual(controller.snapshot.phase, .playing)
        XCTAssertGreaterThan(controller.player.rate, 0)
        XCTAssertEqual(played, [old, next])
    }

    func testOutputUnavailablePausesImmediatelyAndNotifiesOwnerEvenWhenAlreadyPaused() async throws {
        let file = try silentFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let notifications = NotificationCenter()
        let controller = PlaybackController(systemNotifications: notifications)
        let playing = expectation(description: "Output starts")
        var outputs = 0
        var wasPausedBeforeDelivery: [Bool] = []
        let lease = try controller.bind(owner: "music", onPlayPause: { _, _ in
            XCTFail("Output loss must not fabricate a play/pause command"); return false
        }, onOutputUnavailable: {
            outputs += 1
            wasPausedBeforeDelivery.append(controller.player.rate == 0)
        }) { event in if event.kind == .played { playing.fulfill() } }
        defer { controller.stop(); lease.cancel() }
        let attempt = controller.playAttempt(resource: try .init(id: "output", url: file, title: "Output"), owner: "music")
        _ = controller.setVolume(0, attempt: attempt)
        await fulfillment(of: [playing], timeout: 5)

        notifications.post(name: AVAudioSession.routeChangeNotification, object: AVAudioSession.sharedInstance(),
            userInfo: [AVAudioSessionRouteChangeReasonKey: NSNumber(value: AVAudioSession.RouteChangeReason.newDeviceAvailable.rawValue)])
        XCTAssertEqual(outputs, 0)
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(controller.player.rate, 0)
        XCTAssertEqual(controller.snapshot.phase, .paused)
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(outputs, 2, "An already-paused item may still own pending business navigation")
        XCTAssertEqual(wasPausedBeforeDelivery, [true, true])
        controller.stop()
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(outputs, 3, "An owner lease still covers source work after its native attempt stops")
        lease.cancel()
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(outputs, 3, "The final lease cancellation releases output delivery")
    }

    func testOutputSubscriptionRejectsDisabledCancelledAndReplacedLeasesWithoutAttempt() async throws {
        let notifications = NotificationCenter()
        var controller: PlaybackController? = PlaybackController(systemNotifications: notifications)
        weak var released = controller
        var oldCalls = 0, currentCalls = 0
        let old = try XCTUnwrap(controller).bind(owner: "music", onOutputUnavailable: { oldCalls += 1 }) { _ in
            XCTFail("Output subscription alone must not produce a native event")
        }
        XCTAssertNil(controller?.currentAttempt)
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(oldCalls, 1)
        old.setEnabled(false)
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(oldCalls, 1)
        old.setEnabled(true)
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(oldCalls, 2)
        let current = try XCTUnwrap(controller).bind(owner: "music", onOutputUnavailable: { currentCalls += 1 }) { _ in }
        old.cancel()
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(oldCalls, 2)
        XCTAssertEqual(currentCalls, 1)
        current.cancel()
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(currentCalls, 1)
        let final = try XCTUnwrap(controller).bind(owner: "music", onOutputUnavailable: { currentCalls += 1 }) { _ in }
        controller = nil
        XCTAssertNil(released)
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(currentCalls, 1)
        final.cancel()
    }

    func testOutputObservationIsPassiveAndEndsAfterSessionAndSubscriptionRelease() async {
        let notifications = NotificationCenter(), targets = SystemRemoteTargetRecorder()
        let before = MPNowPlayingInfoCenter.default().nowPlayingInfo as NSDictionary?
        var outputs = 0
        let session = PlaybackSystemSession(audioActions: .live,
            onEvent: { _, _ in XCTFail("No attempt-scoped notification was posted") },
            onCommand: { _, _, _ in true }, onNavigation: { _ in true },
            onOutputUnavailable: { outputs += 1 }, notificationCenter: notifications,
            registerCommandTarget: targets.register)
        defer { session.invalidate() }
        session.updateOutputSubscription(true)
        XCTAssertTrue(targets.registered.isEmpty, "An environment subscription cannot install transport commands")
        XCTAssertEqual(MPNowPlayingInfoCenter.default().nowPlayingInfo as NSDictionary?, before)
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(outputs, 1)
        session.endSession()
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(outputs, 2)
        session.updateOutputSubscription(false)
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(outputs, 2)
        session.beginSession(generation: UUID())
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(outputs, 3, "Unbound native transport still observes output loss")
        session.endSession()
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(outputs, 3)
        session.updateOutputSubscription(true)
        session.invalidate()
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(outputs, 3)
    }

    func testOutputUnavailableDuringIdleSourceResolutionRejectsLatePlaybackAndAllowsExplicitPlay() async throws {
        try await outputUnavailableDuringSourceWait(preparedMedia: false)
    }

    func testOutputUnavailableDuringPreparedMediaWaitRejectsLatePlaybackAndAllowsExplicitPlay() async throws {
        try await outputUnavailableDuringSourceWait(preparedMedia: true)
    }

    private func outputUnavailableDuringSourceWait(preparedMedia: Bool) async throws {
        let file = try silentFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let notifications = NotificationCenter()
        let controller = PlaybackController(systemNotifications: notifications)
        let service = SystemOutputMusicService(file: file)
        let model = MusicViewModel(service: service, playback: controller)
        let firstPlayed = expectation(description: "Original music is playing")
        let newPlayed = expectation(description: "New explicit music playback")
        var played: [PlayAttempt] = []
        let lease = try controller.bind(owner: "music", onPlayPause: { [weak model] attempt, command in
            model?.acceptPlayPause(command, attempt: attempt) ?? false
        }, onOutputUnavailable: { [weak model] in model?.onOutputUnavailable() }) { [weak model] event in
            model?.onPlaybackEvent(event)
            if event.kind == .played {
                played.append(event.attempt)
                if played.count == 1 { firstPlayed.fulfill() }
                else { newPlayed.fulfill() }
            }
        }
        defer { lease.cancel(); controller.stop() }
        await model.load()
        await model.prepareCurrent()
        let original = try XCTUnwrap(controller.currentAttempt)
        _ = controller.setVolume(0, attempt: original)
        await fulfillment(of: [firstPlayed], timeout: 5)
        let entered = expectation(description: preparedMedia ? "Prepared media waiting for activation" : "Committed next waiting for source")
        let gate = SystemOutputWait { entered.fulfill() }
        defer { Task { await gate.release() } }
        if preparedMedia {
            model.stopForMediaHandoff()
            await service.holdActivation(gate)
        } else { await service.holdResolution(gate) }
        let request = Task {
            if preparedMedia { await model.switchQuality("128k") }
            else { await model.next() }
        }
        await fulfillment(of: [entered], timeout: 5)
        XCTAssertNil(controller.currentAttempt)
        XCTAssertNil(model.activeNativeAttemptID)
        XCTAssertEqual(model.phase, .idle)
        XCTAssertNotNil(model.pendingSourceSelection)
        let queue = model.queue, current = model.current?.id
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(model.phase, .paused)
        XCTAssertNil(model.pendingSourceSelection)
        await gate.release()
        await request.value
        await model.flushPlaybackFacts()
        XCTAssertNil(controller.currentAttempt, "The old source receipt cannot commit a native attempt")
        XCTAssertEqual(controller.player.rate, 0)
        XCTAssertEqual(played, [original])
        XCTAssertEqual(model.queue, queue)
        XCTAssertEqual(model.current?.id, current)
        XCTAssertEqual(model.phase, .paused)
        XCTAssertNil(model.error)
        await model.togglePlayback()
        await fulfillment(of: [newPlayed], timeout: 5)
        XCTAssertEqual(played.count, 2)
        XCTAssertNotEqual(controller.currentAttempt?.id, original.id)
        XCTAssertEqual(model.current?.id, current)
        XCTAssertEqual(model.phase, .playing)
        XCTAssertNil(model.error)
        await model.close()
    }

    func testInterruptionPauseDoesNotBecomeOwnerPauseAndResetClearsPendingPlayback() async throws {
        let notifications = NotificationCenter(), startup = HeldSystemHLSStartup()
        let controller = PlaybackController(systemNotifications: notifications, startScopedHLS: startup.start)
        var ownerPauses = 0
        let lease = try controller.bind(owner: "music", onPlayPause: { _, _ in ownerPauses += 1; return true }) { _ in }
        defer { controller.stop(); lease.cancel() }
        let attempt = controller.playAttempt(resource: try .init(id: "pending",
            url: URL(string: "https://media.invalid/pending.m3u8")!, title: "Pending",
            headers: ["X-Media-Test": "synthetic"]), owner: "music")
        postInterruption(.began, to: notifications)
        XCTAssertEqual(controller.snapshot.phase, .paused)
        XCTAssertEqual(ownerPauses, 0)
        notifications.post(name: AVAudioSession.mediaServicesWereResetNotification, object: AVAudioSession.sharedInstance())
        XCTAssertEqual(controller.snapshot.phase, .failed)
        XCTAssertEqual(controller.lastError as? NativePlaybackError, .mediaServicesReset)
        XCTAssertNil(controller.player.currentItem)
        XCTAssertFalse(controller.systemCommand(.play, generation: attempt.id))
        postInterruption(.ended, shouldResume: true, to: notifications)
        postOutputUnavailable(to: notifications)
        XCTAssertEqual(ownerPauses, 0)
        XCTAssertEqual(controller.snapshot.phase, .failed)
        controller.stop()
        XCTAssertNil(controller.currentAttempt)
    }

    func testStandardCommandTargetsFollowCapabilitiesAndAreRemovedOnReplacementAndEnd() async throws {
        let targets = SystemRemoteTargetRecorder()
        let session = PlaybackSystemSession(audioActions: .live, onEvent: { _, _ in },
            onCommand: { _, _, _ in true }, onNavigation: { _ in true },
            notificationCenter: NotificationCenter(), registerCommandTarget: targets.register)
        defer { session.endSession() }
        let center = MPRemoteCommandCenter.shared()
        let transport: [MPRemoteCommand] = [center.playCommand, center.pauseCommand, center.togglePlayPauseCommand,
                                            center.stopCommand, center.changePlaybackPositionCommand]
        let music = PlayAttempt(id: UUID(), owner: "music", resourceID: "song")
        let binding = UUID(), revision = UUID()
        let navigation = PlaybackNavigationRoutes(
            previous: .init(attempt: music, bindingID: binding, revision: revision, direction: .previous),
            next: .init(attempt: music, bindingID: binding, revision: revision, direction: .next))
        let playPause = PlaybackPlayPauseRoute(attempt: music, bindingID: binding, revision: revision)
        session.beginSession(generation: music.id)
        XCTAssertEqual(targets.activeCommands, Set(transport.map(ObjectIdentifier.init)))
        XCTAssertFalse(center.previousTrackCommand.isEnabled)
        XCTAssertFalse(center.nextTrackCommand.isEnabled)
        XCTAssertFalse(center.changePlaybackPositionCommand.isEnabled)
        session.update(snapshot: .init(owner: "music", resourceID: "song", phase: .paused),
            attempt: music, metadata: nil, rate: 0, canSeek: true, navigation: navigation, playPause: playPause)
        XCTAssertEqual(targets.activeCommands,
            Set((transport + [center.previousTrackCommand, center.nextTrackCommand]).map(ObjectIdentifier.init)))
        XCTAssertTrue(center.previousTrackCommand.isEnabled)
        XCTAssertTrue(center.nextTrackCommand.isEnabled)
        XCTAssertTrue(center.changePlaybackPositionCommand.isEnabled)
        let musicTargets = targets.activeIDs

        let film = PlayAttempt(id: UUID(), owner: "film", resourceID: "episode")
        session.beginSession(generation: film.id)
        XCTAssertTrue(musicTargets.isSubset(of: Set(targets.removed)))
        XCTAssertEqual(targets.activeCommands, Set(transport.map(ObjectIdentifier.init)))
        session.updateNavigation(navigation)
        session.updatePlayPause(playPause)
        XCTAssertEqual(targets.activeCommands, Set(transport.map(ObjectIdentifier.init)), "Foreign routes cannot install targets")
        XCTAssertFalse(center.previousTrackCommand.isEnabled)
        XCTAssertFalse(center.nextTrackCommand.isEnabled)
        XCTAssertFalse(center.changePlaybackPositionCommand.isEnabled)
        session.endSession()
        XCTAssertTrue(targets.activeIDs.isEmpty)
        XCTAssertEqual(targets.removed.count, targets.registered.count)
        XCTAssertEqual(Set(targets.removed).count, targets.removed.count)
        for command in transport + [center.previousTrackCommand, center.nextTrackCommand] {
            XCTAssertFalse(command.isEnabled)
        }
        let removals = targets.removed
        session.endSession()
        XCTAssertEqual(targets.removed, removals)
    }

    private func postInterruption(_ type: AVAudioSession.InterruptionType, shouldResume: Bool = false,
                                  to center: NotificationCenter) {
        center.post(name: AVAudioSession.interruptionNotification, object: AVAudioSession.sharedInstance(), userInfo: [
            AVAudioSessionInterruptionTypeKey: NSNumber(value: type.rawValue),
            AVAudioSessionInterruptionOptionKey: NSNumber(value: shouldResume ? AVAudioSession.InterruptionOptions.shouldResume.rawValue : 0)
        ])
    }

    private func postOutputUnavailable(to center: NotificationCenter) {
        center.post(name: AVAudioSession.routeChangeNotification, object: AVAudioSession.sharedInstance(), userInfo: [
            AVAudioSessionRouteChangeReasonKey: NSNumber(value: AVAudioSession.RouteChangeReason.oldDeviceUnavailable.rawValue)
        ])
    }

    private func silentFile() throws -> URL {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".wav")
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
        data.append(Data(count: Int(length))); try data.write(to: file)
        return file
    }
}

@MainActor
private final class HeldSystemHLSStartup {
    private var completion: (@Sendable (Result<URL, PlaybackNetworkError>) -> Void)?
    var isWaiting: Bool { completion != nil }
    func start(_ route: HLSLoopbackRoute, completion: @escaping @Sendable (Result<URL, PlaybackNetworkError>) -> Void) {
        self.completion = completion
    }
    func complete(with file: URL) throws {
        let callback = try XCTUnwrap(completion)
        completion = nil
        callback(.success(file))
    }
}

@MainActor
private final class SystemRemoteTargetRecorder {
    private(set) var registered: [MPRemoteCommand] = []
    private(set) var removed: [Int] = []
    var activeIDs: Set<Int> { Set(registered.indices).subtracting(removed) }
    var activeCommands: Set<ObjectIdentifier> { Set(activeIDs.map { ObjectIdentifier(registered[$0]) }) }
    func register(_ command: MPRemoteCommand, handler: @escaping PlaybackSystemSession.CommandHandler) -> (@MainActor () -> Void) {
        let id = registered.count
        registered.append(command)
        let target = command.addTarget(handler: handler)
        return { [self] in
            command.removeTarget(target)
            removed.append(id)
        }
    }
}

private actor SystemOutputWait {
    private let entered: @Sendable () -> Void
    private var continuation: CheckedContinuation<Void, Never>?
    private var released = false
    init(_ entered: @escaping @Sendable () -> Void) { self.entered = entered }
    func wait() async {
        guard !released else { return }
        await withCheckedContinuation { continuation = $0; entered() }
    }
    func release() { released = true; continuation?.resume(); continuation = nil }
}

/// Real controller/source admission with only the source receipts held. The
/// local media and player preparation remain AVFoundation, not a fake player.
private actor SystemOutputMusicService: MusicServing, MusicSourcePlaybackServing {
    let file: URL
    private let entries: [MusicQueueEntry] = ["first", "second"].map {
        .init(id: $0, track: .init(ref: .init(kind: .queue, id: $0), songId: $0, title: $0,
            duration: 30, sources: [.init(sourceKey: "synthetic", platform: "wy", nativeId: $0)]))
    }
    private var index = 0, revision = 1
    private var position = 0.0
    private var resolution: SystemOutputWait?, activation: SystemOutputWait?
    init(file: URL) { self.file = file }
    func holdResolution(_ gate: SystemOutputWait) { resolution = gate }
    func holdActivation(_ gate: SystemOutputWait) { activation = gate }
    func state() -> MusicPlaybackState {
        .init(revision: revision, mode: .loop, currentId: entries[index].id, total: entries.count,
            position: position, canNext: index == 0, canPrevious: index == 1)
    }
    func queue() -> MusicQueueSnapshot { .init(revision: revision, currentId: entries[index].id, entries: entries) }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) throws -> MusicAdvanceResult {
        guard expectedRevision == revision else { throw MusicServiceError.staleRevision }
        index = direction == .next ? 1 : 0; revision += 1; position = 0
        return .init(state: state(), selectedId: entries[index].id)
    }
    func sourceSession(occurrenceID: String, expectedQueueRevision: Int) throws -> any MusicSourcePlaybackSession {
        guard occurrenceID == entries[index].id, expectedQueueRevision == revision else { throw MusicServiceError.staleResult }
        return SystemOutputMusicSession(service: self, occurrence: occurrenceID)
    }
    func preparedSource(_ occurrence: String) async -> MusicPreparedSource {
        let gate = resolution; resolution = nil
        await gate?.wait() // Intentionally returns a receipt even after cancellation.
        let selection = MusicPlaybackSelection(installationId: "official:wy", generation: 1,
            siteId: "wy", sourceKey: "synthetic", quality: "128k")
        let id = UUID().uuidString
        return .init(candidate: .init(attemptId: id, occurrenceId: occurrence, resourceId: "music:" + id,
            url: file, title: occurrence, startPosition: position, requestedQuality: "128k",
            resolvedSelection: selection, delivery: .init(quality: "128k", evidence: .officialResponse, duration: 30)),
            selection: selection)
    }
    func activateSource(attemptID: String) async throws {
        let gate = activation; activation = nil
        await gate?.wait()
    }
    func discardSource(attemptID: String) {}
    func sourceConfigurationChanged() {}
    func inspectSources(for reference: MusicReference) throws -> any MusicSourceInspectionSession { throw MusicServiceError.unsupported }
    func sourceAvailability(for ref: MusicReference) -> MusicSourceAvailability { .init(options: [], scriptState: .notConfigured) }
    func platforms() -> [MusicPlatform] { [] }
    func search(platform: String, query: String, page: Int, limit: Int) throws -> MusicSearchPage { throw MusicServiceError.unsupported }
    func collections() -> [MusicCollection] { [] }
    func tracks(in collection: MusicCollection) -> [MusicRemoteTrack] { [] }
    func createPlaylist(name: String, refs: [MusicReference]) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func removePlaylist(_ collection: MusicCollection) throws { throw MusicServiceError.unsupported }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func updateSettings(_ change: MusicSettingsChange) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func metadataPreferences() throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
    func updateMetadataPreferences(_ change: MusicMetadataPreferencesChange) throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
    func metadataCandidates(occurrenceID: String) throws -> MusicMetadataCandidates { throw MusicServiceError.unsupported }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) throws -> MusicPlaybackCandidate { throw MusicServiceError.unsupported }
    func cancel(attemptId: String?) {}
    func record(_ fact: MusicPlaybackFact) -> Bool { position = fact.position; return true }
    func lyrics(occurrenceId: String, sourceKey: String) -> MusicLyricsSnapshot { .init(occurrenceId: occurrenceId, state: .none, lines: []) }
    func playedSongs() -> [MusicPlayedSong] { [] }
    func playedPlaylists() -> [MusicPlayedPlaylist] { [] }
    func restoreHistory(id: String, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
}

private actor SystemOutputMusicSession: MusicSourcePlaybackSession {
    let service: SystemOutputMusicService
    let occurrence: String
    init(service: SystemOutputMusicService, occurrence: String) { self.service = service; self.occurrence = occurrence }
    func match(intent: MusicSourceIntent) async throws -> MusicPreparedSource { await service.preparedSource(occurrence) }
    func match(quality: String?, sourceID: String?) async throws -> MusicPreparedSource { await service.preparedSource(occurrence) }
    func beginSelection() {}
    func rejectMedia(candidate: MusicPlaybackCandidate, selection: MusicPlaybackSelection) {}
    func cancelInspection() {}
    func updates() -> AsyncThrowingStream<MusicResolvedSourcesSnapshot, Error> { .init { $0.finish() } }
    func loadMore(refresh: Bool) throws -> MusicResolvedSourcesSnapshot { throw MusicServiceError.unsupported }
    func adopt(sourceID: String, occurrenceID: String, expectedQueueRevision: Int) throws -> MusicPlaybackSelection { throw MusicServiceError.unsupported }
    func reject(sourceKey: String, occurrenceID: String, expectedQueueRevision: Int) throws { throw MusicServiceError.unsupported }
    func close() {}
}
#endif
