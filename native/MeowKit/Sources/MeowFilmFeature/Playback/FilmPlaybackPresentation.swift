import Foundation
import MeowRuntime

/// Projects loaded provider data for UIKit. No episode inference, pan aliases,
/// metadata fetching or freshness policy belongs in this projection.
enum FilmPlaybackPresentation {
    static func detail(_ snapshot: FilmPlaybackSnapshot, original: FilmDetail) -> FilmDetail {
        guard let work = snapshot.workInfo else { return original }
        let fields = work.metadata
        let rating = field(fields, "vote_average", "rating")
        let score = rating.flatMap { number($0.filmObject?["value"] ?? $0) }
        let poster = field(fields, "poster_path", "pic", "cover", "cover_url")
        let large = artwork(fields["pic"]?.filmObject?["large"])
        let entry = original.item
        let item = FilmItem(sourceID: entry.sourceID, id: entry.id, title: work.title, kind: entry.kind,
                            posterURL: image(poster), largePosterURL: large ?? image(poster),
                            score: score.map { String(format: "%.1f", $0) } ?? "",
                            subtitle: fields["card_subtitle"]?.filmText ?? "", metadataSeed: entry.metadataSeed,
                            businessID: entry.businessID, playbackTarget: entry.playbackTarget)
        let releaseDate = field(fields, "release_date", "first_air_date", "pubdate")
        return FilmDetail(item: item,
                          backdropURL: artwork(fields["backdrop_path"]),
                          title: work.title, logoURL: logo(fields), summary: work.summary,
                          releaseDate: releaseDate?.filmText ??
                            releaseDate?.filmArray?.compactMap(\.filmText).joined(separator: " · "),
                          seasonCount: work.seasonCount, episodeCount: work.episodeCount,
                          seasons: seasons(field(fields, "doubanSeasons", "seasons")?.filmArray ?? []),
                          cast: cast(fields) ?? [], similarItems: similar(fields, provider: work.provider) ?? [])
    }

    static func seasons(_ snapshot: FilmPlaybackSnapshot) -> [FilmSeason] {
        let fields = snapshot.metadata.filmObject ?? [:]
        let seasonField = field(fields, "doubanSeasons", "seasons")
        let rows = seasonField == nil && !succeeded(snapshot.metadataCache, component: "seasons")
            ? snapshot.metadataSources.first { $0.id == snapshot.metadataSourceID }?.seasons ?? []
            : seasonField?.filmArray ?? []
        return seasons(rows, episodes: snapshot.episodes)
    }

    private static func seasons(_ rows: [RuntimeValue], episodes: [FilmPlaybackEpisode] = []) -> [FilmSeason] {
        rows.compactMap { raw in
            guard let row = raw.filmObject, let number = integer(row["season_number"] ?? row["season"]),
                  let businessID = row["businessId"]?.filmText,
                  let target = row["playbackTarget"].flatMap({ try? FilmPlaybackTarget($0) }) else { return nil }
            let poster = artwork(row["poster_path"])
            return .init(id: businessID, title: text(row, "name", "title") ?? "第 \(number) 季",
                         number: number, posterURL: poster,
                         episodes: episodes.filter { $0.position.season == number }.map {
                             episode($0, fallbackArtworkURL: poster)
                         }, businessID: businessID, playbackTarget: target)
        }
    }

    static func episode(_ value: FilmPlaybackEpisode, fallbackArtworkURL: URL? = nil) -> FilmEpisode {
        .init(id: value.displayID, title: value.name ?? "",
              number: value.position.episode, summary: value.raw["overview"]?.filmText ?? "",
              artworkURL: artwork(value.raw["still_path"]) ?? fallbackArtworkURL,
              releaseDate: value.raw["air_date"]?.filmText, businessID: value.businessID, playbackTarget: value.playbackTarget)
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

    private static func similar(_ fields: RuntimeObject, provider: FilmMetadataProvider?) -> [FilmItem]? {
        guard fields["similar"] != nil else { return nil }
        return (fields["similar"]?.filmObject?["results"]?.filmArray ?? []).compactMap { raw in
            guard let row = raw.filmObject, let id = row["id"]?.filmScalar,
                  let title = text(row, "title", "name"), let provider,
                  let businessID = row["businessId"]?.filmText,
                  let target = row["playbackTarget"].flatMap({ try? FilmPlaybackTarget($0) }) else { return nil }
            return .init(sourceID: provider.rawValue, id: id, title: title, kind: FilmItemKind(rawValue: target.kind.rawValue),
                         posterURL: artwork(row["poster_path"]),
                         score: row["vote_average"]?.filmNumber.map { String(format: "%.1f", $0) } ?? "",
                         businessID: businessID, playbackTarget: target)
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

struct FilmPlaybackDirectoryKey: Hashable {
    let source: String?
    let line: FilmPlaybackIdentifier?
    let metadata: String?
    let season: Int?
    init(source: String?, line: FilmPlaybackIdentifier?, metadata: String?, season: Int?) {
        self.source = source; self.line = line; self.metadata = metadata; self.season = season
    }
    init(_ snapshot: FilmPlaybackSnapshot) {
        self.init(source: snapshot.browsing.sourceID, line: snapshot.browsing.lineID,
                  metadata: snapshot.metadataSourceID, season: snapshot.browsing.season)
    }
    func accepts(_ actual: Self) -> Bool {
        source == actual.source && (metadata == nil || metadata == actual.metadata) &&
            (line == nil || line == actual.line) && (season == nil || season == actual.season)
    }
}

/// Only successful display data; never a stored live playback snapshot.
struct FilmPlaybackDirectorySlice {
    let key: FilmPlaybackDirectoryKey
    let browsing: FilmPlaybackBrowsing
    let metadata: RuntimeValue
    let metadataCache: FilmMetadataCache?
    let metadataSupplement: FilmPlaybackMetadataSupplement?
    var episodes: [FilmPlaybackEpisode]
    let candidates: [FilmPlaybackCandidate]

    init(_ snapshot: FilmPlaybackSnapshot) {
        key = .init(snapshot); browsing = snapshot.browsing
        metadata = snapshot.metadata; metadataCache = snapshot.metadataCache
        metadataSupplement = snapshot.metadataSupplement
        episodes = snapshot.episodes; candidates = snapshot.candidates
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
