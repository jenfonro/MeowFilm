#if os(iOS) || os(macOS)
import Foundation
import Combine

@MainActor
final class MusicDailyStyleViewModel: ObservableObject {
    @Published private(set) var categories: [MusicDailyStyleCategory] = []
    @Published private(set) var category = ""
    @Published private(set) var choices: [String: Set<String>] = [:]
    @Published private(set) var requestID: UUID?
    @Published private(set) var error: String?
    @Published private(set) var saved = false
    private let page: MusicPublicBrowseViewModel
    private let source: MusicRemoteQueueSource
    private var sourceObservation: AnyCancellable?
    private var revocations: Task<Void, Never>?
    private var work: Task<Void, Never>?
    private var workID: UUID?
    private var requested = false
    private var closed = false

    init(page: MusicPublicBrowseViewModel, source: MusicRemoteQueueSource) {
        self.page = page
        self.source = source
        sourceObservation = page.$remoteSource.map { $0?.id }.removeDuplicates()
            .receive(on: RunLoop.main)
            .sink { [weak self] id in
                guard let self, !self.closed, id != self.source.id else { return }
                self.invalidate()
                self.error = MusicServiceError.staleAccess.localizedDescription
            }
    }

    var available: Bool {
        !closed && page.remoteSource?.id == source.id && source.authorization.isActive
    }
    var selectedCategory: MusicDailyStyleCategory? { categories.first { $0.id == category } }
    var selected: Set<String> { choices[category, default: []] }
    var working: Bool { requestID != nil }
    var canSave: Bool { available && !working && !selected.isEmpty }
    var canRetry: Bool { available && requested && !working && categories.isEmpty && source.dailyStyle != nil }

    func loadIfNeeded() {
        guard !requested, available else { return }
        if revocations == nil {
            let events = source.authorization.revocations()
            revocations = Task { [weak self] in
                for await _ in events {
                    guard !Task.isCancelled, let self, !self.closed else { return }
                    self.invalidate()
                    self.error = MusicServiceError.staleAccess.localizedDescription
                    return
                }
            }
        }
        load()
    }

    func retry() {
        guard canRetry else { return }
        load()
    }

    private func load() {
        guard available, work == nil else { return }
        requested = true
        guard let editor = source.dailyStyle else {
            error = MusicServiceError.unsupported.localizedDescription
            return
        }
        let id = UUID()
        requestID = id
        workID = id
        error = nil
        work = Task { [weak self] in
            guard let self else { return }
            defer { finish(id) }
            guard current(id) else { return }
            do {
                try await source.validate()
                let values = try await editor.categories()
                let selection = try await source.page(cursor: nil).dailyStyle
                guard current(id) else { return }
                categories = values
                category = values.first { $0.id == selection?.category }?.id ?? values.first?.id ?? ""
                if let selection {
                    choices = [selection.category: Set(selection.tags.map(\.id))]
                }
            } catch {
                if current(id) { self.error = MusicServiceError.sanitized(error).localizedDescription }
            }
        }
    }

    func selectCategory(_ id: String) {
        guard available, !working, categories.contains(where: { $0.id == id }) else { return }
        category = id
    }

    func selectTag(_ id: String) {
        guard available, !working, let selectedCategory,
              selectedCategory.tags.contains(where: { $0.id == id }) else { return }
        var values = selected
        if values.contains(id) {
            values.remove(id)
        } else {
            values.insert(id)
        }
        choices[category] = values
    }

    func save() {
        guard canSave, work == nil, let selectedCategory else { return }
        let selection = MusicDailyStyleSelection(
            category: category,
            tags: selectedCategory.tags.filter { selected.contains($0.id) }
        )
        let id = UUID()
        requestID = id
        workID = id
        error = nil
        work = Task { [weak self] in
            guard let self else { return }
            defer { finish(id) }
            guard current(id) else { return }
            do {
                try selection.validate(in: categories)
                try await source.validate()
                guard current(id) else { return }
                let result = try await page.saveDailyStyle(selection)
                guard current(id) else { return }
                if let actual = page.dailyStyle {
                    category = actual.category
                    choices = [actual.category: Set(actual.tags.map(\.id))]
                }
                guard !result.selectionChanged else {
                    error = "推荐风格与提交不一致，已显示当前生效选择"
                    return
                }
                saved = true
            } catch {
                if current(id) {
                    let failure = error as? MusicServiceError ?? MusicServiceError.sanitized(error)
                    self.error = failure.localizedDescription
                }
            }
        }
    }

    func dismissError(_ value: String) {
        if error == value { error = nil }
    }

    func acknowledgeSave() { saved = false }
    func finishPending() async { while let task = work { await task.value } }

    func close() {
        closed = true
        sourceObservation = nil
        revocations?.cancel()
        revocations = nil
        invalidate()
    }

    private func current(_ id: UUID) -> Bool {
        available && !Task.isCancelled && requestID == id
    }

    private func finish(_ id: UUID) {
        guard workID == id else { return }
        requestID = nil
        work = nil
        workID = nil
    }

    private func invalidate() {
        requestID = nil
        work?.cancel()
        categories = []
        category = ""
        choices = [:]
        saved = false
        error = nil
    }

    deinit { work?.cancel(); revocations?.cancel() }
}
#endif
