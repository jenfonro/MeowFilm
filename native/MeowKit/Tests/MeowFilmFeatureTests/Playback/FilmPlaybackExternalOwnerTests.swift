import Foundation
import XCTest
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class FilmPlaybackExternalOwnerTests: XCTestCase {
    func testOpeningThenBackgroundKeepsOnlyTheExactExternalReceiptUntilForeground() async throws {
        let server = makeServer(), media = FilmPlaybackTestMedia()
        let entered = FilmPlaybackGate(), result = FilmPlaybackGate()
        let model = await makeModel(server, media) { _ in
            await entered.release(); await result.wait(); return true
        }
        let handoff = model.openExternalPlayer(.infuse)
        await entered.wait()
        await model.applicationDidEnterBackground()
        try await assertExternalOnly(model, server, media)
        let backgroundCalls = await server.calls()
        XCTAssertEqual(sessionReleases(backgroundCalls), [.bool(true)])
        await result.release(); await handoff?.value
        try await assertExternalOnly(model, server, media)
        let acceptedCalls = await server.calls()
        XCTAssertEqual(sessionReleases(acceptedCalls), [.bool(true)], "The accepted result must reuse the background ACK")
        XCTAssertEqual(watchActions(acceptedCalls), watchActions(backgroundCalls),
                       "External acceptance creates no playback or history facts")
        await model.applicationDidBecomeActive()
        try await assertFinallyReleased(model, server, media)
        let calls = await server.calls()
        XCTAssertEqual(sessionReleases(calls), [.bool(true), .bool(false)])
        XCTAssertEqual(media.stopped, [FilmPlaybackFixture.sessionID])
    }

    func testAcceptedThenBackgroundReusesTheExternalOwnerWithoutKeepingPiPOrNativeOutput() async throws {
        let server = makeServer(), media = FilmPlaybackTestMedia()
        let model = await makeModel(server, media) { _ in true }
        media.systemOwner = true
        await model.openExternalPlayer(.nPlayer)?.value
        try await assertExternalOnly(model, server, media)
        let acceptedCalls = await server.calls()
        await model.applicationDidEnterBackground()
        await model.close(allowSystemOwner: true)
        try await assertExternalOnly(model, server, media)
        let backgroundCalls = await server.calls()
        XCTAssertEqual(sessionReleases(backgroundCalls), [.bool(true)])
        XCTAssertEqual(watchActions(backgroundCalls), watchActions(acceptedCalls))
        XCTAssertEqual(media.stopped, [FilmPlaybackFixture.sessionID])
        await model.close()
        try await assertFinallyReleased(model, server, media)
        let calls = await server.calls()
        XCTAssertEqual(sessionReleases(calls), [.bool(true), .bool(false)])
    }

    func testDeclinedOpeningReleasesExternalFirstAndRestoresOnlyItsOwnPause() async throws {
        for paused in [false, true] {
            let server = makeServer(), media = FilmPlaybackTestMedia()
            let model = await makeModel(server, media) { _ in false }
            if paused {
                var state = model.mediaState; state.paused = true
                media.emit(.state(state), attemptID: "a")
            }
            await model.openExternalPlayer(.infuse)?.value
            XCTAssertEqual(media.pauses, paused ? 0 : 1)
            XCTAssertEqual(media.resumes, paused ? 0 : 1)
            XCTAssertEqual(model.mediaState.paused, paused)
            XCTAssertEqual(model.actionError?.code, "EXTERNAL_PLAYER_OPEN_FAILED")
            XCTAssertFalse(model.requiresExplicitPlay); XCTAssertFalse(model.isReleased)
            XCTAssertFalse(media.isLocallyDrained(sessionID: FilmPlaybackFixture.sessionID))
            var calls = await server.calls()
            XCTAssertTrue(mediaReleases(calls).isEmpty); XCTAssertTrue(sessionReleases(calls).isEmpty)
            await model.applicationDidBecomeActive()
            XCTAssertTrue(media.stopped.isEmpty, "Foreground after a refused opening must not close the restored native owner")
            try await media.dispose(attemptID: "a")
            await model.close(); await model.close()
            calls = await server.calls()
            XCTAssertEqual(mediaReleases(calls), [releasePayload("a")])
            XCTAssertEqual(sessionReleases(calls), [.bool(false)])
            XCTAssertTrue(model.isReleased)
        }
    }

    func testForegroundBeforeLateTrueOrFalseDoesNotWaitForOrReviveTheOpening() async throws {
        for backgroundFirst in [false, true] {
            for accepted in [false, true] {
                let server = makeServer(), media = FilmPlaybackTestMedia()
                let entered = FilmPlaybackGate(), result = FilmPlaybackGate()
                let model = await makeModel(server, media) { _ in
                    await entered.release(); await result.wait(); return accepted
                }
                let handoff = model.openExternalPlayer(.infuse)
                await entered.wait()
                if backgroundFirst { await model.applicationDidEnterBackground() }
                await model.applicationDidBecomeActive()
                try await assertFinallyReleased(model, server, media)
                XCTAssertNil(model.open(), "Foreground release cannot silently autoplay")
                let before = await server.calls()
                await result.release(); await handoff?.value
                await model.waitForPendingWork()
                XCTAssertTrue(model.isReleased); XCTAssertFalse(model.hasSystemOwner)
                XCTAssertEqual(media.resumes, 0)
                let after = await server.calls()
                XCTAssertEqual(mediaReleases(after), mediaReleases(before))
                XCTAssertEqual(sessionReleases(after), backgroundFirst ? [.bool(true), .bool(false)] : [.bool(false)])
                XCTAssertEqual(watchActions(after), watchActions(before))
                XCTAssertEqual(media.stopped, [FilmPlaybackFixture.sessionID])
                await model.close()
            }
        }
    }

    func testForegroundRevokesExternalBeforeTheInFlightNativeDrainAndLateResult() async throws {
        let server = makeServer(), media = FilmPlaybackTestMedia()
        let entered = FilmPlaybackGate(), result = FilmPlaybackGate()
        let stopEntered = FilmPlaybackGate(), stopDrain = FilmPlaybackGate()
        let model = await makeModel(server, media) { _ in
            await entered.release(); await result.wait(); return true
        }
        media.stopEntered = stopEntered; media.stopGate = stopDrain
        let handoff = model.openExternalPlayer(.infuse)
        await entered.wait()
        let background = Task { await model.applicationDidEnterBackground() }
        await stopEntered.wait()
        XCTAssertFalse(model.canLeave, "An external token alone does not prove physical drain")
        let foregroundEntered = expectation(description: "Foreground revokes before waiting for native drain")
        let foreground = Task {
            foregroundEntered.fulfill()
            await model.applicationDidBecomeActive()
        }
        await fulfillment(of: [foregroundEntered], timeout: 2)
        await stopDrain.release()
        await background.value; await foreground.value
        try await assertFinallyReleased(model, server, media)
        await result.release(); await handoff?.value
        let calls = await server.calls()
        XCTAssertEqual(mediaReleases(calls), [releasePayload("a")])
        XCTAssertEqual(sessionReleases(calls), [.bool(false)])
        XCTAssertEqual(media.stopped, [FilmPlaybackFixture.sessionID])
        XCTAssertEqual(media.resumes, 0)
    }

    func testExplicitNewPlayDuringOpeningCannotBeStoppedOrResumedByTheOldSystemResult() async throws {
        for accepted in [false, true] {
            let server = makeServer(), media = FilmPlaybackTestMedia()
            let entered = FilmPlaybackGate(), result = FilmPlaybackGate()
            let model = await makeModel(server, media) { _ in
                await entered.release(); await result.wait(); return accepted
            }
            await server.awaitObservation()
            let handoff = model.openExternalPlayer(.infuse)
            await entered.wait()
            await server.reply(to: "play", with: FilmPlaybackFixture.snapshot(100, stage: "pending",
                attempt: FilmPlaybackFixture.attempt("b"), current: FilmPlaybackFixture.current("a")))
            await model.send("play")?.value
            await model.waitForPendingWork()
            XCTAssertEqual(model.mediaState.attemptID, "b")
            try await media.dispose(attemptID: "a")
            await model.waitForPendingWork()
            let resumes = media.resumes
            await result.release(); await handoff?.value
            XCTAssertEqual(model.mediaState.attemptID, "b"); XCTAssertEqual(media.resumes, resumes)
            XCTAssertTrue(media.stopped.isEmpty); XCTAssertNil(model.actionError)
            let calls = await server.calls()
            XCTAssertEqual(mediaReleases(calls), [releasePayload("a")])
            XCTAssertTrue(sessionReleases(calls).isEmpty)
            await model.close()
        }
    }

    func testExplicitStopDuringOpeningExpiresTheReceiptWithoutLateOwnerResurrection() async throws {
        for accepted in [false, true] {
            let server = makeServer(), media = FilmPlaybackTestMedia()
            let entered = FilmPlaybackGate(), result = FilmPlaybackGate()
            let model = await makeModel(server, media) { _ in
                await entered.release(); await result.wait(); return accepted
            }
            let handoff = model.openExternalPlayer(.infuse)
            await entered.wait()
            await model.send("stop")?.value
            await model.waitForPendingWork()
            try await assertReceiptExpired(server)
            XCTAssertTrue(media.isLocallyDrained(sessionID: FilmPlaybackFixture.sessionID))
            let before = await server.calls()
            await result.release(); await handoff?.value
            let after = await server.calls()
            XCTAssertEqual(mediaReleases(after), [releasePayload("a")])
            XCTAssertEqual(sessionReleases(after), sessionReleases(before))
            XCTAssertEqual(media.resumes, 0); XCTAssertNil(model.mediaState.attemptID)
            XCTAssertEqual(media.stopped, [FilmPlaybackFixture.sessionID])
            await model.close()
        }
    }

    func testExactCandidateAndEpisodeSelectionsReopenAfterHandoffWithoutFallingBackToPlay() async throws {
        let selections: [(String, RuntimeObject)] = [
            ("select.candidate", ["candidateId": .string("exact-b")]),
            ("select.episode", ["episode": .object(["season": .number(2), "episode": .number(3)])]),
        ]
        for (action, fields) in selections {
            let server = makeServer(), media = FilmPlaybackTestMedia()
            let model = await makeModel(server, media) { _ in true }
            await model.openExternalPlayer(.infuse)?.value
            await server.reply(to: action, with: FilmPlaybackFixture.snapshot(100, stage: "pending",
                attempt: FilmPlaybackFixture.attempt("b")))
            await model.send(action, parameters: fields)?.value
            await model.waitForPendingWork()
            XCTAssertEqual(model.mediaState.attemptID, "b")
            XCTAssertFalse(model.requiresExplicitPlay); XCTAssertNil(model.actionError)
            XCTAssertEqual(media.stopped, [FilmPlaybackFixture.sessionID])
            let calls = await server.calls()
            let selections = calls.filter { $0.1.filmObject?["action"] == .string(action) }
            XCTAssertEqual(selections.count, 1)
            for (key, value) in fields { XCTAssertEqual(selections.first?.1.filmObject?[key], value) }
            XCTAssertFalse(calls.contains { $0.1.filmObject?["action"] == .string("play") },
                           "Do not silently fall back to a different candidate or episode")
            XCTAssertEqual(mediaReleases(calls), [releasePayload("a")])
            XCTAssertEqual(sessionReleases(calls), [.bool(true), .bool(false)])
            await model.close()
        }
    }

    func testLatestExactSelectionOwnsReentryAcrossTheOldExternalRelease() async throws {
        let server = makeServer(), media = FilmPlaybackTestMedia()
        let releaseEntered = FilmPlaybackGate(), releaseResult = FilmPlaybackGate()
        let selectionEntered = FilmPlaybackGate(), selectionResult = FilmPlaybackGate()
        let model = await makeModel(server, media) { _ in true }
        await model.openExternalPlayer(.infuse)?.value
        await server.beforeRelease { operation, input in
            if operation == "session.release", input.filmObject?["keepMedia"] == .bool(false) {
                await releaseEntered.release(); await releaseResult.wait()
            }
        }
        await server.reply(to: "select.episode", with: FilmPlaybackFixture.snapshot(100, stage: "pending",
            attempt: FilmPlaybackFixture.attempt("c")))
        await server.beforeAction { action in
            if action == "select.episode" {
                await selectionEntered.release(); await selectionResult.wait()
            }
        }
        let older = model.send("select.candidate", parameters: ["candidateId": .string("exact-b")])
        await releaseEntered.wait()
        let episode: RuntimeValue = .object(["season": .number(2), "episode": .number(3)])
        let latest = model.send("select.episode", parameters: ["episode": episode])
        await releaseResult.release()
        await selectionEntered.wait()
        XCTAssertEqual(media.prepared.map(\.attemptID), ["a"],
                       "The reopening snapshot cannot publish media ahead of the exact selection response")
        await selectionResult.release()
        await older?.value; await latest?.value; await model.waitForPendingWork()
        XCTAssertEqual(model.mediaState.attemptID, "c")
        XCTAssertEqual(media.prepared.map(\.attemptID), ["a", "c"])
        XCTAssertEqual(media.resumes, 0)
        XCTAssertNil(model.actionError)
        let calls = await server.calls()
        XCTAssertFalse(calls.contains { $0.1.filmObject?["action"] == .string("select.candidate") ||
            $0.1.filmObject?["action"] == .string("play") })
        let selections = calls.filter { $0.1.filmObject?["action"] == .string("select.episode") }
        XCTAssertEqual(selections.count, 1)
        XCTAssertEqual(selections.first?.1.filmObject?["episode"], episode)
        XCTAssertEqual(mediaReleases(calls), [releasePayload("a")])
        XCTAssertEqual(sessionReleases(calls), [.bool(true), .bool(false)])
        await model.close()
        XCTAssertTrue(model.isReleased)
    }

    func testStopDuringReentryOpenDrainsItsReceiptWithoutSendingTheQueuedSelection() async throws {
        let server = makeServer(), media = FilmPlaybackTestMedia()
        let entered = FilmPlaybackGate(), finish = FilmPlaybackGate()
        let gate = ExternalOwnerOpenGate()
        await server.replyToReceipt { Self.localReceipt($0) }
        let base = server.transport
        let transport = FilmPlaybackTransport(request: { operation, input in
            if operation == "session.open" { await gate.waitIfArmed(entered: entered, finish: finish) }
            return try await base.call(operation, parameters: input)
        }, observation: { sessionID, after, receive in
            try await base.snapshots(sessionID: sessionID, after: after, receive: receive)
        })
        let model = FilmPlaybackModel(transport: transport, media: media,
                                     openExternalURL: { _ in true }, request: { FilmPlaybackFixture.request() })
        await model.open()?.value; await model.waitForPendingWork()
        await model.openExternalPlayer(.infuse)?.value
        await gate.arm()
        let selection = model.send("select.candidate", parameters: ["candidateId": .string("exact-b")])
        await entered.wait()
        XCTAssertNil(model.sessionID, "The new open is admitted but its session identity has not returned")
        let stop = model.send("stop")
        XCTAssertNotNil(stop, "Stop must revoke a queued selection even before the new open returns its ID")
        await finish.release()
        await selection?.value; await stop?.value; await model.waitForPendingWork()
        XCTAssertTrue(model.isReleased)
        XCTAssertNil(model.mediaState.attemptID)
        XCTAssertEqual(media.prepared.map(\.attemptID), ["a"])
        let calls = await server.calls()
        XCTAssertFalse(calls.contains { $0.1.filmObject?["action"] == .string("select.candidate") ||
            $0.1.filmObject?["action"] == .string("play") })
        XCTAssertEqual(calls.filter { $0.0 == "session.open" }.count, 2)
        XCTAssertEqual(sessionReleases(calls), [.bool(true), .bool(false), .bool(false)])
        await model.close()
    }

    func testForegroundDuringKeepAcknowledgementCannotRestoreTheRevokedExternalShare() async throws {
        for accepted in [false, true] {
            let server = makeServer(), media = FilmPlaybackTestMedia()
            let entered = FilmPlaybackGate(), result = FilmPlaybackGate()
            let keepEntered = FilmPlaybackGate(), keepResult = FilmPlaybackGate()
            let model = await makeModel(server, media) { _ in
                await entered.release(); await result.wait(); return accepted
            }
            await server.beforeRelease { operation, input in
                if operation == "session.release", input.filmObject?["keepMedia"] == .bool(true) {
                    await keepEntered.release(); await keepResult.wait()
                }
            }
            let handoff = model.openExternalPlayer(.infuse)
            await entered.wait()
            let background = Task { await model.applicationDidEnterBackground() }
            await keepEntered.wait()
            XCTAssertFalse(model.canLeave, "A requested keep is not an acknowledgement")
            XCTAssertTrue(media.isLocallyDrained(sessionID: FilmPlaybackFixture.sessionID))
            let foregroundEntered = expectation(description: "Revoke before the old keep ACK")
            let foreground = Task {
                foregroundEntered.fulfill()
                await model.applicationDidBecomeActive()
            }
            await fulfillment(of: [foregroundEntered], timeout: 2)
            await keepResult.release()
            await background.value; await foreground.value
            try await assertFinallyReleased(model, server, media)
            await result.release(); await handoff?.value
            let calls = await server.calls()
            XCTAssertEqual(sessionReleases(calls), [.bool(true), .bool(false)])
            XCTAssertEqual(mediaReleases(calls), [releasePayload("a")])
            XCTAssertEqual(media.stopped, [FilmPlaybackFixture.sessionID])
            XCTAssertEqual(media.resumes, 0); XCTAssertFalse(model.hasSystemOwner)
        }
    }

    func testNewPlayAfterBackgroundClosesAAndAnOldCompletionCannotClearBOpening() async throws {
        let server = makeServer(), media = FilmPlaybackTestMedia()
        let aEntered = FilmPlaybackGate(), aResult = FilmPlaybackGate()
        let bEntered = FilmPlaybackGate(), bResult = FilmPlaybackGate()
        var openings = 0
        let model = await makeModel(server, media) { _ in
            openings += 1
            if openings == 1 { await aEntered.release(); await aResult.wait() }
            else { await bEntered.release(); await bResult.wait() }
            return true
        }
        let aHandoff = model.openExternalPlayer(.infuse)
        await aEntered.wait()
        await model.applicationDidEnterBackground()
        try await server.publish(FilmPlaybackFixture.snapshot(100, stage: "pending",
            attempt: FilmPlaybackFixture.attempt("b")))
        await model.send("play")?.value
        await model.waitForPendingWork()
        XCTAssertEqual(model.mediaState.attemptID, "b")
        let bHandoff = model.openExternalPlayer(.nPlayer)
        await bEntered.wait()
        await aResult.release(); await aHandoff?.value
        let sameBHandoff = model.openExternalPlayer(.nPlayer)
        XCTAssertEqual(openings, 2, "Old defer cannot detach the current opening")
        XCTAssertEqual(model.mediaState.attemptID, "b")
        XCTAssertEqual(media.stopped, [FilmPlaybackFixture.sessionID])
        await bResult.release(); await bHandoff?.value; await sameBHandoff?.value
        try await assertExternalOnly(model, server, media, attempt: "b")
        await model.send("stop")?.value
        XCTAssertTrue(model.isReleased)
        let calls = await server.calls()
        XCTAssertEqual(mediaReleases(calls), [releasePayload("a"), releasePayload("b")])
        XCTAssertEqual(sessionReleases(calls), [.bool(true), .bool(false), .bool(true), .bool(false)])
        XCTAssertEqual(media.stopped.count, 2)
    }

    func testAnUnstartedExternalStopCannotCancelTheNewerExplicitPlay() async throws {
        let server = makeServer(), media = FilmPlaybackTestMedia()
        let model = await makeModel(server, media) { _ in true }
        await model.openExternalPlayer(.infuse)?.value
        try await server.publish(FilmPlaybackFixture.snapshot(100, stage: "pending",
            attempt: FilmPlaybackFixture.attempt("b")))
        let stop = model.send("stop")
        let play = model.send("play")
        await stop?.value; await play?.value
        await model.waitForPendingWork()
        XCTAssertEqual(model.mediaState.attemptID, "b")
        XCTAssertFalse(model.isReleased); XCTAssertFalse(model.requiresExplicitPlay)
        XCTAssertEqual(media.stopped, [FilmPlaybackFixture.sessionID])
        let calls = await server.calls()
        XCTAssertEqual(mediaReleases(calls), [releasePayload("a")])
        XCTAssertEqual(sessionReleases(calls), [.bool(true), .bool(false)])
        await model.close()
    }

    func testForegroundCannotStopBToRetryAnAlreadyRevokedAReceipt() async throws {
        let server = makeServer(), media = FilmPlaybackTestMedia()
        let entered = FilmPlaybackGate(), result = FilmPlaybackGate()
        let model = await makeModel(server, media) { _ in
            await entered.release(); await result.wait(); return true
        }
        await server.awaitObservation()
        let handoff = model.openExternalPlayer(.infuse)
        await entered.wait()
        try await media.dispose(attemptID: "a")
        await server.beforeRelease { operation, input in
            if operation == "session.media.release", input.filmObject?["attemptId"] == .string("a") {
                throw FilmPlaybackError(code: "MEDIA_RELEASE_FAILED")
            }
        }
        try await server.publish(FilmPlaybackFixture.snapshot(100, stage: "pending",
            attempt: FilmPlaybackFixture.attempt("b"), current: FilmPlaybackFixture.current("a")))
        await model.waitForPendingWork()
        XCTAssertEqual(model.mediaState.attemptID, "b")
        XCTAssertEqual(model.actionError?.code, "MEDIA_RELEASE_FAILED")
        await model.applicationDidBecomeActive()
        await result.release(); await handoff?.value
        XCTAssertEqual(model.mediaState.attemptID, "b")
        XCTAssertTrue(media.stopped.isEmpty); XCTAssertEqual(media.resumes, 0)
        XCTAssertEqual(model.actionError?.code, "MEDIA_RELEASE_FAILED")
        await server.beforeRelease { _, _ in }
        await model.close()
        XCTAssertTrue(model.isReleased)
        let calls = await server.calls()
        XCTAssertEqual(mediaReleases(calls), [releasePayload("a"), releasePayload("b"), releasePayload("a")])
        XCTAssertEqual(sessionReleases(calls), [.bool(false)])
    }

    func testNewExternalOwnerCannotLoseAnOlderNativeDrainOrItsFailedReceipt() async throws {
        let server = makeServer(), media = FilmPlaybackTestMedia()
        let aEntered = FilmPlaybackGate(), aResult = FilmPlaybackGate()
        let bEntered = FilmPlaybackGate(), bResult = FilmPlaybackGate()
        var openings = 0
        let model = await makeModel(server, media) { _ in
            openings += 1
            if openings == 1 { await aEntered.release(); await aResult.wait() }
            else { await bEntered.release(); await bResult.wait() }
            return true
        }
        await server.awaitObservation()
        let aHandoff = model.openExternalPlayer(.infuse)
        await aEntered.wait()
        await server.reply(to: "play", with: FilmPlaybackFixture.snapshot(100, stage: "pending",
            attempt: FilmPlaybackFixture.attempt("b"), current: FilmPlaybackFixture.current("a")))
        await model.send("play")?.value
        await model.waitForPendingWork()
        XCTAssertEqual(model.mediaState.attemptID, "b")
        let bHandoff = model.openExternalPlayer(.nPlayer)
        await bEntered.wait()
        await server.beforeRelease { operation, input in
            if operation == "session.media.release", input.filmObject?["attemptId"] == .string("a") {
                throw FilmPlaybackError(code: "MEDIA_RELEASE_FAILED")
            }
        }
        try await media.dispose(attemptID: "a")
        await model.waitForPendingWork()
        var calls = await server.calls()
        XCTAssertEqual(mediaReleases(calls), [releasePayload("a")])
        XCTAssertEqual(model.actionError?.code, "MEDIA_RELEASE_FAILED")
        XCTAssertEqual(model.mediaState.attemptID, "b")
        XCTAssertTrue(media.stopped.isEmpty)
        await server.beforeRelease { _, _ in }
        await bResult.release(); await bHandoff?.value
        try await assertExternalOnly(model, server, media, attempt: "b")
        calls = await server.calls()
        XCTAssertEqual(mediaReleases(calls), [releasePayload("a"), releasePayload("a")])
        XCTAssertEqual(sessionReleases(calls), [.bool(true)])
        await aResult.release(); await aHandoff?.value
        await model.applicationDidBecomeActive()
        XCTAssertTrue(model.isReleased)
        calls = await server.calls()
        XCTAssertEqual(mediaReleases(calls), [releasePayload("a"), releasePayload("a"), releasePayload("b")])
        XCTAssertEqual(media.nativeReleases, ["a", "b"])
        XCTAssertEqual(sessionReleases(calls), [.bool(true), .bool(false)])
    }

    func testMusicTakeoverCannotBeStoppedOrResumedByTheOldOpeningResult() async throws {
        for accepted in [false, true] {
            let server = makeServer(), media = FilmPlaybackTestMedia()
            let entered = FilmPlaybackGate(), result = FilmPlaybackGate(), closed = FilmPlaybackGate()
            let model = await makeModel(server, media) { _ in
                await entered.release(); await result.wait(); return accepted
            }
            await server.beforeRelease { operation, _ in
                if operation == "session.release" { await closed.release() }
            }
            let handoff = model.openExternalPlayer(.infuse)
            await entered.wait()
            try await media.dispose(attemptID: "a")
            media.emit(.ownerEnded(attemptID: "a"), attemptID: "a")
            await closed.wait()
            await model.waitForPendingWork()
            XCTAssertTrue(model.isReleased)
            let stops = media.stopped
            await result.release(); await handoff?.value
            XCTAssertEqual(media.stopped, stops)
            XCTAssertEqual(media.resumes, 0)
            let calls = await server.calls()
            XCTAssertEqual(mediaReleases(calls), [releasePayload("a")])
            XCTAssertEqual(sessionReleases(calls), [.bool(false)])
        }
    }

    func testMediaReleaseFailureStaysVisibleAndCloseRetriesOnlyTheExactUnreleasedShare() async throws {
        let server = makeServer(), media = FilmPlaybackTestMedia()
        let model = await makeModel(server, media) { _ in true }
        await model.openExternalPlayer(.infuse)?.value
        await server.beforeRelease { operation, _ in
            if operation == "session.media.release" { throw FilmPlaybackError(code: "MEDIA_RELEASE_FAILED") }
        }
        await model.applicationDidBecomeActive()
        XCTAssertFalse(model.isReleased); XCTAssertFalse(model.canLeave)
        XCTAssertEqual(model.mediaStopError?.code, "MEDIA_RELEASE_FAILED")
        XCTAssertEqual(model.actionError?.code, "MEDIA_RELEASE_FAILED")
        XCTAssertTrue(media.isLocallyDrained(sessionID: FilmPlaybackFixture.sessionID))
        await model.close()
        XCTAssertFalse(model.isReleased)
        var calls = await server.calls()
        XCTAssertEqual(mediaReleases(calls), [releasePayload("a"), releasePayload("a")])
        XCTAssertEqual(sessionReleases(calls), [.bool(true)], "A failed final receipt release cannot be washed by session delete")
        await server.beforeRelease { _, _ in }
        await model.close(); await model.close()
        XCTAssertTrue(model.isReleased); XCTAssertNil(model.mediaStopError); XCTAssertNil(model.sessionReleaseError)
        calls = await server.calls()
        XCTAssertEqual(mediaReleases(calls), [releasePayload("a"), releasePayload("a"), releasePayload("a")])
        XCTAssertEqual(sessionReleases(calls), [.bool(true), .bool(false)])
        XCTAssertEqual(media.nativeReleases, ["a"], "Receipt retries must not replay the physical native release")
        try await assertReceiptExpired(server)
    }

    func testKeepAndFinalSessionFailuresNeedRealAcknowledgementWithoutRepeatingSuccessfulMediaRelease() async throws {
        for failingKeep in [false, true] {
            let server = makeServer(), media = FilmPlaybackTestMedia()
            let model = await makeModel(server, media) { _ in true }
            await server.beforeRelease { operation, input in
                if operation == "session.release", input.filmObject?["keepMedia"] == .bool(failingKeep) {
                    throw FilmPlaybackError(code: "STORAGE_ERROR")
                }
            }
            await model.openExternalPlayer(.infuse)?.value
            if failingKeep {
                XCTAssertFalse(model.canLeave); XCTAssertFalse(model.isReleased)
                XCTAssertEqual(model.sessionReleaseError?.code, "STORAGE_ERROR")
                let heldCalls = await server.calls()
                XCTAssertTrue(mediaReleases(heldCalls).isEmpty)
                await server.beforeRelease { _, _ in }
                await model.applicationDidEnterBackground()
                try await assertExternalOnly(model, server, media)
            }
            await model.applicationDidBecomeActive()
            if !failingKeep {
                XCTAssertFalse(model.isReleased); XCTAssertFalse(model.canLeave)
                XCTAssertEqual(model.sessionReleaseError?.code, "STORAGE_ERROR")
                await model.close()
                XCTAssertFalse(model.isReleased)
                await server.beforeRelease { _, _ in }
                await model.send("stop")?.value
            }
            XCTAssertTrue(model.isReleased); XCTAssertNil(model.sessionReleaseError)
            await model.close()
            let calls = await server.calls()
            XCTAssertEqual(mediaReleases(calls), [releasePayload("a")])
            XCTAssertEqual(sessionReleases(calls), failingKeep ?
                [.bool(true), .bool(true), .bool(false)] : [.bool(true), .bool(false), .bool(false), .bool(false)])
        }
    }

    func testExternalRevokedFirstKeepsTheExactFailedNativeReleaseForCloseRetry() async throws {
        let server = makeServer(), media = FilmPlaybackTestMedia()
        let entered = FilmPlaybackGate(), result = FilmPlaybackGate()
        let model = await makeModel(server, media) { _ in
            await entered.release(); await result.wait(); return true
        }
        let handoff = model.openExternalPlayer(.infuse)
        await entered.wait()
        await server.beforeRelease { operation, _ in
            if operation == "session.media.release" { throw FilmPlaybackError(code: "MEDIA_RELEASE_FAILED") }
        }
        await model.close(); await model.close()
        XCTAssertFalse(model.isReleased); XCTAssertFalse(model.canLeave)
        XCTAssertEqual(model.actionError?.code, "MEDIA_RELEASE_FAILED")
        XCTAssertTrue(media.isLocallyDrained(sessionID: FilmPlaybackFixture.sessionID))
        XCTAssertEqual(media.nativeReleases, ["a"])
        var calls = await server.calls()
        XCTAssertEqual(mediaReleases(calls), [releasePayload("a"), releasePayload("a")])
        XCTAssertTrue(sessionReleases(calls).isEmpty, "Do not discard a failed last-native-share release by deleting its session")
        await server.beforeRelease { _, _ in }
        await model.close(); await model.close()
        XCTAssertTrue(model.isReleased)
        calls = await server.calls()
        XCTAssertEqual(mediaReleases(calls), [releasePayload("a"), releasePayload("a"), releasePayload("a")])
        XCTAssertEqual(sessionReleases(calls), [.bool(false)])
        XCTAssertEqual(media.nativeReleases, ["a"], "The final Node receipt belongs to the external owner, not a replayed native callback")
        await result.release(); await handoff?.value
        XCTAssertTrue(model.isReleased); XCTAssertEqual(media.resumes, 0)
    }

    func testExplicitStopRetriesTheRevokedExternalReceiptWithoutRepeatingNativeRelease() async throws {
        let server = makeServer(), media = FilmPlaybackTestMedia()
        let entered = FilmPlaybackGate(), result = FilmPlaybackGate()
        let model = await makeModel(server, media) { _ in
            await entered.release(); await result.wait(); return true
        }
        let handoff = model.openExternalPlayer(.infuse)
        await entered.wait()
        await server.beforeRelease { operation, _ in
            if operation == "session.media.release" { throw FilmPlaybackError(code: "MEDIA_RELEASE_FAILED") }
        }
        await model.send("stop")?.value
        await model.waitForPendingWork()
        XCTAssertEqual(model.actionError?.code, "MEDIA_RELEASE_FAILED")
        XCTAssertTrue(media.isLocallyDrained(sessionID: FilmPlaybackFixture.sessionID))
        XCTAssertEqual(media.nativeReleases, ["a"])
        var calls = await server.calls()
        XCTAssertEqual(mediaReleases(calls), [releasePayload("a")], "One explicit stop must not silently retry its own failure")
        XCTAssertFalse(calls.contains { $0.1.filmObject?["action"] == .string("stop") })
        XCTAssertTrue(sessionReleases(calls).isEmpty)
        model.acknowledgeError()
        await server.beforeRelease { _, _ in }
        await model.send("stop")?.value
        await model.waitForPendingWork()
        calls = await server.calls()
        XCTAssertEqual(mediaReleases(calls), [releasePayload("a"), releasePayload("a")])
        XCTAssertEqual(calls.filter { $0.1.filmObject?["action"] == .string("stop") }.count, 1)
        XCTAssertEqual(media.nativeReleases, ["a"])
        XCTAssertNil(model.actionError)
        await result.release(); await handoff?.value
        XCTAssertEqual(media.resumes, 0)
        await model.close()
        XCTAssertTrue(model.isReleased)
    }

    func testStopWaitingForAnInFlightExternalReleaseDoesNotRetryItsFailure() async throws {
        let server = makeServer(), media = FilmPlaybackTestMedia()
        let entered = FilmPlaybackGate(), result = FilmPlaybackGate()
        let releaseEntered = FilmPlaybackGate(), releaseResult = FilmPlaybackGate()
        let model = await makeModel(server, media) { _ in
            await entered.release(); await result.wait(); return true
        }
        await server.awaitObservation()
        let handoff = model.openExternalPlayer(.infuse)
        await entered.wait()
        await server.reply(to: "play", with: FilmPlaybackFixture.snapshot(100, stage: "pending",
            attempt: FilmPlaybackFixture.attempt("b"), current: FilmPlaybackFixture.current("a")))
        await model.send("play")?.value
        await model.waitForPendingWork()
        XCTAssertEqual(model.mediaState.attemptID, "b")
        await server.beforeRelease { operation, input in
            if operation == "session.media.release", input.filmObject?["attemptId"] == .string("a") {
                await releaseEntered.release(); await releaseResult.wait()
                throw FilmPlaybackError(code: "MEDIA_RELEASE_FAILED")
            }
        }
        try await media.dispose(attemptID: "a")
        await releaseEntered.wait()
        await server.beforeRelease { _, _ in }
        let stopped = expectation(description: "Native B stops while A's external release is in flight")
        media.onStop = { stopped.fulfill() }
        let stop = model.send("stop")
        await fulfillment(of: [stopped], timeout: 2)
        media.onStop = nil
        await releaseResult.release()
        await stop?.value; await model.waitForPendingWork()
        XCTAssertEqual(model.actionError?.code, "MEDIA_RELEASE_FAILED")
        XCTAssertTrue(media.isLocallyDrained(sessionID: FilmPlaybackFixture.sessionID))
        XCTAssertEqual(media.nativeReleases, ["a", "b"])
        var calls = await server.calls()
        XCTAssertEqual(mediaReleases(calls), [releasePayload("a"), releasePayload("b")],
                       "The stop waiting for A's failed release must not retry it, even if a retry would succeed")
        XCTAssertFalse(calls.contains { $0.1.filmObject?["action"] == .string("stop") })
        XCTAssertTrue(sessionReleases(calls).isEmpty)
        model.acknowledgeError()
        await model.send("stop")?.value
        await model.waitForPendingWork()
        calls = await server.calls()
        XCTAssertEqual(mediaReleases(calls), [releasePayload("a"), releasePayload("b"), releasePayload("a")])
        XCTAssertEqual(calls.filter { $0.1.filmObject?["action"] == .string("stop") }.count, 1)
        XCTAssertEqual(media.nativeReleases, ["a", "b"])
        XCTAssertNil(model.actionError)
        await result.release(); await handoff?.value
        XCTAssertNil(model.mediaState.attemptID); XCTAssertEqual(media.resumes, 0)
        await model.close()
        XCTAssertTrue(model.isReleased)
    }

    func testNativeOnlyReleaseFailureRemainsAnImmutableFailedReceipt() async throws {
        let server = makeServer(), media = FilmPlaybackTestMedia()
        let model = await makeModel(server, media) { _ in
            XCTFail("No external handoff was requested"); return false
        }
        await server.beforeRelease { operation, _ in
            if operation == "session.media.release" { throw FilmPlaybackError(code: "MEDIA_RELEASE_FAILED") }
        }
        await model.close()
        XCTAssertFalse(model.isReleased)
        XCTAssertEqual(model.mediaStopError?.code, "MEDIA_RELEASE_FAILED")
        await server.beforeRelease { _, _ in }
        await model.close()
        XCTAssertFalse(model.isReleased, "An immutable native failure cannot turn into an empty success")
        XCTAssertEqual(model.mediaStopError?.code, "MEDIA_RELEASE_FAILED")
        XCTAssertTrue(media.isLocallyDrained(sessionID: FilmPlaybackFixture.sessionID))
        XCTAssertEqual(media.nativeReleases, ["a"])
        let calls = await server.calls()
        XCTAssertEqual(mediaReleases(calls), [releasePayload("a")])
    }

    func testFailedNativeDrainCannotLeaveEvenWithAnAcknowledgedExternalReceipt() async throws {
        let server = makeServer(), media = FilmPlaybackTestMedia()
        let model = await makeModel(server, media) { _ in true }
        media.stopFailure = .init(code: "MEDIA_UNAVAILABLE")
        await model.openExternalPlayer(.infuse)?.value
        XCTAssertFalse(model.canLeave); XCTAssertFalse(model.isReleased)
        XCTAssertFalse(media.isLocallyDrained(sessionID: FilmPlaybackFixture.sessionID))
        XCTAssertEqual(model.mediaStopError?.code, "MEDIA_UNAVAILABLE")
        let failedCalls = await server.calls()
        XCTAssertEqual(sessionReleases(failedCalls), [.bool(true)])
        XCTAssertTrue(mediaReleases(failedCalls).isEmpty)
        media.stopFailure = nil
        await model.applicationDidEnterBackground()
        try await assertExternalOnly(model, server, media)
        XCTAssertNil(model.mediaStopError)
        await model.close()
        try await assertFinallyReleased(model, server, media)
    }

    func testLateRejectionCannotOverwriteTheRealNativeExitProgressFailure() async throws {
        let server = makeServer(), media = FilmPlaybackTestMedia()
        let entered = FilmPlaybackGate(), result = FilmPlaybackGate()
        let model = await makeModel(server, media) { _ in
            await entered.release(); await result.wait(); return false
        }
        let handoff = model.openExternalPlayer(.infuse)
        await entered.wait()
        await server.fail("media.progress", with: .remote("STORAGE_ERROR"))
        await model.applicationDidEnterBackground()
        XCTAssertEqual(model.actionError?.code, "STORAGE_ERROR")
        await result.release(); await handoff?.value
        XCTAssertTrue(model.isReleased)
        XCTAssertEqual(model.actionError?.code, "STORAGE_ERROR", "System refusal must not hide the actual exit failure")
        let calls = await server.calls()
        XCTAssertEqual(mediaReleases(calls), [releasePayload("a")])
        XCTAssertEqual(sessionReleases(calls), [.bool(true), .bool(false)])
    }

    func testConfirmedRuntimeExitDrainsAndRevokesExternalWithoutWaitingForLateOpener() async throws {
        for accepted in [false, true] {
            let server = makeServer(), media = FilmPlaybackTestMedia()
            let lifetime = ExternalOwnerRuntimeLifetime()
            let entered = FilmPlaybackGate(), result = FilmPlaybackGate(), releaseEntered = FilmPlaybackGate()
            await server.replyToReceipt { Self.localReceipt($0) }
            let transport = FilmPlaybackTransport(request: { operation, input in
                if await lifetime.isClosed {
                    await releaseEntered.release()
                    throw RuntimeHTTPError.closed
                }
                return try await server.call(operation, input)
            }, observation: { id, after, receive in
                try await server.transport.snapshots(sessionID: id, after: after, receive: receive)
            }, runtimeClosure: { try await lifetime.waitUntilClosed() })
            let model = FilmPlaybackModel(transport: transport, media: media, openExternalURL: { _ in
                await entered.release(); await result.wait(); return accepted
            }, request: { FilmPlaybackFixture.request() })
            await model.open()?.value; await model.waitForPendingWork()
            let handoff = model.openExternalPlayer(.infuse)
            await entered.wait()
            await model.applicationDidEnterBackground()
            await server.closeRuntime()
            await lifetime.close()
            await releaseEntered.wait()
            await model.waitForPendingWork()
            XCTAssertTrue(media.isLocallyDrained(sessionID: FilmPlaybackFixture.sessionID))
            XCTAssertTrue(model.canLeave); XCTAssertFalse(model.isReleased)
            XCTAssertFalse(model.hasSystemOwner)
            XCTAssertEqual(model.actionError?.code, "CLOSED")
            do {
                _ = try await server.call("session.attempt", .object([
                    "sessionId": .string(FilmPlaybackFixture.sessionID.uuidString.lowercased()),
                    "attemptId": .string("a"),
                ]))
                XCTFail("A closed runtime cannot expose the external URL")
            } catch let error as RuntimeHTTPError {
                XCTAssertEqual(error, .closed)
            }
            XCTAssertNil(model.open()); XCTAssertNil(model.send("play"))
            let stops = media.stopped
            await result.release(); await handoff?.value
            XCTAssertEqual(media.stopped, stops); XCTAssertEqual(media.resumes, 0)
            XCTAssertFalse(model.isReleased); XCTAssertEqual(model.actionError?.code, "CLOSED")
        }
    }

    private func makeServer() -> FilmPlaybackWireServer {
        FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending", attempt: FilmPlaybackFixture.attempt("a")))
    }
    private func makeModel(_ server: FilmPlaybackWireServer, _ media: FilmPlaybackTestMedia,
                           opener: @escaping @MainActor (URL) async -> Bool) async -> FilmPlaybackModel {
        await server.replyToReceipt { Self.localReceipt($0) }
        let model = FilmPlaybackModel(transport: server.transport, media: media,
                                      openExternalURL: opener, request: { FilmPlaybackFixture.request() })
        await model.open()?.value; await model.waitForPendingWork()
        return model
    }
    nonisolated private static func localReceipt(_ attempt: String) -> RuntimeValue {
        var fields = FilmPlaybackFixture.mediaInput(attempt).filmObject!
        fields["url"] = .string("http://127.0.0.1:19191/media/receipt/\(attempt)?fixture=external")
        return .object(fields)
    }
    private func assertExternalOnly(_ model: FilmPlaybackModel, _ server: FilmPlaybackWireServer,
                                    _ media: FilmPlaybackTestMedia, attempt: String = "a",
                                    file: StaticString = #filePath, line: UInt = #line) async throws {
        XCTAssertTrue(model.canLeave, file: file, line: line)
        XCTAssertFalse(model.isReleased, file: file, line: line)
        XCTAssertFalse(model.hasSystemOwner, file: file, line: line)
        XCTAssertFalse(media.systemOwner, file: file, line: line)
        XCTAssertTrue(media.isLocallyDrained(sessionID: FilmPlaybackFixture.sessionID), file: file, line: line)
        XCTAssertNil(model.mediaState.attemptID, file: file, line: line)
        XCTAssertTrue(model.requiresExplicitPlay, file: file, line: line)
        let receipt = try await server.call("session.attempt", .object([
            "sessionId": .string(FilmPlaybackFixture.sessionID.uuidString.lowercased()), "attemptId": .string(attempt),
        ]))
        XCTAssertEqual(receipt.filmObject?["id"], .string("resource-\(attempt)"), file: file, line: line)
        XCTAssertEqual(receipt.filmObject?["url"],
                       .string("http://127.0.0.1:19191/media/receipt/\(attempt)?fixture=external"), file: file, line: line)
        let calls = await server.calls()
        XCTAssertFalse(mediaReleases(calls).contains(releasePayload(attempt)), file: file, line: line)
    }
    private func assertFinallyReleased(_ model: FilmPlaybackModel, _ server: FilmPlaybackWireServer,
                                       _ media: FilmPlaybackTestMedia,
                                       file: StaticString = #filePath, line: UInt = #line) async throws {
        XCTAssertTrue(model.isReleased, file: file, line: line)
        XCTAssertTrue(model.canLeave, file: file, line: line)
        XCTAssertFalse(model.hasSystemOwner, file: file, line: line)
        XCTAssertTrue(media.isLocallyDrained(sessionID: FilmPlaybackFixture.sessionID), file: file, line: line)
        let calls = await server.calls()
        XCTAssertEqual(mediaReleases(calls), [releasePayload("a")], file: file, line: line)
        try await assertReceiptExpired(server, file: file, line: line)
    }
    private func assertReceiptExpired(_ server: FilmPlaybackWireServer,
                                      file: StaticString = #filePath, line: UInt = #line) async throws {
        do {
            _ = try await server.call("session.attempt", .object([
                "sessionId": .string(FilmPlaybackFixture.sessionID.uuidString.lowercased()), "attemptId": .string("a"),
            ]))
            XCTFail("Expired external share cannot expose its URL again", file: file, line: line)
        } catch let error as FilmPlaybackError {
            XCTAssertEqual(error.code, "STALE_SESSION", file: file, line: line)
        }
    }
    private func releasePayload(_ attempt: String) -> RuntimeValue {
        .object(["sessionId": .string(FilmPlaybackFixture.sessionID.uuidString.lowercased()),
                 "attemptId": .string(attempt), "resourceId": .string("resource-\(attempt)")])
    }
    private func mediaReleases(_ calls: [(String, RuntimeValue)]) -> [RuntimeValue] {
        calls.filter { $0.0 == "session.media.release" }.map(\.1)
    }
    private func sessionReleases(_ calls: [(String, RuntimeValue)]) -> [RuntimeValue?] {
        calls.filter { $0.0 == "session.release" }.map { $0.1.filmObject?["keepMedia"] }
    }
    private func watchActions(_ calls: [(String, RuntimeValue)]) -> [RuntimeValue] {
        calls.filter { $0.0 == "session.command" &&
            $0.1.filmObject?["action"]?.filmText?.hasPrefix("media.") == true ||
            $0.0.hasPrefix("history.") || $0.0.hasPrefix("library.") }.map(\.1)
    }
}

private actor ExternalOwnerOpenGate {
    private var armed = false
    func arm() { armed = true }
    func waitIfArmed(entered: FilmPlaybackGate, finish: FilmPlaybackGate) async {
        guard armed else { return }
        await entered.release(); await finish.wait()
    }
}

private actor ExternalOwnerRuntimeLifetime {
    private(set) var isClosed = false
    private var waiter: CheckedContinuation<Void, Error>?
    func close() { isClosed = true; waiter?.resume(); waiter = nil }
    func waitUntilClosed() async throws {
        if isClosed { return }
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                if Task.isCancelled { continuation.resume(throwing: CancellationError()) }
                else { waiter = continuation }
            }
        } onCancel: { Task { await self.cancel() } }
    }
    private func cancel() { waiter?.resume(throwing: CancellationError()); waiter = nil }
}
