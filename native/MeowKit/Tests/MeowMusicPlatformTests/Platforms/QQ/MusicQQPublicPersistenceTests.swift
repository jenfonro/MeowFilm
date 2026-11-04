import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicPlatform

final class MusicQQPublicPersistenceTests: XCTestCase {
    func testPersistentConstructionAndGrantArePassiveAndLockedByDefault() async throws {
        let f = QQPublicPersistenceFixture()
        XCTAssertEqual(f.document.counts().reads, 0)
        do { try await f.search(); XCTFail("Locked service opened") }
        catch { XCTAssertEqual(error as? MusicServiceError, .accessUnavailable) }
        try await f.enable()
        XCTAssertEqual(f.document.counts().reads, 0)
        let before = try await f.http.methods()
        XCTAssertTrue(before.isEmpty)
        try await f.search()
        let calls = await f.http.calls()
        let first = try XCTUnwrap(calls.first)
        let seed = try MusicQQPublicPersistenceCodec.decode(XCTUnwrap(first.document))
        XCTAssertNil(seed["qimei"]); XCTAssertNil(seed["session"])
        XCTAssertEqual(f.document.counts().commits, 3)
        await f.service.close()
    }

    func testColdRestoreRetainsIdentityAndValidQimeiAndSessionWithoutBootstrapHTTP() async throws {
        let first = QQPublicPersistenceFixture()
        try await first.enable(); try await first.search()
        let bytes = first.document.peek()
        await first.service.close()
        XCTAssertEqual(first.document.peek(), bytes)
        let cold = QQPublicPersistenceFixture(document: first.document, clock: first.clock)
        try await cold.enable(); try await cold.search()
        _ = try await cold.service.lyrics(mid: "SyntheticMID")
        let methods = try await cold.http.methods()
        XCTAssertEqual(methods, ["DoSearchForQQMusicMobile", "CgiGetTrackInfo", "GetPlayLyricInfo"])
        XCTAssertEqual(cold.document.peek(), bytes)
        await cold.service.close()
    }

    func testQimeiExpiresAtExactlyTwentyFourHoursWithoutIdentityRotation() async throws {
        let f = QQPublicPersistenceFixture()
        try await f.enable(); try await f.search()
        let identity = try f.stored()["open_udid"] as? String
        f.clock.advance(86_399)
        try await f.search()
        var methods = try await f.http.methods()
        XCTAssertEqual(methods.filter { $0 == "GetQimei" }.count, 1)
        f.clock.advance(1)
        try await f.search()
        methods = try await f.http.methods()
        XCTAssertEqual(methods.filter { $0 == "GetQimei" }.count, 2)
        XCTAssertEqual(try f.stored()["open_udid"] as? String, identity)
        await f.service.close()
    }

    func testShanghaiMidnightRefreshesOnlySessionAndUsesPreviousAnonymousUID() async throws {
        let clock = QQPublicPersistenceClock(try XCTUnwrap(ISO8601DateFormatter().date(from: "2026-10-22T15:59:59Z")))
        let f = QQPublicPersistenceFixture(clock: clock)
        try await f.enable(); try await f.search()
        clock.advance(2)
        try await f.search()
        let calls = await f.http.calls()
        let sessions = try calls.filter { try QQPublicPersistenceHTTP.method($0.request) == "GetSession" }
        XCTAssertEqual(sessions.count, 2)
        for (index, row) in sessions.enumerated() {
            let root = try QQPublicPersistenceHTTP.root(row.request)
            let comm = try XCTUnwrap(root["comm"] as? [String: Any])
            let rpc = try XCTUnwrap(root["req_0"] as? [String: Any])
            let param = try XCTUnwrap(rpc["param"] as? [String: Any])
            XCTAssertEqual(param["caller"] as? Int, index == 0 ? 2 : 1)
            XCTAssertEqual(param["vkey"] as? Int, 0)
            XCTAssertEqual(param["uid"] as? String, index == 0 ? "" : "synthetic-anonymous")
            XCTAssertNil(row.request.headers["Cookie"]); XCTAssertNil(comm["qq"]); XCTAssertNil(comm["authst"])
            XCTAssertNil(comm["uid"]); XCTAssertNil(comm["sid"])
        }
        let methods = try await f.http.methods()
        XCTAssertEqual(methods.filter { $0 == "GetQimei" }.count, 1)
        XCTAssertEqual((try f.stored()["session"] as? [String: Any])?["day"] as? String, "2026-10-23")
        await f.service.close()
    }

    func testDelayedBootstrapCannotExtendQimeiTTLOrMoveSessionIntoNextChinaDay() async throws {
        for stage in ["GetQimei", "GetSession"] {
            let clock = QQPublicPersistenceClock(try XCTUnwrap(ISO8601DateFormatter().date(from: "2026-10-22T15:59:59Z")))
            let f = QQPublicPersistenceFixture(clock: clock), gate = QQPublicPersistenceGate()
            addTeardownBlock { await gate.release(); await f.service.close() }
            let startedAt = clock.now()
            await f.http.hold(stage, gate: gate)
            try await f.enable()
            let work = Task { try await f.search() }
            await fulfillment(of: [gate.entered], timeout: 3)
            clock.advance(2)
            await gate.release()
            try await work.value
            let value = try f.stored()
            XCTAssertEqual((value["qimei"] as? [String: Any])?["savedAt"] as? Date, startedAt)
            XCTAssertEqual((value["session"] as? [String: Any])?["day"] as? String, "2026-10-22")
            try await f.search()
            var methods = try await f.http.methods()
            XCTAssertEqual(methods.filter { $0 == "GetSession" }.count, 2)
            XCTAssertEqual(methods.filter { $0 == "GetQimei" }.count, 1)
            clock.advance(86_398)
            try await f.search()
            methods = try await f.http.methods()
            XCTAssertEqual(methods.filter { $0 == "GetQimei" }.count, 2)
            await f.service.close()
        }
    }

    func testBackwardClockDoesNotTreatFutureCacheAsFreshOrReplaceStableIdentity() async throws {
        let f = QQPublicPersistenceFixture()
        try await f.enable(); try await f.search()
        let identity = try f.stored()["android_id"] as? String
        f.clock.advance(-60)
        try await f.search()
        let methods = try await f.http.methods()
        XCTAssertEqual(methods.filter { $0 == "GetQimei" }.count, 2)
        XCTAssertEqual(methods.filter { $0 == "GetSession" }.count, 2)
        XCTAssertEqual(try f.stored()["android_id"] as? String, identity)
        await f.service.close()
    }

    func testFirstQimeiFailureStillColdRestoresTheCommittedSeed() async throws {
        let first = QQPublicPersistenceFixture()
        await first.http.fail("GetQimei", status: 500)
        try await first.enable()
        do { try await first.search(); XCTFail("QIMEI failure ignored") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamFailed) }
        let identity = try first.stored()["open_udid"] as? String
        XCTAssertEqual(first.document.counts().commits, 1)
        await first.service.close()
        let cold = QQPublicPersistenceFixture(document: first.document, clock: first.clock)
        try await cold.enable(); try await cold.search()
        XCTAssertEqual(try cold.stored()["open_udid"] as? String, identity)
        await cold.service.close()
    }

    func testSessionFailureKeepsQimeiAndRetryDoesNotRepeatQimeiOrIssueSearchEarly() async throws {
        let first = QQPublicPersistenceFixture()
        await first.http.fail("GetSession", status: 500)
        try await first.enable()
        do { try await first.search(); XCTFail("Session failure ignored") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamFailed) }
        let partial = try first.stored()
        XCTAssertNotNil(partial["qimei"]); XCTAssertNil(partial["session"])
        let failedMethods = try await first.http.methods()
        XCTAssertEqual(failedMethods, ["GetQimei", "GetSession"])
        await first.service.close()
        let cold = QQPublicPersistenceFixture(document: first.document, clock: first.clock)
        try await cold.enable(); try await cold.search()
        let methods = try await cold.http.methods()
        XCTAssertEqual(methods, ["GetSession", "DoSearchForQQMusicMobile"])
        await cold.service.close()
    }

    func testInvalidSessionDoesNotCommitOrBecomeSuccessfulSearch() async throws {
        let f = QQPublicPersistenceFixture()
        await f.http.override("GetSession", bytes: try QQPublicPersistenceHTTP.reply(["session": ["uid": "synthetic", "sid": ""]]))
        try await f.enable()
        do { try await f.search(); XCTFail("Empty session accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        XCTAssertNil(try f.stored()["session"])
        XCTAssertEqual(f.document.counts().commits, 2)
        await f.service.close()
    }

    func testCorruptUnknownVersionAndSecretFieldsNeverRegenerateOrContactHTTP() async throws {
        let valid = try MusicQQPublicPersistenceCodec.encode(QQPublicPersistenceFixture.seed())
        var root = try XCTUnwrap(JSONSerialization.jsonObject(with: valid) as? [String: Any])
        root["version"] = 2
        let newer = try JSONSerialization.data(withJSONObject: root)
        root["version"] = 1; root["auth"] = ["musickey": "synthetic-not-a-public-device"]
        let secret = try JSONSerialization.data(withJSONObject: root)
        let duplicate = Data(String(decoding: valid, as: UTF8.self).replacingOccurrences(of: "\"version\":1", with: "\"version\":1,\"version\":1").utf8)
        for bad in [Data(), Data("broken".utf8), newer, secret, duplicate, Data(repeating: 0x20, count: 16 * 1024 + 1)] {
            let f = QQPublicPersistenceFixture(document: .init(bad))
            try await f.enable()
            do { try await f.search(); XCTFail("Corrupt document accepted") }
            catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument) }
            XCTAssertEqual(f.document.peek(), bad); XCTAssertEqual(f.document.counts().commits, 0)
            let methods = try await f.http.methods()
            XCTAssertTrue(methods.isEmpty)
            await f.service.close()
        }
    }

    func testLockedOrFailedReadIsNotAbsenceAndRequiresFreshAdmissionToRetry() async throws {
        for error in [MusicServiceError.accessUnavailable, .storageFailed, .corruptDocument] {
            let f = QQPublicPersistenceFixture()
            f.document.failRead(error)
            try await f.enable()
            do { try await f.search(); XCTFail("Read failure ignored") }
            catch let actual { XCTAssertEqual(actual as? MusicServiceError, error) }
            f.document.failRead(nil)
            do { try await f.search(); XCTFail("Fault latch bypassed") }
            catch let actual { XCTAssertEqual(actual as? MusicServiceError, error) }
            XCTAssertNil(f.document.peek())
            try await f.enable(2); try await f.search()
            await f.service.close()
        }
    }

    func testInitialCASFailureStopsBeforeAnyHTTPAndDoesNotUseVolatileFallback() async throws {
        let f = QQPublicPersistenceFixture()
        f.document.failCommit(.storageFailed)
        try await f.enable()
        do { try await f.search(); XCTFail("Volatile fallback") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageFailed) }
        let methods = try await f.http.methods()
        XCTAssertTrue(methods.isEmpty); XCTAssertNil(f.document.peek())
        await f.service.close()
    }

    func testCancellationDuringInitialReadCannotMarkUncommittedSeedAsLoaded() async throws {
        let f = QQPublicPersistenceFixture()
        f.document.cancelOnNextRead()
        try await f.enable()
        let cancelled = Task { try await f.search() }
        do { try await cancelled.value; XCTFail("Cancelled seed read accepted") }
        catch { XCTAssertTrue(error is CancellationError) }
        XCTAssertNil(f.document.peek())
        XCTAssertEqual(f.document.counts().commits, 0)
        let before = try await f.http.methods()
        XCTAssertTrue(before.isEmpty)

        // Same admission, not a regrant: this must still commit before HTTP.
        try await f.search()
        let calls = await f.http.calls()
        let first = try XCTUnwrap(calls.first)
        let seed = try MusicQQPublicPersistenceCodec.decode(XCTUnwrap(first.document))
        XCTAssertNil(seed["qimei"]); XCTAssertNil(seed["session"])
        XCTAssertEqual(f.document.counts().commits, 3)
        await f.service.close()
    }

    func testCancellationAtAtomicSeedCommitKeepsIdentityButDoesNotStartHTTP() async throws {
        let f = QQPublicPersistenceFixture()
        f.document.cancelOnNextCommit()
        try await f.enable()
        let cancelled = Task { try await f.search() }
        do { try await cancelled.value; XCTFail("Cancelled seed publication accepted") }
        catch { XCTAssertTrue(error is CancellationError) }
        let seed = try f.stored()
        let identity = try XCTUnwrap(seed["open_udid"] as? String)
        XCTAssertNil(seed["qimei"]); XCTAssertNil(seed["session"])
        XCTAssertEqual(f.document.counts().commits, 1)
        let before = try await f.http.methods()
        XCTAssertTrue(before.isEmpty)

        try await f.search()
        XCTAssertEqual(try f.stored()["open_udid"] as? String, identity)
        XCTAssertEqual(f.document.counts().commits, 3)
        await f.service.close()
    }

    func testAmbiguousAppliedCASIsNotReplayedAndFreshAdmissionReadsDurableIdentity() async throws {
        let f = QQPublicPersistenceFixture()
        f.document.failCommit(.commitUnconfirmed, afterApplying: true)
        try await f.enable()
        do { try await f.search(); XCTFail("Ambiguous commit accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .commitUnconfirmed) }
        let seed = try f.stored()["open_udid"] as? String
        f.document.failCommit(nil)
        do { try await f.search(); XCTFail("Ambiguous write silently replayed") }
        catch { XCTAssertEqual(error as? MusicServiceError, .commitUnconfirmed) }
        XCTAssertEqual(f.document.counts().commits, 1)
        try await f.enable(2); try await f.search()
        XCTAssertEqual(try f.stored()["open_udid"] as? String, seed)
        await f.service.close()
    }

    func testConflictingWriterIsNeverOverwrittenAndRegrantReadsWinner() async throws {
        let f = QQPublicPersistenceFixture(), gate = QQPublicPersistenceGate()
        addTeardownBlock { await gate.release(); await f.service.close() }
        await f.http.hold("GetQimei", gate: gate)
        try await f.enable()
        let work = Task { try await f.search() }
        await fulfillment(of: [gate.entered], timeout: 3)
        let winner = try MusicQQPublicPersistenceCodec.encode(QQPublicPersistenceFixture.seed("b"))
        f.document.replace(winner)
        await gate.release()
        do { try await work.value; XCTFail("CAS conflict overwritten") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        XCTAssertEqual(f.document.peek(), winner)
        try await f.enable(2); try await f.search()
        XCTAssertEqual(try f.stored()["android_id"] as? String, String(repeating: "b", count: 16))
        await f.service.close()
    }

    func testSynchronousRevokeBlocksOldGrantAndLateEachStageWithoutOverwritingNewEpoch() async throws {
        for stage in ["GetQimei", "GetSession", "DoSearchForQQMusicMobile"] {
            let f = QQPublicPersistenceFixture(), gate = QQPublicPersistenceGate()
            addTeardownBlock { await gate.release(); await f.service.close() }
            await f.http.hold(stage, gate: gate)
            try await f.enable()
            let old = Task { try await f.search() }
            await fulfillment(of: [gate.entered], timeout: 3)
            f.service.revokeAccess(generation: 1)
            do { try await f.enable(1); XCTFail("Old grant reopened") }
            catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
            try await f.service.setAccess(available: false, generation: 1)
            try await f.enable(2); try await f.search()
            let winner = f.document.peek()
            await gate.release()
            do { try await old.value; XCTFail("Old response published") } catch {}
            XCTAssertEqual(f.document.peek(), winner)
            XCTAssertGreaterThan(f.http.cancellations.get(), 0)
            await f.service.close()
        }
    }

    func testCancellationDropsLateDeviceUpdateButKeepsSeedForExplicitRetry() async throws {
        let f = QQPublicPersistenceFixture(), gate = QQPublicPersistenceGate()
        addTeardownBlock { await gate.release(); await f.service.close() }
        await f.http.hold("GetQimei", gate: gate)
        try await f.enable()
        let work = Task { try await f.search() }
        await fulfillment(of: [gate.entered], timeout: 3)
        let seed = f.document.peek()
        work.cancel()
        await gate.release()
        do { try await work.value; XCTFail("Cancelled response accepted") } catch {}
        XCTAssertEqual(f.document.peek(), seed)
        try await f.search()
        XCTAssertEqual(try f.stored()["open_udid"] as? String,
                       try MusicQQPublicPersistenceCodec.decode(XCTUnwrap(seed))["open_udid"] as? String)
        await f.service.close()
    }

    func testConcurrentCallsShareOneBootstrapAndIdempotentGrantDoesNotResetIt() async throws {
        let f = QQPublicPersistenceFixture(), gate = QQPublicPersistenceGate()
        addTeardownBlock { await gate.release(); await f.service.close() }
        await f.http.hold("GetQimei", gate: gate)
        try await f.enable()
        let first = Task { try await f.search() }
        await fulfillment(of: [gate.entered], timeout: 3)
        try await f.enable(1)
        let second = Task { try await f.search() }
        await gate.release()
        try await first.value; try await second.value
        let methods = try await f.http.methods()
        XCTAssertEqual(methods.filter { $0 == "GetQimei" }.count, 1)
        XCTAssertEqual(methods.filter { $0 == "GetSession" }.count, 1)
        XCTAssertEqual(f.document.counts().commits, 3)
        await f.service.close()
    }
    func testCancellingOneDeviceWaiterLeavesOtherWaiterAndPhysicalBootstrapAlive() async throws {
        let f = QQPublicPersistenceFixture(), gate = QQPublicPersistenceGate()
        addTeardownBlock { await gate.release(); await f.service.close() }
        await f.http.hold("GetQimei", gate: gate); try await f.enable()
        let first = Task { try await f.search() }
        await fulfillment(of: [gate.entered], timeout: 3)
        let second = Task { try await f.search() }
        let joined = expectation(description: "second device waiter joined")
        let adapter = await f.service.qq, flight = await adapter.deviceRequests
        let observer = Task {
            while !Task.isCancelled {
                if flight.waiterCount == 2 { joined.fulfill(); return }
                await Task.yield()
            }
        }
        defer { observer.cancel() }
        await fulfillment(of: [joined], timeout: 3)
        XCTAssertEqual(flight.waiterCount, 2)
        first.cancel()
        do { try await first.value; XCTFail("Cancelled waiter returned") }
        catch { XCTAssertTrue(error is CancellationError || error as? MusicServiceError == .cancelled) }
        XCTAssertEqual(f.http.cancellations.get(), 0, "The valid waiter still owns the physical bootstrap")
        await gate.release(); try await second.value
        let methods = try await f.http.methods()
        XCTAssertEqual(methods.filter { $0 == "GetQimei" }.count, 1)
        XCTAssertEqual(methods.filter { $0 == "GetSession" }.count, 1)
        XCTAssertEqual(methods.filter { $0 == "DoSearchForQQMusicMobile" }.count, 1)
        await f.service.close()
    }

    func testCloseCancelsInFlightDoesNotWriteAndCannotBeReopened() async throws {
        let f = QQPublicPersistenceFixture(), gate = QQPublicPersistenceGate()
        addTeardownBlock { await gate.release(); await f.service.close() }
        await f.http.hold("GetSession", gate: gate)
        try await f.enable()
        let work = Task { try await f.search() }
        await fulfillment(of: [gate.entered], timeout: 3)
        let before = f.document.peek()
        await f.service.close()
        await gate.release()
        do { try await work.value; XCTFail("Closed operation delivered") } catch {}
        XCTAssertEqual(f.document.peek(), before)
        do { try await f.enable(99); XCTFail("Closed service reopened") }
        catch { XCTAssertEqual(error as? MusicServiceError, .closed) }
        XCTAssertGreaterThan(f.http.cancellations.get(), 0)
    }

    func testRevokedNoncooperativeRequestsCannotPublishAndNewAccessCanFinish() async throws {
        let f = QQPublicPersistenceFixture()
        try await f.enable(); try await f.search()
        let gates = (0..<2).map { _ in QQPublicPersistenceGate() }
        addTeardownBlock {
            for gate in gates { await gate.release() }
            await f.service.close()
        }
        var work: [Task<Void, Error>] = []
        for gate in gates {
            await f.http.hold("DoSearchForQQMusicMobile", gate: gate)
            work.append(Task { try await f.search() })
            await fulfillment(of: [gate.entered], timeout: 3)
        }
        f.service.revokeAccess(generation: 1)
        try await f.enable(2)
        let queuedNew = Task { try await f.search() }
        try await f.http.waitForCalls("DoSearchForQQMusicMobile", count: 3)
        let before = try await f.http.methods()
        XCTAssertEqual(before.filter { $0 == "DoSearchForQQMusicMobile" }.count, 3)
        for gate in gates { await gate.release() }
        for task in work {
            do { try await task.value; XCTFail("Old result delivered") } catch {}
        }
        try await queuedNew.value
        let after = try await f.http.methods()
        XCTAssertEqual(after.filter { $0 == "DoSearchForQQMusicMobile" }.count, 4)
        XCTAssertEqual(after.filter { $0 == "GetQimei" }.count, 1)
        await f.service.close()
    }

    func testCancellingInFlightReadCannotPublishAndFreshReadCanFinish() async throws {
        let f = QQPublicPersistenceFixture()
        let gate = QQPublicPersistenceGate()
        addTeardownBlock { await gate.release(); await f.service.close() }
        try await f.enable(); try await f.search()
        let before = f.document.peek()
        await f.http.hold("DoSearchForQQMusicMobile", gate: gate)
        let cancelled = Task { try await f.search() }
        await fulfillment(of: [gate.entered], timeout: 3)
        cancelled.cancel()
        await gate.release()
        do { try await cancelled.value; XCTFail("Cancelled read delivered") } catch {}
        XCTAssertEqual(f.document.peek(), before)
        try await f.search()
        await f.service.close()
    }

    func testLateTrackOrLyricResponseCannotDeliverLyricsAfterRevocation() async throws {
        for stage in ["CgiGetTrackInfo", "GetPlayLyricInfo"] {
            let f = QQPublicPersistenceFixture(), gate = QQPublicPersistenceGate()
            addTeardownBlock { await gate.release(); await f.service.close() }
            try await f.enable(); try await f.search()
            await f.http.hold(stage, gate: gate)
            let work = Task { try await f.service.lyrics(mid: "SyntheticMID") }
            await fulfillment(of: [gate.entered], timeout: 3)
            let before = f.document.peek()
            f.service.revokeAccess(generation: 1)
            await gate.release()
            do { _ = try await work.value; XCTFail("Revoked lyric delivered") } catch {}
            XCTAssertEqual(f.document.peek(), before)
            let methods = try await f.http.methods()
            XCTAssertEqual(methods.filter { $0 == "GetPlayLyricInfo" }.count, stage == "CgiGetTrackInfo" ? 0 : 1)
            await f.service.close()
        }
    }

    func testAnonymousAdapterRejectsCredentialHeadersCookiesAndAccountCommBeforeHTTP() async throws {
        let state = MusicQQPublicPersistenceState(document: QQPublicPersistenceMemory())
        let http = QQPublicPersistenceHTTP()
        let adapter = QQMusicAccountAdapter(transport: http, now: { Date() }, publicPersistence: state)
        let access = try state.setAccess(available: true, generation: 1)
        let url = try XCTUnwrap(URL(string: "https://u.y.qq.com/cgi-bin/musicu.fcg"))
        for name in ["Cookie", "aUtHoRiZaTiOn", "Proxy-Authorization"] {
            do { _ = try await adapter.request(url, headers: [name: "synthetic"], publicAccess: access); XCTFail("Credential header") }
            catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
        }
        do { _ = try await adapter.request(url, cookies: ["uin": "synthetic"], publicAccess: access); XCTFail("Account cookie") }
        catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
        for name in ["qq", "authst", "tmeLoginType"] {
            do {
                _ = try await adapter.rpcWith("synthetic", "synthetic", [:], comm: [name: "synthetic"], publicAccess: access)
                XCTFail("Account comm")
            } catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
        }
        let methods = try await http.methods()
        XCTAssertTrue(methods.isEmpty)
        state.close(); await adapter.revoke()
    }

    func testStaleRevokeCannotTakeAwayNewAdmissionAndPublicRequestsHaveNoAccountAuth() async throws {
        let f = QQPublicPersistenceFixture()
        try await f.enable(2)
        f.service.revokeAccess(generation: 1)
        try await f.search()
        _ = try await f.service.lyrics(mid: "SyntheticMID")
        let calls = await f.http.calls()
        for seen in calls {
            XCTAssertNil(seen.request.headers["Cookie"])
            let root = try QQPublicPersistenceHTTP.root(seen.request)
            if let comm = root["comm"] as? [String: Any] {
                XCTAssertNil(comm["qq"]); XCTAssertNil(comm["authst"]); XCTAssertNil(comm["tmeLoginType"])
            }
        }
        await f.service.close()
    }

    func testExplicitVolatileConstructionHasNoPersistentFallback() async throws {
        let http = QQPublicPersistenceHTTP()
        let service = NativeMusicQQPublicService(transport: http)
        _ = try await service.search(query: "synthetic", page: 1, limit: 30)
        _ = try await service.lyrics(mid: "SyntheticMID")
        await service.close()
    }

}
