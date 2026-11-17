import Foundation
import MeowRuntime

struct FilmSourceDocument: Codable {
    var version = 2
    var cats: [StoredFilmCat] = []
    var videos: [FilmVideoSource] = []
    var deleted: [FilmSourceKey] = []

    static func read(_ data: Data, configured: [CatSourceConfiguration]) throws -> Self {
        let raw = try RuntimeJSON.decode(data)
        guard raw.catField("version") == .number(2),
              let cats = raw.catField("cats")?.catArray,
              let videos = raw.catField("videos")?.catArray,
              let deleted = raw.catField("deleted")?.catArray else { throw FilmSourceError(code: "INVALID_DOCUMENT") }
        let ids = Set(configured.map { Data($0.installationId.utf8) })
        let catIDs = cats.compactMap { $0.catField("installationId")?.catText }.map { Data($0.utf8) }
        let counts = Dictionary(grouping: catIDs, by: { $0 }).mapValues(\.count)
        let adoptedCats = cats.compactMap { row -> StoredFilmCat? in
            guard let id = row.catField("installationId")?.catText, ids.contains(Data(id.utf8)),
                  counts[Data(id.utf8)] == 1,
                  let cat = try? JSONDecoder().decode(StoredFilmCat.self, from: RuntimeJSON.encode(row)),
                  cat.inputRevision > 0, cat.inputRevision <= 9_007_199_254_740_991,
                  cat.sites.allSatisfy({ !$0.key.isEmpty }),
                  Set(cat.sites.map { Data($0.key.utf8) }).count == cat.sites.count else { return nil }
            return cat
        }
        let removed = deleted.compactMap { try? JSONDecoder().decode(FilmSourceKey.self, from: RuntimeJSON.encode($0)) }
            .filter { !$0.installationId.isEmpty && !$0.siteKey.isEmpty }
        let selected = videos.filter {
            guard let id = $0.catField("key")?.catField("installationId")?.catText else { return true }
            return ids.contains(Data(id.utf8))
        }.filter {
            guard let key = $0.catField("key"),
                  let decoded = try? JSONDecoder().decode(FilmSourceKey.self, from: RuntimeJSON.encode(key)) else { return true }
            return !removed.contains(decoded)
        }
        let keys = selected.compactMap { row -> FilmSourceKey? in
            guard let key = row.catField("key") else { return nil }
            return try? JSONDecoder().decode(FilmSourceKey.self, from: RuntimeJSON.encode(key))
        }
        let videoCounts = Dictionary(grouping: keys, by: { $0 }).mapValues(\.count)
        let adoptedVideos = selected.compactMap { try? JSONDecoder().decode(FilmVideoSource.self, from: RuntimeJSON.encode($0)) }
            .filter { !$0.key.siteKey.isEmpty && videoCounts[$0.key] == 1 }
        guard selected.isEmpty || !adoptedVideos.isEmpty,
              adoptedVideos.filter({ $0.options.aggregateImages }).count <= 1 else {
            throw FilmSourceError(code: "INVALID_DOCUMENT")
        }
        return .init(cats: adoptedCats, videos: adoptedVideos, deleted: removed)
    }

    func encode(replacing previous: Self, original: Data?) throws -> Data {
        let next = try RuntimeJSON.decode(JSONEncoder().encode(self))
        let prior = try RuntimeJSON.decode(JSONEncoder().encode(previous))
        if next == prior, let original { return original }
        guard let original, case .object(var root) = try RuntimeJSON.decode(original),
              case .object(let before) = prior,
              case .object(let after) = next else { return try RuntimeJSON.encode(next) }
        func merge(_ original: [RuntimeValue], _ old: [RuntimeValue], _ new: [RuntimeValue],
                   kind: String) throws -> [RuntimeValue] {
            if old == new { return original }
            func identity(_ row: RuntimeValue) -> RuntimeValue? {
                if kind == "cats" { return row.catField("installationId") }
                if kind == "sites" { return row.catField("key") }
                let key = kind == "videos" ? row.catField("key") : row
                guard let id = key?.catField("installationId")?.catText,
                      let site = key?.catField("siteKey")?.catText else { return nil }
                return .array([.string(id), .string(site)])
            }
            var remaining = new
            let rows = try original.compactMap { row -> RuntimeValue? in
                guard let id = identity(row) else { return row }
                let prior = old.first(where: { identity($0) == id })
                guard let index = remaining.firstIndex(where: { identity($0) == id }) else {
                    return prior == nil ? row : nil
                }
                // Live probe metadata can replace one unreadable cache row;
                // editable video/deletion intent must never be guessed.
                guard original.filter({ identity($0) == id }).count == 1,
                      prior != nil || kind == "cats" || kind == "sites" else {
                    throw FilmSourceError(code: "STORAGE_CONFLICT")
                }
                let replacement = remaining.remove(at: index)
                var merged = row.applyingChanges(from: prior ?? .object([:]), to: replacement)
                if kind == "cats", let sites = row.catField("sites")?.catArray,
                   let nextSites = replacement.catField("sites")?.catArray, case .object(var fields) = merged {
                    fields["sites"] = .array(try merge(sites, prior?.catField("sites")?.catArray ?? [], nextSites, kind: "sites"))
                    merged = .object(fields)
                }
                return merged
            }
            return rows + remaining
        }
        for key in ["cats", "videos", "deleted"] {
            root[key] = .array(try merge(root[key]?.catArray ?? [], before[key]!.catArray!, after[key]!.catArray!, kind: key))
        }
        return try RuntimeJSON.encode(.object(root))
    }
}

/// Read-only installation/probe metadata. No editable intent or script bytes.
struct StoredFilmCat: Codable {
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
