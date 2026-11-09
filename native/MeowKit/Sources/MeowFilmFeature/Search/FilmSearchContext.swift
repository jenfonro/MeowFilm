#if os(iOS) || os(macOS)
import Foundation
import MeowRuntime

/// Actual detail navigation retains this object. Its live snapshot continues to
/// expose sources arriving after the tap without issuing another search.
@MainActor
public final class FilmSearchContext {
    public let session: FilmSearchSession
    public let selectedCard: FilmSearchCard
    init(session: FilmSearchSession, card: FilmSearchCard) {
        self.session = session; selectedCard = card
    }
    public var snapshot: FilmSearchSnapshot { session.snapshot }
    public var card: FilmSearchCard {
        // A raw-list selection keeps the original item identity. Group cards
        // instead follow the shared program's actual member-to-TMDB relation,
        // never a second title match or a new search in the native consumer.
        if let same = (snapshot.cards + snapshot.rawCards).first(where: {
            $0.displayKey.utf8.elementsEqual(selectedCard.displayKey.utf8)
        }) {
            return same
        }
        guard selectedCard.displayKey.hasPrefix("group:") else { return selectedCard }
        let selectedIDs = Set((selectedCard.members.isEmpty ? [selectedCard.value] : selectedCard.members)
            .compactMap { $0.catField("id")?.catText }.map { Data($0.utf8) })
        return snapshot.cards.first { candidate in
            candidate.sourceKind == "tmdb" && candidate.members.contains {
                guard let id = $0.catField("id")?.catText else { return false }
                return selectedIDs.contains(Data(id.utf8))
            }
        } ?? selectedCard
    }
    public var sources: [RuntimeValue] {
        let current = card
        if !current.members.isEmpty { return current.members }
        if current.sourceKind == "site" { return [current.value] }
        return []
    }
    public var metadataDetail: RuntimeValue? {
        let current = card
        guard let id = current.tmdbID, !current.tmdbType.isEmpty else { return nil }
        if let detail = snapshot.metadataDetails["\(current.tmdbType):\(id)"] { return detail }
        return snapshot.metadataSearch?.catField("results")?.catArray?.first {
            $0.catField("media_type") == .string(current.tmdbType) && $0.catField("id")?.catInteger == id
        }
    }
    public var detail: FilmDetail {
        let current = card
        let item = current.filmItem(imageURL: session.imageURL)
        let payload = metadataDetail
        return FilmDetail(item: item, backdropURL: session.imageURL(current.text("backdrop")),
            summary: payload?.catField("overview")?.catText ?? "",
            releaseDate: payload?.catField(current.tmdbType == "movie" ? "release_date" : "first_air_date")?.catText,
            seasonCount: payload?.catField("number_of_seasons")?.catInteger,
            episodeCount: payload?.catField("number_of_episodes")?.catInteger)
    }
}
#endif
