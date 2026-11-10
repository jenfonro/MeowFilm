import XCTest
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class FilmPlaybackBlockAdmissionTests: XCTestCase {
    func testDurableBlockDecisionRetainsTheSameCandidateUntilAcceptedOrRejected() async throws {
        for outcome in ["STALE_SESSION", "STORAGE_ERROR", "accepted"] {
            let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
                attempt: FilmPlaybackFixture.attempt("A")))
            let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
            await model.open()?.value
            await server.awaitObservation()
            await model.waitForPendingWork()
            var donor = FilmMediaState()
            donor.sessionID = FilmPlaybackFixture.sessionID; donor.attemptID = "A"
            donor.position = 137; donor.duration = 600; donor.paused = true
            media.emit(.state(donor), attemptID: "A")
            await model.waitForPendingWork()

            let prepared = FilmPlaybackGate(), preparing = FilmPlaybackGate()
            let requested = FilmPlaybackGate(), decision = FilmPlaybackGate()
            media.preparationGate = prepared; media.preparationEntered = preparing
            await server.beforeAction { action in
                if action == "block" { await requested.release(); await decision.wait() }
            }
            if outcome == "accepted" {
                await server.reply(to: "block", with: FilmPlaybackFixture.snapshot(200, stage: "pending",
                    attempt: FilmPlaybackFixture.attempt("C"), current: FilmPlaybackFixture.current("A")))
            } else { await server.fail("block", with: .remote(outcome)) }
            addTeardownBlock {
                await prepared.release(); await decision.release()
                await model.close()
            }
            try await server.publish(FilmPlaybackFixture.snapshot(100, stage: "pending",
                attempt: FilmPlaybackFixture.attempt("B"), current: FilmPlaybackFixture.current("A")))
            await preparing.wait()
            let pendingCancellations = media.pendingCancellations.count
            let command = model.send("block", parameters: ["attemptId": .string("A")])
            await requested.wait()
            XCTAssertEqual(media.pendingCancellations.count, pendingCancellations)
            let ready = expectation(description: "B is ready while \(outcome) is undecided")
            media.onAwaitingCommit = { ready.fulfill() }
            await prepared.release()
            await fulfillment(of: [ready], timeout: 2)
            media.onAwaitingCommit = nil
            XCTAssertEqual(media.committed, ["A"])
            XCTAssertTrue(media.cancelled.isEmpty)
            XCTAssertEqual(model.mediaState.attemptID, "A")
            XCTAssertEqual(model.mediaState.position, 137)
            XCTAssertTrue(model.mediaState.paused)
            let during = await server.calls()
            XCTAssertFalse(during.contains { $0.0 == "session.media.release" &&
                $0.1.filmObject?["attemptId"] == .string("B") })

            await decision.release()
            await command?.value
            await model.waitForPendingWork()
            let winner = outcome == "accepted" ? "C" : "B"
            XCTAssertEqual(media.committed, ["A", winner])
            XCTAssertEqual(model.mediaState.attemptID, winner)
            XCTAssertEqual(model.mediaState.position, 137)
            XCTAssertTrue(model.mediaState.paused)
            XCTAssertEqual(media.cancelled, outcome == "accepted" ? ["B"] : [])
            XCTAssertEqual(model.actionError?.code, outcome == "accepted" ? nil : outcome)
            let calls = await server.calls()
            XCTAssertEqual(calls.filter { $0.0 == "session.attempt" &&
                $0.1.filmObject?["attemptId"] == .string("B") }.count, 1, "Never reacquire the preserved receipt")
            XCTAssertFalse(calls.contains { $0.1.filmObject?["action"] == .string("media.failed") })
            XCTAssertTrue(media.stopped.isEmpty)
            await model.close()
        }
    }

    func testASecondBlockStillHoldsPublicationAfterTheFirstDecisionReturns() async throws {
        let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
            attempt: FilmPlaybackFixture.attempt("A")))
        let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
        await model.open()?.value; await server.awaitObservation(); await model.waitForPendingWork()
        let prepared = FilmPlaybackGate(), preparing = FilmPlaybackGate()
        let decisions = BlockDecisions()
        media.preparationGate = prepared; media.preparationEntered = preparing
        await server.beforeAction { action in if action == "block" { await decisions.wait() } }
        await server.fail("block", with: .remote("STORAGE_ERROR"))
        addTeardownBlock {
            await prepared.release(); await decisions.first.release(); await decisions.second.release()
            await model.close()
        }
        try await server.publish(FilmPlaybackFixture.snapshot(100, stage: "pending",
            attempt: FilmPlaybackFixture.attempt("B"), current: FilmPlaybackFixture.current("A")))
        await preparing.wait()
        let first = model.send("block", parameters: ["attemptId": .string("A")])
        await decisions.firstEntered.wait()
        let second = model.send("block", parameters: ["attemptId": .string("A")])
        let ready = expectation(description: "candidate waits behind both decisions")
        media.onAwaitingCommit = { ready.fulfill() }
        await prepared.release(); await fulfillment(of: [ready], timeout: 2)
        media.onAwaitingCommit = nil
        await decisions.first.release(); await decisions.secondEntered.wait()
        XCTAssertEqual(media.committed, ["A"])
        XCTAssertTrue(media.cancelled.isEmpty)
        await decisions.second.release(); await first?.value; await second?.value
        await model.waitForPendingWork()
        XCTAssertEqual(media.committed, ["A", "B"])
        XCTAssertTrue(media.cancelled.isEmpty)
        await model.close()
    }

    func testStopAndCloseWakeTheCandidateInsteadOfWaitingOnTheirOwnCommandWorker() async throws {
        for close in [false, true] {
            let server = FilmPlaybackWireServer(FilmPlaybackFixture.snapshot(stage: "pending",
                attempt: FilmPlaybackFixture.attempt("B")))
            let media = FilmPlaybackTestMedia(), model = makeModel(server, media)
            let prepared = FilmPlaybackGate(), preparing = FilmPlaybackGate()
            let decision = FilmPlaybackGate(), requested = FilmPlaybackGate()
            media.preparationGate = prepared; media.preparationEntered = preparing
            await server.beforeAction { action in
                if action == "block" { await requested.release(); await decision.wait() }
            }
            await server.fail("block", with: .remote("STORAGE_ERROR"))
            addTeardownBlock { await prepared.release(); await decision.release(); await model.close() }
            await model.open()?.value; await preparing.wait()
            let block = model.send("block", parameters: ["attemptId": .string("B")])
            await requested.wait()
            let ready = expectation(description: "ready B waits for the business decision")
            media.onAwaitingCommit = { ready.fulfill() }
            await prepared.release(); await fulfillment(of: [ready], timeout: 2)
            media.onAwaitingCommit = nil
            let cancelling = expectation(description: "ending intent revokes publication before decision completion")
            media.onPendingCancellation = { cancelling.fulfill() }
            let ending: Task<Void, Never>?
            if close { ending = Task { await model.close() } }
            else { ending = model.send("stop") }
            await fulfillment(of: [cancelling], timeout: 2)
            media.onPendingCancellation = nil
            await decision.release(); await block?.value; await ending?.value
            await model.waitForPendingWork()
            XCTAssertTrue(media.committed.isEmpty)
            XCTAssertEqual(media.cancelled, ["B"])
            let calls = await server.calls()
            XCTAssertFalse(calls.contains { $0.1.filmObject?["action"] == .string("media.started") ||
                $0.1.filmObject?["action"] == .string("media.failed") })
            await model.close()
        }
    }

    private func makeModel(_ server: FilmPlaybackWireServer, _ media: FilmPlaybackTestMedia) -> FilmPlaybackModel {
        .init(transport: server.transport, media: media, openExternalURL: { _ in false },
              request: { FilmPlaybackFixture.request() })
    }
}

private actor BlockDecisions {
    let first = FilmPlaybackGate(), second = FilmPlaybackGate()
    let firstEntered = FilmPlaybackGate(), secondEntered = FilmPlaybackGate()
    private var count = 0
    func wait() async {
        count += 1
        if count == 1 { await firstEntered.release(); await first.wait() }
        else { await secondEntered.release(); await second.wait() }
    }
}
