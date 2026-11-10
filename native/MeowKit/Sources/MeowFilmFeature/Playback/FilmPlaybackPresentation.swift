import Foundation
import MeowRuntime

/// Projects loaded provider data for UIKit. No episode inference, pan aliases,
/// metadata fetching or freshness policy belongs in this projection.
enum FilmPlaybackPresentation {
    static func detail(_ snapshot: FilmPlaybackSnapshot, original: FilmDetail) -> FilmDetail {
        let displayMetadata = snapshot.metadataSources.first { $0.id == snapshot.metadataSourceID }
        let provider = snapshot.target.metadataProvider ?? displayMetadata?.provider
        let metadataID = snapshot.target.value["metadataId"]?.filmScalar ?? displayMetadata?.metadataID.display
        let displayKind = snapshot.target.metadataProvider == nil ? displayMetadata?.kind : snapshot.target.kind
        let kind = FilmItemKind(rawValue: (displayKind ?? snapshot.target.kind).rawValue)
        let baseline: FilmDetail
        if let targetProvider = snapshot.target.metadataProvider,
           targetProvider.rawValue != original.item.sourceID ||
            metadataID != original.item.id ||
            (targetProvider == .tmdb && original.item.kind != nil && original.item.kind != kind) {
            baseline = .init(item: .init(sourceID: targetProvider.rawValue,
                id: metadataID ?? snapshot.target.id,
                title: snapshot.target.title, kind: kind))
        } else { baseline = original }
        let fields = snapshot.metadata.filmObject ?? [:]
        let title = text(fields, "title", "name") ?? baseline.title
        let score = number(fields["vote_average"]) ?? number(fields["rating"]?.filmObject?["value"])
        let poster = artwork(fields["poster_path"]) ?? image(fields["pic"]) ??
            image(fields["cover"]) ?? artwork(fields["cover_url"])
        let large = artwork(fields["pic"]?.filmObject?["large"])
        let item = FilmItem(sourceID: baseline.item.sourceID, id: baseline.item.id, title: title,
                            kind: provider == nil ? baseline.item.kind : kind,
                            posterURL: poster ?? baseline.item.posterURL,
                            largePosterURL: large ?? baseline.item.largePosterURL,
                            score: score.map { String(format: "%.1f", $0) } ?? baseline.item.score,
                            subtitle: baseline.item.subtitle)
        let seasonRows = fields["seasons"]?.filmArray ?? fields["doubanSeasons"]?.filmArray ??
            snapshot.metadataSources.first { $0.id == snapshot.metadataSourceID }?.seasons
        let seasons = seasonRows?.compactMap { raw -> FilmSeason? in
            guard let row = raw.filmObject, let number = integer(row["season_number"] ?? row["season"]) else { return nil }
            let episodes = snapshot.episodes.filter { $0.position.season == number }.map(episode)
            return .init(id: row["id"]?.filmScalar ?? "season-\(number)",
                         title: text(row, "name", "title") ?? "第 \(number) 季", number: number, episodes: episodes)
        } ?? baseline.seasons
        let credits = fields["credits"]?.filmObject
        let directors = credits?["crew"]?.filmArray?.filter { $0.filmObject?["job"] == .string("Director") } ??
            fields["directors"]?.filmArray ?? []
        let cast = credits?["crew"] != nil || credits?["cast"] != nil || fields["directors"] != nil || fields["actors"] != nil
            ? directors.compactMap { person($0, role: "导演") } +
                (credits?["cast"]?.filmArray ?? fields["actors"]?.filmArray ?? []).compactMap { person($0) }
            : baseline.cast
        let similar = fields["similar"]?.filmObject?["results"]?.filmArray?.compactMap { raw -> FilmItem? in
            guard let row = raw.filmObject, let id = row["id"]?.filmScalar,
                  let title = text(row, "title", "name"), let provider else { return nil }
            return .init(sourceID: provider.rawValue, id: id, title: title,
                         kind: provider == .tmdb ? kind : nil,
                         posterURL: artwork(row["poster_path"]),
                         score: row["vote_average"]?.filmNumber.map { String(format: "%.1f", $0) } ?? "")
        } ?? baseline.similarItems
        let logo = fields["images"]?.filmObject?["logos"]?.filmArray?.compactMap { value in
            artwork(value.filmObject?["file_path"])
        }.first
        return FilmDetail(item: item, backdropURL: artwork(fields["backdrop_path"]) ?? baseline.backdropURL,
                          title: title, logoURL: logo ?? baseline.logoURL,
                          summary: text(fields, "overview", "intro") ?? baseline.summary,
                          releaseDate: text(fields, "release_date", "first_air_date") ??
                            fields["pubdate"]?.filmArray?.compactMap(\.filmText).joined(separator: " · ") ?? baseline.releaseDate,
                          seasonCount: integer(fields["number_of_seasons"]) ?? baseline.seasonCount,
                          episodeCount: integer(fields["number_of_episodes"] ?? fields["episodes_count"]) ?? baseline.episodeCount,
                          seasons: seasons, cast: cast, similarItems: similar)
    }

    static func episode(_ value: FilmPlaybackEpisode) -> FilmEpisode {
        .init(id: value.displayID, title: value.name ?? "\(value.position.episode!)",
              number: value.position.episode, summary: value.raw["overview"]?.filmText ?? "",
              artworkURL: artwork(value.raw["still_path"]), releaseDate: value.raw["air_date"]?.filmText)
    }

    static func artwork(_ value: RuntimeValue?) -> URL? {
        guard let text = value?.filmText, !text.isEmpty else { return nil }
        if text.hasPrefix("/v1/tmdb/images/") || text.hasPrefix("/v1/douban/images/") {
            return URL(string: text)
        }
        guard let url = URL(string: text), ["http", "https"].contains(url.scheme?.lowercased() ?? "") else { return nil }
        return url
    }

    private static func text(_ fields: RuntimeObject, _ names: String...) -> String? {
        names.compactMap { fields[$0]?.filmText }.first { !$0.isEmpty }
    }

    private static func person(_ value: RuntimeValue, role: String? = nil) -> FilmCastMember? {
        if let name = value.filmText {
            return .init(id: name, name: name, role: role ?? "")
        }
        guard let row = value.filmObject, let name = row["name"]?.filmText else { return nil }
        return .init(id: row["id"]?.filmScalar ?? name, name: name,
                     role: role ?? text(row, "character", "role") ?? "",
                     portraitURL: artwork(row["profile_path"]) ?? image(row["avatar"]))
    }
    private static func image(_ value: RuntimeValue?) -> URL? {
        if let row = value?.filmObject {
            // Provider photo entities also contain business `url` links. Only
            // actual image variants are rendered; a detail link is not artwork.
            return artwork(row["normal"]) ?? artwork(row["large"]) ?? artwork(row["small"]) ?? artwork(row["src"])
        }
        return artwork(value)
    }
    private static func number(_ value: RuntimeValue?) -> Double? {
        guard let number = value?.filmNumber ?? value?.filmText.flatMap(Double.init),
              number.isFinite, number >= 0 else { return nil }
        return number
    }
    private static func integer(_ value: RuntimeValue?) -> Int? {
        if let number = value?.filmInteger { return number }
        guard let text = value?.filmText, !text.isEmpty,
              text.utf8.allSatisfy({ (48...57).contains($0) }) else { return nil }
        return Int(text)
    }
}

/// Keeps the original file/candidate identity while navigating display paths.
struct FilmPlaybackDirectory {
    enum Entry: Equatable {
        case folder(String)
        case file(FilmPlaybackFile)
    }
    let files: [FilmPlaybackFile]
    func children(at path: [String]) -> [Entry] {
        var folders = Set<Data>(), entries: [Entry] = []
        for file in files {
            let components = file.path?.split(separator: "/", omittingEmptySubsequences: false).map(String.init) ?? []
            let parents = components.isEmpty ? [] : Array(components.dropLast())
            guard parents.count >= path.count,
                  zip(parents, path).allSatisfy({ $0.utf8.elementsEqual($1.utf8) }) else { continue }
            if parents.count == path.count { entries.append(.file(file)) }
            else {
                let name = parents[path.count]
                if folders.insert(Data(name.utf8)).inserted { entries.append(.folder(name)) }
            }
        }
        return entries
    }
}
