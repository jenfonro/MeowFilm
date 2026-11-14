import XCTest
import Combine
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class FilmPlaybackModelTests: XCTestCase {
    func testPreparedAddressDoesNotCreateNativeMediaOrWatchFactsAndSameIDCanPlay() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "prepared",
            attempt: FilmPlaybackFixture.attempt("attempt-a", prepareOnly: true)))
        let media = FilmPlaybackTestMedia()
        let output = FilmPlaybackGate(), entered = FilmPlaybackGate()
        media.preparationGate = output; media.preparationEntered = entered
        let model = makeModel(server, media)
        await model.open()?.value
        await server.awaitObservation()
        await model.waitForPendingWork()
        XCTAssertTrue(media.prepared.isEmpty)
        var calls = await server.calls()
        XCTAssertEqual(calls.map(\.0), ["session.open"])

        await model.send("play")?.value
        await entered.wait()
        XCTAssertTrue(media.committed.isEmpty)
        XCTAssertEqual(media.prepared.map(\.attemptID), ["attempt-a"])
        calls = await server.calls()
        XCTAssertFalse(calls.contains { $0.1.filmObject?["action"] == .string("media.started") },
                       "A received native input is still not proof of real output")
        XCTAssertFalse(calls.contains { $0.1.filmObject?["action"] == .string("media.progress") })
        await output.release()
        await model.waitForPendingWork()
        XCTAssertEqual(media.committed, ["attempt-a"])
        XCTAssertEqual(model.snapshot?.current?.attemptID, "attempt-a")
        await model.close()
        XCTAssertTrue(model.isReleased)
    }

    func testLatePreparationIsDrainedAndCannotCommitAfterClose() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("late")))
        let media = FilmPlaybackTestMedia()
        let gate = FilmPlaybackGate(), entered = FilmPlaybackGate()
        media.preparationGate = gate; media.preparationEntered = entered
        let model = makeModel(server, media)
        await model.open()?.value
        await entered.wait()
        let close = Task { await model.close() }
        await model.suspendObservation()
        XCTAssertFalse(model.isReleased, "close must wait for the real preparation/lease drain")
        await gate.release()
        await close.value
        XCTAssertTrue(media.committed.isEmpty)
        XCTAssertEqual(media.cancelled, ["late"])
        let calls = await server.calls()
        XCTAssertTrue(calls.contains { $0.0 == "session.media.release" &&
            $0.1.filmObject?["attemptId"] == .string("late") &&
            $0.1.filmObject?["resourceId"] == .string("resource-late") })
        XCTAssertFalse(actions(calls).contains("media.started"))
        XCTAssertTrue(model.isReleased)
    }

    func testLatestUIIntentFencesOldSSEAndCoalescesPendingOperations() async throws {
        let intentGate = FilmPlaybackGate()
        let server = FilmPlaybackWireServer(handler: { operation, input in
            if operation == "session.command" {
                let action = input.filmObject?["action"]?.filmText
                if action == "pan" { await intentGate.wait() }
                return FilmPlaybackFixture.snapshot(action == "pan" ? 2 : 4)
            }
            if operation == "session.attempt" {
                return FilmPlaybackFixture.mediaInput(input.filmObject?["attemptId"]?.filmText ?? "")
            }
            if operation == "session.release" || operation == "session.media.release" {
                return .object(["released": .bool(true)])
            }
            return FilmPlaybackFixture.snapshot()
        })
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        await server.awaitObservation()
        model.selectPan(.quark)
        await server.awaitCalls(2)
        model.selectQuality(.uhd)
        model.send("switch")
        try await server.publish(FilmPlaybackFixture.snapshot(3, stage: "pending",
            attempt: FilmPlaybackFixture.attempt("superseded")))
        XCTAssertTrue(media.prepared.isEmpty, "Old SSE must not bypass the outstanding latest intent")
        await intentGate.release()
        await model.waitForPendingWork()
        let calls = await server.calls()
        XCTAssertEqual(actions(calls), ["pan", "switch"])
        XCTAssertFalse(calls.contains { $0.0 == "session.attempt" })
        await model.close()
    }

    func testOrdinaryCloseDrainsAcceptedUICommandsWithoutPublishingTheirLateSuccessOrFailure() async throws {
        for action in ["browse", "play", "select.episode"] {
            for fails in [false, true] {
                let original = FilmPlaybackFixture.snapshot()
                let server = FilmPlaybackWireServer(original)
                let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
                let entered = FilmPlaybackGate(), response = FilmPlaybackGate()
                await server.beforeAction { requested in
                    if requested == action { await entered.release(); await response.wait() }
                }
                let episode = action == "select.episode" ? 2 : 1
                let lateTarget = FilmPlaybackTarget(id: "tmdb:tv:19", kind: .tv, title: "星河",
                    metadataProvider: .tmdb, metadataID: "19",
                    position: .init(season: 1, episode: episode, globalEpisode: episode))
                var candidate = try FilmPlaybackFixture.candidate().filmRequiredObject()
                candidate["name"] = .string("第\(episode)集")
                candidate["episode"] = .number(Double(episode)); candidate["globalEpisode"] = .number(Double(episode))
                let browsing: RuntimeValue = .object([
                    "sourceId": .string("result-a"), "lineId": .number(0), "loading": .bool(false),
                    "error": .null, "lines": .array([.object(["id": .number(0), "label": .string("原始线路")])]),
                    "episodes": .array([]),
                ])
                if fails { await server.fail(action, with: .remote("STORAGE_ERROR")) }
                else {
                    await server.reply(to: action, with: FilmPlaybackFixture.snapshot(100,
                        stage: action == "browse" ? "idle" : "pending",
                        attempt: action == "browse" ? .null :
                            FilmPlaybackFixture.attempt("late", candidate: .object(candidate)),
                        browsing: action == "browse" ? browsing : nil, target: .object(lateTarget.value)))
                }
                addTeardownBlock { await response.release(); await model.close() }
                await model.open()?.value
                await server.awaitObservation()
                let before = try XCTUnwrap(model.snapshot)
                let parameters: RuntimeObject
                if action == "browse" { parameters = ["sourceId": .string("result-a")] }
                else if action == "select.episode" { parameters = ["episode": .object(lateTarget.position.value)] }
                else { parameters = [:] }
                let command = model.send(action, parameters: parameters)
                await entered.wait()

                let closingEntered = expectation(description: "Ordinary close revokes UI publication before its drain")
                let closing = Task { closingEntered.fulfill(); await model.close() }
                await fulfillment(of: [closingEntered], timeout: 2)
                XCTAssertNil(model.send("search.more"), "The page has already withdrawn UI admission")
                XCTAssertFalse(model.isReleased, "Close must still await the accepted command")
                await response.release()
                await command?.value; await closing.value; await model.waitForPendingWork()

                XCTAssertEqual(model.snapshot, before, "\(action), failure=\(fails)")
                XCTAssertNil(model.actionError, "A departed UI cannot publish this late command error")
                XCTAssertNil(model.mediaStopError); XCTAssertNil(model.sessionReleaseError)
                XCTAssertTrue(model.isReleased)
                XCTAssertTrue(media.prepared.isEmpty); XCTAssertTrue(media.committed.isEmpty)
                var calls = await server.calls()
                XCTAssertEqual(actions(calls), [action], "No native receipt or watching feedback is admitted")
                XCTAssertFalse(calls.contains { $0.0 == "session.attempt" })
                XCTAssertEqual(calls.filter { $0.0 == "session.release" }.count, 1)

                try await server.publish(original)
                await model.open()?.value
                calls = await server.calls()
                let opens = calls.filter { $0.0 == "session.open" }
                XCTAssertEqual(opens.count, 2)
                XCTAssertEqual(opens.last?.1.filmObject?["target"], .object(before.target.value))
                XCTAssertTrue(media.prepared.isEmpty)
                await model.close()
            }
        }
    }

    func testReplacementFailureKeepsThePlayableCurrentAndCannotCommitLate() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("current")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        await server.awaitObservation()
        await model.waitForPendingWork()
        let sequence = Int(try XCTUnwrap(model.snapshot).sequence) + 1
        media.prepareFailure = .init(code: "MEDIA_UNAVAILABLE")
        try await server.publish(FilmPlaybackFixture.snapshot(sequence, stage: "pending",
            attempt: FilmPlaybackFixture.attempt("replacement"), current: FilmPlaybackFixture.current("current")))
        await model.waitForPendingWork()
        XCTAssertEqual(media.committed, ["current"])
        XCTAssertTrue(media.stopped.isEmpty)
        XCTAssertEqual(model.snapshot?.current?.attemptID, "current")
        let calls = await server.calls()
        let failures = calls.filter { $0.1.filmObject?["action"] == .string("media.failed") }
        XCTAssertEqual(failures.map { $0.1.filmObject?["attemptId"] }, [.string("replacement")])
        media.emit(.started(attemptID: "replacement"), attemptID: "replacement")
        await model.waitForPendingWork()
        XCTAssertEqual(model.snapshot?.current?.attemptID, "current")
        await model.close()
    }

    func testDisposedSnapshotCannotEraseCapturedProgressOrTerminalIdentity() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("output")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        await server.awaitObservation()
        await model.waitForPendingWork()
        var state = FilmMediaState()
        state.sessionID = FilmPlaybackFixture.sessionID; state.attemptID = "output"
        state.position = 82; state.duration = 600; state.paused = true
        media.emit(.state(state), attemptID: "output")
        await model.waitForPendingWork()
        try await media.dispose(attemptID: "output")
        XCTAssertNil(model.snapshot?.current)
        XCTAssertNil(model.mediaState.attemptID)
        state.position = 83
        media.emit(.state(state), attemptID: "output")
        media.emit(.ended(attemptID: "output"), attemptID: "output")
        await model.waitForPendingWork()
        let calls = await server.calls()
        let release = try XCTUnwrap(calls.lastIndex { $0.0 == "session.media.release" })
        let progress = try XCTUnwrap(calls.lastIndex { $0.1.filmObject?["action"] == .string("media.progress") })
        let ended = try XCTUnwrap(calls.lastIndex { $0.1.filmObject?["action"] == .string("media.ended") })
        XCTAssertLessThan(release, progress)
        XCTAssertLessThan(progress, ended)
        XCTAssertEqual(calls[progress].1.filmObject?["attemptId"], .string("output"))
        XCTAssertEqual(calls[progress].1.filmObject?["position"], .number(83))
        XCTAssertEqual(calls[ended].1.filmObject?["attemptId"], .string("output"))
        XCTAssertEqual(model.snapshot?.stage, "ended")
        await model.close()
    }

    func testStopDrainsFeedbackEmittedByPhysicalDisposalBeforeReplacingNodeIdentity() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("output")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        await server.awaitObservation()
        await model.waitForPendingWork()
        media.onStop = { [weak media] in
            var state = FilmMediaState()
            state.sessionID = FilmPlaybackFixture.sessionID; state.attemptID = "output"
            state.position = 100; state.duration = 600; state.paused = true
            media?.emit(.state(state), attemptID: "output")
        }
        await model.send("stop")?.value
        let calls = await server.calls()
        let progress = try XCTUnwrap(calls.lastIndex { $0.1.filmObject?["action"] == .string("media.progress") })
        let stop = try XCTUnwrap(calls.lastIndex { $0.1.filmObject?["action"] == .string("stop") })
        XCTAssertLessThan(progress, stop)
        XCTAssertEqual(calls[progress].1.filmObject?["position"], .number(100))
        XCTAssertEqual(model.snapshot?.stage, "stopped")
        await model.close()
    }

    func testObservationDisconnectDoesNotStopMediaAndReconnectUsesLatestSequence() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("active")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        await server.awaitObservation()
        await model.waitForPendingWork()
        let latest = try XCTUnwrap(model.snapshot).sequence
        let disconnected = expectation(description: "The observer publishes the actual disconnect")
        let observation = model.$observationError.first { $0?.code == "OBSERVATION_CLOSED" }.sink { _ in
            disconnected.fulfill()
        }
        await server.disconnect()
        await fulfillment(of: [disconnected], timeout: 2)
        observation.cancel()
        XCTAssertEqual(model.observationError?.code, "OBSERVATION_CLOSED")
        XCTAssertTrue(media.stopped.isEmpty)
        model.reconnect()
        await server.awaitObservation(2)
        let requests = await server.observationRequests()
        XCTAssertEqual(requests.last?.0, FilmPlaybackFixture.sessionID)
        XCTAssertEqual(requests.last?.1, latest)
        await model.close()
    }

    func testCloseDuringAcceptedOpenStillReleasesItsReturnedSessionAndCanReopen() async throws {
        let openGate = FilmPlaybackGate()
        let server = FilmPlaybackWireServer(handler: { operation, _ in
            if operation == "session.open" { await openGate.wait(); return FilmPlaybackFixture.snapshot() }
            return .object(["released": .bool(true)])
        })
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        model.open()
        await server.awaitCalls(1)
        let closeEntered = expectation(description: "Enter close on the same actor before requesting reopen")
        let close = Task { closeEntered.fulfill(); await model.close() }
        await fulfillment(of: [closeEntered], timeout: 2)
        let reopen = model.open()
        await openGate.release()
        await close.value
        await reopen?.value
        XCTAssertNotNil(model.snapshot)
        let calls = await server.calls()
        XCTAssertEqual(calls.map(\.0), ["session.open", "session.release", "session.open"])
        XCTAssertTrue(media.committed.isEmpty)
        await model.close()
    }

    func testOnlyRealSystemOwnedOutputIsRetainedAndOwnerEndReleasesIt() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("pip")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        await server.awaitObservation()
        await model.waitForPendingWork()
        media.systemOwner = true
        await model.close(allowSystemOwner: true)
        XCTAssertTrue(media.stopped.isEmpty)
        XCTAssertFalse(model.isReleased)
        let retainedCalls = await server.calls()
        XCTAssertEqual(retainedCalls.last { $0.0 == "session.release" }?.1.filmObject?["keepMedia"], .bool(true))
        let released = expectation(description: "Ending the retained system owner releases the session")
        let observation = model.$mediaState.first { $0.attemptID == nil }.sink { _ in released.fulfill() }
        media.systemOwner = false
        media.emit(.systemOwnerEnded(attemptID: "pip"), attemptID: "pip")
        await fulfillment(of: [released], timeout: 2)
        await model.waitForPendingWork()
        observation.cancel()
        XCTAssertTrue(model.isReleased)
        let calls = await server.calls()
        XCTAssertEqual(calls.last { $0.0 == "session.release" }?.1.filmObject?["keepMedia"], .bool(false))
    }

    func testRetainedReplacementRechecksTheNewActualSystemOwnerInsteadOfInheritingRetainedForever() async throws {
        for transferredOwner in [false, true] {
            let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
                attempt: FilmPlaybackFixture.attempt("pip-a")))
            let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
            await model.open()?.value
            await server.awaitObservation()
            await model.waitForPendingWork()
            media.systemOwner = true
            await model.close(allowSystemOwner: true)
            await server.awaitObservation(2)
            media.onCommit = { [weak media] id in
                if id == "pip-b" { media?.systemOwner = transferredOwner }
            }
            let released = expectation(description: "The retained session ends only when its new actual system owner is absent")
            let observation = model.$mediaState.first { $0.attemptID == nil }.sink { _ in released.fulfill() }
            try await server.publish(FilmPlaybackFixture.snapshot(100, stage: "pending",
                attempt: FilmPlaybackFixture.attempt("pip-b"), current: FilmPlaybackFixture.current("pip-a")))
            if transferredOwner {
                await model.waitForPendingWork()
                XCTAssertEqual(model.mediaState.attemptID, "pip-b")
                XCTAssertTrue(model.hasSystemOwner); XCTAssertFalse(model.isReleased)
                XCTAssertTrue(media.stopped.isEmpty)
                let calls = await server.calls()
                XCTAssertEqual(calls.filter { $0.0 == "session.release" }.map { $0.1.filmObject?["keepMedia"] },
                               [.bool(true)])
                media.emit(.systemOwnerEnded(attemptID: "pip-a"), attemptID: "pip-a")
                await model.waitForPendingWork()
                XCTAssertTrue(media.stopped.isEmpty, "A retired delegate cannot stop the successfully transferred new owner")
                media.emit(.systemOwnerEnded(attemptID: "pip-b"), attemptID: "pip-b")
                await model.waitForPendingWork()
                XCTAssertTrue(media.stopped.isEmpty, "An ended notification alone is not proof that the new real owner is absent")
                media.systemOwner = false
                media.emit(.systemOwnerEnded(attemptID: "pip-b"), attemptID: "pip-b")
            }
            await fulfillment(of: [released], timeout: 2)
            await model.waitForPendingWork()
            observation.cancel()
            XCTAssertTrue(model.isReleased)
            XCTAssertEqual(media.stopped, [FilmPlaybackFixture.sessionID])
            let calls = await server.calls()
            XCTAssertEqual(calls.filter { $0.0 == "session.release" }.map { $0.1.filmObject?["keepMedia"] },
                           [.bool(true), .bool(false)])
            XCTAssertTrue(calls.contains { $0.0 == "session.media.release" &&
                $0.1.filmObject?["attemptId"] == .string("pip-b") &&
                $0.1.filmObject?["resourceId"] == .string("resource-pip-b") })
        }
    }

    func testStopFailureIsVisibleAndPhysicalStopCanBeRetriedWithoutDoubleSessionRelease() async throws {
        let server = FilmPlaybackWireServer()
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        media.stopFailure = .init(code: "MEDIA_UNAVAILABLE")
        await model.close()
        XCTAssertEqual(model.actionError?.code, "MEDIA_UNAVAILABLE")
        XCTAssertFalse(model.isReleased)
        media.stopFailure = nil
        await model.close()
        XCTAssertTrue(model.isReleased)
        let releases = await server.calls().filter { $0.0 == "session.release" }
        XCTAssertEqual(releases.count, 1)
    }

    func testMetadataBrowseKeepsOnlyExplicitSourceAndRunsBeforeFollowingPlay() async throws {
        let gate = FilmPlaybackGate()
        let server = FilmPlaybackWireServer(handler: { operation, input in
            if operation == "session.command" {
                if input.filmObject?["action"] == .string("browse.metadata") { await gate.wait() }
                return FilmPlaybackFixture.snapshot(input.filmObject?["action"] == .string("play") ? 3 : 2)
            }
            if operation == "session.release" { return .object(["released": .bool(true)]) }
            return FilmPlaybackFixture.snapshot()
        })
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        model.browseMetadata("confirmed-douban")
        await server.awaitCalls(2)
        model.send("play")
        await gate.release()
        await model.waitForPendingWork()
        var calls = await server.calls().filter { $0.0 == "session.command" }
        XCTAssertEqual(actions(calls), ["browse.metadata", "play"])
        XCTAssertEqual(calls[0].1.filmObject?["metadataSourceId"], .string("confirmed-douban"))
        XCTAssertNil(calls[0].1.filmObject?["keepSource"])
        model.browseMetadata("confirmed-tmdb", keepSource: true)
        await model.waitForPendingWork()
        calls = await server.calls().filter { $0.0 == "session.command" }
        XCTAssertEqual(calls.last?.1.filmObject?["keepSource"], .bool(true))
        XCTAssertTrue(media.prepared.isEmpty)
        await model.close()
    }

    func testReceiptRPCFailureIsNotNativeFailureAndDoesNotRetryOnUnrelatedSSE() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("receipt")), handler: { operation, _ in
                if operation == "session.attempt" { throw FilmPlaybackError(code: "TIMEOUT") }
                if operation == "session.release" { return .object(["released": .bool(true)]) }
                return FilmPlaybackFixture.snapshot(stage: "pending", attempt: FilmPlaybackFixture.attempt("receipt"))
            })
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        await server.awaitObservation()
        await model.waitForPendingWork()
        XCTAssertEqual(model.actionError?.code, "TIMEOUT")
        try await server.publish(FilmPlaybackFixture.snapshot(2, stage: "pending",
            attempt: FilmPlaybackFixture.attempt("receipt")))
        await model.waitForPendingWork()
        let calls = await server.calls()
        XCTAssertEqual(calls.filter { $0.0 == "session.attempt" }.count, 1)
        XCTAssertFalse(actions(calls).contains("media.failed"))
        XCTAssertTrue(media.prepared.isEmpty)
        await model.close()
    }

    func testMalformedOpenStillReleasesTheAllocatedSessionRatherThanLeakingIt() async throws {
        let server = FilmPlaybackWireServer(handler: { operation, _ in
            if operation == "session.open" {
                return .object(["sessionId": .string(FilmPlaybackFixture.sessionID.uuidString.lowercased())])
            }
            return .object(["released": .bool(true)])
        })
        let model = makeModel(server, FilmPlaybackTestMedia())
        await model.open()?.value
        XCTAssertEqual(model.actionError?.code, "INVALID_RESPONSE")
        XCTAssertNil(model.snapshot)
        await model.close()
        let calls = await server.calls()
        XCTAssertEqual(calls.map(\.0), ["session.open", "session.release"])
        XCTAssertEqual(calls.last?.1.filmObject?["sessionId"],
                       .string(FilmPlaybackFixture.sessionID.uuidString.lowercased()))
        XCTAssertTrue(model.isReleased)
    }

    func testEmptyMediaReleaseReceiptIsAnActualCloseFailure() async throws {
        let server = FilmPlaybackWireServer(handler: { operation, _ in
            switch operation {
            case "session.open": return FilmPlaybackFixture.snapshot(stage: "pending",
                attempt: FilmPlaybackFixture.attempt("output"))
            case "session.attempt": return FilmPlaybackFixture.mediaInput("output")
            case "session.media.release": return .object([:])
            case "session.release": return .object(["released": .bool(true)])
            default: return FilmPlaybackFixture.snapshot(2, stage: "playing", current: FilmPlaybackFixture.current("output"))
            }
        })
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        await model.waitForPendingWork()
        await model.close()
        XCTAssertEqual(model.actionError?.code, "INVALID_RESPONSE")
        XCTAssertFalse(model.isReleased, "An empty response does not prove physical and receipt drain")
    }

    func testClosingPiPInTheVisiblePlayerDoesNotEndTheInlineOwner() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("inline")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        await model.waitForPendingWork()
        media.emit(.systemOwnerEnded(attemptID: "inline"), attemptID: "inline")
        await model.waitForPendingWork()
        XCTAssertTrue(media.stopped.isEmpty)
        XCTAssertEqual(model.mediaState.attemptID, "inline")
        let calls = await server.calls()
        XCTAssertFalse(calls.contains { $0.0 == "session.release" })
        await model.close()
    }

    func testControlsAndSystemNavigationStayFencedToRealCurrentOutput() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("controls")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        await model.waitForPendingWork()
        model.pauseOrResume()
        var state = model.mediaState
        state.paused = true
        media.emit(.state(state), attemptID: "controls")
        model.pauseOrResume(); model.seek(to: 91); model.setRate(1.5); model.setVolume(0.4)
        XCTAssertEqual(media.pauses, 1); XCTAssertEqual(media.resumes, 1)
        XCTAssertEqual(media.seeks, [91]); XCTAssertEqual(media.rates, [1.5]); XCTAssertEqual(media.volumes, [0.4])
        media.systemOwner = true
        await model.close(allowSystemOwner: true)
        XCTAssertTrue(model.navigateFromSystem(forward: true))
        await model.waitForPendingWork()
        let calls = await server.calls()
        XCTAssertEqual(actions(calls).last, "next")
        media.systemOwner = false
        await model.close()
        XCTAssertFalse(model.navigateFromSystem(forward: false))
    }

    func testBrowsingAnotherSourceDoesNotRetargetCurrentMediaTransport() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("transport-current")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        await model.waitForPendingWork()
        let before = try XCTUnwrap(model.snapshot?.current)
        let browsing: RuntimeValue = .object([
            "sourceId": .string("result-other"), "lineId": .number(0), "season": .number(2),
            "loading": .bool(false), "error": .null, "lines": .array([]), "episodes": .array([]),
        ])
        await server.reply(to: "browse", with: FilmPlaybackFixture.snapshot(10, stage: "playing",
            current: FilmPlaybackFixture.current("transport-current"), browsing: browsing))
        model.browse(sourceID: "result-other", lineID: .number(0))
        await model.waitForPendingWork()
        XCTAssertEqual(model.snapshot?.browsing.sourceID, "result-other")
        XCTAssertEqual(model.snapshot?.current, before)
        model.pauseOrResume()
        model.pauseOrResume()
        await model.waitForPendingWork()
        XCTAssertEqual(media.pauses, 1); XCTAssertEqual(media.resumes, 1)
        XCTAssertEqual(media.prepared.map(\.attemptID), ["transport-current"])
        XCTAssertEqual(model.mediaState.attemptID, "transport-current")
        let calls = await server.calls()
        XCTAssertEqual(actions(calls).filter { ["browse", "play", "select.candidate", "select.episode"].contains($0) }, ["browse"])
        XCTAssertTrue(media.stopped.isEmpty)
        await model.close()
    }

    func testChangingEpisodeDoesNotPreservePositionAndRetiredCallbacksCannotStopNewOutput() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("first")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        await server.awaitObservation()
        await model.waitForPendingWork()
        var candidate = try FilmPlaybackFixture.candidate("second").filmRequiredObject()
        candidate["episode"] = .number(2); candidate["globalEpisode"] = .number(2)
        try await server.publish(FilmPlaybackFixture.snapshot(Int(try XCTUnwrap(model.snapshot).sequence) + 1,
            stage: "pending", attempt: FilmPlaybackFixture.attempt("second", candidate: .object(candidate)),
            current: FilmPlaybackFixture.current("first")))
        await model.waitForPendingWork()
        XCTAssertEqual(media.preservedPosition, [false, false])
        media.emit(.ownerEnded(attemptID: "first"), attemptID: "first")
        media.emit(.ended(attemptID: "first"), attemptID: "first")
        await model.waitForPendingWork()
        XCTAssertEqual(model.mediaState.attemptID, "second")
        XCTAssertTrue(media.stopped.isEmpty)
        let calls = await server.calls()
        XCTAssertFalse(calls.contains { $0.0 == "session.release" })
        XCTAssertFalse(calls.contains { $0.1.filmObject?["action"] == .string("media.ended") &&
            $0.1.filmObject?["attemptId"] == .string("first") })
        await model.close()
    }

    func testSameMovieReplacementExplicitlyPreservesTheCurrentPositionIntent() async throws {
        var target = try FilmPlaybackFixture.target.filmRequiredObject()
        target["kind"] = .string("movie")
        target["season"] = nil; target["episode"] = nil; target["globalEpisode"] = nil
        let movie = RuntimeValue.object(target)
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("first"), target: movie))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        await server.awaitObservation()
        await model.waitForPendingWork()
        try await server.publish(FilmPlaybackFixture.snapshot(Int(try XCTUnwrap(model.snapshot).sequence) + 1,
            stage: "pending", attempt: FilmPlaybackFixture.attempt("replacement"),
            current: FilmPlaybackFixture.current("first"), target: movie))
        await model.waitForPendingWork()
        XCTAssertEqual(media.preservedPosition, [false, true])
        await model.close()
    }

    func testExactSourceReplacementPreservesOnlyTheSameConfirmedMoviePosition() async throws {
        for kind in [FilmPlaybackKind.movie, .tv] {
            let metadataSource: RuntimeValue = .object(["id": .string("confirmed"), "provider": .string("tmdb"),
                "metadataId": .number(19), "kind": .string(kind.rawValue), "title": .string("可靠作品")])
            func candidate(_ id: String) -> RuntimeValue {
                .object(["id": .string(id), "source": FilmPlaybackFixture.source, "lineId": .number(0),
                    "lineLabel": .string("原线路"), "name": .string("真实文件")])
            }
            func target(_ id: String) -> FilmPlaybackTarget {
                .init(id: "stable-content", kind: .source, title: "可靠作品",
                    source: ["key": .string("source-key"), "detailId": .number(0),
                             "lineId": .number(0), "candidateId": .string(id)])
            }
            let first = candidate("first-file"), replacement = candidate("replacement-file")
            let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
                attempt: FilmPlaybackFixture.attempt("first", candidate: first), target: .object(target("first-file").value),
                metadataSources: [metadataSource], metadataSourceID: "confirmed"))
            let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
            await model.open()?.value
            await server.awaitObservation()
            await model.waitForPendingWork()
            var state = model.mediaState
            state.position = 137; state.paused = true
            media.emit(.state(state), attemptID: "first")
            await model.waitForPendingWork()
            let current: RuntimeValue = .object(["attemptId": .string("first"), "candidate": first,
                "position": .number(137), "duration": .number(600), "paused": .bool(true)])
            try await server.publish(FilmPlaybackFixture.snapshot(Int(try XCTUnwrap(model.snapshot).sequence) + 1,
                stage: "pending", attempt: FilmPlaybackFixture.attempt("replacement", candidate: replacement),
                current: current, target: .object(target("replacement-file").value),
                metadataSources: [metadataSource], metadataSourceID: "confirmed"))
            await model.waitForPendingWork()
            XCTAssertEqual(model.snapshot?.target.kind, .source)
            XCTAssertEqual(media.preservedPosition, [false, kind == .movie])
            XCTAssertEqual(model.mediaState.position, kind == .movie ? 137 : 0)
            XCTAssertEqual(model.mediaState.paused, kind == .movie)
            await model.close()
        }
    }

    func testRetryOfMalformedOpenDrainsThePreviousAllocatedSessionFirst() async throws {
        let server = FilmPlaybackWireServer(handler: { operation, _ in
            if operation == "session.open" {
                return .object(["sessionId": .string(FilmPlaybackFixture.sessionID.uuidString.lowercased())])
            }
            return .object(["released": .bool(true)])
        })
        let model = makeModel(server, FilmPlaybackTestMedia())
        await model.open()?.value
        await model.open()?.value
        let calls = await server.calls()
        XCTAssertEqual(calls.map(\.0), ["session.open", "session.release", "session.open"])
        XCTAssertEqual(model.actionError?.code, "INVALID_RESPONSE")
        await model.close()
    }

    func testNativeOwnerWithdrawalReleasesVisiblePlaybackWithoutWaitingForUIExit() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("withdrawn")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        await model.waitForPendingWork()
        let released = expectation(description: "A native owner withdrawal closes the real Film owner")
        let observation = model.$mediaState.first { $0.attemptID == nil }.sink { _ in released.fulfill() }
        media.emit(.ownerEnded(attemptID: "withdrawn"), attemptID: "withdrawn")
        await fulfillment(of: [released], timeout: 2)
        await model.waitForPendingWork()
        observation.cancel()
        XCTAssertTrue(model.isReleased)
        XCTAssertEqual(media.stopped, [FilmPlaybackFixture.sessionID])
        let calls = await server.calls()
        XCTAssertEqual(calls.last { $0.0 == "session.release" }?.1.filmObject?["keepMedia"], .bool(false))
    }

    func testRevokedPendingAttemptIsPhysicallyDrainedAndCannotAcquireOutput() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("revoked")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        let output = FilmPlaybackGate(), entered = FilmPlaybackGate()
        media.preparationGate = output; media.preparationEntered = entered
        await model.open()?.value
        await server.awaitObservation()
        await entered.wait()
        try await server.publish(FilmPlaybackFixture.snapshot(2, stage: "idle"))
        await output.release()
        await model.waitForPendingWork()
        XCTAssertTrue(media.committed.isEmpty)
        XCTAssertEqual(media.cancelled, ["revoked"])
        let calls = await server.calls()
        XCTAssertTrue(calls.contains { $0.0 == "session.media.release" &&
            $0.1.filmObject?["attemptId"] == .string("revoked") })
        XCTAssertFalse(actions(calls).contains("media.started"))
        XCTAssertFalse(actions(calls).contains("media.failed"), "Revocation is not a request failure")
        XCTAssertNil(model.actionError)
        await model.close()
    }

    func testLateMalformedReceiptCannotPublishAnErrorIntoANewerIntent() async throws {
        let gate = FilmPlaybackGate()
        let server = FilmPlaybackWireServer(handler: { operation, _ in
            switch operation {
            case "session.open": return FilmPlaybackFixture.snapshot(stage: "pending",
                attempt: FilmPlaybackFixture.attempt("old-receipt"))
            case "session.attempt":
                await gate.wait()
                return .object([:])
            case "session.command": return FilmPlaybackFixture.snapshot(2, stage: "stopped")
            case "session.release": return .object(["released": .bool(true)])
            default: throw FilmPlaybackError.invalidResponse
            }
        })
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        await server.awaitCalls(2)
        model.send("stop")
        await gate.release()
        await model.waitForPendingWork()
        XCTAssertNil(model.actionError, "A superseded receipt cannot overwrite the latest operation's result")
        XCTAssertEqual(model.snapshot?.stage, "stopped")
        XCTAssertTrue(media.prepared.isEmpty)
        let calls = await server.calls()
        XCTAssertEqual(actions(calls), ["stop"])
        await model.close()
    }

    func testASecondCloseCancelsAPendingReopenInsteadOfResurrectingTheSession() async throws {
        let gate = FilmPlaybackGate()
        let server = FilmPlaybackWireServer(handler: { operation, _ in
            if operation == "session.open" { await gate.wait(); return FilmPlaybackFixture.snapshot() }
            return .object(["released": .bool(true)])
        })
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        model.open()
        await server.awaitCalls(1)
        let firstEntered = expectation(description: "First close is admitted")
        let first = Task { firstEntered.fulfill(); await model.close() }
        await fulfillment(of: [firstEntered], timeout: 2)
        let reopen = model.open()
        let secondEntered = expectation(description: "A later close revokes the reopen intent")
        let second = Task { secondEntered.fulfill(); await model.close() }
        await fulfillment(of: [secondEntered], timeout: 2)
        await gate.release()
        await first.value; await second.value; await reopen?.value
        let calls = await server.calls()
        XCTAssertEqual(calls.map(\.0), ["session.open", "session.release"])
        XCTAssertTrue(model.isReleased)
        XCTAssertTrue(media.prepared.isEmpty)
    }

    func testLatestOpenAfterTwoClosesIsNotLostOrClosedByTheOlderDrain() async throws {
        let gate = FilmPlaybackGate()
        let server = FilmPlaybackWireServer(handler: { operation, _ in
            if operation == "session.open" { await gate.wait(); return FilmPlaybackFixture.snapshot() }
            return .object(["released": .bool(true)])
        })
        let model = makeModel(server, FilmPlaybackTestMedia())
        model.open()
        await server.awaitCalls(1)
        let firstEntered = expectation(description: "First close is admitted")
        let first = Task { firstEntered.fulfill(); await model.close() }
        await fulfillment(of: [firstEntered], timeout: 2)
        let superseded = model.open()
        let secondEntered = expectation(description: "Second close revokes the first reopen")
        let second = Task { secondEntered.fulfill(); await model.close() }
        await fulfillment(of: [secondEntered], timeout: 2)
        let latest = model.open()
        await gate.release()
        await first.value; await second.value; await superseded?.value; await latest?.value
        await model.waitForPendingWork()
        let calls = await server.calls()
        XCTAssertEqual(calls.map(\.0), ["session.open", "session.release", "session.open"])
        XCTAssertNotNil(model.snapshot)
        XCTAssertFalse(model.isReleased, "The older close must not target the newly opened session")
        await model.close()
    }

    func testReopeningRetainedOutputSupersedesAnOlderWaitingClose() async throws {
        let gate = FilmPlaybackGate(), releaseEntered = FilmPlaybackGate()
        let server = FilmPlaybackWireServer(handler: { operation, input in
            switch operation {
            case "session.open": return FilmPlaybackFixture.snapshot(stage: "pending",
                attempt: FilmPlaybackFixture.attempt("pip"))
            case "session.attempt": return FilmPlaybackFixture.mediaInput("pip")
            case "session.release":
                if input.filmObject?["keepMedia"] == .bool(true) {
                    await releaseEntered.release()
                    await gate.wait()
                    return .object(["retained": .bool(true)])
                }
                return .object(["released": .bool(true)])
            case "session.media.release": return .object(["released": .bool(true)])
            default: return FilmPlaybackFixture.snapshot(2, stage: "playing",
                current: FilmPlaybackFixture.current("pip"))
            }
        })
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        await model.waitForPendingWork()
        media.systemOwner = true
        let retained = Task { await model.close(allowSystemOwner: true) }
        await releaseEntered.wait()
        let entered = expectation(description: "A full close waits behind the in-flight keepMedia receipt")
        let older = Task { entered.fulfill(); await model.close() }
        await fulfillment(of: [entered], timeout: 2)
        let latest = model.open()
        await gate.release()
        await retained.value; await older.value; await latest?.value
        XCTAssertTrue(media.stopped.isEmpty)
        XCTAssertTrue(model.hasSystemOwner)
        XCTAssertFalse(model.isReleased)
        let calls = await server.calls()
        XCTAssertEqual(calls.filter { $0.0 == "session.release" }.count, 1)
        media.systemOwner = false
        await model.close()
    }

    func testOrdinaryBackgroundReturnDoesNotAutoplayAndOnePlayRequestsFreshOutputWithEitherPreparationPreference() async throws {
        for autoPrepare in [false, true] {
            let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
                attempt: FilmPlaybackFixture.attempt("before-background")))
            let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
            await model.open()?.value
            await model.waitForPendingWork()
            await model.close(allowSystemOwner: true)
            XCTAssertTrue(model.isReleased); XCTAssertNil(model.mediaState.attemptID)
            let idle = FilmPlaybackFixture.snapshot(100, stage: autoPrepare ? "prepared" : "idle",
                attempt: autoPrepare ? FilmPlaybackFixture.attempt("after-background", prepareOnly: true) : .null)
            var fields = try idle.filmRequiredObject()
            var preferences = FilmPlaybackPreferences.defaults
            preferences.iosAutoPrepare = autoPrepare
            fields["preferences"] = preferences.value
            try await server.publish(.object(fields))
            await model.open()?.value
            await server.awaitObservation(2)
            await model.waitForPendingWork()
            XCTAssertEqual(model.snapshot?.stage, autoPrepare ? "prepared" : "idle")
            XCTAssertEqual(model.snapshot?.preferences.iosAutoPrepare, autoPrepare)
            XCTAssertEqual(media.prepared.map(\.attemptID), ["before-background"])
            XCTAssertNil(model.mediaState.attemptID)
            var calls = await server.calls()
            XCTAssertFalse(actions(calls).contains("play"))
            XCTAssertEqual(actions(calls).filter { $0 == "media.started" }.count, 1)
            if !autoPrepare {
                await server.reply(to: "play", with: FilmPlaybackFixture.snapshot(101, stage: "pending",
                    attempt: FilmPlaybackFixture.attempt("after-background")))
            }
            model.pauseOrResume()
            await model.waitForPendingWork()
            XCTAssertEqual(media.prepared.map(\.attemptID), ["before-background", "after-background"])
            XCTAssertEqual(model.mediaState.attemptID, "after-background")
            XCTAssertEqual(media.resumes, 0, "Without a current owner the button must issue formal play, not native resume")
            calls = await server.calls()
            XCTAssertEqual(actions(calls).filter { $0 == "play" }, ["play"])
            XCTAssertEqual(calls.filter { $0.0 == "session.attempt" }.map { $0.1.filmObject?["attemptId"] },
                           [.string("before-background"), .string("after-background")])
            XCTAssertEqual(calls.filter { $0.0 == "session.media.release" }.map { $0.1.filmObject?["attemptId"] },
                           [.string("before-background")])
            await model.close()
        }
    }

    func testMetadataEntryReopensExactSourceWithOriginalProvenanceAndWithoutNativePreparation() async throws {
        for kind in [FilmPlaybackKind.movie, .tv] {
            let entry = FilmPlaybackTarget(id: "tmdb:\(kind.rawValue):19", kind: kind, title: "原入口",
                metadataProvider: .tmdb, metadataID: "19")
            let metadata: RuntimeValue = .object(["id": .number(19), "title": .string("原入口资料")])
            let confirmed: RuntimeValue = .object(["id": .string("confirmed-tmdb"), "provider": .string("tmdb"),
                "metadataId": .number(19), "kind": .string(kind.rawValue), "title": .string("原入口")])
            let exact = FilmPlaybackTarget(id: entry.id, kind: .source, title: entry.title,
                position: kind == .tv ? .init(season: 0, episode: 0) : .init(),
                source: ["key": .string("source-key"), "detailId": .number(0),
                         "lineId": .string("0"), "candidateId": .string("opaque-selected")])
            var initial = try FilmPlaybackFixture.snapshot(metadata: metadata, target: .object(entry.value),
                metadataSources: [confirmed], metadataSourceID: "confirmed-tmdb").filmRequiredObject()
            initial["entryTarget"] = .object(entry.value)
            let server = FilmPlaybackWireServer(.object(initial)), media = FilmPlaybackTestMedia()
            let request = FilmPlaybackRequest(target: entry, sources: [try FilmPlaybackSource(FilmPlaybackFixture.source)],
                metadata: metadata, entrySurface: "home")
            let model = FilmPlaybackModel(transport: server.transport, media: media, request: { request })
            addTeardownBlock { await model.close() }
            await model.open()?.value
            XCTAssertEqual(model.snapshot?.target, entry)
            var selected = initial
            selected["sequence"] = .number(10); selected["target"] = .object(exact.value)
            await server.reply(to: "select.candidate", with: .object(selected))
            model.selectCandidate("opaque-selected")
            await model.waitForPendingWork()
            XCTAssertEqual(model.snapshot?.target, exact)
            await model.close()
            XCTAssertTrue(model.isReleased)
            await model.open()?.value
            await model.waitForPendingWork()
            let calls = await server.calls()
            let opens = calls.filter { $0.0 == "session.open" }
            XCTAssertEqual(opens.count, 2)
            XCTAssertNil(opens.first?.1.filmObject?["resumeTarget"])
            XCTAssertEqual(opens.last?.1.filmObject?["target"], .object(entry.value))
            XCTAssertEqual(opens.last?.1.filmObject?["metadata"], metadata)
            XCTAssertEqual(opens.last?.1.filmObject?["resumeTarget"], .object(exact.value))
            XCTAssertEqual(opens.last?.1.filmObject?["sources"], .array(request.sources.map { .object($0.value) }))
            XCTAssertEqual(opens.last?.1.filmObject?["entrySurface"], .string("home"))
            XCTAssertEqual(opens.last?.1.filmObject?["platform"], .string("ios"))
            XCTAssertNil(opens.last?.1.filmObject?["searchReference"])
            XCTAssertEqual(model.snapshot?.entryTarget, entry)
            XCTAssertEqual(model.snapshot?.target, exact)
            XCTAssertEqual(model.snapshot?.metadataSourceID, "confirmed-tmdb")
            XCTAssertEqual(model.snapshot?.metadataSources.first?.kind, kind)
            XCTAssertEqual(actions(calls), ["select.candidate"])
            XCTAssertTrue(media.prepared.isEmpty)
            XCTAssertNil(model.mediaState.attemptID); XCTAssertNil(model.actionError)
            await model.close()
        }
    }

    func testBufferingKeepsPlayingIntentUntilAnAccuratePauseAndProgressReportsTheActualIntent() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("buffering")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        await model.waitForPendingWork()
        var state = model.mediaState; state.buffering = true; state.paused = false; state.position = 47
        media.emit(.state(state), attemptID: "buffering")
        await model.waitForPendingWork()
        var calls = await server.calls()
        XCTAssertEqual(calls.last { $0.1.filmObject?["action"] == .string("media.progress") }?.1.filmObject?["paused"],
                       .bool(false))
        XCTAssertTrue(model.mediaState.buffering); XCTAssertFalse(model.mediaState.paused)
        model.pauseOrResume()
        await model.waitForPendingWork()
        XCTAssertEqual(media.pauses, 1); XCTAssertEqual(media.resumes, 0)
        XCTAssertTrue(model.mediaState.paused)
        calls = await server.calls()
        XCTAssertEqual(calls.last { $0.1.filmObject?["action"] == .string("media.progress") }?.1.filmObject?["paused"],
                       .bool(true))
        await model.close()
    }

    func testExactCandidateAndEpisodeSelectionsReopenAfterBackgroundWithoutFallingBackToPlay() async throws {
        let selections: [(String, RuntimeObject)] = [
            ("select.candidate", ["candidateId": .string("exact-b")]),
            ("select.episode", ["episode": .object(["season": .number(2), "episode": .number(3)])]),
        ]
        for (action, fields) in selections {
            let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
                attempt: FilmPlaybackFixture.attempt("a")))
            let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
            await model.open()?.value; await model.waitForPendingWork()
            await model.applicationDidEnterBackground()
            await server.reply(to: action, with: FilmPlaybackFixture.snapshot(100, stage: "pending",
                attempt: FilmPlaybackFixture.attempt("b")))
            await model.send(action, parameters: fields)?.value
            await model.waitForPendingWork()
            XCTAssertEqual(model.mediaState.attemptID, "b")
            XCTAssertNil(model.actionError)
            XCTAssertEqual(media.stopped, [FilmPlaybackFixture.sessionID])
            let calls = await server.calls()
            let selections = calls.filter { $0.1.filmObject?["action"] == .string(action) }
            XCTAssertEqual(selections.count, 1)
            for (key, value) in fields { XCTAssertEqual(selections.first?.1.filmObject?[key], value) }
            XCTAssertFalse(actions(calls).contains("play"),
                           "Do not silently fall back to a different candidate or episode")
            XCTAssertEqual(mediaReleases(calls), [releasePayload("a")])
            XCTAssertEqual(sessionReleases(calls), [.bool(false)])
            await model.close()
        }
    }

    func testLatestExactSelectionOwnsReentryAcrossTheOldCloseDrain() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("a")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        let releaseEntered = FilmPlaybackGate(), releaseResult = FilmPlaybackGate()
        let selectionEntered = FilmPlaybackGate(), selectionResult = FilmPlaybackGate()
        await model.open()?.value; await model.waitForPendingWork()
        await server.beforeRelease { operation, _ in
            if operation == "session.release" {
                await releaseEntered.release(); await releaseResult.wait()
            }
        }
        let background = Task { await model.applicationDidEnterBackground() }
        await releaseEntered.wait()
        await server.reply(to: "select.episode", with: FilmPlaybackFixture.snapshot(100, stage: "pending",
            attempt: FilmPlaybackFixture.attempt("c")))
        await server.beforeAction { action in
            if action == "select.episode" {
                await selectionEntered.release(); await selectionResult.wait()
            }
        }
        let older = model.send("select.candidate", parameters: ["candidateId": .string("exact-b")])
        let episode: RuntimeValue = .object(["season": .number(2), "episode": .number(3)])
        let latest = model.send("select.episode", parameters: ["episode": episode])
        await releaseResult.release()
        await selectionEntered.wait()
        XCTAssertEqual(media.prepared.map(\.attemptID), ["a"],
                       "The reopening snapshot cannot publish media ahead of the exact selection response")
        await selectionResult.release()
        await background.value; await older?.value; await latest?.value; await model.waitForPendingWork()
        XCTAssertEqual(model.mediaState.attemptID, "c")
        XCTAssertEqual(media.prepared.map(\.attemptID), ["a", "c"])
        XCTAssertEqual(media.resumes, 0)
        XCTAssertNil(model.actionError)
        let calls = await server.calls()
        XCTAssertFalse(actions(calls).contains("select.candidate") || actions(calls).contains("play"))
        let selections = calls.filter { $0.1.filmObject?["action"] == .string("select.episode") }
        XCTAssertEqual(selections.count, 1)
        XCTAssertEqual(selections.first?.1.filmObject?["episode"], episode)
        XCTAssertEqual(mediaReleases(calls), [releasePayload("a")])
        XCTAssertEqual(sessionReleases(calls), [.bool(false)])
        await model.close()
        XCTAssertTrue(model.isReleased)
    }

    func testStopDuringReentryOpenDrainsItsReceiptWithoutSendingTheQueuedSelection() async throws {
        let entered = FilmPlaybackGate(), finish = FilmPlaybackGate()
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("a")))
        let media = FilmPlaybackTestMedia(), gate = FilmPlaybackReentryGate()
        let base = server.transport
        let transport = FilmPlaybackTransport(request: { operation, input in
            if operation == "session.open" { await gate.waitIfArmed(entered: entered, finish: finish) }
            return try await base.call(operation, parameters: input)
        }, observation: { sessionID, after, receive in
            try await base.snapshots(sessionID: sessionID, after: after, receive: receive)
        })
        let model = FilmPlaybackModel(transport: transport, media: media, request: { FilmPlaybackFixture.request() })
        await model.open()?.value; await model.waitForPendingWork()
        await model.applicationDidEnterBackground()
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
        XCTAssertFalse(actions(calls).contains("select.candidate") || actions(calls).contains("play"))
        XCTAssertEqual(calls.filter { $0.0 == "session.open" }.count, 2)
        XCTAssertEqual(mediaReleases(calls), [releasePayload("a")])
        XCTAssertEqual(sessionReleases(calls), [.bool(false), .bool(false)])
        await model.close()
    }

    func testUnstartedStopCannotCancelTheNewerExplicitPlayAfterBackground() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("a")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value; await model.waitForPendingWork()
        await model.applicationDidEnterBackground()
        try await server.publish(FilmPlaybackFixture.snapshot(100, stage: "pending",
            attempt: FilmPlaybackFixture.attempt("b")))
        let stop = model.send("stop")
        let play = model.send("play")
        await stop?.value; await play?.value; await model.waitForPendingWork()
        XCTAssertEqual(model.mediaState.attemptID, "b")
        XCTAssertFalse(model.isReleased)
        XCTAssertEqual(media.stopped, [FilmPlaybackFixture.sessionID])
        let calls = await server.calls()
        XCTAssertEqual(mediaReleases(calls), [releasePayload("a")])
        XCTAssertEqual(sessionReleases(calls), [.bool(false)])
        XCTAssertEqual(actions(calls).filter { $0 == "play" }, ["play"])
        await model.close()
    }

    func testNativeReleaseFailureRemainsAnImmutableFailedReceipt() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("a")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value; await model.waitForPendingWork()
        await server.beforeRelease { operation, _ in
            if operation == "session.media.release" { throw FilmPlaybackError(code: "MEDIA_RELEASE_FAILED") }
        }
        await model.close()
        XCTAssertFalse(model.isReleased)
        XCTAssertEqual(model.mediaStopError?.code, "MEDIA_RELEASE_FAILED")
        await server.beforeRelease { _, _ in }
        await model.close()
        XCTAssertFalse(model.isReleased, "An immutable native failure cannot turn into an empty success")
        XCTAssertFalse(model.canLeave)
        XCTAssertEqual(model.mediaStopError?.code, "MEDIA_RELEASE_FAILED")
        XCTAssertTrue(media.isLocallyDrained(sessionID: FilmPlaybackFixture.sessionID))
        XCTAssertEqual(media.nativeReleases, ["a"])
        let calls = await server.calls()
        XCTAssertEqual(mediaReleases(calls), [releasePayload("a")])
    }

    func testPiPKeepAndFinalSessionFailuresNeedRealAcknowledgementWithoutRepeatingMediaRelease() async throws {
        for failingKeep in [false, true] {
            let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
                attempt: FilmPlaybackFixture.attempt("a")))
            let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
            await model.open()?.value; await model.waitForPendingWork()
            media.systemOwner = true
            await server.beforeRelease { operation, input in
                if operation == "session.release", input.filmObject?["keepMedia"] == .bool(failingKeep) {
                    throw FilmPlaybackError(code: "STORAGE_ERROR")
                }
            }
            await model.applicationDidEnterBackground()
            if failingKeep {
                XCTAssertFalse(model.canLeave); XCTAssertFalse(model.isReleased)
                XCTAssertEqual(model.sessionReleaseError?.code, "STORAGE_ERROR")
                XCTAssertTrue(model.hasSystemOwner)
                let calls = await server.calls()
                XCTAssertTrue(mediaReleases(calls).isEmpty)
                await server.beforeRelease { _, _ in }
            }
            await model.close()
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
                [.bool(true), .bool(false)] : [.bool(true), .bool(false), .bool(false), .bool(false)])
            XCTAssertEqual(media.nativeReleases, ["a"])
        }
    }

    func testBackgroundClosePreservesProgressFailureAfterPhysicalDrain() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("a")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value; await model.waitForPendingWork()
        await server.fail("media.progress", with: .remote("STORAGE_ERROR"))
        await model.applicationDidEnterBackground()
        XCTAssertTrue(model.isReleased)
        XCTAssertEqual(model.actionError?.code, "STORAGE_ERROR")
        XCTAssertTrue(media.isLocallyDrained(sessionID: FilmPlaybackFixture.sessionID))
        let calls = await server.calls()
        XCTAssertEqual(mediaReleases(calls), [releasePayload("a")])
        XCTAssertEqual(sessionReleases(calls), [.bool(false)])
        XCTAssertEqual(actions(calls).filter { $0 == "media.started" }, ["media.started"])
        XCTAssertFalse(actions(calls).contains("media.ended"))
        XCTAssertFalse(calls.contains { $0.0.hasPrefix("library.") || $0.0.hasPrefix("history.") })
    }

    func testBlockForwardsTheCapturedAttemptAndDoesNotRetargetAStaleConfirmation() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "playing",
            current: FilmPlaybackFixture.current("attempt-A")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        await server.awaitObservation()
        let selected = try XCTUnwrap(model.snapshot?.current?.attemptID ?? model.snapshot?.attempt?.id)
        try await server.publish(FilmPlaybackFixture.snapshot(10, stage: "playing",
            current: FilmPlaybackFixture.current("attempt-B")))
        await server.fail("block", with: .remote("STALE_SESSION"))
        await model.send("block", parameters: ["attemptId": .string(selected)])?.value
        let calls = await server.calls().filter { $0.1.filmObject?["action"] == .string("block") }
        XCTAssertEqual(calls.count, 1)
        XCTAssertEqual(calls.first?.1.filmObject?["attemptId"], .string("attempt-A"))
        XCTAssertEqual(model.actionError?.code, "STALE_SESSION")
        XCTAssertEqual(model.snapshot?.current?.attemptID, "attempt-B")
        XCTAssertTrue(media.prepared.isEmpty)
        await model.close()
    }

    func testBlockSendsTheExactPreparedAttemptWithoutStartingMedia() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "prepared",
            attempt: FilmPlaybackFixture.attempt("prepared-A", prepareOnly: true)))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value
        let selected = try XCTUnwrap(model.snapshot?.current?.attemptID ?? model.snapshot?.attempt?.id)
        await model.send("block", parameters: ["attemptId": .string(selected)])?.value
        let calls = await server.calls()
        let block = try XCTUnwrap(calls.last { $0.1.filmObject?["action"] == .string("block") })
        XCTAssertEqual(block.1.filmObject?["attemptId"], .string("prepared-A"))
        XCTAssertEqual(actions(calls), ["block"])
        XCTAssertNil(model.actionError)
        XCTAssertTrue(media.prepared.isEmpty)
        await model.close()
    }

    private func makeModel(_ server: FilmPlaybackWireServer, _ media: FilmPlaybackTestMedia) -> FilmPlaybackModel {
        .init(transport: server.transport, media: media, request: { FilmPlaybackFixture.request() })
    }
    private func actions(_ calls: [(String, RuntimeValue)]) -> [String] {
        calls.filter { $0.0 == "session.command" }.compactMap { $0.1.filmObject?["action"]?.filmText }
    }
    private func mediaReleases(_ calls: [(String, RuntimeValue)]) -> [RuntimeValue] {
        calls.filter { $0.0 == "session.media.release" }.map(\.1)
    }
    private func releasePayload(_ attempt: String) -> RuntimeValue {
        .object(["sessionId": .string(FilmPlaybackFixture.sessionID.uuidString.lowercased()),
                 "attemptId": .string(attempt), "resourceId": .string("resource-\(attempt)")])
    }
    private func sessionReleases(_ calls: [(String, RuntimeValue)]) -> [RuntimeValue?] {
        calls.filter { $0.0 == "session.release" }.map { $0.1.filmObject?["keepMedia"] }
    }
}

private actor FilmPlaybackReentryGate {
    private var armed = false
    func arm() { armed = true }
    func waitIfArmed(entered: FilmPlaybackGate, finish: FilmPlaybackGate) async {
        guard armed else { return }
        await entered.release(); await finish.wait()
    }
}
