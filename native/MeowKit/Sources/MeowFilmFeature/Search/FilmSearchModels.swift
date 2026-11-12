import Foundation
import MeowRuntime

public enum FilmSearchChannelState: String, Equatable, Sendable {
    case idle, loading, completed, unavailable, failed, cancelled
}

public enum FilmSearchError: Error, Sendable { case unavailable, closed, invalidResponse }

/// The adapter returns the original TMDB structures. Image paths are resolved by
/// that adapter against its runtime; the search rules never invent an image host.
public protocol FilmSearchMetadataReading: Sendable {
    func search(query: String) async throws -> RuntimeValue
    func detail(type: String, id: Int) async throws -> RuntimeValue
    func imageURL(path: String) -> URL?
    func searchRead(query: String) async throws -> FilmSearchRead
    func detailRead(type: String, id: Int) async throws -> FilmSearchRead
}

extension FilmSearchMetadataReading {
    public func searchRead(query: String) async throws -> FilmSearchRead {
        .init(value: try await search(query: query))
    }
    public func detailRead(type: String, id: Int) async throws -> FilmSearchRead {
        .init(value: try await detail(type: type, id: id))
    }
}

public struct FilmSearchSite: Sendable {
    public let cat: FilmCatSource
    public let source: FilmVideoSource
    public init(cat: FilmCatSource, source: FilmVideoSource) { self.cat = cat; self.source = source }
    public var key: String {
        // JSON tuple encoding is unambiguous even for separators in protocol IDs.
        let data = try! RuntimeJSON.encode(.array([.string(cat.installationId),
            .string(source.key.siteKey)]))
        return data.base64EncodedString()
    }
}

/// Keeps the complete shared-program value, including every original member.
/// Native presentation reads only the fields it needs; it does not regroup data.
public struct FilmSearchCard: Equatable, Sendable {
    public let value: RuntimeValue
    public init(value: RuntimeValue) { self.value = value }
    public var displayKey: String { text("displayKey") }
    public var id: String { text("id") }
    public var title: String { text("title") }
    public var sourceKind: String { text("sourceKind") }
    public var groupKey: String { text("groupKey") }
    public var contentKey: String { text("contentKey") }
    public var textBadge: String { text("textBadge") }
    public var label: String { text("siteLabel") }
    public var tmdbType: String { text("tmdbType") }
    public var tmdbID: Int? { value.catField("tmdbId")?.catInteger }
    public var detailID: FilmPlaybackIdentifier? {
        value.catField("detailId").flatMap { try? FilmPlaybackIdentifier($0) }
    }
    public var members: [RuntimeValue] { value.catField("aggregateItems")?.catArray ?? [] }
    func text(_ field: String) -> String {
        guard let value = value.catField(field) else { return "" }
        if let text = value.catText { return text }
        // Code-unit arrays stay intact in value; replacement is display-only.
        guard let data = try? RuntimeJSON.encode(value),
              let text = try? JSONDecoder().decode(FilmRuleText.self, from: data) else { return "" }
        return text.displayString
    }
    public func filmItem(imageURL: (String) -> URL?) -> FilmItem {
        FilmItem(sourceID: sourceKind == "tmdb" ? "tmdb" : text("siteKey"),
            id: sourceKind == "tmdb" ? tmdbID.map(String.init) ?? id : id, title: title,
            kind: sourceKind == "tmdb" ? FilmItemKind(rawValue: tmdbType) : nil,
            posterURL: imageURL(text("poster")), subtitle: label)
    }
}

public struct FilmSearchSnapshot: Equatable, Sendable {
    public let sessionID: UUID
    public let query: String
    public var round = 0
    public var revision = 0
    public var sequence: Int64 = -1
    public var siteState: FilmSearchChannelState = .idle
    public var metadataState: FilmSearchChannelState = .idle
    public var loading = false
    public var hasMore = false
    public var noSources = false
    public var siteItems: [RuntimeValue] = []
    public var tmdbItems: [RuntimeValue] = []
    public var metadataSearch: RuntimeValue?
    public var metadataQuery: String?
    public var metadataDetails: [String: RuntimeValue] = [:]
    public var cards: [FilmSearchCard] = []
    public var rawCards: [FilmSearchCard] = []
    public var failed: Bool {
        !loading && cards.isEmpty && rawCards.isEmpty &&
            [.failed, .unavailable].contains(siteState) &&
            [.completed, .failed, .unavailable].contains(metadataState)
    }
}

extension FilmSearchSnapshot {
    public init(value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        guard let id = UUID(uuidString: try fields.filmString("sessionId")),
              let sequence = fields["sequence"]?.filmInteger,
              let revision = fields["revision"]?.filmInteger,
              let round = fields["round"]?.filmInteger,
              let site = FilmSearchChannelState(rawValue: try fields.filmString("siteState")),
              let metadata = FilmSearchChannelState(rawValue: try fields.filmString("metadataState")),
              let details = fields["metadataDetails"]?.filmObject else { throw FilmSearchError.invalidResponse }
        sessionID = id; query = try fields.filmString("query")
        self.sequence = Int64(sequence); self.revision = revision; self.round = round
        siteState = site; metadataState = metadata
        loading = try fields.filmBool("loading"); hasMore = try fields.filmBool("hasMore")
        noSources = try fields.filmBool("noSources")
        siteItems = try fields.filmArray("siteItems"); tmdbItems = try fields.filmArray("tmdbItems")
        metadataSearch = fields["metadataSearch"].flatMap { $0 == .null ? nil : $0 }
        metadataQuery = fields["metadataQuery"]?.filmText
        metadataDetails = Dictionary(uniqueKeysWithValues: details.map { ($0.key, $0.value) })
        cards = try fields.filmArray("cards").map(FilmSearchCard.init)
        rawCards = try fields.filmArray("rawCards").map(FilmSearchCard.init)
    }
}
