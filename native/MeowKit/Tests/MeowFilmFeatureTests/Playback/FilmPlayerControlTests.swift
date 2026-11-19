import XCTest
import Combine
import MeowContracts
import MeowRuntime
import MeowStorageTestSupport
@testable import MeowFilmFeature
#if os(iOS)
import UIKit
#endif

@MainActor
final class FilmPlayerControlTests: XCTestCase {
    func testSameOrUnconsumedPreferencesDoNotCancelTheActiveHold() async throws {
        let store = makeStore()
        let (model, media, _) = try await open(store: store)
        let token = try XCTUnwrap(model.beginTemporaryRate())
        let commands = media.temporaryCommands.count
        await store.reload()
        storageTestTrue(await store.set(\.holdRate, store.preferences.holdRate))
        storageTestTrue(await store.set(\.showsControlCenter, false))
        storageTestTrue(await store.set(\.defaultRate, 1.25))
        XCTAssertEqual(media.temporaryCommands.count, commands)
        XCTAssertEqual(model.mediaState.rate, 2)
        storageTestTrue(await store.set(\.holdRate, 3))
        XCTAssertNil(media.temporaryCommands.last?.rate)
        let after = media.temporaryCommands.count
        model.endTemporaryRate(token)
        XCTAssertEqual(media.temporaryCommands.count, after)
        await model.close()
    }

    func testTemporaryRateDoesNotReplaceManualBaseOrResumePausedOutput() async throws {
        let (model, media, _) = try await open()
        model.setRate(1.5)
        model.pauseOrResume()
        let token = try XCTUnwrap(model.beginTemporaryRate())
        XCTAssertEqual(model.mediaState.rate, 2)
        XCTAssertTrue(model.mediaState.paused)
        model.endTemporaryRate(token)
        XCTAssertEqual(model.mediaState.rate, 1.5)
        XCTAssertTrue(model.mediaState.paused)
        XCTAssertEqual(media.base.resumes, 0)
        XCTAssertEqual(media.temporaryCommands.count, 2)
        XCTAssertNil(media.temporaryCommands.last?.rate)
        await model.close()
    }

    func testActualEngineFiltersMenuAndClampsHoldRegardlessOfPreferredEngine() async throws {
        let store = makeStore()
        storageTestTrue(await store.set(\.holdRate, 8))
        let (model, media, _) = try await open(engine: .aether, store: store)
        XCTAssertEqual(model.playerPreferences.engine, .ksPlayer)
        XCTAssertEqual(model.actualEngine, .aether)
        XCTAssertEqual(model.availableRates, [0.5, 0.75, 1, 1.25, 1.5, 2])
        model.setRate(3)
        XCTAssertEqual(model.mediaState.rate, 2)
        let token = try XCTUnwrap(model.beginTemporaryRate())
        XCTAssertEqual(media.temporaryCommands.last?.rate, 2)
        model.endTemporaryRate(token)
        XCTAssertEqual(model.mediaState.rate, 2)
        await model.close()
    }

    func testOldHoldCompletionCannotRestoreNewAttemptOrEndANewerHold() async throws {
        let (model, media, server) = try await open()
        let old = try XCTUnwrap(model.beginTemporaryRate())
        try await server.publish(FilmPlaybackFixture.snapshot(20, stage: "pending",
            attempt: FilmPlaybackFixture.attempt("replacement"),
            current: FilmPlaybackFixture.current("controls")))
        await model.waitForPendingWork()
        XCTAssertEqual(model.mediaState.attemptID, "replacement")
        let commands = media.temporaryCommands.count
        model.endTemporaryRate(old)
        XCTAssertEqual(media.temporaryCommands.count, commands)
        let newer = try XCTUnwrap(model.beginTemporaryRate())
        model.endTemporaryRate(old)
        XCTAssertEqual(model.mediaState.rate, 2)
        model.endTemporaryRate(newer)
        XCTAssertEqual(model.mediaState.rate, 1)
        await model.close()
    }

    func testManualRateCancelsHeldRateBeforeChangingBaseAndOldEndIsIgnored() async throws {
        let (model, media, _) = try await open()
        let token = try XCTUnwrap(model.beginTemporaryRate())
        model.setRate(1.25)
        XCTAssertNil(media.temporaryCommands.last?.rate)
        XCTAssertEqual(model.mediaState.rate, 1.25)
        let count = media.temporaryCommands.count
        model.endTemporaryRate(token)
        XCTAssertEqual(media.temporaryCommands.count, count)
        XCTAssertEqual(model.mediaState.rate, 1.25)
        await model.close()
    }

    func testHiddenPreferencesChangeAndBackgroundReturnHeldRate() async throws {
        let store = makeStore()
        let (model, media, _) = try await open(store: store)
        model.setRate(1.5)
        let owner = UUID()
        model.setPresentationVisible(true, owner: owner)
        _ = model.beginTemporaryRate()
        model.setPresentationVisible(false, owner: owner)
        XCTAssertEqual(model.mediaState.rate, 1.5)
        _ = model.beginTemporaryRate()
        storageTestTrue(await store.set(\.seekStep, 30))
        XCTAssertEqual(model.mediaState.rate, 1.5)
        _ = model.beginTemporaryRate()
        media.base.systemOwner = true
        await model.applicationDidEnterBackground()
        XCTAssertEqual(model.mediaState.rate, 1.5)
        XCTAssertTrue(model.hasSystemOwner)
        await model.close()
    }

    func testStepClampsAtEdgesAndRejectsNonFiniteControlValues() async throws {
        let store = makeStore()
        storageTestTrue(await store.set(\.seekStep, 30))
        let (model, media, _) = try await open(store: store)
        media.move(to: 590)
        model.seekByStep(forward: true)
        media.move(to: 5)
        model.seekByStep(forward: false)
        XCTAssertEqual(media.base.seeks, [600, 0])
        model.seek(to: .nan); model.setRate(.infinity); model.setVolume(.nan)
        XCTAssertEqual(media.base.seeks, [600, 0])
        XCTAssertTrue(media.base.rates.isEmpty)
        XCTAssertTrue(media.base.volumes.isEmpty)
        await model.close()
        XCTAssertNil(model.beginTemporaryRate())
    }

    func testChapterRecognitionOnlyReceivesCommittedReceiptAndSkipUsesSeek() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("chapters")))
        let media = FilmPlayerControlMedia()
        media.chapters = [.init(title: "Opening", start: 0, end: 45)]
        let entered = FilmPlaybackGate(), output = FilmPlaybackGate()
        media.base.preparationEntered = entered; media.base.preparationGate = output
        let chapterCalls = FilmPlayerChapterCalls()
        let transport = FilmPlaybackTransport(request: { operation, input in
            if input.filmObject?["action"] == .string("media.chapters") {
                await chapterCalls.append(input)
                return .object(["segments": .array([.object([
                    "kind": .string("intro"), "start": .number(0), "end": .number(45),
                ])])])
            }
            return try await server.call(operation, input)
        }, observation: { id, after, receive in
            try await server.transport.snapshots(sessionID: id, after: after, receive: receive)
        })
        let model = FilmPlaybackModel(transport: transport, media: media, playerPreferences: makeStore(),
                                     request: { FilmPlaybackFixture.request() })
        let recognized = expectation(description: "Committed chapter segments published")
        var didRecognize = false
        let observation = model.objectWillChange.receive(on: RunLoop.main).sink {
            if !didRecognize, model.skipSegment != nil { didRecognize = true; recognized.fulfill() }
        }
        await model.open()?.value
        await entered.wait()
        let beforeCommit = await chapterCalls.inputs()
        XCTAssertTrue(beforeCommit.isEmpty)
        await output.release()
        await fulfillment(of: [recognized], timeout: 2)
        observation.cancel()
        let inputs = await chapterCalls.inputs()
        XCTAssertEqual(inputs.count, 1)
        XCTAssertEqual(inputs.first?.filmObject?["attemptId"], .string("chapters"))
        XCTAssertEqual(inputs.first?.filmObject?["resourceId"], .string("resource-chapters"))
        model.skipChapter()
        XCTAssertEqual(media.base.seeks, [45])
        await model.waitForPendingWork()
        let actions = await server.calls().compactMap { $0.1.filmObject?["action"]?.filmText }
        XCTAssertFalse(actions.contains("media.ended"), "Skipping cannot invent a decoder terminal receipt")
        await model.close()
    }

    func testScreenRegionsAreEqualThirds() {
        XCTAssertEqual(FilmPlayerScreenRegion(x: 99, width: 300), .left)
        XCTAssertEqual(FilmPlayerScreenRegion(x: 100, width: 300), .center)
        XCTAssertEqual(FilmPlayerScreenRegion(x: 199, width: 300), .center)
        XCTAssertEqual(FilmPlayerScreenRegion(x: 200, width: 300), .right)
    }

    func testPanSeekIsPreviewOnlyAndCompletionIsOneShot() {
        var pan = interaction(region: .center)
        XCTAssertEqual(pan.update(x: 25, y: 2, width: 100, height: 100), .seek(250))
        XCTAssertEqual(pan.mode, .seek)
        XCTAssertEqual(pan.finish(cancelled: false, current: pan.identity), .seek(250))
        XCTAssertNil(pan.finish(cancelled: false, current: pan.identity))
    }

    func testCancelledOrSupersededPanCannotSeek() {
        for cancelled in [true, false] {
            var pan = interaction(region: .left)
            _ = pan.update(x: 50, y: 2, width: 100, height: 100)
            let current = cancelled ? pan.identity : FilmPlayerControlIdentity(sessionID: pan.identity.sessionID,
                                                                               attemptID: "new")
            XCTAssertNil(pan.finish(cancelled: cancelled, current: current))
        }
    }

    func testDisabledAxisLocksWithoutTurningIntoAnotherOperation() {
        var preferences = FilmPlayerPreferences()
        preferences.swipeSeek = false
        var pan = interaction(region: .right, preferences: preferences)
        XCTAssertNil(pan.update(x: 20, y: 1, width: 100, height: 100))
        XCTAssertEqual(pan.mode, FilmPlayerPanInteraction.Mode.none)
        XCTAssertNil(pan.update(x: 20, y: -80, width: 100, height: 100))
        XCTAssertNil(pan.finish(cancelled: false, current: pan.identity))
    }

    func testVerticalBrightnessAndVolumeDoNotDriftIntoSeek() {
        for region in [FilmPlayerScreenRegion.left, .right] {
            var pan = interaction(region: region)
            let first: FilmPlayerPanInteraction.Change = region == .left ? .brightness(0.8) : .volume(0.8)
            XCTAssertEqual(pan.update(x: 1, y: -30, width: 100, height: 100), first)
            let second: FilmPlayerPanInteraction.Change = region == .left ? .brightness(1) : .volume(1)
            XCTAssertEqual(pan.update(x: 90, y: -80, width: 100, height: 100), second)
            XCTAssertNil(pan.finish(cancelled: false, current: pan.identity))
        }
    }

    func testShortVideoEpisodeSwipeRequiresBothModeAndPreferenceAndOnlyCommitsOnEnd() {
        for mode in [false, true] {
            for enabled in [false, true] {
                var preferences = FilmPlayerPreferences(); preferences.swipeEpisodes = enabled
                var pan = interaction(region: .center, preferences: preferences, shortVideo: mode)
                XCTAssertNil(pan.update(x: 0, y: -60, width: 100, height: 100))
                XCTAssertEqual(pan.finish(cancelled: false, current: pan.identity),
                               mode && enabled ? .episode(next: true) : nil)
                XCTAssertNil(pan.finish(cancelled: false, current: pan.identity))
            }
        }
        var shortSwipe = interaction(region: .center, shortVideo: true)
        _ = shortSwipe.update(x: 0, y: 15, width: 100, height: 100)
        XCTAssertNil(shortSwipe.finish(cancelled: false, current: shortSwipe.identity))
    }

    private func interaction(region: FilmPlayerScreenRegion, preferences: FilmPlayerPreferences = .init(),
                             shortVideo: Bool = false) -> FilmPlayerPanInteraction {
        .init(identity: .init(sessionID: UUID(), attemptID: "pan"), preferences: preferences,
              region: region, shortVideo: shortVideo, initialPosition: 100, duration: 600,
              initialVolume: 0.5, initialBrightness: 0.5)
    }
    private func makeStore() -> FilmPlayerPreferencesStore {
        FilmPlayerPreferencesStore()
    }
    private func open(engine: PlaybackVideoEngine = .ksPlayer, store: FilmPlayerPreferencesStore? = nil) async throws
        -> (FilmPlaybackModel, FilmPlayerControlMedia, FilmPlaybackWireServer) {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("controls")))
        let media = FilmPlayerControlMedia(); media.engine = engine
        let model = FilmPlaybackModel(transport: server.transport, media: media, playerPreferences: store ?? makeStore(),
                                     request: { FilmPlaybackFixture.request() })
        addTeardownBlock { await model.close() }
        await model.open()?.value
        await model.waitForPendingWork()
        return (model, media, server)
    }
}

private actor FilmPlayerChapterCalls {
    private var values: [RuntimeValue] = []
    func append(_ value: RuntimeValue) { values.append(value) }
    func inputs() -> [RuntimeValue] { values }
}

/// Exercises the control contract, not the decoder's real wall-clock speed.
@MainActor
private final class FilmPlayerControlMedia: FilmPlaybackMediaControlling {
    struct TemporaryCommand {
        let attemptID: String
        let rate: Double?
    }
    let base = FilmPlaybackTestMedia()
    var engine: PlaybackVideoEngine = .ksPlayer
    var chapters: [PlaybackVideoChapter] = []
    private var state = FilmMediaState()
    private var baseRate: Double = 1
    private(set) var temporaryCommands: [TemporaryCommand] = []

    func play(_ input: FilmPlaybackMediaInput, preservePosition: Bool,
              waitForCommit: @escaping @MainActor () async throws -> Void,
              canCommit: @escaping @MainActor () -> Bool,
              release: @escaping @MainActor () async throws -> Void,
              receive: @escaping @MainActor (FilmMediaEvent) -> Void) async throws {
        try await base.play(input, preservePosition: preservePosition, waitForCommit: waitForCommit,
                            canCommit: canCommit, release: release) { [self] event in
            if case .state(var value) = event {
                if value.attemptID != state.attemptID { baseRate = 1 }
                value.engine = engine; value.chapters = chapters; value.resourceID = input.resourceID
                state = value
                receive(.state(value))
            } else { receive(event) }
        }
    }
    func move(to position: Double) {
        state.position = position
        publish()
    }
    private func publish() { base.emit(.state(state), attemptID: state.attemptID!) }
    func cancelPending(sessionID: UUID) async throws { try await base.cancelPending(sessionID: sessionID) }
    func stop(sessionID: UUID) async throws { try await base.stop(sessionID: sessionID) }
    func isLocallyDrained(sessionID: UUID) -> Bool { base.isLocallyDrained(sessionID: sessionID) }
    func isCurrent(sessionID: UUID, attemptID: String) -> Bool { base.isCurrent(sessionID: sessionID, attemptID: attemptID) }
    func pause(sessionID: UUID, attemptID: String) -> Bool { base.pause(sessionID: sessionID, attemptID: attemptID) }
    func resume(sessionID: UUID, attemptID: String, userInitiated: Bool) {
        base.resume(sessionID: sessionID, attemptID: attemptID, userInitiated: userInitiated)
    }
    func seek(sessionID: UUID, to position: Double) { base.seek(sessionID: sessionID, to: position) }
    func setRate(sessionID: UUID, rate: Double) {
        base.setRate(sessionID: sessionID, rate: rate)
        baseRate = rate; state.rate = rate; publish()
    }
    func setTemporaryRate(sessionID: UUID, attemptID: String, rate: Double?) {
        guard isCurrent(sessionID: sessionID, attemptID: attemptID) else { return }
        temporaryCommands.append(.init(attemptID: attemptID, rate: rate))
        state.rate = rate ?? baseRate; publish()
    }
    func setVolume(sessionID: UUID, volume: Double) { base.setVolume(sessionID: sessionID, volume: volume) }
    func startPictureInPicture(sessionID: UUID) { base.startPictureInPicture(sessionID: sessionID) }
    func stopPictureInPicture(sessionID: UUID) { base.stopPictureInPicture(sessionID: sessionID) }
    func hasSystemOwner(sessionID: UUID) -> Bool { base.hasSystemOwner(sessionID: sessionID) }
    #if os(iOS)
    func makeVideoView(sessionID: UUID) -> UIView { base.makeVideoView(sessionID: sessionID) }
    func setVideoScale(sessionID: UUID, scale: FilmVideoScale) { base.setVideoScale(sessionID: sessionID, scale: scale) }
    #endif
}
