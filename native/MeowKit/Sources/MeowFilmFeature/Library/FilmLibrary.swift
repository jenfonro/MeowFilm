import Combine
import Foundation
import MeowRuntime
import MeowStorage

public enum FilmLibraryError: Error, Equatable, LocalizedError {
    case corruptedData
    case missingKind, missingTarget

    public var errorDescription: String? {
        switch self {
        case .corruptedData: "收藏数据已损坏，原数据已保留，无法更改收藏。"
        case .missingKind: "缺少 TMDB 电影或剧集类型，无法更改收藏。"
        case .missingTarget: "缺少影片目标，无法更改收藏。"
        }
    }
}

@MainActor
public final class FilmLibrary: ObservableObject {
    @Published public private(set) var favorites: Set<FilmItemIdentity> = []
    @Published public private(set) var loadError: FilmLibraryError?
    private var database: SQLiteDatabase?
    private var changes: [FilmItemIdentity: UUID] = [:]
    private var closed = false

    public init() {}
    public func bind(database: SQLiteDatabase) async throws {
        guard self.database == nil, !closed else { return }
        do {
            let records = try await database.snapshot { try FilmFavoriteRepository.load($0) }
            let saved = try Self.projectIdentities(records)
            guard !closed else { return }
            self.database = database; favorites = saved; loadError = nil
        } catch {
            loadError = .corruptedData
            throw error
        }
    }
    public func contains(_ item: FilmItem) -> Bool { favorites.contains(item.identity) }

    @discardableResult
    public func toggle(_ item: FilmItem) async throws -> Bool {
        guard !closed else { throw SQLiteStorageError.closed }
        if let loadError { throw loadError }
        guard item.sourceID != "tmdb" || item.kind != nil else { throw FilmLibraryError.missingKind }
        guard let businessID = item.businessID, !businessID.isEmpty,
              let target = item.playbackTarget, target.id.utf8.elementsEqual(businessID.utf8) else { throw FilmLibraryError.missingTarget }
        let identity = item.identity, ticket = UUID()
        let record = try Self.makeRecord(identity, businessID: businessID, target: target)
        changes[identity] = ticket
        let added: Bool
        if let database {
            added = try await database.transaction { db in
                try FilmFavoriteRepository.toggle(record, db)
            }
        } else { added = !favorites.contains(identity) }
        if changes[identity] == ticket, !closed {
            if added { favorites.insert(identity) } else { favorites.remove(identity) }
            changes[identity] = nil
        }
        return added
    }
    public func close() async {
        closed = true
        _ = try? await database?.flush()
    }

    // MARK: - Domain <-> Repository Projection

    private static func projectIdentities(_ records: [FilmFavoriteRecord]) throws -> Set<FilmItemIdentity> {
        var result = Set<FilmItemIdentity>()
        for rec in records {
            var target: RuntimeObject = ["id": .string(rec.favoriteID)]
            if let v = rec.targetKind { target["kind"] = .string(v) }
            if let v = rec.targetTitle { target["title"] = .string(v) }
            if let v = rec.targetYear { target["year"] = .number(Double(v)) }
            if let v = rec.metadataProvider { target["metadataProvider"] = .string(v) }
            if let v = rec.metadataID { target["metadataId"] = .string(v) }
            if let v = rec.season { target["season"] = .number(Double(v)) }
            if let v = rec.episode { target["episode"] = .number(Double(v)) }
            if let v = rec.globalEpisode { target["globalEpisode"] = .number(Double(v)) }

            if let src = rec.source {
                var sourceObj = RuntimeObject()
                if let v = src.key { sourceObj["key"] = .string(v) }
                if let v = src.engine { sourceObj["engine"] = .string(v) }
                if let v = src.installationID { sourceObj["installationId"] = .string(v) }
                if let v = src.siteID { sourceObj["siteId"] = .string(v) }
                if let detail = src.detailID { sourceObj["detailId"] = runtimeIdentifier(detail) }
                if let line = src.lineID { sourceObj["lineId"] = runtimeIdentifier(line) }
                if !sourceObj.isEmpty { target["source"] = .object(sourceObj) }
            }

            if !rec.members.isEmpty || rec.targetKind == "group" {
                target["members"] = .array(rec.members.map { member in
                    .object(["sourceKey": .string(member.sourceKey), "detailId": runtimeIdentifier(member.detailID)])
                })
            }

            let item = FilmItemIdentity(
                sourceID: rec.sourceID,
                itemID: rec.itemID,
                kind: rec.kind,
                businessID: rec.favoriteID,
                playbackTarget: try FilmPlaybackTarget(.object(target))
            )
            guard result.insert(item).inserted else { throw FilmLibraryError.corruptedData }
        }
        return result
    }

    private static func makeRecord(_ item: FilmItemIdentity, businessID: String, target: FilmPlaybackTarget) throws -> FilmFavoriteRecord {
        let sourceObj = target.value["source"]?.filmObject ?? RuntimeObject()
        let src = FilmFavoriteSourceRecord(
            key: try optionalText(sourceObj["key"]),
            engine: try optionalText(sourceObj["engine"]),
            installationID: try optionalText(sourceObj["installationId"]),
            siteID: try optionalTextScalar(sourceObj["siteId"]),
            detailID: try optionalIdentifier(sourceObj["detailId"]),
            lineID: try optionalIdentifier(sourceObj["lineId"])
        )
        let members: [FilmFavoriteMemberRecord] = try (target.value["members"]?.filmArray ?? []).map { member in
            guard let fields = member.filmObject, let key = fields["sourceKey"]?.filmText,
                  let detail = try optionalIdentifier(fields["detailId"]) else {
                throw FilmLibraryError.corruptedData
            }
            return FilmFavoriteMemberRecord(sourceKey: key, detailID: detail)
        }
        return FilmFavoriteRecord(
            favoriteID: businessID,
            sourceID: item.sourceID,
            itemID: item.itemID,
            kind: item.kind,
            targetKind: try optionalText(target.value["kind"]),
            targetTitle: try optionalText(target.value["title"]),
            targetYear: try optionalInt64(target.value["year"]),
            metadataProvider: try optionalText(target.value["metadataProvider"]),
            metadataID: try optionalTextScalar(target.value["metadataId"]),
            season: try optionalInt64(target.value["season"]),
            episode: try optionalInt64(target.value["episode"]),
            globalEpisode: try optionalInt64(target.value["globalEpisode"]),
            source: src.isEmpty ? nil : src,
            members: members
        )
    }

    private static func optionalText(_ value: RuntimeValue?) throws -> String? {
        switch value {
        case nil, .null: return nil
        case .string(let s): return s
        default: throw FilmLibraryError.corruptedData
        }
    }

    private static func optionalTextScalar(_ value: RuntimeValue?) throws -> String? {
        switch value {
        case nil, .null: return nil
        case .string(let s): return s
        case .number(let n) where n.isFinite && abs(n) <= 9_007_199_254_740_991 && n.rounded(.towardZero) == n:
            return String(Int64(n))
        default: throw FilmLibraryError.corruptedData
        }
    }

    private static func optionalInt64(_ value: RuntimeValue?) throws -> Int64? {
        switch value {
        case nil, .null: return nil
        case .number(let n) where n.isFinite && abs(n) <= 9_007_199_254_740_991 && n.rounded(.towardZero) == n:
            return Int64(n)
        default: throw FilmLibraryError.corruptedData
        }
    }

    private static func optionalIdentifier(_ value: RuntimeValue?) throws -> FilmFavoriteIdentifier? {
        guard let value, value != .null else { return nil }
        switch try FilmPlaybackIdentifier(value) {
        case .string(let text): return .string(text)
        case .number(let num): return .number(num)
        }
    }

    private static func runtimeIdentifier(_ value: FilmFavoriteIdentifier) -> RuntimeValue {
        switch value {
        case .string(let text): return .string(text)
        case .number(let num): return .number(Double(num))
        }
    }
}
