import Foundation
import MeowRuntime

struct FilmSourceDocument: Codable {
    var version = 1
    var cats: [StoredFilmCat] = []
    var videos: [FilmVideoSource] = []
    var deleted: [FilmSourceKey] = []

    func validate() throws {
        let ids = Set(cats.map(\.installationId))
        guard version == 1, ids.count == cats.count,
              cats.allSatisfy({ cat in !cat.installationId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty &&
                  ["local", "network"].contains(cat.sourceType) &&
                  Set(cat.sites.map { $0.identity(installationId: cat.installationId) }).count == cat.sites.count &&
                  cat.sites.allSatisfy({ !$0.key.isEmpty }) }),
              Set(videos.map(\.key)).count == videos.count,
              videos.allSatisfy({ ids.contains($0.key.installationId) && !$0.key.siteKey.isEmpty }),
              deleted.allSatisfy({ ids.contains($0.installationId) }),
              videos.allSatisfy({ !deleted.contains($0.key) }),
              videos.filter({ $0.options.aggregateImages }).count <= 1 else {
            throw FilmSourceError(code: "INVALID_DOCUMENT")
        }
    }
}

/// Persist only Node management metadata, never port, generation or script bytes.
struct StoredFilmCat: Codable {
    let installationId: String
    let name: String
    let sourceType: String
    let sourceUrl: String?
    let enabled: Bool
    let importedAt: Int64?
    let updatedAt: Int64?
    let updateState: String
    let updateError: String?
    let sourceHash: String?
    let sites: [FilmSourceSite]

    init(_ cat: FilmCatSource) {
        installationId = cat.installationId; name = cat.name; sourceType = cat.sourceType
        sourceUrl = cat.sourceUrl; enabled = cat.enabled; importedAt = cat.importedAt
        updatedAt = cat.updatedAt; updateState = cat.updateState; updateError = cat.updateError
        sourceHash = cat.sourceHash; sites = cat.sites
    }

    var snapshot: FilmCatSource {
        FilmCatSource(installationId: installationId, name: name, sourceType: sourceType,
            sourceUrl: sourceUrl, enabled: enabled, importedAt: importedAt, updatedAt: updatedAt,
            updateState: updateState, updateError: updateError, generation: 0,
            state: enabled ? "stopped" : "disabled", sourceHash: sourceHash, error: nil, sites: sites)
    }
}

func parseFilmCat(_ value: RuntimeValue) throws -> FilmCatSource {
    do {
        let cat = try JSONDecoder().decode(FilmCatSource.self, from: RuntimeJSON.encode(value))
        let timestamps = [cat.importedAt, cat.updatedAt].compactMap { $0 }
        guard !cat.installationId.isEmpty, !cat.name.isEmpty, ["local", "network"].contains(cat.sourceType),
              ["idle", "updated", "unchanged", "failed"].contains(cat.updateState),
              cat.generation >= 0, cat.generation <= 9_007_199_254_740_991,
              timestamps.allSatisfy({ $0 >= 0 && $0 <= 9_007_199_254_740_991 }),
              cat.sites.allSatisfy({ !$0.key.isEmpty }), Set(cat.sites.map { $0.identity(installationId: cat.installationId) }).count == cat.sites.count else {
            throw FilmSourceError(code: "INVALID_RESPONSE")
        }
        return cat
    } catch { throw FilmSourceError(code: "INVALID_RESPONSE") }
}
