#if os(iOS) || os(macOS)
import Foundation
import Combine
import MeowStorage

/// One list of built-in and script sources. Built-in sources are playback
/// settings owned by the music service; scripts are owned by the script owner.
/// Neither is a global "current source", and the page never stores drafts.
@MainActor
final class MusicSourceManagementState: ObservableObject {
    @Published private(set) var official: [MusicAccountPlatform] = MusicAccountPlatform.allCases
    @Published private(set) var scripts: [MusicScriptSourceItem] = []
    @Published private(set) var available = false
    @Published private(set) var savingOfficial = false
    @Published private(set) var importing = false
    @Published private(set) var pending: [String: Bool] = [:]
    @Published var error: String?
    let model: MusicViewModel
    var managesScripts: Bool { model.scriptSources != nil }
    private var active = false
    private var generation: UUID?
    private var officialWork: Task<Void, Never>?
    private var work: [UUID: Task<Void, Never>] = [:]
    private var readID = UUID()

    init(model: MusicViewModel) {
        self.model = model
        official = model.state.officialSources
    }

    func start() {
        active = true
        reconcileAccess()
    }

    func close() {
        active = false
        available = false
        error = nil
        cancelWork()
    }

    func reconcileAccess() {
        let next = active && model.isAvailable && !model.isClosed
        let access = model.accountAccessGeneration
        let changed = generation != access || available != next
        if changed { cancelWork(); error = nil }
        generation = access
        if available != next { available = next }
        reconcileSettings()
        if changed {
            if next { reload() } else { scripts = [] }
        }
    }

    func reconcileSettings() {
        let next = model.state.officialSources
        if official != next { official = next }
    }

    func officialEnabled(_ platform: MusicAccountPlatform) -> Bool { official.contains(platform) }

    func setOfficial(_ platform: MusicAccountPlatform, enabled: Bool) {
        guard admitted, officialWork == nil, enabled != officialEnabled(platform) else { return }
        let values = MusicAccountPlatform.allCases.filter {
            $0 == platform ? enabled : official.contains($0)
        }
        let change = MusicSettingsChange(ifRevision: model.state.settingsRevision, officialSources: values)
        let access = generation
        savingOfficial = true
        officialWork = Task { [weak self] in
            guard let self else { return }
            defer { officialWork = nil; savingOfficial = false; reconcileSettings() }
            guard !Task.isCancelled, active, available, access == model.accountAccessGeneration else { return }
            // Write failures are reported by the playback-settings owner.
            await model.updateSettings(change)
        }
    }

    /// The switch shows the requested value while its own write is in flight.
    func scriptEnabled(_ item: MusicScriptSourceItem) -> Bool { pending[item.id] ?? item.enabled }
    func isPending(_ id: String) -> Bool { pending[id] != nil }
    func item(_ id: String) -> MusicScriptSourceItem? { scripts.first { $0.id == id } }

    func setScript(_ id: String, enabled: Bool) {
        guard admitted, let service = model.scriptSources, pending[id] == nil,
              let current = item(id), current.enabled != enabled else { return }
        pending[id] = enabled
        readID = UUID()
        let access = generation
        run { [weak self] in
            do {
                let value = try await service.setScriptSourceEnabled(id, enabled: enabled)
                guard let self, self.current(access) else { return }
                self.pending[id] = nil
                self.replace(value)
            } catch {
                guard let self, self.current(access) else { return }
                self.pending[id] = nil
                self.fail(error)
                self.reload()
            }
        }
    }

    func remove(_ id: String) {
        guard admitted, let service = model.scriptSources, pending[id] == nil, item(id) != nil else { return }
        pending[id] = false
        readID = UUID()
        let access = generation
        run { [weak self] in
            do {
                try await service.removeScriptSource(id)
                guard let self, self.current(access) else { return }
                self.pending[id] = nil
                self.scripts.removeAll { $0.id == id }
            } catch {
                guard let self, self.current(access) else { return }
                self.pending[id] = nil
                self.fail(error)
                self.reload()
            }
        }
    }

    func importFiles(_ urls: [URL]) {
        guard admitted, let service = model.scriptSources, !importing, !urls.isEmpty else { return }
        let access = generation
        importing = true
        readID = UUID()
        run { [weak self] in
            var failure: Error?
            for url in urls {
                do {
                    let data = try await ImportedFileReader.read(url, maximumBytes: MusicScriptSourceItem.maximumSourceBytes)
                    let value = try await service.importScriptSource(data, fileName: url.lastPathComponent)
                    guard let self, self.current(access) else { return }
                    self.replace(value)
                } catch {
                    failure = failure ?? error
                    if MusicServiceError.sanitized(error) == .cancelled { break }
                }
            }
            guard let self, self.current(access) else { return }
            self.importing = false
            if let failure { self.fail(failure) }
        }
    }

    func download(_ text: String) {
        guard admitted, let service = model.scriptSources, !importing else { return }
        guard let url = URL(string: text.trimmingCharacters(in: .whitespacesAndNewlines)), url.scheme != nil else {
            fail(MusicServiceError.insecureScriptURL); return
        }
        let access = generation
        importing = true
        readID = UUID()
        run { [weak self] in
            do {
                let value = try await service.downloadScriptSource(from: url)
                guard let self, self.current(access) else { return }
                self.importing = false
                self.replace(value)
            } catch {
                guard let self, self.current(access) else { return }
                self.importing = false
                self.fail(error)
            }
        }
    }

    func reload() {
        guard admitted, let service = model.scriptSources else { return }
        let access = generation
        let id = UUID(); readID = id
        run { [weak self] in
            do {
                let values = try await service.scriptSources()
                guard let self, self.current(access), self.readID == id else { return }
                self.scripts = values
            } catch {
                guard let self, self.current(access), self.readID == id else { return }
                self.fail(error)
            }
        }
    }

    /// Waits for admitted page operations (also used by lifecycle tests).
    func flush() async {
        while officialWork != nil || !work.isEmpty {
            await officialWork?.value
            for task in Array(work.values) { await task.value }
        }
    }

    private var admitted: Bool {
        active && available && generation == model.accountAccessGeneration && model.isAvailable && !model.isClosed
    }

    private func current(_ access: UUID?) -> Bool {
        !Task.isCancelled && active && available && access == generation && access == model.accountAccessGeneration
    }

    private func replace(_ value: MusicScriptSourceItem) {
        if let index = scripts.firstIndex(where: { $0.id == value.id }) { scripts[index] = value }
        else { scripts.append(value) }
    }

    private func fail(_ error: Error) {
        if let error = error as? ImportedFileError {
            if error != .cancelled { self.error = error.localizedDescription }
            return
        }
        let failure = MusicServiceError.sanitized(error)
        guard failure != .cancelled else { return }
        self.error = failure.localizedDescription
    }

    private func run(_ body: @escaping @MainActor () async -> Void) {
        let id = UUID()
        work[id] = Task { [weak self] in
            await body()
            self?.work[id] = nil
        }
    }

    private func cancelWork() {
        readID = UUID()
        officialWork?.cancel(); officialWork = nil; savingOfficial = false
        for task in work.values { task.cancel() }
        work = [:]; pending = [:]; importing = false
    }

    deinit {
        officialWork?.cancel()
        for task in work.values { task.cancel() }
    }
}
#endif
