import Foundation
import MeowRuntime

/// Projects normalized Node snapshots for UIKit without client-side JSON scraping or episode inference.
enum FilmPlaybackPresentation {
    static func detail(_ snapshot: FilmPlaybackSnapshot, original: FilmDetail) -> FilmDetail {
        let work = snapshot.work
        let entry = original.item
        let posterURL = artwork(.string(work.poster)) ?? entry.posterURL
        let backdropURL = artwork(.string(work.backdrop)) ?? original.backdropURL
        let logoURL = artwork(.string(work.logo))
        let itemKind = FilmItemKind(rawValue: work.kind.rawValue) ?? entry.kind

        let updatedEntry: FilmEntry? = if let base = entry.entry {
            FilmEntry(
                targetId: work.targetID.isEmpty ? base.targetId : work.targetID,
                keyword: work.keyword.isEmpty ? base.keyword : work.keyword,
                title: work.title.isEmpty ? base.title : work.title,
                subtitle: work.subtitle.isEmpty ? base.subtitle : work.subtitle,
                poster: work.poster.isEmpty ? base.poster : work.poster,
                backdrop: work.backdrop.isEmpty ? base.backdrop : work.backdrop,
                score: work.score.isEmpty ? base.score : work.score,
                textBadge: base.textBadge,
                siteLabel: base.siteLabel,
                kind: itemKind,
                year: Int(work.year) ?? base.year,
                season: base.season,
                episode: base.episode,
                seasonHint: base.seasonHint,
                tmdb: work.tmdb ?? base.tmdb,
                douban: work.douban ?? base.douban,
                siteEntry: base.siteEntry
            )
        } else if !work.targetID.isEmpty && !work.title.isEmpty {
            FilmEntry(
                targetId: work.targetID,
                keyword: work.keyword.isEmpty ? work.title : work.keyword,
                title: work.title,
                subtitle: work.subtitle,
                poster: work.poster,
                backdrop: work.backdrop,
                score: work.score,
                kind: itemKind,
                year: Int(work.year),
                tmdb: work.tmdb,
                douban: work.douban
            )
        } else {
            nil
        }

        let item = FilmItem(
            sourceID: entry.sourceID,
            id: entry.id,
            title: work.title.isEmpty ? entry.title : work.title,
            kind: itemKind,
            posterURL: posterURL,
            largePosterURL: entry.largePosterURL ?? posterURL,
            score: work.score.isEmpty ? entry.score : work.score,
            subtitle: work.subtitle.isEmpty ? entry.subtitle : work.subtitle,
            entry: updatedEntry
        )

        let metaLine = [work.year, work.countries, work.duration, work.genres.joined(separator: " / ")]
            .filter { !$0.isEmpty }
            .joined(separator: " · ")

        return FilmDetail(
            item: item,
            backdropURL: backdropURL,
            title: item.title,
            logoURL: logoURL,
            summary: work.summary,
            releaseDate: metaLine.isEmpty ? original.releaseDate : metaLine,
            seasonCount: work.seasonCount,
            episodeCount: work.episodeCount,
            seasons: [],
            cast: work.cast,
            similarItems: work.relatedItems
        )
    }

    static func episode(from item: FilmDirectoryItem, mode: String, fallbackArtworkURL: URL?) -> FilmEpisode {
        let artworkURL = artwork(.string(item.still)) ?? fallbackArtworkURL
        if mode == "episodes" {
            let subParts = [item.duration, item.airDate, item.quality, item.sourceLabel]
                .filter { !$0.isEmpty }
            let summaryText = !item.summary.isEmpty
                ? item.summary
                : subParts.joined(separator: " · ")
            return FilmEpisode(
                id: item.id,
                title: item.title,
                number: nil,
                summary: summaryText,
                artworkURL: artworkURL,
                releaseDate: item.airDate.isEmpty ? nil : item.airDate
            )
        }
        let subParts = [item.quality, item.sourceLabel, item.duration].filter { !$0.isEmpty }
        return FilmEpisode(
            id: item.id,
            title: item.fileName.isEmpty ? item.title : item.fileName,
            number: item.episode,
            summary: subParts.joined(separator: " · "),
            artworkURL: artworkURL,
            releaseDate: nil
        )
    }

    static func artwork(_ value: RuntimeValue?) -> URL? {
        guard let text = value?.filmText, !text.isEmpty else { return nil }
        if let reference = RuntimeHTTPClient.metadataImageURL(text) { return reference }
        guard let url = URL(string: text), ["http", "https"].contains(url.scheme?.lowercased() ?? "") else { return nil }
        return url
    }
}

/// Navigates raw file paths inside a concrete site/pan directory.
struct FilmPlaybackDirectory {
    enum Entry: Equatable {
        case folder(String)
        case file(FilmDirectoryItem)
    }

    let files: [FilmDirectoryItem]

    func children(at path: [String]) -> [Entry] {
        var folders = Set<Data>(), entries: [Entry] = []
        for file in files {
            let components = file.path.split(whereSeparator: { $0 == "/" || $0 == "\\" }).map(String.init)
            let parents = components.isEmpty ? [] : Array(components.dropLast())
            guard parents.count >= path.count,
                  zip(parents, path).allSatisfy({ $0.utf8.elementsEqual($1.utf8) }) else { continue }
            if parents.count == path.count {
                entries.append(.file(file))
            } else {
                let name = parents[path.count]
                if folders.insert(Data(name.utf8)).inserted {
                    entries.append(.folder(name))
                }
            }
        }
        return entries
    }
}
