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
    public let entry: FilmEntry?

    public var targetID: String? { entry?.targetId }

    public init(sourceID: String, itemID: String, kind: FilmItemKind? = nil, entry: FilmEntry? = nil) {
        self.sourceID = sourceID
        self.itemID = itemID
        self.entry = entry
        self.kind = sourceID == "tmdb" ? kind : (entry?.kind ?? kind)
    }

    public static func == (lhs: Self, rhs: Self) -> Bool {
        if let a = lhs.targetID, !a.isEmpty, let b = rhs.targetID, !b.isEmpty {
            return a.utf8.elementsEqual(b.utf8)
        }
        guard (lhs.targetID ?? "").isEmpty, (rhs.targetID ?? "").isEmpty else { return false }
        return lhs.sourceID.utf8.elementsEqual(rhs.sourceID.utf8) &&
            lhs.itemID.utf8.elementsEqual(rhs.itemID.utf8) && lhs.kind == rhs.kind
    }

    public func hash(into hasher: inout Hasher) {
        if let targetID, !targetID.isEmpty {
            hasher.combine(Data(targetID.utf8))
            return
        }
        hasher.combine(sourceID.utf8.count)
        for byte in sourceID.utf8 { hasher.combine(byte) }
        hasher.combine(itemID.utf8.count)
        for byte in itemID.utf8 { hasher.combine(byte) }
        hasher.combine(kind)
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
    public let entry: FilmEntry?

    public var targetID: String? { entry?.targetId }

    public var identity: FilmItemIdentity {
        FilmItemIdentity(sourceID: sourceID, itemID: id, kind: kind, entry: entry)
    }

    public init(sourceID: String, id: String, title: String, kind: FilmItemKind? = nil, posterURL: URL? = nil,
                largePosterURL: URL? = nil, score: String = "", subtitle: String = "",
                entry: FilmEntry? = nil) {
        self.sourceID = sourceID
        self.id = id
        self.title = title
        self.kind = kind ?? entry?.kind
        self.posterURL = posterURL
        self.largePosterURL = largePosterURL
        self.score = score
        self.subtitle = subtitle
        self.entry = entry
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
