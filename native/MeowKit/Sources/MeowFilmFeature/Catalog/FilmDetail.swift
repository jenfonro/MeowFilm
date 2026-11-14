import Foundation

public struct FilmDetail: Equatable, Sendable {
    public let item: FilmItem
    public let backdropURL: URL?
    public let title: String
    public let logoURL: URL?
    public let summary: String
    public let releaseDate: String?
    public let seasonCount: Int?
    public let episodeCount: Int?
    public let playbackURL: URL?
    public let seasons: [FilmSeason]
    public let cast: [FilmCastMember]
    public let similarItems: [FilmItem]

    public init(item: FilmItem, backdropURL: URL? = nil, title: String? = nil,
                logoURL: URL? = nil, summary: String = "", releaseDate: String? = nil,
                seasonCount: Int? = nil, episodeCount: Int? = nil, playbackURL: URL? = nil,
                seasons: [FilmSeason] = [], cast: [FilmCastMember] = [],
                similarItems: [FilmItem] = []) {
        self.item = item
        self.backdropURL = backdropURL
        self.title = title ?? item.title
        self.logoURL = logoURL
        self.summary = summary
        self.releaseDate = releaseDate
        self.seasonCount = seasonCount
        self.episodeCount = episodeCount
        self.playbackURL = playbackURL
        self.seasons = seasons
        self.cast = cast
        self.similarItems = similarItems
    }
}

public struct FilmSeason: Equatable, Sendable {
    public let id: String
    public let title: String
    public let number: Int?
    public let posterURL: URL?
    public let episodes: [FilmEpisode]

    public init(id: String, title: String, number: Int? = nil, posterURL: URL? = nil, episodes: [FilmEpisode] = []) {
        self.id = id
        self.title = title
        self.number = number
        self.posterURL = posterURL
        self.episodes = episodes
    }
}

public struct FilmEpisode: Equatable, Sendable {
    public let id: String
    public let title: String
    public let number: Int?
    public let summary: String
    public let artworkURL: URL?
    public let playbackURL: URL?
    public let releaseDate: String?

    public init(id: String, title: String, number: Int? = nil, summary: String = "",
                artworkURL: URL? = nil, playbackURL: URL? = nil, releaseDate: String? = nil) {
        self.id = id
        self.title = title
        self.number = number
        self.summary = summary
        self.artworkURL = artworkURL
        self.playbackURL = playbackURL
        self.releaseDate = releaseDate
    }
}

public struct FilmCastMember: Equatable, Sendable {
    public let id: String
    public let name: String
    public let role: String
    public let portraitURL: URL?

    public init(id: String, name: String, role: String = "", portraitURL: URL? = nil) {
        self.id = id
        self.name = name
        self.role = role
        self.portraitURL = portraitURL
    }
}
