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
    @Published var selected = Set<String>()
    @Published private(set) var checking = Set<String>()
    @Published private(set) var officialChecks: [MusicAccountPlatform: Bool] = [:]
    @Published private(set) var details: [String: MusicScriptSourceDetail] = [:]
    let model: MusicViewModel
    var managesScripts: Bool { model.scriptSources != nil }
    private var active = false
    private var generation: UUID?
    private var officialWork: Task<Void, Never>?
    private var work: [UUID: Task<Void, Never>] = [:]
    private var readID = UUID()
    private var listRevision = UUID()
    private var detailReadIDs: [String: UUID] = [:]
    private var uncertainDetails = Set<String>()

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
            defer {
                officialWork = nil; savingOfficial = false
                if active, available, access == model.accountAccessGeneration { reconcileSettings() }
            }
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
        listRevision = UUID()
        let access = generation
        run(configurationWrite: true) { [weak self] in
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
        listRevision = UUID()
        let access = generation
        run(configurationWrite: true) { [weak self] in
            do {
                try await service.removeScriptSource(id)
                guard let self, self.current(access) else { return }
                self.pending[id] = nil
                self.listRevision = UUID()
                self.scripts.removeAll { $0.id == id }
                self.selected.remove(id); self.details[id] = nil; self.detailReadIDs[id] = nil
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
        listRevision = UUID()
        run(configurationWrite: true) { [weak self] in
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
            if let failure { self.fail(failure); self.reload() }
        }
    }

    func download(_ text: String) {
        guard admitted, let service = model.scriptSources, !importing else { return }
        guard let url = URL(string: text.trimmingCharacters(in: .whitespacesAndNewlines)), url.scheme != nil else {
            fail(MusicServiceError.insecureScriptURL); return
        }
        let access = generation
        importing = true
        listRevision = UUID()
        run(configurationWrite: true) { [weak self] in
            do {
                let value = try await service.downloadScriptSource(from: url)
                guard let self, self.current(access) else { return }
                self.importing = false
                self.replace(value)
            } catch {
                guard let self, self.current(access) else { return }
                self.importing = false
                self.fail(error)
                self.reload()
            }
        }
    }

    func reload() {
        guard admitted, let service = model.scriptSources else { return }
        let access = generation
        let id = UUID(), revision = listRevision; readID = id
        run { [weak self] in
            do {
                let values = try await service.scriptSources()
                guard let self, self.current(access), self.readID == id else { return }
                // A read spanning a management change cannot replace newer facts.
                // Re-read rather than dropping the reconciliation of uncertain writes.
                guard self.listRevision == revision else { self.reload(); return }
                let previous = Dictionary(uniqueKeysWithValues: self.scripts.map { ($0.id, $0) })
                let next = Dictionary(uniqueKeysWithValues: values.map { ($0.id, $0) })
                let observed = Set(self.details.keys).union(self.detailReadIDs.keys)
                let changed = observed.filter { previous[$0] != next[$0] || self.uncertainDetails.contains($0) }
                self.uncertainDetails = []
                for sourceID in changed {
                    self.detailReadIDs[sourceID] = nil
                    self.details[sourceID] = nil
                }
                self.scripts = values
                for sourceID in changed where next[sourceID] != nil { self.readDetail(sourceID) }
            } catch {
                guard let self, self.current(access), self.readID == id else { return }
                guard self.listRevision == revision else { self.reload(); return }
                self.fail(error)
            }
        }
    }

    func select(_ id: String) {
        if !selected.insert(id).inserted { selected.remove(id) }
    }
    func checkSelected() {
        for id in selected {
            if let platform = MusicAccountPlatform(rawValue: String(id.dropFirst("official:".count))), id.hasPrefix("official:") {
                checkOfficial(platform)
            } else { checkScript(id) }
        }
    }
    func removeSelected() { for id in selected where item(id) != nil { remove(id) } }
    func checkOfficial(_ platform: MusicAccountPlatform) {
        let id = "official:" + platform.rawValue
        guard admitted, !checking.contains(id), let service = model.service as? any MusicOfficialSourceChecking else { return }
        checking.insert(id)
        let access = generation
        run { [weak self] in
            do {
                try await service.checkOfficialSource(platform)
                guard let self, self.current(access) else { return }
                self.officialChecks[platform] = true; self.checking.remove(id)
            } catch {
                guard let self, self.current(access) else { return }
                self.officialChecks[platform] = false; self.checking.remove(id); self.fail(error)
            }
        }
    }
    func readDetail(_ id: String) {
        guard admitted, let service = model.scriptSources, item(id) != nil else { return }
        let access = generation, readID = UUID()
        detailReadIDs[id] = readID
        run { [weak self] in
            do {
                let value = try await service.scriptSourceDetail(id)
                guard let self, self.current(access), self.detailReadIDs[id] == readID,
                      self.item(id) != nil else { return }
                self.details[id] = value
            } catch {
                guard let self, self.current(access), self.detailReadIDs[id] == readID,
                      self.item(id) != nil else { return }
                self.fail(error)
            }
        }
    }
    func checkScript(_ id: String) {
        perform(id, checking: true) { try await $0.checkScriptSource(id) }
    }
    func edit(_ id: String, name: String, url: String?) {
        perform(id) { try await $0.editScriptSource(id, name: name, networkURL: url) }
    }
    func update(_ id: String, file: URL? = nil) {
        perform(id) { service in
            if let file {
                let data = try await ImportedFileReader.read(file, maximumBytes: MusicScriptSourceItem.maximumSourceBytes)
                return try await service.updateScriptSource(id, data: data, fileName: file.lastPathComponent)
            }
            return try await service.updateScriptSource(id)
        }
    }
    func rollback(_ id: String) { perform(id) { try await $0.rollbackScriptSource(id) } }
    private func perform(_ id: String, checking check: Bool = false,
                         operation: @escaping @Sendable (any MusicScriptSourceManaging) async throws -> MusicScriptSourceItem) {
        guard admitted, let service = model.scriptSources, let item = item(id), !isPending(id) else { return }
        pending[id] = item.enabled
        if check { checking.insert(id) }
        listRevision = UUID()
        let access = generation
        run(configurationWrite: !check) { [weak self] in
            do {
                let value = try await operation(service)
                guard let self, self.current(access) else { return }
                self.pending[id] = nil; self.checking.remove(id); self.replace(value); self.readDetail(id)
            } catch {
                guard let self, self.current(access) else { return }
                self.pending[id] = nil; self.checking.remove(id); self.fail(error)
                self.uncertainDetails.insert(id)
                self.reload()
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
        listRevision = UUID()
        detailReadIDs[value.id] = nil
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

    private func run(configurationWrite: Bool = false, _ body: @escaping @MainActor () async -> Void) {
        let id = UUID(), owner = model.service as? any MusicSourcePlaybackServing
        work[id] = Task { [weak self] in
            await body()
            // A committed or uncertain write outlives its page's receipt. This
            // separate task must not inherit UI cancellation/access checks.
            if configurationWrite { await Task { await owner?.sourceConfigurationChanged() }.value }
            self?.work[id] = nil
        }
    }

    private func cancelWork() {
        readID = UUID()
        detailReadIDs = [:]
        uncertainDetails = []
        officialWork?.cancel()
        for task in work.values { task.cancel() }
        work = [:]; pending = [:]; checking = []; importing = false
    }

    deinit {
        officialWork?.cancel()
        for task in work.values { task.cancel() }
    }
}
#endif
