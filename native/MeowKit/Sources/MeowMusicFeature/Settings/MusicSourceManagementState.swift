#if os(iOS) || os(macOS)
import Foundation
import Combine
import MeowStorage

@MainActor
final class MusicSourceManagementState: ObservableObject {
    @Published private(set) var official: [MusicAccountPlatform] = MusicAccountPlatform.allCases
    @Published private(set) var scripts: [MusicScriptSourceItem] = []
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
    var isActive: Bool { active && !model.isClosed }
    private var active = false
    private var visible = false
    private var visiblePage: ObjectIdentifier?
    private var pageGeneration = UUID()
    private var generation = UUID()
    private var officialWork: Task<Void, Never>?
    private var work: [UUID: Task<Void, Never>] = [:]
    private var sourceWork: [String: UUID] = [:]
    private var importWork: UUID?
    private var readID = UUID()
    private var detailReadIDs: [String: UUID] = [:]

    init(model: MusicViewModel) {
        self.model = model
        official = model.state.officialSources
    }

    func start() {
        let wasActive = active
        active = true
        visible = true
        reconcileSettings()
        if !wasActive, !model.isClosed {
            reload()
        } else if model.isClosed {
            invalidateAccess()
            scripts = []
        }
    }

    func show(_ page: AnyObject) {
        let id = ObjectIdentifier(page)
        guard !visible || visiblePage != id else { return }
        let returning = !visible || visiblePage != nil
        if returning { cancelPageWork() }
        visiblePage = id
        visible = true
        if returning {
            importing = importWork != nil
            savingOfficial = officialWork != nil
            reload()
        }
    }

    func hide(_ page: AnyObject) {
        guard visiblePage == ObjectIdentifier(page) else { return }
        visible = false
        cancelPageWork()
    }

    func close() {
        active = false; visible = false; visiblePage = nil; error = nil
        invalidateAccess()
    }

    func reconcileAccess() {
        guard active, model.isClosed else { return }
        invalidateAccess()
        error = nil; scripts = []
        reconcileSettings()
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
        let access = generation, page = pageGeneration
        savingOfficial = true
        officialWork = Task { [weak self] in
            guard let self else { return }
            defer {
                officialWork = nil
                if admitted { savingOfficial = false; reconcileSettings() }
            }
            guard canSubmit(access, page: page) else { return }
            await model.updateSettings(change)
        }
    }

    func scriptEnabled(_ item: MusicScriptSourceItem) -> Bool { pending[item.id] ?? item.enabled }
    func isPending(_ id: String) -> Bool { sourceWork[id] != nil }
    func item(_ id: String) -> MusicScriptSourceItem? { scripts.first { $0.id == id } }

    func setScript(_ id: String, enabled: Bool) {
        guard admitted, let service = model.scriptSources, !isPending(id),
              let current = item(id), current.enabled != enabled else { return }
        pending[id] = enabled
        let access = generation, page = pageGeneration
        run(configurationWrite: true, sourceID: id) { [weak self] in
            do {
                let value = try await service.setScriptSourceEnabled(id, enabled: enabled)
                guard let self, self.canSubmit(access, page: page) else { return }
                self.pending[id] = nil
                self.replace(value)
            } catch {
                guard let self, self.canSubmit(access, page: page) else { return }
                self.pending[id] = nil
                self.fail(error); self.reload()
            }
        }
    }

    func remove(_ id: String) {
        guard admitted, let service = model.scriptSources, !isPending(id), item(id) != nil else { return }
        pending[id] = false
        let access = generation, page = pageGeneration
        run(configurationWrite: true, sourceID: id) { [weak self] in
            do {
                try await service.removeScriptSource(id)
                guard let self, self.canSubmit(access, page: page) else { return }
                self.pending[id] = nil
                self.scripts.removeAll { $0.id == id }
                self.selected.remove(id); self.details[id] = nil; self.detailReadIDs[id] = nil
            } catch {
                guard let self, self.canSubmit(access, page: page) else { return }
                self.pending[id] = nil
                self.fail(error); self.reload()
            }
        }
    }

    func importFiles(_ urls: [URL]) {
        guard admitted, let service = model.scriptSources, importWork == nil, !urls.isEmpty else { return }
        let access = generation, page = pageGeneration
        importing = true
        run(configurationWrite: true, importing: true) { [weak self] in
            var failure: Error?
            for url in urls {
                guard self?.canSubmit(access, page: page) == true else { break }
                do {
                    let data = try await ImportedFileReader.read(url)
                    guard self?.canSubmit(access, page: page) == true else { break }
                    let value = try await service.importScriptSource(data, fileName: url.lastPathComponent)
                    guard let self, self.canSubmit(access, page: page) else { return }
                    self.replace(value)
                } catch {
                    failure = failure ?? error
                    if MusicServiceError.sanitized(error) == .cancelled { break }
                }
            }
            guard let self, self.canSubmit(access, page: page) else { return }
            self.importing = false
            if let failure { self.fail(failure); self.reload() }
        }
    }

    func download(_ text: String) {
        guard admitted, let service = model.scriptSources, importWork == nil else { return }
        guard let url = URL(string: text.trimmingCharacters(in: .whitespacesAndNewlines)), url.scheme != nil else {
            fail(MusicServiceError.insecureScriptURL); return
        }
        let access = generation, page = pageGeneration
        importing = true
        run(configurationWrite: true, importing: true) { [weak self] in
            do {
                let value = try await service.downloadScriptSource(from: url)
                guard let self, self.canSubmit(access, page: page) else { return }
                self.importing = false
                self.replace(value)
            } catch {
                guard let self, self.canSubmit(access, page: page) else { return }
                self.importing = false
                self.fail(error); self.reload()
            }
        }
    }

    func reload() {
        guard admitted, let service = model.scriptSources else { return }
        let access = generation, id = UUID(); readID = id
        run { [weak self] in
            do {
                let values = try await service.scriptSources()
                guard let self, self.currentRead(access), self.readID == id else { return }
                let previous = Dictionary(self.scripts.map { ($0.id, $0) }, uniquingKeysWith: { _, latest in latest })
                let next = Dictionary(values.map { ($0.id, $0) }, uniquingKeysWith: { _, latest in latest })
                let observed = Set(self.details.keys).union(self.detailReadIDs.keys)
                let changed = observed.filter { previous[$0] != next[$0] }
                for sourceID in changed {
                    self.detailReadIDs[sourceID] = nil
                    self.details[sourceID] = nil
                }
                self.scripts = values
                for sourceID in changed where next[sourceID] != nil { self.readDetail(sourceID) }
            } catch {
                guard let self, self.currentRead(access), self.readID == id else { return }
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
        guard admitted, !checking.contains(id), let service = model.service else { return }
        checking.insert(id)
        let access = generation
        run { [weak self] in
            do {
                try await service.checkOfficialSource(platform)
                guard let self, self.currentRead(access) else { return }
                self.officialChecks[platform] = true; self.checking.remove(id)
            } catch {
                guard let self, self.currentRead(access) else { return }
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
                guard let self, self.currentRead(access), self.detailReadIDs[id] == readID,
                      self.item(id) != nil else { return }
                self.details[id] = value
            } catch {
                guard let self, self.currentRead(access), self.detailReadIDs[id] == readID,
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
                let data = try await ImportedFileReader.read(file)
                return try await service.updateScriptSource(id, data: data, fileName: file.lastPathComponent)
            }
            return try await service.updateScriptSource(id)
        }
    }
    func rollback(_ id: String) {
        perform(id) { try await $0.rollbackScriptSource(id) }
    }
    private func perform(_ id: String, checking check: Bool = false,
                         operation: @escaping @MainActor (NativeMusicScriptService) async throws -> MusicScriptSourceItem) {
        guard admitted, let service = model.scriptSources, let item = item(id), !isPending(id) else { return }
        pending[id] = item.enabled
        if check { checking.insert(id) }
        let access = generation, page = pageGeneration
        run(configurationWrite: !check, sourceID: id) { [weak self] in
            do {
                let value = try await operation(service)
                guard let self, self.canSubmit(access, page: page) else { return }
                self.pending[id] = nil; self.checking.remove(id); self.replace(value); self.readDetail(id)
            } catch {
                guard let self, self.canSubmit(access, page: page) else { return }
                self.pending[id] = nil; self.checking.remove(id); self.fail(error); self.reload()
            }
        }
    }

    private var admitted: Bool { visible && isActive }
    private func currentRead(_ access: UUID) -> Bool { !Task.isCancelled && visible && isActive && access == generation }
    private func canSubmit(_ access: UUID, page: UUID) -> Bool { currentRead(access) && pageGeneration == page }

    private func replace(_ value: MusicScriptSourceItem) {
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
        if failure != .cancelled { self.error = failure.localizedDescription }
    }

    private func run(configurationWrite: Bool = false, sourceID: String? = nil, importing: Bool = false,
                     _ body: @escaping @MainActor () async -> Void) {
        let id = UUID(), owner = model.service
        let access = generation, page = pageGeneration
        if let sourceID { sourceWork[sourceID] = id }
        if importing { importWork = id }
        work[id] = Task { [weak self] in
            guard let self else { return }
            defer {
                work[id] = nil
                if let sourceID, sourceWork[sourceID] == id {
                    sourceWork[sourceID] = nil; pending[sourceID] = nil
                }
                if importWork == id { importWork = nil; self.importing = false }
            }
            guard canSubmit(access, page: page) else { return }
            await body()
            if configurationWrite {
                await owner?.sourceConfigurationChanged()
            }
        }
    }

    private func cancelPageWork() {
        pageGeneration = UUID(); readID = UUID(); detailReadIDs = [:]
        officialWork?.cancel()
        for (_, task) in work { task.cancel() }
        work = [:]; sourceWork = [:]; importWork = nil
        pending = [:]; checking = []; importing = false; savingOfficial = false
    }

    private func invalidateAccess() {
        generation = UUID()
        cancelPageWork()
    }

    deinit {
        officialWork?.cancel()
        for task in work.values { task.cancel() }
    }
}
#endif
