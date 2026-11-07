#if os(iOS)
import Foundation
import Combine

@MainActor
public final class FilmRulesDraft: ObservableObject {
    public let service: FilmRuleService
    @Published public private(set) var draft: FilmRuleSet?
    @Published public private(set) var busy = false
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
    func save() async throws {
        guard let draft, !busy else { return }
        busy = true; defer { busy = false }
        self.draft = try await service.save(draft)
    }
    func add(_ group: FilmRuleGroup, rule: FilmMatchRule) async throws {
        guard var value = draft, !busy else { return }
        busy = true; defer { busy = false }
        if group.isClean {
            let pattern = try await service.normalizeNewCleanRule(rule.pattern)
            if group == .episodeClean { value.episodeCleanRegexRules.append(pattern) }
            else { value.aggregateRegexRules.append(pattern) }
        } else {
            guard var normalized = try await service.normalizeNewMatchRule(rule.pattern, replace: rule.replace) else { return }
            if !rule.flags.isEmpty { normalized.flags = rule.flags }
            if group == .episode { value.episodeRules.append(normalized) }
            else { value.movieRules.append(normalized) }
        }
        // An unsuccessful write leaves the same complete draft available for retry.
        draft = value
        draft = try await service.save(value)
    }
    func delete(_ group: FilmRuleGroup, index: Int) async throws {
        guard var value = draft, !busy else { return }
        switch group {
        case .episodeClean: value.episodeCleanRegexRules.remove(at: index)
        case .episode: value.episodeRules.remove(at: index)
        case .movie: value.movieRules.remove(at: index)
        case .search: value.aggregateRegexRules.remove(at: index)
        }
        draft = value
        try await save()
    }
    func restore(_ section: FilmRuleSection) async throws {
        guard let draft, !busy else { return }
        busy = true; defer { busy = false }
        self.draft = try await service.restoreDefaults(section, draft: draft)
    }
    func rules(_ group: FilmRuleGroup) -> [FilmMatchRule] {
        guard let draft else { return [] }
        switch group {
        case .episodeClean: return draft.episodeCleanRegexRules.map { .init(pattern: $0) }
        case .episode: return draft.episodeRules
        case .movie: return draft.movieRules
        case .search: return draft.aggregateRegexRules.map { .init(pattern: $0) }
        }
    }
}

enum FilmRuleGroup: String, CaseIterable {
    case episodeClean, episode, movie, search
    var title: String {
        switch self {
        case .episodeClean: return "剧集净化"
        case .episode: return "集数匹配"
        case .movie: return "电影匹配"
        case .search: return "搜索列表净化"
        }
    }
    var isClean: Bool { self == .episodeClean || self == .search }
}

extension FilmRuleSection {
    var settingsTitle: String {
        switch self {
        case .episode: return "列表净化"
        case .movie: return "电影匹配"
        case .search: return "搜索列表净化"
        }
    }
    var settingsGroups: [FilmRuleGroup] {
        switch self {
        case .episode: return [.episodeClean, .episode]
        case .movie: return [.movie]
        case .search: return [.search]
        }
    }
}
#endif
