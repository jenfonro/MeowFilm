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
        let hasDetail = succeeded(snapshot.metadataCache, component: "detail")
        let supplement = provider == .douban ? snapshot.metadataSupplement?.metadata.filmObject ?? [:] : [:]
        let title = field(fields, "title", "name").map { $0.filmText ?? "" } ?? baseline.title
        let rating = field(fields, "vote_average", "rating")
        let score = rating.flatMap { number($0.filmObject?["value"] ?? $0) }
        let poster = field(fields, "poster_path", "pic", "cover", "cover_url")
        let large = artwork(fields["pic"]?.filmObject?["large"])
        let supplementalPoster = artwork(supplement["poster_path"])
        let item = FilmItem(sourceID: baseline.item.sourceID, id: baseline.item.id, title: title,
                            kind: provider == nil ? baseline.item.kind : kind,
                            posterURL: image(poster) ?? supplementalPoster ?? (poster == nil && !hasDetail ? baseline.item.posterURL : nil),
                            largePosterURL: large ?? supplementalPoster ?? (poster == nil && !hasDetail ? baseline.item.largePosterURL : nil),
                            score: rating == nil && !hasDetail ? baseline.item.score : score.map { String(format: "%.1f", $0) } ?? "",
                            subtitle: baseline.item.subtitle, metadataSeed: baseline.item.metadataSeed)
        let seasonField = field(fields, "doubanSeasons", "seasons")
        let seasonRows = seasonField == nil && !succeeded(snapshot.metadataCache, component: "seasons")
            ? displayMetadata?.seasons : seasonField?.filmArray ?? []
        let seasons = seasonRows?.compactMap { raw -> FilmSeason? in
            guard let row = raw.filmObject, let number = integer(row["season_number"] ?? row["season"]) else { return nil }
            let poster = artwork(row["poster_path"])
            let episodes = snapshot.episodes.filter { $0.position.season == number }.map {
                episode($0, fallbackArtworkURL: poster ?? item.posterURL ?? item.largePosterURL)
            }
            return .init(id: row["id"]?.filmScalar ?? "season-\(number)",
                         title: text(row, "name", "title") ?? "第 \(number) 季", number: number,
                         posterURL: poster, episodes: episodes)
        } ?? baseline.seasons
        let primaryCast = cast(fields), supplementalCast = cast(supplement)
        let cast = primaryCast.flatMap { $0.isEmpty ? nil : $0 } ??
            supplementalCast.flatMap { $0.isEmpty ? nil : $0 } ?? primaryCast ??
            (succeeded(snapshot.metadataCache, component: "credits") ? [] : baseline.cast)
        let primarySimilar = similar(fields, provider: provider, kind: kind)
        let supplementalSimilar = similar(supplement, provider: .tmdb,
                                           kind: snapshot.metadataSupplement.flatMap { FilmItemKind(rawValue: $0.kind.rawValue) })
        let similar = primarySimilar.flatMap { $0.isEmpty ? nil : $0 } ??
            supplementalSimilar.flatMap { $0.isEmpty ? nil : $0 } ?? primarySimilar ??
            (succeeded(snapshot.metadataCache, component: "similar") ? [] : baseline.similarItems)
        let logo = logo(fields) ?? logo(supplement) ??
            (fields["images"] == nil && !succeeded(snapshot.metadataCache, component: "images") ? baseline.logoURL : nil)
        let summary = field(fields, "overview", "intro")
        let summaryText = summary?.filmText ?? ""
        let displaySummary = summaryText.isEmpty
            ? text(supplement, "overview") ?? (summary == nil && !hasDetail ? baseline.summary : "")
            : summaryText
        let releaseDate = field(fields, "release_date", "first_air_date", "pubdate")
        let episodeCount = field(fields, "number_of_episodes", "episodes_count")
        return FilmDetail(item: item,
                          backdropURL: artwork(fields["backdrop_path"]) ?? artwork(supplement["backdrop_path"]) ??
                            (fields["backdrop_path"] == nil && !hasDetail ? baseline.backdropURL : nil),
                          title: title, logoURL: logo, summary: displaySummary,
                          releaseDate: releaseDate == nil && !hasDetail ? baseline.releaseDate : releaseDate?.filmText ??
                            releaseDate?.filmArray?.compactMap(\.filmText).joined(separator: " · "),
                          seasonCount: fields["number_of_seasons"] == nil && !hasDetail ? baseline.seasonCount : integer(fields["number_of_seasons"]),
                          episodeCount: episodeCount == nil && !hasDetail ? baseline.episodeCount : integer(episodeCount),
                          seasons: seasons, cast: cast, similarItems: similar)
    }

    static func episode(_ value: FilmPlaybackEpisode, fallbackArtworkURL: URL? = nil) -> FilmEpisode {
        .init(id: value.displayID, title: value.name ?? "",
              number: value.position.episode, summary: value.raw["overview"]?.filmText ?? "",
              artworkURL: artwork(value.raw["still_path"]) ?? fallbackArtworkURL,
              releaseDate: value.raw["air_date"]?.filmText)
    }

    static func artwork(_ value: RuntimeValue?) -> URL? {
        guard let text = value?.filmText, !text.isEmpty else { return nil }
        if let reference = RuntimeHTTPClient.metadataImageURL(text) { return reference }
        guard let url = URL(string: text), ["http", "https"].contains(url.scheme?.lowercased() ?? "") else { return nil }
        return url
    }

    private static func text(_ fields: RuntimeObject, _ names: String...) -> String? {
        names.compactMap { fields[$0]?.filmText }.first { !$0.isEmpty }
    }

    private static func field(_ fields: RuntimeObject, _ names: String...) -> RuntimeValue? {
        names.compactMap { fields[$0] }.first
    }

    private static func succeeded(_ cache: FilmMetadataCache?, component: String) -> Bool {
        guard let fields = cache?.components[component]?.filmObject else { return false }
        return fields["status"] == .string("ready") || fields["status"] == .string("empty") ||
            fields["fetchedAt"]?.filmNumber != nil
    }

    private static func cast(_ fields: RuntimeObject) -> [FilmCastMember]? {
        guard fields["credits"] != nil || fields["directors"] != nil || fields["actors"] != nil else { return nil }
        let credits = fields["credits"]?.filmObject
        let directors = credits?["crew"]?.filmArray?.filter { $0.filmObject?["job"] == .string("Director") } ??
            fields["directors"]?.filmArray ?? []
        return directors.compactMap { person($0, role: "导演") } +
            (credits?["cast"]?.filmArray ?? fields["actors"]?.filmArray ?? []).compactMap { person($0) }
    }

    private static func similar(_ fields: RuntimeObject, provider: FilmMetadataProvider?,
                                kind: FilmItemKind?) -> [FilmItem]? {
        guard fields["similar"] != nil else { return nil }
        return (fields["similar"]?.filmObject?["results"]?.filmArray ?? []).compactMap { raw in
            guard let row = raw.filmObject, let id = row["id"]?.filmScalar,
                  let title = text(row, "title", "name"), let provider else { return nil }
            return .init(sourceID: provider.rawValue, id: id, title: title, kind: provider == .tmdb ? kind : nil,
                         posterURL: artwork(row["poster_path"]),
                         score: row["vote_average"]?.filmNumber.map { String(format: "%.1f", $0) } ?? "")
        }
    }

    private static func logo(_ fields: RuntimeObject) -> URL? {
        fields["images"]?.filmObject?["logos"]?.filmArray?.compactMap { artwork($0.filmObject?["file_path"]) }.first
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
