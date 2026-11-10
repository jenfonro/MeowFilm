import XCTest
import UIKit
import Combine
import Network
import CryptoKit
import Darwin
import MeowRuntime
@testable import MeowFilmFeature
@testable import Meowfilm

private let filmHostProcessNonce = UUID().uuidString.lowercased()

/// Terminal cases must run alone, followed by cleanup in another App process.
@MainActor
final class MeowfilmFilmShutdownHostTests: XCTestCase {
    func testActualRuntimeOwnerExitStopsTheDirectDonorAndDrainsTheUncommittedCandidate() async throws {
        let run = try FilmLifecycleHost.requirePhase("MEOWFILM_FILM_SHUTDOWN_PHASE", "close")
        try FilmLifecycleHost.requireNoCheckpoint("shutdown")
        let resources = try await hostResources()
        let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
        let assembly = try XCTUnwrap(app.readyAssembly())
        let client = try await resources.scriptSession.connection()
        addTeardownBlock {
            try await self.finishAssembly(assembly, client: client,
                expectsFailure: assembly.filmPlayback?.isReleased == false)
        }
        let movie = try XCTUnwrap(Bundle(for: Self.self).url(forResource: "ordinary", withExtension: "mp4"))
        let upstream = try FilmLifecycleMovieServer(movie: Data(contentsOf: movie))
        let fixture = FilmPlaybackHostFixture(assembly: assembly, client: client, movie: movie,
                                             id: "film-shutdown-" + run)
        var checkpoint = try await FilmLifecycleHost.checkpoint(run: run, fixture: fixture, assembly: assembly)
        try FilmLifecycleHost.write(checkpoint, kind: "shutdown")
        addTeardownBlock {
            defer { upstream.stop() }
            var failure: Error?
            var cleaned = false, exited = false
            do {
                if client.isClosed { try await fixture.removePresentation() }
                else { try await fixture.close(); cleaned = true }
            } catch { failure = error }
            do {
                try await self.finishAssembly(assembly, client: client,
                    expectsFailure: assembly.filmPlayback?.isReleased == false)
                exited = true
            } catch { failure = failure ?? error }
            if exited {
                checkpoint["executorExitConfirmed"] = .bool(true)
                if cleaned { try await FilmLifecycleHost.removeCheckpoint("shutdown") }
                else { try await FilmLifecycleHost.write(checkpoint, kind: "shutdown") }
            }
            if let failure { throw failure }
        }
        try await upstream.start()
        try await fixture.install(autoPrepare: true, directMediaBase: upstream.base, signMediaURL: true)
        let model = try await fixture.makeModel()
        await model.open()?.value
        try await lifecycleWait {
            model.snapshot?.stage == "prepared" && model.snapshot?.attempt?.prepareOnly == true
        }
        try await fixture.mountSurface(model)
        let surface = try XCTUnwrap(model.makeVideoView())
        var nativeCommits: [UUID] = []
        let commits = assembly.playback.$currentAttempt.removeDuplicates().compactMap { $0 }.sink {
            nativeCommits.append($0.id)
        }
        defer { commits.cancel() }
        await model.send("play")?.value
        try await lifecycleWait {
            model.mediaState.attemptID != nil && model.mediaState.position > 0.1 &&
                model.snapshot?.current?.attemptID == model.mediaState.attemptID &&
                assembly.playback.wantsPlayback
        }
        let donor = try XCTUnwrap(assembly.playback.currentAttempt)
        let donorID = try XCTUnwrap(model.mediaState.attemptID)
        let sessionID = try XCTUnwrap(model.sessionID)
        model.pauseOrResume()
        try await lifecycleWait { model.mediaState.paused && !assembly.playback.wantsPlayback }
        let receipt = try await client.call(module: .film, operation: "session.attempt", parameters: .object([
            "sessionId": .string(sessionID.uuidString.lowercased()), "attemptId": .string(donorID),
        ]))
        let url = try XCTUnwrap(receipt.filmObject?["url"]?.filmText.flatMap(URL.init(string:)))
        XCTAssertEqual(url.host, upstream.base.host)
        XCTAssertEqual(url.port, upstream.base.port)
        XCTAssertEqual(url.path, "/one.mp4")
        XCTAssertEqual(url.query, "signature=film-host-synthetic-signature")
        XCTAssertTrue(receipt.filmObject?["mediaOrigin"] == nil || receipt.filmObject?["mediaOrigin"] == .null)
        XCTAssertTrue(receipt.filmObject?["headers"] == nil || receipt.filmObject?["headers"] == .object([:]))
        XCTAssertTrue(receipt.filmObject?["drm"] == nil || receipt.filmObject?["drm"] == .null)
        XCTAssertGreaterThan(upstream.movieGETs, 0)
        XCTAssertFalse(model.hasSystemOwner)
        let next = try XCTUnwrap(model.snapshot?.candidates.first {
            $0.lineLabel == "备用原始线路" && $0.position.episode == 1
        })
        model.selectCandidate(next.id)
        try await lifecycleWait {
            model.snapshot?.attempt?.candidate.id == next.id &&
                model.snapshot?.attempt?.prepareOnly == false && upstream.heldGETs > 0
        }
        let pendingID = try XCTUnwrap(model.snapshot?.attempt?.id)
        XCTAssertNotEqual(pendingID, donorID)
        XCTAssertEqual(assembly.playback.currentAttempt, donor)
        XCTAssertEqual(model.snapshot?.current?.attemptID, donorID)
        XCTAssertEqual(upstream.unexpectedHeaders, 0, "A direct input receives no runtime key or source headers")
        await model.suspendObservation()
        XCTAssertFalse(client.isClosed)
        XCTAssertNil(model.observationError)
        XCTAssertEqual(assembly.playback.currentAttempt, donor)
        XCTAssertEqual(nativeCommits, [donor.id])
        XCTAssertGreaterThan(upstream.heldGETs, 0, "Stopping SSE observation alone does not revoke native media")
        // Restore global settings while the owner is alive. The candidate is
        // already inside real native I/O; this is not a synthetic closure seam.
        try await fixture.restorePreferences()
        _ = try await client.call(module: .film, operation: "library.remove", parameters: .object([
            "kind": .string("history"), "id": .string(fixture.targetID),
        ]))
        XCTAssertEqual(assembly.playback.currentAttempt, donor)
        XCTAssertGreaterThan(upstream.heldGETs, 0)
        XCTAssertEqual(upstream.disconnectedHeldGETs, 0, "The pending I/O must still be alive at actual owner close")
        let exited = expectation(description: "RuntimeSession.close confirms embedded Node executor exit")
        var executorExited = false
        let closing = Task {
            await resources.scriptSession.close()
            executorExited = true; exited.fulfill()
        }
        await fulfillment(of: [exited], timeout: 20)
        guard executorExited else { throw FilmPlaybackError(code: "HOST_RUNTIME_EXIT_TIMEOUT") }
        await closing.value
        XCTAssertTrue(client.isClosed)
        try await lifecycleWait {
            model.observationError?.code == "CLOSED" && model.mediaStopError != nil &&
                model.sessionReleaseError != nil && assembly.playback.currentAttempt == nil &&
                upstream.disconnectedHeldGETs > 0
        }
        await model.waitForPendingWork()
        XCTAssertTrue(model.canLeave)
        try await assembly.leaveFilm(allowSystemOwner: false)
        XCTAssertEqual(nativeCommits, [donor.id], "The pending native input never commits after owner revocation")
        XCTAssertNil(assembly.playback.currentAttempt)
        XCTAssertFalse(assembly.playback.wantsPlayback)
        XCTAssertFalse(model.hasSystemOwner)
        XCTAssertFalse(model.mediaState.pictureInPicture)
        XCTAssertTrue(surface.layer.sublayers?.isEmpty != false, "Both active and staged output layers physically leave")
        XCTAssertEqual(upstream.heldGETs, 0)
        XCTAssertEqual(model.mediaStopError?.code, "MEDIA_RELEASE_FAILED")
        XCTAssertEqual(model.sessionReleaseError?.code, "CLOSED")
        XCTAssertFalse(model.isReleased, "A physically stopped decoder is not a successful receipt/session acknowledgement")
        XCTAssertNil(model.open())
        XCTAssertNil(model.send("play"), "This model cannot resurrect the revoked epoch")
        try await finishAssembly(assembly, client: client, expectsFailure: true)
        checkpoint["executorExitConfirmed"] = .bool(true)
        try FilmLifecycleHost.write(checkpoint, kind: "shutdown")
    }
}

@MainActor
final class MeowfilmFilmShutdownCleanupHostTests: XCTestCase {
    func testNextAppProcessRemovesOnlyTheTerminalFixturesOwnRecords() async throws {
        let run = try FilmLifecycleHost.requirePhase("MEOWFILM_FILM_SHUTDOWN_PHASE", "cleanup")
        let checkpoint = try FilmLifecycleHost.read("shutdown", run: run)
        let resources = try await hostResources()
        let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
        let assembly = try XCTUnwrap(app.readyAssembly())
        let client = try await resources.scriptSession.connection()
        addTeardownBlock { try await self.finishAssembly(assembly, client: client) }
        XCTAssertEqual(checkpoint["executorExitConfirmed"], .bool(true), "The terminal phase must have confirmed its real executor exit")
        let before = try await FilmLifecycleHost.library(client)
        try FilmLifecycleHost.assertPreserved(before, checkpoint: checkpoint, library: assembly.filmLibrary)
        XCTAssertEqual(before["preferences"], checkpoint["originalSettings"])
        try await FilmLifecycleHost.cleanup(checkpoint, client: client, assembly: assembly)
        let after = try await FilmLifecycleHost.library(client)
        XCTAssertEqual(after["preferences"], checkpoint["originalSettings"])
        XCTAssertEqual(try FilmLifecycleHost.owned(after, checkpoint: checkpoint),
                       ["history": .array([]), "manual": .array([]), "blocks": .array([])])
        try FilmLifecycleHost.assertPreserved(after, checkpoint: checkpoint, library: assembly.filmLibrary)
        try await finishAssembly(assembly, client: client)
        try FilmLifecycleHost.removeCheckpoint("shutdown")
    }
}

@MainActor
final class MeowfilmFilmColdStartPrepareTests: XCTestCase {
    func testRealOutputAndFormalAPIsSavePlaybackDocumentsForTheNextAppProcess() async throws {
        let run = try FilmLifecycleHost.requirePhase("MEOWFILM_FILM_COLD_PHASE", "prepare")
        try FilmLifecycleHost.requireNoCheckpoint("cold")
        let resources = try await hostResources()
        let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
        let assembly = try XCTUnwrap(app.readyAssembly())
        let client = try await resources.scriptSession.connection()
        addTeardownBlock { try await self.finishAssembly(assembly, client: client) }
        let movie = try XCTUnwrap(Bundle(for: Self.self).url(forResource: "ordinary", withExtension: "mp4"))
        let fixture = FilmPlaybackHostFixture(assembly: assembly, client: client, movie: movie,
                                             id: "film-cold-" + run)
        var checkpoint = try await FilmLifecycleHost.checkpoint(run: run, fixture: fixture, assembly: assembly)
        try FilmLifecycleHost.write(checkpoint, kind: "cold")
        var preserveForRestore = false
        addTeardownBlock {
            var failure: Error?
            var cleaned = false, exited = false
            if !preserveForRestore, !client.isClosed {
                // Stop real feedback before deleting its history; otherwise a
                // teardown progress event could recreate the just-removed row.
                do { try await fixture.close(preservingLibrary: true) }
                catch { failure = error }
                do { try await FilmLifecycleHost.cleanup(checkpoint, client: client, assembly: assembly) }
                catch { failure = failure ?? error }
                cleaned = failure == nil
            }
            do {
                try await self.finishFixture(fixture, assembly: assembly, client: client, preservingLibrary: true)
                exited = true
            }
            catch { failure = failure ?? error }
            if exited {
                checkpoint["executorExitConfirmed"] = .bool(true)
                if cleaned { try await FilmLifecycleHost.removeCheckpoint("cold") }
                else { try await FilmLifecycleHost.write(checkpoint, kind: "cold") }
            }
            if let failure { throw failure }
        }
        try await fixture.install(autoPrepare: true, signMediaURL: true)
        let model = try await fixture.makeModel()
        await model.open()?.value
        try await lifecycleWait {
            model.snapshot?.stage == "prepared" && model.snapshot?.attempt?.prepareOnly == true
        }
        let unplayed = try await fixture.history()
        XCTAssertTrue(unplayed.isEmpty)
        XCTAssertNil(assembly.playback.currentAttempt)
        try await fixture.mountSurface(model)
        await model.send("play")?.value
        try await lifecycleWait {
            model.mediaState.attemptID != nil && model.mediaState.position > 0.1 &&
                model.snapshot?.current?.attemptID == model.mediaState.attemptID
        }
        let played = try XCTUnwrap(model.snapshot?.current)
        XCTAssertEqual(assembly.playback.currentAttempt?.owner, "film")
        XCTAssertNil(assembly.playback.player.currentItem)
        model.pauseOrResume()
        try await lifecycleWait { model.mediaState.paused }
        model.seek(to: 1.1)
        try await lifecycleWait { model.mediaState.paused && abs(model.mediaState.position - 1.1) < 0.15 }
        await model.waitForPendingWork()
        let sourceState = try await fixture.sourceState(waitFor: [
            "report": .object(["event": .string("started"), "id": .string("one"), "flag": .string("原始线路")]),
        ])
        XCTAssertGreaterThan(try XCTUnwrap(sourceState["mediaReads"]?.filmNumber), 0)
        XCTAssertEqual(sourceState["missingCookie"], .number(0))
        await model.close()
        XCTAssertTrue(model.isReleased)
        XCTAssertNil(assembly.playback.currentAttempt)
        let history = try await fixture.history()
        XCTAssertEqual(history.count, 1)
        let row = try XCTUnwrap(history.first?.filmObject)
        XCTAssertEqual(row["candidateId"], .string(played.candidate.id))
        XCTAssertEqual(row["sourceKey"], played.candidate.source.value["key"])
        XCTAssertEqual(row["sourceKey"], .string(fixture.id))
        XCTAssertEqual(row["detailId"], played.candidate.source.detailID)
        XCTAssertEqual(row["detailId"], .number(0))
        XCTAssertEqual(row["lineId"], played.candidate.lineID.value)
        XCTAssertEqual(row["episode"], .object(["season": .number(1), "episode": .number(1), "globalEpisode": .number(1)]))
        XCTAssertEqual(try XCTUnwrap(row["position"]?.filmNumber), 1.1, accuracy: 0.15)
        XCTAssertEqual(row["paused"], .bool(true))
        // Block starts a formal reselection. This real business session has no
        // native subscriber, so it cannot add another start/history behind the
        // persistence checkpoint. It uses the same production wire and seeds.
        let blockRequest = try await fixture.makeRequest()
        let opened = try await client.call(module: .film, operation: "session.open", parameters: blockRequest.value)
        let openedSnapshot = try FilmPlaybackSnapshot(value: opened)
        let blockSessionID = openedSnapshot.sessionID.uuidString.lowercased()
        var blockReleased = false
        addTeardownBlock {
            if !blockReleased, !client.isClosed {
                let receipt = try await client.call(module: .film, operation: "session.release",
                    parameters: .object(["sessionId": .string(blockSessionID), "keepMedia": .bool(false)]))
                XCTAssertEqual(receipt.filmObject?["released"], .bool(true))
            }
        }
        let events = try client.events(moduleID: "film", sessionID: openedSnapshot.sessionID, after: openedSnapshot.sequence)
        var blockSnapshot = openedSnapshot
        var blockObservationError: Error?
        let observing = Task {
            do {
                for try await value in events { blockSnapshot = try FilmPlaybackSnapshot(value: value) }
            } catch { if !Task.isCancelled { blockObservationError = error } }
        }
        addTeardownBlock { observing.cancel(); await events.cancel(); await observing.value }
        try await lifecycleWait {
            blockSnapshot.stage == "prepared" && blockSnapshot.attempt?.prepareOnly == true
        }
        XCTAssertNil(blockObservationError)
        let selected = try XCTUnwrap(blockSnapshot.attempt)
        observing.cancel(); await events.cancel(); await observing.value
        _ = try await client.call(module: .film, operation: "session.command", parameters: .object([
            "sessionId": .string(blockSessionID), "action": .string("block"), "attemptId": .string(selected.id),
        ]))
        let blockRelease = try await client.call(module: .film, operation: "session.release",
            parameters: .object(["sessionId": .string(blockSessionID), "keepMedia": .bool(false)]))
        XCTAssertEqual(blockRelease.filmObject?["released"], .bool(true))
        blockReleased = blockRelease.filmObject?["released"] == .bool(true)
        XCTAssertNil(assembly.playback.currentAttempt)
        let afterBlock = try await fixture.history()
        XCTAssertEqual(afterBlock, history)
        let block: RuntimeValue = .object([
            "targetId": .string(fixture.targetID), "sourceKey": try XCTUnwrap(selected.candidate.source.value["key"]),
            "detailId": try XCTUnwrap(selected.candidate.source.detailID), "lineId": selected.candidate.lineID.value,
        ])
        let changed: RuntimeValue = .object([
            "ruleOrder": .array([.string("keyword"), .string("pan"), .string("quality")]),
            "panOrder": .array([.string("baidu"), .string("uc"), .string("quark"), .string("tianyi"), .string("yidong")]),
            "keywords": .array([.string("1080p"), .string("原始")]),
            "siteCleanKeywords": .array([.string("合成净化词")]), "iosAutoPrepare": .bool(false),
        ])
        let saved = try await client.call(module: .film, operation: "settings.set", parameters: changed)
        XCTAssertEqual(saved, changed)
        let manual: [RuntimeValue] = [
            .object(["id": .string(run + "-site"), "targetId": .string(fixture.targetID), "kind": .string("site"),
                "name": .string("Synthetic site mapping"), "sourceKey": .string(fixture.id), "detailId": .number(0),
                "seasonHint": .number(2), "enabled": .bool(true), "autoDisable": .bool(false), "failureCount": .number(0)]),
            .object(["id": .string(run + "-share"), "targetId": .string(fixture.targetID), "kind": .string("share"),
                "name": .string("Synthetic share mapping"), "provider": .string("quark"),
                "share": .object(["url": .string("https://pan.quark.cn/s/film-host-" + run),
                                  "password": .string("synthetic-share-password")]),
                "seasonHint": .number(0), "enabled": .bool(false), "autoDisable": .bool(true), "failureCount": .number(0)]),
        ]
        for item in manual {
            _ = try await client.call(module: .film, operation: "manual.update",
                parameters: .object(["action": .string("save"), "item": item]))
        }
        let favorite = try FilmLifecycleHost.favorite(checkpoint)
        XCTAssertTrue(try assembly.filmLibrary.toggle(favorite))
        let library = try await FilmLifecycleHost.library(client)
        let own = try FilmLifecycleHost.owned(library, checkpoint: checkpoint)
        XCTAssertEqual(own["history"], .array(history))
        XCTAssertEqual(own["blocks"], .array([block]))
        XCTAssertEqual(own["manual"], .array(manual))
        XCTAssertEqual(library["preferences"], changed)
        try FilmLifecycleHost.assertPreserved(library, checkpoint: checkpoint, library: assembly.filmLibrary)
        checkpoint["expected"] = .object(own)
        checkpoint["expectedPreferences"] = changed
        try FilmLifecycleHost.write(checkpoint, kind: "cold")
        XCTAssertTrue(UserDefaults.standard.synchronize())
        preserveForRestore = true
        try await finishFixture(fixture, assembly: assembly, client: client, preservingLibrary: true)
        XCTAssertTrue(client.isClosed)
        checkpoint["executorExitConfirmed"] = .bool(true)
        checkpoint["document"] = .object(try FilmLifecycleHost.documentIdentity())
        try FilmLifecycleHost.write(checkpoint, kind: "cold")
        try FilmLifecycleHost.assertNoTransportInDocuments(checkpoint)
    }
}

@MainActor
final class MeowfilmFilmColdStartRestoreTests: XCTestCase {
    func testAnotherAppProcessRestoresExactFilmDocumentsAndFavoritesWithoutReplayingOrRewritingThem() async throws {
        let run = try FilmLifecycleHost.requirePhase("MEOWFILM_FILM_COLD_PHASE", "restore")
        let checkpoint = try FilmLifecycleHost.read("cold", run: run)
        let resources = try await hostResources()
        let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
        let assembly = try XCTUnwrap(app.readyAssembly())
        let client = try await resources.scriptSession.connection()
        addTeardownBlock {
            var failure: Error?
            if !client.isClosed {
                do { try await FilmLifecycleHost.cleanup(checkpoint, client: client, assembly: assembly) }
                catch { failure = error }
            }
            do { try await self.finishAssembly(assembly, client: client) } catch { failure = failure ?? error }
            if let failure { throw failure }
        }
        XCTAssertEqual(checkpoint["executorExitConfirmed"], .bool(true), "The prepare phase must have confirmed its real executor exit")
        let beforeDisk = try FilmLifecycleHost.documentIdentity()
        XCTAssertEqual(.object(beforeDisk), checkpoint["document"], "App startup must not rewrite the saved document")
        // These reads and native load precede every restore-process business write.
        let library = try await FilmLifecycleHost.library(client)
        let own = try FilmLifecycleHost.owned(library, checkpoint: checkpoint)
        XCTAssertEqual(library["preferences"], checkpoint["expectedPreferences"])
        XCTAssertEqual(.object(own), checkpoint["expected"])
        let favorite = try FilmLifecycleHost.favorite(checkpoint)
        XCTAssertTrue(assembly.filmLibrary.contains(favorite))
        let reloaded = FilmLibrary()
        XCTAssertNil(reloaded.loadError)
        XCTAssertTrue(reloaded.contains(favorite))
        XCTAssertNil(assembly.playback.currentAttempt)
        XCTAssertFalse(assembly.playback.wantsPlayback)
        try FilmLifecycleHost.assertPreserved(library, checkpoint: checkpoint, library: reloaded)
        try FilmLifecycleHost.assertNoTransportInDocuments(checkpoint)
        let again = try await FilmLifecycleHost.library(client)
        XCTAssertEqual(again, library, "Reading restored documents is not a progress update or writeback")
        XCTAssertEqual(try FilmLifecycleHost.documentIdentity(), beforeDisk, "Bytes, modification time and inode stay unchanged")
        try await FilmLifecycleHost.cleanup(checkpoint, client: client, assembly: assembly)
        let after = try await FilmLifecycleHost.library(client)
        XCTAssertEqual(after["preferences"], checkpoint["originalSettings"])
        let removed = try FilmLifecycleHost.owned(after, checkpoint: checkpoint)
        XCTAssertEqual(removed, ["history": .array([]), "manual": .array([]), "blocks": .array([])])
        XCTAssertFalse(assembly.filmLibrary.contains(favorite))
        try FilmLifecycleHost.assertPreserved(after, checkpoint: checkpoint, library: assembly.filmLibrary)
        try await finishAssembly(assembly, client: client)
        try FilmLifecycleHost.removeCheckpoint("cold")
    }
}

@MainActor
private enum FilmLifecycleHost {
    static var libraryURL: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("meowfilm/script-runtime/film-playback/library.json")
    }
    private static func checkpointURL(_ kind: String) -> URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("FilmPlaybackHostTests/\(kind).json")
    }
    static func requirePhase(_ key: String, _ phase: String) throws -> String {
        #if targetEnvironment(simulator)
        let environment = ProcessInfo.processInfo.environment
        guard environment[key] == phase else { throw XCTSkip("Run the dedicated Film lifecycle App process for this phase") }
        guard let run = environment["MEOWFILM_FILM_LIFECYCLE_RUN"], UUID(uuidString: run) != nil else {
            throw FilmPlaybackError(code: "HOST_LIFECYCLE_RUN_ID_REQUIRED")
        }
        return run.lowercased()
        #else
        throw XCTSkip("Terminal and cold-start lifecycle cases require a dedicated simulator container")
        #endif
    }
    static func requireNoCheckpoint(_ kind: String) throws {
        guard !FileManager.default.fileExists(atPath: checkpointURL(kind).path) else {
            throw FilmPlaybackError(code: "HOST_PREVIOUS_CHECKPOINT_REQUIRES_RESTORE_OR_CLEANUP")
        }
    }
    static func write(_ value: RuntimeObject, kind: String) throws {
        let url = checkpointURL(kind)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try RuntimeJSON.encode(.object(value)).write(to: url, options: .atomic)
    }
    static func removeCheckpoint(_ kind: String) throws {
        try FileManager.default.removeItem(at: checkpointURL(kind))
    }
    static func read(_ kind: String, run: String) throws -> RuntimeObject {
        let value = try RuntimeJSON.decode(Data(contentsOf: checkpointURL(kind))).filmRequiredObject()
        let pid = try value.filmNumber("pid")
        let nonce = try value.filmString("processNonce")
        _ = try value.filmBool("executorExitConfirmed")
        let metadataID = try value.filmString("metadataId")
        guard value["run"] == .string(run),
              value["container"] == .string(URL(fileURLWithPath: NSHomeDirectory()).resolvingSymlinksInPath().path),
              pid > 0, pid.rounded(.towardZero) == pid,
              pid != Double(ProcessInfo.processInfo.processIdentifier),
              UUID(uuidString: nonce) != nil, nonce != filmHostProcessNonce,
              let numericID = Int(metadataID), numericID > 0, String(numericID) == metadataID,
              value["targetId"] == .string("tmdb:tv:" + metadataID),
              value["installationId"] == .string("film-\(kind == "cold" ? "cold" : "shutdown")-" + run) else {
            throw FilmPlaybackError(code: "HOST_REQUIRES_DISTINCT_PROCESS_AND_THE_SAME_PREPARED_CONTAINER")
        }
        return value
    }
    static func library(_ client: RuntimeHTTPClient) async throws -> RuntimeObject {
        try await client.call(module: .film, operation: "library.get").filmRequiredObject()
    }
    static func favorite(_ checkpoint: RuntimeObject) throws -> FilmItem {
        .init(sourceID: "tmdb", id: try checkpoint.filmString("metadataId"),
              title: try checkpoint.filmString("installationId"), kind: .tv)
    }
    static func checkpoint(run: String, fixture: FilmPlaybackHostFixture,
                           assembly: MeowfilmAssembly) async throws -> RuntimeObject {
        var value: RuntimeObject = [
            "run": .string(run), "pid": .number(Double(ProcessInfo.processInfo.processIdentifier)),
            "processNonce": .string(filmHostProcessNonce),
            "container": .string(URL(fileURLWithPath: NSHomeDirectory()).resolvingSymlinksInPath().path),
            "installationId": .string(fixture.id), "targetId": .string(fixture.targetID),
            "metadataId": .string(String(fixture.metadataID)), "executorExitConfirmed": .bool(false),
        ]
        let before = try await library(fixture.client)
        value["originalSettings"] = try before.filmValue("preferences")
        let empty = try owned(before, checkpoint: value)
        let status = try await fixture.client.call(module: .cat, operation: "status").filmRequiredObject()
        guard empty.values.allSatisfy({ $0 == .array([]) }), !assembly.filmLibrary.contains(try favorite(value)),
              assembly.filmLibrary.loadError == nil,
              !(try status.filmArray("instances")).contains(where: {
                  $0.filmObject?["installationId"] == .string(fixture.id)
              }) else { throw FilmPlaybackError(code: "HOST_SYNTHETIC_ID_COLLISION") }
        value["preserved"] = .object(try preserved(before, checkpoint: value, library: assembly.filmLibrary))
        return value
    }
    static func owned(_ document: RuntimeObject, checkpoint: RuntimeObject) throws -> RuntimeObject {
        let target = RuntimeValue.string(try checkpoint.filmString("targetId"))
        let run = try checkpoint.filmString("run")
        return [
            "history": .array(try document.filmArray("history").filter { $0.filmObject?["targetId"] == target }),
            "blocks": .array(try document.filmArray("blocks").filter { $0.filmObject?["targetId"] == target }),
            "manual": .array(try document.filmArray("manual").filter {
                $0.filmObject?["id"] == .string(run + "-site") || $0.filmObject?["id"] == .string(run + "-share")
            }),
        ]
    }
    private static func digest(_ value: RuntimeValue) throws -> RuntimeValue {
        let object = try JSONSerialization.jsonObject(with: RuntimeJSON.encode(value))
        let bytes = try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys])
        return .string(SHA256.hash(data: bytes).map { String(format: "%02x", $0) }.joined())
    }
    static func documentIdentity() throws -> RuntimeObject {
        let bytes = try Data(contentsOf: libraryURL)
        let attributes = try FileManager.default.attributesOfItem(atPath: libraryURL.path)
        let modified = try XCTUnwrap(attributes[.modificationDate] as? Date)
        let inode = try XCTUnwrap(attributes[.systemFileNumber] as? NSNumber)
        return [
            "sha256": .string(SHA256.hash(data: bytes).map { String(format: "%02x", $0) }.joined()),
            "modifiedAt": .number(modified.timeIntervalSince1970),
            "inode": .string(inode.stringValue),
        ]
    }
    private static func preserved(_ document: RuntimeObject, checkpoint: RuntimeObject,
                                  library: FilmLibrary) throws -> RuntimeObject {
        let own = try owned(document, checkpoint: checkpoint)
        var result = RuntimeObject()
        for key in ["history", "manual", "blocks"] {
            let excluded = try own.filmArray(key)
            result[key] = try digest(.array(try document.filmArray(key).filter { !excluded.contains($0) }))
        }
        result["previewTargets"] = try digest(try document.filmValue("previewTargets"))
        let item = try favorite(checkpoint)
        let favorites = library.favorites.filter { $0 != item.identity }.map { identity in
            [identity.sourceID, identity.itemID, identity.kind?.rawValue ?? ""]
        }.sorted { $0.lexicographicallyPrecedes($1) }
        result["favorites"] = try digest(.array(favorites.map { .array($0.map(RuntimeValue.string)) }))
        return result
    }
    static func assertPreserved(_ document: RuntimeObject, checkpoint: RuntimeObject, library: FilmLibrary) throws {
        XCTAssertEqual(.object(try preserved(document, checkpoint: checkpoint, library: library)), checkpoint["preserved"],
                       "Unrelated existing container records and favorites must not be cleared or rewritten")
    }
    static func cleanup(_ checkpoint: RuntimeObject, client: RuntimeHTTPClient, assembly: MeowfilmAssembly) async throws {
        let run = try checkpoint.filmString("run")
        let targetID = try checkpoint.filmString("targetId")
        let installationID = try checkpoint.filmString("installationId")
        var failure: Error?
        do {
            _ = try await client.call(module: .film, operation: "library.remove", parameters: .object([
                "kind": .string("history"), "id": .string(targetID),
            ]))
        } catch { failure = error }
        do {
            let own = try owned(try await library(client), checkpoint: checkpoint)
            for block in try own.filmArray("blocks") {
                do {
                    _ = try await client.call(module: .film, operation: "library.remove",
                        parameters: .object(["kind": .string("blocks"), "block": block]))
                } catch { failure = failure ?? error }
            }
        } catch { failure = failure ?? error }
        for id in [run + "-site", run + "-share"] {
            do {
                _ = try await client.call(module: .film, operation: "manual.update",
                    parameters: .object(["action": .string("remove"), "id": .string(id)]))
            } catch { failure = failure ?? error }
        }
        do {
            let favorite = try favorite(checkpoint)
            if assembly.filmLibrary.contains(favorite) { XCTAssertFalse(try assembly.filmLibrary.toggle(favorite)) }
        } catch { failure = failure ?? error }
        do {
            let status = try await client.call(module: .cat, operation: "status").filmRequiredObject()
            if try status.filmArray("instances").contains(where: {
                $0.filmObject?["installationId"] == .string(installationID)
            }) {
                _ = try await client.call(module: .cat, operation: "uninstall",
                    parameters: .object(["installationId": .string(installationID)]))
            }
            let removed = try await client.call(module: .cat, operation: "status").filmRequiredObject()
            XCTAssertFalse(try removed.filmArray("instances").contains {
                $0.filmObject?["installationId"] == .string(installationID)
            })
        } catch { failure = failure ?? error }
        do {
            _ = try await client.call(module: .film, operation: "settings.set",
                                      parameters: try checkpoint.filmValue("originalSettings"))
        } catch { failure = failure ?? error }
        XCTAssertTrue(UserDefaults.standard.synchronize())
        if let failure { throw failure }
    }
    static func assertNoTransportInDocuments(_ checkpoint: RuntimeObject) throws {
        let bytes = try Data(contentsOf: libraryURL)
        let document = try RuntimeJSON.decode(bytes).filmRequiredObject()
        let own = try owned(document, checkpoint: checkpoint)
        func inspect(_ value: RuntimeValue, share: Bool = false) {
            switch value {
            case .object(let fields):
                for (key, child) in fields {
                    XCTAssertFalse(["headers", "cookie", "authorization", "mediaorigin", "drm", "resourceid"].contains(key.lowercased()))
                    if key == "url" { XCTAssertTrue(share, "Only the deliberately saved share URL is persistent") }
                    inspect(child, share: key == "share")
                }
            case .array(let values): values.forEach { inspect($0) }
            default: break
            }
        }
        inspect(.object(own))
        for marker in ["film-host-synthetic-signature", "host-fixture=private", "/v1/film/media/", "/proxy/one.mp4"] {
            XCTAssertNil(bytes.range(of: Data(marker.utf8)), "Transport capability/header material must not reach the document")
        }
        if let expected = checkpoint["expected"] { XCTAssertEqual(.object(own), expected) }
    }
}

@MainActor
private extension XCTestCase {
    func lifecycleWait(_ predicate: @escaping @MainActor () -> Bool) async throws {
        let ready = expectation(for: NSPredicate { _, _ in MainActor.assumeIsolated { predicate() } }, evaluatedWith: nil)
        await fulfillment(of: [ready], timeout: 20)
        guard predicate() else { throw FilmPlaybackError(code: "HOST_LIFECYCLE_TIMEOUT") }
    }
    func finishAssembly(_ assembly: MeowfilmAssembly, client: RuntimeHTTPClient,
                        expectsFailure: Bool = false) async throws {
        let ended = expectation(description: "Complete App assembly and embedded executor physically drain")
        var finished = false
        assembly.shutdown()
        let closing = Task { await assembly.waitForShutdown(); finished = true; ended.fulfill() }
        await fulfillment(of: [ended], timeout: 20)
        guard finished else { throw FilmPlaybackError(code: "HOST_APP_SHUTDOWN_TIMEOUT") }
        await closing.value
        XCTAssertTrue(client.isClosed)
        XCTAssertNil(assembly.playback.currentAttempt)
        XCTAssertFalse(assembly.playback.wantsPlayback)
        XCTAssertEqual(assembly.playback.networkUsage.activeTasks, 0)
        XCTAssertEqual(assembly.playback.networkUsage.activeConnections, 0)
        XCTAssertEqual(assembly.playback.networkUsage.activeListeners, 0)
        XCTAssertEqual(assembly.playback.networkUsage.reservedPayloadBytes, 0)
        if expectsFailure { XCTAssertNotNil(assembly.shutdownError) }
        else { XCTAssertNil(assembly.shutdownError) }
    }
    func finishFixture(_ fixture: FilmPlaybackHostFixture, assembly: MeowfilmAssembly, client: RuntimeHTTPClient,
                       preservingLibrary: Bool) async throws {
        var failure: Error?
        do {
            if client.isClosed { try fixture.removePresentation() }
            else { try await fixture.close(preservingLibrary: preservingLibrary) }
        } catch { failure = error }
        do { try await finishAssembly(assembly, client: client, expectsFailure: assembly.filmPlayback?.isReleased == false) }
        catch { failure = failure ?? error }
        if let failure { throw failure }
    }
}

/// Independent public/direct test upstream. The held candidate outlives Node
/// until native cancellation actually disconnects it; it never receives keys.
private final class FilmLifecycleMovieServer: @unchecked Sendable {
    private let listener: NWListener
    private let host: String
    private let queue = DispatchQueue(label: "meowfilm.film-lifecycle-movie")
    private let movie: Data
    private var connections: [ObjectIdentifier: NWConnection] = [:]
    private var held: Set<ObjectIdentifier> = []
    private var reads = 0, disconnected = 0, leaked = 0
    var base: URL { URL(string: "http://\(host):\(listener.port!.rawValue)")! }
    var movieGETs: Int { queue.sync { reads } }
    var heldGETs: Int { queue.sync { held.count } }
    var disconnectedHeldGETs: Int { queue.sync { disconnected } }
    var unexpectedHeaders: Int { queue.sync { leaked } }
    init(movie: Data) throws {
        self.movie = movie
        let host = try Self.localIPv4Address()
        self.host = host
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .hostPort(host: NWEndpoint.Host(host), port: .any)
        listener = try NWListener(using: parameters)
    }
    private static func localIPv4Address() throws -> String {
        var interfaces: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&interfaces) == 0 else {
            throw FilmPlaybackError(code: "HOST_LOCAL_INTERFACE_ENUMERATION_FAILED")
        }
        defer { freeifaddrs(interfaces) }
        var current = interfaces
        let requiredFlags = UInt32(IFF_UP | IFF_RUNNING)
        while let interface = current {
            defer { current = interface.pointee.ifa_next }
            let flags = interface.pointee.ifa_flags
            guard flags & requiredFlags == requiredFlags, flags & UInt32(IFF_LOOPBACK) == 0,
                  let address = interface.pointee.ifa_addr,
                  address.pointee.sa_family == sa_family_t(AF_INET) else { continue }
            var ipv4 = UnsafeRawPointer(address).assumingMemoryBound(to: sockaddr_in.self).pointee.sin_addr
            var buffer = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
            guard inet_ntop(AF_INET, &ipv4, &buffer, socklen_t(INET_ADDRSTRLEN)) != nil else { continue }
            let host = String(cString: buffer)
            guard host != "0.0.0.0", !host.hasPrefix("127.") else { continue }
            return host
        }
        throw FilmPlaybackError(code: "HOST_NON_LOOPBACK_IPV4_UNAVAILABLE")
    }
    func start() async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            listener.stateUpdateHandler = { [weak self] state in
                switch state {
                case .ready: self?.listener.stateUpdateHandler = nil; continuation.resume()
                case .failed(let error): self?.listener.stateUpdateHandler = nil; continuation.resume(throwing: error)
                default: break
                }
            }
            listener.newConnectionHandler = { [weak self] connection in self?.accept(connection) }
            listener.start(queue: queue)
        }
    }
    func stop() {
        queue.sync {
            listener.cancel()
            for connection in connections.values { connection.cancel() }
            connections.removeAll(); held.removeAll()
        }
    }
    private func accept(_ connection: NWConnection) {
        connections[ObjectIdentifier(connection)] = connection
        connection.start(queue: queue)
        receive(connection, bytes: Data())
    }
    private func receive(_ connection: NWConnection, bytes: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 16 * 1024) { [weak self] chunk, _, ended, error in
            guard let self else { return }
            var bytes = bytes; if let chunk { bytes.append(chunk) }
            guard bytes.count <= 16 * 1024 else { remove(connection); return }
            if let split = bytes.range(of: Data("\r\n\r\n".utf8)),
               let head = String(data: bytes[..<split.lowerBound], encoding: .utf8) {
                respond(connection, head: head)
            } else if ended || error != nil { remove(connection) }
            else { receive(connection, bytes: bytes) }
        }
    }
    private func respond(_ connection: NWConnection, head: String) {
        let lines = head.components(separatedBy: "\r\n")
        let first = lines.first?.split(separator: " ") ?? []
        guard first.count == 3, ["GET", "HEAD"].contains(String(first[0])),
              let url = URL(string: String(first[1]), relativeTo: base) else { remove(connection); return }
        var headers: [String: String] = [:]
        for line in lines.dropFirst() {
            guard let colon = line.firstIndex(of: ":") else { remove(connection); return }
            headers[String(line[..<colon]).lowercased()] = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
        }
        if ["cookie", "authorization", "x-meow-session", "x-meow-request-id"].contains(where: { headers[$0] != nil }) { leaked += 1 }
        if url.path == "/other.mp4", first[0] == "GET" {
            held.insert(ObjectIdentifier(connection))
            waitForDisconnect(connection); return
        }
        guard ["/one.mp4", "/other.mp4"].contains(url.path) else { remove(connection); return }
        if first[0] == "GET" { reads += 1 }
        var start = 0, end = movie.count - 1
        let range = headers["range"]
        if let range {
            let parts = range.replacingOccurrences(of: "bytes=", with: "").split(separator: "-", omittingEmptySubsequences: false)
            guard range.hasPrefix("bytes="), parts.count == 2, let lower = Int(parts[0]), lower >= 0 else { remove(connection); return }
            start = lower
            if !parts[1].isEmpty {
                guard let upper = Int(parts[1]) else { remove(connection); return }
                end = min(end, upper)
            }
        }
        guard start <= end, start < movie.count else {
            send(connection, data: Data("HTTP/1.1 416 Range\r\nContent-Range: bytes */\(movie.count)\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".utf8))
            return
        }
        var response = "HTTP/1.1 \(range == nil ? "200 OK" : "206 Partial Content")\r\nContent-Type: video/mp4\r\nContent-Length: \(end - start + 1)\r\nAccept-Ranges: bytes\r\nConnection: close\r\n"
        if range != nil { response += "Content-Range: bytes \(start)-\(end)/\(movie.count)\r\n" }
        var data = Data((response + "\r\n").utf8)
        if first[0] != "HEAD" { data.append(movie.subdata(in: start..<(end + 1))) }
        send(connection, data: data)
    }
    private func send(_ connection: NWConnection, data: Data) {
        connection.send(content: data, contentContext: .finalMessage, isComplete: true,
            completion: .contentProcessed { [weak self] _ in self?.remove(connection) })
    }
    private func waitForDisconnect(_ connection: NWConnection) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 1024) { [weak self] _, _, ended, error in
            guard let self else { return }
            if ended || error != nil {
                if held.contains(ObjectIdentifier(connection)) { disconnected += 1 }
                remove(connection)
            } else { waitForDisconnect(connection) }
        }
    }
    private func remove(_ connection: NWConnection) {
        connections[ObjectIdentifier(connection)] = nil
        held.remove(ObjectIdentifier(connection))
        connection.cancel()
    }
}
