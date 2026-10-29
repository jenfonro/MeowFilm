// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus b900c259 static/netease-client.js discovery(), with the fixed routes
// in api-enhanced/module/{search_hot_detail,toplist,search_default,playlist_track_all}.js.
import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicSearchDiscoveryProviding {
    public func searchDiscovery() async throws -> MusicSearchDiscoverySnapshot {
        try check()
        try requirePlatform(MusicPublicBrowsePlatform.netease.rawValue)
        let own = epoch, port = publicPlatforms
        let result = try await work(label: "musicSearchDiscovery") {
            try await port.searchDiscovery()
        }
        try check(own)
        // Reserve the complete batch before issuing any handles: a later chart
        // must not evict this response's earlier chart when timestamps tie.
        let nativeTracks = result.charts.flatMap(\.tracks)
        let registered = try page(.init(items: nativeTracks), maximumItems: 60).items
        var offset = 0
        let charts = result.charts.map { chart -> MusicSearchDiscoveryChart in
            defer { offset += chart.tracks.count }
            return .init(collection: chart.collection,
                         tracks: Array(registered.dropFirst(offset).prefix(chart.tracks.count)))
        }
        return .init(hot: result.hot, suggestions: result.suggestions, charts: charts,
                     failures: result.failures, validUntil: now().addingTimeInterval(600))
    }
}

fileprivate struct MusicNativeSearchDiscoveryChart: Sendable {
    let collection: MusicPublicBrowseCollection
    let tracks: [MusicNativeTrackData]
}
fileprivate struct MusicNativeSearchDiscoverySnapshot: Sendable {
    let hot: [MusicSearchHotKeyword]
    let suggestions: [String]
    let charts: [MusicNativeSearchDiscoveryChart]
    let failures: [MusicSearchDiscoveryFailure]
}

/// Decode only bounded UI data; no fallback to home recommendation sections.
enum MusicNativeSearchDiscoveryParser {
    static func keyword(_ value: MJ) throws -> String {
        let text = try value.text(800).trimmingCharacters(in: .whitespacesAndNewlines)
        try musicRequire(!text.isEmpty && text.unicodeScalars.count <= 200, .upstreamInvalid)
        return text
    }
    static func hot(_ root: MJ) throws -> [MusicSearchHotKeyword] {
        try musicRequire(root["data"].isArray && root["data"].array.count <= 1000, .upstreamInvalid)
        var seen = Set<String>()
        return try root["data"].array.prefix(20).compactMap { row in
            let text = try keyword(row["searchWord"])
            guard seen.insert(text).inserted else { return nil }
            let rawScore = row["score"]
            let score = rawScore.isNumber && rawScore.number.isFinite && rawScore.number >= 0 ? rawScore.number : nil
            return .init(keyword: text, score: score)
        }
    }
    static func defaultKeyword(_ root: MJ) throws -> String? {
        let value = root["data"]["realkeyword"]
        if value == .null || value == .string("") { return nil }
        return try keyword(value)
    }
    static func suggestions(defaultKeyword: String?, hot: [MusicSearchHotKeyword]) -> [String] {
        var seen = Set<String>()
        return ([defaultKeyword].compactMap { $0 } + hot.map(\.keyword)).filter { seen.insert($0).inserted }
    }
}

private func musicSearchDiscoveryAttempt<T: Sendable>(
    _ operation: @escaping @Sendable () async throws -> T
) async -> Result<T, MusicServiceError> {
    do { try Task.checkCancellation(); return .success(try await operation()) }
    catch { return .failure(MusicServiceError.sanitized(error)) }
}

extension MusicNativePlatforms {
    fileprivate func searchDiscovery() async throws -> MusicNativeSearchDiscoverySnapshot {
        let own = epoch
        // Three independent requests, at most two on interfacepc's host. The
        // shared native transport's per-host budget is not raised for discovery.
        async let hotRequest = musicSearchDiscoveryAttempt { [self] in
            let root = try await browseWEAPI("/api/hotsearchlist/get", .obj())
            return try MusicNativeSearchDiscoveryParser.hot(root)
        }
        async let topsRequest = musicSearchDiscoveryAttempt { [self] in
            try await searchDiscoveryToplists()
        }
        async let defaultRequest = musicSearchDiscoveryAttempt { [self] in
            let root = try await wy("/api/search/defaultkeyword/get", .obj())
            return try MusicNativeSearchDiscoveryParser.defaultKeyword(root)
        }
        let (hotResult, topsResult, defaultResult) = await (hotRequest, topsRequest, defaultRequest)
        try Task.checkCancellation()
        try musicRequire(own == epoch, .staleAccess)
        // A default word alone was not a successful discovery in the reference.
        if case .failure(let error) = hotResult, case .failure = topsResult { throw error }
        var failures: [MusicSearchDiscoveryFailure] = []
        let hot: [MusicSearchHotKeyword]
        switch hotResult {
        case .success(let value): hot = value
        case .failure(let error): hot = []; failures.append(.init(component: .hot, error: error))
        }
        let tops: [MusicPublicBrowseCollection]
        switch topsResult {
        case .success(let value): tops = value
        case .failure(let error): tops = []; failures.append(.init(component: .charts, error: error))
        }
        let word: String?
        switch defaultResult {
        case .success(let value): word = value
        case .failure(let error): word = nil; failures.append(.init(component: .suggestions, error: error))
        }
        var charts: [MusicNativeSearchDiscoveryChart] = []
        // The three charts are bounded and retain toplist order. Serial hydration
        // avoids a third concurrent playlist request exhausting the host budget.
        for collection in tops {
            try Task.checkCancellation()
            try musicRequire(own == epoch, .staleAccess)
            let attempt = await musicSearchDiscoveryAttempt { [self] in
                let head = try await browseWYHead(collection.nativeID)
                let ids = try await browseWYIDs(head)
                let tracks = try await browseWYSongs(Array(ids.prefix(20)))
                return MusicNativeSearchDiscoveryChart(collection: collection, tracks: tracks)
            }
            try Task.checkCancellation()
            try musicRequire(own == epoch, .staleAccess)
            switch attempt {
            case .success(let chart): charts.append(chart)
            case .failure(let error): failures.append(.init(component: .charts, chartID: collection.id, error: error))
            }
        }
        return .init(hot: hot, suggestions: MusicNativeSearchDiscoveryParser.suggestions(defaultKeyword: word, hot: hot),
                     charts: charts, failures: failures)
    }

    private func searchDiscoveryToplists() async throws -> [MusicPublicBrowseCollection] {
        let root = try await wy("/api/toplist", .obj())
        try musicRequire(root["list"].isArray && root["list"].array.count <= 1000, .upstreamInvalid)
        let collections = try root["list"].array.prefix(3).map { try browseCollection($0, platform: .netease) }
        try musicRequire(Set(collections.map(\.id)).count == collections.count, .upstreamInvalid)
        return collections
    }
}
