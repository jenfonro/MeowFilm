#if os(iOS) || os(macOS)
import Foundation
import MeowRuntime

/// Lightweight handoff from search results to detail/playback.
/// Node resolves all matched group members, raw-card site selection, and cross-query display attachments internally.
@MainActor
public final class FilmSearchContext {
    public let session: FilmSearchSession
    public let selectedCard: FilmSearchCard

    init(session: FilmSearchSession, card: FilmSearchCard) {
        self.session = session
        self.selectedCard = card
    }

    public var snapshot: FilmSearchSnapshot { session.snapshot }
    public var selectionID: String { selectedCard.id }

    public var card: FilmSearchCard {
        (snapshot.cards + snapshot.rawCards).first {
            $0.id.utf8.elementsEqual(selectionID.utf8)
        } ?? selectedCard
    }

    public func playbackRequest() -> FilmPlaybackRequest {
        let current = card
        return FilmPlaybackRequest(
            entry: current.entry,
            searchReference: session.reference(for: selectionID)
        )
    }

    public var detail: FilmDetail {
        let current = card
        let cardItem = current.filmItem(imageURL: session.imageURL)
        return FilmDetail(
            item: cardItem,
            backdropURL: session.imageURL(current.entry.backdrop)
        )
    }
}
#endif
