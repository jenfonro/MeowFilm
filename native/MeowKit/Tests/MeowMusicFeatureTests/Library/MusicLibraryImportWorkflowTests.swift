#if os(iOS) || os(macOS)
import Foundation
import XCTest
import MeowStorage
@testable import MeowMusicFeature

@MainActor final class MusicLibraryImportWorkflowTests: XCTestCase {
    func testStagingUsesExactArchiveBytesAndCommitOnlyNotifiesHost() async throws {
        let bytes = Data("abc".utf8), file = try makeFile(Data("abc".utf8))
        defer { try? FileManager.default.removeItem(at: file) }
        let service = LibraryImportWorkflowService(), host = LibraryImportWorkflowHost()
        let workflow = host.workflow(service: service)
        workflow.owner = "previous-owner"

        await workflow.loadFile(file)
        XCTAssertTrue(workflow.isOpen); XCTAssertFalse(workflow.isBusy)
        XCTAssertNil(workflow.preview); XCTAssertEqual(workflow.owner, "")
        workflow.owner = "synthetic-owner"
        await workflow.previewImport()
        let token = try XCTUnwrap(workflow.preview)
        let prepared = await service.snapshot()
        XCTAssertEqual(prepared.inputs.map(\.data), [bytes])
        XCTAssertEqual(prepared.inputs.map(\.sourceID),
            ["archive-sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"])
        XCTAssertEqual(prepared.inputs.map(\.owner), ["synthetic-owner"])

        await workflow.confirmImport()
        let committed = await service.snapshot()
        XCTAssertEqual(committed.commits, [token.importId])
        XCTAssertEqual(committed.released, [token.importId])
        XCTAssertEqual(committed.writes, 1); XCTAssertEqual(host.commits, 1)
        XCTAssertEqual(committed.libraryReads, 0); XCTAssertEqual(committed.rollbacks, 0)
        XCTAssertFalse(workflow.isOpen); XCTAssertFalse(workflow.isBusy)
        XCTAssertNil(workflow.preview); XCTAssertEqual(workflow.owner, "")
        XCTAssertTrue(host.failures.isEmpty)
        XCTAssertEqual(try Data(contentsOf: file), bytes)
        await workflow.close()
    }

    func testSevenMiBLimitAndUnavailableServiceReportWithoutOpeningSession() async throws {
        let limit = 7 * 1024 * 1024
        let exact = try makeFile(Data(repeating: 1, count: limit))
        let oversized = try makeFile(Data(repeating: 2, count: limit + 1))
        defer {
            try? FileManager.default.removeItem(at: exact)
            try? FileManager.default.removeItem(at: oversized)
        }
        let service = LibraryImportWorkflowService(), host = LibraryImportWorkflowHost()
        let workflow = host.workflow(service: service)
        await workflow.loadFile(exact)
        XCTAssertTrue(workflow.isOpen)
        await workflow.loadFile(oversized)
        XCTAssertFalse(workflow.isOpen); XCTAssertFalse(workflow.isBusy)
        XCTAssertNil(workflow.preview)
        XCTAssertEqual(host.failures.first?.error as? ImportedFileError, .tooLarge)
        XCTAssertEqual(host.failures.first?.generation, host.generation)
        XCTAssertEqual(host.failures.first?.operation, "读取曲库文件")
        let snapshot = await service.snapshot()
        XCTAssertTrue(snapshot.inputs.isEmpty); XCTAssertEqual(snapshot.writes, 0)
        XCTAssertEqual(try Data(contentsOf: oversized).count, limit + 1)
        await workflow.close()

        let unavailableHost = LibraryImportWorkflowHost()
        let unavailable = unavailableHost.workflow(service: nil)
        await unavailable.loadFile(exact)
        XCTAssertFalse(unavailable.isOpen); XCTAssertFalse(unavailable.isBusy)
        XCTAssertEqual(unavailableHost.failures.first?.error as? MusicServiceError, .unavailable)
        await unavailable.close()
    }

    func testDismissedPreviewAndReplacementWaitForExactPhysicalRelease() async throws {
        let first = try makeFile(), second = try makeFile(Data("before".utf8))
        defer {
            try? FileManager.default.removeItem(at: first)
            try? FileManager.default.removeItem(at: second)
        }
        let service = LibraryImportWorkflowService(), host = LibraryImportWorkflowHost()
        let workflow = host.workflow(service: service)
        await workflow.loadFile(first); workflow.owner = "old-owner"
        let preparing = expectation(description: "prepare entered")
        let releasing = expectation(description: "late preview release entered")
        let prepareGate = LibraryImportWorkflowGate { preparing.fulfill() }
        let releaseGate = LibraryImportWorkflowGate { releasing.fulfill() }
        await service.blockPrepare(prepareGate); await service.blockRelease(releaseGate)
        let previewTask = Task { await workflow.previewImport() }
        await fulfillment(of: [preparing], timeout: 2)

        let cleanup = try XCTUnwrap(workflow.cancelPresentation())
        XCTAssertFalse(workflow.isOpen); XCTAssertNil(workflow.preview)
        XCTAssertTrue(workflow.isBusy)
        var drained = false, replaced = false
        let drain = Task { await cleanup.value; drained = true }
        let replacementStarted = expectation(description: "replacement requested")
        let replacement = Task {
            replacementStarted.fulfill()
            await workflow.loadFile(second)
            replaced = true
        }
        await fulfillment(of: [replacementStarted], timeout: 2)
        XCTAssertFalse(drained); XCTAssertFalse(replaced)
        await prepareGate.release()
        await fulfillment(of: [releasing], timeout: 2)
        XCTAssertFalse(drained); XCTAssertFalse(replaced)
        XCTAssertTrue(workflow.isBusy); XCTAssertFalse(workflow.isOpen)

        let changed = Data("changed-while-cleaning".utf8)
        try changed.write(to: second)
        await releaseGate.release()
        await previewTask.value; await drain.value; await replacement.value
        XCTAssertTrue(drained); XCTAssertTrue(replaced)
        XCTAssertTrue(workflow.isOpen); XCTAssertFalse(workflow.isBusy)
        XCTAssertNil(workflow.preview); XCTAssertEqual(workflow.owner, "")
        XCTAssertFalse(previewTask.isCancelled); XCTAssertFalse(cleanup.isCancelled)
        workflow.owner = "new-owner"
        await workflow.previewImport()
        let snapshot = await service.snapshot()
        XCTAssertEqual(snapshot.inputs.map(\.data), [Data("synthetic-archive".utf8), changed])
        XCTAssertEqual(snapshot.inputs.map(\.owner), ["old-owner", "new-owner"])
        XCTAssertEqual(snapshot.released, ["preview-1"])
        XCTAssertEqual(snapshot.peakPhysicalCalls, 1)
        XCTAssertTrue(host.failures.isEmpty); XCTAssertEqual(host.commits, 0)
        await workflow.close()
    }

    func testCallerCancellationDoesNotMaskUnconfirmedCommitOrRollBack() async throws {
        let file = try makeFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let service = LibraryImportWorkflowService(), host = LibraryImportWorkflowHost()
        let workflow = host.workflow(service: service)
        let preview = try await prepare(workflow, file: file)
        let committing = expectation(description: "commit entered")
        let gate = LibraryImportWorkflowGate { committing.fulfill() }
        await service.blockCommit(gate, failure: .commitUnconfirmed)
        let caller = Task { await workflow.confirmImport() }
        await fulfillment(of: [committing], timeout: 2)
        caller.cancel()
        XCTAssertTrue(workflow.isBusy)
        await gate.release(); await caller.value

        let snapshot = await service.snapshot()
        XCTAssertEqual(snapshot.commits, [preview.importId])
        XCTAssertEqual(snapshot.released, [preview.importId])
        XCTAssertEqual(snapshot.writes, 1, "fixture models an unconfirmed write that may have committed")
        XCTAssertEqual(snapshot.rollbacks, 0); XCTAssertEqual(host.commits, 0)
        XCTAssertEqual(host.failures.count, 1)
        XCTAssertEqual(host.failures.first?.error as? MusicServiceError, .commitUnconfirmed)
        XCTAssertEqual(host.failures.first?.operation, "导入曲库")
        XCTAssertEqual(host.failures.first?.reportWasCancelled, false)
        XCTAssertTrue(snapshot.cancellation.allSatisfy { !$0 })
        XCTAssertTrue(workflow.isOpen); XCTAssertFalse(workflow.isBusy)
        XCTAssertNil(workflow.preview)
        await workflow.confirmImport()
        let after = await service.snapshot()
        XCTAssertEqual(after.commits.count, 1, "no automatic retry of an unconfirmed commit")
        XCTAssertEqual(try Data(contentsOf: file), Data("synthetic-archive".utf8))
        await workflow.close()
    }

    func testDismissedConfirmedCommitDrainsBeforeNewFileAndOnlyNotifiesHost() async throws {
        let first = try makeFile(), second = try makeFile(Data("next-archive".utf8))
        defer {
            try? FileManager.default.removeItem(at: first)
            try? FileManager.default.removeItem(at: second)
        }
        let service = LibraryImportWorkflowService(), host = LibraryImportWorkflowHost()
        let workflow = host.workflow(service: service)
        let preview = try await prepare(workflow, file: first)
        let committing = expectation(description: "commit entered")
        let releasing = expectation(description: "commit token release entered")
        let commitGate = LibraryImportWorkflowGate { committing.fulfill() }
        let releaseGate = LibraryImportWorkflowGate { releasing.fulfill() }
        await service.blockCommit(commitGate); await service.blockRelease(releaseGate)
        let caller = Task { await workflow.confirmImport() }
        await fulfillment(of: [committing], timeout: 2)
        let cleanup = try XCTUnwrap(workflow.cancelPresentation())
        XCTAssertFalse(workflow.isOpen); XCTAssertTrue(workflow.isBusy)
        let before = await service.snapshot()
        XCTAssertTrue(before.releaseAttempts.isEmpty)

        let replacementStarted = expectation(description: "replacement requested")
        var replaced = false
        let replacement = Task {
            replacementStarted.fulfill()
            await workflow.loadFile(second)
            replaced = true
        }
        await fulfillment(of: [replacementStarted], timeout: 2)
        await commitGate.release()
        await fulfillment(of: [releasing], timeout: 2)
        XCTAssertFalse(replaced); XCTAssertTrue(workflow.isBusy)
        XCTAssertEqual(host.commits, 0)
        await releaseGate.release()
        await cleanup.value; await caller.value; await replacement.value

        let snapshot = await service.snapshot()
        XCTAssertEqual(snapshot.writes, 1); XCTAssertEqual(host.commits, 1)
        XCTAssertEqual(snapshot.released, [preview.importId])
        XCTAssertEqual(snapshot.rollbacks, 0); XCTAssertEqual(snapshot.libraryReads, 0)
        XCTAssertEqual(snapshot.peakPhysicalCalls, 1)
        XCTAssertTrue(workflow.isOpen); XCTAssertFalse(workflow.isBusy)
        XCTAssertNil(workflow.preview); XCTAssertEqual(workflow.owner, "")
        XCTAssertTrue(host.failures.isEmpty)
        await workflow.close()
    }

    func testDismissedCommitReportsRealFailureWithoutTreatingCancellationAsRollback() async throws {
        let file = try makeFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let service = LibraryImportWorkflowService(), host = LibraryImportWorkflowHost()
        let workflow = host.workflow(service: service)
        let preview = try await prepare(workflow, file: file)
        let committing = expectation(description: "commit entered")
        let gate = LibraryImportWorkflowGate { committing.fulfill() }
        await service.blockCommit(gate, failure: .storageFailed)
        let caller = Task { await workflow.confirmImport() }
        await fulfillment(of: [committing], timeout: 2)
        let cleanup = try XCTUnwrap(workflow.cancelPresentation())
        XCTAssertFalse(workflow.isOpen); XCTAssertTrue(workflow.isBusy)
        await gate.release(); await cleanup.value; await caller.value
        let snapshot = await service.snapshot()
        XCTAssertEqual(snapshot.released, [preview.importId])
        XCTAssertEqual(snapshot.rollbacks, 0); XCTAssertEqual(host.commits, 0)
        XCTAssertEqual(host.failures.count, 1)
        XCTAssertEqual(host.failures.first?.error as? MusicServiceError, .storageFailed)
        XCTAssertEqual(host.failures.first?.reportWasCancelled, false)
        XCTAssertFalse(workflow.isOpen); XCTAssertFalse(workflow.isBusy)
        await workflow.close()
    }

    func testCloseWaitsLatePreviewReleaseAndRejectsNewWork() async throws {
        let file = try makeFile()
        defer { try? FileManager.default.removeItem(at: file) }
        let service = LibraryImportWorkflowService(), host = LibraryImportWorkflowHost()
        let workflow = host.workflow(service: service)
        await workflow.loadFile(file); workflow.owner = "synthetic-owner"
        let preparing = expectation(description: "prepare entered")
        let releasing = expectation(description: "close release entered")
        let prepareGate = LibraryImportWorkflowGate { preparing.fulfill() }
        let releaseGate = LibraryImportWorkflowGate { releasing.fulfill() }
        await service.blockPrepare(prepareGate); await service.blockRelease(releaseGate)
        let preview = Task { await workflow.previewImport() }
        await fulfillment(of: [preparing], timeout: 2)
        let closeStarted = expectation(description: "close requested")
        var closed = false, secondClosed = false
        let close = Task {
            closeStarted.fulfill()
            await workflow.close()
            closed = true
        }
        await fulfillment(of: [closeStarted], timeout: 2)
        let secondClose = Task { await workflow.close(); secondClosed = true }
        XCTAssertFalse(workflow.isOpen); XCTAssertNil(workflow.preview)
        XCTAssertTrue(workflow.isBusy); XCTAssertFalse(closed)
        await prepareGate.release()
        await fulfillment(of: [releasing], timeout: 2)
        XCTAssertFalse(closed); XCTAssertFalse(secondClosed)
        await releaseGate.release()
        await preview.value; await close.value; await secondClose.value
        XCTAssertTrue(closed); XCTAssertTrue(secondClosed)
        XCTAssertFalse(workflow.isBusy); XCTAssertFalse(workflow.isOpen)
        await workflow.loadFile(file); workflow.owner = "unused"
        await workflow.previewImport(); await workflow.confirmImport()
        let snapshot = await service.snapshot()
        XCTAssertEqual(snapshot.inputs.count, 1); XCTAssertEqual(snapshot.released, ["preview-1"])
        XCTAssertTrue(snapshot.commits.isEmpty); XCTAssertTrue(host.failures.isEmpty)
    }

    func testAccessInvalidationSuppressesLateCommitErrorWithoutClearingReplacement() async throws {
        let first = try makeFile(), second = try makeFile(Data("new-access-archive".utf8))
        defer {
            try? FileManager.default.removeItem(at: first)
            try? FileManager.default.removeItem(at: second)
        }
        let service = LibraryImportWorkflowService(), host = LibraryImportWorkflowHost()
        let workflow = host.workflow(service: service)
        let preview = try await prepare(workflow, file: first)
        let committing = expectation(description: "old generation commit entered")
        let gate = LibraryImportWorkflowGate { committing.fulfill() }
        await service.blockCommit(gate, failure: .storageFailed)
        let commit = Task { await workflow.confirmImport() }
        await fulfillment(of: [committing], timeout: 2)
        host.available = false; host.generation = UUID(); workflow.invalidate()
        host.available = true; host.generation = UUID(); workflow.invalidate()
        XCTAssertFalse(workflow.isOpen); XCTAssertTrue(workflow.isBusy)
        let replacementStarted = expectation(description: "new generation replacement requested")
        let replacement = Task {
            replacementStarted.fulfill()
            await workflow.loadFile(second)
        }
        await fulfillment(of: [replacementStarted], timeout: 2)
        await gate.release(); await commit.value; await replacement.value
        XCTAssertTrue(workflow.isOpen); XCTAssertFalse(workflow.isBusy)
        XCTAssertTrue(host.failures.isEmpty); XCTAssertEqual(host.commits, 0)
        workflow.owner = "new-owner"; await workflow.previewImport()
        let snapshot = await service.snapshot()
        XCTAssertEqual(snapshot.inputs.last?.data, Data("new-access-archive".utf8))
        XCTAssertEqual(snapshot.released, [preview.importId])
        XCTAssertEqual(snapshot.peakPhysicalCalls, 1)
        await workflow.close()
    }

    func testReleaseFailureBlocksReplacementWithoutRetryingOrDiscardingToken() async throws {
        let first = try makeFile(), second = try makeFile(Data("next-archive".utf8))
        defer {
            try? FileManager.default.removeItem(at: first)
            try? FileManager.default.removeItem(at: second)
        }
        let service = LibraryImportWorkflowService(), host = LibraryImportWorkflowHost()
        let workflow = host.workflow(service: service)
        let preview = try await prepare(workflow, file: first)
        await service.failNextRelease(.storageFailed)
        await workflow.loadFile(second)
        let failed = await service.snapshot()
        XCTAssertEqual(failed.releaseAttempts, [preview.importId])
        XCTAssertTrue(failed.released.isEmpty)
        XCTAssertFalse(workflow.isOpen); XCTAssertFalse(workflow.isBusy)
        XCTAssertEqual(host.failures.count, 1)
        XCTAssertEqual(host.failures.first?.error as? MusicServiceError, .storageFailed)
        XCTAssertEqual(host.failures.first?.operation, "释放曲库导入预览")
        XCTAssertEqual(host.commits, 0)

        await workflow.loadFile(second)
        XCTAssertTrue(workflow.isOpen); XCTAssertFalse(workflow.isBusy)
        let cleaned = await service.snapshot()
        XCTAssertEqual(cleaned.releaseAttempts, [preview.importId, preview.importId])
        XCTAssertEqual(cleaned.released, [preview.importId])
        workflow.owner = "new-owner"; await workflow.previewImport()
        let prepared = await service.snapshot()
        XCTAssertEqual(prepared.inputs.count, 2)
        XCTAssertEqual(prepared.inputs.last?.data, Data("next-archive".utf8))
        await workflow.close()
    }

    private func prepare(_ workflow: MusicLibraryImportWorkflow, file: URL) async throws -> MusicImportPreview {
        await workflow.loadFile(file); workflow.owner = "synthetic-owner"
        await workflow.previewImport()
        return try XCTUnwrap(workflow.preview)
    }

    private func makeFile(_ data: Data = Data("synthetic-archive".utf8)) throws -> URL {
        let file = FileManager.default.temporaryDirectory
            .appendingPathComponent("meow-library-workflow-" + UUID().uuidString + ".json")
        try data.write(to: file)
        return file
    }
}

@MainActor private final class LibraryImportWorkflowHost {
    struct Failure {
        let error: Error
        let generation: UUID
        let operation: String
        let reportWasCancelled: Bool
    }
    var available = true, closed = false, generation = UUID()
    var commits = 0
    var failures: [Failure] = []
    func workflow(service: (any MusicServing)?) -> MusicLibraryImportWorkflow {
        MusicLibraryImportWorkflow(service: service,
            context: { (self.available, self.closed, self.generation) },
            didCommit: { self.commits += 1 },
            report: { error, generation, operation in
                self.failures.append(.init(error: error, generation: generation, operation: operation,
                                           reportWasCancelled: Task.isCancelled))
            })
    }
}

private actor LibraryImportWorkflowGate {
    private let entered: @Sendable () -> Void
    private var released = false
    private var waiters: [CheckedContinuation<Void, Never>] = []
    init(_ entered: @escaping @Sendable () -> Void) { self.entered = entered }
    func wait() async {
        entered()
        if !released { await withCheckedContinuation { waiters.append($0) } }
    }
    func release() {
        released = true
        let pending = waiters; waiters = []
        pending.forEach { $0.resume() }
    }
}

/// Cancellation-insensitive business fixture, not a native archive or store.
private actor LibraryImportWorkflowService: MusicServing {
    struct Input: Sendable {
        let data: Data
        let sourceID: String
        let owner: String
    }
    struct Snapshot: Sendable {
        let inputs: [Input]
        let commits: [String]
        let releaseAttempts: [String]
        let released: [String]
        let writes: Int
        let rollbacks: Int
        let libraryReads: Int
        let peakPhysicalCalls: Int
        let cancellation: [Bool]
    }
    private var inputs: [Input] = [], commits: [String] = []
    private var releaseAttempts: [String] = [], released: [String] = []
    private var tokens: Set<String> = []
    private var writes = 0, rollbacks = 0, libraryReads = 0
    private var physicalCalls = 0, peakPhysicalCalls = 0
    private var cancellation: [Bool] = []
    private var prepareGate: LibraryImportWorkflowGate?
    private var commitGate: LibraryImportWorkflowGate?
    private var releaseGate: LibraryImportWorkflowGate?
    private var commitFailure: MusicServiceError?
    private var releaseFailure: MusicServiceError?
    func blockPrepare(_ gate: LibraryImportWorkflowGate) { prepareGate = gate }
    func blockCommit(_ gate: LibraryImportWorkflowGate, failure: MusicServiceError? = nil) {
        commitGate = gate; commitFailure = failure
    }
    func blockRelease(_ gate: LibraryImportWorkflowGate) { releaseGate = gate }
    func failNextRelease(_ failure: MusicServiceError) { releaseFailure = failure }
    func snapshot() -> Snapshot {
        .init(inputs: inputs, commits: commits, releaseAttempts: releaseAttempts, released: released,
              writes: writes, rollbacks: rollbacks, libraryReads: libraryReads,
              peakPhysicalCalls: peakPhysicalCalls, cancellation: cancellation)
    }
    private func entered() {
        physicalCalls += 1; peakPhysicalCalls = max(peakPhysicalCalls, physicalCalls)
    }
    private func finished() {
        cancellation.append(Task.isCancelled); physicalCalls -= 1
    }
    func prepareImport(data: Data, sourceId: String, ownerId: String) async throws -> MusicImportPreview {
        entered(); defer { finished() }
        inputs.append(.init(data: data, sourceID: sourceId, owner: ownerId))
        let id = "preview-\(inputs.count)", gate = prepareGate
        prepareGate = nil
        if let gate { await gate.wait() }
        tokens.insert(id)
        return .init(importId: id, report: .init(batchId: "batch-\(id)", alreadyApplied: writes > 0,
                                                songs: 0, collections: 0, members: 0))
    }
    func commitImport(_ preview: MusicImportPreview) async throws -> MusicImportReport {
        entered(); defer { finished() }
        commits.append(preview.importId)
        let gate = commitGate, failure = commitFailure
        commitGate = nil; commitFailure = nil
        if let gate { await gate.wait() }
        guard tokens.contains(preview.importId) else { throw MusicServiceError.importNotFound }
        if let failure {
            if failure == .commitUnconfirmed { writes += 1 }
            throw failure
        }
        writes += 1
        return preview.report
    }
    func releaseImport(_ preview: MusicImportPreview) async throws {
        entered(); defer { finished() }
        releaseAttempts.append(preview.importId)
        let gate = releaseGate, failure = releaseFailure
        releaseGate = nil; releaseFailure = nil
        if let gate { await gate.wait() }
        if let failure { throw failure }
        guard tokens.remove(preview.importId) != nil else { throw MusicServiceError.importNotFound }
        released.append(preview.importId)
    }
    func state() -> MusicPlaybackState { libraryReads += 1; return .init() }
    func collections() -> [MusicCollection] { libraryReads += 1; return [] }
    func rollbackImport(batchId: String) throws { rollbacks += 1; throw MusicServiceError.unsupported }
    func platforms() -> [MusicPlatform] { [] }
    func search(platform: String, query: String, page: Int) throws -> MusicSearchPage { throw MusicServiceError.unsupported }
    func tracks(in collection: MusicCollection) throws -> [MusicRemoteTrack] { throw MusicServiceError.unsupported }
    func createPlaylist(name: String, refs: [MusicReference]) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func removePlaylist(_ collection: MusicCollection) throws { throw MusicServiceError.unsupported }
    func queue() throws -> MusicQueueSnapshot { throw MusicServiceError.unsupported }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) throws -> MusicAdvanceResult { throw MusicServiceError.unsupported }
    func updateSettings(_ change: MusicSettingsChange) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func playbackOptions(for ref: MusicReference) throws -> [MusicPlaybackOption] { throw MusicServiceError.unsupported }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) throws -> MusicPlaybackCandidate { throw MusicServiceError.unsupported }
    func cancel(attemptId: String?) {}
    func record(_ fact: MusicPlaybackFact) throws -> Bool { throw MusicServiceError.unsupported }
    func lyrics(occurrenceId: String, sourceKey: String) throws -> MusicLyricsSnapshot { throw MusicServiceError.unsupported }
    func playedSongs() -> [MusicPlayedSong] { [] }
    func playedPlaylists() -> [MusicPlayedPlaylist] { [] }
    func restoreHistory(id: String, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
}
#endif
