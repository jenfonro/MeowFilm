import Foundation
import XCTest
import Combine
@testable import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class FilmPlaybackRuntimeLifetimeTests: XCTestCase {
    func testCloseBeforeLifetimeRegistrationRevokesTheSessionWithoutStartingMedia() async throws {
        let client = try client()
        let server = FilmPlaybackWireServer(handler: { operation, _ in
            if operation == "session.open" {
                // Simulates success admission immediately before the lifetime
                // subscriber can register, not an HTTP CLOSED-as-success path.
                client.invalidate()
                return FilmPlaybackFixture.snapshot(stage: "pending", attempt: FilmPlaybackFixture.attempt("late"))
            }
            throw RuntimeHTTPError.closed
        })
        let media = FilmPlaybackTestMedia(), stopped = expectation(description: "revoked session stops")
        media.onStop = { stopped.fulfill() }
        let model = makeModel(server, media, client)
        await model.open()?.value
        await fulfillment(of: [stopped], timeout: 2)
        await model.waitForPendingWork()
        XCTAssertTrue(media.prepared.isEmpty)
        XCTAssertEqual(model.observationError?.code, "CLOSED")
        XCTAssertEqual(model.sessionReleaseError?.code, "CLOSED")
        XCTAssertFalse(model.isReleased)
        XCTAssertTrue(model.canLeave, "The confirmed dead owner has no local media to drain")
        XCTAssertNil(model.open())
        await client.close()
    }

    func testConfirmedCloseStopsDirectPlaybackAfterSSESuspensionOrDisconnect() async throws {
        for suspended in [true, false] {
            let client = try client()
            let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
                attempt: FilmPlaybackFixture.attempt("direct")))
            let media = FilmPlaybackTestMedia(), model = makeModel(server, media, client)
            await model.open()?.value
            await server.awaitObservation()
            await model.waitForPendingWork()
            if suspended { await model.suspendObservation() }
            else {
                let disconnected = expectation(description: "ordinary SSE CLOSED")
                let subscription = model.$observationError.first { $0?.code == "CLOSED" }.sink { _ in disconnected.fulfill() }
                await server.disconnect(error: .closed)
                await fulfillment(of: [disconnected], timeout: 2)
                subscription.cancel()
            }
            XCTAssertTrue(media.stopped.isEmpty)
            XCTAssertTrue(media.isCurrent(sessionID: FilmPlaybackFixture.sessionID, attemptID: "direct"))
            let stopped = expectation(description: "lifetime stops the real direct owner")
            media.onStop = { stopped.fulfill() }
            await client.close()
            await fulfillment(of: [stopped], timeout: 2)
            await model.waitForPendingWork()
            XCTAssertEqual(media.stopped, [FilmPlaybackFixture.sessionID])
            XCTAssertFalse(media.isCurrent(sessionID: FilmPlaybackFixture.sessionID, attemptID: "direct"))
            XCTAssertEqual(model.mediaStopError?.code, "CLOSED")
            XCTAssertEqual(model.sessionReleaseError?.code, "CLOSED")
            XCTAssertFalse(model.isReleased, "Physical disposal is not a successful RPC receipt")
            XCTAssertTrue(model.canLeave, "The failed ACK remains visible without trapping a drained dead page")
        }
    }

    func testOrdinarySSEClosedOrTimeoutAndRPCClosedDoNotStopMediaAndReconnectionStillWorks() async throws {
        for (error, code) in [(RuntimeHTTPError.closed, "CLOSED"), (.timeout, "TIMEOUT")] {
            let client = try client()
            let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
                attempt: FilmPlaybackFixture.attempt("live")))
            let media = FilmPlaybackTestMedia(), model = makeModel(server, media, client)
            await model.open()?.value
            await server.awaitObservation()
            await model.waitForPendingWork()
            await server.fail("quality", with: .closed)
            await model.send("quality", parameters: ["quality": .string("uhd")])?.value
            XCTAssertEqual(model.actionError?.code, "CLOSED")
            let disconnected = expectation(description: "SSE reports ordinary \(code)")
            let subscription = model.$observationError.first { $0?.code == code }.sink { _ in disconnected.fulfill() }
            await server.disconnect(error: error)
            await fulfillment(of: [disconnected], timeout: 2)
            subscription.cancel()
            XCTAssertFalse(client.isClosed)
            XCTAssertTrue(media.stopped.isEmpty)
            XCTAssertTrue(media.isCurrent(sessionID: FilmPlaybackFixture.sessionID, attemptID: "live"))
            model.reconnect()
            await server.awaitObservation(2)
            await model.close()
            XCTAssertTrue(model.isReleased)
            XCTAssertNil(model.mediaStopError)
            XCTAssertNil(model.sessionReleaseError)
            XCTAssertFalse(client.isClosed, "Cancelling the Film lifetime wait cannot close the shared client")
            await client.close()
        }
    }

    func testRetainedPiPStillObservesRevocationAndCannotBeRetainedAgain() async throws {
        let client = try client()
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("pip")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media, client)
        await model.open()?.value
        await model.waitForPendingWork()
        media.systemOwner = true
        await model.close(allowSystemOwner: true)
        XCTAssertTrue(model.hasSystemOwner)
        XCTAssertTrue(media.stopped.isEmpty)
        let stopped = expectation(description: "revocation ends retained PiP")
        media.onStop = { stopped.fulfill() }
        await client.close()
        await fulfillment(of: [stopped], timeout: 2)
        await model.waitForPendingWork()
        XCTAssertFalse(media.systemOwner)
        XCTAssertFalse(model.hasSystemOwner)
        XCTAssertFalse(model.navigateFromSystem(forward: true))
        XCTAssertNil(model.send("play"))
        XCTAssertFalse(model.isReleased)
        XCTAssertTrue(model.canLeave)
        XCTAssertEqual(model.sessionReleaseError?.code, "CLOSED")
    }

    func testRevocationDuringRetainedCloseFencesSystemCommandsBeforeTheOldCloseDrains() async throws {
        let client = try client(), retained = FilmPlaybackGate(), entered = FilmPlaybackGate()
        let server = FilmPlaybackWireServer(handler: { operation, input in
            switch operation {
            case "session.open":
                return FilmPlaybackFixture.snapshot(stage: "pending", attempt: FilmPlaybackFixture.attempt("pip"))
            case "session.attempt": return FilmPlaybackFixture.mediaInput("pip")
            case "session.release" where input.filmObject?["keepMedia"] == .bool(true):
                await entered.release(); await retained.wait()
                return .object(["retained": .bool(true)])
            case "session.release", "session.media.release": return .object(["released": .bool(true)])
            default: return FilmPlaybackFixture.snapshot(2, stage: "playing", current: FilmPlaybackFixture.current("pip"))
            }
        })
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media, client)
        await model.open()?.value
        await model.waitForPendingWork()
        media.systemOwner = true
        let background = Task { await model.close(allowSystemOwner: true) }
        await entered.wait()
        let revoked = expectation(description: "runtime revocation is admitted before old retained close finishes")
        let subscription = model.$observationError.first { $0?.code == "CLOSED" }.sink { _ in revoked.fulfill() }
        await client.close()
        await fulfillment(of: [revoked], timeout: 2)
        subscription.cancel()
        XCTAssertFalse(model.navigateFromSystem(forward: true))
        XCTAssertNil(model.open())
        XCTAssertTrue(media.stopped.isEmpty, "The old admitted close is still being drained")
        let stopped = expectation(description: "non-retaining close follows the admitted close")
        media.onStop = { stopped.fulfill() }
        await retained.release()
        await background.value
        await fulfillment(of: [stopped], timeout: 2)
        await model.waitForPendingWork()
        XCTAssertFalse(media.systemOwner)
        XCTAssertEqual(model.sessionReleaseError?.code, "CLOSED")
    }

    func testRevocationCancelsAndDrainsCandidateWithoutAllowingLateOutputCommit() async throws {
        let client = try client(), candidate = FilmPlaybackGate(), entered = FilmPlaybackGate()
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("candidate")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media, client)
        media.preparationGate = candidate; media.preparationEntered = entered
        await model.open()?.value
        await entered.wait()
        let revoked = expectation(description: "candidate authorization revoked")
        let subscription = model.$observationError.first { $0?.code == "CLOSED" }.sink { _ in revoked.fulfill() }
        await client.close()
        await fulfillment(of: [revoked], timeout: 2)
        subscription.cancel()
        XCTAssertTrue(media.stopped.isEmpty, "Close must wait for the real admitted candidate drain")
        XCTAssertFalse(model.canLeave, "A confirmed runtime close cannot skip an admitted native drain")
        XCTAssertNil(model.send("play"))
        await candidate.release()
        await model.waitForPendingWork()
        media.emit(.started(attemptID: "candidate"), attemptID: "candidate")
        XCTAssertTrue(media.committed.isEmpty)
        XCTAssertEqual(media.cancelled, ["candidate"])
        XCTAssertNil(model.mediaState.attemptID)
        XCTAssertEqual(media.stopped, [FilmPlaybackFixture.sessionID])
        XCTAssertFalse(model.isReleased)
        XCTAssertTrue(model.canLeave)
        let calls = await server.calls()
        XCTAssertFalse(calls.contains { $0.1.filmObject?["action"] == .string("media.started") })
    }

    func testOldRuntimeRevocationDrainsOnlyItsSessionAndCannotStopTheNewOwner() async throws {
        let oldClient = try client(), newClient = try client()
        let oldServer = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("old")))
        let newID = UUID(uuidString: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")!
        var newFields = try FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("new")).filmRequiredObject()
        newFields["sessionId"] = .string(newID.uuidString.lowercased())
        let newServer = FilmPlaybackWireServer(.object(newFields))
        let media = FilmPlaybackTestMedia()
        let oldModel = makeModel(oldServer, media, oldClient)
        await oldModel.open()?.value
        await oldModel.waitForPendingWork()
        let newModel = makeModel(newServer, media, newClient)
        await newModel.open()?.value
        await newModel.waitForPendingWork()
        let stopped = expectation(description: "old session drain does not seize current owner")
        media.onStop = { stopped.fulfill() }
        await oldClient.close()
        await fulfillment(of: [stopped], timeout: 2)
        await oldModel.waitForPendingWork()
        XCTAssertEqual(media.stopped, [FilmPlaybackFixture.sessionID])
        XCTAssertTrue(media.isCurrent(sessionID: newID, attemptID: "new"))
        XCTAssertEqual(newModel.mediaState.attemptID, "new")
        XCTAssertEqual(media.pauses, 0)
        XCTAssertFalse(newClient.isClosed)
        media.onStop = nil
        await newModel.close()
        await newClient.close()
    }

    func testRevocationDrainsAReplacementCandidateAndTheExistingOutput() async throws {
        let client = try client(), candidate = FilmPlaybackGate(), entered = FilmPlaybackGate()
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("current")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media, client)
        await model.open()?.value
        await server.awaitObservation()
        await model.waitForPendingWork()
        media.preparationGate = candidate; media.preparationEntered = entered
        try await server.publish(FilmPlaybackFixture.snapshot(100, stage: "pending",
            attempt: FilmPlaybackFixture.attempt("replacement"), current: FilmPlaybackFixture.current("current")))
        await entered.wait()
        let revoked = expectation(description: "both current and candidate lose runtime authorization")
        let subscription = model.$observationError.first { $0?.code == "CLOSED" }.sink { _ in revoked.fulfill() }
        await client.close()
        await fulfillment(of: [revoked], timeout: 2)
        subscription.cancel()
        await candidate.release()
        await model.waitForPendingWork()
        XCTAssertEqual(media.committed, ["current"])
        XCTAssertEqual(media.cancelled, ["replacement"])
        XCTAssertFalse(media.isCurrent(sessionID: FilmPlaybackFixture.sessionID, attemptID: "current"))
        XCTAssertEqual(media.stopped, [FilmPlaybackFixture.sessionID])
        XCTAssertFalse(model.isReleased)
        XCTAssertTrue(model.canLeave)
        XCTAssertEqual(model.sessionReleaseError?.code, "CLOSED")
    }

    func testCloseWaitsForDrainAndKeepsNativeStopAndRPCReleaseFailuresSeparatelyVisible() async throws {
        let client = try client(), drain = FilmPlaybackGate(), entered = FilmPlaybackGate()
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("output")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media, client)
        await model.open()?.value
        await model.waitForPendingWork()
        media.stopGate = drain; media.stopEntered = entered
        media.stopFailure = .init(code: "MEDIA_OUTPUT_FAILED")
        await client.close()
        await entered.wait()
        XCTAssertFalse(model.isReleased)
        XCTAssertFalse(model.canLeave)
        XCTAssertNil(model.sessionReleaseError, "Session release cannot precede the admitted native drain")
        await drain.release()
        await model.waitForPendingWork()
        XCTAssertEqual(model.mediaStopError?.code, "MEDIA_OUTPUT_FAILED")
        XCTAssertEqual(model.sessionReleaseError?.code, "CLOSED")
        XCTAssertEqual(model.actionError?.code, "MEDIA_OUTPUT_FAILED")
        XCTAssertFalse(model.isReleased)
        XCTAssertFalse(model.canLeave, "A failed native stop left actual local output undrained")
        model.pauseOrResume()
        model.seek(to: 120)
        model.setRate(2)
        model.startPictureInPicture()
        XCTAssertEqual(media.pauses, 0)
        XCTAssertTrue(media.seeks.isEmpty)
        XCTAssertTrue(media.rates.isEmpty)
        XCTAssertFalse(media.systemOwner)
    }

    func testFailedOrdinaryCloseKeepsLifetimeUntilTheUndrainedOwnerIsRevoked() async throws {
        let client = try client()
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("undrained")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media, client)
        await model.open()?.value
        await model.waitForPendingWork()
        media.stopFailure = .init(code: "MEDIA_OUTPUT_FAILED")
        await model.close()
        XCTAssertFalse(model.isReleased)
        XCTAssertFalse(model.canLeave, "Ordinary close failure is not proof the runtime died")
        XCTAssertTrue(media.isCurrent(sessionID: FilmPlaybackFixture.sessionID, attemptID: "undrained"))
        media.stopFailure = nil
        let stopped = expectation(description: "runtime still revokes a previously failed close")
        media.onStop = { stopped.fulfill() }
        await client.close()
        await fulfillment(of: [stopped], timeout: 2)
        await model.waitForPendingWork()
        XCTAssertFalse(media.isCurrent(sessionID: FilmPlaybackFixture.sessionID, attemptID: "undrained"))
        XCTAssertEqual(model.observationError?.code, "CLOSED")
        XCTAssertNil(model.open())
    }

    func testMissingSessionReleaseReceiptRemainsFailureAfterPhysicalStop() async throws {
        let client = try client()
        let server = FilmPlaybackWireServer(handler: { operation, _ in
            if operation == "session.release" { return .object([:]) }
            return FilmPlaybackFixture.snapshot()
        })
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media, client)
        await model.open()?.value
        await model.close()
        XCTAssertEqual(media.stopped, [FilmPlaybackFixture.sessionID])
        XCTAssertNil(model.mediaStopError)
        XCTAssertEqual(model.sessionReleaseError?.code, "INVALID_RESPONSE")
        XCTAssertFalse(model.isReleased)
        XCTAssertFalse(client.isClosed)
        await model.close()
        XCTAssertFalse(model.isReleased, "Retrying close cannot invent the missing release receipt")
        XCTAssertFalse(model.canLeave, "Missing ordinary ACK does not grant terminal navigation")
        XCTAssertEqual(model.sessionReleaseError?.code, "INVALID_RESPONSE")
        let revoked = expectation(description: "failed receipt keeps lifetime until revocation")
        let subscription = model.$observationError.first { $0?.code == "CLOSED" }.sink { _ in revoked.fulfill() }
        await client.close()
        await fulfillment(of: [revoked], timeout: 2)
        subscription.cancel()
        await model.waitForPendingWork()
        XCTAssertTrue(model.canLeave, "Only confirmed revocation and a completed local drain may allow exit")
    }

    private func client() throws -> RuntimeHTTPClient {
        try RuntimeHTTPClient(baseURL: URL(string: "http://127.0.0.1:43210")!)
    }

    private func makeModel(_ server: FilmPlaybackWireServer, _ media: FilmPlaybackTestMedia,
                           _ client: RuntimeHTTPClient) -> FilmPlaybackModel {
        .init(transport: server.transport(runtime: client), media: media,
              request: { FilmPlaybackFixture.request() })
    }
}
