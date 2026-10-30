#if os(iOS) || os(macOS)
import Foundation
import Combine
import MeowStorage

@MainActor public final class MusicCredentialImportViewModel: ObservableObject {
    @Published public private(set) var platform = MusicAccountPlatform.qq
    @Published public private(set) var format = MusicCredentialImportFormat.iwebPlusSnapshot
    @Published public private(set) var preview: MusicCredentialImportPreview?
    @Published public private(set) var completed: MusicAccountStatus?
    @Published public private(set) var error: MusicCredentialImportError?
    @Published public private(set) var isBusy = false
    private let service: any MusicCredentialsImporting
    private let onSessionChanged: @MainActor (MusicAccountPlatform) -> Void
    private let readFile: @Sendable (URL, Int) async throws -> Data
    private var active = false, available = false
    private var externalGeneration: UUID?, epoch = UUID(), requestID: UUID?
    private var input: MusicCredentialImportInput?
    private var operation: Task<Void, Never>?

    public init(service: any MusicCredentialsImporting,
                onSessionChanged: @escaping @MainActor (MusicAccountPlatform) -> Void = { _ in },
                readFile: @escaping @Sendable (URL, Int) async throws -> Data = {
                    try await ImportedFileReader.read($0, maximumBytes: $1)
                }) {
        self.service = service; self.onSessionChanged = onSessionChanged; self.readFile = readFile
    }
    public var formats: [MusicCredentialImportFormat] {
        platform == .qq ? MusicCredentialImportFormat.allCases : [.iwebPlusSnapshot, .meowMusicRecord]
    }
    public func activate(isAvailable: Bool, accessGeneration: UUID?) {
        active = true
        setAccess(isAvailable: isAvailable, accessGeneration: accessGeneration)
    }
    public func setAccess(isAvailable: Bool, accessGeneration: UUID?) {
        guard available != isAvailable || externalGeneration != accessGeneration else { return }
        available = isAvailable; externalGeneration = accessGeneration; invalidate()
    }
    public func deactivate() { active = false; invalidate() }
    public func cancel() { invalidate() }
    public func selectPlatform(_ value: MusicAccountPlatform) {
        guard platform != value else { return }
        invalidate(); platform = value
        if !formats.contains(format) { format = .iwebPlusSnapshot }
    }
    public func selectFormat(_ value: MusicCredentialImportFormat) {
        guard formats.contains(value), format != value else { return }
        invalidate(); format = value
    }
    public func dismissError() { error = nil }
    public func rejectOversizedInput() { invalidate(); error = .sizeLimit }
    private func invalidate() {
        let old = requestID, service = service
        epoch = UUID(); requestID = nil
        input?.revoke(); input = nil
        operation?.cancel(); operation = nil
        preview = nil; completed = nil; error = nil; isBusy = false
        if let old { Task { await service.cancelCredentialImport(id: old) } }
    }
    private func current(_ own: UUID) -> Bool { active && available && epoch == own && !Task.isCancelled }
    public func previewText(_ text: String) {
        guard active, available, !isBusy else { return }
        do {
            // Convert immediately, before any Task captures the source string.
            let value = try MusicCredentialImportInput(data: Data(text.utf8))
            load { value }
        } catch { self.error = .sanitized(error) }
    }
    public func previewFile(_ url: URL) {
        guard active, available, !isBusy else { return }
        let readFile = readFile
        load {
            let bytes = try await readFile(url, MusicCredentialImportInput.maximumBytes)
            return try MusicCredentialImportInput(data: bytes)
        }
    }
    public func fileSelectionFailed() {
        guard active, available else { return }
        invalidate(); error = .unavailable
    }
    private func load(_ obtain: @escaping @Sendable () async throws -> MusicCredentialImportInput) {
        invalidate()
        let own = epoch, id = UUID(), platform = platform, format = format, service = service
        requestID = id; isBusy = true
        operation = Task { [weak self] in
            guard let self, current(own) else { return }
            do {
                let value = try await obtain()
                defer { value.revoke() }
                guard current(own) else { value.revoke(); return }
                input = value
                let result = try await service.previewCredentialImport(value, platform: platform, format: format, requestID: id)
                value.revoke()
                guard current(own), requestID == id else {
                    await service.cancelCredentialImport(id: id); return
                }
                input = nil
                guard result.id == id, result.platform == platform else {
                    throw MusicCredentialImportError.invalidFormat
                }
                preview = result
            } catch {
                if current(own), requestID == id { input?.revoke(); input = nil }
                await service.cancelCredentialImport(id: id)
                guard current(own), requestID == id else { return }
                requestID = nil
                if let file = error as? ImportedFileError {
                    self.error = file == .tooLarge ? .sizeLimit : file == .cancelled ? .cancelled : .invalidFormat
                } else { self.error = .sanitized(error) }
            }
            if current(own) { isBusy = false; operation = nil }
        }
    }
    public func commit(replaceExisting: Bool) {
        guard active, available, !isBusy, let preview, requestID == preview.id else { return }
        if preview.requiresReplacement && !replaceExisting { error = .confirmationRequired; return }
        let own = epoch, id = preview.id, platform = preview.platform, service = service
        isBusy = true; error = nil
        operation = Task { [weak self] in
            guard let self, current(own) else { return }
            do {
                let status = try await service.commitCredentialImport(id: id, replaceExisting: replaceExisting)
                guard status.platform == platform, status.phase == .requiresValidation, status.nickname.isEmpty else {
                    throw MusicCredentialImportError.invalidFormat
                }
                // Confirmed writes outlive their presentation; only UI publication is fenced below.
                onSessionChanged(platform)
                guard current(own), requestID == id else { return }
                completed = .init(platform: platform, phase: .requiresValidation, canLogin: status.canLogin)
            } catch {
                guard current(own), requestID == id else { return }
                self.error = .sanitized(error)
            }
            await service.cancelCredentialImport(id: id)
            if current(own), requestID == id {
                requestID = nil; self.preview = nil; isBusy = false; operation = nil
            }
        }
    }
    func flush() async { await operation?.value }
    var pendingOperation: Task<Void, Never>? { operation }
    deinit { input?.revoke(); operation?.cancel() }
}
#endif
