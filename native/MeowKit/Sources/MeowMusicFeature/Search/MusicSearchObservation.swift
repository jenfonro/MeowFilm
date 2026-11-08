// SPDX-License-Identifier: GPL-3.0-only
import Foundation

/// Evidence of an executed native query. Page-cache reuse carries the original
/// evidence; it never creates a new platform check.
public struct MusicSearchObservation: Codable, Equatable, Sendable {
    public enum Scope: String, Codable, Sendable { case page, recording }
    public enum Outcome: String, Codable, Sendable { case found, notFound, incomplete, failed }
    public let requestID: UUID
    public let startedAt: Double
    public let order: UInt64
    public let checkedAt: Double
    public let platform: String
    public let query: String
    /// Public search is anonymous. Account-backed callers must supply their
    /// non-secret account/cache scope, never a credential or an authorization.
    public let context: String
    public let page: Int
    public let limit: Int
    public let scope: Scope
    public let outcome: Outcome
    public let failureCode: String?

    public init(requestID: UUID = UUID(), startedAt: Double, order: UInt64 = 0,
                checkedAt: Double, platform: String, query: String, context: String = "public",
                page: Int = 1, limit: Int = 30, scope: Scope = .page,
                outcome: Outcome, failure: MusicServiceError? = nil) {
        self.requestID = requestID; self.startedAt = startedAt; self.order = order
        self.checkedAt = checkedAt; self.platform = platform; self.query = query
        self.context = context; self.page = page; self.limit = limit; self.scope = scope
        self.outcome = outcome; failureCode = failure?.rawValue
    }

    public func result(_ outcome: Outcome, scope: Scope? = nil,
                       checkedAt: Double? = nil, failure: MusicServiceError? = nil) -> Self {
        .init(requestID: requestID, startedAt: startedAt, order: order, checkedAt: checkedAt ?? self.checkedAt,
              platform: platform, query: query, context: context, page: page, limit: limit,
              scope: scope ?? self.scope, outcome: outcome, failure: failure)
    }

    public func isNewer(than other: Self) -> Bool {
        startedAt > other.startedAt || startedAt == other.startedAt && order > other.order
    }

    public func sameQuery(as other: Self) -> Bool {
        platform == other.platform && context == other.context &&
            MusicRecordingIdentity.normalize(query) == MusicRecordingIdentity.normalize(other.query) &&
            page == other.page && limit == other.limit && scope == other.scope
    }

    public func sameEvidence(as other: Self) -> Bool {
        func kind(_ outcome: Outcome) -> Int {
            switch outcome {
            case .found, .notFound: return 0
            case .incomplete: return 1
            case .failed: return 2
            }
        }
        return sameQuery(as: other) && kind(outcome) == kind(other.outcome)
    }
}

/// Binds completed pages to recordings revealed by another platform in the same
/// round. Only native references are accepted; DTOs cannot supply native facts.
public protocol MusicSearchObserving: Sendable {
    func observeSearchPages(_ pages: [MusicSearchPage], references: [MusicReference]) async throws
}
