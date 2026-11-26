import Foundation
import MeowDebug
import MeowRuntime

public enum FilmSearchChannelState: String, Equatable, Sendable {
    case idle, loading, completed, unavailable, failed, cancelled
}

public enum FilmSearchError: Error, Sendable { case unavailable, closed, invalidResponse }

/// Normalized clean search card projected by Node `film.search`.
public struct FilmSearchCard: Equatable, Sendable {
    public let id: String
    public let entry: FilmEntry
    public let sourceKind: String
    public let textBadge: String
    public let label: String

    public init(value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        let rawID = fields["id"]?.filmRuleDisplayText ?? fields["displayKey"]?.filmRuleDisplayText ?? ""
        guard !rawID.isEmpty else { throw FilmSearchError.invalidResponse }
        id = rawID
        entry = try FilmEntry(value: value)
        sourceKind = fields["sourceKind"]?.filmText ?? ""
        textBadge = entry.textBadge
        label = entry.siteLabel
    }

    public func filmItem(imageURL: (String) -> URL?) -> FilmItem {
        let sourceID = sourceKind == "tmdb" ? "tmdb" : (entry.siteEntry?.sourceKey ?? "search")
        let itemID = entry.tmdb.map { String($0.id) } ?? id
        return FilmItem(
            sourceID: sourceID,
            id: itemID,
            title: entry.title,
            kind: entry.kind,
            posterURL: imageURL(entry.poster),
            subtitle: label,
            entry: entry
        )
    }
}

public struct FilmSearchSnapshot: Equatable, Sendable {
    public let sessionID: UUID
    public let query: String
    public var revision = 0
    public var sequence: Int64 = -1
    public var siteState: FilmSearchChannelState = .idle
    public var metadataState: FilmSearchChannelState = .idle
    public var loading = false
    public var hasMore = false
    public var noSources = false
    public var invalidated = false
    public var cards: [FilmSearchCard] = []
    public var rawCards: [FilmSearchCard] = []

    public var failed: Bool {
        !loading && cards.isEmpty && rawCards.isEmpty &&
            [.failed, .unavailable].contains(siteState) &&
            [.completed, .failed, .unavailable].contains(metadataState)
    }
}

extension FilmSearchSnapshot {
    func retainingDisplay(_ previous: Self) -> Self {
        var value = self
        value.cards = previous.cards
        value.rawCards = previous.rawCards
        return value
    }

    func retainingLoadingDisplay(_ previous: Self) -> Self {
        guard cards.isEmpty, rawCards.isEmpty,
              [.idle, .loading, .failed, .unavailable].contains(siteState),
              [.idle, .loading, .failed, .unavailable].contains(metadataState) else { return self }
        return retainingDisplay(previous)
    }

    public init(value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        guard let id = UUID(uuidString: try fields.filmString("sessionId")),
              let sequence = fields["sequence"]?.filmInteger,
              let revision = fields["revision"]?.filmInteger else { throw FilmSearchError.invalidResponse }
        sessionID = id
        query = try fields.filmString("query")
        self.sequence = Int64(sequence)
        self.revision = revision
        siteState = FilmSearchChannelState(rawValue: (try? fields.filmString("siteState")) ?? "") ?? .idle
        metadataState = FilmSearchChannelState(rawValue: (try? fields.filmString("metadataState")) ?? "") ?? .idle
        loading = try fields.filmBool("loading")
        hasMore = try fields.filmBool("hasMore")
        noSources = try fields.filmBool("noSources")
        invalidated = (fields["invalidated"] == .bool(true))

        let rawCardArray = try fields.filmArray("cards")
        let parsedCards = rawCardArray.compactMap { item -> FilmSearchCard? in
            do { return try FilmSearchCard(value: item) }
            catch {
                DebugRuntime.logError(.search, sub: "card", event: "decode.card.skipped", error: error)
                return nil
            }
        }
        let rawListArray = try fields.filmArray("rawCards")
        let parsedRawCards = rawListArray.compactMap { item -> FilmSearchCard? in
            do { return try FilmSearchCard(value: item) }
            catch {
                DebugRuntime.logError(.search, sub: "card", event: "decode.rawCard.skipped", error: error)
                return nil
            }
        }
        guard (rawCardArray.isEmpty || !parsedCards.isEmpty) &&
              (rawListArray.isEmpty || !parsedRawCards.isEmpty) else {
            throw FilmSearchError.invalidResponse
        }
        cards = parsedCards
        rawCards = parsedRawCards
    }
}
