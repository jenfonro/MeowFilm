import Foundation

public struct FilmSourceDescriptor: Hashable, Sendable {
    public let id: String
    public let title: String

    public init(id: String, title: String) {
        self.id = id
        self.title = title
    }
}

public struct FilmItemIdentity: Hashable, Codable, Sendable {
    public let sourceID: String
    public let itemID: String

    public init(sourceID: String, itemID: String) {
        self.sourceID = sourceID
        self.itemID = itemID
    }
}

public struct FilmItem: Equatable, Codable, Sendable {
    public let sourceID: String
    public let id: String
    public let title: String
    public let posterURL: URL?
    /// Higher-resolution poster for full-width detail artwork; cards keep `posterURL`.
    public let largePosterURL: URL?
    public let score: String
    public let subtitle: String

    public var identity: FilmItemIdentity {
        FilmItemIdentity(sourceID: sourceID, itemID: id)
    }

    public init(sourceID: String, id: String, title: String, posterURL: URL? = nil,
                largePosterURL: URL? = nil, score: String = "", subtitle: String = "") {
        self.sourceID = sourceID
        self.id = id
        self.title = title
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

    public init(id: String, title: String, items: [FilmItem]) {
        self.id = id
        self.title = title
        self.items = items
    }
}

public protocol FilmSource: Sendable {
    var descriptor: FilmSourceDescriptor { get }
    func loadHome() async throws -> [FilmHomeSection]
}
