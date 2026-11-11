#if os(iOS)
import Foundation
import Combine
import MeowStorage

private final class MusicAccountExportStagingFile {
    let directory: URL
    init(directory: URL) { self.directory = directory }
    func remove() throws {
        if FileManager.default.fileExists(atPath: directory.path) { try FileManager.default.removeItem(at: directory) }
    }
    deinit { try? remove() }
}

/// Page lifetime only. Secrets remain in the dedicated payload and the native owner plan.
@MainActor
final class MusicAccountTransferState: ObservableObject {
    @Published private(set) var busy = false
    @Published private(set) var summary: MusicAccountImportSummary?
    let service: (any MusicAccountTransferring)?
    private let owner: MusicViewModel
    private var active = false
    private var operation = UUID()
    var selectionID: UUID { operation }
    private var access: UUID?
    private var task: Task<Void, Never>?
    private(set) var plan: MusicAccountImportPlan?
    private var committing = false
    private var stagingFile: MusicAccountExportStagingFile?
    var temporaryDirectory: URL? { stagingFile?.directory }
    var confirm: ((MusicAccountImportPlan) -> Void)?
    var exported: ((URL) -> Void)?
    var result: ((String) -> Void)?

    init(owner: MusicViewModel) {
        self.owner = owner
        service = owner.accounts as? any MusicAccountTransferring
    }
    var available: Bool { active && owner.isAvailable && !owner.isClosed && service != nil }
    func start() { active = true; access = owner.accountAccessGeneration }
    func reconcileAccess() {
        guard active else { return }
        if !owner.isAvailable || owner.isClosed || access != owner.accountAccessGeneration {
            selectionCancelled(); access = owner.accountAccessGeneration
        }
    }
    private func current(_ id: UUID) -> Bool {
        active && available && operation == id && access == owner.accountAccessGeneration && !Task.isCancelled
    }
    func beginSelection() -> Bool {
        guard available, !busy else { return false }
        busy = true; summary = nil; operation = UUID()
        return true
    }
    func importFile(_ url: URL) {
        guard busy, let service else { return }
        let id = operation
        task = Task { [weak self] in
            guard let self else { return }
            do {
                let bytes = try await ImportedFileReader.read(url, maximumBytes: MusicAccountFilePayload.maximumBytes)
                guard current(id) else { return }
                let next = try await service.prepareAccountImport(MusicAccountFilePayload(fileData: bytes))
                guard current(id) else { next.cancel(); await service.discardAccountImport(next); return }
                plan = next
                if next.conflicts.isEmpty { commit(.overwrite) }
                else { confirm?(next) }
            } catch {
                guard current(id) else { return }
                busy = false; result?(Self.message(error))
            }
        }
    }
    func commit(_ policy: MusicAccountImportPolicy) {
        guard let plan, let service, !committing else { return }
        committing = true
        let id = operation
        task = Task { [weak self] in
            guard let self, current(id) else { return }
            let report = await service.importAccounts(plan, policy: policy)
            guard current(id) else { return }
            self.plan = nil; committing = false; busy = false; summary = report
            result?(report.message)
        }
    }
    func exportFile() {
        guard beginSelection(), let service else { return }
        let id = operation
        task = Task { [weak self] in
            guard let self else { return }
            do {
                let payload = try await service.exportAccountFile()
                guard current(id) else { return }
                let url = try makeTemporaryFile(payload)
                guard current(id) else { cleanupExport(); return }
                exported?(url)
            } catch {
                guard current(id) else { return }
                cleanupExport(); busy = false; result?(Self.message(error))
            }
        }
    }
    private func makeTemporaryFile(_ payload: MusicAccountFilePayload) throws -> URL {
        cleanupExport()
        guard stagingFile == nil else { throw MusicAccountTransferError.fileOperation }
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("music-account-export-" + UUID().uuidString, isDirectory: true)
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false,
                attributes: [.protectionKey: FileProtectionType.complete])
            stagingFile = MusicAccountExportStagingFile(directory: directory)
            let file = directory.appendingPathComponent("meowfilm-music-accounts.json")
            try payload.dataForFile().write(to: file, options: [.atomic, .completeFileProtection])
            return file
        } catch { cleanupExport(); throw MusicAccountTransferError.fileOperation }
    }
    func selectionCancelled() {
        operation = UUID(); task?.cancel(); task = nil
        if let plan, let service {
            plan.cancel(); Task { await service.discardAccountImport(plan) }
        }
        plan = nil; committing = false; busy = false; summary = nil; cleanupExport()
    }
    func exportFinished() {
        cleanupExport(); busy = false
        if stagingFile != nil { result?(MusicAccountTransferError.fileOperation.localizedDescription) }
    }
    private func cleanupExport() {
        if let stagingFile {
            do {
                try stagingFile.remove()
                self.stagingFile = nil
            } catch {
                // Retain ownership for the next cleanup attempt. Never delete destination URLs.
            }
        }
    }
    func close() { active = false; selectionCancelled() }
    private static func message(_ error: Error) -> String {
        if let error = error as? MusicAccountTransferError { return error.localizedDescription }
        if let error = error as? ImportedFileError { return error.localizedDescription }
        if let error = error as? MusicServiceError { return error.message(operation: "账号导入导出") }
        return MusicAccountTransferError.fileOperation.localizedDescription
    }
    var pendingOperation: Task<Void, Never>? { task }
}
#endif
