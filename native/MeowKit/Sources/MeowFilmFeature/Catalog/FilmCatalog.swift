import Foundation

public struct FilmSourceDescriptor: Hashable, Sendable {
    public let id: String
    public let title: String

    public init(id: String, title: String) {
        self.id = id
        self.title = title
    }
}

public enum FilmItemKind: String, Hashable, Codable, Sendable {
    case movie, tv
}

public struct FilmItemIdentity: Hashable, Codable, Sendable {
    public let sourceID: String
    public let itemID: String
    public let kind: FilmItemKind?

    public init(sourceID: String, itemID: String, kind: FilmItemKind? = nil) {
        self.sourceID = sourceID
        self.itemID = itemID
        // TMDB has separate movie/tv ID namespaces; Douban subject IDs are global.
        self.kind = sourceID == "tmdb" ? kind : nil
    }

    public static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.sourceID.utf8.elementsEqual(rhs.sourceID.utf8) &&
            lhs.itemID.utf8.elementsEqual(rhs.itemID.utf8) && lhs.kind == rhs.kind
    }

    public func hash(into hasher: inout Hasher) {
        hasher.combine(sourceID.utf8.count)
        for byte in sourceID.utf8 { hasher.combine(byte) }
        hasher.combine(itemID.utf8.count)
        for byte in itemID.utf8 { hasher.combine(byte) }
        hasher.combine(kind)
    }

    private enum CodingKeys: String, CodingKey { case sourceID, itemID, kind }

    public init(from decoder: any Decoder) throws {
        let fields = try decoder.container(keyedBy: CodingKeys.self)
        self.init(sourceID: try fields.decode(String.self, forKey: .sourceID),
                  itemID: try fields.decode(String.self, forKey: .itemID),
                  kind: try fields.decodeIfPresent(FilmItemKind.self, forKey: .kind))
    }
}

public struct FilmItem: Equatable, Codable, Sendable {
    public let sourceID: String
    public let id: String
    public let title: String
    public let kind: FilmItemKind?
    public let posterURL: URL?
    /// Higher-resolution poster for full-width detail artwork; cards keep `posterURL`.
    public let largePosterURL: URL?
    public let score: String
    public let subtitle: String

    public var identity: FilmItemIdentity {
        FilmItemIdentity(sourceID: sourceID, itemID: id, kind: kind)
    }

    public init(sourceID: String, id: String, title: String, kind: FilmItemKind? = nil, posterURL: URL? = nil,
                largePosterURL: URL? = nil, score: String = "", subtitle: String = "") {
        self.sourceID = sourceID
        self.id = id
        self.title = title
        self.kind = kind
        self.posterURL = posterURL
        self.largePosterURL = largePosterURL
        self.score = score
        self.subtitle = subtitle
    }
}

public struct FilmHomeSection: Equatable, Sendable {
    public let id: String
    public let title: String
    public let items: [FilmItem]
    public let cache: FilmMetadataCache?

    public init(id: String, title: String, items: [FilmItem], cache: FilmMetadataCache? = nil) {
        self.id = id
        self.title = title
        self.items = items
        self.cache = cache
    }
}

public protocol FilmSource: Sendable {
    var descriptor: FilmSourceDescriptor { get }
    func loadHome(refresh: Bool) async throws -> [FilmHomeSection]
}
