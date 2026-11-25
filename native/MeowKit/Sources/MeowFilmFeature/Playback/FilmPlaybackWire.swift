import Foundation
import MeowDebug
import MeowRuntime

public enum FilmPlaybackKind: String, Sendable { case movie, tv, source, group }
public enum FilmMetadataProvider: String, Sendable {
    case tmdb, douban
    public var title: String { self == .tmdb ? "TMDB" : "豆瓣" }
}

/// Provider identifiers preserve their wire prototype, including numeric zero.
/// Display text must not be round-tripped back into a playback/browse identity.
public enum FilmPlaybackIdentifier: Hashable, Sendable {
    case string(String)
    case number(Int64)
    init(_ value: RuntimeValue) throws {
        switch value {
        case .string(let text): self = .string(text)
        case .number(let number) where number.isFinite &&
            abs(number) <= 9_007_199_254_740_991 && number.rounded(.towardZero) == number:
            self = .number(Int64(number))
        default: throw FilmPlaybackError.invalidResponse
        }
    }
    var value: RuntimeValue {
        switch self {
        case .string(let text): .string(text)
        case .number(let number): .number(Double(number))
        }
    }
    public var display: String {
        switch self {
        case .string(let text): text
        case .number(let number): String(number)
        }
    }
}

/// Carries the selected identity, not a title-only request or a reconstructed search.
public struct FilmPlaybackTarget: Equatable, Codable, Sendable {
    public let value: RuntimeObject
    public var id: String { value["id"]!.filmText! }
    public var title: String { value["title"]!.filmText! }
    public var kind: FilmPlaybackKind { FilmPlaybackKind(rawValue: value["kind"]!.filmText!)! }
    public var metadataProvider: FilmMetadataProvider? {
        value["metadataProvider"]?.filmText.flatMap(FilmMetadataProvider.init(rawValue:))
    }
    public var position: FilmEpisodePosition {
        .init(season: value["season"]?.filmInteger, episode: value["episode"]?.filmInteger,
              globalEpisode: value["globalEpisode"]?.filmInteger)
    }

    public init(id: String, kind: FilmPlaybackKind, title: String, year: Int? = nil,
                metadataProvider: FilmMetadataProvider? = nil, metadataID: String? = nil,
                position: FilmEpisodePosition = .init(), source: RuntimeObject? = nil,
                members: [RuntimeValue]? = nil) {
        var value: RuntimeObject = ["id": .string(id), "kind": .string(kind.rawValue), "title": .string(title)]
        value["year"] = year.map { .number(Double($0)) }
        value["metadataProvider"] = metadataProvider.map { .string($0.rawValue) }
        value["metadataId"] = metadataID.map(RuntimeValue.string)
        for (key, field) in position.value { value[key] = field }
        value["source"] = source.map(RuntimeValue.object)
        value["members"] = members.map(RuntimeValue.array)
        self.value = value
    }

    public init(from decoder: any Decoder) throws {
        let bytes = try decoder.singleValueContainer().decode(Data.self)
        try self.init(RuntimeJSON.decode(bytes))
    }

    public func encode(to encoder: any Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(RuntimeJSON.encode(.object(value)))
    }

    public init(_ value: RuntimeValue) throws {
        let object = try value.filmRequiredObject()
        _ = try object.filmString("id"); _ = try object.filmString("title")
        guard FilmPlaybackKind(rawValue: try object.filmString("kind")) != nil else {
            throw FilmPlaybackError.invalidResponse
        }
        self.value = object
    }
}

public struct FilmEpisodePosition: Hashable, Sendable {
    public let season: Int?
    public let episode: Int?
    public let globalEpisode: Int?
    public init(season: Int? = nil, episode: Int? = nil, globalEpisode: Int? = nil) {
        self.season = season; self.episode = episode; self.globalEpisode = globalEpisode
    }
    public var value: RuntimeObject {
        var fields = RuntimeObject()
        fields["season"] = season.map { .number(Double($0)) }
        fields["episode"] = episode.map { .number(Double($0)) }
        fields["globalEpisode"] = globalEpisode.map { .number(Double($0)) }
        return fields
    }
}

public struct FilmPlaybackSource: Equatable, Sendable {
    public let value: RuntimeObject
    public var key: String? { value["key"]?.filmText }
    public var name: String { value["name"]?.filmText ?? "" }
    public var detailID: RuntimeValue? { value["detailId"] }

    public init(key: String? = nil, engine: String, installationID: String, siteID: String,
                revision: Int64? = nil, generation: Int64, name: String,
                enabled: Bool = true, searchable: Bool = true) {
        var value: RuntimeObject = [
            "engine": .string(engine), "installationId": .string(installationID),
            "siteId": .string(siteID), "generation": .number(Double(generation)), "name": .string(name),
            "enabled": .bool(enabled), "searchable": .bool(searchable),
        ]
        value["key"] = key.map(RuntimeValue.string)
        value["revision"] = revision.map { .number(Double($0)) }
        self.value = value
    }

    init(_ value: RuntimeValue) throws {
        let object = try value.filmRequiredObject()
        _ = try object.filmString("key")
        _ = try object.filmString("name")
        let engine = try object.filmString("engine")
        if engine == "cat" || engine == "source" {
            _ = try object.filmString("installationId")
            _ = try object.filmString("siteId")
        } else if engine != "pan" {
            throw FilmPlaybackError.invalidResponse
        }
        if let detailID = object["detailId"] { _ = try FilmPlaybackIdentifier(detailID) }
        // A manual share is a pan resource, not an installed script. Its
        // provider is candidate.provider; a display name is never a pan alias.
        self.value = object
    }
}

public struct FilmPlaybackRequest: Sendable {
    public let target: FilmPlaybackTarget
    public let sources: [FilmPlaybackSource]
    public let query: String?
    public let metadata: RuntimeValue?
    public let searchReference: RuntimeValue?
    public let entrySurface: String?
    public var restoreOnly = false
    public var browsing: RuntimeObject?
    public private(set) var resumeTarget: FilmPlaybackTarget?

    public init(target: FilmPlaybackTarget, sources: [FilmPlaybackSource], query: String? = nil,
                metadata: RuntimeValue? = nil, searchReference: RuntimeValue? = nil,
                entrySurface: String? = nil) {
        self.target = target; self.sources = sources; self.query = query
        self.metadata = metadata; self.searchReference = searchReference
        self.entrySurface = entrySurface
        resumeTarget = nil
    }

    var value: RuntimeValue {
        var fields: RuntimeObject = [
            "platform": .string("ios"), "target": .object(target.value),
            "sources": .array(sources.map { .object($0.value) }),
        ]
        fields["query"] = query.map(RuntimeValue.string)
        fields["metadata"] = metadata
        fields["searchReference"] = searchReference
        fields["resumeTarget"] = resumeTarget.map { .object($0.value) }
        fields["entrySurface"] = entrySurface.map(RuntimeValue.string)
        if restoreOnly {
            fields["restoreOnly"] = .bool(true)
            fields["browsing"] = browsing.map(RuntimeValue.object)
        }
        return .object(fields)
    }

    mutating func restoreBrowsing(_ snapshot: FilmPlaybackSnapshot) {
        restoreOnly = true
        var fields: RuntimeObject = [
            "sourceId": snapshot.browsing.sourceID.map(RuntimeValue.string) ?? .null,
            "metadataSourceId": snapshot.metadataSourceID.map(RuntimeValue.string) ?? .null,
        ]
        fields["lineId"] = snapshot.browsing.lineID?.value
        fields["season"] = snapshot.browsing.season.map { .number(Double($0)) }
        if let selected = snapshot.sources.first(where: { $0.id == snapshot.browsing.sourceID }) {
            fields["source"] = .object(selected.source.value)
        }
        browsing = fields
    }

    func retargeted(_ target: FilmPlaybackTarget, errorCode: String? = nil) -> Self {
        if self.target.metadataProvider != nil, searchReference == nil {
            // Keep the metadata entry and its seed separate from the current
            // coordinate or exact source selected by the user.
            var request = self
            let waitingGroup = self.target.metadataProvider == .douban && errorCode != "SOURCE_NOT_FOUND" &&
                resumeTarget == nil && target.kind == .group && target.value["source"] == nil &&
                target.value["members"] == .array([])
            // Only Node may create the empty waiting group from the seed.
            request.resumeTarget = target == self.target || waitingGroup ? nil : target
            return request
        }
        let sameMetadata = target.kind == self.target.kind && target.metadataProvider == self.target.metadataProvider &&
            target.value["metadataId"] == self.target.value["metadataId"]
        var request = Self(target: target, sources: sources, query: query,
                     metadata: sameMetadata ? metadata : nil,
                     searchReference: searchReference, entrySurface: entrySurface)
        if searchReference != nil { request.resumeTarget = target }
        request.restoreOnly = restoreOnly; request.browsing = browsing
        return request
    }
}

public enum FilmPanProvider: String, CaseIterable, Sendable {
    case yidong, tianyi, quark, uc, baidu
    public var title: String {
        switch self {
        case .yidong: "移动云盘"
        case .tianyi: "天翼云盘"
        case .quark: "夸克网盘"
        case .uc: "UC 网盘"
        case .baidu: "百度网盘"
        }
    }
}

public enum FilmResourceQuality: String, CaseIterable, Sendable {
    case hdr = "4k_hdr", highFrameRate = "4k_fps", uhd = "4k", fullHD = "1080p", hd = "720p"
    public var title: String {
        switch self {
        case .hdr: "4K HDR"
        case .highFrameRate: "4K 60帧"
        case .uhd: "4K"
        case .fullHD: "1080P"
        case .hd: "720P"
        }
    }
}

public enum FilmPreferenceDimension: String, CaseIterable, Sendable {
    case quality, pan, keyword
    public var title: String {
        switch self {
        case .quality: "画质"
        case .pan: "网盘"
        case .keyword: "关键词"
        }
    }
}

public struct FilmPlaybackPreferences: Equatable, Sendable {
    public var ruleOrder: [FilmPreferenceDimension]
    public var panOrder: [FilmPanProvider]
    public var keywords: [String]
    public var siteCleanKeywords: [String]
    public var iosAutoPrepare: Bool
    public var metadataViewProvider: FilmMetadataProvider

    public static let defaults = Self(ruleOrder: [.quality, .pan, .keyword],
        panOrder: [.yidong, .tianyi, .quark, .uc, .baidu], keywords: [],
        siteCleanKeywords: ["直播", "体育", "短剧", "听书", "舞曲", "哔哩"], iosAutoPrepare: true)

    public init(ruleOrder: [FilmPreferenceDimension], panOrder: [FilmPanProvider],
                keywords: [String], siteCleanKeywords: [String], iosAutoPrepare: Bool,
                metadataViewProvider: FilmMetadataProvider = .tmdb) {
        self.ruleOrder = ruleOrder; self.panOrder = panOrder; self.keywords = keywords
        self.siteCleanKeywords = siteCleanKeywords; self.iosAutoPrepare = iosAutoPrepare
        self.metadataViewProvider = metadataViewProvider
    }

    init(record: FilmPreferencesRecord) throws {
        let rules = try record.ruleOrder.map { text -> FilmPreferenceDimension in
            guard let value = FilmPreferenceDimension(rawValue: text) else {
                throw FilmPlaybackError(code: "INVALID_PREFERENCES")
            }
            return value
        }
        let pans = try record.panOrder.map { text -> FilmPanProvider in
            guard let value = FilmPanProvider(rawValue: text) else {
                throw FilmPlaybackError(code: "INVALID_PREFERENCES")
            }
            return value
        }
        guard Set(rules).count == rules.count, Set(pans).count == pans.count,
              let provider = FilmMetadataProvider(rawValue: record.metadataViewProvider) else {
            throw FilmPlaybackError(code: "INVALID_PREFERENCES")
        }
        self.init(
            ruleOrder: rules,
            panOrder: pans,
            keywords: record.keywords,
            siteCleanKeywords: record.siteCleanKeywords,
            iosAutoPrepare: record.iosAutoPrepare,
            metadataViewProvider: provider
        )
    }

    var record: FilmPreferencesRecord {
        FilmPreferencesRecord(
            iosAutoPrepare: iosAutoPrepare,
            metadataViewProvider: metadataViewProvider.rawValue,
            ruleOrder: ruleOrder.map(\.rawValue),
            panOrder: panOrder.map(\.rawValue),
            keywords: keywords,
            siteCleanKeywords: siteCleanKeywords
        )
    }
}

public struct FilmPlaybackCandidate: Equatable, Sendable {
    public let businessID: String
    public let playbackTarget: FilmPlaybackTarget
    public let id: String
    public let source: FilmPlaybackSource
    public let lineID: FilmPlaybackIdentifier
    public let lineLabel: String
    /// Unknown labels stay labels. Only this explicit field classifies a pan family.
    public let provider: String?
    public let name: String
    public let path: String?
    public let position: FilmEpisodePosition
    public let quality: String?
    public let resolution: String?
    public let fps: Double?
    public let codec: String?

    init(_ value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        businessID = try fields.filmString("businessId")
        playbackTarget = try FilmPlaybackTarget(fields.filmValue("playbackTarget"))
        id = try fields.filmString("id")
        source = try FilmPlaybackSource(fields.filmValue("source"))
        lineID = try FilmPlaybackIdentifier(fields.filmValue("lineId"))
        lineLabel = try fields.filmString("lineLabel")
        provider = fields["provider"]?.filmText
        name = try fields.filmString("name"); path = fields["path"]?.filmText
        quality = fields["quality"]?.filmText
        resolution = fields["resolution"]?.filmText
        fps = fields["fps"]?.filmNumber
        codec = fields["codec"]?.filmText
        position = .init(season: fields["season"]?.filmInteger, episode: fields["episode"]?.filmInteger,
                         globalEpisode: fields["globalEpisode"]?.filmInteger)
    }
}

public struct FilmPlaybackSourceRow: Equatable, Sendable {
    public let businessID: String
    public let playbackTarget: FilmPlaybackTarget
    public let id: String
    public let title: String
    public let source: FilmPlaybackSource
    public let state: String
    public let error: String?
    init(_ value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        businessID = try fields.filmString("businessId")
        playbackTarget = try FilmPlaybackTarget(fields.filmValue("playbackTarget"))
        id = try fields.filmString("id"); title = try fields.filmString("title")
        source = try FilmPlaybackSource(fields.filmValue("source"))
        state = try fields.filmString("state"); error = fields["error"]?.filmText
    }
}

public struct FilmPlaybackMetadataSource: Equatable, Sendable {
    public let businessID: String
    public let playbackTarget: FilmPlaybackTarget
    public let id: String
    public let provider: FilmMetadataProvider
    public let metadataID: FilmPlaybackIdentifier
    public let kind: FilmPlaybackKind
    public let title: String
    public let seasons: [RuntimeValue]?

    init(_ value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        businessID = try fields.filmString("businessId")
        playbackTarget = try FilmPlaybackTarget(fields.filmValue("playbackTarget"))
        id = try fields.filmString("id")
        guard let provider = FilmMetadataProvider(rawValue: try fields.filmString("provider")),
              let kind = FilmPlaybackKind(rawValue: try fields.filmString("kind")) else {
            throw FilmPlaybackError.invalidResponse
        }
        self.provider = provider; self.kind = kind
        metadataID = try FilmPlaybackIdentifier(fields.filmValue("metadataId"))
        title = try fields.filmString("title")
        seasons = fields["seasons"]?.filmArray
    }
}

public struct FilmPlaybackLine: Equatable, Sendable {
    public let businessID: String
    public let playbackTarget: FilmPlaybackTarget
    public let id: FilmPlaybackIdentifier
    public let label: String
    public let provider: String?
    init(_ value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        businessID = try fields.filmString("businessId")
        playbackTarget = try FilmPlaybackTarget(fields.filmValue("playbackTarget"))
        id = try FilmPlaybackIdentifier(fields.filmValue("id")); label = try fields.filmString("label")
        provider = fields["provider"]?.filmText
    }
}

public struct FilmPlaybackFile: Equatable, Sendable {
    public let businessID: String
    public let playbackTarget: FilmPlaybackTarget
    public let id: String
    public let candidateID: String?
    public let name: String
    public let path: String?
    public let position: FilmEpisodePosition
    public let quality: String?
    public let resolution: String?
    public let fps: Double?
    public let codec: String?
    init(_ value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        businessID = try fields.filmString("businessId")
        playbackTarget = try FilmPlaybackTarget(fields.filmValue("playbackTarget"))
        id = try fields.filmString("id"); name = try fields.filmString("name")
        candidateID = fields["candidateId"]?.filmText; path = fields["path"]?.filmText
        quality = fields["quality"]?.filmText; resolution = fields["resolution"]?.filmText
        fps = fields["fps"]?.filmNumber; codec = fields["codec"]?.filmText
        position = .init(season: fields["season"]?.filmInteger, episode: fields["episode"]?.filmInteger,
                         globalEpisode: fields["globalEpisode"]?.filmInteger)
    }
}

public struct FilmPlaybackBrowsing: Equatable, Sendable {
    public let sourceID: String?
    public let lineID: FilmPlaybackIdentifier?
    public let season: Int?
    public let loading: Bool
    public let refreshing: Bool
    public let error: String?
    public let lines: [FilmPlaybackLine]
    public let files: [FilmPlaybackFile]
    public let originalFiles: [FilmPlaybackFile]
    init(sourceID: String?, lineID: FilmPlaybackIdentifier?, season: Int?,
         lines: [FilmPlaybackLine], loading: Bool = true, refreshing: Bool = false,
         error: String? = nil, files: [FilmPlaybackFile] = [], originalFiles: [FilmPlaybackFile] = []) {
        self.sourceID = sourceID; self.lineID = lineID; self.season = season
        self.lines = lines; self.loading = loading; self.error = error
        self.refreshing = refreshing; self.files = files; self.originalFiles = originalFiles
    }
    init(_ value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        sourceID = fields["sourceId"]?.filmText
        lineID = try fields.filmOptional("lineId").map(FilmPlaybackIdentifier.init)
        season = fields["season"]?.filmInteger
        loading = try fields.filmBool("loading"); error = fields["error"]?.filmText
        if fields["refreshing"] != nil { refreshing = try fields.filmBool("refreshing") }
        else { refreshing = false }
        lines = try fields.filmArray("lines").map(FilmPlaybackLine.init)
        // A failed current list must never expose a previous list's playable rows.
        if error != nil {
            files = []; originalFiles = []
        } else {
            files = try fields.filmArray("episodes").map(FilmPlaybackFile.init)
            originalFiles = try fields["originalEpisodes"].map { value in
                guard let values = value.filmArray else { throw FilmPlaybackError.invalidResponse }
                return try values.map(FilmPlaybackFile.init)
            } ?? []
        }
    }
}

public struct FilmPlaybackEpisode: Equatable, Sendable {
    public let raw: RuntimeObject
    public let position: FilmEpisodePosition
    public let name: String?
    public let businessID: String
    public let playbackTarget: FilmPlaybackTarget
    public var displayID: String { businessID }
    init(_ value: RuntimeValue) throws {
        raw = try value.filmRequiredObject()
        businessID = try raw.filmString("businessId")
        playbackTarget = try FilmPlaybackTarget(raw.filmValue("playbackTarget"))
        guard let season = raw["season"]?.filmInteger, let episode = raw["episode"]?.filmInteger else {
            throw FilmPlaybackError.invalidResponse
        }
        position = .init(season: season, episode: episode, globalEpisode: raw["globalEpisode"]?.filmInteger)
        name = raw["name"]?.filmText
    }
}

public struct FilmPlaybackAttempt: Equatable, Sendable {
    public let id: String
    public let candidate: FilmPlaybackCandidate
    public let prepareOnly: Bool
    public let position: Double
    public let paused: Bool
    init(_ value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        id = try fields.filmString("id"); candidate = try FilmPlaybackCandidate(fields.filmValue("candidate"))
        prepareOnly = try fields.filmBool("prepareOnly")
        position = try fields.filmNumber("position"); paused = try fields.filmBool("paused")
    }
}

public struct FilmPlaybackCurrent: Equatable, Sendable {
    public let attemptID: String
    public let candidate: FilmPlaybackCandidate
    public let position: Double
    public let duration: Double
    public let paused: Bool
    init(_ value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        attemptID = try fields.filmString("attemptId")
        candidate = try FilmPlaybackCandidate(fields.filmValue("candidate"))
        position = try fields.filmNumber("position"); duration = try fields.filmNumber("duration")
        paused = try fields.filmBool("paused")
    }
}

public struct FilmPlaybackMetadataSupplement: Equatable, Sendable {
    public let provider: FilmMetadataProvider
    public let metadataID: FilmPlaybackIdentifier
    public let kind: FilmPlaybackKind
    public let metadata: RuntimeValue

    init(_ value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        guard try fields.filmString("provider") == "tmdb",
              let kind = FilmPlaybackKind(rawValue: try fields.filmString("kind")),
              kind == .movie || kind == .tv else { throw FilmPlaybackError.invalidResponse }
        provider = .tmdb
        metadataID = try FilmPlaybackIdentifier(fields.filmValue("metadataId"))
        self.kind = kind
        metadata = try fields.filmValue("metadata")
    }
}

public struct FilmPlaybackWorkInfo: Equatable, Sendable {
    public let provider: FilmMetadataProvider
    public let metadataID: FilmPlaybackIdentifier
    public let kind: FilmPlaybackKind
    public let title: String
    public let summary: String
    public let seasonCount: Int?
    public let episodeCount: Int?
    public let metadata: RuntimeObject

    init(_ value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        guard let provider = FilmMetadataProvider(rawValue: try fields.filmString("provider")),
              let kind = FilmPlaybackKind(rawValue: try fields.filmString("kind")),
              kind == .movie || kind == .tv else { throw FilmPlaybackError.invalidResponse }
        self.provider = provider; self.kind = kind
        metadataID = try FilmPlaybackIdentifier(fields.filmValue("metadataId"))
        title = try fields.filmString("title"); summary = try fields.filmString("summary")
        func count(_ key: String) throws -> Int? {
            guard let value = try fields.filmOptional(key) else { return nil }
            guard let count = value.filmInteger else { throw FilmPlaybackError.invalidResponse }
            return count
        }
        seasonCount = try count("seasonCount"); episodeCount = try count("episodeCount")
        metadata = try fields.filmValue("metadata").filmRequiredObject()
    }
}

public struct FilmPlaybackSnapshot: Equatable, Sendable {
    public let sessionID: UUID
    public let sequence: Int64
    public let target: FilmPlaybackTarget
    public let entryTarget: FilmPlaybackTarget?
    public let entrySurface: String?
    public private(set) var entryResolution: String?
    public let stage: String
    public let error: String?
    public let initializationError: String?
    public let retryMediaAttemptID: String?
    public private(set) var workInfo: FilmPlaybackWorkInfo?
    public private(set) var metadata: RuntimeValue
    public private(set) var metadataCache: FilmMetadataCache?
    public private(set) var metadataSupplement: FilmPlaybackMetadataSupplement?
    public private(set) var metadataSources: [FilmPlaybackMetadataSource]
    public private(set) var metadataSourceID: String?
    public let metadataError: String?
    public private(set) var sources: [FilmPlaybackSourceRow]
    public private(set) var browsing: FilmPlaybackBrowsing
    public private(set) var episodes: [FilmPlaybackEpisode]
    public private(set) var candidates: [FilmPlaybackCandidate]
    public let attempt: FilmPlaybackAttempt?
    public let current: FilmPlaybackCurrent?
    public let searchComplete: Bool
    public let shortVideo: Bool
    public let previewEpisodes: Bool
    public let reportError: String?
    public let prefetch: RuntimeValue?

    public init(value: RuntimeValue) throws {
        let fields = try value.filmRequiredObject()
        guard let id = UUID(uuidString: try fields.filmString("sessionId")),
              let sequence = fields["sequence"]?.filmInteger else { throw FilmPlaybackError.invalidResponse }
        sessionID = id; self.sequence = Int64(sequence)
        target = try FilmPlaybackTarget(fields.filmValue("target"))
        entryTarget = try fields["entryTarget"].flatMap { $0 == .null ? nil : $0 }.map(FilmPlaybackTarget.init)
        entrySurface = fields["entrySurface"]?.filmText
        entryResolution = fields["entryResolution"]?.filmText
        stage = try fields.filmString("stage"); error = fields["error"]?.filmText
        initializationError = fields["initializationError"]?.filmText
        retryMediaAttemptID = fields["retryMediaAttemptId"]?.filmText
        workInfo = try fields.filmOptional("workInfo").map(FilmPlaybackWorkInfo.init)
        metadata = try fields.filmValue("metadata")
        if let cache = fields["metadataCache"], cache != .null {
            metadataCache = FilmMetadataCache(value: cache)
        } else {
            metadataCache = nil
        }
        if let supplement = fields["metadataSupplement"], supplement != .null {
            do {
                metadataSupplement = try FilmPlaybackMetadataSupplement(supplement)
            } catch {
                DebugRuntime.logError(.filmPlayback, sub: "wire", event: "decode.metadataSupplement.skipped", error: error)
                metadataSupplement = nil
            }
        } else {
            metadataSupplement = nil
        }
        let metadataSources: [FilmPlaybackMetadataSource] = try fields["metadataSources"].map { value in
            guard let values = value.filmArray else { throw FilmPlaybackError.invalidResponse }
            return values.compactMap { entry in
                do {
                    return try FilmPlaybackMetadataSource(entry)
                } catch {
                    DebugRuntime.logError(.filmPlayback, sub: "wire", event: "decode.metadataSource.skipped", error: error)
                    return nil
                }
            }
        } ?? []
        let metadataSourceID = try fields["metadataSourceId"].flatMap { $0 == .null ? nil : $0 }.map { value in
            guard let id = value.filmText, !id.isEmpty else { throw FilmPlaybackError.invalidResponse }
            return id
        }
        if let metadataSourceID, !metadataSources.contains(where: { $0.id == metadataSourceID }) {
            throw FilmPlaybackError.invalidResponse
        }
        self.metadataSources = metadataSources
        self.metadataSourceID = metadataSourceID
        metadataError = fields["metadataError"]?.filmText
        sources = try fields.filmArray("sources").map(FilmPlaybackSourceRow.init)
        browsing = try FilmPlaybackBrowsing(fields.filmValue("browsing"))
        episodes = try fields.filmArray("episodes").map(FilmPlaybackEpisode.init)
        candidates = try fields.filmArray("candidates").map(FilmPlaybackCandidate.init)
        attempt = try fields.filmOptional("attempt").map(FilmPlaybackAttempt.init)
        current = try fields.filmOptional("current").map(FilmPlaybackCurrent.init)
        searchComplete = try fields.filmBool("searchComplete")
        shortVideo = try fields.filmBool("shortVideo"); previewEpisodes = try fields.filmBool("previewEpisodes")
        reportError = fields["reportError"]?.filmText; prefetch = fields["prefetch"]
    }

    /// Native display state may outlive its executor. Never feed this projection
    /// back to the execution owner as authorization or cache freshness.
    func retainingLoadedPresentation(_ previous: Self?) -> Self {
        guard let previous else { return self }
        var result = self
        if metadata == .null, metadataSourceID == previous.metadataSourceID || sessionID != previous.sessionID {
            result.metadata = previous.metadata
            result.metadataCache = previous.metadataCache
            result.metadataSupplement = previous.metadataSupplement
            result.metadataSources = previous.metadataSources
            result.metadataSourceID = previous.metadataSourceID
        }
        if workInfo == nil, stage == "loading" || stage == "error" ||
            browsing.loading || browsing.error != nil || metadataError != nil {
            result.workInfo = previous.workInfo
        }
        if sources.isEmpty, stage == "loading" || error != nil { result.sources = previous.sources }
        if browsing.loading || browsing.error != nil,
           browsing.sourceID == previous.browsing.sourceID,
           browsing.lineID == previous.browsing.lineID,
           browsing.season == previous.browsing.season,
           metadataSourceID == previous.metadataSourceID {
            result.browsing = previous.browsing
            result.episodes = previous.episodes
            result.candidates = previous.candidates
        }
        return result
    }

    func displaying(_ directory: FilmPlaybackDirectorySlice) -> Self {
        var result = self
        result.browsing = directory.browsing
        result.metadata = directory.metadata
        result.metadataCache = directory.metadataCache
        result.metadataSupplement = directory.metadataSupplement
        result.metadataSourceID = directory.key.metadata
        result.episodes = directory.episodes
        result.candidates = directory.candidates
        return result
    }

    func refreshingDirectory(_ directory: FilmPlaybackDirectorySlice) -> Self {
        var result = displaying(directory)
        let browsing = directory.browsing
        result.browsing = .init(sourceID: browsing.sourceID, lineID: browsing.lineID, season: browsing.season,
            lines: browsing.lines, loading: false, refreshing: true,
            files: browsing.files, originalFiles: browsing.originalFiles)
        return result
    }

    func loadingDirectory(_ key: FilmPlaybackDirectoryKey, error: String? = nil) -> Self {
        var result = self
        result.browsing = .init(sourceID: key.source, lineID: key.line, season: key.season,
            lines: key.source == browsing.sourceID ? browsing.lines : [], loading: error == nil, error: error)
        if key.metadata != metadataSourceID {
            result.metadata = .null; result.metadataCache = nil; result.metadataSupplement = nil
        }
        result.metadataSourceID = key.metadata
        result.episodes = []; result.candidates = []
        return result
    }

    /// True when only high-frequency playback telemetry (`sequence` or `current` position/duration/paused) changed.
    func hasSamePresentation(as other: Self) -> Bool {
        sessionID == other.sessionID &&
        target == other.target &&
        entryTarget == other.entryTarget &&
        entrySurface == other.entrySurface &&
        entryResolution == other.entryResolution &&
        stage == other.stage &&
        error == other.error &&
        initializationError == other.initializationError &&
        retryMediaAttemptID == other.retryMediaAttemptID &&
        workInfo == other.workInfo &&
        metadata == other.metadata &&
        metadataCache == other.metadataCache &&
        metadataSupplement == other.metadataSupplement &&
        metadataSources == other.metadataSources &&
        metadataSourceID == other.metadataSourceID &&
        metadataError == other.metadataError &&
        sources == other.sources &&
        browsing == other.browsing &&
        episodes == other.episodes &&
        candidates == other.candidates &&
        attempt == other.attempt &&
        current?.attemptID == other.current?.attemptID &&
        current?.candidate == other.current?.candidate &&
        searchComplete == other.searchComplete &&
        shortVideo == other.shortVideo &&
        previewEpisodes == other.previewEpisodes &&
        reportError == other.reportError &&
        prefetch == other.prefetch
    }
}

/// Sensitive input: consumed by the native media owner, never ordinary presentation.
public struct FilmPlaybackMediaInput: Sendable, CustomStringConvertible, CustomDebugStringConvertible, CustomReflectable {
    public let sessionID: UUID
    public let attemptID: String
    public let resourceID: String
    public let url: URL
    public let format: String?
    public let drm: RuntimeValue?
    public let title: String
    public let position: Double
    public let paused: Bool

    init(value: RuntimeValue, sessionID: UUID, attemptID: String) throws {
        let fields = try value.filmRequiredObject()
        resourceID = try fields.filmString("id")
        guard !resourceID.isEmpty, try fields.filmString("attemptId") == attemptID,
              let url = URL(string: try fields.filmString("url")),
              ["http", "https"].contains(url.scheme?.lowercased() ?? ""),
              url.host?.isEmpty == false else { throw FilmPlaybackError.invalidResponse }
        self.sessionID = sessionID; self.attemptID = attemptID; self.url = url
        format = fields["format"]?.filmText
        drm = fields["drm"]; title = try fields.filmString("title")
        position = try fields.filmNumber("position"); paused = try fields.filmBool("paused")
    }

    public var description: String { "FilmPlaybackMediaInput(redacted)" }
    public var debugDescription: String { description }
    public var customMirror: Mirror { Mirror(self, children: ["attemptID": attemptID]) }
}

public struct FilmPlaybackError: Error, Equatable, Sendable, LocalizedError {
    public let code: String
    public init(code: String) { self.code = code }
    static let invalidResponse = Self(code: "INVALID_RESPONSE")
    static let closed = Self(code: "CLOSED")
    public var errorDescription: String? {
        switch code {
        case "NO_MATCH", "MEDIA_UNAVAILABLE": "暂无可用片源"
        case "TIMEOUT": "请求超时"
        case "CLOSED", "HOST_CLOSED": "本地运行器已关闭"
        case "OBSERVATION_CLOSED": "播放状态连接已断开"
        case "INVALID_RESPONSE", "UPSTREAM_FORMAT": "返回数据格式错误"
        case "STORAGE_ERROR", "LIBRARY_UPDATE_FAILED": "保存失败"
        case "CORRUPT_STORAGE": "本地播放资料已损坏"
        case "NETWORK_ERROR", "MEDIA_HTTP_NETWORK_FAILED": "网络连接失败"
        case "NOT_CONFIGURED": "尚未完成数据源配置"
        case "AUTH_FAILED": "认证失败，请检查凭据"
        case "WEB_INTERACTION_REQUIRED": "需要完成网页验证，请在数据源设置中检查 Cookie 或代理后重试"
        case "MEDIA_HTTP_TLS_FAILED": "安全连接失败"
        case "UPSTREAM_HTTP", "UPSTREAM_ERROR", "MEDIA_UPSTREAM_ERROR": "上游服务请求失败"
        case "MEDIA_HTTP_DEADLINE", "MEDIA_UPSTREAM_TIMEOUT": "请求超时"
        case "MEDIA_HTTP_UNAUTHORIZED": "媒体认证失败"
        case "MEDIA_HTTP_NOT_FOUND", "MEDIA_NOT_FOUND": "媒体资源不存在"
        case "MEDIA_HTTP_EXPIRED": "媒体地址已过期"
        case "MEDIA_HTTP_THROTTLED": "媒体请求过于频繁"
        case "MEDIA_HTTP_TRUNCATED", "MEDIA_READ_TRUNCATED": "媒体响应不完整"
        case "MEDIA_RESOURCE_REVOKED", "MEDIA_READ_REVOKED": "媒体授权已失效"
        case "MEDIA_INPUT_INVALID", "MEDIA_INVALID_REQUEST": "媒体请求无效"
        case "MEDIA_FORMAT_UNSUPPORTED", "MEDIA_HTTP_FORMAT_UNSUPPORTED", "MEDIA_READ_FORMAT_UNSUPPORTED": "媒体格式不受支持"
        case "MEDIA_DECODING_FAILED": "媒体解码失败"
        case "MEDIA_OUTPUT_FAILED", "MEDIA_PLAYBACK_FAILED": "媒体播放失败"
        case "MEDIA_OPEN_FAILED": "媒体打开失败"
        case "MEDIA_READ_FAILED": "媒体读取失败"
        case "MEDIA_SEEK_FAILED", "MEDIA_READ_SEEK_FAILED": "媒体跳转失败"
        case "MEDIA_DECRYPTION_FAILED": "媒体解密失败"
        case "MEDIA_RELEASE_FAILED": "媒体关闭失败"
        case "SCRIPT_FAILURE", "CAT_FAILURE": "脚本执行失败"
        case "SCRIPT_PROTOCOL_ERROR": "脚本响应格式错误"
        case "SOURCE_FAILURE": "来源请求失败"
        case "PAN_INTERCEPTION_UNSUPPORTED": "网盘请求未能接管，请关闭对应网盘接管后重试"
        case "BUSY": "操作尚未完成"
        default: code
        }
    }
}

extension RuntimeValue {
    var filmText: String? { if case .string(let text) = self { text } else { nil } }
    var filmNumber: Double? { if case .number(let number) = self { number } else { nil } }
    var filmInteger: Int? {
        guard let number = filmNumber, number.isFinite, number >= 0,
              number <= 9_007_199_254_740_991, number.rounded(.towardZero) == number else { return nil }
        return Int(number)
    }
    var filmArray: [RuntimeValue]? { if case .array(let values) = self { values } else { nil } }
    var filmObject: RuntimeObject? { if case .object(let fields) = self { fields } else { nil } }
    var filmScalar: String? { filmText ?? filmNumber.map { String(format: "%.0f", $0) } }
    func filmRequiredObject() throws -> RuntimeObject {
        guard let fields = filmObject else { throw FilmPlaybackError.invalidResponse }
        return fields
    }
}

extension RuntimeObject {
    func filmValue(_ key: String) throws -> RuntimeValue {
        guard let value = self[key] else { throw FilmPlaybackError.invalidResponse }
        return value
    }
    func filmString(_ key: String) throws -> String {
        guard let value = self[key]?.filmText else { throw FilmPlaybackError.invalidResponse }
        return value
    }
    func filmArray(_ key: String) throws -> [RuntimeValue] {
        guard let value = self[key]?.filmArray else { throw FilmPlaybackError.invalidResponse }
        return value
    }
    func filmBool(_ key: String) throws -> Bool {
        guard case .bool(let value)? = self[key] else { throw FilmPlaybackError.invalidResponse }
        return value
    }
    func filmNumber(_ key: String) throws -> Double {
        guard let value = self[key]?.filmNumber, value.isFinite, value >= 0 else {
            throw FilmPlaybackError.invalidResponse
        }
        return value
    }
    func filmOptional(_ key: String) throws -> RuntimeValue? {
        let value = try filmValue(key)
        return value == .null ? nil : value
    }
}
