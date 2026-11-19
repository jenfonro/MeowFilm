import Foundation
import MeowRuntime

struct FilmSourceDocument: Equatable, Sendable {
    var cats: [StoredFilmCat] = []
    var videos: [FilmVideoSource] = []
    var deleted: [FilmSourceKey] = []
}

/// Read-only installation/probe metadata. No editable intent or script bytes.
struct StoredFilmCat: Codable, Equatable, Sendable {
    let installationId: String
    let inputRevision: Int64
    let importedAt: Int64?
    let updatedAt: Int64?
    let updateState: String
    let updateError: String?
    let sourceHash: String?
    let sites: [FilmSourceSite]

    init(_ cat: FilmCatSource) {
        installationId = cat.installationId; inputRevision = cat.inputRevision; importedAt = cat.importedAt
        updatedAt = cat.updatedAt; updateState = cat.updateState; updateError = cat.updateError
        sourceHash = cat.sourceHash; sites = cat.sites
    }

    private enum CodingKeys: String, CodingKey {
        case installationId, inputRevision, importedAt, updatedAt, updateState, updateError, sourceHash, sites
    }

    init(from decoder: Decoder) throws {
        let fields = try decoder.container(keyedBy: CodingKeys.self)
        installationId = try fields.decode(String.self, forKey: .installationId)
        inputRevision = try fields.decode(Int64.self, forKey: .inputRevision)
        importedAt = try? fields.decodeIfPresent(Int64.self, forKey: .importedAt)
        updatedAt = try? fields.decodeIfPresent(Int64.self, forKey: .updatedAt)
        updateState = try fields.decode(String.self, forKey: .updateState)
        updateError = try fields.decodeIfPresent(String.self, forKey: .updateError)
        sourceHash = try fields.decodeIfPresent(String.self, forKey: .sourceHash)
        sites = try fields.decode([FilmSourceSite].self, forKey: .sites)
    }

    func snapshot(_ input: CatSourceConfiguration) -> FilmCatSource {
        let current = inputRevision == input.inputRevision
        return FilmCatSource(installationId: installationId, name: input.name, sourceType: input.sourceType,
            sourceUrl: input.sourceUrl, enabled: input.enabled, importedAt: importedAt, updatedAt: updatedAt,
            updateState: current ? updateState : "idle", updateError: current ? updateError : nil, generation: 0,
            state: input.enabled ? "stopped" : "disabled", sourceHash: sourceHash, error: nil, sites: sites,
            inputRevision: input.inputRevision)
    }
}

func parseFilmCat(_ value: RuntimeValue) throws -> FilmCatSource {
    do {
        var fields = try value.filmRequiredObject()
        for name in ["importedAt", "updatedAt"] where fields[name] != nil && fields[name] != .null && fields[name]?.catInteger == nil {
            fields[name] = nil
        }
        let cat = try JSONDecoder().decode(FilmCatSource.self, from: RuntimeJSON.encode(.object(fields)))
        guard !cat.installationId.isEmpty, !cat.name.isEmpty, ["local", "network"].contains(cat.sourceType),
              cat.generation >= 0, cat.generation <= 9_007_199_254_740_991,
              cat.inputRevision > 0, cat.inputRevision <= 9_007_199_254_740_991,
              cat.sites.allSatisfy({ !$0.key.isEmpty }), Set(cat.sites.map { $0.identity(installationId: cat.installationId) }).count == cat.sites.count else {
            throw FilmSourceError(code: "INVALID_RESPONSE")
        }
        return cat
    } catch { throw FilmSourceError(code: "INVALID_RESPONSE") }
}
