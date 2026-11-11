#if os(iOS)
import Foundation
import Combine

@MainActor
public final class FilmRulesDraft: ObservableObject {
    public let service: FilmRuleService
    /// Presentation drafts survive navigation. Each write submits one group only.
    @Published public private(set) var draft: FilmRuleSet?
    @Published public private(set) var busy = false
    private var saveWork: Task<Void, Error>?
    public init(service: FilmRuleService) { self.service = service }

    func load() async throws {
        guard draft == nil, !busy else { return }
        busy = true; defer { busy = false }
        let saved = try await service.savedSnapshot()
        try Task.checkCancellation()
        draft = saved
    }
    func edit(_ group: FilmRuleGroup, index: Int, rule: FilmMatchRule) {
        guard var value = draft, !busy else { return }
        switch group {
        case .episodeClean: value.episodeCleanRegexRules[index] = rule.pattern
        case .episode: value.episodeRules[index] = rule
        case .movie: value.movieRules[index] = rule
        case .search: value.aggregateRegexRules[index] = rule.pattern
        }
        draft = value
    }
    func save(_ group: FilmRuleGroup) async throws {
        guard let value = draft, !busy else { return }
        try Task.checkCancellation()
        busy = true
        // Handoff belongs to the stable draft owner. Cancelling a page's waiter
        // must not cancel the write or discard its actual normalization receipt.
        let work = Task { [self] in
            defer { busy = false; saveWork = nil }
            let saved = try await service.save(group, rules: value.rules(in: group))
            draft?.replace(group, with: saved.rules(in: group))
        }
        saveWork = work
        try await work.value
    }
    func add(_ group: FilmRuleGroup) {
        guard var value = draft, !busy else { return }
        var rows = value.rules(in: group)
        rows.append(.init(pattern: ""))
        value.replace(group, with: rows); draft = value
    }
    func delete(_ group: FilmRuleGroup, index: Int) {
        guard var value = draft, !busy else { return }
        var rows = value.rules(in: group)
        rows.remove(at: index)
        value.replace(group, with: rows); draft = value
    }
    func restore(_ group: FilmRuleGroup) async throws {
        guard draft != nil, !busy else { return }
        busy = true; defer { busy = false }
        let rows = try await service.defaults(group)
        try Task.checkCancellation()
        draft?.replace(group, with: rows)
    }
    func rules(_ group: FilmRuleGroup) -> [FilmMatchRule] { draft?.rules(in: group) ?? [] }
}
#endif
