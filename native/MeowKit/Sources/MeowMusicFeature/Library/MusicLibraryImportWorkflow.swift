#if os(iOS) || os(macOS)
import Foundation
import Combine
import CryptoKit
import MeowStorage

@MainActor public final class MusicLibraryImportWorkflow: ObservableObject {
    @Published public private(set) var preview: MusicImportPreview?
    @Published public private(set) var isBusy = false
    @Published public private(set) var isOpen = false
    @Published public var owner = ""

    private let service: (any MusicServing)?
    private let context: @MainActor () -> (available: Bool, closed: Bool, generation: UUID)
    private let didCommit: @MainActor () async -> Void
    private let report: @MainActor (Error, UUID, String) -> Void
    private var data: Data?
    private var sourceID: String?
    private var preparedOwner: String?
    private var sessionID = UUID()
    private var sessionGeneration: UUID?
    private var operationID = UUID()
    private var operationGeneration: UUID?
    private var operation: Task<Void, Never>?
    private var pendingRelease: (preview: MusicImportPreview, failure: Error?)?
    private var closed = false

    public init(service: (any MusicServing)?,
                context: @escaping @MainActor () -> (available: Bool, closed: Bool, generation: UUID),
                didCommit: @escaping @MainActor () async -> Void,
                report: @escaping @MainActor (Error, UUID, String) -> Void) {
        self.service = service; self.context = context
        self.didCommit = didCommit; self.report = report
    }

    public func loadFile(_ url: URL) async {
        let access = context()
        guard !closed, access.available, !access.closed, !Task.isCancelled else { return }
        let previous = cancelPresentation(), session = sessionID
        let id = beginOperation(generation: access.generation)
        let task = Task { [self] in
            defer { finish(id, generation: access.generation) }
            await previous?.value
            do {
                guard service != nil else { throw MusicServiceError.unavailable }
                if let failure = pendingRelease?.failure {
                    if currentOperation(id, generation: access.generation) {
                        report(failure, access.generation, "释放曲库导入预览")
                    }
                    return
                }
                guard pendingRelease == nil,
                      currentSession(session, operation: id, generation: access.generation) else { return }
                let bytes = try await ImportedFileReader.read(url, maximumBytes: 7 * 1024 * 1024)
                guard currentSession(session, operation: id, generation: access.generation) else { return }
                data = bytes
                sourceID = "archive-sha256:" + SHA256.hash(data: bytes).map { String(format: "%02x", $0) }.joined()
                sessionGeneration = access.generation
                isOpen = true
            } catch {
                if currentSession(session, operation: id, generation: access.generation) {
                    report(error, access.generation, "读取曲库文件")
                }
            }
        }
        operation = task
        await task.value
    }

    public func previewImport() async {
        let access = context()
        guard !closed, access.available, !access.closed, !Task.isCancelled else { return }
        guard let data, let sourceID, !owner.isEmpty, !isBusy else { return }
        guard sessionGeneration == access.generation else { invalidate(); return }
        guard let service else { return }
        let session = sessionID, inputOwner = owner, previous = operation
        if let preview { pendingRelease = (preview, nil) }
        preview = nil; preparedOwner = nil
        let id = beginOperation(generation: access.generation)
        let task = Task { [self] in
            defer { finish(id, generation: access.generation) }
            await previous?.value
            guard await releasePending(using: service, id: id, generation: access.generation),
                  currentSession(session, operation: id, generation: access.generation) else { return }
            do {
                let value = try await service.prepareImport(data: data, sourceId: sourceID, ownerId: inputOwner)
                if currentSession(session, operation: id, generation: access.generation), owner == inputOwner {
                    preview = value; preparedOwner = inputOwner
                } else {
                    pendingRelease = (value, nil)
                    _ = await releasePending(using: service, id: id, generation: access.generation)
                }
            } catch {
                if currentSession(session, operation: id, generation: access.generation) {
                    report(error, access.generation, "预览曲库导入")
                }
            }
        }
        operation = task
        await task.value
    }

    public func confirmImport() async {
        let access = context()
        guard !closed, access.available, !access.closed, !Task.isCancelled, !isBusy else { return }
        guard sessionGeneration == access.generation else { invalidate(); return }
        guard let preview, let service, owner == preparedOwner else { return }
        let session = sessionID, previous = operation
        // The commit task now owns this token; dismissal must not release it
        // while a transaction is still using it.
        self.preview = nil; preparedOwner = nil
        let id = beginOperation(generation: access.generation)
        let task = Task { [self] in
            defer { finish(id, generation: access.generation) }
            await previous?.value
            guard currentSession(session, operation: id, generation: access.generation) else {
                pendingRelease = (preview, nil)
                _ = await releasePending(using: service, id: id, generation: access.generation)
                return
            }
            var committed = false
            do {
                _ = try await service.commitImport(preview)
                committed = true
                if currentSession(session, operation: id, generation: access.generation) { clearSession() }
            } catch {
                // This task is not cancelled with its caller. In particular,
                // an unconfirmed write must not be sanitized into cancellation.
                if currentOperation(id, generation: access.generation) {
                    report(error, access.generation, "导入曲库")
                }
            }
            pendingRelease = (preview, nil)
            _ = await releasePending(using: service, id: id, generation: access.generation)
            // Closing an import presentation is not a rollback of a confirmed
            // write. Notify its access owner without touching a newer session.
            if committed, currentAccess(access.generation) { await didCommit() }
        }
        operation = task
        await task.value
    }

    public func cancel() async {
        await cancelPresentation()?.value
    }

    /// Revoke the session synchronously, but retain the exact physical task
    /// until reading, preparation, commit and token release actually finish.
    @discardableResult
    public func cancelPresentation() -> Task<Void, Never>? {
        guard !closed else { return operation }
        let access = context(), previous = operation
        let parkedPreview = preview
        sessionID = UUID()
        clearSession()
        if let parkedPreview { pendingRelease = (parkedPreview, nil) }
        if parkedPreview == nil, let previous, operationGeneration == access.generation {
            return previous
        }
        guard previous != nil || pendingRelease != nil else { isBusy = false; return nil }
        let id = beginOperation(generation: access.generation)
        let task = Task { [self] in
            defer { finish(id, generation: access.generation) }
            await previous?.value
            if let service {
                _ = await releasePending(using: service, id: id, generation: access.generation)
            }
        }
        operation = task
        return task
    }

    public func invalidate() {
        _ = cancelPresentation()
    }

    public func close() async {
        if !closed {
            let task = cancelPresentation()
            closed = true
            await task?.value
        } else {
            await operation?.value
        }
    }

    private func clearSession() {
        data = nil; sourceID = nil; sessionGeneration = nil; preparedOwner = nil
        preview = nil; owner = ""; isOpen = false
    }

    private func beginOperation(generation: UUID) -> UUID {
        let id = UUID()
        operationID = id; operationGeneration = generation; isBusy = true
        return id
    }

    private func finish(_ id: UUID, generation: UUID) {
        guard operationID == id, operationGeneration == generation,
              context().generation == generation || closed else { return }
        operation = nil; operationGeneration = nil; isBusy = false
    }

    private func currentAccess(_ generation: UUID) -> Bool {
        let access = context()
        return access.available && !access.closed && access.generation == generation
    }

    private func currentOperation(_ id: UUID, generation: UUID) -> Bool {
        operationID == id && operationGeneration == generation && currentAccess(generation)
    }

    private func currentSession(_ session: UUID, operation id: UUID, generation: UUID) -> Bool {
        !closed && sessionID == session && currentOperation(id, generation: generation)
    }

    private func releasePending(using service: any MusicServing, id: UUID, generation: UUID) async -> Bool {
        guard let value = pendingRelease?.preview else { return true }
        do {
            try await service.releaseImport(value)
            if pendingRelease?.preview.importId == value.importId { pendingRelease = nil }
            return true
        } catch {
            // A failed release still owns a token. A later explicit operation
            // may clean it up, but no new prepare can bypass it.
            if pendingRelease?.preview.importId == value.importId { pendingRelease?.failure = error }
            if currentOperation(id, generation: generation) {
                report(error, generation, "释放曲库导入预览")
            }
            return false
        }
    }
}
#endif
