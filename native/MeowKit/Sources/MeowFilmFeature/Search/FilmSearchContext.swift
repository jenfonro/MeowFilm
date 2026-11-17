#if os(iOS) || os(macOS)
import Foundation
import MeowRuntime

/// A navigation selection keeps its original opaque display identity. Only Node
/// follows late members and reliable metadata carriers for that selection.
@MainActor
public final class FilmSearchContext {
    public let session: FilmSearchSession
    public let selectedCard: FilmSearchCard
    private let authorizedSources: [FilmPlaybackSource]
    private let query: String
    init(session: FilmSearchSession, card: FilmSearchCard) {
        self.session = session; selectedCard = card
        authorizedSources = session.snapshot.sources; query = session.snapshot.query
    }
    public var snapshot: FilmSearchSnapshot { session.snapshot }
    public var selectionID: String { selectedCard.displayKey }
    public var card: FilmSearchCard {
        (snapshot.cards + snapshot.rawCards).first {
            $0.displayKey.utf8.elementsEqual(selectionID.utf8)
        } ?? selectedCard
    }
    public var sources: [RuntimeValue] {
        let current = card
        return current.members.isEmpty && current.sourceKind == "site" ? [current.value] : current.members
    }
    public var metadataDetail: RuntimeValue? {
        let current = card
        guard current.sourceKind == "tmdb", let id = current.tmdbID else { return nil }
        if let detail = snapshot.metadataDetails["\(current.tmdbType):\(id)"] { return detail }
        return snapshot.metadataSearch?.catField("results")?.catArray?.first {
            $0.catField("media_type") == .string(current.tmdbType) && $0.catField("id")?.catInteger == id
        }
    }

    public func playbackRequest(sources available: [FilmPlaybackSource]? = nil) throws -> FilmPlaybackRequest {
        guard let target = selectedCard.playbackTarget, !selectedCard.businessID.isEmpty else {
            throw FilmSearchError.invalidResponse
        }
        let sources = try available.map { current in
            try authorizedSources.compactMap { previous -> FilmPlaybackSource? in
                guard let source = current.first(where: { candidate in
                    ["engine", "installationId", "siteId"].allSatisfy {
                        candidate.value[$0] == previous.value[$0]
                    }
                }) else { return nil }
                var fields = source.value
                fields["key"] = previous.value["key"]
                return try FilmPlaybackSource(.object(fields))
            }
        } ?? authorizedSources
        return .init(target: target, sources: sources, query: query,
                     metadata: selectedCard.sourceKind == "tmdb" ? metadataDetail : nil,
                     searchReference: session.reference(for: selectionID), entrySurface: "search")
    }

    func playbackUpdate() -> (fields: RuntimeObject, revision: Int)? {
        guard let reference = session.reference(for: selectionID), let target = selectedCard.playbackTarget else { return nil }
        return (["selectionId": .string(target.id),
                 "revision": .number(Double(snapshot.sequence)), "searchReference": reference,
                 "sources": .array(authorizedSources.map { .object($0.value) })], Int(snapshot.sequence))
    }

    public var detail: FilmDetail {
        let current = card
        let payload = metadataDetail
        let cardItem = current.filmItem(imageURL: session.imageURL)
        let poster = payload?.catField("poster_path")?.catText.flatMap(session.imageURL)
        let item = FilmItem(sourceID: cardItem.sourceID, id: cardItem.id, title: cardItem.title, kind: cardItem.kind,
            posterURL: poster ?? cardItem.posterURL, largePosterURL: poster ?? cardItem.largePosterURL,
            score: cardItem.score, subtitle: cardItem.subtitle, metadataSeed: cardItem.metadataSeed,
            businessID: cardItem.businessID, playbackTarget: cardItem.playbackTarget)
        return FilmDetail(item: item,
            backdropURL: payload?.catField("backdrop_path")?.catText.flatMap(session.imageURL) ?? session.imageURL(current.text("backdrop")),
            summary: payload?.catField("overview")?.catText ?? "",
            releaseDate: payload?.catField(current.tmdbType == "movie" ? "release_date" : "first_air_date")?.catText,
            seasonCount: payload?.catField("number_of_seasons")?.catInteger,
            episodeCount: payload?.catField("number_of_episodes")?.catInteger)
    }
}

#endif
