#if os(iOS) || os(macOS)
import Foundation
import MeowRuntime

/// A navigation selection keeps its original opaque display identity. Only Node
/// follows late members and reliable metadata carriers for that selection.
@MainActor
public final class FilmSearchContext {
    public let session: FilmSearchSession
    public let selectedCard: FilmSearchCard
    private var received: FilmSearchHandoff?
    var handoffRevision: Int { received?.revision ?? -1 }
    init(session: FilmSearchSession, card: FilmSearchCard) {
        self.session = session; selectedCard = card
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
        if let received { return received.request.metadata }
        let current = card
        guard current.sourceKind == "tmdb", let id = current.tmdbID else { return nil }
        if let detail = snapshot.metadataDetails["\(current.tmdbType):\(id)"] { return detail }
        return snapshot.metadataSearch?.catField("results")?.catArray?.first {
            $0.catField("media_type") == .string(current.tmdbType) && $0.catField("id")?.catInteger == id
        }
    }

    public func playbackRequest(sources: [FilmPlaybackSource], owner: RuntimeHTTPClient?,
                                target: FilmPlaybackTarget? = nil) async throws -> FilmPlaybackRequest {
        let handoff = try await session.handoff(selectionID: selectionID, sources: sources, owner: owner)
        guard handoff.selectionID.utf8.elementsEqual(selectionID.utf8) else { throw FilmSearchError.invalidResponse }
        received = handoff
        return target.map { handoff.request.retargeted($0) } ?? handoff.request
    }

    func playbackUpdate(sources: [FilmPlaybackSource], owner: RuntimeHTTPClient?) async throws -> (fields: RuntimeObject, revision: Int) {
        let handoff = try await session.handoff(selectionID: selectionID, sources: sources, owner: owner)
        guard handoff.selectionID.utf8.elementsEqual(selectionID.utf8) else { throw FilmSearchError.invalidResponse }
        received = handoff
        // Node resolves this reference against the current playback selection;
        // the client neither manufactures facts nor chooses a metadata carrier.
        return (["selectionId": .string(selectionID), "revision": .number(Double(handoff.revision)),
                 "searchReference": handoff.reference,
                 "sources": .array(sources.map { .object($0.value) })], handoff.revision)
    }

    public var detail: FilmDetail {
        let current = card
        let payload = metadataDetail
        return FilmDetail(item: current.filmItem(imageURL: session.imageURL),
            backdropURL: session.imageURL(current.text("backdrop")),
            summary: payload?.catField("overview")?.catText ?? "",
            releaseDate: payload?.catField(current.tmdbType == "movie" ? "release_date" : "first_air_date")?.catText,
            seasonCount: payload?.catField("number_of_seasons")?.catInteger,
            episodeCount: payload?.catField("number_of_episodes")?.catInteger)
    }
}

/// Exact facts exported by the same Node search owner, never reconstructed from
/// card labels, display snapshots or client timestamps.
struct FilmSearchHandoff {
    let request: FilmPlaybackRequest
    let selectionID: String
    let revision: Int
    let reference: RuntimeValue

    init(_ value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        selectionID = try fields.filmString("selectionId")
        guard let revision = fields["revision"]?.filmInteger,
              let reference = fields["searchReference"], let ref = reference.filmObject,
              let sessionID = ref["sessionId"]?.filmText, UUID(uuidString: sessionID) != nil,
              ref["selectionId"] == .string(selectionID) else { throw FilmSearchError.invalidResponse }
        self.revision = revision; self.reference = reference
        request = .init(target: try FilmPlaybackTarget(fields.filmValue("target")),
            sources: try fields.filmArray("sources").map(FilmPlaybackSource.init),
            query: try fields.filmString("query"), results: try fields.filmArray("results"),
            resultHints: try fields.filmArray("resultHints"), search: try fields.filmArray("search"),
            metadata: fields["metadata"].flatMap { $0 == .null ? nil : $0 },
            cacheEntries: try fields.filmArray("cacheEntries"), searchReference: reference)
    }
}
#endif
