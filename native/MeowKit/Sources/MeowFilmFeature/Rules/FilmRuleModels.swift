import Foundation

public enum FilmRuleSection: String, Codable, CaseIterable, Sendable {
    case episode, movie, search
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

/// A value snapshot also used as the editor's complete, unsaved draft.
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
    public let text: String
}

public struct FilmRuleResult: Codable, Equatable, Sendable {
    public let section: FilmRuleSection
    /// The shared program returns "", "success" or "error".
    public let type: String
    public let text: String
    public let inputText: String
    public let processedText: String
    public let matched: Bool
    public let matchedRuleIndex: Int?
    public let rewriteApplied: Bool
    /// nil is no rewrite; an empty string is a failed rewrite.
    public let rewrittenText: String?
    public var outputText: String { rewrittenText ?? processedText }
    public let groupKey: String
    public let trailingDigits: String
    public let errors: [FilmRuleError]
    public let previews: [String]
}

public enum FilmRuleServiceError: Error, Equatable, Sendable {
    case closed
    case shuttingDown
    case notInitialized
    case corruptDocument
    case invalidEngineResponse
}

extension FilmRuleServiceError: LocalizedError {
    public var errorDescription: String? {
        switch self {
        case .closed: return "规则服务已关闭"
        case .shuttingDown: return "规则服务正在停止"
        case .notInitialized: return "规则尚未加载"
        case .corruptDocument: return "规则文档已损坏"
        case .invalidEngineResponse: return "规则引擎返回无效结果"
        }
    }
}
