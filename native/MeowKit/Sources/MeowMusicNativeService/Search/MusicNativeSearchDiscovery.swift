// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus b900c259 static/netease-client.js discovery(), with fixed discovery routes.
import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicSearchDiscoveryProviding, MusicSearchHotPlaybackProviding {
    public func searchDiscovery(components: Set<MusicSearchDiscoveryFailure.Component>) async throws -> MusicSearchDiscoverySnapshot {
        try check()
        try requirePlatform(MusicPublicBrowsePlatform.netease.rawValue)
        try musicRequire(!components.isEmpty)
        let own = epoch, port = publicPlatforms
        let result = try await work(label: "musicSearchDiscovery") {
            try await port.searchDiscovery(components: components)
        }
        try check(own)
        return result
    }

    public func searchHotPlayback(keywords: [String]) async throws -> [MusicRemoteTrack] {
        try check()
        try requirePlatform(MusicPublicBrowsePlatform.netease.rawValue)
        let params = try MusicNativeSearchHotPlaybackParser.parameters(keywords)
        let own = epoch, port = publicPlatforms
        let result = try await work(label: "musicSearchHotPlayback") {
            try await port.searchHotPlayback(params: params, count: keywords.count)
        }
        try check(own)
        try musicRequire(result.count == keywords.count &&
            result.allSatisfy { $0.platform == MusicPublicBrowsePlatform.netease.rawValue },
            .upstreamInvalid)
        return try await metadataPage(.init(items: result, total: result.count), maximumItems: 20).items
    }
}

struct MusicNativeSearchDiscoveryValue<Value: Sendable>: Sendable {
    let value: Value
    let validUntil: Date
}

struct MusicNativeSearchHotValue: Sendable {
    let keywords: [MusicSearchHotKeyword]
    let canPlay: Bool
}

struct MusicNativeSearchHotDirectory: Sendable {
    let canPlay: Bool
    let items: [MJ]
}

/// Decode only bounded UI data; no fallback to home recommendation sections.
enum MusicNativeSearchDiscoveryParser {
    static let hotChartID = "HOT_SEARCH_SONG#@#"

    static func keyword(_ value: MJ) throws -> String {
        let text = try value.text(800).trimmingCharacters(in: .whitespacesAndNewlines)
        try musicRequire(!text.isEmpty && text.unicodeScalars.count <= 200, .upstreamInvalid)
        return text
    }

    static func hotDirectory(_ root: MJ) throws -> MusicNativeSearchHotDirectory? {
        let data = root["data"], tab = data["tabInfo"], list = tab["list"], items = data["items"]
        try musicRequire(data.isObject && tab.isObject && list.isArray && list.array.count <= 100 &&
            (items == .null || items.isObject && items.fields.count <= 100), .upstreamInvalid)
        var selected: MJ?
        for row in list.array {
            try musicRequire(row.isObject, .upstreamInvalid)
            let id = try row["id"].text(128)
            if id == hotChartID {
                try musicRequire(selected == nil, .upstreamInvalid)
                selected = row
            }
        }
        guard let selected else { return nil }
        _ = try selected["name"].text(256)
        let rawPlay = selected["needPlay"]
        let canPlay: Bool
        if rawPlay.isBool { canPlay = rawPlay.bool }
        else if rawPlay.isInteger, (0...1).contains(rawPlay.int) { canPlay = rawPlay.int == 1 }
        else { throw MusicServiceError.upstreamInvalid }
        let embedded = items[hotChartID]
        try musicRequire(embedded == .null || embedded.isArray && embedded.array.count <= 1000,
                         .upstreamInvalid)
        return .init(canPlay: canPlay, items: embedded.array)
    }

    static func hotDetail(_ root: MJ) throws -> [MJ] {
        let data = root["data"], rows = data["itemList"]
        try musicRequire(data.isObject && rows.isArray && rows.array.count <= 1000, .upstreamInvalid)
        return rows.array
    }

    static func hotItems(_ rows: [MJ]) throws -> [MusicSearchHotKeyword] {
        try musicRequire(rows.count <= 1000, .upstreamInvalid)
        var seen = Set<Data>()
        return try rows.prefix(20).enumerated().compactMap { index, row in
            try musicRequire(row.isObject, .upstreamInvalid)
            let text = try keyword(row["searchWord"])
            guard seen.insert(Data(text.utf8)).inserted else { return nil }
            let rawScore = row["score"]
            let score = rawScore.isNumber && rawScore.number.isFinite && rawScore.number >= 0 ? rawScore.number : nil
            return .init(keyword: text, rank: index + 1, score: score)
        }
    }
}

enum MusicNativeSearchHotPlaybackParser {
    static func parameters(_ keywords: [String]) throws -> MJ {
        try musicRequire((1...20).contains(keywords.count), .invalidRequest)
        var seen = Set<Data>()
        for value in keywords {
            let normalized = try MusicNativeSearchDiscoveryParser.keyword(.string(value))
            try musicRequire(normalized == value && seen.insert(Data(value.utf8)).inserted,
                             .invalidRequest)
        }
        return .obj([("keywords", .string(MJ.strings(keywords).json))])
    }

    static func songs(_ root: MJ, count: Int) throws -> [MJ] {
        let data = root["data"], rows = data["songs"]
        try musicRequire((1...20).contains(count) && data.isObject && rows.isArray &&
            rows.array.count == count, .upstreamInvalid)
        return try rows.array.map { row in
            try musicRequire(row.isObject && row["song"].isObject, .upstreamInvalid)
            if row.contains("alg"), row["alg"] != .null {
                _ = try row["alg"].text(4096, empty: true)
            }
            return row["song"]
        }
    }
}

private func musicSearchDiscoveryAttempt<T: Sendable>(
    ifRequested requested: Bool,
    _ operation: @escaping @Sendable () async throws -> T
) async -> Result<T, MusicServiceError>? {
    guard requested else { return nil }
    do { try Task.checkCancellation(); return .success(try await operation()) }
    catch { return .failure(MusicServiceError.sanitized(error)) }
}

extension MusicNativePlatforms {
    fileprivate func searchDiscovery(components: Set<MusicSearchDiscoveryFailure.Component>) async throws -> MusicSearchDiscoverySnapshot {
        let own = epoch
        // Read only the requested targets. Cache hits and independent waiters
        // share pure data; no member or playback handle is part of discovery.
        async let hotRequest: Result<MusicNativeSearchDiscoveryValue<MusicNativeSearchHotValue>, MusicServiceError>? =
            musicSearchDiscoveryAttempt(ifRequested: components.contains(.hot)) { [self] in
                try await searchDiscoveryHot()
            }
        async let topsRequest: Result<MusicNativeSearchDiscoveryValue<[MusicPublicBrowseCollection]>, MusicServiceError>? =
            musicSearchDiscoveryAttempt(ifRequested: components.contains(.charts)) { [self] in
                try await searchDiscoveryToplists()
            }
        let (hotResult, topsResult) = await (hotRequest, topsRequest)
        try Task.checkCancellation()
        try musicRequire(own == epoch, .staleAccess)
        var failures: [MusicSearchDiscoveryFailure] = []
        var hot: [MusicSearchHotKeyword] = [], hotCanPlay = false
        var tops: [MusicPublicBrowseCollection] = []
        var expiries: [Date] = []
        switch hotResult {
        case .some(.success(let value)):
            hot = value.value.keywords; hotCanPlay = value.value.canPlay
            expiries.append(value.validUntil)
        case .some(.failure(let error)): failures.append(.init(component: .hot, error: error))
        case nil: break
        }
        switch topsResult {
        case .some(.success(let value)): tops = value.value; expiries.append(value.validUntil)
        case .some(.failure(let error)): failures.append(.init(component: .charts, error: error))
        case nil: break
        }
        return .init(hot: hot, hotCanPlay: hotCanPlay,
                     charts: tops.map { .init(collection: $0) }, failures: failures,
                     validUntil: expiries.min() ?? now())
    }

    private func searchDiscoveryHot() async throws -> MusicNativeSearchDiscoveryValue<MusicNativeSearchHotValue> {
        let trace = MusicRequestTrace(.search, platform: .netease)
        return try await discoveryHot.value(for: "wy", lifetime: 600, observe: { access in
            switch access {
            case .hit: trace.record(.cacheHit)
            case .miss: trace.record(.cacheMiss)
            case .coalesced: trace.record(.coalesced)
            }
        }) { [self] in
            try await MusicRequestTrace.$current.withValue(trace) {
                let root = try await wy("/api/search/pc/chart/list", .obj())
                guard let directory = try MusicNativeSearchDiscoveryParser.hotDirectory(root) else {
                    return .init(value: .init(keywords: [], canPlay: false),
                                 validUntil: now().addingTimeInterval(600))
                }
                let rows: [MJ]
                if directory.items.isEmpty {
                    let detail = try await wy("/api/search/pc/chart/detail",
                                              .obj([("id", .string(MusicNativeSearchDiscoveryParser.hotChartID))]))
                    rows = try MusicNativeSearchDiscoveryParser.hotDetail(detail)
                } else { rows = directory.items }
                let keywords = try MusicNativeSearchDiscoveryParser.hotItems(rows)
                return .init(value: .init(keywords: keywords,
                                          canPlay: directory.canPlay && !keywords.isEmpty),
                             validUntil: now().addingTimeInterval(600))
            }
        }
    }

    fileprivate func searchHotPlayback(params: MJ, count: Int) async throws -> [MusicNativeTrackData] {
        let root = try await wy("/api/hot/search/relate/song/get", params)
        let rows = try MusicNativeSearchHotPlaybackParser.songs(root, count: count)
        return try rows.map { try Self.neteaseSong($0) }
    }

    private func searchDiscoveryToplists() async throws -> MusicNativeSearchDiscoveryValue<[MusicPublicBrowseCollection]> {
        let trace = MusicRequestTrace(.browse, platform: .netease)
        return try await discoveryCharts.value(for: "wy", lifetime: 600, observe: { access in
            switch access {
            case .hit: trace.record(.cacheHit)
            case .miss: trace.record(.cacheMiss)
            case .coalesced: trace.record(.coalesced)
            }
        }) { [self] in
            try await MusicRequestTrace.$current.withValue(trace) {
                let root = try await wy("/api/toplist", .obj())
                try musicRequire(root["list"].isArray && root["list"].array.count <= 1000, .upstreamInvalid)
                var collections: [MusicPublicBrowseCollection] = []
                for row in root["list"].array.prefix(3) {
                    collections.append(try await browseCollection(row, platform: .netease))
                }
                try musicRequire(Set(collections.map(\.id)).count == collections.count, .upstreamInvalid)
                return .init(value: collections, validUntil: now().addingTimeInterval(600))
            }
        }
    }
}
