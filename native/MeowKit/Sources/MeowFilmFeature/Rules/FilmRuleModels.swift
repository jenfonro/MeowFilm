import Foundation

public enum FilmRuleSection: String, Codable, CaseIterable, Sendable {
    case episode, movie, search
}

public enum FilmRuleGroup: String, Codable, CaseIterable, Sendable {
    case episodeClean, episode, movie, search

    public var isClean: Bool { self == .episodeClean || self == .search }
    public var section: FilmRuleSection {
        switch self {
        case .episodeClean, .episode: return .episode
        case .movie: return .movie
        case .search: return .search
        }
    }
    public var title: String {
        switch self {
        case .episodeClean: return "剧集列表净化规则"
        case .episode: return "集数匹配规则"
        case .movie: return "电影匹配规则"
        case .search: return "搜索净化设置"
        }
    }
}

public struct FilmMatchRule: Codable, Equatable, Sendable {
    public var pattern: String
    public var replace: String
    public var flags: String

    public init(pattern: String, replace: String = "", flags: String = "") {
        self.pattern = pattern
        self.replace = replace
        self.flags = flags
    }
}

/// A value snapshot acknowledged by the Node owner.
public struct FilmRuleSet: Codable, Equatable, Sendable {
    public var episodeCleanRegexRules: [String]
    public var episodeRules: [FilmMatchRule]
    public var movieRules: [FilmMatchRule]
    public var aggregateRegexRules: [String]

    public init(episodeCleanRegexRules: [String] = [], episodeRules: [FilmMatchRule] = [],
                movieRules: [FilmMatchRule] = [], aggregateRegexRules: [String] = []) {
        self.episodeCleanRegexRules = episodeCleanRegexRules
        self.episodeRules = episodeRules
        self.movieRules = movieRules
        self.aggregateRegexRules = aggregateRegexRules
    }
}

extension FilmRuleSet {
    public func rules(in group: FilmRuleGroup) -> [FilmMatchRule] {
        switch group {
        case .episodeClean: return episodeCleanRegexRules.map { .init(pattern: $0) }
        case .episode: return episodeRules
        case .movie: return movieRules
        case .search: return aggregateRegexRules.map { .init(pattern: $0) }
        }
    }

    public mutating func replace(_ group: FilmRuleGroup, with rules: [FilmMatchRule]) {
        switch group {
        case .episodeClean: episodeCleanRegexRules = rules.map(\.pattern)
        case .episode: episodeRules = rules
        case .movie: movieRules = rules
        case .search: aggregateRegexRules = rules.map(\.pattern)
        }
    }
}

public struct FilmRuleRequest: Codable, Equatable, Sendable {
    public var section: FilmRuleSection
    public var text: String
    public var query: String?

    public init(section: FilmRuleSection, text: String, query: String? = nil) {
        self.section = section
        self.text = text
        self.query = query
    }
}

public struct FilmRuleError: Codable, Equatable, Sendable {
    public let group: String
    /// One-based index in episodeClean, episode, movie or search.
    public let ruleIndex: Int
    public let text: FilmRuleText
}

public struct FilmRuleResult: Codable, Equatable, Sendable {
    public let section: FilmRuleSection
    /// The shared program returns "", "success" or "error".
    public let type: String
    public let text: FilmRuleText
    public let inputText: FilmRuleText
    public let processedText: FilmRuleText
    public let matched: Bool
    public let matchedRuleIndex: Int?
    public let rewriteApplied: Bool
    /// nil is no rewrite; an empty string is a failed rewrite.
    public let rewrittenText: FilmRuleText?
    public var outputText: FilmRuleText { rewrittenText ?? processedText }
    public let groupKey: FilmRuleText
    public let trailingDigits: FilmRuleText
    public let errors: [FilmRuleError]
    public let previews: [FilmRuleText]
}

public enum FilmRuleServiceError: Error, Equatable, Sendable {
    case closed
    case shuttingDown
    case notInitialized
    case invalidResponse
}

extension FilmRuleServiceError: LocalizedError {
    public var errorDescription: String? {
        switch self {
        case .closed: return "规则服务已关闭"
        case .shuttingDown: return "规则服务正在停止"
        case .notInitialized: return "规则尚未加载"
        case .invalidResponse: return "规则服务返回无效结果"
        }
    }
}
