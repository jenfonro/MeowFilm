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

    public var selectionID: String { selectedCard.displayKey }
    public var isRawSelection: Bool {
        selectedCard.sourceKind == "site" && !selectedCard.displayKey.hasPrefix("group:")
    }

    /// Only the shared search carrier can associate a group with metadata.
    /// A raw card remains a source even when its display has TMDB decoration.
    public func playbackTarget() throws -> FilmPlaybackTarget {
        let current = card
        if !isRawSelection, current.sourceKind == "tmdb",
           let id = current.tmdbID, let kind = FilmPlaybackKind(rawValue: current.tmdbType),
           kind == .movie || kind == .tv {
            return .init(id: selectionID, kind: kind, title: current.title,
                         metadataProvider: .tmdb, metadataID: String(id))
        }
        let members = try sources.map { row -> RuntimeValue in
            guard let key = row.catField("siteKey")?.catText, let detailID = row.catField("detailId") else {
                throw FilmPlaybackError.invalidResponse
            }
            _ = try FilmPlaybackIdentifier(detailID)
            return .object(["sourceKey": .string(key), "detailId": detailID])
        }
        guard !members.isEmpty else { throw FilmPlaybackError(code: "SOURCE_NOT_FOUND") }
        if isRawSelection {
            let member = try members[0].filmRequiredObject()
            return .init(id: selectionID, kind: .source, title: selectedCard.title,
                source: ["key": try member.filmValue("sourceKey"), "detailId": try member.filmValue("detailId")])
        }
        return .init(id: selectionID, kind: .group, title: current.title, members: members)
    }

    /// Open uses the intersection of the original admitted query scope and the
    /// currently authorized sources. A matching generation is not owner proof.
    public func playbackRequest(base: FilmPlaybackRequest, owner: RuntimeHTTPClient?,
                                target: FilmPlaybackTarget? = nil) throws -> FilmPlaybackRequest {
        var target = try target ?? playbackTarget()
        let scope = try session.admittedSources.compactMap { original -> FilmPlaybackSource? in
            guard !isRawSelection || original.key == selectedCard.text("siteKey"),
                  let current = base.sources.first(where: {
                      Self.sameSource(original, $0) && $0.value["enabled"] == .bool(true) &&
                          $0.value["searchable"] == .bool(true)
                  }) else { return nil }
            // The old key still names the selected rows/members. Installation
            // identity grants no old generation: execution uses today's scope.
            var fields = current.value
            fields["key"] = original.value["key"]
            return try FilmPlaybackSource(.object(fields))
        }
        if target.kind == .source {
            guard scope.contains(where: { $0.key == target.value["source"]?.filmObject?["key"]?.filmText }) else {
                throw FilmPlaybackError(code: "SOURCE_NOT_FOUND")
            }
        }
        if target.kind == .group {
            let members = (target.value["members"]?.filmArray ?? []).filter { member in
                scope.contains { $0.key == member.filmObject?["sourceKey"]?.filmText }
            }
            guard !members.isEmpty else { throw FilmPlaybackError(code: "SOURCE_NOT_FOUND") }
            var fields = target.value
            fields["members"] = .array(members)
            if let selected = fields["source"]?.filmObject, !members.contains(where: {
                $0.filmObject?["sourceKey"] == selected["key"] &&
                    $0.filmObject?["detailId"] == selected["detailId"]
            }) { fields["source"] = nil }
            target = try FilmPlaybackTarget(.object(fields))
        }
        let handoff = handoff(sources: scope, owner: owner)
        let associated = try playbackTarget()
        let sameMetadata = target.metadataProvider != nil && target.metadataProvider == associated.metadataProvider &&
            target.value["metadataId"] == associated.value["metadataId"]
        return .init(target: target, sources: scope, query: snapshot.query,
            results: handoff.results, resultHints: handoff.hints, search: handoff.search,
            metadata: sameMetadata ? metadataDetail ?? base.metadata : nil,
            cacheEntries: handoff.cacheEntries, rules: base.rules)
    }

    func playbackUpdate(sources: [FilmPlaybackSource], owner: RuntimeHTTPClient?,
                        target: FilmPlaybackTarget) throws -> RuntimeObject {
        let handoff = handoff(sources: sources, owner: owner)
        var fields: RuntimeObject = [
            "selectionId": .string(selectionID), "revision": .number(Double(snapshot.revision)),
            "sources": .array(sources.map { .object($0.value) }),
            "results": .array(handoff.results), "resultHints": .array(handoff.hints),
            "search": .array(handoff.search), "cacheEntries": .array(handoff.cacheEntries),
        ]
        let associated = try playbackTarget()
        if associated.metadataProvider != nil,
           target.kind == .group || (target.kind == associated.kind &&
            target.metadataProvider == associated.metadataProvider &&
            target.value["metadataId"] == associated.value["metadataId"]) {
            fields["metadataTarget"] = .object(associated.value)
            fields["metadata"] = metadataDetail
        }
        return fields
    }

    private func handoff(sources: [FilmPlaybackSource], owner: RuntimeHTTPClient?) ->
        (results: [RuntimeValue], hints: [RuntimeValue], search: [RuntimeValue], cacheEntries: [RuntimeValue]) {
        var results: [RuntimeValue] = [], hints: [RuntimeValue] = [], search: [RuntimeValue] = []
        var entries: [RuntimeValue] = []
        for source in sources {
            let pages = session.pages.filter { $0.site.key == source.key }
            guard !pages.isEmpty else { continue }
            let sameOwner = !session.invalidated && owner != nil && owner?.isClosed == false &&
                pages.allSatisfy {
                    $0.read.producer === owner && Self.sameSource($0.site.playbackSource, source) &&
                        $0.site.playbackSource.value["generation"] == source.value["generation"] &&
                        $0.site.playbackSource.value["revision"] == source.value["revision"]
                }
            let items = snapshot.siteItems.filter {
                $0.catField("siteKey")?.catText == source.key &&
                    (!isRawSelection || $0.catField("detailId") == selectedCard.value.catField("detailId"))
            }.compactMap { $0.catField("raw") }
            let result: RuntimeValue = .object(["sourceKey": .string(source.key!), "items": .array(items)])
            if sameOwner {
                results.append(result)
                if let cursor = session.confirmedSearch(sourceKey: source.key!) { search.append(cursor) }
                entries += pages.compactMap(\.read.cacheEntry)
            } else { hints.append(result) }
        }
        // Metadata seeds retain their producer's opaque settings revision and
        // timestamps. Node, not this view, decides whether they are still valid.
        if !isRawSelection {
            if let read = session.metadataReads["search"], let entry = read.cacheEntry { entries.append(entry) }
            if let id = card.tmdbID, let read = session.metadataReads["\(card.tmdbType):\(id)"],
               let entry = read.cacheEntry { entries.append(entry) }
        }
        return (results, hints, search, entries)
    }

    private static func sameSource(_ lhs: FilmPlaybackSource, _ rhs: FilmPlaybackSource) -> Bool {
        ["engine", "installationId", "siteId"].allSatisfy { lhs.value[$0] == rhs.value[$0] }
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
